using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Messaging.RabbitMq;

namespace QuantConnect.Services.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RabbitMqMessageBus"/> as <see cref="IMessageBus"/>/<see cref="IMessagePublisher"/>/<see cref="IMessageConsumer"/>
    /// from <c>RabbitMQ:Uri</c> (env <c>RabbitMQ__Uri</c>) and <c>RabbitMQ:ClientName</c>, plus a <c>rabbitmq</c> readiness check.
    /// </summary>
    public static IServiceCollection AddLeanRabbitMq(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new RabbitMqOptions();
        if (configuration["RabbitMQ:Uri"] is { Length: > 0 } uri)
        {
            options.Uri = new Uri(uri);
        }
        options.ClientName = configuration["RabbitMQ:ClientName"] ?? configuration["OTEL_SERVICE_NAME"];
        if (int.TryParse(configuration["RabbitMQ:PublishTimeoutSeconds"], out var seconds) && seconds > 0)
        {
            options.PublishTimeout = TimeSpan.FromSeconds(seconds);
        }

        services.AddSingleton(options);
        services.AddSingleton<RabbitMqMessageBus>();
        services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<RabbitMqMessageBus>());
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<RabbitMqMessageBus>());
        services.AddSingleton<IMessageConsumer>(sp => sp.GetRequiredService<RabbitMqMessageBus>());
        services.AddSingleton<ITransientSubscriber>(sp => sp.GetRequiredService<RabbitMqMessageBus>());
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: new[] { HealthTags.Ready });
        return services;
    }

    /// <summary>Registers an <see cref="InMemoryMessageBus"/> (unit tests).</summary>
    public static IServiceCollection AddLeanInMemoryMessaging(this IServiceCollection services, InMemoryMessageBus? bus = null)
    {
        bus ??= new InMemoryMessageBus();
        services.AddSingleton(bus);
        services.AddSingleton<IMessageBus>(bus);
        services.AddSingleton<IMessagePublisher>(bus);
        services.AddSingleton<IMessageConsumer>(bus);
        services.AddSingleton<ITransientSubscriber>(bus);
        return services;
    }
}

public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqMessageBus _bus;

    public RabbitMqHealthCheck(RabbitMqMessageBus bus) => _bus = bus;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _bus.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ unreachable", e);
        }
    }
}
