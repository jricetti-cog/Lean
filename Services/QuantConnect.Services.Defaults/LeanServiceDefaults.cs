using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using HealthStatus = QuantConnect.Services.Contracts.Http.HealthStatus;

namespace QuantConnect.Services.Defaults;

/// <summary>
/// The defaults every LEAN service host applies with one call:
/// <list type="bullet">
/// <item>configuration from environment variables (already layered by the host builder; <c>Service__*</c>, <c>RabbitMQ__*</c>, ... and <c>LEAN_*</c> onto LEAN <c>Config</c> when <see cref="LeanServiceDefaultsOptions.HostsLeanCode"/>);</item>
/// <item><c>GET /healthz</c> (liveness) and <c>GET /readyz</c> (checks tagged <see cref="HealthTags.Ready"/>) with the <see cref="Health"/> contract body;</item>
/// <item>OpenTelemetry traces and metrics (ASP.NET Core, HttpClient, runtime, <c>QuantConnect.Services*</c> sources/meters), OTLP export when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set;</item>
/// <item>JSON console logging in UTC;</item>
/// <item>graceful shutdown: readiness goes down first, an optional drain delay (<c>Service__DrainSeconds</c>), then hosted services stop within <c>Service__ShutdownTimeoutSeconds</c> (default 30).</item>
/// </list>
/// </summary>
public static class LeanServiceDefaults
{
    public const string HealthPath = "/healthz";
    public const string ReadyPath = "/readyz";
    /// <summary>Prefix for service <see cref="ActivitySource"/>s and meters picked up automatically.</summary>
    public const string TelemetryPrefix = "QuantConnect.Services";
    public const int DefaultShutdownTimeoutSeconds = 30;

    public static TBuilder AddLeanServiceDefaults<TBuilder>(this TBuilder builder, Action<LeanServiceDefaultsOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new LeanServiceDefaultsOptions();
        configure?.Invoke(options);
        var configuration = builder.Configuration;

        if (options.HostsLeanCode)
        {
            LeanConfigEnvironment.Apply();
        }

        var serviceName = configuration["OTEL_SERVICE_NAME"] ?? configuration["Service:Name"] ?? builder.Environment.ApplicationName;

        // JSON console logging
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
            o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
        });

        // OpenTelemetry
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(LeanServiceDefaults).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(o => o.Filter = context => !IsProbe(context.Request.Path))
                    .AddHttpClientInstrumentation()
                    .AddSource(TelemetryPrefix + "*");
                options.ConfigureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(TelemetryPrefix + "*");
                options.ConfigureMetrics?.Invoke(metrics);
            });
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        // Health and readiness
        builder.Services.AddSingleton<ReadinessGate>();
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Live })
            .AddCheck<ReadinessGate>("accepting-work", tags: new[] { HealthTags.Ready });
        builder.Services.AddSingleton<IStartupFilter, HealthEndpointsStartupFilter>();

        // Graceful shutdown
        var shutdownSeconds = configuration.GetValue("Service:ShutdownTimeoutSeconds", DefaultShutdownTimeoutSeconds);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(shutdownSeconds));
        builder.Services.AddSingleton(new DrainOptions(TimeSpan.FromSeconds(configuration.GetValue("Service:DrainSeconds", 0))));
        builder.Services.AddHostedService<GracefulShutdownService>();

        return builder;
    }

    private static bool IsProbe(PathString path) => path.StartsWithSegments(HealthPath) || path.StartsWithSegments(ReadyPath);

    internal static Task WriteHealthAsync(HttpContext context, HealthReport report)
    {
        var body = new Health
        {
            Status = ToContract(report.Status),
            Checks = report.Entries.Count == 0 ? null : report.Entries.ToDictionary(e => e.Key, e => ToContract(e.Value.Status)),
        };
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(ContractJson.Serialize(body));
    }

    private static HealthStatus ToContract(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus status) => status switch
    {
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy => HealthStatus.Ok,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded => HealthStatus.Degraded,
        _ => HealthStatus.Down,
    };

    private sealed class HealthEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseHealthChecks(HealthPath, Probe(HealthTags.Live));
            app.UseHealthChecks(ReadyPath, Probe(HealthTags.Ready));
            next(app);
        };

        private static HealthCheckOptions Probe(string tag) => new()
        {
            Predicate = check => check.Tags.Contains(tag),
            ResponseWriter = WriteHealthAsync,
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        };
    }
}

internal sealed record DrainOptions(TimeSpan Delay);

/// <summary>Readiness check that reports down once the host starts stopping, so traffic and work drain away first.</summary>
public sealed class ReadinessGate : IHealthCheck
{
    private volatile bool _draining;

    public bool IsDraining => _draining;

    public void BeginDrain() => _draining = true;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_draining ? HealthCheckResult.Unhealthy("Shutting down") : HealthCheckResult.Healthy());
}

/// <summary>Runs before any hosted service stops: fail readiness, then wait the configured drain delay.</summary>
internal sealed class GracefulShutdownService : IHostedLifecycleService
{
    private readonly ReadinessGate _gate;
    private readonly DrainOptions _drain;
    private readonly ILogger<GracefulShutdownService> _logger;

    public GracefulShutdownService(ReadinessGate gate, DrainOptions drain, ILogger<GracefulShutdownService> logger)
    {
        _gate = gate;
        _drain = drain;
        _logger = logger;
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _gate.BeginDrain();
        _logger.LogInformation("Shutdown requested: readiness down, draining for {DrainSeconds}s", _drain.Delay.TotalSeconds);
        if (_drain.Delay > TimeSpan.Zero)
        {
            await Task.Delay(_drain.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
