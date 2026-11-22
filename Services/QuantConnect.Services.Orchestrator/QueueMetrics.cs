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
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.RabbitMq;

namespace QuantConnect.Services.Orchestrator;

/// <summary>Ready/unacked/dead counts of one queue.</summary>
public sealed record QueueDepth(string Queue, long Ready, long Unacked, long? Dead);

/// <summary>Reads queue depth from the broker.</summary>
public interface IQueueDepthSource
{
    Task<QueueDepth> GetAsync(string queue, CancellationToken ct);
}

/// <summary>Queue depth from the RabbitMQ management API (<c>RabbitMQ:ManagementUri</c>), as the user/vhost of <c>RabbitMQ:Uri</c>.</summary>
public sealed class RabbitMqManagementQueueDepth : IQueueDepthSource
{
    private readonly HttpClient _http;
    private readonly string _vhost;

    public RabbitMqManagementQueueDepth(HttpClient http, RabbitMqOptions rabbit, Uri managementUri)
    {
        _http = http;
        _http.BaseAddress = managementUri;
        var user = Uri.UnescapeDataString(rabbit.Uri.UserInfo);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user)));
        var vhost = Uri.UnescapeDataString(rabbit.Uri.AbsolutePath.TrimStart('/'));
        _vhost = vhost.Length == 0 ? "/" : vhost;
    }

    public async Task<QueueDepth> GetAsync(string queue, CancellationToken ct)
    {
        var main = await Read(queue, ct);
        QueueStats? dead = null;
        try { dead = await Read(Queues.DeadLetter(queue), ct); } catch (HttpRequestException) { }
        return new QueueDepth(queue, main.Ready, main.Unacked, dead?.Ready);
    }

    private async Task<QueueStats> Read(string queue, CancellationToken ct) =>
        await _http.GetFromJsonAsync<QueueStats>(
            $"api/queues/{Uri.EscapeDataString(_vhost)}/{Uri.EscapeDataString(queue)}?columns=messages_ready,messages_unacknowledged", ct)
        ?? throw new HttpRequestException($"empty stats for {queue}");

    private sealed record QueueStats
    {
        [JsonPropertyName("messages_ready")] public long Ready { get; init; }
        [JsonPropertyName("messages_unacknowledged")] public long Unacked { get; init; }
    }
}

/// <summary>
/// Queue-depth scaling hints for the dispatch queues the orchestrator feeds, cached briefly, also exported as the
/// <c>lean.orchestrator.queue.*</c> OpenTelemetry gauges and the Prometheus text of <c>GET /metrics</c>.
/// </summary>
public sealed class QueueMetrics
{
    public const string MeterName = "QuantConnect.Services.Orchestrator";

    public static readonly JobKind[] DispatchKinds = { JobKind.Backtest, JobKind.Live, JobKind.Optimization, JobKind.Report };

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private readonly IQueueDepthSource _source;
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<QueueMetrics> _logger;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private List<QueueHint> _hints = new();
    private DateTimeOffset _hintsAt = DateTimeOffset.MinValue;

    public QueueMetrics(IQueueDepthSource source, IOptions<OrchestratorOptions> options, TimeProvider time, ILogger<QueueMetrics> logger, IMeterFactory? meters = null)
    {
        _source = source;
        _options = options.Value;
        _time = time;
        _logger = logger;
        var meter = meters?.Create(MeterName) ?? new Meter(MeterName);
        meter.CreateObservableGauge("lean.orchestrator.queue.ready", () => Measure(h => h.Ready), "{message}", "Ready messages per dispatch queue");
        meter.CreateObservableGauge("lean.orchestrator.queue.unacked", () => Measure(h => h.Unacked), "{message}", "Unacknowledged (in-flight) messages per dispatch queue");
        meter.CreateObservableGauge("lean.orchestrator.queue.suggested_replicas", () => Measure(h => h.SuggestedReplicas), "{replica}", "Suggested worker replicas per dispatch queue");
    }

    private IEnumerable<Measurement<long>> Measure(Func<QueueHint, long> value) =>
        _hints.Select(h => new Measurement<long>(value(h), new KeyValuePair<string, object?>("queue", h.Queue)));

    public int SuggestReplicas(JobKind kind, long ready, long unacked)
    {
        var demand = ready + unacked;
        var replicas = kind == JobKind.Live ? demand : (long)Math.Ceiling(demand / (double)Math.Max(1, _options.JobsPerReplica));
        return (int)Math.Clamp(replicas, _options.MinReplicas, Math.Max(_options.MinReplicas, _options.MaxReplicas));
    }

    public async Task<List<QueueHint>> GetHintsAsync(CancellationToken ct)
    {
        if (_time.GetUtcNow() - _hintsAt < CacheFor) return _hints;
        await _refresh.WaitAsync(ct);
        try
        {
            if (_time.GetUtcNow() - _hintsAt < CacheFor) return _hints;
            var hints = new List<QueueHint>();
            foreach (var kind in DispatchKinds)
            {
                var depth = await _source.GetAsync(Queues.Dispatch(kind), ct);
                hints.Add(new QueueHint
                {
                    Queue = depth.Queue, Ready = depth.Ready, Unacked = depth.Unacked, Dead = depth.Dead,
                    SuggestedReplicas = SuggestReplicas(kind, depth.Ready, depth.Unacked),
                });
            }
            _hints = hints;
            _hintsAt = _time.GetUtcNow();
            return hints;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Prometheus text exposition of queue depth, job counts and outbox backlog.</summary>
    public async Task<string> PrometheusAsync(JobStore store, CancellationToken ct)
    {
        var text = new StringBuilder();
        void Line(string s) => text.Append(s).Append('\n');
        try
        {
            var hints = await GetHintsAsync(ct);
            Line("# HELP lean_queue_ready Ready messages per dispatch queue.");
            Line("# TYPE lean_queue_ready gauge");
            foreach (var h in hints) Line($"lean_queue_ready{{queue=\"{h.Queue}\"}} {h.Ready}");
            Line("# HELP lean_queue_unacked Unacknowledged messages per dispatch queue.");
            Line("# TYPE lean_queue_unacked gauge");
            foreach (var h in hints) Line($"lean_queue_unacked{{queue=\"{h.Queue}\"}} {h.Unacked}");
            Line("# HELP lean_queue_dead Dead-lettered messages per dispatch queue.");
            Line("# TYPE lean_queue_dead gauge");
            foreach (var h in hints) Line($"lean_queue_dead{{queue=\"{h.Queue}\"}} {h.Dead ?? 0}");
            Line("# HELP lean_queue_suggested_replicas Suggested worker replicas per dispatch queue.");
            Line("# TYPE lean_queue_suggested_replicas gauge");
            foreach (var h in hints) Line($"lean_queue_suggested_replicas{{queue=\"{h.Queue}\"}} {h.SuggestedReplicas}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(e, "Queue depth unavailable");
        }
        Line("# HELP lean_jobs Jobs per kind and status.");
        Line("# TYPE lean_jobs gauge");
        foreach (var (kind, status, count) in await store.CountsAsync(ct)) Line($"lean_jobs{{kind=\"{kind}\",status=\"{status}\"}} {count.ToString(CultureInfo.InvariantCulture)}");
        Line("# HELP lean_outbox_unsent Outbox rows not yet confirmed by the broker.");
        Line("# TYPE lean_outbox_unsent gauge");
        Line($"lean_outbox_unsent {await store.OutboxBacklogAsync(ct)}");
        return text.ToString();
    }
}
