using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Defaults;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Results;

namespace QuantConnect.Services.Report;

using HttpResults = Microsoft.AspNetCore.Http.Results;

/// <summary>Service registration and the report API routes (<c>report.openapi.yaml</c>).</summary>
public static class ReportHost
{
    public const string BasePath = "/internal/v1/reports";

    /// <summary>Registers the report service. Tests pass their own object store, renderer and bus wiring.</summary>
    public static WebApplicationBuilder AddReportService(this WebApplicationBuilder builder, IResultObjectStore? objectStore = null,
        IReportRenderer? renderer = null, bool useRabbitMq = true)
    {
        builder.AddLeanServiceDefaults(o => o.HostsLeanCode = true);
        var settings = ReportSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        if (objectStore != null)
        {
            builder.Services.AddSingleton(objectStore);
        }
        else
        {
            builder.Services.AddSingleton<IResultObjectStore>(_ => S3ResultObjectStore.FromConfiguration(builder.Configuration));
        }
        if (renderer != null)
        {
            builder.Services.AddSingleton(renderer);
        }
        else
        {
            builder.Services.AddSingleton<IReportRenderer, LeanReportRenderer>();
        }
        if (settings.ResultsApiUrl != null)
        {
            builder.Services.AddHttpClient<ResultsApiClient>(c => c.BaseAddress = settings.ResultsApiUrl);
        }
        builder.Services.AddSingleton<ReportCatalog>();
        builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ReportProcessor>(sp));
        builder.Services.AddHealthChecks().AddCheck<ReportEngineHealthCheck>("lean-report", tags: [HealthTags.Ready]);
        if (useRabbitMq)
        {
            builder.Services.AddLeanRabbitMq(builder.Configuration);
        }
        if (settings.Consume)
        {
            builder.Services.AddHostedService<ReportConsumer>();
        }
        return builder;
    }

    public static IEndpointRouteBuilder MapReportApi(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup(BasePath).WithTags("reports");

        reports.MapGet("/", async (ReportCatalog catalog, Guid? sourceJobId, string? cursor, int? limit, CancellationToken ct) =>
        {
            if (limit is < 1 or > ReportCatalog.MaxLimit)
            {
                return BadRequest($"limit must be between 1 and {ReportCatalog.MaxLimit}");
            }
            try
            {
                return Json(await catalog.ListAsync(sourceJobId, cursor, limit, ct).ConfigureAwait(false));
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                return BadRequest("Invalid cursor");
            }
        }).WithName("listReports");

        reports.MapGet("/{id:guid}", async (ReportCatalog catalog, Guid id, int? attempt, CancellationToken ct) =>
            attempt is < 1
                ? BadRequest("attempt must be >= 1")
                : await catalog.GetAsync(id, attempt, ct).ConfigureAwait(false) is { } report
                    ? Json(report)
                    : HttpResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Report not found", detail: $"No report for job {id:D}"))
            .WithName("getReport");

        return app;
    }

    private static IResult Json<T>(T value) => HttpResults.Json(value, ContractJson.Options);

    private static IResult BadRequest(string detail) =>
        HttpResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);
}

/// <summary>Ready when the LEAN report assembly is present; degraded when the data folder lacks the market-hours database.</summary>
public sealed class ReportEngineHealthCheck(ReportSettings settings, IReportRenderer renderer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (renderer is LeanReportRenderer && !File.Exists(Path.Combine(settings.ReportDirectory, LeanReportRenderer.ReportAssembly)))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"{LeanReportRenderer.ReportAssembly} not found in {settings.ReportDirectory}"));
        }
        var marketHours = Path.Combine(settings.DataFolder, "market-hours", "market-hours-database.json");
        return Task.FromResult(File.Exists(marketHours)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"{marketHours} not found; reports need the lean-data volume"));
    }
}
