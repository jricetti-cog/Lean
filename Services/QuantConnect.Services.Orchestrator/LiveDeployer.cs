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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.Orchestrator;

/// <summary><c>Orchestrator:LiveDeployment</c>: how live deployments get their own live-worker container.</summary>
public sealed class LiveDeploymentOptions
{
    public const string Section = "Orchestrator:LiveDeployment";

    /// <summary><c>docker</c> starts one container per deployment through the Docker Engine API; <c>none</c> leaves <c>jobs.live</c> to externally run workers.</summary>
    public string Mode { get; set; } = "none";
    public string DockerSocket { get; set; } = "/var/run/docker.sock";
    public string Image { get; set; } = "lean/live-worker:dev";
    /// <summary>Docker network the container joins (the compose <c>lean</c> network).</summary>
    public string? Network { get; set; }
    /// <summary>Named volume holding the market data, mounted read-only at <see cref="DataMountPath"/> (ADR 0004).</summary>
    public string? DataVolume { get; set; }
    public string DataMountPath { get; set; } = "/Lean/Data";
    public string ContainerPrefix { get; set; } = "lean-live-";
    /// <summary>Orchestrator and results base URLs as seen from the container.</summary>
    public string OrchestratorUri { get; set; } = "http://job-orchestrator:8080/";
    public string ResultsUri { get; set; } = "http://results:8080/";
    /// <summary>Comma-separated variables of this process copied into each container (broker, MinIO and OTLP export settings).</summary>
    public string ForwardEnvironment { get; set; } =
        "RabbitMQ__Uri,S3__ServiceUrl,S3__ForcePathStyle,S3__AccessKey,S3__SecretKey,S3__AlgorithmsBucket,S3__ResultsBucket,S3__ReportsBucket,LEAN_DATA_FOLDER," +
        "OTEL_EXPORTER_OTLP_ENDPOINT,OTEL_EXPORTER_OTLP_PROTOCOL,OTEL_METRIC_EXPORT_INTERVAL";
    /// <summary>Docker restarts a crashed live-worker this many times; it restores the portfolio from results on every start.</summary>
    public int MaxRestarts { get; set; } = 5;
    /// <summary>Container memory limit in MB (Docker <c>Memory</c>); 0 leaves it unlimited.</summary>
    public int MemoryMb { get; set; }
    /// <summary>Container CPU limit in cores (Docker <c>NanoCpus</c>); 0 leaves it unlimited.</summary>
    public double Cpus { get; set; }

    public bool Enabled => string.Equals(Mode, "docker", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A container as far as live deployment cares.</summary>
public sealed record LiveContainer(string Id, string Name, bool Running, int ExitCode, IReadOnlyDictionary<string, string> Labels);

/// <summary>The container runtime that hosts live-workers (Docker Engine API in compose; a Deployment on Kubernetes later).</summary>
public interface ILiveContainerRuntime
{
    Task<LiveContainer?> InspectAsync(string name, CancellationToken ct);
    Task<string> CreateAsync(string name, JsonObject spec, CancellationToken ct);
    Task StartAsync(string id, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<LiveContainer>> ListAsync(string label, CancellationToken ct);
}

/// <summary>Docker Engine API over the unix socket.</summary>
public sealed class DockerLiveContainerRuntime : ILiveContainerRuntime, IDisposable
{
    private readonly HttpClient _http;

    public DockerLiveContainerRuntime(IOptions<LiveDeploymentOptions> options)
    {
        var socket = options.Value.DockerSocket;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await unix.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                    return new NetworkStream(unix, ownsSocket: true);
                }
                catch
                {
                    unix.Dispose();
                    throw;
                }
            },
        })
        { BaseAddress = new Uri("http://docker/") };
    }

    public async Task<LiveContainer?> InspectAsync(string name, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"containers/{Uri.EscapeDataString(name)}/json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, "inspect", ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
        return new LiveContainer((string)body["Id"]!, ((string?)body["Name"] ?? name).TrimStart('/'), (bool?)body["State"]?["Running"] ?? false,
            (int?)body["State"]?["ExitCode"] ?? 0, Labels(body["Config"]?["Labels"]));
    }

    public async Task<string> CreateAsync(string name, JsonObject spec, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"containers/create?name={Uri.EscapeDataString(name)}", spec, ct);
        await EnsureAsync(response, "create", ct);
        return (string)JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["Id"]!;
    }

    public async Task StartAsync(string id, CancellationToken ct)
    {
        using var response = await _http.PostAsync($"containers/{id}/start", null, ct);
        if (response.StatusCode != HttpStatusCode.NotModified) await EnsureAsync(response, "start", ct);
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"containers/{id}?force=true", ct);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureAsync(response, "remove", ct);
    }

    public async Task<IReadOnlyList<LiveContainer>> ListAsync(string label, CancellationToken ct)
    {
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = new[] { label } }));
        using var response = await _http.GetAsync($"containers/json?all=true&filters={filters}", ct);
        await EnsureAsync(response, "list", ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsArray().OfType<JsonObject>().Select(c => new LiveContainer(
            (string)c["Id"]!, ((string?)c["Names"]?[0] ?? "").TrimStart('/'), (string?)c["State"] == "running",
            ParseExitCode((string?)c["Status"]), Labels(c["Labels"]))).ToList();
    }

    private static int ParseExitCode(string? status) =>
        status != null && status.StartsWith("Exited (", StringComparison.Ordinal) && int.TryParse(status[8..status.IndexOf(')')], out var code) ? code : 0;

    private static IReadOnlyDictionary<string, string> Labels(JsonNode? labels) =>
        labels is JsonObject o ? o.ToDictionary(p => p.Key, p => (string?)p.Value ?? "") : new Dictionary<string, string>();

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"docker {what}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Consumes <c>jobs.live</c> and gives every live deployment its own live-worker container (<c>lean-live-&lt;jobId&gt;</c>),
/// started through the container runtime with the dispatch in its environment; the delivery is acked once the container
/// runs. The worker itself takes the lease (ADR 0002), so a redelivered dispatch of the same attempt only makes sure its
/// container is up, a new attempt replaces the container, and a cancelled or finished job is dropped. Docker restarts a
/// crashed worker (<c>on-failure</c>); exited containers of finished deployments are removed by <see cref="SweepAsync"/>.
/// </summary>
public sealed class LiveDeployer : BackgroundService
{
    public const string RoleLabel = "lean.role";
    public const string JobLabel = "lean.job-id";
    public const string AttemptLabel = "lean.attempt";
    public const string Role = "live-worker";

    private readonly IMessageConsumer _consumer;
    private readonly ILiveContainerRuntime _runtime;
    private readonly JobStore? _store;
    private readonly LiveDeploymentOptions _options;
    private readonly ILogger<LiveDeployer> _logger;
    private readonly Func<string, string?> _environment;

    public LiveDeployer(IMessageConsumer consumer, ILiveContainerRuntime runtime, JobStore store, IOptions<LiveDeploymentOptions> options, ILogger<LiveDeployer> logger)
        : this(consumer, runtime, store, options.Value, logger, Environment.GetEnvironmentVariable)
    {
    }

    public LiveDeployer(IMessageConsumer consumer, ILiveContainerRuntime runtime, JobStore? store, LiveDeploymentOptions options, ILogger<LiveDeployer> logger,
        Func<string, string?> environment)
    {
        _consumer = consumer;
        _runtime = runtime;
        _store = store;
        _options = options;
        _logger = logger;
        _environment = environment;
    }

    public string ContainerName(Guid jobId) => $"{_options.ContainerPrefix}{jobId:D}";

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken ct)
    {
        if (message.Envelope.Type != MessageTypes.LiveRequested) return MessageDisposition.DeadLetter;
        JobSubmitted job;
        try
        {
            job = ContractJson.Deserialize<JobSubmitted>(message.Body.Span);
        }
        catch (JsonException e)
        {
            _logger.LogError(e, "Dead-lettering {MessageId}: invalid JobSubmitted", message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }
        if (job is not { Kind: JobKind.Live, Live: not null }) return MessageDisposition.DeadLetter;

        if (_store != null && await _store.GetAsync(job.JobId, ct) is { } current
            && (JobStateMachine.IsTerminal(current.Status) || current.Attempt > job.Attempt))
        {
            _logger.LogInformation("Dropping live dispatch {JobId} attempt {Attempt}: job is {Status} at attempt {Current}", job.JobId, job.Attempt,
                JobStateMachine.ToWire(current.Status), current.Attempt);
            return MessageDisposition.Ack;
        }

        var name = ContainerName(job.JobId);
        var existing = await _runtime.InspectAsync(name, ct);
        if (existing != null && existing.Labels.GetValueOrDefault(AttemptLabel) == job.Attempt.ToString())
        {
            if (!existing.Running)
            {
                await _runtime.StartAsync(existing.Id, ct);
            }
            _logger.LogInformation("Live deployment {JobId} attempt {Attempt}: container {Name} already exists", job.JobId, job.Attempt, name);
            return MessageDisposition.Ack;
        }
        if (existing != null)
        {
            _logger.LogWarning("Live deployment {JobId}: replacing container {Name} of attempt {Old} with attempt {Attempt}", job.JobId, name,
                existing.Labels.GetValueOrDefault(AttemptLabel), job.Attempt);
            await _runtime.RemoveAsync(existing.Id, ct);
        }
        var id = await _runtime.CreateAsync(name, ContainerSpec(job), ct);
        await _runtime.StartAsync(id, ct);
        _logger.LogInformation("Live deployment {JobId} attempt {Attempt}: started container {Name} ({Image})", job.JobId, job.Attempt, name, _options.Image);
        return MessageDisposition.Ack;
    }

    /// <summary>Docker <c>containers/create</c> body for one deployment.</summary>
    public JsonObject ContainerSpec(JobSubmitted job)
    {
        var name = ContainerName(job.JobId);
        var env = new JsonArray();
        foreach (var variable in _options.ForwardEnvironment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (_environment(variable) is { } value) env.Add($"{variable}={value}");
        }
        env.Add($"LiveWorker__WorkerId={name}");
        env.Add($"LiveWorker__OrchestratorUri={_options.OrchestratorUri}");
        env.Add($"LiveWorker__ResultsUri={_options.ResultsUri}");
        env.Add($"LiveWorker__Dispatch={ContractJson.Serialize(job)}");
        var host = new JsonObject
        {
            ["RestartPolicy"] = new JsonObject { ["Name"] = "on-failure", ["MaximumRetryCount"] = _options.MaxRestarts },
        };
        if (_options.Network is { Length: > 0 } network) host["NetworkMode"] = network;
        if (_options.MemoryMb > 0) host["Memory"] = (long)_options.MemoryMb * 1024 * 1024;
        if (_options.Cpus > 0) host["NanoCpus"] = (long)(_options.Cpus * 1_000_000_000);
        if (_options.DataVolume is { Length: > 0 } volume) host["Binds"] = new JsonArray($"{volume}:{_options.DataMountPath}:ro");
        return new JsonObject
        {
            ["Image"] = _options.Image,
            ["Hostname"] = name.Length > 63 ? name[..63] : name,
            ["Env"] = env,
            ["Labels"] = new JsonObject { [RoleLabel] = Role, [JobLabel] = job.JobId.ToString("D"), [AttemptLabel] = job.Attempt.ToString() },
            ["HostConfig"] = host,
        };
    }

    /// <summary>Removes live-worker containers that exited cleanly (the deployment was stopped or ended); crashed ones stay for Docker to restart.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var removed = 0;
        foreach (var container in await _runtime.ListAsync($"{RoleLabel}={Role}", ct))
        {
            if (!container.Running && container.ExitCode == 0)
            {
                await _runtime.RemoveAsync(container.Id, ct);
                removed++;
                _logger.LogInformation("Removed finished live-worker container {Name}", container.Name);
            }
        }
        return removed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IAsyncDisposable? subscription = null;
        while (subscription == null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                subscription = await _consumer.ConsumeAsync(Queues.Live, async (message, ct) =>
                {
                    try
                    {
                        return await HandleAsync(message, ct);
                    }
                    catch (Exception e) when (e is HttpRequestException or SocketException or System.IO.IOException)
                    {
                        _logger.LogWarning(e, "Container runtime unavailable for live dispatch {MessageId}; requeueing", message.Envelope.MessageId);
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        return MessageDisposition.Retry;
                    }
                }, new ConsumerOptions { Prefetch = 1 }, stoppingToken);
                _logger.LogInformation("Live deployer consuming {Queue}: one {Image} container per deployment", Queues.Live, _options.Image);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "Cannot consume {Queue} yet; retrying", Queues.Live);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug(e, "Live-worker sweep failed");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (subscription != null) await subscription.DisposeAsync();
        }
    }
}
