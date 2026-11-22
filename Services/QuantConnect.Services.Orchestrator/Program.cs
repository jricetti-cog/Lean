/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Defaults;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.RabbitMq;

namespace QuantConnect.Services.Orchestrator
{
    /// <summary>job-orchestrator host: jobs API, outbox relay, status consumer and heartbeat monitor.</summary>
    public static class OrchestratorProgram
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.AddLeanServiceDefaults();
            builder.Services.AddOpenApi();
            builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection(OrchestratorOptions.Section));
            builder.Services.AddSingleton(TimeProvider.System);

            var connectionString = builder.Configuration.GetConnectionString("Jobs")
                ?? throw new InvalidOperationException("ConnectionStrings:Jobs (the PostgreSQL jobs database) is required");
            builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
            builder.Services.AddSingleton<JobStore>();
            builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: new[] { HealthTags.Ready });

            builder.Services.AddLeanRabbitMq(builder.Configuration);
            var managementUri = new Uri(builder.Configuration["RabbitMQ:ManagementUri"] ?? "http://localhost:15672/");
            builder.Services.AddHttpClient<IQueueDepthSource, RabbitMqManagementQueueDepth>((http, sp) =>
                new RabbitMqManagementQueueDepth(http, sp.GetRequiredService<RabbitMqOptions>(), managementUri));
            builder.Services.AddSingleton<QueueMetrics>();

            var s3 = builder.Configuration.GetSection(S3Options.Section).Get<S3Options>() ?? new S3Options();
            builder.Services.AddSingleton<IAlgorithmStore>(new S3AlgorithmStore(s3));

            builder.Services.AddSingleton<OutboxRelay>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
            builder.Services.AddHostedService<StatusConsumer>();
            builder.Services.AddHostedService<HeartbeatMonitor>();
            builder.Services.Configure<LiveDeploymentOptions>(builder.Configuration.GetSection(LiveDeploymentOptions.Section));
            if (builder.Configuration.GetSection(LiveDeploymentOptions.Section).Get<LiveDeploymentOptions>() is { Enabled: true })
            {
                builder.Services.AddSingleton<ILiveContainerRuntime, DockerLiveContainerRuntime>();
                builder.Services.AddHostedService<LiveDeployer>();
            }

            var app = builder.Build();
            await MigrateWithRetryAsync(app.Services.GetRequiredService<JobStore>(), app.Lifetime.ApplicationStopping);
            app.MapOpenApi();

            const long MaxAlgorithmBytes = 64L * 1024 * 1024;
            var jobs = app.MapGroup("/internal/v1/jobs");

            jobs.MapPost("/", async Task<IResult> (HttpRequest http, JobStore store, IAlgorithmStore algorithms, IOptions<OrchestratorOptions> options, CancellationToken ct) =>
            {
                JobRequest request;
                try
                {
                    request = await JsonSerializer.DeserializeAsync<JobRequest>(http.Body, ContractJson.Options, ct) ?? throw new JsonException("empty body");
                }
                catch (JsonException e)
                {
                    return Problems.BadRequest(http, $"invalid JobRequest: {e.Message}");
                }
                var errors = JobStateMachine.Validate(request, algorithms.Bucket);
                foreach (var (field, uploaded) in new[] { ("backtest.algorithm", request.Backtest?.Algorithm), ("live.algorithm", request.Live?.Algorithm),
                             ("optimization.backtest.algorithm", request.Optimization?.Backtest.Algorithm) })
                {
                    if (errors.Count == 0 && uploaded is UploadedAlgorithmRef { Object: var obj } && !await algorithms.ExistsAsync(obj, ct))
                    {
                        errors[field + ".object"] = new() { $"s3://{obj.Bucket}/{obj.Key} does not exist (upload it with POST /internal/v1/algorithms)" };
                    }
                }
                if (errors.Count > 0) return Problems.BadRequest(http, "the job request is invalid", errors);

                var idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault();
                if (idempotencyKey is { Length: > 128 }) return Problems.BadRequest(http, "Idempotency-Key is longer than 128 characters");
                var submittedBy = http.Headers["X-Lean-User"].FirstOrDefault() is { Length: > 0 } user ? user : "anonymous";
                var result = await store.CreateAsync(request, submittedBy, idempotencyKey, ct);
                if (result.Outcome != StoreOutcome.Ok) return Problems.From(http, result.Outcome, result.Detail);
                http.HttpContext.Response.Headers.Location = $"/internal/v1/jobs/{result.Value!.Job.Id}";
                return Contract(result.Value.Job, StatusCodes.Status201Created);
            }).WithName("createJob");

            jobs.MapGet("/", async Task<IResult> (HttpRequest http, JobStore store, string? kind, string? status, Guid? parentJobId, string? submittedBy, string? cursor, int? limit, CancellationToken ct) =>
            {
                JobKind? kindFilter = null;
                JobStatus? statusFilter = null;
                try
                {
                    if (kind != null) kindFilter = JobStateMachine.ParseKind(kind);
                    if (status != null) statusFilter = JobStateMachine.ParseStatus(status);
                }
                catch (JsonException)
                {
                    return Problems.BadRequest(http, "unknown kind or status");
                }
                if (limit is < 1 or > 500) return Problems.BadRequest(http, "limit must be between 1 and 500");
                try
                {
                    return Contract(await store.ListAsync(kindFilter, statusFilter, parentJobId, submittedBy, cursor, limit ?? 50, ct));
                }
                catch (ArgumentException e)
                {
                    return Problems.BadRequest(http, e.Message);
                }
            }).WithName("listJobs");

            jobs.MapGet("/{id:guid}", async Task<IResult> (HttpRequest http, Guid id, JobStore store, CancellationToken ct) =>
                await store.GetAsync(id, ct) is { } job ? Contract(job) : Problems.From(http, StoreOutcome.NotFound, $"job {id} does not exist")).WithName("getJob");

            jobs.MapPost("/{id:guid}/cancel", async Task<IResult> (HttpRequest http, Guid id, JobStore store, CancellationToken ct) =>
            {
                var body = await ReadOptional<CancelRequest>(http, ct);
                if (body is { Reason.Length: > 500 }) return Problems.BadRequest(http, "reason is longer than 500 characters");
                var result = await store.CancelAsync(id, body?.Reason, http.Headers["X-Lean-User"].FirstOrDefault(), ct, body?.Liquidate == true);
                return result.Outcome == StoreOutcome.Ok ? Contract(result.Value!, StatusCodes.Status202Accepted) : Problems.From(http, result.Outcome, result.Detail);
            }).WithName("cancelJob");

            jobs.MapPost("/{id:guid}/liquidate", async Task<IResult> (HttpRequest http, Guid id, JobStore store, CancellationToken ct) =>
            {
                var result = await store.LiquidateAsync(id, http.Headers["X-Lean-User"].FirstOrDefault(), ct);
                return result.Outcome == StoreOutcome.Ok ? Contract(result.Value!, StatusCodes.Status202Accepted) : Problems.From(http, result.Outcome, result.Detail);
            }).WithName("liquidateJob");

            jobs.MapPost("/{id:guid}/attempts", async Task<IResult> (HttpRequest http, Guid id, JobStore store, CancellationToken ct) =>
            {
                var body = await ReadOptional<StartAttemptRequest>(http, ct);
                if (body is not { WorkerId.Length: > 0 } || body.DeliveryCount < 0) return Problems.BadRequest(http, "workerId and deliveryCount (>= 0) are required");
                var result = await store.StartAttemptAsync(id, body.WorkerId, body.DeliveryCount, body.Attempt, ct);
                return result.Outcome == StoreOutcome.Ok ? Contract(result.Value!, StatusCodes.Status201Created) : Problems.From(http, result.Outcome, result.Detail);
            }).WithName("startAttempt");

            jobs.MapPost("/{id:guid}/lease", async Task<IResult> (HttpRequest http, Guid id, JobStore store, CancellationToken ct) =>
            {
                var body = await ReadOptional<AcquireLeaseRequest>(http, ct);
                if (body is not { WorkerId.Length: > 0 }) return Problems.BadRequest(http, "workerId is required");
                var result = await store.AcquireLeaseAsync(id, body.WorkerId, ct);
                return result.Outcome == StoreOutcome.Ok ? Contract(result.Value!, StatusCodes.Status201Created) : Problems.From(http, result.Outcome, result.Detail);
            }).WithName("acquireLease");

            app.MapPost("/internal/v1/algorithms", async Task<IResult> (HttpRequest http, IAlgorithmStore algorithms, string? fileName, CancellationToken ct) =>
            {
                var name = IAlgorithmStore.SanitizeFileName(fileName);
                if (name == null) return Problems.BadRequest(http, "fileName query parameter is required ([A-Za-z0-9._-], at most 200 characters)");
                if (http.ContentLength > MaxAlgorithmBytes) return Problems.BadRequest(http, $"algorithm packages are limited to {MaxAlgorithmBytes} bytes");
                using var buffer = new MemoryStream();
                await http.Body.CopyToAsync(buffer, ct);
                if (buffer.Length == 0 || buffer.Length > MaxAlgorithmBytes) return Problems.BadRequest(http, "the body must be the algorithm source or package");
                var contentType = System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(http.ContentType, out var media) && media.MediaType != null ? media.MediaType : "application/octet-stream";
                var obj = await algorithms.PutAsync(name, buffer.ToArray(), contentType, ct);
                return Contract(obj, StatusCodes.Status201Created);
            }).WithName("uploadAlgorithm");

            app.MapGet("/internal/v1/queues", async Task<IResult> (HttpRequest http, QueueMetrics metrics, CancellationToken ct) =>
            {
                try
                {
                    return Contract(await metrics.GetHintsAsync(ct));
                }
                catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
                {
                    return Problems.Unavailable(http, $"queue depth unavailable: {e.Message}");
                }
            }).WithName("getQueueHints");

            app.MapGet("/metrics", async (QueueMetrics metrics, JobStore store, CancellationToken ct) =>
                Results.Text(await metrics.PrometheusAsync(store, ct), "text/plain; version=0.0.4")).WithName("metrics");

            await app.RunAsync();
        }
        private static IResult Contract<T>(T value, int status = StatusCodes.Status200OK) => Results.Text(ContractJson.Serialize(value), "application/json", statusCode: status);

        private static async Task<T?> ReadOptional<T>(HttpRequest http, CancellationToken ct) where T : class
        {
            if (http.ContentLength == 0) return null;
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(http.Body, ContractJson.Options, ct);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static async Task MigrateWithRetryAsync(JobStore store, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await store.MigrateAsync(ct);
                    return;
                }
                catch (NpgsqlException) when (attempt < 30 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
    }
}

namespace QuantConnect.Services.Orchestrator
{
    public sealed record CancelRequest(string? Reason, bool? Liquidate = null);

    /// <summary><c>startAttempt</c> body; <c>attempt</c> is the attempt in the dispatch being taken (drops superseded dispatches).</summary>
    public sealed record StartAttemptRequest(string WorkerId, long DeliveryCount, int? Attempt);

    public sealed record AcquireLeaseRequest(string WorkerId);

    internal static class Problems
    {
        public static IResult BadRequest(HttpRequest http, string detail, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? errors = null) =>
            Write(http, StatusCodes.Status400BadRequest, "Bad request", detail, errors);

        public static IResult Unavailable(HttpRequest http, string detail) => Write(http, StatusCodes.Status503ServiceUnavailable, "Unavailable", detail);

        public static IResult From(HttpRequest http, StoreOutcome outcome, string? detail) => outcome switch
        {
            StoreOutcome.NotFound => Write(http, StatusCodes.Status404NotFound, "Not found", detail ?? "job does not exist"),
            StoreOutcome.Conflict => Write(http, StatusCodes.Status409Conflict, "Conflict", detail),
            StoreOutcome.Exhausted => Write(http, StatusCodes.Status410Gone, "Gone", detail),
            _ => Write(http, StatusCodes.Status400BadRequest, "Bad request", detail),
        };

        private static IResult Write(HttpRequest http, int status, string title, string? detail,
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? errors = null) =>
            Results.Text(ContractJson.Serialize(new Problem
            {
                Type = $"https://httpstatuses.io/{status}", Title = title, Status = status, Detail = detail, Instance = http.Path,
                TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(), Errors = errors,
            }), "application/problem+json", statusCode: status);
    }

    internal sealed class PostgresHealthCheck : IHealthCheck
    {
        private readonly JobStore _store;

        public PostgresHealthCheck(JobStore store) => _store = store;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await _store.PingAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("jobs database unreachable");
    }
}
