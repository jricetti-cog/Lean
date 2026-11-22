using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Messaging.InMemory;

/// <summary>
/// Deterministic in-process stand-in for RabbitMQ in unit tests. Routes with the same exchanges, bindings,
/// delivery limits and dead-letter arguments as the broker (from <see cref="TopologyDefinition"/>), applies the
/// same publish checks, and delivers only when the test calls <see cref="DeliverAllAsync"/>.
/// </summary>
public sealed class InMemoryMessageBus : IMessageBus
{
    /// <summary>A message the bus accepted (confirmed).</summary>
    public sealed record PublishedMessage(string Exchange, string RoutingKey, MessageEnvelope Envelope, ReadOnlyMemory<byte> Body);

    private sealed class Entry
    {
        public required string Exchange { get; init; }
        public required string RoutingKey { get; init; }
        public required MessageEnvelope Envelope { get; init; }
        public required ReadOnlyMemory<byte> Body { get; init; }
        public long DeliveryCount { get; set; }
        public bool Redelivered { get; set; }
    }

    private sealed class QueueState
    {
        public required TopologyDefinition.Queue Definition { get; init; }
        public LinkedList<Entry> Ready { get; } = new();
        public List<(MessageHandler Handler, Subscription Subscription)> Consumers { get; } = new();
        public int NextConsumer { get; set; }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        public bool Active { get; private set; } = true;
        public ValueTask DisposeAsync()
        {
            Active = false;
            return ValueTask.CompletedTask;
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _exchanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueueState> _queues = new(StringComparer.Ordinal);
    private readonly List<TopologyDefinition.Binding> _bindings = new();
    private readonly List<PublishedMessage> _published = new();

    public InMemoryMessageBus(TopologyDefinition? topology = null)
    {
        topology ??= TopologyDefinition.V1;
        foreach (var exchange in topology.Exchanges)
        {
            DeclareExchange(exchange.Name, exchange.Type);
        }
        foreach (var queue in topology.Queues)
        {
            DeclareQueue(queue.Name, queue.Arguments);
        }
        foreach (var binding in topology.Bindings)
        {
            Bind(binding.Source, binding.Destination, binding.RoutingKey);
        }
    }

    /// <summary>Simulates a broker nack for matching messages (publisher-confirm failure).</summary>
    public Func<OutgoingMessage, bool>? NackWhen { get; set; }

    /// <summary>Every confirmed publish, in order.</summary>
    public IReadOnlyList<PublishedMessage> Published
    {
        get { lock (_lock) { return _published.ToList(); } }
    }

    public void DeclareExchange(string name, string type)
    {
        lock (_lock) { _exchanges[name] = type; }
    }

    public void DeclareQueue(string name, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        lock (_lock)
        {
            _queues.TryAdd(name, new QueueState { Definition = new TopologyDefinition.Queue(name, arguments ?? new Dictionary<string, object?>()) });
        }
    }

    public void Bind(string exchange, string queue, string routingKey)
    {
        lock (_lock) { _bindings.Add(new TopologyDefinition.Binding(exchange, queue, routingKey)); }
    }

    /// <summary>Messages waiting in <paramref name="queue"/> (not yet delivered or requeued).</summary>
    public IReadOnlyList<ReceivedMessage> Peek(string queue)
    {
        lock (_lock)
        {
            return Queue(queue).Ready.Select(e => ToReceived(queue, e)).ToList();
        }
    }

    public Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        message.Validate();
        if (NackWhen?.Invoke(message) == true)
        {
            throw new MessagePublishException($"Broker nacked '{message.Envelope.Type}' to '{message.RoutingKey}'");
        }
        lock (_lock)
        {
            if (!_exchanges.ContainsKey(message.Exchange))
            {
                throw new MessagePublishException($"Exchange '{message.Exchange}' not declared");
            }
            var routed = Route(message.Exchange, message.RoutingKey, message.Envelope, message.Body.ToArray());
            if (routed == 0 && message.Mandatory)
            {
                throw new UnroutableMessageException(message.Exchange, message.RoutingKey);
            }
            _published.Add(new PublishedMessage(message.Exchange, message.RoutingKey, message.Envelope, message.Body));
        }
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> ConsumeAsync(string queue, MessageHandler handler, ConsumerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var subscription = new Subscription();
        lock (_lock)
        {
            Queue(queue).Consumers.Add((handler, subscription));
        }
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    /// <summary>Delivers until every queue with an active consumer is empty; returns the number of deliveries.</summary>
    public async Task<int> DeliverAllAsync(CancellationToken cancellationToken = default)
    {
        var deliveries = 0;
        while (true)
        {
            (QueueState Queue, Entry Entry, MessageHandler Handler)? next = null;
            lock (_lock)
            {
                foreach (var state in _queues.Values)
                {
                    state.Consumers.RemoveAll(c => !c.Subscription.Active);
                    if (state.Ready.First is { } first && state.Consumers.Count > 0)
                    {
                        state.Ready.RemoveFirst();
                        var consumer = state.Consumers[state.NextConsumer++ % state.Consumers.Count];
                        next = (state, first.Value, consumer.Handler);
                        break;
                    }
                }
            }
            if (next is not { } delivery)
            {
                return deliveries;
            }
            deliveries++;

            MessageDisposition disposition;
            try
            {
                disposition = await delivery.Handler(ToReceived(delivery.Queue.Definition.Name, delivery.Entry), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                disposition = MessageDisposition.Retry;
            }
            Settle(delivery.Queue, delivery.Entry, disposition);
        }
    }

    private void Settle(QueueState queue, Entry entry, MessageDisposition disposition)
    {
        lock (_lock)
        {
            switch (disposition)
            {
                case MessageDisposition.Ack:
                    return;
                case MessageDisposition.Retry:
                    // Quorum queues dead-letter once a message has been returned more than x-delivery-limit times.
                    entry.DeliveryCount++;
                    entry.Redelivered = true;
                    if (queue.Definition.DeliveryLimit is { } limit && entry.DeliveryCount > limit)
                    {
                        DeadLetter(queue, entry);
                    }
                    else
                    {
                        queue.Ready.AddLast(entry);
                    }
                    return;
                case MessageDisposition.DeadLetter:
                    DeadLetter(queue, entry);
                    return;
            }
        }
    }

    private void DeadLetter(QueueState queue, Entry entry)
    {
        if (queue.Definition.DeadLetterExchange is { } dlx && _exchanges.ContainsKey(dlx))
        {
            Route(dlx, queue.Definition.DeadLetterRoutingKey ?? entry.RoutingKey, entry.Envelope, entry.Body);
        }
    }

    private int Route(string exchange, string routingKey, MessageEnvelope envelope, ReadOnlyMemory<byte> body)
    {
        var type = _exchanges[exchange];
        var targets = _bindings
            .Where(b => b.Source == exchange && type switch
            {
                "fanout" => true,
                "topic" => RoutingKeys.TopicMatches(b.RoutingKey, routingKey),
                _ => b.RoutingKey == routingKey,
            })
            .Select(b => b.Destination)
            .Distinct()
            .ToList();
        foreach (var target in targets)
        {
            Queue(target).Ready.AddLast(new Entry { Exchange = exchange, RoutingKey = routingKey, Envelope = envelope, Body = body });
        }
        return targets.Count;
    }

    private QueueState Queue(string name) =>
        _queues.TryGetValue(name, out var queue) ? queue : throw new InvalidOperationException($"Queue '{name}' not declared");

    private static ReceivedMessage ToReceived(string queue, Entry entry) => new()
    {
        Queue = queue,
        Exchange = entry.Exchange,
        RoutingKey = entry.RoutingKey,
        Envelope = entry.DeliveryCount > 0 ? entry.Envelope with { Headers = entry.Envelope.Headers with { DeliveryCount = entry.DeliveryCount } } : entry.Envelope,
        Body = entry.Body,
        Redelivered = entry.Redelivered,
    };
}
