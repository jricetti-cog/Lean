using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace QuantConnect.Tests.Services.Parity;

/// <summary>One row of the parity table.</summary>
public sealed record ParityRow
{
    public required string Algorithm { get; init; }
    public required string Language { get; init; }
    /// <summary><c>match</c>, <c>mismatch</c> or <c>skipped</c>.</summary>
    public required string Result { get; init; }
    /// <summary>LEAN's own regression test of the case (from the monolith TRX): <c>yes</c>, <c>no</c> or <c>not-run</c>.</summary>
    public string MonolithMatch { get; set; } = "not-run";
    /// <summary>The platform result against <c>ExpectedStatistics</c>: <c>yes</c>, <c>no</c> or <c>skipped</c>.</summary>
    public required string PlatformMatch { get; init; }
    public string? OrderListHashMatch { get; init; }
    public double? MonolithSeconds { get; set; }
    public double? PlatformSeconds { get; init; }
    public double? PlatformLatencySeconds { get; init; }
    /// <summary>Terminal job to terminal result in the results service.</summary>
    public double? ResultLagSeconds { get; init; }
    public Guid? JobId { get; init; }
    public string? Detail { get; init; }
}

public static class ParityReport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes <c>parity.json</c>, <c>parity.csv</c> and <c>parity.md</c>; returns the markdown summary.</summary>
    public static string Write(IReadOnlyCollection<ParityRow> unordered, TimeSpan platformSuite, DateTimeOffset startedAt)
    {
        var rows = unordered.OrderBy(r => r.Language).ThenBy(r => r.Algorithm, StringComparer.Ordinal).ToList();
        var monolith = ReadMonolith(ParitySettings.MonolithTrx, out var monolithSuite);
        foreach (var row in rows)
        {
            if (monolith.TryGetValue($"{row.Language}/{row.Algorithm}", out var outcome))
            {
                row.MonolithMatch = outcome.Passed ? "yes" : "no";
                row.MonolithSeconds = outcome.Seconds;
            }
        }

        var runnable = rows.Where(r => r.Result != "skipped").ToList();
        var summary = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            platformStartedAt = startedAt,
            gateway = ParitySettings.GatewayUrl.ToString(),
            concurrency = ParitySettings.Concurrency,
            cases = rows.Count,
            runnable = runnable.Count,
            skipped = rows.Count - runnable.Count,
            platformMatches = runnable.Count(r => r.PlatformMatch == "yes"),
            platformMismatches = runnable.Count(r => r.PlatformMatch != "yes"),
            monolithMatches = runnable.Count(r => r.MonolithMatch == "yes"),
            monolithMismatches = runnable.Count(r => r.MonolithMatch == "no"),
            monolithNotRun = runnable.Count(r => r.MonolithMatch == "not-run"),
            platformSuiteSeconds = Math.Round(platformSuite.TotalSeconds, 1),
            monolithSuiteSeconds = monolithSuite.HasValue ? Math.Round(monolithSuite.Value.TotalSeconds, 1) : (double?)null,
            platformWorkerSecondsSum = Math.Round(runnable.Sum(r => r.PlatformSeconds ?? 0), 1),
            monolithSecondsSum = Math.Round(runnable.Sum(r => r.MonolithSeconds ?? 0), 1),
        };

        var folder = ParitySettings.OutputFolder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "parity.json"), JsonSerializer.Serialize(new { summary, rows }, Json) + "\n");

        var csv = new StringBuilder("algorithm,language,result,monolith_match,platform_match,order_list_hash_match,monolith_seconds,platform_seconds,platform_latency_seconds,result_lag_seconds,job_id,detail\n");
        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(",", r.Algorithm, r.Language, r.Result, r.MonolithMatch, r.PlatformMatch, r.OrderListHashMatch,
                Fmt(r.MonolithSeconds), Fmt(r.PlatformSeconds), Fmt(r.PlatformLatencySeconds), Fmt(r.ResultLagSeconds), r.JobId?.ToString("D"), Csv(r.Detail)));
        }
        File.WriteAllText(Path.Combine(folder, "parity.csv"), csv.ToString());

        var md = new StringBuilder();
        md.AppendLine("# Platform parity: LEAN regression algorithms through the gateway").AppendLine();
        md.AppendLine("| | count |").AppendLine("|---|---|");
        md.AppendLine($"| cases (C# + Python) | {summary.cases} |");
        md.AppendLine($"| runnable on the repo's `Data/` | {summary.runnable} |");
        md.AppendLine($"| skipped (reason listed) | {summary.skipped} |");
        md.AppendLine($"| platform matches `ExpectedStatistics` | {summary.platformMatches} / {summary.runnable} |");
        md.AppendLine($"| monolith matches `ExpectedStatistics` | {summary.monolithMatches} / {summary.runnable} (not run: {summary.monolithNotRun}) |");
        md.AppendLine($"| suite wall time, monolith (one process, sequential) | {Fmt(summary.monolithSuiteSeconds)} s |");
        md.AppendLine($"| suite wall time, platform ({summary.concurrency} in flight) | {Fmt(summary.platformSuiteSeconds)} s |");
        var summaryMarkdown = md.ToString();
        md.AppendLine();
        md.AppendLine("Monolith = LEAN's `RegressionTests.AlgorithmStatisticsRegression`; platform = the job through `POST /api/v1/backtests`, its time from the job's `startedAt` to `finishedAt` (worker process start to final result).").AppendLine();
        md.AppendLine("| algorithm | language | monolith match | platform match | OrderListHash | monolith s | platform s | detail |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            md.AppendLine($"| {r.Algorithm} | {r.Language} | {r.MonolithMatch} | {r.PlatformMatch} | {r.OrderListHashMatch ?? ""} | {Fmt(r.MonolithSeconds)} | {Fmt(r.PlatformSeconds)} | {(r.Detail ?? "").Replace("|", "\\|")} |");
        }
        File.WriteAllText(Path.Combine(folder, "parity.md"), md.ToString());
        return summaryMarkdown;
    }

    private static Dictionary<string, (bool Passed, double Seconds)> ReadMonolith(string? trx, out TimeSpan? suite)
    {
        suite = null;
        var outcomes = new Dictionary<string, (bool, double)>(StringComparer.Ordinal);
        if (trx == null || !File.Exists(trx)) return outcomes;
        var doc = XDocument.Load(trx);
        XNamespace ns = doc.Root!.Name.Namespace;
        if (doc.Root.Element(ns + "Times") is { } times && times.Attribute("start") is { } start && times.Attribute("finish") is { } finish)
        {
            suite = DateTimeOffset.Parse(finish.Value, CultureInfo.InvariantCulture) - DateTimeOffset.Parse(start.Value, CultureInfo.InvariantCulture);
        }
        foreach (var result in doc.Descendants(ns + "UnitTestResult"))
        {
            var name = result.Attribute("testName")?.Value;
            if (name == null) continue;
            var seconds = TimeSpan.TryParse(result.Attribute("duration")?.Value, CultureInfo.InvariantCulture, out var d) ? d.TotalSeconds : 0;
            outcomes[name] = (result.Attribute("outcome")?.Value == "Passed", Math.Round(seconds, 2));
        }
        return outcomes;
    }

    private static string Fmt(double? value) => value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "";

    private static string Csv(string? value) => value == null ? "" : value.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
