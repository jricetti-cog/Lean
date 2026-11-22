using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>
/// <see cref="QuantConnect.Messaging.Messaging"/> that also publishes every packet to <c>lean.events</c>
/// (<c>messaging-handler: BusMessagingHandler</c>). <see cref="Send"/> runs the base handler first, so logging,
/// the final packet's <c>OrderListHash</c> and its statistics are exactly the monolith's. Publishing is
/// asynchronous through an in-process outbox: the result thread never waits on the broker, and when the outbox
/// is over <c>bus-outbox-limit</c> transient messages (progress, heartbeats) are dropped, never packets.
/// The final backtest result goes by reference to the engine's own result file
/// (<c>lean-results/&lt;jobId&gt;/&lt;attempt&gt;/&lt;algorithmId&gt;.json</c>, <c>final</c>, <c>lean-result-file</c>), and any
/// packet over the 256 KiB inline limit to its packet JSON (<c>.../packets/&lt;sequence&gt;.json</c>, <c>lean-packet</c>). <see cref="Dispose"/>, called by the engine at shutdown,
/// stops the heartbeat, drains the outbox and closes the broker connection.
/// </summary>
internal static class PacketFormats
{
    public const string ResultFile = "lean-result-file";
    public const string Packet = "lean-packet";
}

public class BusMessagingHandler : QuantConnect.Messaging.Messaging
{
    private const string AppId = "lean-engine";
    private readonly Channel<Func<Task>> _outbox = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _startLock = new();
    private readonly int _outboxLimit = BusSettings.GetInt(BusSettings.OutboxLimit, 10_000);
    private int _pending;
    private long _sequence;
    private long _dropped;
    private Task? _pump;
    private IMessageBus? _bus;
    private JobHeartbeatPublisher? _heartbeat;
    private bool _disposed;

    /// <summary>The job attempt packets are published for; null until known.</summary>
    public BusJobContext? Job { get; private set; }

    /// <summary>Transient messages dropped because the outbox was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public override void SetAuthentication(AlgorithmNodePacket job)
    {
        base.SetAuthentication(job);
        EnsureStarted();
    }

    public override void Send(Packet packet)
    {
        base.Send(packet);
        try
        {
            Publish(packet);
        }
        catch (Exception exception)
        {
            Log.Error(exception, $"BusMessagingHandler.Send(): {packet.Type} not published");
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var timeout = TimeSpan.FromSeconds(BusSettings.GetInt(BusSettings.ShutdownTimeoutSeconds, 30));
        _heartbeat?.Dispose();
        _outbox.Writer.TryComplete();
        if (_pump != null && !_pump.Wait(timeout))
        {
            Log.Error($"BusMessagingHandler.Dispose(): outbox not drained after {timeout}, {Volatile.Read(ref _pending)} messages lost");
        }
        EngineBus.Close(timeout);
        Log.Trace($"BusMessagingHandler.Dispose(): published {Interlocked.Read(ref _sequence)}, dropped {Dropped}");
        base.Dispose();
    }

    private BusJobContext? EnsureStarted()
    {
        if (Job != null || _disposed)
        {
            return Job;
        }
        lock (_startLock)
        {
            if (Job != null)
            {
                return Job;
            }
            if (EngineBus.Job is not { } job)
            {
                return null;
            }
            _bus = EngineBus.Bus;
            _pump = Task.Run(PumpAsync);
            Job = job;
            var interval = JobHeartbeatPublisher.IntervalFor(job);
            if (interval > TimeSpan.Zero)
            {
                _heartbeat = new JobHeartbeatPublisher(job, message => Enqueue(message), interval);
            }
            Log.Trace($"BusMessagingHandler: publishing job {job.JobId:D} attempt {job.Attempt} to {BusExchanges.Events}");
            return job;
        }
    }

    private void Publish(Packet packet)
    {
        if (EnsureStarted() is not { } job)
        {
            return;
        }
        var packetType = packet.Type.ToString();
        if (!MessageTypes.InlinePacketTypes.Contains(packetType))
        {
            return;
        }
        var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(packet));
        var sequence = Interlocked.Increment(ref _sequence);

        if (packet is BacktestResultPacket result && result.Progress < 1m)
        {
            Enqueue(Progress(job, result));
        }

        if (packet is BacktestResultPacket { Progress: 1m } final && ReadResultFile(final.BacktestId) is { } resultFile)
        {
            EnqueueByReference(job, packetType, sequence, resultFile, $"{final.BacktestId}.json", PacketFormats.ResultFile, true,
                final.Progress, final.Results?.Statistics, packet);
        }
        else if (packet is BacktestResultPacket { Progress: 1m } || body.Length > OutgoingMessage.MaxInlineBodyBytes)
        {
            var resultPacket = packet as BacktestResultPacket;
            EnqueueByReference(job, packetType, sequence, body, $"packets/{sequence}.json", PacketFormats.Packet, false,
                resultPacket?.Progress, resultPacket?.Progress == 1m ? resultPacket.Results?.Statistics : null, packet);
        }
        else
        {
            var type = MessageTypes.PacketPrefix + packetType;
            Enqueue(OutgoingMessage.CreateRaw(BusExchanges.Events, RoutingKeys.EventFor(type, job.Kind, job.JobId), Envelope(job, type, packetType, sequence), body));
        }
    }

    /// <summary>
    /// The engine's own result file, which <c>BacktestingResultHandler.SendFinalResult</c> writes to
    /// <c>results-destination-folder</c> just before it sends the final packet; null if it is not there.
    /// </summary>
    private static byte[]? ReadResultFile(string algorithmId)
    {
        var path = Path.Combine(Globals.ResultsDestinationFolder ?? Directory.GetCurrentDirectory(), $"{algorithmId}.json");
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException exception)
        {
            Log.Error(exception, $"BusMessagingHandler: cannot read result file {path}");
            return null;
        }
    }

    private void EnqueueByReference(BusJobContext job, string packetType, long sequence, byte[] body, string name, string format,
        bool final, decimal? progress, IDictionary<string, string>? statistics, Packet packet)
    {
        var algorithmId = (packet as BacktestResultPacket)?.BacktestId ?? (packet as LiveResultPacket)?.DeployId;
        var key = $"{job.Results.Prefix.TrimEnd('/')}/{name}";
        var copy = statistics == null ? null : new Dictionary<string, string>(statistics);
        Interlocked.Increment(ref _pending);
        Write(async () =>
        {
            var obj = await EngineBus.Store.PutObjectAsync(job.Results.Bucket, key, body).ConfigureAwait(false);
            var reference = new PacketRef
            {
                PacketType = packetType,
                JobId = job.JobId,
                Attempt = job.Attempt,
                AlgorithmId = algorithmId,
                Final = final,
                Progress = progress is { } p ? (double)p : null,
                Object = obj,
                Format = format,
                Statistics = copy,
            };
            await PublishAsync(OutgoingMessage.Create(BusExchanges.Events, RoutingKeys.EventFor(MessageTypes.PacketRef, job.Kind, job.JobId),
                Envelope(job, MessageTypes.PacketRef, packetType, sequence), reference)).ConfigureAwait(false);
        });
    }

    private static OutgoingMessage Progress(BusJobContext job, BacktestResultPacket result)
    {
        var progress = new JobProgress
        {
            JobId = job.JobId,
            Kind = job.Kind,
            Attempt = job.Attempt,
            ParentJobId = job.ParentJobId,
            At = DateTimeOffset.UtcNow,
            Progress = (double)result.Progress,
            RuntimeStatistics = result.Results?.RuntimeStatistics is { } statistics ? new Dictionary<string, string>(statistics) : null,
        };
        var envelope = MessageEnvelope.Create(MessageTypes.JobProgress, job.JobId, job.Attempt, AppId, job.Kind, job.ParentJobId, job.LeaseEpoch);
        return OutgoingMessage.Create(BusExchanges.Events, RoutingKeys.EventFor(MessageTypes.JobProgress, job.Kind, job.JobId), envelope, progress);
    }

    private static MessageEnvelope Envelope(BusJobContext job, string type, string packetType, long sequence) =>
        MessageEnvelope.Create(type, job.JobId, job.Attempt, AppId, job.Kind, job.ParentJobId, job.LeaseEpoch, packetType, sequence);

    private void Enqueue(OutgoingMessage message)
    {
        var transient = MessageTypes.IsTransient(message.Envelope.Type);
        if (transient && Volatile.Read(ref _pending) >= _outboxLimit)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        // Progress has no consumer queue bound by default (topology.md); it must not fail as unroutable.
        var toPublish = transient ? message with { Mandatory = false } : message;
        Interlocked.Increment(ref _pending);
        Write(() => PublishAsync(toPublish));
    }

    private void Write(Func<Task> work)
    {
        if (!_outbox.Writer.TryWrite(work))
        {
            Interlocked.Decrement(ref _pending);
            Log.Error("BusMessagingHandler: outbox closed, message dropped");
        }
    }

    private Task PublishAsync(OutgoingMessage message) => _bus!.PublishAsync(message);

    private async Task PumpAsync()
    {
        await foreach (var work in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log.Error(exception, "BusMessagingHandler: publish failed");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }
}
