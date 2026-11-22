/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.BacktestWorker;

/// <summary>Environment handed to every child engine (bus and MinIO settings as <c>LEAN_BUS_*</c>, so no secret is written to disk).</summary>
public sealed record EngineEnvironment(IReadOnlyDictionary<string, string> Variables);

/// <summary>Snapshot served on <c>GET /internal/v1/worker</c>.</summary>
public sealed record WorkerStatus
{
    public required string WorkerId { get; init; }
    public Guid? CurrentJobId { get; init; }
    public int? CurrentAttempt { get; init; }
    public int? EnginePid { get; init; }
    public long JobsCompleted { get; init; }
    public long JobsFailed { get; init; }
    public long DeliveriesDropped { get; init; }
}

/// <summary>
/// Handles one <c>jobs.backtest</c> delivery end to end: claims the attempt from job-orchestrator, runs the stock Launcher
/// with the bus handlers in a child process under the job's time and RAM limits, reports <c>JobStarted</c>, worker
/// heartbeats and the terminal <c>JobCompleted</c>/<c>JobFailed</c>, and only then lets the delivery be acked.
/// The engine itself publishes its packets, heartbeats and the final result (<c>BusMessagingHandler</c>); the worker
/// confirms that upload instead of repeating it.
/// </summary>
public sealed class BacktestJobRunner
{
    private const string Json = "application/json";

    private readonly IMessagePublisher _publisher;
    private readonly IAttemptClient _attempts;
    private readonly IResultStore _store;
    private readonly IEngineLauncher _launcher;
    private readonly EngineEnvironment _environment;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BacktestJobRunner> _logger;
    private readonly object _gate = new();
    private (Guid JobId, int Attempt, int Pid)? _current;
    private long _completed, _failed, _dropped;

    public BacktestJobRunner(IMessagePublisher publisher, IAttemptClient attempts, IResultStore store, IEngineLauncher launcher,
        EngineEnvironment environment, IOptions<WorkerOptions> options, TimeProvider time, ILogger<BacktestJobRunner> logger)
    {
        _publisher = publisher;
        _attempts = attempts;
        _store = store;
        _launcher = launcher;
        _environment = environment;
        _options = options.Value;
        _time = time;
        _logger = logger;
        WorkerId = _options.ResolvedWorkerId;
    }

    public string WorkerId { get; }

    public WorkerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new WorkerStatus
                {
                    WorkerId = WorkerId,
                    CurrentJobId = _current?.JobId,
                    CurrentAttempt = _current?.Attempt,
                    EnginePid = _current?.Pid,
                    JobsCompleted = Interlocked.Read(ref _completed),
                    JobsFailed = Interlocked.Read(ref _failed),
                    DeliveriesDropped = Interlocked.Read(ref _dropped),
                };
            }
        }
    }

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        if (message.Envelope.Type != MessageTypes.BacktestRequested)
        {
            _logger.LogError("Dead-lettering {Type} {MessageId}: backtest-worker only runs {Expected}", message.Envelope.Type, message.Envelope.MessageId, MessageTypes.BacktestRequested);
            return MessageDisposition.DeadLetter;
        }
        JobSubmitted? job;
        try
        {
            job = ContractJson.Deserialize<JobSubmitted>(message.Body.Span);
        }
        catch (JsonException e)
        {
            _logger.LogError(e, "Dead-lettering {MessageId}: invalid JobSubmitted body", message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }
        if (job is not { Kind: JobKind.Backtest, Backtest: not null })
        {
            _logger.LogError("Dead-lettering {MessageId}: not a backtest job", message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }

        var grant = await _attempts.StartAttemptAsync(job.JobId, WorkerId, message.DeliveryCount, job.Attempt, cancellationToken).ConfigureAwait(false);
        switch (grant.Outcome)
        {
            case AttemptOutcome.Gone:
                Interlocked.Increment(ref _dropped);
                _logger.LogInformation("Dropping job {JobId} attempt {Attempt} (delivery count {DeliveryCount}): {Detail}", job.JobId, job.Attempt, message.DeliveryCount, grant.Detail);
                return MessageDisposition.Ack;
            case AttemptOutcome.Rejected:
                _logger.LogError("Dead-lettering job {JobId}: job-orchestrator rejected the attempt: {Detail}", job.JobId, grant.Detail);
                return MessageDisposition.DeadLetter;
        }
        var attempt = grant.Attempt!;
        job = job with { Attempt = attempt.Attempt, Results = attempt.Results, Deadline = attempt.Deadline ?? job.Deadline };

        OutgoingMessage terminal;
        var started = _time.GetTimestamp();
        using (var activity = WorkerTelemetry.StartRun(job, WorkerId))
        {
            try
            {
                terminal = await RunAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                activity?.AddException(e);
                WorkerTelemetry.Abandon();
                throw;
            }
            WorkerTelemetry.EndRun(activity, terminal.Envelope.Type, _time.GetElapsedTime(started));
            await _publisher.PublishAsync(terminal, cancellationToken).ConfigureAwait(false);
        }
        if (terminal.Envelope.Type == MessageTypes.JobCompleted)
        {
            Interlocked.Increment(ref _completed);
            _logger.LogInformation("Job {JobId} attempt {Attempt} completed on {WorkerId}", job.JobId, job.Attempt, WorkerId);
        }
        else
        {
            Interlocked.Increment(ref _failed);
            _logger.LogWarning("Job {JobId} attempt {Attempt} failed on {WorkerId}", job.JobId, job.Attempt, WorkerId);
        }
        return MessageDisposition.Ack;
    }

    /// <summary>Runs one granted attempt and returns its terminal status message (not yet published).</summary>
    public async Task<OutgoingMessage> RunAsync(JobSubmitted job, CancellationToken cancellationToken)
    {
        var spec = job.Backtest!;
        var ramMb = spec.MaxRamMb ?? _options.DefaultRamMb;
        job = job with { Backtest = spec with { MaxRamMb = ramMb }, Results = job.Results ?? ResultsPrefix.For(job.JobId, job.Attempt) };
        var startedAt = _time.GetUtcNow();
        var runtimeSeconds = spec.MaxRuntimeSeconds ?? _options.DefaultMaxRuntimeSeconds;
        var deadline = startedAt.AddSeconds(runtimeSeconds);
        if (job.Deadline is { } dispatchDeadline && dispatchDeadline > startedAt && dispatchDeadline < deadline)
        {
            deadline = dispatchDeadline;
        }
        var limitBytes = (long)(ramMb + _options.MemoryHeadroomMb) * 1024 * 1024;
        var heartbeat = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));
        var poll = TimeSpan.FromMilliseconds(Math.Max(10, _options.PollIntervalMilliseconds));

        var workspace = JobWorkspace.Create(_options, job, WorkerId);
        try
        {
            await _publisher.PublishAsync(Started(job, startedAt), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Job {JobId} attempt {Attempt} starting on {WorkerId}: {Algorithm}, {RamMb} MB RAM, deadline {Deadline:O}",
                job.JobId, job.Attempt, WorkerId, Describe(spec.Algorithm), ramMb, deadline);

            ErrorInfo? limit = null;
            int exitCode;
            await using (var engine = _launcher.Start(StartInfo(workspace, ramMb)))
            {
                SetCurrent((job.JobId, job.Attempt, engine.Id));
                var nextBeat = startedAt + heartbeat;
                while (!engine.Exited.IsCompleted)
                {
                    try
                    {
                        await engine.Exited.WaitAsync(poll, _time, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Worker stopping: killing job {JobId} attempt {Attempt}; the delivery is requeued", job.JobId, job.Attempt);
                        engine.Kill();
                        throw;
                    }

                    var now = _time.GetUtcNow();
                    if (now >= deadline)
                    {
                        limit = new ErrorInfo { Code = ErrorCode.Timeout, Message = $"Backtest exceeded its run time limit of {(deadline - startedAt).TotalSeconds:0} s and was stopped" };
                        engine.Kill();
                        break;
                    }
                    var rss = engine.RssBytes;
                    if (rss > limitBytes)
                    {
                        limit = new ErrorInfo
                        {
                            Code = ErrorCode.RuntimeError,
                            Message = $"Backtest exceeded its {ramMb} MB RAM limit (engine RSS {rss / (1024 * 1024)} MB, headroom {_options.MemoryHeadroomMb} MB) and was stopped",
                        };
                        engine.Kill();
                        break;
                    }
                    if (now >= nextBeat)
                    {
                        await TryPublishAsync(Heartbeat(job, now, rss), cancellationToken).ConfigureAwait(false);
                        nextBeat = now + heartbeat;
                    }
                }
                exitCode = await engine.Exited.ConfigureAwait(false);
            }
            SetCurrent(null);
            return await TerminalAsync(job, workspace, exitCode, limit, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SetCurrent(null);
            if (!_options.KeepWorkDirectories)
            {
                workspace.Delete();
            }
        }
    }

    private async Task<OutgoingMessage> TerminalAsync(JobSubmitted job, JobWorkspace workspace, int exitCode, ErrorInfo? limit, CancellationToken cancellationToken)
    {
        var results = job.Results!;
        string Key(string name) => $"{results.Prefix.TrimEnd('/')}/{name}";
        var artifacts = new List<ObjectRef>();
        if (File.Exists(workspace.LogPath))
        {
            try
            {
                artifacts.Add(await _store.PutRefAsync(results.Bucket, Key("engine.log"), await File.ReadAllBytesAsync(workspace.LogPath, cancellationToken).ConfigureAwait(false),
                    "text/plain", cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not store the engine log of job {JobId} attempt {Attempt}", job.JobId, job.Attempt);
            }
        }

        if (limit != null)
        {
            return Failed(job, limit with { ExitCode = exitCode }, final: true, artifacts);
        }

        var state = ResultState.Read(workspace.ResultPath);
        if (state is { Completed: true } || (state is { Status: null } && exitCode == 0))
        {
            // The engine uploaded the final result before publishing its final PacketRef; confirm it landed in MinIO.
            var resultKey = Key($"{job.JobId:D}.json");
            if (await _store.TryGetAsync(results.Bucket, resultKey, cancellationToken).ConfigureAwait(false) is not { } uploaded)
            {
                return Failed(job, new ErrorInfo { Code = ErrorCode.UploadFailed, Message = $"The engine finished but s3://{results.Bucket}/{resultKey} is missing", ExitCode = exitCode },
                    final: false, artifacts);
            }
            if (await _store.TryRefAsync(results.Bucket, Key($"{job.JobId:D}-order-events.json"), Json, cancellationToken).ConfigureAwait(false) is { } orderEvents)
            {
                artifacts.Add(orderEvents);
            }
            return Completed(job, ResultStoreExtensions.RefOf(results.Bucket, resultKey, uploaded, Json), artifacts, ResultState.Statistics(uploaded), exitCode);
        }

        if (state != null || exitCode == 1)
        {
            // The Launcher exits 1 whenever the algorithm did not complete: deterministic, retrying would fail the same way.
            return Failed(job, new ErrorInfo
            {
                Code = ErrorCode.RuntimeError,
                Message = state?.RuntimeError ?? LastError(workspace.LogPath) ?? $"Algorithm ended with status {state?.Status ?? "unknown"}",
                StackTrace = state?.StackTrace,
                ExitCode = exitCode,
            }, final: true, artifacts);
        }

        return Failed(job, new ErrorInfo
        {
            Code = ErrorCode.EngineCrash,
            Message = $"Engine process exited with code {exitCode} before storing a final result" + (LastError(workspace.LogPath) is { } error ? $": {error}" : ""),
            ExitCode = exitCode,
        }, final: false, artifacts);
    }

    private EngineStartInfo StartInfo(JobWorkspace workspace, int ramMb)
    {
        var environment = new Dictionary<string, string>(_environment.Variables)
        {
            // Hard cap on the managed heap of the child (RAM limit plus headroom); Python and native memory are bounded by the RSS watchdog.
            ["DOTNET_GCHeapHardLimit"] = ((long)(ramMb + _options.MemoryHeadroomMb) * 1024 * 1024).ToString("X"),
        };
        return new EngineStartInfo
        {
            FileName = _options.DotnetPath,
            Arguments = new[] { _options.LauncherPath, "--config", workspace.ConfigPath },
            WorkingDirectory = workspace.Directory,
            LogPath = workspace.LogPath,
            Environment = environment,
        };
    }

    private static string? LastError(string logPath)
    {
        if (!File.Exists(logPath)) return null;
        try
        {
            return File.ReadLines(logPath).LastOrDefault(line => line.Contains("ERROR::", StringComparison.Ordinal) || line.Contains("Runtime Error", StringComparison.Ordinal))?.Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Describe(AlgorithmRef algorithm) => algorithm switch
    {
        BuiltinAlgorithmRef builtin => builtin.TypeName,
        UploadedAlgorithmRef uploaded => $"{uploaded.Language} s3://{uploaded.Object.Bucket}/{uploaded.Object.Key}",
        _ => algorithm.GetType().Name,
    };

    private void SetCurrent((Guid, int, int)? current)
    {
        lock (_gate)
        {
            _current = current;
        }
    }

    private async Task TryPublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not publish {Type} for job {JobId}", message.Envelope.Type, message.Envelope.Headers.JobId);
        }
    }

    private static OutgoingMessage Event<T>(string type, JobSubmitted job, T body) => OutgoingMessage.Create(Exchanges.Events,
        RoutingKeys.EventFor(type, job.Kind, job.JobId), MessageEnvelope.Create(type, job.JobId, job.Attempt, AppIds.BacktestWorker, job.Kind, job.ParentJobId), body);

    private OutgoingMessage Started(JobSubmitted job, DateTimeOffset at) => Event(MessageTypes.JobStarted, job, new JobStarted
    {
        JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, ParentJobId = job.ParentJobId, StartedAt = at, WorkerId = WorkerId, Results = job.Results!,
        EngineVersion = typeof(BacktestJobRunner).Assembly.GetName().Version?.ToString(),
    });

    private OutgoingMessage Heartbeat(JobSubmitted job, DateTimeOffset at, long rss) => Event(MessageTypes.JobHeartbeat, job, new JobHeartbeat
    {
        JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, At = at, WorkerId = WorkerId, EngineAlive = true, RssBytes = rss,
    });

    private OutgoingMessage Completed(JobSubmitted job, ObjectRef result, List<ObjectRef> artifacts, Dictionary<string, string>? statistics, int exitCode) =>
        Event(MessageTypes.JobCompleted, job, new JobCompleted
        {
            JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, ParentJobId = job.ParentJobId, CompletedAt = _time.GetUtcNow(),
            Result = result, Artifacts = artifacts, Statistics = statistics, AlgorithmId = job.JobId.ToString("D"), ExitCode = exitCode,
        });

    private OutgoingMessage Failed(JobSubmitted job, ErrorInfo error, bool final, List<ObjectRef> artifacts) => Event(MessageTypes.JobFailed, job, new JobFailed
    {
        JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, ParentJobId = job.ParentJobId, FailedAt = _time.GetUtcNow(),
        Error = error, Final = final, Artifacts = artifacts.Count > 0 ? artifacts : null,
    });
}

/// <summary>The <c>state</c> block LEAN writes into the result file (<c>Status</c>, <c>RuntimeError</c>, <c>StackTrace</c>).</summary>
public sealed record ResultState(string? Status, string? RuntimeError, string? StackTrace)
{
    public bool Completed => string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);

    public static ResultState? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!TryGet(document.RootElement, "state", out var state) || state.ValueKind != JsonValueKind.Object)
            {
                return new ResultState(null, null, null);
            }
            return new ResultState(Text(state, "Status"), Text(state, "RuntimeError"), Text(state, "StackTrace"));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public static Dictionary<string, string>? Statistics(byte[] result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            if (!TryGet(document.RootElement, "statistics", out var statistics) || statistics.ValueKind != JsonValueKind.Object) return null;
            return statistics.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        TryGet(parent, name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool TryGet(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
