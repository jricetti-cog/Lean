using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Queues;
using QuantConnect.Securities;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;
using BusQueues = QuantConnect.Services.Messaging.Queues;
using QuantConnect.Util;

namespace QuantConnect.Services.MarketData.Live;

public sealed class LiveFanoutOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>LEAN <see cref="IDataQueueHandler"/> feeding the fan-out; <c>FakeDataQueue</c> (simulated) since no vendor is connected.</summary>
    public string DataQueueHandler { get; set; } = nameof(FakeDataQueue);

    /// <summary>Symbols as <c>&lt;SecurityType&gt;/&lt;market&gt;/&lt;TICKER&gt;</c>, e.g. <c>Equity/usa/SPY</c>.</summary>
    public List<string> Symbols { get; set; } = new();

    /// <summary><c>Tick</c> publishes ticks; any other resolution publishes trade/quote bars aggregated by LEAN.</summary>
    public Resolution Resolution { get; set; } = Resolution.Second;

    /// <summary>Simulated points per second per symbol for <c>FakeDataQueue</c>.</summary>
    public int DataPointsPerSecond { get; set; } = 2;

    public int FlushIntervalMilliseconds { get; set; } = 1000;

    public int MaxPointsPerMessage { get; set; } = 1000;
}

/// <summary>
/// Live fan-out (ADR 0004): one LEAN data queue handler subscribed to the configured symbols, its ticks / aggregated bars
/// batched per symbol and published on <c>lean.marketdata.live</c> with routing key <c>md.&lt;securityType&gt;.&lt;market&gt;.&lt;ticker&gt;.v1</c>
/// (<c>marketdata.v1.LiveData</c>, transient, not mandatory). Live workers bind their own exclusive queues to the symbols they trade.
/// </summary>
public sealed class LiveDataFanout : BackgroundService
{
    private sealed record Feed(Symbol Symbol, DateTimeZoneInfo Zone, List<IEnumerator<BaseData>> Enumerators)
    {
        public long Sequence;
    }

    private sealed record DateTimeZoneInfo(NodaTime.DateTimeZone Exchange);

    private readonly IMessagePublisher _publisher;
    private readonly LiveFanoutOptions _options;
    private readonly ILogger<LiveDataFanout> _logger;
    private readonly List<Feed> _feeds = new();
    private IDataQueueHandler? _handler;
    private AggregationManager? _aggregator;

    public LiveDataFanout(IMessagePublisher publisher, LiveFanoutOptions options, ILogger<LiveDataFanout> logger)
    {
        _publisher = publisher;
        _options = options;
        _logger = logger;
    }

    /// <summary>Envelope <c>job-id</c> and body <c>feedId</c> of everything this process publishes.</summary>
    public Guid FeedId { get; } = Guid.NewGuid();

    public long Published { get; private set; }

    public IReadOnlyList<Symbol> Symbols => _feeds.Select(f => f.Symbol).ToList();

    public static Symbol ParseSymbol(string value)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !ContractMapping.TryParse<ContractSecurityType>(parts[0], out var securityType))
        {
            throw new ArgumentException($"Live symbol '{value}' must be <SecurityType>/<market>/<TICKER>, e.g. Equity/usa/SPY");
        }
        return ContractMapping.CreateSymbol(securityType.ToLean(), parts[1], parts[2]);
    }

    public void Start()
    {
        _ = LeanComposition.Providers;
        _aggregator = new AggregationManager();
        _aggregator.Initialize(new DataAggregatorInitializeParameters { AlgorithmSettings = new AlgorithmSettings() });
        _handler = _options.DataQueueHandler == nameof(FakeDataQueue)
            ? new FakeDataQueue(_aggregator, Math.Max(1, _options.DataPointsPerSecond))
            : Composer.Instance.GetExportedValueByTypeName<IDataQueueHandler>(_options.DataQueueHandler);

        var marketHours = MarketHoursDatabase.FromDataFolder();
        foreach (var symbol in _options.Symbols.Select(ParseSymbol).Distinct())
        {
            var entry = marketHours.GetEntry(symbol.ID.Market, symbol, symbol.SecurityType);
            var enumerators = new List<IEnumerator<BaseData>>();
            foreach (var tickType in SubscriptionManager.DefaultDataTypes()[symbol.SecurityType].Where(t => t != TickType.OpenInterest))
            {
                var type = LeanData.GetDataType(_options.Resolution, tickType);
                var config = new SubscriptionDataConfig(type, symbol, _options.Resolution, entry.DataTimeZone, entry.ExchangeHours.TimeZone,
                    fillForward: false, extendedHours: true, isInternalFeed: false, tickType: tickType, dataNormalizationMode: DataNormalizationMode.Raw);
                var enumerator = _handler.Subscribe(config, (_, _) => { });
                if (enumerator != null) enumerators.Add(enumerator);
            }
            _feeds.Add(new Feed(symbol, new DateTimeZoneInfo(entry.ExchangeHours.TimeZone), enumerators));
        }
        _logger.LogInformation("Live fan-out {FeedId}: {Handler} -> {Exchange} for {Symbols} at {Resolution}",
            FeedId, _handler.GetType().Name, BusExchanges.MarketDataLive, string.Join(", ", _feeds.Select(f => f.Symbol.Value)), _options.Resolution);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _options.Symbols.Count == 0)
        {
            _logger.LogInformation("Live fan-out disabled (no MarketData__Live__Symbols)");
            return;
        }
        try
        {
            Start();
        }
        catch (Exception e)
        {
            // The catalog, history and fetch APIs keep serving without the live feed.
            _logger.LogError(e, "Live fan-out could not start");
            return;
        }
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, _options.FlushIntervalMilliseconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Live fan-out publish failed; continuing");
            }
        }
    }

    /// <summary>Drains every subscription and publishes one batch per symbol (chunked to <see cref="LiveFanoutOptions.MaxPointsPerMessage"/>).</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        var messages = 0;
        foreach (var feed in _feeds)
        {
            var points = new List<LiveDataPoint>();
            foreach (var enumerator in feed.Enumerators)
            {
                while (enumerator.MoveNext() && enumerator.Current != null)
                {
                    points.Add(ToPoint(enumerator.Current, feed.Zone.Exchange));
                }
            }
            foreach (var chunk in points.OrderBy(p => p.Time).Chunk(Math.Max(1, _options.MaxPointsPerMessage)))
            {
                var sequence = feed.Sequence++;
                var body = new LiveDataBatch
                {
                    FeedId = FeedId, Sequence = sequence, Points = chunk.ToList(),
                    Symbol = new LiveDataSymbol
                    {
                        SecurityType = feed.Symbol.SecurityType.ToContract(), Market = feed.Symbol.ID.Market, Ticker = feed.Symbol.Value, Id = feed.Symbol.ID.ToString(),
                    },
                };
                var envelope = MessageEnvelope.Create(MessageTypes.LiveData, FeedId, 1, AppIds.MarketData, JobKind.MarketData, sequence: sequence);
                var routingKey = RoutingKeys.MarketData(feed.Symbol.SecurityType.ToString(), feed.Symbol.ID.Market, feed.Symbol.Value);
                await _publisher.PublishAsync(OutgoingMessage.Create(BusExchanges.MarketDataLive, routingKey, envelope, body) with { Mandatory = false }, cancellationToken)
                    .ConfigureAwait(false);
                messages++;
                Published += chunk.Length;
            }
        }
        return messages;
    }

    public static LiveDataPoint ToPoint(BaseData data, NodaTime.DateTimeZone exchangeTimeZone)
    {
        DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time.ConvertToUtc(exchangeTimeZone), DateTimeKind.Utc));
        return data switch
        {
            Tick { TickType: TickType.Quote } q => new LiveDataPoint
            {
                DataType = LiveDataType.Tick, TickType = ContractTickType.Quote, Time = Utc(q.Time), Value = q.Value,
                BidPrice = q.BidPrice, AskPrice = q.AskPrice, BidSize = q.BidSize, AskSize = q.AskSize,
            },
            Tick t => new LiveDataPoint
            {
                DataType = LiveDataType.Tick, TickType = t.TickType.ToContract(), Time = Utc(t.Time), Value = t.Value, Volume = t.Quantity,
            },
            TradeBar b => new LiveDataPoint
            {
                DataType = LiveDataType.TradeBar, TickType = ContractTickType.Trade, Time = Utc(b.Time), EndTime = Utc(b.EndTime), Value = b.Close,
                Open = b.Open, High = b.High, Low = b.Low, Close = b.Close, Volume = b.Volume,
            },
            QuoteBar b => new LiveDataPoint
            {
                DataType = LiveDataType.QuoteBar, TickType = ContractTickType.Quote, Time = Utc(b.Time), EndTime = Utc(b.EndTime), Value = b.Close,
                Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                BidPrice = b.Bid?.Close, AskPrice = b.Ask?.Close, BidSize = b.LastBidSize, AskSize = b.LastAskSize,
            },
            _ => new LiveDataPoint { DataType = LiveDataType.Tick, Time = Utc(data.Time), Value = data.Value },
        };
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _handler, null)?.DisposeSafely();
        Interlocked.Exchange(ref _aggregator, null)?.DisposeSafely();
        base.Dispose();
    }
}
