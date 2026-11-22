using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace QuantConnect.Tests.Services.Parity;

/// <summary>What the platform produced for one case.</summary>
public sealed record PlatformRun
{
    public Guid? JobId { get; init; }
    public string Status { get; init; } = "not-run";
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, string> Statistics { get; init; } = new();
    /// <summary>Worker wall time: the job's <c>startedAt</c> to <c>finishedAt</c> (engine start to final result).</summary>
    public double? WallSeconds { get; init; }
    /// <summary>Submit to terminal status as seen by the harness (includes queueing behind other cases).</summary>
    public double LatencySeconds { get; init; }
    public IReadOnlyList<string>? PythonModules { get; init; }
}

/// <summary>
/// Submits every runnable case through the gateway, keeping <see cref="ParitySettings.Concurrency"/> jobs in flight,
/// and exposes one task per case so the NUnit cases can await their own job.
/// </summary>
public sealed class ParityRunner
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "completed", "failed", "cancelled", "stale" };
    private readonly GatewayClient _gateway;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PlatformRun>> _runs = new();
    private readonly Stopwatch _suite = new();

    public ParityRunner(GatewayClient gateway) => _gateway = gateway;

    public TimeSpan SuiteElapsed => _suite.Elapsed;

    public DateTimeOffset StartedAt { get; private set; }

    public Task<PlatformRun> Get(ParityCase parityCase) => Completion(parityCase.Name).Task;

    public Task RunAsync(IReadOnlyList<ParityCase> cases, CancellationToken ct)
    {
        StartedAt = DateTimeOffset.UtcNow;
        _suite.Start();
        var window = new SemaphoreSlim(ParitySettings.Concurrency);
        // Slowest first keeps the tail short; there is no runtime history, so C# before Python is the only hint.
        var tasks = cases.Select(async parityCase =>
        {
            await window.WaitAsync(ct);
            try
            {
                Completion(parityCase.Name).TrySetResult(await RunOneAsync(parityCase, ct));
            }
            catch (Exception e)
            {
                Completion(parityCase.Name).TrySetResult(new PlatformRun { Status = "harness-error", Error = e.Message });
            }
            finally
            {
                window.Release();
            }
        }).ToList();
        return Task.WhenAll(tasks).ContinueWith(_ => _suite.Stop(), TaskScheduler.Default);
    }

    private TaskCompletionSource<PlatformRun> Completion(string name) =>
        _runs.GetOrAdd(name, _ => new TaskCompletionSource<PlatformRun>(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task<PlatformRun> RunOneAsync(ParityCase parityCase, CancellationToken ct)
    {
        IReadOnlyList<string>? modules = null;
        JsonNode algorithm;
        if (parityCase.Language == Language.CSharp)
        {
            // Built-in reference: the worker runs the class from its own QuantConnect.Algorithm.CSharp.dll.
            algorithm = new JsonObject { ["source"] = "builtin", ["language"] = "CSharp", ["typeName"] = parityCase.Algorithm };
        }
        else
        {
            var zip = PythonPackage.Build(parityCase.Algorithm, out modules);
            algorithm = await _gateway.UploadAsync(zip, parityCase.Algorithm + ".zip", Language.Python, ct);
        }

        var submitted = Stopwatch.StartNew();
        var id = await _gateway.SubmitBacktestAsync($"parity {parityCase.Name}", algorithm, ParitySettings.MaxRuntimeSeconds, ct);
        var deadline = TimeSpan.FromSeconds(ParitySettings.MaxRuntimeSeconds + 600);
        JsonNode job;
        while (true)
        {
            job = (await _gateway.GetJobAsync(id, ct))!;
            if (Terminal.Contains(job["status"]!.GetValue<string>())) break;
            if (submitted.Elapsed > deadline)
            {
                return new PlatformRun { JobId = id, Status = "harness-timeout", Error = $"still {job["status"]} after {deadline}", PythonModules = modules };
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        var latency = submitted.Elapsed.TotalSeconds;
        var status = job["status"]!.GetValue<string>();

        // The results service ingests the final packet asynchronously; wait for its statistics.
        var statistics = new Dictionary<string, string>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var result = await _gateway.GetResultAsync(id, ct);
            if (result?["statistics"] is JsonObject stats && stats.Count > 0)
            {
                statistics = stats.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? "");
                if (statistics.ContainsKey("OrderListHash") || status != "completed") break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return new PlatformRun
        {
            JobId = id,
            Status = status,
            ErrorCode = job["error"]?["code"]?.GetValue<string>(),
            Error = job["error"]?["message"]?.GetValue<string>(),
            Statistics = statistics,
            WallSeconds = Seconds(job["startedAt"], job["finishedAt"]),
            LatencySeconds = latency,
            PythonModules = modules,
        };
    }

    private static double? Seconds(JsonNode? start, JsonNode? end) =>
        start != null && end != null ? (DateTimeOffset.Parse(end.GetValue<string>()) - DateTimeOffset.Parse(start.GetValue<string>())).TotalSeconds : null;
}
