using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace QuantConnect.Tests.Services.Parity;

/// <summary>Minimal client of the public gateway API (<c>/api/v1</c>); retries 429 and transient 5xx/network errors.</summary>
public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _http;

    public GatewayClient(Uri gateway, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(gateway, "api/v1/"), Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_http.BaseAddress!, "/healthz"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary><c>POST /algorithms</c>: returns the <c>AlgorithmRef</c> to submit.</summary>
    public async Task<JsonNode> UploadAsync(byte[] content, string fileName, Language language, CancellationToken ct) =>
        (await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".zip") ? "application/zip" : "text/plain");
            form.Add(file, "file", fileName);
            form.Add(new StringContent(language.ToString()), "language");
            return new HttpRequestMessage(HttpMethod.Post, "algorithms") { Content = form };
        }, ct))!;

    /// <summary><c>POST /backtests</c>: returns the job id.</summary>
    public async Task<Guid> SubmitBacktestAsync(string name, JsonNode algorithm, IReadOnlyDictionary<string, string> parameters,
        int maxRuntimeSeconds, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["algorithm"] = algorithm.DeepClone(),
            ["parameters"] = new JsonObject(parameters.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
            ["fetchMissingData"] = false,
            ["maxRuntimeSeconds"] = maxRuntimeSeconds,
        };
        var job = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "backtests")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        }, ct);
        return Guid.Parse(job!["id"]!.GetValue<string>());
    }

    public Task<JsonNode?> GetJobAsync(Guid id, CancellationToken ct) => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"jobs/{id:D}"), ct);

    /// <summary><c>GET /jobs/{id}/result</c>; null while the results service has no summary yet (404).</summary>
    public Task<JsonNode?> GetResultAsync(Guid id, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"jobs/{id:D}/result"), ct, notFoundIsNull: true);

    private async Task<JsonNode?> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct, bool notFoundIsNull = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var message = request();
                response = await _http.SendAsync(message, ct);
            }
            catch (Exception e) when (attempt < 10 && e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)), ct);
                continue;
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound) return null;
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!transient || attempt >= 30)
                {
                    throw new HttpRequestException($"{request().Method} {request().RequestUri} -> {(int)response.StatusCode}: {text}", null, response.StatusCode);
                }
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, attempt * 2));
                await Task.Delay(delay, ct);
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
