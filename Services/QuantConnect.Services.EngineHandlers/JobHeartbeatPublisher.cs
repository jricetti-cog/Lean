using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>
/// Publishes <c>events.v1.JobHeartbeat</c> from inside the engine process (<c>EngineAlive = true</c>): once at
/// start, then every <c>bus-heartbeat-interval-seconds</c> (default 30 s, 10 s for live; 0 disables).
/// </summary>
public sealed class JobHeartbeatPublisher : IDisposable
{
    private readonly Timer _timer;
    private readonly BusJobContext _job;
    private readonly Action<OutgoingMessage> _enqueue;

    public JobHeartbeatPublisher(BusJobContext job, Action<OutgoingMessage> enqueue, TimeSpan interval)
    {
        _job = job;
        _enqueue = enqueue;
        Interval = interval;
        Beat();
        _timer = new Timer(_ => Beat(), null, interval, interval);
    }

    public TimeSpan Interval { get; }

    public static TimeSpan IntervalFor(BusJobContext job) =>
        TimeSpan.FromSeconds(BusSettings.GetInt(BusSettings.HeartbeatIntervalSeconds, job.Kind == JobKind.Live ? 10 : 30));

    public OutgoingMessage Create(DateTimeOffset now)
    {
        var envelope = MessageEnvelope.Create(MessageTypes.JobHeartbeat, _job.JobId, _job.Attempt, "lean-engine", _job.Kind,
            _job.ParentJobId, _job.LeaseEpoch, now: now);
        var heartbeat = new JobHeartbeat
        {
            JobId = _job.JobId,
            Kind = _job.Kind,
            Attempt = _job.Attempt,
            At = now,
            WorkerId = _job.WorkerId,
            LeaseEpoch = _job.LeaseEpoch,
            EngineAlive = true,
            RssBytes = Environment.WorkingSet,
        };
        return OutgoingMessage.Create(BusExchanges.Events, RoutingKeys.EventFor(MessageTypes.JobHeartbeat, _job.Kind, _job.JobId), envelope, heartbeat);
    }

    private void Beat() => _enqueue(Create(DateTimeOffset.UtcNow));

    public void Dispose()
    {
        using var stopped = new ManualResetEvent(false);
        if (_timer.Dispose(stopped))
        {
            stopped.WaitOne(TimeSpan.FromSeconds(5));
        }
    }
}
