using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.EngineHandlers;
using QuantConnect.Services.MarketData.Fetch;
using QuantConnect.Services.MarketData.Live;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.MarketData;

/// <summary>
/// Wires market-data. Configuration (environment variables use <c>__</c>):
/// <c>ConnectionStrings:Marketdata</c> (PostgreSQL; in-memory store when unset or <c>MarketData:Store=memory</c>),
/// <c>RabbitMQ:Uri</c> (in-memory bus when unset or <c>MarketData:Bus=memory</c>), <c>S3:*</c> (task manifests; a local folder when unset),
/// <c>MarketData:Fetch:*</c>, <c>MarketData:Live:*</c>, <c>MarketData:RunTasks</c>. The volume is LEAN's <c>data-folder</c>
/// (<c>LEAN_DATA_FOLDER</c>, <c>/Lean/Data/</c> in the image).
/// </summary>
public static class MarketDataServiceExtensions
{
    public static WebApplicationBuilder AddMarketData(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;
        Globals.Reset();
        _ = LeanComposition.Providers;

        services.AddSingleton(new LeanDataVolume(configuration["MarketData:DataFolder"] ?? Globals.DataFolder));
        services.AddSingleton<DataCatalog>();
        services.AddSingleton<HistoryService>();

        var fetchOptions = configuration.GetSection("MarketData:Fetch").Get<MarketDataFetchOptions>() ?? new MarketDataFetchOptions();
        services.AddSingleton(fetchOptions);
        services.AddSingleton(ReadLiveOptions(configuration));

        var connectionString = configuration.GetConnectionString("Marketdata");
        if (!string.Equals(configuration["MarketData:Store"], "memory", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(connectionString))
        {
            var store = new PostgresFetchTaskStore(connectionString);
            services.AddSingleton(store);
            services.AddSingleton<IFetchTaskStore>(store);
            services.AddHealthChecks().AddCheck("postgres", new PostgresHealthCheck(store), tags: new[] { HealthTags.Ready });
        }
        else
        {
            services.AddSingleton<IFetchTaskStore, InMemoryFetchTaskStore>();
        }

        if (!string.Equals(configuration["MarketData:Bus"], "memory", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration["RabbitMQ:Uri"]))
        {
            services.AddLeanRabbitMq(configuration);
        }
        else if (!services.Any(s => s.ServiceType == typeof(IMessageBus)))
        {
            services.AddLeanInMemoryMessaging();
        }

        services.AddSingleton<IArtifactStore>(_ => configuration["S3:ServiceUrl"] is { Length: > 0 } s3
            ? new S3ArtifactStore(new Uri(s3), configuration["S3:AccessKey"] ?? string.Empty, configuration["S3:SecretKey"] ?? string.Empty, configuration["S3:Region"] ?? "us-east-1")
            : new FileSystemArtifactStore(configuration["MarketData:ArtifactRoot"] ?? Path.Combine(Path.GetTempPath(), "market-data-artifacts")));
        services.AddSingleton<FetchExecutor>();
        services.AddHealthChecks().AddCheck<LeanDataVolumeHealthCheck>("lean-data", tags: new[] { HealthTags.Ready });

        services.AddHostedService<MarketDataStartup>();
        if (!string.Equals(configuration["MarketData:RunTasks"], "false", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<OutboxRelay>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
            services.AddSingleton<FetchTaskRunner>();
            services.AddHostedService(sp => sp.GetRequiredService<FetchTaskRunner>());
        }
        services.AddSingleton<LiveDataFanout>();
        services.AddHostedService(sp => sp.GetRequiredService<LiveDataFanout>());
        return builder;
    }

    /// <summary><c>MarketData:Live:Symbols</c> is a list (<c>__0</c>, <c>__1</c>, ...) or one comma-separated string.</summary>
    public static LiveFanoutOptions ReadLiveOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("MarketData:Live");
        var options = new LiveFanoutOptions();
        section.Bind(options, o => o.ErrorOnUnknownConfiguration = false);
        if (section["Symbols"] is { Length: > 0 } csv)
        {
            options.Symbols = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        return options;
    }
}

/// <summary>Applies the <c>marketdata</c> schema (with retries while PostgreSQL starts) and clears crashed tasks' staging before serving.</summary>
public sealed class MarketDataStartup : IHostedService
{
    private readonly IFetchTaskStore _store;
    private readonly LeanDataVolume _volume;
    private readonly ILogger<MarketDataStartup> _logger;

    public MarketDataStartup(IFetchTaskStore store, LeanDataVolume volume, ILogger<MarketDataStartup> logger)
    {
        _store = store;
        _volume = volume;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception e) when (attempt < 30 && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "marketdata schema not applied yet (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        _volume.CleanStaging();
        _logger.LogInformation("market-data serving lean-data at {Root} with {Store}", _volume.Root, _store.GetType().Name);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly PostgresFetchTaskStore _store;

    public PostgresHealthCheck(PostgresFetchTaskStore store) => _store = store;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = _store.DataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("marketdata database unavailable", e);
        }
    }
}
