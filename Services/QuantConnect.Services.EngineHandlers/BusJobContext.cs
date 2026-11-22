using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>The job attempt this engine process runs: stamped on every envelope it publishes.</summary>
public sealed record BusJobContext
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public Guid? ParentJobId { get; init; }
    public long? LeaseEpoch { get; init; }
    public required string WorkerId { get; init; }
    public required ResultsPrefix Results { get; init; }
    public string? TraceParent { get; init; }

    public static BusJobContext From(JobSubmitted job) => new()
    {
        JobId = job.JobId,
        Kind = job.Kind,
        Attempt = job.Attempt,
        ParentJobId = job.ParentJobId,
        LeaseEpoch = long.TryParse(BusSettings.Get(BusSettings.LeaseEpoch), out var epoch) ? epoch : null,
        WorkerId = BusSettings.Get(BusSettings.WorkerId) ?? Environment.MachineName,
        Results = job.Results ?? new ResultsPrefix { Prefix = $"{job.JobId:D}/{job.Attempt}/" },
        TraceParent = BusSettings.Get(BusSettings.TraceParent),
    };

    /// <summary>Reads the dispatched job from <c>bus-job-file</c> or <c>bus-job</c>; null when neither is set.</summary>
    public static JobSubmitted? LoadDispatchedJob()
    {
        if (BusSettings.Get(BusSettings.JobFile) is { } path)
        {
            return ContractJson.Deserialize<JobSubmitted>(File.ReadAllText(path));
        }
        return BusSettings.Get(BusSettings.Job) is { } json ? ContractJson.Deserialize<JobSubmitted>(json) : null;
    }
}
