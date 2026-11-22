using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantConnect.Services.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace QuantConnect.Services.Messaging.RabbitMq;

public sealed class RabbitMqOptions
{
    /// <summary>12-factor: <c>RabbitMQ__Uri</c>. The path is the vhost (<c>lean</c>).</summary>
    public Uri Uri { get; set; } = new("amqp://guest:guest@localhost:5672/lean");
    public string? ClientName { get; set; }
    /// <summary>How long a publish may wait for its broker confirm.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Thin RabbitMQ.Client wrapper: one confirm-tracking publish channel (a publish completes only once the
/// broker acks it; nacks and mandatory returns throw), one channel per consumer with manual acks
/// (<see cref="MessageDisposition"/> → ack / nack-requeue / reject-to-DLX). Connects lazily with automatic recovery.
/// </summary>
public sealed class RabbitMqMessageBus : IMessageBus, ITransientSubscriber, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private int _disposed;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publishChannel;

    public RabbitMqMessageBus(RabbitMqOptions options, ILogger<RabbitMqMessageBus>? logger = null)
    {
        _options = options;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public bool IsConnected => _connection?.IsOpen == true;

    public async Task<IConnection> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { IsOpen: true } open && _publishChannel is { IsOpen: true })
        {
            return open;
        }
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not { IsOpen: true })
            {
                if (_connection != null)
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                }
                var factory = new ConnectionFactory
                {
                    Uri = _options.Uri,
                    ClientProvidedName = _options.ClientName ?? AppDomain.CurrentDomain.FriendlyName,
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                };
                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                _publishChannel = null;
            }
            if (_publishChannel is not { IsOpen: true })
            {
                _publishChannel = await _connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                    cancellationToken).ConfigureAwait(false);
            }
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        message.Validate();
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var envelope = message.Envelope;
        using var activity = MessagingTelemetry.StartPublish(message);
        var properties = new BasicProperties
        {
            MessageId = envelope.MessageId.ToString("D"),
            CorrelationId = envelope.CorrelationId.ToString("D"),
            Type = envelope.Type,
            ContentType = envelope.ContentType,
            ContentEncoding = envelope.ContentEncoding,
            DeliveryMode = envelope.DeliveryMode == MessageEnvelope.Transient ? DeliveryModes.Transient : DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(envelope.Timestamp),
            AppId = envelope.AppId,
            Expiration = envelope.Expiration,
            Headers = AmqpHeaders.ToAmqp(MessagingTelemetry.HeadersFor(envelope, activity)),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.PublishTimeout);
        await _publishLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await _publishChannel!.BasicPublishAsync(message.Exchange, message.RoutingKey, message.Mandatory, properties, message.Body, timeout.Token)
                .ConfigureAwait(false);
            MessagingTelemetry.RecordPublished(message);
        }
        catch (PublishException e) when (e.IsReturn)
        {
            throw new UnroutableMessageException(message.Exchange, message.RoutingKey, e);
        }
        catch (PublishException e)
        {
            throw new MessagePublishException($"Broker nacked '{envelope.Type}' to '{message.RoutingKey}'", e);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public async Task<IAsyncDisposable> ConsumeAsync(string queue, MessageHandler handler, ConsumerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ConsumerOptions();
        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.BasicQosAsync(0, options.Prefetch, false, cancellationToken).ConfigureAwait(false);
        var stopping = new CancellationTokenSource();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, queue, handler, delivery, stopping.Token);
        var tag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
        return new Subscription(channel, tag, stopping);
    }

    public async Task<IAsyncDisposable> SubscribeTransientAsync(string exchange, IReadOnlyCollection<string> bindingKeys, MessageHandler handler,
        TransientQueueOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TransientQueueOptions();
        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var arguments = new Dictionary<string, object?>();
        if (options.MessageTtl is { } ttl)
        {
            arguments["x-message-ttl"] = (int)ttl.TotalMilliseconds;
        }
        var declared = await channel.QueueDeclareAsync(options.QueueName ?? string.Empty, durable: false, exclusive: true, autoDelete: true, arguments: arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var key in bindingKeys)
        {
            await channel.QueueBindAsync(declared.QueueName, exchange, key, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await channel.BasicQosAsync(0, options.Prefetch, false, cancellationToken).ConfigureAwait(false);
        var stopping = new CancellationTokenSource();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, declared.QueueName, handler, delivery, stopping.Token);
        var tag = await channel.BasicConsumeAsync(declared.QueueName, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
        return new Subscription(channel, tag, stopping);
    }

    /// <summary>Declares exchanges, queues and bindings (dev/test brokers; production loads definitions.json).</summary>
    public async Task DeclareAsync(TopologyDefinition topology, CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var exchange in topology.Exchanges)
        {
            await channel.ExchangeDeclareAsync(exchange.Name, exchange.Type, durable: exchange.Durable, autoDelete: exchange.AutoDelete, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        foreach (var queue in topology.Queues)
        {
            await channel.QueueDeclareAsync(queue.Name, durable: queue.Durable, exclusive: false, autoDelete: queue.AutoDelete,
                arguments: queue.Arguments.ToDictionary(a => a.Key, a => a.Value), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        foreach (var binding in topology.Bindings)
        {
            await channel.QueueBindAsync(binding.Destination, binding.Source, binding.RoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnReceivedAsync(IChannel channel, string queue, MessageHandler handler, BasicDeliverEventArgs delivery, CancellationToken stopping)
    {
        MessageDisposition disposition;
        ReceivedMessage? message = null;
        try
        {
            message = new ReceivedMessage
            {
                Queue = queue,
                Exchange = delivery.Exchange,
                RoutingKey = delivery.RoutingKey,
                Envelope = ToEnvelope(delivery.BasicProperties),
                Body = delivery.Body.ToArray(),
                Redelivered = delivery.Redelivered,
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            _logger.LogError(e, "Dead-lettering delivery on {Queue} ({RoutingKey}) with an invalid v1 envelope", queue, delivery.RoutingKey);
        }

        if (message == null)
        {
            disposition = MessageDisposition.DeadLetter;
        }
        else
        {
            using var activity = MessagingTelemetry.StartProcess(message);
            var started = Stopwatch.GetTimestamp();
            try
            {
                disposition = await handler(message, stopping).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                activity?.AddException(e);
                _logger.LogWarning(e, "Handler failed for {Type} {MessageId} on {Queue} (delivery count {DeliveryCount}); requeueing",
                    message.Envelope.Type, message.Envelope.MessageId, queue, message.DeliveryCount);
                disposition = MessageDisposition.Retry;
            }
            MessagingTelemetry.RecordProcessed(activity, queue, message.Envelope.Type, disposition, Stopwatch.GetElapsedTime(started));
        }

        switch (disposition)
        {
            case MessageDisposition.Ack:
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
                break;
            case MessageDisposition.Retry:
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true).ConfigureAwait(false);
                break;
            default:
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false).ConfigureAwait(false);
                break;
        }
    }

    internal static MessageEnvelope ToEnvelope(IReadOnlyBasicProperties properties) => new()
    {
        MessageId = Guid.Parse(properties.MessageId ?? throw new FormatException("Missing message_id")),
        CorrelationId = Guid.Parse(properties.CorrelationId ?? throw new FormatException("Missing correlation_id")),
        Type = properties.Type ?? throw new FormatException("Missing type"),
        ContentType = properties.ContentType ?? MessageEnvelope.JsonContentType,
        ContentEncoding = properties.ContentEncoding,
        DeliveryMode = properties.DeliveryMode == DeliveryModes.Transient ? MessageEnvelope.Transient : MessageEnvelope.Persistent,
        Timestamp = properties.Timestamp.UnixTime,
        AppId = properties.AppId,
        Expiration = properties.Expiration,
        Headers = AmqpHeaders.FromAmqp(properties.Headers),
    };

    public async ValueTask DisposeAsync()
    {
        // DI disposes the bus once per registration that resolves it (concrete type and each interface).
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        if (_publishChannel != null)
        {
            await _publishChannel.DisposeAsync().ConfigureAwait(false);
        }
        if (_connection != null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        _connectLock.Dispose();
        _publishLock.Dispose();
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly IChannel _channel;
        private readonly string _tag;
        private readonly CancellationTokenSource _stopping;

        public Subscription(IChannel channel, string tag, CancellationTokenSource stopping)
        {
            _channel = channel;
            _tag = tag;
            _stopping = stopping;
        }

        public async ValueTask DisposeAsync()
        {
            if (_channel.IsOpen)
            {
                await _channel.BasicCancelAsync(_tag).ConfigureAwait(false);
            }
            _stopping.Cancel();
            if (_channel.IsOpen)
            {
                await _channel.CloseAsync().ConfigureAwait(false);
            }
            await _channel.DisposeAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }
    }
}
