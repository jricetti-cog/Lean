using System.Diagnostics;
using System.Text.RegularExpressions;

namespace QuantConnect.Services.Gateway;

/// <summary>
/// Accepts a well-formed client <c>X-Request-ID</c> (or uses the trace id), echoes it on the response, tags the
/// server span and its baggage with it, and leaves it on the request so the proxy and gateway clients forward it.
/// </summary>
public sealed partial class RequestIdMiddleware
{
    public const string Header = "X-Request-ID";
    public const string TagName = "lean.request_id";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext http)
    {
        var incoming = http.Request.Headers[Header].ToString();
        var activity = Activity.Current;
        var id = ValidId().IsMatch(incoming) ? incoming : activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        http.Request.Headers[Header] = id;
        http.TraceIdentifier = id;
        http.Response.OnStarting(() =>
        {
            http.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });
        activity?.SetTag(TagName, id);
        activity?.SetBaggage(TagName, id);
        using (_logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id }))
        {
            await _next(http);
        }
    }
}
