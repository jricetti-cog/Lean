using QuantConnect.Interfaces;

namespace QuantConnect.Tests.Services.Parity;

/// <summary>
/// The assertions of LEAN's <c>AlgorithmRunner.AssertAlgorithmState</c>, applied to a platform job: final status and
/// every <see cref="IRegressionAlgorithmDefinition.ExpectedStatistics"/> entry (including <c>OrderListHash</c>) must be
/// equal, with <c>-0</c> and <c>0</c> treated alike exactly as there.
/// </summary>
public static class ParityComparison
{
    public const string OrderListHash = "OrderListHash";

    /// <summary>The job status the platform reports for an engine that ends in <paramref name="status"/>.</summary>
    public static string ExpectedJobStatus(AlgorithmStatus status) => status switch
    {
        AlgorithmStatus.RuntimeError => "failed",
        _ => "completed",
    };

    public static List<string> Compare(IRegressionAlgorithmDefinition definition, PlatformRun run)
    {
        var problems = new List<string>();
        var expectedStatus = ExpectedJobStatus(definition.AlgorithmStatus);
        if (run.Status != expectedStatus)
        {
            problems.Add($"status: expected {expectedStatus} (algorithm {definition.AlgorithmStatus}), platform {run.Status}"
                + (run.Error != null ? $" [{run.ErrorCode}] {Truncate(run.Error)}" : ""));
        }
        else if (definition.AlgorithmStatus == AlgorithmStatus.RuntimeError && run.ErrorCode != "runtime-error")
        {
            problems.Add($"error code: expected runtime-error, platform {run.ErrorCode}");
        }
        foreach (var (key, value) in definition.ExpectedStatistics)
        {
            if (!run.Statistics.TryGetValue(key, out var actual))
            {
                problems.Add($"{key}: expected {value}, platform missing");
            }
            else if (Normalize(actual) != Normalize(value))
            {
                problems.Add($"{key}: expected {value}, platform {actual}");
            }
        }
        return problems;
    }

    public static bool? OrderListHashMatches(IRegressionAlgorithmDefinition definition, PlatformRun run) =>
        definition.ExpectedStatistics.TryGetValue(OrderListHash, out var expected)
            ? run.Statistics.TryGetValue(OrderListHash, out var actual) && actual == expected
            : null;

    private static string Normalize(string value) => value == "-0" ? "0" : value;

    private static string Truncate(string text) => text.Length > 300 ? text[..300] + "..." : text;
}
