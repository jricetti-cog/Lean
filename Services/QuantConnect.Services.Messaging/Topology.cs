using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Messaging;

/// <summary>Exchange names from <c>docs/architecture/contracts/topology/definitions.json</c> (vhost <c>lean</c>).</summary>
public static class Exchanges
{
    public const string Jobs = "lean.jobs";
    public const string JobsDeadLetter = "lean.jobs.dlx";
    public const string Events = "lean.events";
    public const string Control = "lean.control";
    public const string MarketDataLive = "lean.marketdata.live";
}

/// <summary>Durable queue names from the v1 topology.</summary>
public static class Queues
{
    public const string Backtest = "jobs.backtest";
    public const string Live = "jobs.live";
    public const string Report = "jobs.report";
    public const string Optimization = "jobs.optimization";
    public const string MarketDataFetch = "marketdata.fetch";
    public const string ResultsPackets = "results.packets";
    public const string OrchestratorStatus = "orchestrator.status";
    public const string OptimizerChildren = "optimizer.children";

    public static string Dispatch(JobKind kind) => kind switch
    {
        JobKind.Backtest => Backtest,
        JobKind.Live => Live,
        JobKind.Report => Report,
        JobKind.Optimization => Optimization,
        JobKind.MarketData => MarketDataFetch,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string DeadLetter(string queue) => queue + ".dead";
}

/// <summary>
/// Builders and checks for v1 routing keys. Every key a service publishes ends in <c>.v1</c>; a breaking
/// change gets <c>.v2</c> keys and new queues alongside, never an in-place edit.
/// </summary>
public static class RoutingKeys
{
    public static string Dispatch(JobKind kind) => kind switch
    {
        JobKind.MarketData => "marketdata.fetch.v1",
        _ => $"jobs.{Segment(kind)}.v1",
    };

    /// <summary>Dispatch key for a <c>jobs.v1.*</c> type.</summary>
    public static string DispatchFor(string type) => type switch
    {
        MessageTypes.BacktestRequested => Dispatch(JobKind.Backtest),
        MessageTypes.LiveRequested => Dispatch(JobKind.Live),
        MessageTypes.OptimizationRequested => Dispatch(JobKind.Optimization),
        MessageTypes.ReportRequested => Dispatch(JobKind.Report),
        MessageTypes.MarketDataFetchRequested => Dispatch(JobKind.MarketData),
        _ => throw new ArgumentException($"'{type}' is not a dispatch type", nameof(type)),
    };

    /// <summary><c>job.&lt;kind&gt;.&lt;jobId&gt;.&lt;event&gt;.v1</c> on <c>lean.events</c>.</summary>
    public static string Event(JobKind kind, Guid jobId, string eventSegment) => $"job.{Segment(kind)}.{jobId:D}.{eventSegment}.v1";

    /// <summary>Event key for an <c>events.v1.*</c> or <c>packets.v1.*</c> type.</summary>
    public static string EventFor(string type, JobKind kind, Guid jobId) =>
        Event(kind, jobId, MessageTypes.EventSegment(type) ?? throw new ArgumentException($"'{type}' is not an event type", nameof(type)));

    /// <summary><c>job.&lt;jobId&gt;.cancel.v1</c> on <c>lean.control</c>.</summary>
    public static string Cancel(Guid jobId) => $"job.{jobId:D}.cancel.v1";

    /// <summary><c>job.&lt;jobId&gt;.liquidate.v1</c> on <c>lean.control</c> (live deployments).</summary>
    public static string Liquidate(Guid jobId) => $"job.{jobId:D}.liquidate.v1";

    /// <summary><c>md.&lt;securityType&gt;.&lt;market&gt;.&lt;ticker&gt;.v1</c> on <c>lean.marketdata.live</c>, lower-case.</summary>
    public static string MarketData(string securityType, string market, string ticker) =>
        $"md.{Word(securityType)}.{Word(market)}.{Word(ticker)}.v1".ToLowerInvariant();

    /// <summary><c>&lt;queue&gt;.dead.v1</c> on <c>lean.jobs.dlx</c>.</summary>
    public static string DeadLetter(string queue) => $"{queue}.dead.v1";

    public static string Segment(JobKind kind) => kind switch
    {
        JobKind.Backtest => "backtest",
        JobKind.Live => "live",
        JobKind.Optimization => "optimization",
        JobKind.Report => "report",
        JobKind.MarketData => "marketdata",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>True for a concrete (no wildcard) key with non-empty words ending in <c>.v1</c>.</summary>
    public static bool IsV1(string routingKey)
    {
        if (string.IsNullOrEmpty(routingKey) || routingKey.Length > 255 || !routingKey.EndsWith(ContractVersion.RoutingSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var words = routingKey.Split('.');
        return words.Length >= 2 && words.All(w => w.Length > 0 && w != "*" && w != "#");
    }

    public static void EnsureV1(string routingKey)
    {
        if (!IsV1(routingKey))
        {
            throw new ArgumentException($"Routing key '{routingKey}' is not a concrete v1 key (must end in '{ContractVersion.RoutingSuffix}')", nameof(routingKey));
        }
    }

    /// <summary>AMQP topic matching: <c>*</c> matches one word, <c>#</c> zero or more.</summary>
    public static bool TopicMatches(string pattern, string routingKey) =>
        Match(pattern.Split('.'), 0, routingKey.Split('.'), 0);

    private static bool Match(string[] pattern, int p, string[] key, int k)
    {
        if (p == pattern.Length)
        {
            return k == key.Length;
        }
        if (pattern[p] == "#")
        {
            for (var skip = k; skip <= key.Length; skip++)
            {
                if (Match(pattern, p + 1, key, skip))
                {
                    return true;
                }
            }
            return false;
        }
        return k < key.Length && (pattern[p] == "*" || pattern[p] == key[k]) && Match(pattern, p + 1, key, k + 1);
    }

    private static string Word(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains('.') ? throw new ArgumentException($"Invalid routing-key word '{value}'") : value;
}
