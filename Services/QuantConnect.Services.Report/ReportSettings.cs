using Microsoft.Extensions.Configuration;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Report;

/// <summary>Report service settings (<c>Report:*</c>, <c>S3:*</c>, <c>LEAN_DATA_FOLDER</c>).</summary>
public sealed record ReportSettings
{
    /// <summary>Directory holding the built <c>QuantConnect.Report.dll</c>, <c>template.html</c>, <c>css/</c> and <c>ReportCharts.py</c>.</summary>
    public string ReportDirectory { get; init; } = "/Lean/Report/bin/Release/";
    /// <summary>Absolute LEAN data folder (the read-only <c>lean-data</c> mount): <c>PortfolioLooperAlgorithm</c> reads <c>market-hours-database.json</c> from it.</summary>
    public string DataFolder { get; init; } = "/Lean/Data/";
    /// <summary>Scratch space; each attempt renders in <c>&lt;WorkDirectory&gt;/&lt;jobId&gt;/&lt;attempt&gt;/</c>, deleted afterwards.</summary>
    public string WorkDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "lean-report");
    public string DotnetPath { get; init; } = "dotnet";
    public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Results API base URL, used to resolve a live job's result when the request names <c>liveJobId</c> without <c>liveResult</c>.</summary>
    public Uri? ResultsApiUrl { get; init; }
    public bool Consume { get; init; } = true;
    public string ReportsBucket { get; init; } = Buckets.Reports;
    /// <summary>Attempts after which a retryable failure is reported as <c>final</c>.</summary>
    public int MaxAttempts { get; init; } = 3;

    public static ReportSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Report");
        var dataFolder = section["DataFolder"] ?? configuration["LEAN_DATA_FOLDER"] ?? "/Lean/Data/";
        if (!Path.IsPathRooted(dataFolder))
        {
            throw new InvalidOperationException($"Report:DataFolder / LEAN_DATA_FOLDER must be absolute (got '{dataFolder}')");
        }
        return new ReportSettings
        {
            ReportDirectory = section["Directory"] is { Length: > 0 } dir ? dir : "/Lean/Report/bin/Release/",
            DataFolder = dataFolder,
            WorkDirectory = section["WorkDirectory"] is { Length: > 0 } work ? work : Path.Combine(Path.GetTempPath(), "lean-report"),
            DotnetPath = section["DotnetPath"] is { Length: > 0 } dotnet ? dotnet : "dotnet",
            RenderTimeout = TimeSpan.FromSeconds(section.GetValue("RenderTimeoutSeconds", 600)),
            ResultsApiUrl = section["ResultsApiUrl"] is { Length: > 0 } url ? new Uri(url) : null,
            Consume = section.GetValue("Consume", true),
            ReportsBucket = configuration["S3:ReportsBucket"] is { Length: > 0 } bucket ? bucket : Buckets.Reports,
            MaxAttempts = section.GetValue("MaxAttempts", 3),
        };
    }
}
