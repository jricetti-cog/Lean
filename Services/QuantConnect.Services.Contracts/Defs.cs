using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Services.Contracts;

// Mirrors docs/architecture/contracts/messages/v1/defs.schema.json. Keep in sync: Tests/Services/Contracts
// round-trips every record against the JSON Schemas.

/// <summary>Contract version this assembly implements (<c>contract-version</c> header, <c>.v1</c> routing keys).</summary>
public static class ContractVersion
{
    public const int Major = 1;
    public const string Current = "1.0";
    public const string RoutingSuffix = ".v1";
}

[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind
{
    [JsonStringEnumMemberName("backtest")] Backtest,
    [JsonStringEnumMemberName("live")] Live,
    [JsonStringEnumMemberName("optimization")] Optimization,
    [JsonStringEnumMemberName("report")] Report,
    [JsonStringEnumMemberName("marketdata")] MarketData,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("dispatched")] Dispatched,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("stale")] Stale,
}

/// <summary>Same names as LEAN's <c>Language</c> enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlgorithmLanguage>))]
public enum AlgorithmLanguage { CSharp, Python }

[JsonConverter(typeof(JsonStringEnumConverter<ErrorCode>))]
public enum ErrorCode
{
    [JsonStringEnumMemberName("invalid-request")] InvalidRequest,
    [JsonStringEnumMemberName("algorithm-not-found")] AlgorithmNotFound,
    [JsonStringEnumMemberName("runtime-error")] RuntimeError,
    [JsonStringEnumMemberName("engine-crash")] EngineCrash,
    [JsonStringEnumMemberName("data-missing")] DataMissing,
    [JsonStringEnumMemberName("timeout")] Timeout,
    [JsonStringEnumMemberName("lease-lost")] LeaseLost,
    [JsonStringEnumMemberName("upload-failed")] UploadFailed,
    [JsonStringEnumMemberName("max-attempts-exceeded")] MaxAttemptsExceeded,
    [JsonStringEnumMemberName("internal")] Internal,
    [JsonStringEnumMemberName("compile-error")] CompileError,
}

/// <summary>MinIO buckets (ADR 0003).</summary>
public static class Buckets
{
    public const string Algorithms = "lean-algorithms";
    public const string Results = "lean-results";
    public const string Reports = "lean-reports";
}

/// <summary>Reference to an immutable MinIO object; used instead of inline payloads.</summary>
public sealed record ObjectRef
{
    public required string Bucket { get; init; }
    public required string Key { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public string? ContentType { get; init; }
}

/// <summary>Where an attempt uploads its outputs: <c>lean-results/&lt;jobId&gt;/&lt;attempt&gt;/</c>.</summary>
public sealed record ResultsPrefix
{
    public string Bucket { get; init; } = Buckets.Results;
    public required string Prefix { get; init; }

    public static ResultsPrefix For(Guid jobId, int attempt) => new() { Prefix = $"{jobId:D}/{attempt}/" };
}

/// <summary>The algorithm a job runs; <c>source</c> discriminates built-in classes from uploads.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "source")]
[JsonDerivedType(typeof(BuiltinAlgorithmRef), "builtin")]
[JsonDerivedType(typeof(UploadedAlgorithmRef), "upload")]
public abstract record AlgorithmRef
{
    public abstract AlgorithmLanguage Language { get; init; }
}

/// <summary>A class of <c>QuantConnect.Algorithm.CSharp.dll</c> in the worker image.</summary>
public sealed record BuiltinAlgorithmRef : AlgorithmRef
{
    public override AlgorithmLanguage Language { get; init; } = AlgorithmLanguage.CSharp;
    public required string TypeName { get; init; }
}

/// <summary>A Python file or prebuilt dll in <c>lean-algorithms</c>.</summary>
public sealed record UploadedAlgorithmRef : AlgorithmRef
{
    public override required AlgorithmLanguage Language { get; init; }
    public required ObjectRef Object { get; init; }
    public string? TypeName { get; init; }
}

public sealed record ErrorInfo
{
    public required ErrorCode Code { get; init; }
    public required string Message { get; init; }
    public string? StackTrace { get; init; }
    public int? ExitCode { get; init; }
}

public sealed record BacktestSpec
{
    public string? Name { get; init; }
    public required AlgorithmRef Algorithm { get; init; }
    public Dictionary<string, string>? Parameters { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public decimal? Cash { get; init; }
    public bool? FetchMissingData { get; init; }
    public int? MaxRuntimeSeconds { get; init; }
    public int? MaxRamMb { get; init; }
}

public sealed record LiveSpec
{
    public string? Name { get; init; }
    public required AlgorithmRef Algorithm { get; init; }
    public Dictionary<string, string>? Parameters { get; init; }
    public required string Brokerage { get; init; }
    public List<string>? DataQueueHandlers { get; init; }
    public bool? RedeployOnStale { get; init; }
}

/// <summary>LEAN <c>OptimizationStepParameter</c> (min/max/step) or <c>StaticOptimizationParameter</c> (value).</summary>
public sealed record OptimizationParameter
{
    public required string Name { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public decimal? Step { get; init; }
    public decimal? MinStep { get; init; }
    public string? Value { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<Extremum>))]
public enum Extremum
{
    [JsonStringEnumMemberName("max")] Max,
    [JsonStringEnumMemberName("min")] Min,
}

public sealed record OptimizationCriterion
{
    public required string Target { get; init; }
    public required Extremum Extremum { get; init; }
    public decimal? TargetValue { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ConstraintOperator>))]
public enum ConstraintOperator { Equals, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

public sealed record OptimizationConstraint
{
    public required string Target { get; init; }
    public required ConstraintOperator Operator { get; init; }
    public required decimal TargetValue { get; init; }
}

public sealed record OptimizationSpec
{
    public string? Name { get; init; }
    public required BacktestSpec Backtest { get; init; }
    public string? Strategy { get; init; }
    public JsonElement? StrategySettings { get; init; }
    public required OptimizationCriterion Criterion { get; init; }
    public List<OptimizationConstraint>? Constraints { get; init; }
    public required List<OptimizationParameter> Parameters { get; init; }
    public int? MaximumConcurrentBacktests { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ReportFormat>))]
public enum ReportFormat
{
    [JsonStringEnumMemberName("html")] Html,
    [JsonStringEnumMemberName("pdf")] Pdf,
}

public sealed record ReportSpec
{
    public required Guid SourceJobId { get; init; }
    public int? SourceAttempt { get; init; }
    public Guid? LiveJobId { get; init; }
    public string? StrategyName { get; init; }
    public string? StrategyDescription { get; init; }
    public string? StrategyVersion { get; init; }
    public List<ReportFormat>? Formats { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FetchMode>))]
public enum FetchMode
{
    [JsonStringEnumMemberName("download")] Download,
    [JsonStringEnumMemberName("fetch-files")] FetchFiles,
    [JsonStringEnumMemberName("generate-synthetic")] GenerateSynthetic,
}

/// <summary>Same names as LEAN's <c>SecurityType</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContractSecurityType>))]
public enum ContractSecurityType { Equity, Option, Forex, Cfd, Future, FutureOption, Crypto, CryptoFuture, Index, IndexOption }

/// <summary>Same names as LEAN's <c>Resolution</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContractResolution>))]
public enum ContractResolution { Tick, Second, Minute, Hour, Daily }

/// <summary>Same names as LEAN's <c>TickType</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContractTickType>))]
public enum ContractTickType { Trade, Quote, OpenInterest }

public sealed record MarketDataFetchSpec
{
    public required FetchMode Mode { get; init; }
    public required ContractSecurityType SecurityType { get; init; }
    public required string Market { get; init; }
    public List<string>? Tickers { get; init; }
    public required ContractResolution Resolution { get; init; }
    public ContractTickType? TickType { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public List<string>? Paths { get; init; }
    public string? Downloader { get; init; }
}
