using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Services.Contracts;

/// <summary>
/// <c>envelope.schema.json</c>: the AMQP properties and headers of every v1 message. The body travels separately
/// as the AMQP payload.
/// </summary>
public sealed record MessageEnvelope
{
    public const string JsonContentType = "application/json";
    public const byte Persistent = 2;
    public const byte Transient = 1;

    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string Type { get; init; }
    public string ContentType { get; init; } = JsonContentType;
    public string? ContentEncoding { get; init; }
    public required byte DeliveryMode { get; init; }
    /// <summary>Unix seconds.</summary>
    public required long Timestamp { get; init; }
    public string? AppId { get; init; }
    public string? Expiration { get; init; }
    public required MessageHeaders Headers { get; init; }

    /// <summary>A new envelope for <paramref name="type"/>; persistence follows the topology (heartbeat/progress/log are transient).</summary>
    public static MessageEnvelope Create(string type, Guid jobId, int attempt, string? appId = null, JobKind? kind = null,
        Guid? parentJobId = null, long? leaseEpoch = null, string? packetType = null, long? sequence = null, DateTimeOffset? now = null) => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = jobId,
        Type = type,
        DeliveryMode = MessageTypes.IsTransient(type) ? Transient : Persistent,
        Timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
        AppId = appId,
        Headers = new MessageHeaders
        {
            JobId = jobId,
            Attempt = attempt,
            JobKind = kind,
            ParentJobId = parentJobId,
            LeaseEpoch = leaseEpoch,
            PacketType = packetType,
            Sequence = sequence,
            TraceParent = Activity.Current?.Id is { } id && Activity.Current.IdFormat == ActivityIdFormat.W3C ? id : null,
            TraceState = Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.TraceStateString : null,
        },
    };

    /// <summary>This envelope carrying <paramref name="traceParent"/> unless it already has a trace context.</summary>
    public MessageEnvelope WithTraceParent(string? traceParent) =>
        Headers.TraceParent != null || string.IsNullOrEmpty(traceParent) ? this : this with { Headers = Headers with { TraceParent = traceParent } };
}

/// <summary><c>envelope.schema.json#/properties/headers</c>.</summary>
public sealed record MessageHeaders
{
    [JsonPropertyName("contract-version")] public string ContractVersion { get; init; } = Contracts.ContractVersion.Current;
    [JsonPropertyName("job-id")] public required Guid JobId { get; init; }
    [JsonPropertyName("attempt")] public required int Attempt { get; init; }
    [JsonPropertyName("job-kind")] public JobKind? JobKind { get; init; }
    [JsonPropertyName("parent-job-id")] public Guid? ParentJobId { get; init; }
    [JsonPropertyName("lease-epoch")] public long? LeaseEpoch { get; init; }
    [JsonPropertyName("packet-type")] public string? PacketType { get; init; }
    [JsonPropertyName("sequence")] public long? Sequence { get; init; }
    /// <summary>W3C trace context of the publishing span, so a job's trace spans every service it crosses.</summary>
    [JsonPropertyName("traceparent")] public string? TraceParent { get; init; }
    [JsonPropertyName("tracestate")] public string? TraceState { get; init; }
    /// <summary>Set by the broker on quorum-queue redeliveries; never set by publishers.</summary>
    [JsonPropertyName("x-delivery-count")] public long? DeliveryCount { get; init; }
    /// <summary>Additional (additive, minor-version) headers.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Producer <c>appId</c> values.</summary>
public static class AppIds
{
    public const string Gateway = "api-gateway";
    public const string Orchestrator = "job-orchestrator";
    public const string BacktestWorker = "backtest-worker";
    public const string LiveWorker = "live-worker";
    /// <summary>The bus <c>Messaging</c> adapter inside a job's child Launcher process.</summary>
    public const string LeanEngine = "lean-engine";
    public const string Optimizer = "optimizer";
    public const string Report = "report";
    public const string MarketData = "market-data";
    public const string Results = "results";
}
