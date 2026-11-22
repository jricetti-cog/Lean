using System.Collections.Concurrent;
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
                if (_connection is { CloseReason: { } reason } && reason.Initiator != ShutdownInitiator.Application)
                {
                    // Lost to the broker or network: automatic recovery is reconnecting it and re-creating every consumer and
                    // the publish channel on it. Disposing it here would strand those consumers on a dead connection.
                    throw new AlreadyClosedException(reason);
                }
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
                    TopologyRecoveryFilter = RecoveryFilter(),
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
        var subscription = new TransientSubscription(this, connection, channel, exchange, bindingKeys, handler, options);
        try
        {
            await subscription.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return subscription;
    }

    // Exclusive transient queues are deleted by the broker with the connection; automatic topology recovery would re-bind them before
    // re-declaring them (not_found closes the recovery channel and aborts the rest of the recovery, including the durable consumers).
    // They are excluded from it and re-created by their TransientSubscription once the connection has recovered.
    private readonly ConcurrentDictionary<string, byte> _transientQueues = new();

    private TopologyRecoveryFilter RecoveryFilter() => new()
    {
        QueueFilter = q => !_transientQueues.ContainsKey(q.Name),
        BindingFilter = b => !_transientQueues.ContainsKey(b.Destination),
        ConsumerFilter = c => !_transientQueues.ContainsKey(c.Queue),
    };

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

    private sealed class TransientSubscription : IAsyncDisposable
    {
        private readonly RabbitMqMessageBus _bus;
        private readonly IConnection _connection;
        private readonly IChannel _channel;
        private readonly string _exchange;
        private readonly IReadOnlyCollection<string> _bindingKeys;
        private readonly MessageHandler _handler;
        private readonly TransientQueueOptions _options;
        private readonly CancellationTokenSource _stopping = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string? _queue;
        private string? _tag;

        public TransientSubscription(RabbitMqMessageBus bus, IConnection connection, IChannel channel, string exchange,
            IReadOnlyCollection<string> bindingKeys, MessageHandler handler, TransientQueueOptions options)
        {
            _bus = bus;
            _connection = connection;
            _channel = channel;
            _exchange = exchange;
            _bindingKeys = bindingKeys;
            _handler = handler;
            _options = options;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await DeclareAndConsumeAsync(cancellationToken).ConfigureAwait(false);
            _connection.RecoverySucceededAsync += OnRecoveredAsync;
        }

        private async Task DeclareAndConsumeAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_stopping.IsCancellationRequested) return;
                var arguments = new Dictionary<string, object?>();
                if (_options.MessageTtl is { } ttl)
                {
                    arguments["x-message-ttl"] = (int)ttl.TotalMilliseconds;
                }
                if (_options.QueueName != null) _bus._transientQueues[_options.QueueName] = 0;
                var declared = await _channel.QueueDeclareAsync(_options.QueueName ?? string.Empty, durable: false, exclusive: true, autoDelete: true,
                    arguments: arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (_queue != null && _queue != declared.QueueName) _bus._transientQueues.TryRemove(_queue, out _);
                _queue = declared.QueueName;
                _bus._transientQueues[_queue] = 0;
                foreach (var key in _bindingKeys)
                {
                    await _channel.QueueBindAsync(_queue, _exchange, key, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                await _channel.BasicQosAsync(0, _options.Prefetch, false, cancellationToken).ConfigureAwait(false);
                var queue = _queue;
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += (_, delivery) => _bus.OnReceivedAsync(_channel, queue, _handler, delivery, _stopping.Token);
                _tag = await _channel.BasicConsumeAsync(_queue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task OnRecoveredAsync(object sender, AsyncEventArgs args)
        {
            for (var attempt = 1; !_stopping.IsCancellationRequested; attempt++)
            {
                try
                {
                    await DeclareAndConsumeAsync(_stopping.Token).ConfigureAwait(false);
                    _bus._logger.LogInformation("Re-created transient queue {Queue} after the connection recovered", _queue);
                    return;
                }
                catch (Exception e) when (!_stopping.IsCancellationRequested && attempt < 5)
                {
                    _bus._logger.LogWarning(e, "Cannot re-create transient queue {Queue} after recovery (attempt {Attempt}); retrying", _options.QueueName ?? _queue, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), _stopping.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (!_stopping.IsCancellationRequested)
                {
                    _bus._logger.LogError(e, "Giving up re-creating transient queue {Queue} after recovery", _options.QueueName ?? _queue);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _connection.RecoverySucceededAsync -= OnRecoveredAsync;
            _stopping.Cancel();
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Deleting the queue also drops its recorded bindings; until that succeeds they stay excluded from recovery.
                var forgotten = _queue == null;
                try
                {
                    if (_channel.IsOpen && _tag != null)
                    {
                        await _channel.BasicCancelAsync(_tag).ConfigureAwait(false);
                    }
                    if (_channel.IsOpen && _queue != null)
                    {
                        foreach (var key in _bindingKeys)
                        {
                            await _channel.QueueUnbindAsync(_queue, _exchange, key).ConfigureAwait(false);
                        }
                        await _channel.QueueDeleteAsync(_queue).ConfigureAwait(false);
                        forgotten = true;
                    }
                    if (_channel.IsOpen)
                    {
                        await _channel.CloseAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is OperationInterruptedException or AlreadyClosedException or IOException)
                {
                    _bus._logger.LogDebug(e, "Transient queue {Queue} closed with its channel", _queue);
                }
                finally
                {
                    if (forgotten)
                    {
                        if (_queue != null) _bus._transientQueues.TryRemove(_queue, out _);
                        if (_options.QueueName != null) _bus._transientQueues.TryRemove(_options.QueueName, out _);
                    }
                    await _channel.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
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
