using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.Gateway;

/// <summary>Named <see cref="HttpClient"/>s for the internal services; each forwards the caller and request id.</summary>
public static class UpstreamClients
{
    public const string UserHeader = "X-Lean-User";

    public static IServiceCollection AddUpstreamClients(this IServiceCollection services, GatewayOptions options)
    {
        services.AddHttpContextAccessor();
        services.AddTransient<CallerHeadersHandler>();
        foreach (var (name, url) in options.Upstreams.All())
        {
            services.AddHttpClient(name, c =>
            {
                c.BaseAddress = new Uri(url.TrimEnd('/') + "/");
                c.Timeout = TimeSpan.FromSeconds(30);
            }).AddHttpMessageHandler<CallerHeadersHandler>();
        }
        return services;
    }

    public static HttpClient Client(this HttpContext http, string upstream) =>
        http.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(upstream);

    /// <summary>Sends to an upstream; an unreachable service becomes a 503 problem instead of an exception.</summary>
    public static async Task<(HttpResponseMessage? Response, IResult? Failure)> SendAsync(this HttpContext http, string upstream, HttpRequestMessage request)
    {
        try
        {
            return (await http.Client(upstream).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            return (null, Problems.Unavailable(http, upstream, e.Message));
        }
    }

    public static async Task<(T? Value, IResult? Failure)> GetContractAsync<T>(this HttpContext http, string upstream, string path) where T : class
    {
        var (response, failure) = await http.SendAsync(upstream, new HttpRequestMessage(HttpMethod.Get, path));
        if (failure != null) return (null, failure);
        using (response)
        {
            if (!response!.IsSuccessStatusCode) return (null, await RelayAsync(response));
            return (JsonSerializer.Deserialize<T>(await response.Content.ReadAsStreamAsync(http.RequestAborted), ContractJson.Options), null);
        }
    }

    public static Task<(HttpResponseMessage? Response, IResult? Failure)> PostJsonAsync<T>(this HttpContext http, string upstream, string path, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(ContractJson.SerializeToUtf8Bytes(body)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (http.Request.Headers.TryGetValue("Idempotency-Key", out var key)) request.Headers.TryAddWithoutValidation("Idempotency-Key", (string?)key);
        return http.SendAsync(upstream, request);
    }

    /// <summary>Copies an upstream response (status, content type, Location, body) to the caller.</summary>
    public static async Task<IResult> RelayAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.ToString();
            var location = response.Headers.Location?.ToString();
            return new RelayResult((int)response.StatusCode, contentType, location, body);
        }
    }

    public static async Task<IResult> RelayAsync(this Task<(HttpResponseMessage? Response, IResult? Failure)> send)
    {
        var (response, failure) = await send;
        return failure ?? await RelayAsync(response!);
    }

    private sealed record RelayResult(int Status, string? ContentType, string? Location, byte[] Body) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = Status;
            if (ContentType != null) http.Response.ContentType = ContentType;
            if (Location != null) http.Response.Headers.Location = Location.Replace("/internal/v1/", "/api/v1/");
            if (Body.Length > 0) await http.Response.Body.WriteAsync(Body, http.RequestAborted);
        }
    }

    private sealed class CallerHeadersHandler : DelegatingHandler
    {
        private readonly IHttpContextAccessor _accessor;

        public CallerHeadersHandler(IHttpContextAccessor accessor) => _accessor = accessor;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_accessor.HttpContext is { } http)
            {
                request.Headers.Remove(UserHeader);
                request.Headers.TryAddWithoutValidation(UserHeader, http.User.User());
                request.Headers.TryAddWithoutValidation(RequestIdMiddleware.Header, http.TraceIdentifier);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>Non-admin callers only see their own jobs (and everything keyed by the job id).</summary>
public static class JobAccess
{
    public static async Task<IResult?> CheckAsync(HttpContext http, Guid id)
    {
        if (http.User.IsAdmin()) return null;
        var (job, failure) = await http.GetContractAsync<Job>(GatewayProxy.Orchestrator, $"internal/v1/jobs/{id:D}");
        if (failure != null) return failure;
        return job!.SubmittedBy == http.User.User() ? null : Problems.NotFound(http, $"job {id:D} not found");
    }
}
