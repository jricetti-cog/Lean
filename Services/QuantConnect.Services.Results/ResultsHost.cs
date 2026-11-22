using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Defaults;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Results.Storage;

namespace QuantConnect.Services.Results;

using HttpResults = Microsoft.AspNetCore.Http.Results;

/// <summary>Service registration and the results API routes (<c>results.openapi.yaml</c>).</summary>
public static class ResultsHost
{
    public const string BasePath = "/internal/v1/results";

    /// <summary>Registers the results service. Tests pass their own object store / consumer wiring.</summary>
    public static WebApplicationBuilder AddResultsService(this WebApplicationBuilder builder, IResultObjectStore? objectStore = null, bool useRabbitMq = true)
    {
        builder.AddLeanServiceDefaults();
        var settings = ResultsSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
        builder.Services.AddSingleton<ResultsDatabase>();
        if (objectStore != null)
        {
            builder.Services.AddSingleton(objectStore);
        }
        else
        {
            builder.Services.AddSingleton<IResultObjectStore>(_ => S3ResultObjectStore.FromConfiguration(builder.Configuration));
        }
        builder.Services.AddSingleton<ResultsIngestor>();
        builder.Services.AddSingleton<ResultsQueries>();
        builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready]);
        if (useRabbitMq)
        {
            builder.Services.AddLeanRabbitMq(builder.Configuration);
        }
        if (settings.Consume)
        {
            builder.Services.AddHostedService<ResultsConsumer>();
        }
        return builder;
    }

    public static IEndpointRouteBuilder MapResultsApi(this IEndpointRouteBuilder app)
    {
        var results = app.MapGroup(BasePath).WithTags("results");

        results.MapGet("/", async (ResultsQueries queries, [FromQuery] Guid? parentJobId, [FromQuery] Guid[]? jobId, string? cursor, int? limit, CancellationToken ct) =>
            await Guarded(async () => Json(await queries.ListAsync(parentJobId, jobId, cursor, limit, ct).ConfigureAwait(false))).ConfigureAwait(false))
            .WithName("listResults");

        results.MapGet("/{id:guid}", (ResultsQueries queries, Guid id, int? attempt, CancellationToken ct) =>
            ForAttempt(queries, id, attempt, ct, async a => await queries.GetAsync(id, a, ct).ConfigureAwait(false) is { } summary ? Json(summary) : NotFound(id)))
            .WithName("getResult");

        results.MapGet("/{id:guid}/charts", (ResultsQueries queries, Guid id, int? attempt, CancellationToken ct) =>
            ForAttempt(queries, id, attempt, ct, async a => Json(await queries.ChartsAsync(id, a, ct).ConfigureAwait(false))))
            .WithName("listCharts");

        results.MapGet("/{id:guid}/charts/{chart}", (ResultsQueries queries, Guid id, string chart, int? attempt, int? maxPoints, CancellationToken ct) =>
            maxPoints is < 10 or > 100_000
                ? Task.FromResult(BadRequest("maxPoints must be between 10 and 100000"))
                : ForAttempt(queries, id, attempt, ct, async a => await queries.ChartAsync(id, a, chart, maxPoints, ct).ConfigureAwait(false) is { } body
                    ? Json(body)
                    : HttpResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Chart not found", detail: $"Job {id:D} attempt {a} has no chart '{chart}'")))
            .WithName("getChart");

        results.MapGet("/{id:guid}/orders", (ResultsQueries queries, Guid id, int? attempt, string? cursor, int? limit, CancellationToken ct) =>
            ForAttempt(queries, id, attempt, ct, async a => Json(await queries.OrdersAsync(id, a, cursor, limit, ct).ConfigureAwait(false))))
            .WithName("listOrders");

        results.MapGet("/{id:guid}/order-events", (ResultsQueries queries, Guid id, int? attempt, string? cursor, int? limit, CancellationToken ct) =>
            ForAttempt(queries, id, attempt, ct, async a => Json(await queries.OrderEventsAsync(id, a, cursor, limit, ct).ConfigureAwait(false))))
            .WithName("listOrderEvents");

        results.MapGet("/{id:guid}/logs", (ResultsQueries queries, Guid id, int? attempt, string? level, string? cursor, int? limit, CancellationToken ct) =>
            level is not (null or "Log" or "Debug" or "SystemDebug" or "RuntimeError" or "HandledError")
                ? Task.FromResult(BadRequest($"Unknown level '{level}'"))
                : ForAttempt(queries, id, attempt, ct, async a => Json(await queries.LogsAsync(id, a, level, cursor, limit, ct).ConfigureAwait(false))))
            .WithName("listLogs");

        results.MapGet("/{id:guid}/artifacts", (ResultsQueries queries, Guid id, int? attempt, CancellationToken ct) =>
            ForAttempt(queries, id, attempt, ct, async a => Json(await queries.ArtifactsAsync(id, a, ct).ConfigureAwait(false))))
            .WithName("listArtifacts");

        results.MapGet("/{id:guid}/live-state", (ResultsQueries queries, Guid id, CancellationToken ct) =>
            Guarded(async () => await queries.LiveStateAsync(id, ct).ConfigureAwait(false) is { } state ? Json(state) : NotFound(id)))
            .WithName("getLiveState");

        return app;
    }

    private static async Task<IResult> ForAttempt(ResultsQueries queries, Guid id, int? attempt, CancellationToken ct, Func<int, Task<IResult>> handler)
    {
        if (attempt is < 1)
        {
            return BadRequest("attempt must be >= 1");
        }
        return await Guarded(async () => await queries.ResolveAttemptAsync(id, attempt, ct).ConfigureAwait(false) is { } resolved
            ? await handler(resolved).ConfigureAwait(false)
            : NotFound(id)).ConfigureAwait(false);
    }

    /// <summary>Malformed cursors are client errors.</summary>
    private static async Task<IResult> Guarded(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler().ConfigureAwait(false);
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException)
        {
            return BadRequest("Invalid cursor");
        }
    }

    private static IResult Json<T>(T value) => HttpResults.Json(value, ContractJson.Options);

    private static IResult NotFound(Guid id) =>
        HttpResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Result not found", detail: $"No result for job {id:D}");

    private static IResult BadRequest(string detail) =>
        HttpResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);
}
