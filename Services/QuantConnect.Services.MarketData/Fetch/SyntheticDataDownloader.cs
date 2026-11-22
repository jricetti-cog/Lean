using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;

namespace QuantConnect.Services.MarketData.Fetch;

/// <summary>
/// <see cref="IDataDownloader"/> producing a deterministic random walk (seeded by the ticker) during regular market
/// hours, so <c>generate-synthetic</c> tasks run through the same <c>DownloaderDataProvider</c> write path
/// (<c>Program.RunDownload</c> → <c>LeanDataWriter</c>) as a vendor download. Times are in the exchange time zone,
/// like every LEAN downloader. Only used for synthetic tickers: real symbols are rejected before this runs.
/// </summary>
public sealed class SyntheticDataDownloader : IDataDownloader
{
    private readonly MarketHoursDatabase _marketHours = MarketHoursDatabase.FromDataFolder();

    public IEnumerable<BaseData> Get(DataDownloaderGetParameters parameters)
    {
        var symbol = parameters.Symbol;
        var tickType = parameters.TickType;
        if (tickType == TickType.OpenInterest) return Enumerable.Empty<BaseData>();
        var hours = _marketHours.GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType);
        var start = parameters.StartUtc.ConvertFromUtc(hours.TimeZone);
        var end = parameters.EndUtc.ConvertFromUtc(hours.TimeZone);
        return Generate(symbol, parameters.Resolution, tickType, hours, start, end);
    }

    private static IEnumerable<BaseData> Generate(Symbol symbol, Resolution resolution, TickType tickType, SecurityExchangeHours hours, DateTime start, DateTime end)
    {
        var random = new Random(StableSeed(symbol.Value));
        var price = 20m + random.Next(0, 18000) / 100m;
        var step = resolution == Resolution.Tick ? TimeSpan.FromSeconds(10) : resolution.ToTimeSpan();
        var volatility = (decimal)Math.Sqrt(step.TotalDays / 252) * 0.25m;

        foreach (var day in Time.EachTradeableDay(hours, start.Date, end.Date))
        {
            if (resolution == Resolution.Daily)
            {
                if (day < start || day >= end) continue;
                yield return Next(symbol, tickType, day, step, ref price, volatility, random);
                continue;
            }
            for (var time = day; time < day.AddDays(1); time += step)
            {
                if (time < start || time + step > end || !hours.IsOpen(time, time + step, extendedMarketHours: false)) continue;
                if (resolution == Resolution.Tick)
                {
                    price = Walk(price, volatility, random);
                    yield return tickType == TickType.Trade
                        ? new Tick { Symbol = symbol, Time = time, Value = price, Quantity = random.Next(1, 50) * 100, TickType = TickType.Trade }
                        : new Tick(time, symbol, string.Empty, string.Empty, bidSize: random.Next(1, 20) * 100, bidPrice: price - 0.01m,
                            askPrice: price + 0.01m, askSize: random.Next(1, 20) * 100);
                    continue;
                }
                yield return Next(symbol, tickType, time, step, ref price, volatility, random);
            }
        }
    }

    private static BaseData Next(Symbol symbol, TickType tickType, DateTime time, TimeSpan period, ref decimal price, decimal volatility, Random random)
    {
        var open = price;
        var close = Walk(open, volatility, random);
        var high = Math.Max(open, close) * (1 + (decimal)random.NextDouble() * volatility / 2);
        var low = Math.Min(open, close) * (1 - (decimal)random.NextDouble() * volatility / 2);
        price = close;
        if (tickType == TickType.Trade)
        {
            return new TradeBar(time, symbol, Round(open), Round(high), Round(low), Round(close), random.Next(10, 5000) * 100, period);
        }
        var spread = Math.Max(0.01m, Round(close * 0.0002m));
        return new QuoteBar(time, symbol,
            new Bar(Round(open) - spread, Round(high) - spread, Round(low) - spread, Round(close) - spread), random.Next(1, 50) * 100,
            new Bar(Round(open) + spread, Round(high) + spread, Round(low) + spread, Round(close) + spread), random.Next(1, 50) * 100, period);
    }

    private static decimal Walk(decimal price, decimal volatility, Random random)
    {
        var gaussian = (decimal)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));
        return Math.Max(1m, price * (1 + volatility * gaussian));
    }

    private static decimal Round(decimal value) => Math.Round(value, 2);

    /// <summary>FNV-1a over the ticker: the same ticker always generates the same series.</summary>
    private static int StableSeed(string ticker)
    {
        var hash = 2166136261u;
        foreach (var c in ticker.ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }
        return (int)(hash & 0x7fffffff);
    }
}
