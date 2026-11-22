namespace QuantConnect.Services.Contracts;

/// <summary>The v1 AMQP <c>type</c> catalogue (<c>&lt;family&gt;.v1.&lt;Name&gt;</c>) and the body record for each.</summary>
public static class MessageTypes
{
    public const string BacktestRequested = "jobs.v1.BacktestRequested";
    public const string LiveRequested = "jobs.v1.LiveRequested";
    public const string OptimizationRequested = "jobs.v1.OptimizationRequested";
    public const string ReportRequested = "jobs.v1.ReportRequested";
    public const string MarketDataFetchRequested = "jobs.v1.MarketDataFetchRequested";
    public const string JobStarted = "events.v1.JobStarted";
    public const string JobHeartbeat = "events.v1.JobHeartbeat";
    public const string JobProgress = "events.v1.JobProgress";
    public const string JobCompleted = "events.v1.JobCompleted";
    public const string JobFailed = "events.v1.JobFailed";
    public const string JobCancelled = "events.v1.JobCancelled";
    public const string CancelJob = "control.v1.CancelJob";
    public const string PacketPrefix = "packets.v1.";
    public const string PacketRef = PacketPrefix + "PacketRef";

    /// <summary>Inline engine packet types (body is LEAN's own Newtonsoft JSON, PascalCase).</summary>
    public static IReadOnlyList<string> InlinePacketTypes { get; } = new[]
    {
        "BacktestResult", "LiveResult", "Log", "Debug", "SystemDebug", "OrderEvent", "RuntimeError", "HandledError", "AlgorithmStatus",
    };

    private static readonly Dictionary<string, (Type? Body, string Schema)> Catalogue = new(StringComparer.Ordinal)
    {
        [BacktestRequested] = (typeof(JobSubmitted), "job.submitted.schema.json"),
        [LiveRequested] = (typeof(JobSubmitted), "job.submitted.schema.json"),
        [OptimizationRequested] = (typeof(Contracts.OptimizationRequested), "optimization.requested.schema.json"),
        [ReportRequested] = (typeof(Contracts.ReportRequested), "report.requested.schema.json"),
        [MarketDataFetchRequested] = (typeof(Contracts.MarketDataFetchRequested), "marketdata.fetch.requested.schema.json"),
        [JobStarted] = (typeof(Contracts.JobStarted), "job.started.schema.json"),
        [JobHeartbeat] = (typeof(Contracts.JobHeartbeat), "job.heartbeat.schema.json"),
        [JobProgress] = (typeof(Contracts.JobProgress), "job.progress.schema.json"),
        [JobCompleted] = (typeof(Contracts.JobCompleted), "job.completed.schema.json"),
        [JobFailed] = (typeof(Contracts.JobFailed), "job.failed.schema.json"),
        [JobCancelled] = (typeof(Contracts.JobCancelled), "job.cancelled.schema.json"),
        [CancelJob] = (typeof(Contracts.CancelJob), "job.cancel.schema.json"),
        [PacketRef] = (typeof(Contracts.PacketRef), "result.packet.schema.json"),
    };

    static MessageTypes()
    {
        foreach (var packet in InlinePacketTypes)
        {
            Catalogue[PacketPrefix + packet] = (null, "result.packet.schema.json");
        }
    }

    /// <summary>Every v1 AMQP type.</summary>
    public static IReadOnlyCollection<string> All => Catalogue.Keys;

    public static bool IsKnown(string type) => Catalogue.ContainsKey(type);

    /// <summary>The body record for <paramref name="type"/>; null for inline engine packets.</summary>
    public static Type? BodyType(string type) =>
        Catalogue.TryGetValue(type, out var entry) ? entry.Body : throw new ArgumentException($"Unknown v1 message type '{type}'", nameof(type));

    /// <summary>The schema file (under <c>messages/v1/</c>) that validates the body of <paramref name="type"/>.</summary>
    public static string SchemaFile(string type) =>
        Catalogue.TryGetValue(type, out var entry) ? entry.Schema : throw new ArgumentException($"Unknown v1 message type '{type}'", nameof(type));

    /// <summary>Heartbeats and progress are published transient (<c>deliveryMode</c> 1); everything else is persistent.</summary>
    public static bool IsTransient(string type) => type is JobHeartbeat or JobProgress;

    /// <summary>The <c>&lt;event&gt;</c> segment of <c>job.&lt;kind&gt;.&lt;jobId&gt;.&lt;event&gt;.v1</c> for event types.</summary>
    public static string? EventSegment(string type) => type switch
    {
        JobStarted => "status",
        JobCompleted => "status",
        JobFailed => "status",
        JobCancelled => "status",
        JobHeartbeat => "heartbeat",
        JobProgress => "progress",
        _ when type.StartsWith(PacketPrefix, StringComparison.Ordinal) => type[PacketPrefix.Length..] switch
        {
            "Log" or "Debug" or "SystemDebug" => "log",
            "OrderEvent" => "order",
            "RuntimeError" or "HandledError" => "error",
            _ => "result",
        },
        _ => null,
    };
}
