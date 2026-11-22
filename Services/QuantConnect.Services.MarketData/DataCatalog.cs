using System.Globalization;
using System.IO.Compression;
using QuantConnect.Securities;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.MarketData;

/// <summary>
/// The data catalog, read straight from the volume layout: <c>&lt;securityType&gt;/&lt;market&gt;/&lt;resolution&gt;/&lt;ticker&gt;[_&lt;tickType&gt;].zip</c>
/// for daily/hour (one file, every date inside) and <c>.../&lt;ticker&gt;/&lt;yyyyMMdd&gt;_&lt;tickType&gt;.zip</c> for minute/second/tick.
/// The volume is the source of truth: files published by any task (or seeded) show up immediately.
/// </summary>
public sealed class DataCatalog
{
    public const int MaxCatalogEntries = 5000;
    private readonly LeanDataVolume _volume;

    public DataCatalog(LeanDataVolume volume) => _volume = volume;

    public static bool IsDailyOrHour(Resolution resolution) => resolution is Resolution.Daily or Resolution.Hour;

    public static string ResolutionDirectory(Resolution resolution) => resolution.ToString().ToLowerInvariant();

    public string ResolutionPath(SecurityType securityType, string market, Resolution resolution) =>
        Path.Combine(_volume.Root, securityType.ToString().ToLowerInvariant(), market.ToLowerInvariant(), ResolutionDirectory(resolution));

    public List<CatalogEntry> List(SecurityType? securityType, string? market, Resolution? resolution)
    {
        var entries = new List<CatalogEntry>();
        foreach (var st in securityType.HasValue ? new[] { securityType.Value } : Enum.GetValues<SecurityType>())
        {
            var stDir = Path.Combine(_volume.Root, st.ToString().ToLowerInvariant());
            if (!ContractMapping.TryParse<ContractSecurityType>(st.ToString(), out _) || !Directory.Exists(stDir)) continue;
            foreach (var marketDir in Directory.EnumerateDirectories(stDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var m = Path.GetFileName(marketDir);
                if (market != null && m != market) continue;
                foreach (var res in resolution.HasValue ? new[] { resolution.Value } : Enum.GetValues<Resolution>().Reverse())
                {
                    var resDir = Path.Combine(marketDir, ResolutionDirectory(res));
                    if (!Directory.Exists(resDir)) continue;
                    foreach (var group in Scan(resDir, res).GroupBy(f => f.Ticker).OrderBy(g => g.Key, StringComparer.Ordinal))
                    {
                        var dates = group.SelectMany(f => f.Dates ?? Array.Empty<DateOnly>()).ToList();
                        entries.Add(new CatalogEntry
                        {
                            SecurityType = st.ToString(), Market = m, Ticker = group.Key.ToUpperInvariant(), Resolution = res.ToString(),
                            TickTypes = group.Select(f => f.TickType).Distinct().OrderBy(t => t).Select(t => t.ToContract()).ToList(),
                            Files = group.Count(),
                            Start = dates.Count > 0 ? dates.Min() : null,
                            End = dates.Count > 0 ? dates.Max() : null,
                        });
                        if (entries.Count >= MaxCatalogEntries) return entries;
                    }
                }
            }
        }
        return entries;
    }

    /// <summary>Dates with data for one symbol/resolution/tick type, ascending.</summary>
    public SortedSet<DateOnly> AvailableDates(SecurityType securityType, string market, string ticker, Resolution resolution, TickType tickType)
    {
        var dates = new SortedSet<DateOnly>();
        var resDir = ResolutionPath(securityType, market, resolution);
        var lower = ticker.ToLowerInvariant();
        if (IsDailyOrHour(resolution))
        {
            foreach (var file in DailyFiles(resDir, lower, tickType))
            {
                dates.UnionWith(ReadDates(file));
            }
        }
        else
        {
            var dir = Path.Combine(resDir, lower);
            if (!Directory.Exists(dir)) return dates;
            foreach (var file in Directory.EnumerateFiles(dir, "*.zip"))
            {
                if (TryParseDailyFileName(Path.GetFileNameWithoutExtension(file), out var date, out var tt) && tt == tickType) dates.Add(date);
            }
        }
        return dates;
    }

    public Coverage Coverage(SecurityType securityType, string market, string ticker, Resolution resolution, TickType tickType, DateOnly start, DateOnly end)
    {
        var available = AvailableDates(securityType, market, ticker, resolution, tickType);
        var symbol = ContractMapping.CreateSymbol(securityType, market, ticker);
        var hours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(market, symbol, securityType);
        var tradeable = Time.EachTradeableDay(hours, start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue))
            .Select(DateOnly.FromDateTime).ToList();

        var inRange = available.GetViewBetween(start, end);
        var ranges = new List<DateRange>();
        DateOnly? runStart = null, runEnd = null;
        foreach (var day in tradeable.Union(inRange).OrderBy(d => d))
        {
            if (inRange.Contains(day))
            {
                runStart ??= day;
                runEnd = day;
            }
            else if (runStart.HasValue)
            {
                ranges.Add(new DateRange { Start = runStart.Value, End = runEnd!.Value });
                runStart = runEnd = null;
            }
        }
        if (runStart.HasValue) ranges.Add(new DateRange { Start = runStart.Value, End = runEnd!.Value });

        return new Coverage
        {
            SecurityType = securityType.ToString(), Market = market, Ticker = ticker.ToUpperInvariant(), Resolution = resolution.ToString(),
            Ranges = ranges,
            Missing = tradeable.Where(d => !inRange.Contains(d)).ToList(),
        };
    }

    private sealed record ScannedFile(string Ticker, TickType TickType, DateOnly[]? Dates);

    private static IEnumerable<ScannedFile> Scan(string resDir, Resolution resolution)
    {
        if (IsDailyOrHour(resolution))
        {
            foreach (var file in Directory.EnumerateFiles(resDir, "*.zip"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var tickType = TickType.Trade;
                var underscore = name.LastIndexOf('_');
                if (underscore > 0 && Enum.TryParse<TickType>(name[(underscore + 1)..], true, out var tt))
                {
                    tickType = tt;
                    name = name[..underscore];
                }
                var dates = ReadDates(file).ToArray();
                yield return new ScannedFile(name, tickType, dates.Length == 0 ? null : new[] { dates.Min(), dates.Max() });
            }
            yield break;
        }
        foreach (var dir in Directory.EnumerateDirectories(resDir))
        {
            var ticker = Path.GetFileName(dir);
            foreach (var file in Directory.EnumerateFiles(dir, "*.zip"))
            {
                if (TryParseDailyFileName(Path.GetFileNameWithoutExtension(file), out var date, out var tickType))
                {
                    yield return new ScannedFile(ticker, tickType, new[] { date });
                }
            }
        }
    }

    private static IEnumerable<string> DailyFiles(string resDir, string lowerTicker, TickType tickType)
    {
        var candidates = tickType == TickType.Trade
            ? new[] { $"{lowerTicker}.zip", $"{lowerTicker}_trade.zip" }
            : new[] { $"{lowerTicker}_{tickType.ToString().ToLowerInvariant()}.zip" };
        return candidates.Select(c => Path.Combine(resDir, c)).Where(File.Exists);
    }

    private static bool TryParseDailyFileName(string name, out DateOnly date, out TickType tickType)
    {
        date = default;
        tickType = TickType.Trade;
        var parts = name.Split('_');
        return parts.Length >= 2
            && DateOnly.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && Enum.TryParse(parts[1], true, out tickType);
    }

    /// <summary>Distinct dates (first CSV column, <c>yyyyMMdd[ HH:mm]</c>) in a daily/hour zip.</summary>
    public static IEnumerable<DateOnly> ReadDates(string zipFile)
    {
        var dates = new SortedSet<DateOnly>();
        try
        {
            using var archive = ZipFile.OpenRead(zipFile);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length >= 8 && DateOnly.TryParseExact(line.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        dates.Add(date);
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
        }
        return dates;
    }
}
