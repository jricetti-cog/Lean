using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace QuantConnect.Services.Gateway;

/// <summary>
/// Builds <c>/openapi</c>: the gateway's own operations plus each proxied service's live OpenAPI document with its
/// <c>/internal/v1</c> paths rewritten to the public routes (only the proxied methods). Unreachable services are
/// listed under <c>x-lean-upstreams</c> and the document is cached briefly.
/// </summary>
public sealed class OpenApiMerger
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _clients;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTimeOffset At, string Json)? _cached;

    public OpenApiMerger(IServiceProvider services, IHttpClientFactory clients)
    {
        _services = services;
        _clients = clients;
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - cached.At < CacheFor) return cached.Json;
            var json = (await BuildAsync(ct)).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            _cached = (DateTimeOffset.UtcNow, json);
            return json;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JsonObject> BuildAsync(CancellationToken ct)
    {
        var provider = _services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var own = await provider.GetOpenApiDocumentAsync(ct);
        var merged = JsonNode.Parse(await own.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, ct))!.AsObject();
        merged["info"] = new JsonObject
        {
            ["title"] = "LEAN public API (api-gateway)",
            ["version"] = "1.0.0",
            ["description"] = "Merged contract: gateway endpoints plus the proxied orchestrator-adjacent services (results, market-data, report, optimizer).",
        };
        merged["servers"] = new JsonArray(new JsonObject { ["url"] = "/" });
        var paths = merged["paths"] as JsonObject ?? new JsonObject();
        merged["paths"] = paths;
        var components = merged["components"] as JsonObject ?? new JsonObject();
        merged["components"] = components;
        var schemas = components["schemas"] as JsonObject ?? new JsonObject();
        components["schemas"] = schemas;
        components["securitySchemes"] = new JsonObject
        {
            ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "API key issued from configuration, or a JWT from the identity provider." },
            ["apiKey"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = GatewayAuth.ApiKeyHeader },
        };
        merged["security"] = new JsonArray(new JsonObject { ["bearer"] = new JsonArray() }, new JsonObject { ["apiKey"] = new JsonArray() });

        var upstreams = new JsonObject();
        foreach (var cluster in GatewayProxy.Routes.Select(r => r.Cluster).Distinct())
        {
            JsonObject document;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var text = await _clients.CreateClient(cluster).GetStringAsync("openapi/v1.json", timeout.Token);
                document = JsonNode.Parse(text)!.AsObject();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                upstreams[cluster] = $"unavailable: {e.Message}";
                continue;
            }
            var count = 0;
            foreach (var (internalPath, item) in document["paths"] as JsonObject ?? new JsonObject())
            {
                foreach (var route in GatewayProxy.Routes.Where(r => r.Cluster == cluster))
                {
                    if (PublicPath(route, internalPath) is not { } publicPath || paths.ContainsKey(publicPath)) continue;
                    var operations = new JsonObject();
                    foreach (var (method, operation) in item!.AsObject())
                    {
                        if (!route.Methods.Contains(method, StringComparer.OrdinalIgnoreCase)) continue;
                        var copy = operation!.DeepClone();
                        if (copy is JsonObject op) op["tags"] = new JsonArray(cluster);
                        operations[method] = copy;
                        count++;
                    }
                    if (operations.Count > 0) paths[publicPath] = operations;
                    break;
                }
            }
            foreach (var (name, schema) in document["components"]?["schemas"] as JsonObject ?? new JsonObject())
            {
                if (!schemas.ContainsKey(name)) schemas[name] = schema!.DeepClone();
            }
            upstreams[cluster] = $"merged {count} operations";
        }
        merged["x-lean-upstreams"] = upstreams;
        return merged;
    }

    /// <summary>Maps an upstream path onto <paramref name="route"/>'s public template, or null when it is not proxied.</summary>
    public static string? PublicPath(ProxyRoute route, string internalPath)
    {
        static string Trim(string template) => template.Replace("/{**rest}", "");
        var internalBase = Trim(route.InternalPath);
        var publicBase = Trim(route.PublicPath);
        var catchAll = route.InternalPath.EndsWith("{**rest}");
        if (internalPath == internalBase) return publicBase;
        return catchAll && internalPath.StartsWith(internalBase + "/") ? publicBase + internalPath[internalBase.Length..] : null;
    }
}
