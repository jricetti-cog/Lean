using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.MarketData.Fetch;

namespace QuantConnect.Services.MarketData;

/// <summary><c>POST /internal/v1/data/fetches</c> request body (market-data.openapi.yaml).</summary>
public sealed record CreateFetchRequest
{
    public required MarketDataFetchSpec Fetch { get; init; }
    public Guid? RequestingJobId { get; init; }
}

/// <summary>The market-data internal API (docs/architecture/contracts/openapi/market-data.openapi.yaml).</summary>
public static class MarketDataEndpoints
{
    public const string Prefix = "/internal/v1/data";

    public static IEndpointRouteBuilder MapMarketData(this IEndpointRouteBuilder app)
    {
        var data = app.MapGroup(Prefix);
        data.MapGet("/catalog", Catalog);
        data.MapGet("/coverage", GetCoverage);
        data.MapGet("/history", History);
        data.MapPost("/fetches", CreateFetch);
        data.MapGet("/fetches", ListFetches);
        data.MapGet("/fetches/{id}", GetFetch);
        return app;
    }

    private static IResult Catalog(HttpRequest request, DataCatalog catalog)
    {
        SecurityType? securityType = null;
        Resolution? resolution = null;
        var q = request.Query;
        if (q.ContainsKey("securityType"))
        {
            if (!ContractMapping.TryParse<ContractSecurityType>(q["securityType"], out var st)) return BadRequest("securityType", "unknown securityType");
            securityType = st.ToLean();
        }
        if (q.ContainsKey("resolution"))
        {
            if (!ContractMapping.TryParse<ContractResolution>(q["resolution"], out var r)) return BadRequest("resolution", "unknown resolution");
            resolution = r.ToLean();
        }
        string? market = q.ContainsKey("market") ? q["market"].ToString() : null;
        return Json(catalog.List(securityType, market, resolution));
    }

    private static IResult GetCoverage(HttpRequest request, DataCatalog catalog)
    {
        if (!TryParseSeries(request, out var series, out var problem)) return problem!;
        return Json(catalog.Coverage(series.SecurityType, series.Market, series.Ticker, series.Resolution, series.TickType, series.Start, series.End));
    }

    private static IResult History(HttpRequest request, HistoryService history, DataCatalog catalog)
    {
        if (!TryParseSeries(request, out var series, out var problem)) return problem!;
        var bars = history.Get(series.SecurityType, series.Market, series.Ticker, series.Resolution, series.TickType, series.Start, series.End);
        if (bars == null) return BadRequest("end", $"more than {HistoryService.MaxBars} bars; narrow the range");
        if (bars.Count == 0 && catalog.AvailableDates(series.SecurityType, series.Market, series.Ticker, series.Resolution, series.TickType).Count == 0)
        {
            return Problem(StatusCodes.Status404NotFound, "Not Found", $"No {series.Resolution} {series.TickType} data for {series.SecurityType}/{series.Market}/{series.Ticker} on lean-data", request);
        }
        return Json(bars);
    }

    private static async Task<IResult> CreateFetch(HttpRequest request, IFetchTaskStore store, MarketDataFetchOptions options, CancellationToken cancellationToken)
    {
        CreateFetchRequest? body;
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync<CreateFetchRequest>(request.Body, ContractJson.Options, cancellationToken);
        }
        catch (System.Text.Json.JsonException e)
        {
            return BadRequest("fetch", e.Message);
        }
        if (body?.Fetch == null) return BadRequest("fetch", "fetch is required");
        if (FetchExecutor.Validate(body.Fetch, options) is { } error) return BadRequest("fetch", error);

        var idempotencyKey = request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } key ? key : null;
        if (idempotencyKey is { Length: > 128 }) return BadRequest("Idempotency-Key", "at most 128 characters");
        var submittedBy = request.Headers[FetchTasks.SubmittedByHeader].ToString() is { Length: > 0 } by ? by
            : body.RequestingJobId?.ToString("D") ?? "market-data-api";
        try
        {
            var (task, _) = await store.CreateAsync(body.Fetch, body.RequestingJobId, submittedBy, idempotencyKey, cancellationToken);
            return Json(task, StatusCodes.Status202Accepted);
        }
        catch (NpgsqlException e)
        {
            request.HttpContext.Response.Headers.RetryAfter = "5";
            return Problem(StatusCodes.Status503ServiceUnavailable, "Service Unavailable", $"marketdata database unavailable: {e.Message}", request);
        }
    }

    private static async Task<IResult> ListFetches(HttpRequest request, IFetchTaskStore store, CancellationToken cancellationToken)
    {
        var q = request.Query;
        JobStatus? status = null;
        if (q.ContainsKey("status"))
        {
            try
            {
                status = FetchTasks.ParseStatus(q["status"].ToString());
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest("status", "unknown status");
            }
        }
        var limit = 50;
        if (q.ContainsKey("limit") && (!int.TryParse(q["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 500))
        {
            return BadRequest("limit", "limit must be 1..500");
        }
        var (items, next) = await store.ListAsync(status, q["cursor"].ToString() is { Length: > 0 } c ? c : null, limit, cancellationToken);
        return Json(new Page<FetchTask> { Items = items, Next = next });
    }

    private static async Task<IResult> GetFetch(string id, HttpRequest request, IFetchTaskStore store, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var taskId)) return BadRequest("id", "id must be a UUID");
        var task = await store.GetAsync(taskId, cancellationToken);
        return task == null ? Problem(StatusCodes.Status404NotFound, "Not Found", $"No fetch task {taskId}", request) : Json(task);
    }

    private sealed record Series(SecurityType SecurityType, string Market, string Ticker, Resolution Resolution, TickType TickType, DateOnly Start, DateOnly End);

    private static bool TryParseSeries(HttpRequest request, out Series series, out IResult? problem)
    {
        series = null!;
        var q = request.Query;
        problem = null;
        if (!ContractMapping.TryParse<ContractSecurityType>(q["securityType"], out var st)) problem = BadRequest("securityType", "securityType is required (Equity, Forex, ...)");
        var market = q["market"].ToString();
        var ticker = q["ticker"].ToString();
        if (problem == null && (market.Length == 0 || !market.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c)))) problem = BadRequest("market", "market must match ^[a-z0-9]+$");
        if (problem == null && (ticker.Length is 0 or > 64 || ticker.Contains('/') || ticker.Contains('\\') || ticker.Contains(".."))) problem = BadRequest("ticker", "ticker is required (at most 64 characters)");
        if (!ContractMapping.TryParse<ContractResolution>(q["resolution"], out var res)) problem ??= BadRequest("resolution", "resolution is required (Tick, Second, Minute, Hour, Daily)");
        if (!DateOnly.TryParseExact(q["start"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) problem ??= BadRequest("start", "start must be yyyy-MM-dd");
        if (!DateOnly.TryParseExact(q["end"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) problem ??= BadRequest("end", "end must be yyyy-MM-dd");
        if (start > end) problem ??= BadRequest("start", "start is after end");
        if (problem != null) return false;

        var tickType = ContractMapping.DefaultTickType(st.ToLean());
        if (q.ContainsKey("tickType"))
        {
            if (!ContractMapping.TryParse<ContractTickType>(q["tickType"], out var tt))
            {
                problem = BadRequest("tickType", "tickType must be Trade, Quote or OpenInterest");
                return false;
            }
            tickType = tt.ToLean();
        }
        series = new Series(st.ToLean(), market, ticker, res.ToLean(), tickType, start, end);
        return true;
    }

    private static IResult Json<T>(T value, int status = StatusCodes.Status200OK) => Results.Json(value, ContractJson.Options, statusCode: status);

    private static IResult BadRequest(string field, string message) => Results.Json(new Problem
    {
        Type = "about:blank", Title = "Bad Request", Status = StatusCodes.Status400BadRequest, Detail = message,
        Errors = new() { [field] = new() { message } },
    }, ContractJson.Options, "application/problem+json", StatusCodes.Status400BadRequest);

    private static IResult Problem(int status, string title, string detail, HttpRequest request) => Results.Json(new Problem
    {
        Type = "about:blank", Title = title, Status = status, Detail = detail, Instance = request.Path,
        TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
    }, ContractJson.Options, "application/problem+json", status);
}
