/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System.Collections.Concurrent;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Util;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>
/// <see cref="IDataQueueHandler"/> fed by the market-data service's fan-out on <c>lean.marketdata.live</c> (ADR 0004 decision 5),
/// selected with <c>dataQueueHandlers: ["bus"]</c> in a live job. Each subscribed symbol gets one transient queue bound to
/// <c>md.&lt;securityType&gt;.&lt;market&gt;.&lt;ticker&gt;.v1</c>; every point becomes a LEAN tick (bars as their closing trade/quote)
/// pushed into the engine's <see cref="IDataAggregator"/>, which consolidates it to each subscription's resolution, the same
/// path a brokerage data queue takes.
/// </summary>
public class BusDataQueueHandler : IDataQueueHandler
{
    /// <summary>Name accepted in <c>LiveSpec.dataQueueHandlers</c>.</summary>
    public const string Alias = "bus";

    private static readonly TimeSpan MessageTtl = TimeSpan.FromSeconds(5);

    private readonly IDataAggregator _aggregator;
    private readonly Func<ITransientSubscriber> _subscriber;
    private readonly ConcurrentDictionary<Symbol, Feed> _feeds = new();
    private readonly object _lock = new();
    private long _received;
    private bool _disposed;

    public BusDataQueueHandler()
        : this(Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
            Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager")), () => EngineBus.Bus as ITransientSubscriber
                ?? throw new InvalidOperationException("The engine bus does not support transient subscriptions"))
    {
    }

    public BusDataQueueHandler(IDataAggregator aggregator, Func<ITransientSubscriber> subscriber)
    {
        _aggregator = aggregator;
        _subscriber = subscriber;
    }

    /// <summary>Points pushed into the aggregator so far.</summary>
    public long Received => Interlocked.Read(ref _received);

    public bool IsConnected => !_disposed;

    public void SetJob(LiveNodePacket job)
    {
    }

    public IEnumerator<BaseData>? Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
    {
        if (!CanSubscribe(dataConfig.Symbol))
        {
            return null;
        }
        var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
        lock (_lock)
        {
            var feed = _feeds.GetOrAdd(dataConfig.Symbol, symbol => new Feed(symbol, dataConfig.ExchangeTimeZone));
            feed.Configs++;
            if (feed.Subscription == null)
            {
                var key = RoutingKeys.MarketData(dataConfig.Symbol.SecurityType.ToString(), dataConfig.Symbol.ID.Market, dataConfig.Symbol.Value);
                feed.Subscription = _subscriber().SubscribeTransientAsync(BusExchanges.MarketDataLive, new[] { key },
                    (message, _) => Task.FromResult(OnMessage(feed, message)), new TransientQueueOptions { Prefetch = 200, MessageTtl = MessageTtl })
                    .GetAwaiter().GetResult();
                Log.Trace($"BusDataQueueHandler.Subscribe(): {dataConfig.Symbol} <- {BusExchanges.MarketDataLive} {key}");
            }
        }
        return enumerator;
    }

    public void Unsubscribe(SubscriptionDataConfig dataConfig)
    {
        _aggregator.Remove(dataConfig);
        IAsyncDisposable? stop = null;
        lock (_lock)
        {
            if (_feeds.TryGetValue(dataConfig.Symbol, out var feed) && --feed.Configs <= 0)
            {
                _feeds.TryRemove(dataConfig.Symbol, out _);
                stop = feed.Subscription;
            }
        }
        stop?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Canonical option/future chains and universes have no live feed on the bus.</summary>
    public static bool CanSubscribe(Symbol symbol) => !symbol.IsCanonical() && !symbol.Value.Contains("UNIVERSE", StringComparison.OrdinalIgnoreCase)
        && symbol.SecurityType != SecurityType.Base;

    private MessageDisposition OnMessage(Feed feed, ReceivedMessage message)
    {
        if (message.Envelope.Type != MessageTypes.LiveData)
        {
            return MessageDisposition.Ack;
        }
        LiveDataBatch batch;
        try
        {
            batch = ContractJson.Deserialize<LiveDataBatch>(message.Body.Span);
        }
        catch (System.Text.Json.JsonException e)
        {
            Log.Error(e, "BusDataQueueHandler: invalid live data batch dropped");
            return MessageDisposition.Ack;
        }
        if (feed.LastSequence is { } last && batch.FeedId == feed.FeedId && batch.Sequence > last + 1)
        {
            Log.Trace($"BusDataQueueHandler: {feed.Symbol} gap of {batch.Sequence - last - 1} batch(es) from feed {batch.FeedId:D}");
        }
        feed.FeedId = batch.FeedId;
        feed.LastSequence = batch.Sequence;
        foreach (var point in batch.Points)
        {
            if (ToTick(point, feed.Symbol, feed.ExchangeTimeZone) is { } tick)
            {
                _aggregator.Update(tick);
                Interlocked.Increment(ref _received);
            }
        }
        return MessageDisposition.Ack;
    }

    /// <summary>One fan-out point as a LEAN tick in exchange time: ticks as they are, bars by their closing trade or quote.</summary>
    public static Tick? ToTick(LiveDataPoint point, Symbol symbol, NodaTime.DateTimeZone exchangeTimeZone)
    {
        var time = (point.EndTime ?? point.Time).UtcDateTime.ConvertFromUtc(exchangeTimeZone);
        switch (point.DataType)
        {
            case LiveDataType.Tick when point.TickType == ContractTickType.Quote:
            case LiveDataType.QuoteBar:
                var bid = point.BidPrice ?? point.Value;
                var ask = point.AskPrice ?? point.Value;
                if (bid <= 0 && ask <= 0) return null;
                return new Tick(time, symbol, string.Empty, string.Empty, point.BidSize ?? 0, bid, point.AskSize ?? 0, ask);
            case LiveDataType.Tick when point.TickType == ContractTickType.OpenInterest:
                return new Tick { Time = time, Symbol = symbol, TickType = TickType.OpenInterest, Value = point.Value, Quantity = point.Value };
            default:
                var price = point.Close ?? point.Value;
                if (price <= 0) return null;
                return new Tick(time, symbol, string.Empty, string.Empty, point.Volume ?? 0, price);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var feed in _feeds.Values)
        {
            feed.Subscription?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _feeds.Clear();
    }

    private sealed class Feed(Symbol symbol, NodaTime.DateTimeZone exchangeTimeZone)
    {
        public Symbol Symbol { get; } = symbol;
        public NodaTime.DateTimeZone ExchangeTimeZone { get; } = exchangeTimeZone;
        public int Configs { get; set; }
        public IAsyncDisposable? Subscription { get; set; }
        public Guid? FeedId { get; set; }
        public long? LastSequence { get; set; }
    }
}
