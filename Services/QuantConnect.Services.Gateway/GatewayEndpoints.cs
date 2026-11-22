using System.Globalization;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.Gateway;

/// <summary>The gateway's own <c>/api/v1</c> endpoints (gateway.openapi.yaml); the rest is proxied.</summary>
public static class GatewayEndpoints
{
    private const string Jobs = "internal/v1/jobs";

    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapPost("/algorithms", async Task<IResult> (HttpContext http) =>
        {
            if (!http.Request.HasFormContentType) return Problems.BadRequest(http, "send multipart/form-data with a 'file' part");
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var (algorithm, failure) = await UploadAsync(http, form);
            return failure ?? Contract(algorithm!, StatusCodes.Status201Created);
        }).WithName("uploadAlgorithm").WithTags("algorithms").DisableAntiforgery();

        api.MapPost("/backtests", Task<IResult> (HttpContext http) => SubmitAsync(http, JobKind.Backtest))
            .WithName("submitBacktest").WithTags("backtests").DisableAntiforgery();
        api.MapPost("/live", Task<IResult> (HttpContext http) => SubmitAsync(http, JobKind.Live))
            .WithName("deployLive").WithTags("live").DisableAntiforgery();
        api.MapPost("/live/{id:guid}/stop", (HttpContext http, Guid id) => CancelAsync(http, id, "live deployment stopped"))
            .WithName("stopLive").WithTags("live");
        api.MapPost("/optimizations", Task<IResult> (HttpContext http) => SubmitAsync(http, JobKind.Optimization))
            .WithName("submitOptimization").WithTags("optimizations");
        api.MapPost("/reports", Task<IResult> (HttpContext http) => SubmitAsync(http, JobKind.Report))
            .WithName("createReport").WithTags("reports");

        api.MapGet("/jobs", (HttpContext http, string? kind, string? status, Guid? parentJobId, string? submittedBy, string? cursor, int? limit) =>
        {
            var query = new List<string>();
            void Add(string name, object? value)
            {
                if (value != null) query.Add($"{name}={Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture)!)}");
            }
            Add("kind", kind);
            Add("status", status);
            Add("parentJobId", parentJobId);
            Add("submittedBy", http.User.IsAdmin() ? submittedBy : http.User.User());
            Add("cursor", cursor);
            Add("limit", limit);
            return http.SendAsync(GatewayProxy.Orchestrator, new HttpRequestMessage(HttpMethod.Get, $"{Jobs}?{string.Join('&', query)}")).RelayAsync();
        }).WithName("listJobs").WithTags("jobs");

        api.MapGet("/jobs/{id:guid}", async Task<IResult> (HttpContext http, Guid id) =>
        {
            var (job, failure) = await http.GetContractAsync<Job>(GatewayProxy.Orchestrator, $"{Jobs}/{id:D}");
            if (failure != null) return failure;
            return http.User.IsAdmin() || job!.SubmittedBy == http.User.User() ? Contract(job!) : Problems.NotFound(http, $"job {id:D} not found");
        }).WithName("getJob").WithTags("jobs");

        api.MapPost("/jobs/{id:guid}/cancel", (HttpContext http, Guid id) => CancelAsync(http, id, null))
            .WithName("cancelJob").WithTags("jobs");

        api.MapGet("/jobs/{id:guid}/result/download", async Task<IResult> (HttpContext http, Guid id, int? attempt) =>
        {
            if (await JobAccess.CheckAsync(http, id) is { } denied) return denied;
            var path = $"internal/v1/results/{id:D}" + (attempt != null ? $"?attempt={attempt}" : "");
            var (summary, failure) = await http.GetContractAsync<ResultSummary>(GatewayProxy.Results, path);
            if (failure != null) return failure;
            return summary!.Result is { } obj ? Presign(http, obj) : Problems.NotFound(http, $"job {id:D} has no result document yet");
        }).WithName("downloadResult").WithTags("results");

        api.MapGet("/reports/{id:guid}/{format}", async Task<IResult> (HttpContext http, Guid id, string format) =>
        {
            if (format is not ("html" or "pdf")) return Problems.NotFound(http, "report formats are html and pdf");
            if (await JobAccess.CheckAsync(http, id) is { } denied) return denied;
            var (report, failure) = await http.GetContractAsync<Report>(GatewayProxy.Report, $"internal/v1/reports/{id:D}");
            if (failure != null) return failure;
            var obj = format == "html" ? report!.Html : report!.Pdf;
            return obj != null ? Presign(http, obj) : Problems.NotFound(http, $"report {id:D} has no {format} rendering");
        }).WithName("downloadReport").WithTags("reports");
    }

    private static async Task<IResult> SubmitAsync(HttpContext http, JobKind kind)
    {
        JobRequest request;
        try
        {
            if (http.Request.HasFormContentType && kind is JobKind.Backtest or JobKind.Live)
            {
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                var (algorithm, failure) = await UploadAsync(http, form);
                if (failure != null) return failure;
                request = kind == JobKind.Backtest
                    ? new JobRequest { Kind = kind, Backtest = BacktestFromForm(form, algorithm!) }
                    : new JobRequest { Kind = kind, Live = LiveFromForm(form, algorithm!) };
            }
            else if (http.Request.HasJsonContentType())
            {
                request = kind switch
                {
                    JobKind.Backtest => new JobRequest { Kind = kind, Backtest = await ReadAsync<BacktestSpec>(http) },
                    JobKind.Live => new JobRequest { Kind = kind, Live = await ReadAsync<LiveSpec>(http) },
                    JobKind.Optimization => new JobRequest { Kind = kind, Optimization = await ReadAsync<OptimizationSpec>(http) },
                    _ => new JobRequest { Kind = kind, Report = await ReadAsync<ReportSpec>(http) },
                };
            }
            else
            {
                return Problems.Result(http, StatusCodes.Status415UnsupportedMediaType, "Unsupported Media Type",
                    kind is JobKind.Backtest or JobKind.Live ? "send application/json or multipart/form-data" : "send application/json");
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
        {
            return Problems.BadRequest(http, $"invalid {kind} request: {e.Message}");
        }
        if (request.Report is { } report)
        {
            if (await JobAccess.CheckAsync(http, report.SourceJobId) is { } denied) return denied;
            if (report.LiveJobId is { } live && await JobAccess.CheckAsync(http, live) is { } deniedLive) return deniedLive;
        }
        return await http.PostJsonAsync(GatewayProxy.Orchestrator, Jobs, request).RelayAsync();
    }

    private static async Task<IResult> CancelAsync(HttpContext http, Guid id, string? defaultReason)
    {
        if (await JobAccess.CheckAsync(http, id) is { } denied) return denied;
        string? reason = defaultReason;
        if (http.Request.HasJsonContentType() && http.Request.ContentLength != 0)
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("reason", out var r)) reason = r.GetString();
            }
            catch (JsonException e)
            {
                return Problems.BadRequest(http, $"invalid cancel request: {e.Message}");
            }
        }
        return await http.PostJsonAsync(GatewayProxy.Orchestrator, $"{Jobs}/{id:D}/cancel", new { reason }).RelayAsync();
    }

    /// <summary>Validates the multipart <c>file</c> and stores it in <c>lean-algorithms</c> through the orchestrator.</summary>
    private static async Task<(UploadedAlgorithmRef? Algorithm, IResult? Failure)> UploadAsync(HttpContext http, IFormCollection form)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0) return (null, Problems.BadRequest(http, "a non-empty 'file' part is required"));
        if (file.Length > options.MaxUploadBytes) return (null, Problems.BadRequest(http, $"algorithm uploads are limited to {options.MaxUploadBytes} bytes"));
        AlgorithmLanguage? declared = null;
        if (form["language"].FirstOrDefault() is { Length: > 0 } language)
        {
            if (!Enum.TryParse<AlgorithmLanguage>(language, true, out var parsed)) return (null, Problems.BadRequest(http, "language must be csharp or python"));
            declared = parsed;
        }
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, http.RequestAborted);
        var content = buffer.ToArray();
        var fileName = Path.GetFileName(file.FileName);
        var package = AlgorithmPackages.Inspect(fileName, content, declared, out var error);
        if (error != null) return (null, Problems.BadRequest(http, error, new() { ["file"] = new() { error } }));

        var upload = new HttpRequestMessage(HttpMethod.Post, $"internal/v1/algorithms?fileName={Uri.EscapeDataString(fileName)}")
        {
            Content = new ByteArrayContent(content),
        };
        upload.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(package.ContentType);
        var (response, failure) = await http.SendAsync(GatewayProxy.Orchestrator, upload);
        if (failure != null) return (null, failure);
        if (!response!.IsSuccessStatusCode) return (null, await UpstreamClients.RelayAsync(response));
        using (response)
        {
            var obj = JsonSerializer.Deserialize<ObjectRef>(await response.Content.ReadAsStreamAsync(http.RequestAborted), ContractJson.Options)!;
            return (new UploadedAlgorithmRef { Language = package.Language, Object = obj, TypeName = Field(form, "typeName") }, null);
        }
    }

    private static BacktestSpec BacktestFromForm(IFormCollection form, AlgorithmRef algorithm) => new()
    {
        Algorithm = algorithm,
        Name = Field(form, "name"),
        Parameters = Parameters(form),
        StartDate = Field(form, "startDate") is { } start ? DateOnly.Parse(start, CultureInfo.InvariantCulture) : null,
        EndDate = Field(form, "endDate") is { } end ? DateOnly.Parse(end, CultureInfo.InvariantCulture) : null,
        Cash = Field(form, "cash") is { } cash ? decimal.Parse(cash, CultureInfo.InvariantCulture) : null,
        FetchMissingData = Field(form, "fetchMissingData") is { } fetch ? bool.Parse(fetch) : null,
        MaxRuntimeSeconds = Field(form, "maxRuntimeSeconds") is { } max ? int.Parse(max, CultureInfo.InvariantCulture) : null,
    };

    private static LiveSpec LiveFromForm(IFormCollection form, AlgorithmRef algorithm) => new()
    {
        Algorithm = algorithm,
        Name = Field(form, "name"),
        Parameters = Parameters(form),
        Brokerage = Field(form, "brokerage") ?? throw new ArgumentException("brokerage is required"),
        DataQueueHandlers = Field(form, "dataQueueHandlers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    };

    private static string? Field(IFormCollection form, string name) => form[name].FirstOrDefault() is { Length: > 0 } value ? value : null;

    private static Dictionary<string, string>? Parameters(IFormCollection form) =>
        Field(form, "parameters") is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) : null;

    private static async Task<T> ReadAsync<T>(HttpContext http) =>
        await JsonSerializer.DeserializeAsync<T>(http.Request.Body, ContractJson.Options, http.RequestAborted) ?? throw new JsonException("empty body");

    private static IResult Contract<T>(T value, int status = StatusCodes.Status200OK) =>
        Results.Text(ContractJson.Serialize(value), "application/json", System.Text.Encoding.UTF8, status);

    /// <summary>302 to a short-lived presigned MinIO URL for <paramref name="obj"/>.</summary>
    private static IResult Presign(HttpContext http, ObjectRef obj)
    {
        var s3 = http.RequestServices.GetRequiredService<PresignedUrls>();
        return s3.Configured
            ? Results.Redirect(s3.For(obj))
            : Problems.Unavailable(http, "object-storage", "S3:AccessKey / S3:SecretKey are not configured");
    }
}

/// <summary>Signs GET URLs against the client-reachable MinIO endpoint (no network call).</summary>
public sealed class PresignedUrls
{
    private readonly AmazonS3Client _client;
    private readonly int _seconds;
    private readonly bool _http;

    public bool Configured { get; }

    public PresignedUrls(IConfiguration configuration, IOptions<GatewayOptions> options)
    {
        var url = options.Value.PublicS3Url ?? configuration["S3:ServiceUrl"] ?? "http://127.0.0.1:9000";
        _http = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        _seconds = options.Value.PresignedUrlSeconds;
        Configured = !string.IsNullOrEmpty(configuration["S3:AccessKey"]) && !string.IsNullOrEmpty(configuration["S3:SecretKey"]);
        _client = new AmazonS3Client(configuration["S3:AccessKey"] ?? "", configuration["S3:SecretKey"] ?? "", new AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = !string.Equals(configuration["S3:ForcePathStyle"], "false", StringComparison.OrdinalIgnoreCase),
            AuthenticationRegion = configuration["S3:Region"] ?? "us-east-1",
        });
    }

    public string For(ObjectRef obj) => _client.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = obj.Bucket,
        Key = obj.Key,
        Verb = HttpVerb.GET,
        Expires = DateTime.UtcNow.AddSeconds(_seconds),
        Protocol = _http ? Protocol.HTTP : Protocol.HTTPS,
    });
}
