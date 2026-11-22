using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Messaging;

/// <summary>Publishes v1 messages; completes only once the broker has confirmed the message.</summary>
public interface IMessagePublisher
{
    /// <exception cref="MessagePublishException">The broker nacked or returned (unroutable) the message.</exception>
    Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Consumes a queue with manual acknowledgements.</summary>
public interface IMessageConsumer
{
    /// <summary>Starts consuming <paramref name="queue"/>; dispose the result to stop.</summary>
    Task<IAsyncDisposable> ConsumeAsync(string queue, MessageHandler handler, ConsumerOptions? options = null, CancellationToken cancellationToken = default);
}

public interface IMessageBus : IMessagePublisher, IMessageConsumer
{
}

/// <summary>What to do with a delivery once the handler is done.</summary>
public enum MessageDisposition
{
    /// <summary>Processed (and any transaction committed): remove from the queue.</summary>
    Ack,
    /// <summary>Transient failure: requeue; the queue's <c>x-delivery-limit</c> eventually dead-letters it.</summary>
    Retry,
    /// <summary>Poison: reject without requeue so the broker dead-letters it now.</summary>
    DeadLetter,
}

/// <summary>Handles one delivery. Throwing is treated as <see cref="MessageDisposition.Retry"/>.</summary>
public delegate Task<MessageDisposition> MessageHandler(ReceivedMessage message, CancellationToken cancellationToken);

public sealed record ConsumerOptions
{
    /// <summary>Unacked deliveries per consumer (<c>basic.qos</c>). One for job dispatch queues.</summary>
    public ushort Prefetch { get; init; } = 1;
}

/// <summary>A v1 message to publish: destination, AMQP properties and the serialized body.</summary>
public sealed record OutgoingMessage
{
    /// <summary>Contract inline cap; anything larger goes to MinIO and is published as a reference.</summary>
    public const int MaxInlineBodyBytes = 256 * 1024;

    public required string Exchange { get; init; }
    public required string RoutingKey { get; init; }
    public required MessageEnvelope Envelope { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    /// <summary>Unroutable messages fail the publish instead of being dropped.</summary>
    public bool Mandatory { get; init; } = true;

    /// <summary>A message whose body is a contract record serialized with <see cref="ContractJson"/>.</summary>
    public static OutgoingMessage Create<T>(string exchange, string routingKey, MessageEnvelope envelope, T body) =>
        CreateRaw(exchange, routingKey, envelope, ContractJson.SerializeToUtf8Bytes(body));

    /// <summary>A message with an already serialized body (inline LEAN packets keep their Newtonsoft JSON).</summary>
    public static OutgoingMessage CreateRaw(string exchange, string routingKey, MessageEnvelope envelope, ReadOnlyMemory<byte> body)
    {
        var message = new OutgoingMessage { Exchange = exchange, RoutingKey = routingKey, Envelope = envelope, Body = body };
        message.Validate();
        return message;
    }

    /// <summary>Contract checks every publisher applies before sending.</summary>
    public void Validate()
    {
        RoutingKeys.EnsureV1(RoutingKey);
        if (string.IsNullOrEmpty(Exchange))
        {
            throw new ArgumentException("Publishing to the default exchange is not part of the v1 topology", nameof(Exchange));
        }
        if (!MessageTypes.IsKnown(Envelope.Type))
        {
            throw new ArgumentException($"Unknown v1 message type '{Envelope.Type}'", nameof(Envelope));
        }
        if (Envelope.ContentType != MessageEnvelope.JsonContentType)
        {
            throw new ArgumentException($"Content type must be {MessageEnvelope.JsonContentType}", nameof(Envelope));
        }
        if (!Envelope.Headers.ContractVersion.StartsWith($"{ContractVersion.Major}.", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Contract version '{Envelope.Headers.ContractVersion}' is not v{ContractVersion.Major}", nameof(Envelope));
        }
        if (Body.Length > MaxInlineBodyBytes)
        {
            throw new MessageTooLargeException(Body.Length);
        }
    }
}

/// <summary>A delivery handed to a <see cref="MessageHandler"/>.</summary>
public sealed record ReceivedMessage
{
    public required string Queue { get; init; }
    public required string Exchange { get; init; }
    public required string RoutingKey { get; init; }
    public required MessageEnvelope Envelope { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public bool Redelivered { get; init; }
    /// <summary>Previous failed deliveries (<c>x-delivery-count</c>), zero on first delivery.</summary>
    public long DeliveryCount => Envelope.Headers.DeliveryCount ?? 0;

    public T GetBody<T>() => ContractJson.Deserialize<T>(Body.Span);

    /// <summary>The body record for <see cref="MessageEnvelope.Type"/>; null for inline engine packets.</summary>
    public object? GetBody() => MessageTypes.BodyType(Envelope.Type) is { } type ? ContractJson.Deserialize(Body.Span, type) : null;
}

public class MessagePublishException : Exception
{
    public MessagePublishException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>The broker had no queue bound for a mandatory message.</summary>
public sealed class UnroutableMessageException : MessagePublishException
{
    public UnroutableMessageException(string exchange, string routingKey, Exception? inner = null)
        : base($"No queue bound for '{routingKey}' on '{exchange}'", inner)
    {
    }
}

public sealed class MessageTooLargeException : ArgumentException
{
    public MessageTooLargeException(int size)
        : base($"Body is {size} bytes; inline cap is {OutgoingMessage.MaxInlineBodyBytes}. Upload to MinIO and publish a reference.")
    {
    }
}
