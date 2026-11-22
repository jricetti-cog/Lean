using Microsoft.Extensions.Configuration;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Results;

/// <summary>Results service settings (<c>Results:*</c>, <c>S3:*</c>, <c>ConnectionStrings:Results</c>).</summary>
public sealed record ResultsSettings
{
    public required string ConnectionString { get; init; }
    /// <summary>Series points kept per chart series in PostgreSQL; full resolution stays in the MinIO result JSON.</summary>
    public int ChartMaxPoints { get; init; } = 2000;
    /// <summary>Most recent log lines kept per attempt (ADR 0003: capped recent logs).</summary>
    public int MaxLogsPerAttempt { get; init; } = 10_000;
    public ushort Prefetch { get; init; } = 64;
    /// <summary>Jobs ingested concurrently; each job's messages stay in order (one partition per job).</summary>
    public int Partitions { get; init; } = 8;
    public bool Consume { get; init; } = true;
    public string ResultsBucket { get; init; } = Buckets.Results;

    public static ResultsSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Results");
        return new ResultsSettings
        {
            ConnectionString = configuration.GetConnectionString("Results")
                ?? throw new InvalidOperationException("ConnectionStrings:Results is required"),
            ChartMaxPoints = section.GetValue("ChartMaxPoints", 2000),
            MaxLogsPerAttempt = section.GetValue("MaxLogsPerAttempt", 10_000),
            Prefetch = section.GetValue<ushort>("Prefetch", 64),
            Partitions = Math.Max(1, section.GetValue("Partitions", 8)),
            Consume = section.GetValue("Consume", true),
            ResultsBucket = configuration["S3:ResultsBucket"] is { Length: > 0 } bucket ? bucket : Buckets.Results,
        };
    }
}
