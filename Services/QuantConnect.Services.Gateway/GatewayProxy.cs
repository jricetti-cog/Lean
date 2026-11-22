using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace QuantConnect.Services.Gateway;

/// <summary>One public route forwarded unchanged (apart from the path) to an internal service.</summary>
public sealed record ProxyRoute(string Id, string Cluster, string PublicPath, string InternalPath, string[] Methods);

/// <summary>YARP routes from the public <c>/api/v1</c> surface to the services' <c>/internal/v1</c> APIs.</summary>
public static class GatewayProxy
{
    public const string Orchestrator = "orchestrator";
    public const string Results = "results";
    public const string MarketData = "market-data";
    public const string Report = "report";
    public const string Optimizer = "optimizer";

    private static readonly string[] Get = { "GET" };
    private static readonly string[] GetPost = { "GET", "POST" };

    public static readonly IReadOnlyList<ProxyRoute> Routes = new[]
    {
        new ProxyRoute("results-summary", Results, "/api/v1/jobs/{id}/result", "/internal/v1/results/{id}", Get),
        new ProxyRoute("results-live-state", Results, "/api/v1/live/{id}/state", "/internal/v1/results/{id}/live-state", Get),
    }.Concat(new[] { "charts", "orders", "order-events", "logs", "artifacts" }.Select(section =>
        new ProxyRoute($"results-{section}", Results, $"/api/v1/jobs/{{id}}/{section}/{{**rest}}", $"/internal/v1/results/{{id}}/{section}/{{**rest}}", Get)))
    .Concat(new[]
    {
        new ProxyRoute("market-data", MarketData, "/api/v1/data/{**rest}", "/internal/v1/data/{**rest}", GetPost),
        new ProxyRoute("optimizer", Optimizer, "/api/v1/optimizations/{**rest}", "/internal/v1/optimizations/{**rest}", GetPost),
        new ProxyRoute("reports-list", Report, "/api/v1/reports", "/internal/v1/reports", Get),
        new ProxyRoute("reports-get", Report, "/api/v1/reports/{id}", "/internal/v1/reports/{id}", Get),
    }).ToList();

    public static IReverseProxyBuilder AddGatewayProxy(this IServiceCollection services, GatewayOptions options)
    {
        var routes = Routes.Select(r => new RouteConfig
        {
            RouteId = r.Id,
            ClusterId = r.Cluster,
            Match = new RouteMatch { Path = r.PublicPath, Methods = r.Methods },
        }.WithTransformPathRouteValues(r.InternalPath)).ToList();
        var clusters = options.Upstreams.All()
            .Where(u => u.Key != Orchestrator)
            .Select(u => new ClusterConfig
            {
                ClusterId = u.Key,
                Destinations = new Dictionary<string, DestinationConfig> { ["primary"] = new() { Address = u.Value } },
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) },
            }).ToList();
        return services.AddReverseProxy()
            .LoadFromMemory(routes, clusters)
            .ConfigureHttpClient((_, handler) => handler.ConnectTimeout = UpstreamClients.ConnectTimeout)
            .AddTransforms(context =>
            {
                context.AddRequestHeaderRemove("Authorization");
                context.AddRequestHeaderRemove(GatewayAuth.ApiKeyHeader);
                context.AddRequestTransform(transform =>
                {
                    var headers = transform.ProxyRequest.Headers;
                    headers.Remove(UpstreamClients.UserHeader);
                    headers.TryAddWithoutValidation(UpstreamClients.UserHeader, transform.HttpContext.User.User());
                    return ValueTask.CompletedTask;
                });
            });
    }

    /// <summary>Ownership check before forwarding, and a 503 problem+json when the upstream cannot be reached.</summary>
    public static void UseGatewayProxyPipeline(this IReverseProxyApplicationBuilder proxy)
    {
        proxy.Use(async (http, next) =>
        {
            await next();
            var error = http.GetForwarderErrorFeature();
            if (error == null || http.Response.HasStarted) return;
            var cluster = http.GetReverseProxyFeature().Cluster?.Config.ClusterId ?? "upstream";
            http.Response.Clear();
            await Problems.WriteAsync(http, StatusCodes.Status503ServiceUnavailable, "Service Unavailable",
                $"upstream '{cluster}' is unavailable ({error.Error}): {error.Exception?.Message}");
        });
        proxy.Use(async (http, next) =>
        {
            if (http.GetReverseProxyFeature().Route.Config.RouteId == "reports-list" && !http.User.IsAdmin())
            {
                var denied = Guid.TryParse(http.Request.Query["sourceJobId"], out var sourceJobId)
                    ? await JobAccess.CheckAsync(http, sourceJobId)
                    : Problems.BadRequest(http, "sourceJobId is required to list reports without an admin key");
                if (denied != null)
                {
                    await denied.ExecuteAsync(http);
                    return;
                }
            }
            else if (http.GetRouteValue("id") is string raw)
            {
                var denied = Guid.TryParse(raw, out var id)
                    ? await JobAccess.CheckAsync(http, id)
                    : Problems.NotFound(http, $"job {raw} not found");
                if (denied != null)
                {
                    await denied.ExecuteAsync(http);
                    return;
                }
            }
            await next();
        });
        proxy.UseLoadBalancing();
    }
}
