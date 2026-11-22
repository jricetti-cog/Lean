using System.Text.Json.Serialization;

namespace QuantConnect.Services.Contracts;

// Message bodies, one record per docs/architecture/contracts/messages/v1/*.schema.json.

/// <summary><c>job.submitted.schema.json</c>: <c>jobs.v1.BacktestRequested</c> / <c>jobs.v1.LiveRequested</c>.</summary>
public sealed record JobSubmitted
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required string SubmittedBy { get; init; }
    public Guid? ParentJobId { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public ResultsPrefix? Results { get; init; }
    public BacktestSpec? Backtest { get; init; }
    public LiveSpec? Live { get; init; }
}

/// <summary><c>optimization.requested.schema.json</c>: <c>jobs.v1.OptimizationRequested</c>.</summary>
public sealed record OptimizationRequested
{
    public required Guid JobId { get; init; }
    public JobKind Kind { get; init; } = JobKind.Optimization;
    public required int Attempt { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required string SubmittedBy { get; init; }
    public required ResultsPrefix Results { get; init; }
    public required OptimizationSpec Optimization { get; init; }
}

/// <summary><c>report.requested.schema.json</c>: <c>jobs.v1.ReportRequested</c>.</summary>
public sealed record ReportRequested
{
    public required Guid JobId { get; init; }
    public JobKind Kind { get; init; } = JobKind.Report;
    public required int Attempt { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required string SubmittedBy { get; init; }
    public required ReportSpec Report { get; init; }
    public required ObjectRef BacktestResult { get; init; }
    public ObjectRef? LiveResult { get; init; }
}

/// <summary><c>marketdata.fetch.requested.schema.json</c>: <c>jobs.v1.MarketDataFetchRequested</c>.</summary>
public sealed record MarketDataFetchRequested
{
    public required Guid JobId { get; init; }
    public JobKind Kind { get; init; } = JobKind.MarketData;
    public required int Attempt { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required string SubmittedBy { get; init; }
    public Guid? RequestingJobId { get; init; }
    public required MarketDataFetchSpec Fetch { get; init; }
}

/// <summary><c>marketdata.live.schema.json</c>: <c>marketdata.v1.LiveData</c> (transient), one symbol's batch on <c>lean.marketdata.live</c>.</summary>
public sealed record LiveDataBatch
{
    public required Guid FeedId { get; init; }
    public required LiveDataSymbol Symbol { get; init; }
    public required long Sequence { get; init; }
    public required List<LiveDataPoint> Points { get; init; }
}

public sealed record LiveDataSymbol
{
    public required ContractSecurityType SecurityType { get; init; }
    public required string Market { get; init; }
    public required string Ticker { get; init; }
    /// <summary>LEAN <c>SecurityIdentifier</c> string.</summary>
    public required string Id { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<LiveDataType>))]
public enum LiveDataType { Tick, TradeBar, QuoteBar }

public sealed record LiveDataPoint
{
    public required LiveDataType DataType { get; init; }
    public ContractTickType? TickType { get; init; }
    public required DateTimeOffset Time { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public required decimal Value { get; init; }
    public decimal? Open { get; init; }
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public decimal? Close { get; init; }
    public decimal? Volume { get; init; }
    public decimal? BidPrice { get; init; }
    public decimal? AskPrice { get; init; }
    public decimal? BidSize { get; init; }
    public decimal? AskSize { get; init; }
}

/// <summary><c>job.started.schema.json</c>: <c>events.v1.JobStarted</c>.</summary>
public sealed record JobStarted
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public Guid? ParentJobId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string WorkerId { get; init; }
    public long? LeaseEpoch { get; init; }
    public required ResultsPrefix Results { get; init; }
    public string? EngineVersion { get; init; }
}

/// <summary><c>job.heartbeat.schema.json</c>: <c>events.v1.JobHeartbeat</c> (transient).</summary>
public sealed record JobHeartbeat
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string WorkerId { get; init; }
    public long? LeaseEpoch { get; init; }
    public bool? EngineAlive { get; init; }
    public long? RssBytes { get; init; }
}

/// <summary><c>job.progress.schema.json</c>: <c>events.v1.JobProgress</c> (transient).</summary>
public sealed record JobProgress
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public Guid? ParentJobId { get; init; }
    public required DateTimeOffset At { get; init; }
    public required double Progress { get; init; }
    public DateTimeOffset? AlgorithmTime { get; init; }
    public Dictionary<string, string>? RuntimeStatistics { get; init; }
    public OptimizationProgress? Optimization { get; init; }
}

public sealed record OptimizationProgress
{
    public required int Total { get; init; }
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required int Running { get; init; }
    public Guid? BestJobId { get; init; }
    public double? BestTargetValue { get; init; }
}

/// <summary><c>job.completed.schema.json</c>: <c>events.v1.JobCompleted</c>.</summary>
public sealed record JobCompleted
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public Guid? ParentJobId { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required ObjectRef Result { get; init; }
    public required List<ObjectRef> Artifacts { get; init; }
    public Dictionary<string, string>? Statistics { get; init; }
    public string? AlgorithmId { get; init; }
    public int? ExitCode { get; init; }
}

/// <summary><c>job.failed.schema.json</c>: <c>events.v1.JobFailed</c>.</summary>
public sealed record JobFailed
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public Guid? ParentJobId { get; init; }
    public required DateTimeOffset FailedAt { get; init; }
    public required ErrorInfo Error { get; init; }
    public required bool Final { get; init; }
    public List<ObjectRef>? Artifacts { get; init; }
}

/// <summary><c>job.cancel.schema.json</c>: <c>control.v1.CancelJob</c>.</summary>
public sealed record CancelJob
{
    public required Guid JobId { get; init; }
    public int? Attempt { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public string? RequestedBy { get; init; }
    public required string Reason { get; init; }
    public long? LeaseEpoch { get; init; }
}

/// <summary><c>job.cancelled.schema.json</c>: <c>events.v1.JobCancelled</c>.</summary>
public sealed record JobCancelled
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required int Attempt { get; init; }
    public Guid? ParentJobId { get; init; }
    public required DateTimeOffset CancelledAt { get; init; }
    public required string Reason { get; init; }
    public long? LeaseEpoch { get; init; }
    public List<ObjectRef>? Artifacts { get; init; }
}

/// <summary>
/// <c>result.packet.schema.json#/$defs/PacketRef</c>: <c>packets.v1.PacketRef</c>, a packet too large to inline
/// (and every final result) uploaded to MinIO.
/// </summary>
public sealed record PacketRef
{
    public required string PacketType { get; init; }
    public required Guid JobId { get; init; }
    public required int Attempt { get; init; }
    public string? AlgorithmId { get; init; }
    public required bool Final { get; init; }
    public double? Progress { get; init; }
    public required ObjectRef Object { get; init; }
    public required string Format { get; init; }
    public Dictionary<string, string>? Statistics { get; init; }

    /// <summary>The engine's own result file (always the format of a final result).</summary>
    public const string FinalFormat = "lean-result-file";
    /// <summary>The PascalCase packet JSON that would have been inline.</summary>
    public const string PacketFormat = "lean-packet";
}
