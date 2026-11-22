using System.Diagnostics;
using System.Text;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.Gateway;

/// <summary>RFC 9457 <c>application/problem+json</c> responses (common.yaml#/components/schemas/Problem).</summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    public static Problem Create(HttpContext http, int status, string title, string? detail = null, Dictionary<string, List<string>>? errors = null) => new()
    {
        Type = $"https://httpstatuses.io/{status}",
        Title = title,
        Status = status,
        Detail = detail,
        Instance = http.Request.Path,
        TraceId = Activity.Current?.TraceId.ToString(),
        Errors = errors,
    };

    public static IResult Result(HttpContext http, int status, string title, string? detail = null, Dictionary<string, List<string>>? errors = null) =>
        Results.Text(ContractJson.Serialize(Create(http, status, title, detail, errors)), ContentType, Encoding.UTF8, status);

    public static async Task WriteAsync(HttpContext http, int status, string title, string? detail = null)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = ContentType;
        await http.Response.WriteAsync(ContractJson.Serialize(Create(http, status, title, detail)), http.RequestAborted);
    }

    public static IResult BadRequest(HttpContext http, string detail, Dictionary<string, List<string>>? errors = null) =>
        Result(http, StatusCodes.Status400BadRequest, "Bad Request", detail, errors);

    public static IResult NotFound(HttpContext http, string detail) => Result(http, StatusCodes.Status404NotFound, "Not Found", detail);

    public static IResult Unavailable(HttpContext http, string upstream, string detail) =>
        Result(http, StatusCodes.Status503ServiceUnavailable, "Service Unavailable", $"upstream '{upstream}' is unavailable: {detail}");
}
