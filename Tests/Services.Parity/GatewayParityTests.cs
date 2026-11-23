using System.Collections.Concurrent;
using NUnit.Framework;

namespace QuantConnect.Tests.Services.Parity;

/// <summary>
/// Every regression algorithm of LEAN's local backtest suite, run through the gateway of a running stack and held to
/// its own <c>ExpectedStatistics</c>. A failure here is a platform bug: fix the services, never the expectation.
/// </summary>
[TestFixture, Category("ServicesParity"), NonParallelizable]
public class GatewayParityTests
{
    private static readonly ConcurrentBag<ParityRow> Rows = new();
    private static GatewayClient? _gateway;
    private static ParityRunner? _runner;
    private static Task? _suite;
    private static readonly CancellationTokenSource Cancel = new();

    public static IEnumerable<TestCaseData> Cases() => ParityCatalog.All.Select(c => new TestCaseData(c).SetName(c.Name));

    [OneTimeSetUp]
    public async Task StartSuite()
    {
        _gateway = new GatewayClient(ParitySettings.GatewayUrl, ParitySettings.ApiKey);
        if (!await _gateway.IsHealthyAsync(Cancel.Token))
        {
            var message = $"No gateway at {ParitySettings.GatewayUrl}: bring the stack up (see Tests/Services.Parity/README.md)";
            if (ParitySettings.GatewayExplicit) Assert.Fail(message);
            Assert.Ignore(message);
        }
        _runner = new ParityRunner(_gateway);
        _suite = _runner.RunAsync(ParityCatalog.All.Where(c => c.SkipReason == null).ToList(), Cancel.Token);
    }

    [Test, TestCaseSource(nameof(Cases))]
    public async Task PlatformMatchesExpectedStatistics(ParityCase parityCase)
    {
        if (parityCase.SkipReason != null)
        {
            Rows.Add(new ParityRow
            {
                Algorithm = parityCase.Algorithm, Language = parityCase.Language.ToString(), Result = "skipped", MonolithMatch = "not-run",
                PlatformMatch = "skipped", Detail = parityCase.SkipReason,
            });
            Assert.Ignore(parityCase.SkipReason);
        }

        var run = await _runner!.Get(parityCase);
        var problems = ParityComparison.Compare(parityCase.Definition, run);
        var hash = ParityComparison.OrderListHashMatches(parityCase.Definition, run);
        Rows.Add(new ParityRow
        {
            Algorithm = parityCase.Algorithm, Language = parityCase.Language.ToString(), Result = problems.Count == 0 ? "match" : "mismatch",
            PlatformMatch = problems.Count == 0 ? "yes" : "no", OrderListHashMatch = hash switch { true => "yes", false => "no", null => null },
            PlatformSeconds = run.WallSeconds is { } s ? Math.Round(s, 2) : null, PlatformLatencySeconds = Math.Round(run.LatencySeconds, 2),
            ResultLagSeconds = run.ResultLagSeconds is { } lag ? Math.Round(lag, 2) : null,
            JobId = run.JobId, Detail = problems.Count == 0 ? null : string.Join("; ", problems),
        });
        TestContext.Out.WriteLine($"{parityCase.Name}: job {run.JobId} {run.Status} in {run.WallSeconds:0.0} s, OrderListHash {(hash == true ? "match" : "MISMATCH")}");
        if (problems.Count > 0)
        {
            Assert.Fail($"{parityCase.Name} (job {run.JobId}) differs from its ExpectedStatistics through the platform:\n  " + string.Join("\n  ", problems));
        }
    }

    [OneTimeTearDown]
    public async Task WriteTable()
    {
        if (_runner == null) return;
        if (_suite != null && Rows.Count(r => r.Result != "skipped") >= ParityCatalog.All.Count(c => c.SkipReason == null))
        {
            await _suite;
        }
        Cancel.Cancel();
        var summary = ParityReport.Write(Rows.ToList(), _runner.SuiteElapsed, _runner.StartedAt);
        TestContext.Progress.WriteLine(summary);
        TestContext.Progress.WriteLine($"Parity table: {ParitySettings.OutputFolder}/parity.{{md,csv,json}}");
        _gateway?.Dispose();
    }
}
