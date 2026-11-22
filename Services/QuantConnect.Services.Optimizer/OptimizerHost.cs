using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Configuration;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Defaults;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Results;
using BusQueues = QuantConnect.Services.Messaging.Queues;

namespace QuantConnect.Services.Optimizer;

using HttpResults = Microsoft.AspNetCore.Http.Results;

/// <summary>Service registration and the optimizer API routes (<c>optimizer.openapi.yaml</c>).</summary>
public static class OptimizerHost
{
    public const string BasePath = "/internal/v1/optimizations";
    public const string OrchestratorClient = "job-orchestrator";
    public const int MaxLimit = 500;

    /// <summary>Registers the optimizer. Tests pass their own orchestrator client, object store and bus wiring.</summary>
    public static WebApplicationBuilder AddOptimizerService(this WebApplicationBuilder builder, IOrchestratorJobs? jobs = null,
        IResultObjectStore? objectStore = null, bool useRabbitMq = true)
    {
        builder.AddLeanServiceDefaults();
        builder.Services.AddOpenApi();
        var settings = OptimizerSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        // LeanOptimizer reads its update interval from LEAN Config.
        Config.Set("optimization-update-interval", settings.UpdateIntervalSeconds.ToString(CultureInfo.InvariantCulture));

        if (objectStore != null)
        {
            builder.Services.AddSingleton(objectStore);
        }
        else
        {
            builder.Services.AddSingleton<IResultObjectStore>(_ => S3ResultObjectStore.FromConfiguration(builder.Configuration));
        }
        builder.Services.AddHttpClient(OrchestratorClient, c => c.BaseAddress = settings.OrchestratorUri);
        if (jobs != null)
        {
            builder.Services.AddSingleton(jobs);
        }
        else
        {
            builder.Services.AddHttpClient<IOrchestratorJobs, OrchestratorJobsClient>(c => c.BaseAddress = settings.OrchestratorUri);
            builder.Services.AddHealthChecks().AddCheck<OrchestratorHealthCheck>("job-orchestrator", tags: [HealthTags.Ready]);
        }
        builder.Services.AddSingleton<OptimizationCatalog>();
        builder.Services.AddSingleton<OptimizationRunner>();
        builder.Services.AddSingleton<ChildEventsHandler>();
        if (useRabbitMq)
        {
            builder.Services.AddLeanRabbitMq(builder.Configuration);
        }
        if (settings.Consume)
        {
            builder.Services.AddHostedService<OptimizerConsumers>();
        }
        return builder;
    }

    public static IEndpointRouteBuilder MapOptimizerApi(this IEndpointRouteBuilder app)
    {
        app.MapOpenApi();
        var optimizations = app.MapGroup(BasePath).WithTags("optimizations");

        optimizations.MapPost("/estimate", async (HttpRequest http, OptimizerSettings settings, CancellationToken ct) =>
        {
            OptimizationSpec? spec;
            try
            {
                spec = await JsonSerializer.DeserializeAsync<OptimizationSpec>(http.Body, ContractJson.Options, ct).ConfigureAwait(false);
            }
            catch (JsonException e)
            {
                return BadRequest($"invalid OptimizationSpec: {e.Message}");
            }
            if (spec == null) return BadRequest("the body must be an OptimizationSpec");
            try
            {
                return Json(new OptimizationEstimate { TotalBacktests = OptimizationPacketFactory.Estimate(spec, settings) });
            }
            catch (InvalidOptimizationException e)
            {
                return BadRequest(e.Message);
            }
        }).WithName("estimateOptimization");

        optimizations.MapGet("/{id:guid}", (OptimizationCatalog catalog, Guid id) =>
            catalog.Get(id) is { } state ? Json(state) : NotFound(id)).WithName("getOptimization");

        optimizations.MapGet("/{id:guid}/children", (OptimizationCatalog catalog, Guid id, string? status, string? cursor, int? limit) =>
        {
            if (limit is < 1 or > MaxLimit) return BadRequest($"limit must be between 1 and {MaxLimit}");
            JobStatus? statusFilter = null;
            if (status != null)
            {
                try
                {
                    statusFilter = ContractJson.Deserialize<JobStatus>(JsonSerializer.SerializeToUtf8Bytes(status));
                }
                catch (JsonException)
                {
                    return BadRequest($"unknown status '{status}'");
                }
            }
            var offset = 0;
            if (cursor != null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
            {
                return BadRequest("Invalid cursor");
            }
            if (catalog.Children(id) is not { } children) return NotFound(id);
            var filtered = statusFilter is { } s ? children.Where(c => c.Status == s).ToList() : children;
            var size = limit ?? 50;
            var page = filtered.Skip(offset).Take(size).ToList();
            var next = offset + page.Count < filtered.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
            return Json(new Page<ChildBacktest> { Items = page, Next = next });
        }).WithName("listOptimizationChildren");

        return app;
    }

    private static IResult Json<T>(T value) => HttpResults.Text(ContractJson.Serialize(value), "application/json");

    private static IResult NotFound(Guid id) =>
        HttpResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Optimization not found", detail: $"No optimization {id:D} on this optimizer");

    private static IResult BadRequest(string detail) =>
        HttpResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);
}

/// <summary><c>estimateOptimization</c> response.</summary>
public sealed record OptimizationEstimate
{
    public required int TotalBacktests { get; init; }
}

/// <summary>
/// Consumes <c>jobs.optimization</c> with one prefetch-1 subscription per <see cref="OptimizerSettings.MaxConcurrentOptimizations"/>
/// (a delivery is held until its optimization ends) and <c>optimizer.children</c> for the child backtests' status events.
/// </summary>
public sealed class OptimizerConsumers(IMessageConsumer consumer, OptimizationRunner runner, ChildEventsHandler children, OptimizerSettings settings,
    ILogger<OptimizerConsumers> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task> { ConsumeAsync(BusQueues.OptimizerChildren, children.HandleAsync, 32, stoppingToken) };
        for (var i = 0; i < settings.MaxConcurrentOptimizations; i++)
        {
            loops.Add(ConsumeAsync(BusQueues.Optimization, runner.HandleAsync, 1, stoppingToken));
        }
        return Task.WhenAll(loops);
    }

    private async Task ConsumeAsync(string queue, MessageHandler handler, ushort prefetch, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await consumer.ConsumeAsync(queue, handler, new ConsumerOptions { Prefetch = prefetch }, stoppingToken)
                    .ConfigureAwait(false);
                logger.LogInformation("Consuming {Queue} (prefetch {Prefetch})", queue, prefetch);
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Consumer on {Queue} stopped; resubscribing", queue);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Ready while job-orchestrator answers its liveness probe: every child backtest goes through it.</summary>
public sealed class OrchestratorHealthCheck(IHttpClientFactory http) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.CreateClient(OptimizerHost.OrchestratorClient).GetAsync(LeanServiceDefaults.HealthPath, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"job-orchestrator answered {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("job-orchestrator unreachable", e);
        }
    }
}
