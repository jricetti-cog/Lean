using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace QuantConnect.Services.Messaging;

/// <summary>
/// Producer and consumer spans plus delivery metrics for the bus. The W3C trace context travels in the
/// <c>traceparent</c>/<c>tracestate</c> headers, so one trace follows a job across every service it crosses.
/// </summary>
public static class MessagingTelemetry
{
    public const string Name = "QuantConnect.Services.Messaging";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Published = Meter.CreateCounter<long>("lean.messaging.published", "{message}", "Messages published to RabbitMQ");
    private static readonly Histogram<double> ProcessDuration = Meter.CreateHistogram("lean.messaging.process.duration", "s", "Time to handle one delivery",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 1800, 7200] });

    /// <summary>A producer span for <paramref name="message"/>, parented on its <c>traceparent</c> header or else the current activity.</summary>
    public static Activity? StartPublish(OutgoingMessage message)
    {
        var envelope = message.Envelope;
        var activity = Source.StartActivity($"{message.Exchange} publish", ActivityKind.Producer, ParentOf(envelope.Headers.TraceParent, envelope.Headers.TraceState));
        if (activity is { IsAllDataRequested: true })
        {
            Tag(activity, message.Exchange, envelope);
            activity.SetTag("messaging.operation.type", "send");
            activity.SetTag("messaging.rabbitmq.destination.routing_key", message.RoutingKey);
        }
        return activity;
    }

    /// <summary>The headers to publish: the producer span's trace context when there is one, never the broker's delivery count.</summary>
    public static Contracts.MessageHeaders HeadersFor(Contracts.MessageEnvelope envelope, Activity? producer) => producer == null
        ? envelope.Headers with { DeliveryCount = null }
        : envelope.Headers with { DeliveryCount = null, TraceParent = producer.Id, TraceState = producer.TraceStateString };

    public static void RecordPublished(OutgoingMessage message) =>
        Published.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", message.Exchange), new KeyValuePair<string, object?>("lean.message.type", message.Envelope.Type));

    /// <summary>A consumer span for one delivery, parented on the publisher's <c>traceparent</c> header.</summary>
    public static Activity? StartProcess(ReceivedMessage message)
    {
        var envelope = message.Envelope;
        var activity = Source.StartActivity($"{message.Queue} process", ActivityKind.Consumer, ParentOf(envelope.Headers.TraceParent, envelope.Headers.TraceState));
        if (activity is { IsAllDataRequested: true })
        {
            Tag(activity, message.Exchange, envelope);
            activity.SetTag("messaging.operation.type", "process");
            activity.SetTag("messaging.rabbitmq.destination.routing_key", message.RoutingKey);
            activity.SetTag("messaging.consumer.group.name", message.Queue);
            if (message.DeliveryCount is { } count) activity.SetTag("messaging.rabbitmq.delivery_count", count);
        }
        return activity;
    }

    public static void RecordProcessed(Activity? activity, string queue, string? type, MessageDisposition disposition, TimeSpan elapsed)
    {
        var outcome = disposition switch
        {
            MessageDisposition.Ack => "ack",
            MessageDisposition.Retry => "retry",
            _ => "dead_letter",
        };
        if (activity != null)
        {
            activity.SetTag("lean.message.disposition", outcome);
            if (disposition != MessageDisposition.Ack) activity.SetStatus(ActivityStatusCode.Error, outcome);
        }
        ProcessDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("messaging.destination.name", queue),
            new KeyValuePair<string, object?>("lean.message.type", type ?? "invalid"), new KeyValuePair<string, object?>("lean.message.disposition", outcome));
    }

    private static ActivityContext ParentOf(string? traceParent, string? traceState) =>
        traceParent != null && ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context) ? context : default;

    private static void Tag(Activity activity, string exchange, Contracts.MessageEnvelope envelope)
    {
        activity.SetTag("messaging.system", "rabbitmq");
        activity.SetTag("messaging.destination.name", exchange);
        activity.SetTag("messaging.message.id", envelope.MessageId.ToString("D"));
        activity.SetTag("messaging.message.conversation_id", envelope.CorrelationId.ToString("D"));
        activity.SetTag("lean.message.type", envelope.Type);
        activity.SetTag("lean.job.id", envelope.Headers.JobId.ToString("D"));
        activity.SetTag("lean.job.attempt", envelope.Headers.Attempt);
        if (envelope.Headers.JobKind is { } kind) activity.SetTag("lean.job.kind", kind.ToString());
    }
}
