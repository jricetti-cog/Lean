using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.RabbitMq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace QuantConnect.Services.Gateway;

/// <summary>One message from <c>lean.events</c> for a job: AMQP type, routing key, headers and raw body.</summary>
public sealed record JobBusEvent(string Type, string RoutingKey, MessageHeaders Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary><c>job.&lt;kind&gt;.&lt;jobId&gt;.&lt;event&gt;.v1</c> → <c>&lt;event&gt;</c>.</summary>
    public string Event => RoutingKey.Split('.') is { Length: >= 4 } parts ? parts[3] : MessageTypes.EventSegment(Type) ?? "result";
}

/// <summary>Per-request subscription to every bus message of one job (ADR 0002).</summary>
public interface IJobEventSource
{
    /// <summary>Starts relaying the job's messages to <paramref name="onEvent"/>; disposing deletes the queue.</summary>
    Task<IAsyncDisposable> SubscribeAsync(Guid jobId, Func<JobBusEvent, ValueTask> onEvent, CancellationToken cancellationToken);
}

public sealed class EventStreamOptions
{
    public int KeepAliveSeconds { get; set; } = 15;
    /// <summary>Log lines buffered per client before the oldest are dropped.</summary>
    public int LogBuffer { get; set; } = 500;
    /// <summary>Order events buffered per client before the oldest are dropped.</summary>
    public int OrderBuffer { get; set; } = 1000;
}

/// <summary>
/// Declares a server-named, exclusive, auto-delete queue per SSE request bound to <c>job.*.&lt;jobId&gt;.#</c>
/// on <c>lean.events</c> (topology: <c>sse.*</c> policy caps it at 64 MiB, drop-head). Auto-ack: the stream is
/// best-effort and the REST endpoints stay authoritative.
/// </summary>
public sealed class RabbitMqJobEventSource : IJobEventSource, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqJobEventSource> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly string _instance = (Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName).ToLowerInvariant();
    private IConnection? _connection;

    public RabbitMqJobEventSource(IOptions<RabbitMqOptions> options, ILogger<RabbitMqJobEventSource> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public static string QueueName(string instance) => $"sse.{instance}.{Guid.NewGuid():N}";
    public static string BindingKey(Guid jobId) => $"job.*.{jobId:D}.#";

    public async Task<IAsyncDisposable> SubscribeAsync(Guid jobId, Func<JobBusEvent, ValueTask> onEvent, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await channel.QueueDeclareAsync(QueueName(_instance), durable: false, exclusive: true, autoDelete: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueBindAsync(queue.QueueName, Exchanges.Events, BindingKey(jobId), cancellationToken: cancellationToken).ConfigureAwait(false);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) =>
            {
                try
                {
                    var props = delivery.BasicProperties;
                    var headers = AmqpHeaders.FromAmqp(props.Headers);
                    // Keep the publisher's trace: one consumer span per relayed message, parented on its traceparent.
                    var parent = headers.TraceParent != null && ActivityContext.TryParse(headers.TraceParent, headers.TraceState, isRemote: true, out var context) ? context : default;
                    using var activity = MessagingTelemetry.Source.StartActivity($"{queue.QueueName} relay", ActivityKind.Consumer, parent);
                    activity?.SetTag("messaging.system", "rabbitmq");
                    activity?.SetTag("messaging.destination.name", Exchanges.Events);
                    activity?.SetTag("messaging.rabbitmq.destination.routing_key", delivery.RoutingKey);
                    activity?.SetTag("lean.job_id", jobId.ToString("D"));
                    await onEvent(new JobBusEvent(props.Type ?? string.Empty, delivery.RoutingKey, headers, delivery.Body.ToArray())).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "Dropped malformed event {RoutingKey}", delivery.RoutingKey);
                }
            };
            await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("SSE queue {Queue} bound to {Binding}", queue.QueueName, BindingKey(jobId));
            return new Subscription(channel);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open) return open;
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } current) return current;
            if (_connection != null) await _connection.DisposeAsync().ConfigureAwait(false);
            var factory = new ConnectionFactory
            {
                Uri = _options.Uri,
                AutomaticRecoveryEnabled = true,
                ClientProvidedName = (_options.ClientName ?? "api-gateway") + ":sse",
            };
            return _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Subscription(IChannel channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Closing the only consumer's channel deletes the exclusive auto-delete queue.
                await channel.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // connection already gone: the broker drops exclusive queues with it
            }
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>One SSE frame waiting to be written.</summary>
public sealed record SseFrame(string Event, string Type, Guid JobId, int Attempt, long? Sequence, ReadOnlyMemory<byte> Body, int Dropped = 0, string? LastEventId = null, string? TraceParent = null)
{
    public string Id => $"{Attempt}:{Sequence ?? 0}";
}

/// <summary>
/// Per-client backpressure between the bus consumer and a (possibly slow) HTTP response: <c>progress</c>,
/// <c>heartbeat</c> and <c>result</c> (chart/statistics updates) are latest-wins, <c>log</c> and <c>order</c> are
/// bounded FIFOs that drop their oldest entry, and <c>status</c>/<c>error</c> are never dropped. Every frame
/// carries how many earlier frames of its event were dropped, so the client knows to reload that data over REST.
/// </summary>
public sealed class JobEventBuffer
{
    private static readonly HashSet<string> LatestWins = new(StringComparer.Ordinal) { "progress", "heartbeat", "result" };

    private readonly LinkedList<SseFrame> _frames = new();
    private readonly Dictionary<string, LinkedListNode<SseFrame>> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _dropped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _capacity;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _lock = new();

    public JobEventBuffer(EventStreamOptions options)
    {
        _capacity = new(StringComparer.Ordinal) { ["log"] = Math.Max(1, options.LogBuffer), ["order"] = Math.Max(1, options.OrderBuffer) };
    }

    public int Count
    {
        get { lock (_lock) return _frames.Count; }
    }

    public void Add(SseFrame frame)
    {
        lock (_lock)
        {
            var key = frame.Event;
            if (LatestWins.Contains(key))
            {
                if (_latest.Remove(key, out var stale))
                {
                    _frames.Remove(stale);
                    _dropped[key] = _dropped.GetValueOrDefault(key) + 1 + stale.Value.Dropped;
                }
                _latest[key] = _frames.AddLast(frame with { Dropped = _dropped.GetValueOrDefault(key) });
                _dropped.Remove(key);
            }
            else if (_capacity.TryGetValue(key, out var capacity))
            {
                if (_counts.GetValueOrDefault(key) >= capacity)
                {
                    var oldest = _frames.First;
                    while (oldest != null && oldest.Value.Event != key) oldest = oldest.Next;
                    if (oldest != null)
                    {
                        _frames.Remove(oldest);
                        _counts[key]--;
                        _dropped[key] = _dropped.GetValueOrDefault(key) + 1 + oldest.Value.Dropped;
                    }
                }
                _frames.AddLast(frame with { Dropped = _dropped.GetValueOrDefault(key) });
                _dropped.Remove(key);
                _counts[key] = _counts.GetValueOrDefault(key) + 1;
            }
            else
            {
                _frames.AddLast(frame);
            }
        }
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    /// <summary>Waits up to <paramref name="wait"/> for frames and takes all of them, oldest first.</summary>
    public async Task<IReadOnlyList<SseFrame>> TakeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (Count == 0) await _signal.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            var taken = _frames.ToList();
            _frames.Clear();
            _latest.Clear();
            _counts.Clear();
            return taken;
        }
    }
}

/// <summary><c>GET /api/v1/jobs/{id}/events</c>: authorised, per-request SSE stream of one job's bus messages.</summary>
public static class JobEventsEndpoint
{
    public const string SnapshotType = "gateway.v1.Snapshot";
    public static readonly string[] EventNames = ["snapshot", "status", "heartbeat", "progress", "result", "log", "order", "error"];
    private static readonly HashSet<string> TerminalTypes = new(StringComparer.Ordinal) { MessageTypes.JobCompleted, MessageTypes.JobFailed, MessageTypes.JobCancelled };
    private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");

    public static async Task<IResult> StreamAsync(Guid id, HttpContext http, IJobEventSource source, IOptions<GatewayOptions> gatewayOptions)
    {
        var options = gatewayOptions.Value.Events;
        var requested = ParseFilter(http.Request.Query["events"]);
        if (requested == null)
        {
            return Problems.BadRequest(http, "events must be a comma-separated subset of " + string.Join(",", EventNames));
        }
        var buffer = new JobEventBuffer(options);
        var lastEventId = http.Request.Headers["Last-Event-ID"].FirstOrDefault();
        var cancellation = http.RequestAborted;

        // Subscribe before reading the snapshot so nothing published in between is missed.
        IAsyncDisposable subscription;
        try
        {
            subscription = await source.SubscribeAsync(id, message =>
            {
                var name = message.Event;
                if (requested.Contains(name))
                {
                    buffer.Add(new SseFrame(name, message.Type, message.Headers.JobId, message.Headers.Attempt, message.Headers.Sequence, message.Body, TraceParent: message.Headers.TraceParent));
                }
                return ValueTask.CompletedTask;
            }, cancellation);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(JobEventsEndpoint)).LogWarning(e, "Cannot subscribe to job {JobId} events", id);
            return Problems.Unavailable(http, "rabbitmq", "the event bus is unavailable; poll the REST endpoints instead");
        }

        await using (subscription)
        {
            var (job, failure) = await http.GetContractAsync<Job>(GatewayProxy.Orchestrator, $"internal/v1/jobs/{id:D}");
            if (failure != null) return failure;
            if (!http.User.IsAdmin() && job!.SubmittedBy != http.User.User()) return Problems.NotFound(http, $"job {id:D} not found");

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            var writer = http.Response.BodyWriter;
            if (requested.Contains("snapshot"))
            {
                var snapshot = new SseFrame("snapshot", SnapshotType, job!.Id, job.Attempt, null, ContractJson.SerializeToUtf8Bytes(job), LastEventId: lastEventId);
                await WriteAsync(writer, snapshot, cancellation);
            }
            await writer.FlushAsync(cancellation);
            if (job!.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Stale) return Results.Empty;

            var keepAlive = TimeSpan.FromSeconds(Math.Max(1, options.KeepAliveSeconds));
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var frames = await buffer.TakeAsync(keepAlive, cancellation);
                    if (frames.Count == 0)
                    {
                        await writer.WriteAsync(KeepAlive, cancellation);
                    }
                    var terminal = false;
                    foreach (var frame in frames)
                    {
                        await WriteAsync(writer, frame, cancellation);
                        terminal |= TerminalTypes.Contains(frame.Type);
                    }
                    await writer.FlushAsync(cancellation);
                    if (terminal) break;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // client went away
            }
            return Results.Empty;
        }
    }

    /// <summary>Default: all but <c>heartbeat</c>; <c>null</c> when the filter names an unknown event.</summary>
    public static HashSet<string>? ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return new HashSet<string>(EventNames.Where(e => e != "heartbeat"), StringComparer.Ordinal);
        var names = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.All(EventNames.Contains) ? new HashSet<string>(names, StringComparer.Ordinal) : null;
    }

    /// <summary><c>id</c>, <c>event</c> and a single-line <c>data</c> holding the <c>SseEvent</c> JSON.</summary>
    public static byte[] Format(SseFrame frame)
    {
        var data = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(data))
        {
            json.WriteStartObject();
            json.WriteString("event", frame.Event);
            json.WriteString("type", frame.Type);
            json.WriteString("jobId", frame.JobId.ToString("D"));
            json.WriteNumber("attempt", frame.Attempt);
            if (frame.Sequence is { } sequence) json.WriteNumber("sequence", sequence);
            if (frame.Dropped > 0) json.WriteNumber("dropped", frame.Dropped);
            if (frame.LastEventId != null) json.WriteString("lastEventId", frame.LastEventId);
            if (frame.TraceParent != null) json.WriteString("traceparent", frame.TraceParent);
            json.WritePropertyName("body");
            try
            {
                // Re-serialising compacts the body onto one line, as SSE data lines cannot contain newlines.
                using var body = JsonDocument.Parse(frame.Body);
                body.WriteTo(json);
            }
            catch (JsonException)
            {
                json.WriteStringValue(Encoding.UTF8.GetString(frame.Body.Span));
            }
            json.WriteEndObject();
        }
        var header = Encoding.UTF8.GetBytes($"id: {frame.Id}\nevent: {frame.Event}\ndata: ");
        var result = new byte[header.Length + data.WrittenCount + 2];
        header.CopyTo(result, 0);
        data.WrittenSpan.CopyTo(result.AsSpan(header.Length));
        result[^2] = (byte)'\n';
        result[^1] = (byte)'\n';
        return result;
    }

    private static async ValueTask WriteAsync(System.IO.Pipelines.PipeWriter writer, SseFrame frame, CancellationToken cancellationToken) =>
        await writer.WriteAsync(Format(frame), cancellationToken);
}
