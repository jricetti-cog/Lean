using Microsoft.Extensions.DependencyInjection.Extensions;
using QuantConnect.Services.Defaults;

namespace QuantConnect.Services.Gateway;

/// <summary>Composition of the api-gateway host; <see cref="Program"/> runs it and the tests build it in-process.</summary>
public static class GatewayHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.AddLeanServiceDefaults();
        var options = builder.Configuration.GetSection(GatewayOptions.Section).Get<GatewayOptions>() ?? new GatewayOptions();
        builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = options.MaxUploadBytes + 1024 * 1024);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = options.MaxUploadBytes + 1024 * 1024);
        builder.Services.AddOpenApi();
        builder.Services.AddGatewayAuth(options);
        builder.Services.AddUpstreamClients(options);
        builder.Services.AddGatewayProxy(options);
        builder.Services.AddSingleton<OpenApiMerger>();
        builder.Services.AddSingleton<PresignedUrls>();
        builder.Services.Configure<QuantConnect.Services.Messaging.RabbitMq.RabbitMqOptions>(builder.Configuration.GetSection("RabbitMQ"));
        builder.Services.TryAddSingleton<IJobEventSource, RabbitMqJobEventSource>();

        var app = builder.Build();
        if (options.ApiKeys.Count == 0 && !options.Jwt.Enabled)
        {
            app.Logger.LogWarning("No Gateway:ApiKeys and no Gateway:Jwt configured: every /api/v1 request will be rejected");
        }
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapOpenApi("/openapi/gateway.json").AllowAnonymous();
        app.MapGet("/openapi", async (OpenApiMerger merger, CancellationToken ct) =>
            Results.Text(await merger.GetAsync(ct), "application/json")).AllowAnonymous().ExcludeFromDescription();
        app.MapGatewayEndpoints();
        app.MapReverseProxy(proxy => proxy.UseGatewayProxyPipeline()).RequireAuthorization();
        return app;
    }
}
