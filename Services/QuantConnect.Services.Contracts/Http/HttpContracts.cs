using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Services.Contracts.Http;

// components.schemas of docs/architecture/contracts/openapi/*.yaml. Serialize with ContractJson.Options.

/// <summary><c>common.yaml#/components/schemas/Problem</c> (RFC 9457).</summary>
public sealed record Problem
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required int Status { get; init; }
    public string? Detail { get; init; }
    public string? Instance { get; init; }
    public string? TraceId { get; init; }
    public Dictionary<string, List<string>>? Errors { get; init; }
}

/// <summary><c>common.yaml#/components/schemas/Page</c>: cursor paging; <c>next</c> is opaque.</summary>
public sealed record Page<T>
{
    public required List<T> Items { get; init; }
    public string? Next { get; init; }
}

/// <summary><c>common.yaml#/components/schemas/JobRequest</c>: exactly one spec, matching <c>kind</c>.</summary>
public sealed record JobRequest
{
    public required JobKind Kind { get; init; }
    public Guid? ParentJobId { get; init; }
    public BacktestSpec? Backtest { get; init; }
    public LiveSpec? Live { get; init; }
    public OptimizationSpec? Optimization { get; init; }
    public ReportSpec? Report { get; init; }
}

/// <summary><c>common.yaml#/components/schemas/Job</c>: the job row as seen over HTTP.</summary>
public sealed record Job
{
    public required Guid Id { get; init; }
    public required JobKind Kind { get; init; }
    public required JobStatus Status { get; init; }
    public required int Attempt { get; init; }
    public Guid? ParentJobId { get; init; }
    public string? Name { get; init; }
    public required string SubmittedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public double? Progress { get; init; }
    public long? LeaseEpoch { get; init; }
    public string? WorkerId { get; init; }
    public ErrorInfo? Error { get; init; }
    public JobRequest? Spec { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<HealthStatus>))]
public enum HealthStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("degraded")] Degraded,
    [JsonStringEnumMemberName("down")] Down,
}

/// <summary><c>common.yaml#/components/schemas/Health</c>: body of <c>/healthz</c> and <c>/readyz</c>.</summary>
public sealed record Health
{
    public required HealthStatus Status { get; init; }
    public Dictionary<string, HealthStatus>? Checks { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SseEventName>))]
public enum SseEventName
{
    [JsonStringEnumMemberName("snapshot")] Snapshot,
    [JsonStringEnumMemberName("status")] Status,
    [JsonStringEnumMemberName("heartbeat")] Heartbeat,
    [JsonStringEnumMemberName("progress")] Progress,
    [JsonStringEnumMemberName("result")] Result,
    [JsonStringEnumMemberName("log")] Log,
    [JsonStringEnumMemberName("order")] Order,
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary><c>gateway.openapi.yaml#/components/schemas/SseEvent</c>: <c>body</c> is the AMQP body verbatim.</summary>
public sealed record SseEvent
{
    public required SseEventName Event { get; init; }
    public required string Type { get; init; }
    public required Guid JobId { get; init; }
    public required int Attempt { get; init; }
    public long? Sequence { get; init; }
    public required JsonElement Body { get; init; }
}

/// <summary><c>orchestrator.openapi.yaml#/components/schemas/Attempt</c>.</summary>
public sealed record JobAttempt
{
    public required Guid JobId { get; init; }
    public required int Attempt { get; init; }
    public required ResultsPrefix Results { get; init; }
    public DateTimeOffset? Deadline { get; init; }
}

/// <summary><c>orchestrator.openapi.yaml#/components/schemas/Lease</c>.</summary>
public sealed record Lease
{
    public required Guid JobId { get; init; }
    public required int Attempt { get; init; }
    public required long LeaseEpoch { get; init; }
    public required int HeartbeatIntervalSeconds { get; init; }
    public ResultsPrefix? Results { get; init; }
}

/// <summary><c>orchestrator.openapi.yaml#/components/schemas/QueueHint</c>.</summary>
public sealed record QueueHint
{
    public required string Queue { get; init; }
    public required long Ready { get; init; }
    public required long Unacked { get; init; }
    public long? Dead { get; init; }
    public required int SuggestedReplicas { get; init; }
}

/// <summary><c>market-data.openapi.yaml#/components/schemas/DateRange</c>.</summary>
public sealed record DateRange
{
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
}

/// <summary><c>market-data.openapi.yaml#/components/schemas/CatalogEntry</c>.</summary>
public sealed record CatalogEntry
{
    public required string SecurityType { get; init; }
    public required string Market { get; init; }
    public required string Ticker { get; init; }
    public required string Resolution { get; init; }
    public required List<ContractTickType> TickTypes { get; init; }
    public required int Files { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
}

/// <summary><c>market-data.openapi.yaml#/components/schemas/Coverage</c>.</summary>
public sealed record Coverage
{
    public required string SecurityType { get; init; }
    public required string Market { get; init; }
    public required string Ticker { get; init; }
    public required string Resolution { get; init; }
    public required List<DateRange> Ranges { get; init; }
    public required List<DateOnly> Missing { get; init; }
}

/// <summary><c>market-data.openapi.yaml#/components/schemas/Bar</c>.</summary>
public sealed record Bar
{
    public required DateTimeOffset Time { get; init; }
    public required decimal Open { get; init; }
    public required decimal High { get; init; }
    public required decimal Low { get; init; }
    public required decimal Close { get; init; }
    public decimal? Volume { get; init; }
}

/// <summary><c>market-data.openapi.yaml#/components/schemas/FetchTask</c>.</summary>
public sealed record FetchTask
{
    public required Guid Id { get; init; }
    public required JobStatus Status { get; init; }
    public required int Attempt { get; init; }
    public required MarketDataFetchSpec Fetch { get; init; }
    public Guid? RequestingJobId { get; init; }
    public long? FilesWritten { get; init; }
    public ErrorInfo? Error { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary><c>optimizer.openapi.yaml#/components/schemas/ChildBacktest</c>.</summary>
public sealed record ChildBacktest
{
    public required Guid JobId { get; init; }
    public required Dictionary<string, string> Parameters { get; init; }
    public required JobStatus Status { get; init; }
    public double? Objective { get; init; }
    public Dictionary<string, string>? Statistics { get; init; }
}

/// <summary><c>optimizer.openapi.yaml#/components/schemas/OptimizationState</c>.</summary>
public sealed record OptimizationState
{
    public required Guid JobId { get; init; }
    public required JobStatus Status { get; init; }
    public required int TotalBacktests { get; init; }
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required int Running { get; init; }
    public required int Queued { get; init; }
    public ChildBacktest? Best { get; init; }
    public ObjectRef? Result { get; init; }
}

/// <summary><c>report.openapi.yaml#/components/schemas/Report</c>.</summary>
public sealed record Report
{
    public required Guid JobId { get; init; }
    public required JobStatus Status { get; init; }
    public required int Attempt { get; init; }
    public required ReportSpec Spec { get; init; }
    public ObjectRef? Html { get; init; }
    public ObjectRef? Pdf { get; init; }
    public ErrorInfo? Error { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary><c>results.openapi.yaml#/components/schemas/ResultSummary</c>.</summary>
public sealed record ResultSummary
{
    public required Guid JobId { get; init; }
    public required int Attempt { get; init; }
    public required JobKind Kind { get; init; }
    public Guid? ParentJobId { get; init; }
    public required JobStatus Status { get; init; }
    public string? AlgorithmId { get; init; }
    public required double Progress { get; init; }
    public Dictionary<string, string>? Parameters { get; init; }
    public required Dictionary<string, string> Statistics { get; init; }
    public required Dictionary<string, string> RuntimeStatistics { get; init; }
    public long? OrderCount { get; init; }
    public ObjectRef? Result { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary><c>results.openapi.yaml#/components/schemas/LiveState</c>.</summary>
public sealed record LiveState
{
    public required Guid JobId { get; init; }
    public required int Attempt { get; init; }
    public required List<System.Text.Json.Nodes.JsonObject> Holdings { get; init; }
    public required List<LiveCashAmount> Cash { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record LiveCashAmount
{
    public required string Currency { get; init; }
    public required decimal Amount { get; init; }
}

/// <summary><c>results.openapi.yaml#/components/schemas/LogLine</c>.</summary>
public sealed record LogLine
{
    public required DateTimeOffset Time { get; init; }
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? StackTrace { get; init; }
}

/// <summary>Health-check tags shared by every host: checks tagged <see cref="Ready"/> gate <c>/readyz</c>.</summary>
public static class HealthTags
{
    public const string Live = "live";
    public const string Ready = "ready";
}
