using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Securities;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Util;
using Bar = QuantConnect.Services.Contracts.Http.Bar;

namespace QuantConnect.Services.MarketData;

/// <summary>
/// History over the volume through LEAN's own <see cref="SubscriptionDataReaderHistoryProvider"/> (ADR 0004): the same
/// readers, map/factor files and market hours a backtest uses, read from <see cref="Globals.DataFolder"/>.
/// A fresh zip cache per request, so files published by a task are visible to the next request.
/// </summary>
public sealed class HistoryService
{
    public const int MaxBars = 100_000;
    private readonly IDataProvider _dataProvider;
    private readonly IMapFileProvider _mapFileProvider;
    private readonly IFactorFileProvider _factorFileProvider;

    public HistoryService()
    {
        (_dataProvider, _mapFileProvider, _factorFileProvider) = LeanComposition.Providers;
    }

    /// <summary>Bars in time order, or null when more than <see cref="MaxBars"/> match.</summary>
    public List<Bar>? Get(SecurityType securityType, string market, string ticker, Resolution resolution, TickType tickType,
        DateOnly start, DateOnly end, DataNormalizationMode normalization = DataNormalizationMode.Raw)
    {
        var symbol = ContractMapping.CreateSymbol(securityType, market, ticker);
        var entry = MarketHoursDatabase.FromDataFolder().GetEntry(market, symbol, securityType);
        var exchangeTimeZone = entry.ExchangeHours.TimeZone;
        var startUtc = start.ToDateTime(TimeOnly.MinValue).ConvertToUtc(exchangeTimeZone);
        var endUtc = end.AddDays(1).ToDateTime(TimeOnly.MinValue).ConvertToUtc(exchangeTimeZone);
        var dataType = LeanData.GetDataType(resolution, tickType);

        var request = new HistoryRequest(startUtc, endUtc, dataType, symbol, resolution, entry.ExchangeHours, entry.DataTimeZone,
            fillForwardResolution: null, includeExtendedMarketHours: true, isCustomData: false, normalization, tickType);

        using var cache = new ZipDataCacheProvider(_dataProvider, isDataEphemeral: true);
        var provider = new SubscriptionDataReaderHistoryProvider();
        provider.Initialize(new HistoryProviderInitializeParameters(null, null, _dataProvider, cache, _mapFileProvider, _factorFileProvider,
            null, false, new DataPermissionManager(), null, new AlgorithmSettings()));

        var bars = new List<Bar>();
        foreach (var slice in provider.GetHistory(new[] { request }, exchangeTimeZone) ?? Enumerable.Empty<Slice>())
        {
            foreach (var data in slice.AllData)
            {
                if (data.Symbol != symbol || data.GetType() != dataType && !(dataType == typeof(Tick) && data is Tick)) continue;
                if (bars.Count == MaxBars) return null;
                bars.Add(ToBar(data, exchangeTimeZone));
            }
        }
        return bars;
    }

    public static Bar ToBar(BaseData data, NodaTime.DateTimeZone exchangeTimeZone)
    {
        var time = new DateTimeOffset(DateTime.SpecifyKind(data.Time.ConvertToUtc(exchangeTimeZone), DateTimeKind.Utc));
        return data switch
        {
            TradeBar t => new Bar { Time = time, Open = t.Open, High = t.High, Low = t.Low, Close = t.Close, Volume = t.Volume },
            QuoteBar q => new Bar { Time = time, Open = q.Open, High = q.High, Low = q.Low, Close = q.Close },
            Tick k => new Bar { Time = time, Open = k.Value, High = k.Value, Low = k.Value, Close = k.Value, Volume = k.TickType == TickType.Trade ? k.Quantity : null },
            _ => new Bar { Time = time, Open = data.Value, High = data.Value, Low = data.Value, Close = data.Value },
        };
    }
}
