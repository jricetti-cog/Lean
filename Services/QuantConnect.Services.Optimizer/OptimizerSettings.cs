using Microsoft.Extensions.Configuration;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Optimizer;

/// <summary>Optimizer settings (<c>Optimizer:*</c>, <c>S3:ResultsBucket</c>).</summary>
public sealed record OptimizerSettings
{
    /// <summary>job-orchestrator base URL: child backtests are submitted and cancelled through its jobs API.</summary>
    public Uri OrchestratorUri { get; init; } = new("http://job-orchestrator:8080/");
    /// <summary>Upper bound for an optimization's <c>maximumConcurrentBacktests</c> (children in flight at once).</summary>
    public int MaxConcurrentBacktests { get; init; } = 16;
    /// <summary>Children in flight when the request does not say (contract default).</summary>
    public int DefaultConcurrentBacktests { get; init; } = 4;
    /// <summary>Optimizations run at once by one replica (<c>jobs.optimization</c> prefetch).</summary>
    public int MaxConcurrentOptimizations { get; init; } = 2;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How often running children are checked against the orchestrator (failures that publish no final event).</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>LEAN <c>optimization-update-interval</c>: minimum seconds between <c>job.progress</c> updates.</summary>
    public int UpdateIntervalSeconds { get; init; } = 5;
    /// <summary>Attempts after which a retryable failure is reported as <c>final</c>.</summary>
    public int MaxAttempts { get; init; } = 3;
    public string ResultsBucket { get; init; } = Buckets.Results;
    /// <summary>Finished optimizations kept in memory for the API.</summary>
    public int RetainFinished { get; init; } = 200;
    public bool Consume { get; init; } = true;
    public string WorkerId { get; init; } = $"optimizer-{Environment.MachineName}";

    public static OptimizerSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Optimizer");
        var defaults = new OptimizerSettings();
        return new OptimizerSettings
        {
            OrchestratorUri = section["OrchestratorUri"] is { Length: > 0 } uri ? new Uri(uri) : defaults.OrchestratorUri,
            MaxConcurrentBacktests = Math.Max(1, section.GetValue("MaxConcurrentBacktests", defaults.MaxConcurrentBacktests)),
            DefaultConcurrentBacktests = Math.Max(1, section.GetValue("DefaultConcurrentBacktests", defaults.DefaultConcurrentBacktests)),
            MaxConcurrentOptimizations = Math.Max(1, section.GetValue("MaxConcurrentOptimizations", defaults.MaxConcurrentOptimizations)),
            HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, section.GetValue("HeartbeatIntervalSeconds", 30))),
            ReconcileInterval = TimeSpan.FromSeconds(Math.Max(1, section.GetValue("ReconcileIntervalSeconds", 15))),
            UpdateIntervalSeconds = Math.Max(0, section.GetValue("UpdateIntervalSeconds", defaults.UpdateIntervalSeconds)),
            MaxAttempts = Math.Max(1, section.GetValue("MaxAttempts", defaults.MaxAttempts)),
            ResultsBucket = configuration["S3:ResultsBucket"] is { Length: > 0 } bucket ? bucket : Buckets.Results,
            RetainFinished = Math.Max(1, section.GetValue("RetainFinished", defaults.RetainFinished)),
            Consume = section.GetValue("Consume", true),
            WorkerId = section["WorkerId"] is { Length: > 0 } id ? id : defaults.WorkerId,
        };
    }
}
