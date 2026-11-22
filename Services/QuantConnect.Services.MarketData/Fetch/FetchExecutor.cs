using System.Text.RegularExpressions;
using QuantConnect.Data;
using QuantConnect.DownloaderDataProvider.Launcher;
using QuantConnect.DownloaderDataProvider.Launcher.Models;
using QuantConnect.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Util;
using DownloaderProgram = QuantConnect.DownloaderDataProvider.Launcher.Program;

namespace QuantConnect.Services.MarketData.Fetch;

/// <summary>A task failure; <see cref="Retryable"/> failures go back on the queue until the delivery limit.</summary>
public sealed class FetchFailedException : Exception
{
    public FetchFailedException(ErrorCode code, string message, bool retryable = false, Exception? inner = null) : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }

    public ErrorCode Code { get; }
    public bool Retryable { get; }
}

public sealed class MarketDataFetchOptions
{
    /// <summary><see cref="IDataDownloader"/> type name for <c>download</c> tasks that do not name one (no vendor connected by default).</summary>
    public string? Downloader { get; set; }

    /// <summary>Upstream folder (same layout as <c>Data/</c>) <c>fetch-files</c> tasks copy from.</summary>
    public string? SourceFolder { get; set; }

    /// <summary>Longest range of a synthetic tick/second task, in days.</summary>
    public int MaxSyntheticHighResolutionDays { get; set; } = 31;
}

/// <summary>
/// Runs one fetch task: writes into the task's staging directory with LEAN's own downloader path
/// (<see cref="DownloaderProgram.RunDownload"/>, i.e. <c>LeanDataWriter</c> zips, merged with the files already published) and then
/// publishes atomically (ADR 0004).
/// </summary>
public sealed partial class FetchExecutor
{
    private static readonly KeyStringSynchronizer DiskSynchronizer = new();
    private readonly LeanDataVolume _volume;
    private readonly IFetchTaskStore _store;
    private readonly MarketDataFetchOptions _options;
    private readonly Func<string, IDataDownloader> _resolveDownloader;

    public FetchExecutor(LeanDataVolume volume, IFetchTaskStore store, MarketDataFetchOptions options, Func<string, IDataDownloader>? resolveDownloader = null)
    {
        _volume = volume;
        _store = store;
        _options = options;
        _resolveDownloader = resolveDownloader ?? (name => Composer.Instance.GetExportedValueByTypeName<IDataDownloader>(name));
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._^-]{0,63}$")]
    private static partial Regex TickerPattern();

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex MarketPattern();

    /// <summary>Request validation shared by the API (400) and the runner (final failure).</summary>
    public static string? Validate(MarketDataFetchSpec spec, MarketDataFetchOptions? options = null)
    {
        if (!MarketPattern().IsMatch(spec.Market.ToLowerInvariant())) return "fetch.market must match ^[a-z0-9]+$";
        if (spec.Mode == FetchMode.FetchFiles)
        {
            if (spec.Paths is not { Count: > 0 }) return "fetch-files needs fetch.paths";
            foreach (var path in spec.Paths)
            {
                var normalized = path.Replace('\\', '/');
                if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Split('/').Any(p => p is ".." or "." || p.StartsWith('.')) || Path.IsPathRooted(path))
                {
                    return $"fetch.paths entry '{path}' must be a relative path inside the data folder";
                }
            }
            return null;
        }
        if (spec.Tickers is not { Count: > 0 }) return $"{Mode(spec.Mode)} needs fetch.tickers";
        if (spec.Tickers.FirstOrDefault(t => !TickerPattern().IsMatch(t.Trim())) is { } bad) return $"invalid ticker '{bad}'";
        if (spec.StartDate is null || spec.EndDate is null) return $"{Mode(spec.Mode)} needs fetch.startDate and fetch.endDate";
        if (spec.StartDate > spec.EndDate) return "fetch.startDate is after fetch.endDate";
        if (spec.TickType == ContractTickType.OpenInterest && spec.Mode == FetchMode.GenerateSynthetic) return "synthetic data has no open interest";
        if (spec.Mode == FetchMode.GenerateSynthetic)
        {
            if (spec.SecurityType is not (ContractSecurityType.Equity or ContractSecurityType.Forex or ContractSecurityType.Crypto or ContractSecurityType.Cfd or ContractSecurityType.Index))
            {
                return "synthetic data supports Equity, Forex, Crypto, Cfd and Index";
            }
            var maxDays = (options ?? new MarketDataFetchOptions()).MaxSyntheticHighResolutionDays;
            if (spec.Resolution is ContractResolution.Tick or ContractResolution.Second && spec.EndDate.Value.DayNumber - spec.StartDate.Value.DayNumber + 1 > maxDays)
            {
                return $"synthetic {spec.Resolution} data is limited to {maxDays} days per task";
            }
        }
        return null;
    }

    private static string Mode(FetchMode mode) => ContractJson.Serialize(mode).Trim('"');

    /// <summary>Runs the task and returns the published paths (relative to the data folder).</summary>
    public async Task<IReadOnlyList<string>> ExecuteAsync(Guid taskId, MarketDataFetchSpec spec, CancellationToken cancellationToken)
    {
        if (Validate(spec, _options) is { } error) throw new FetchFailedException(ErrorCode.InvalidRequest, error);

        var staging = _volume.StagingFor(taskId);
        LeanDataVolume.TryDelete(staging);
        Directory.CreateDirectory(staging);
        try
        {
            switch (spec.Mode)
            {
                case FetchMode.FetchFiles:
                    CopyFromSource(spec, staging);
                    break;
                case FetchMode.GenerateSynthetic:
                    await EnsureSyntheticTickersAsync(spec, cancellationToken).ConfigureAwait(false);
                    await Task.Run(() => Download(new SyntheticDataDownloader(), spec, staging, mapSymbol: false), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    var name = spec.Downloader ?? _options.Downloader;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new FetchFailedException(ErrorCode.InvalidRequest,
                            "No data downloader configured: name one in fetch.downloader or MarketData__Fetch__Downloader (no data vendor is connected in this deployment)");
                    }
                    IDataDownloader downloader;
                    try
                    {
                        downloader = _resolveDownloader(name);
                    }
                    catch (Exception e)
                    {
                        throw new FetchFailedException(ErrorCode.InvalidRequest, $"Unknown data downloader '{name}': {e.Message}", inner: e);
                    }
                    var mapSymbol = spec.SecurityType is ContractSecurityType.Equity or ContractSecurityType.Option;
                    await Task.Run(() => Download(downloader, spec, staging, mapSymbol), cancellationToken).ConfigureAwait(false);
                    break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return _volume.Publish(staging);
        }
        catch (FetchFailedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new FetchFailedException(ErrorCode.Internal, e.Message, retryable: e is IOException, inner: e);
        }
        finally
        {
            LeanDataVolume.TryDelete(staging);
        }
    }

    private void Download(IDataDownloader downloader, MarketDataFetchSpec spec, string staging, bool mapSymbol)
    {
        var securityType = spec.SecurityType.ToLean();
        var resolution = spec.Resolution.ToLean();
        var tickType = spec.TickType?.ToLean() ?? ContractMapping.DefaultTickType(securityType);
        var symbols = spec.Tickers!.Select(t => ContractMapping.CreateSymbol(securityType, spec.Market, t)).ToList();
        var start = spec.StartDate!.Value.ToDateTime(TimeOnly.MinValue);
        var end = spec.EndDate!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);

        // Merge with what is already published: LeanDataWriter merges into existing files in its target directory.
        foreach (var symbol in symbols)
        {
            var dates = DataCatalog.IsDailyOrHour(resolution) ? new[] { start } : Time.EachDay(start, end.AddDays(-1)).ToArray();
            foreach (var date in dates)
            {
                _volume.CopyToStaging(staging, LeanData.GenerateRelativeZipFilePath(symbol, date, resolution, tickType));
            }
        }

        var config = new DataDownloadConfig(tickType, securityType, resolution, start, end, spec.Market.ToLowerInvariant(), symbols);
        Log.Trace($"FetchExecutor: {downloader.GetType().Name} {string.Join(",", symbols)} {resolution} {tickType} {start:yyyy-MM-dd}..{end:yyyy-MM-dd} -> {staging}");
        DownloaderProgram.RunDownload(downloader, config, staging, new DiskDataCacheProvider(DiskSynchronizer), mapSymbol);
    }

    /// <summary>Synthetic data never lands on a real symbol (e.g. never regenerates <c>equity/usa/daily/spy.zip</c>).</summary>
    private async Task EnsureSyntheticTickersAsync(MarketDataFetchSpec spec, CancellationToken cancellationToken)
    {
        var securityType = spec.SecurityType.ToString().ToLowerInvariant();
        var market = spec.Market.ToLowerInvariant();
        foreach (var ticker in spec.Tickers!)
        {
            if (await _store.IsSyntheticAsync(securityType, market, ticker, cancellationToken).ConfigureAwait(false)) continue;
            if (HasRealData(securityType, market, ticker.ToLowerInvariant()))
            {
                throw new FetchFailedException(ErrorCode.InvalidRequest,
                    $"{ticker} has real data on lean-data ({securityType}/{market}); synthetic data must use a synthetic ticker");
            }
            await _store.MarkSyntheticAsync(securityType, market, ticker, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool HasRealData(string securityType, string market, string ticker)
    {
        var marketDir = Path.Combine(_volume.Root, securityType, market);
        if (!Directory.Exists(marketDir)) return false;
        if (File.Exists(Path.Combine(marketDir, "map_files", ticker + ".csv")) || File.Exists(Path.Combine(marketDir, "factor_files", ticker + ".csv"))) return true;
        foreach (var resolution in Enum.GetValues<Resolution>())
        {
            var dir = Path.Combine(marketDir, DataCatalog.ResolutionDirectory(resolution));
            if (!Directory.Exists(dir)) continue;
            if (Directory.Exists(Path.Combine(dir, ticker)) || File.Exists(Path.Combine(dir, ticker + ".zip"))
                || Directory.EnumerateFiles(dir, ticker + "_*.zip").Any())
            {
                return true;
            }
        }
        return false;
    }

    private void CopyFromSource(MarketDataFetchSpec spec, string staging)
    {
        if (string.IsNullOrWhiteSpace(_options.SourceFolder) || !Directory.Exists(_options.SourceFolder))
        {
            throw new FetchFailedException(ErrorCode.InvalidRequest, "No upstream data source configured for fetch-files (MarketData__Fetch__SourceFolder)");
        }
        var root = Path.GetFullPath(_options.SourceFolder);
        foreach (var path in spec.Paths!)
        {
            var source = Path.GetFullPath(Path.Combine(root, path));
            if (!source.StartsWith(root, StringComparison.Ordinal) || !File.Exists(source))
            {
                throw new FetchFailedException(ErrorCode.DataMissing, $"{path} not found upstream");
            }
            var target = Path.Combine(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }
}
