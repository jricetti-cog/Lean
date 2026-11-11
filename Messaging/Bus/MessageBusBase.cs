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
 *
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Logging;
using QuantConnect.Services;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Transport independent part of a message bus: subscription registry, ordered dispatch,
    /// request/reply correlation and readiness probing
    /// </summary>
    public abstract class MessageBusBase : IMessageBus
    {
        private const string PingHeader = "ping";
        private readonly object _subscriptionsLock = new();
        private readonly List<Subscription> _subscriptions = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pendingRequests = new();
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _pendingPings = new();
        private readonly BlockingCollection<(Subscription, MessageEnvelope)> _dispatchQueue = new();
        private readonly Thread _dispatchThread;
        private bool _disposed;

        /// <summary>
        /// Unique id of this bus connection
        /// </summary>
        public string InstanceId { get; }

        /// <summary>
        /// The private topic replies to this instance are sent to
        /// </summary>
        public string InboxTopic => "_inbox." + InstanceId;

        /// <summary>
        /// Creates a new instance
        /// </summary>
        protected MessageBusBase(string instanceId = null)
        {
            InstanceId = string.IsNullOrEmpty(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
            _dispatchThread = new Thread(Dispatch) { IsBackground = true, Name = "MessageBusDispatch" };
            _dispatchThread.Start();
        }

        /// <summary>
        /// Must be called by implementations once the transport is able to receive messages
        /// </summary>
        protected void SubscribeInbox()
        {
            TransportSubscribe(InboxTopic);
        }

        /// <summary>
        /// Sends a message over the transport
        /// </summary>
        protected abstract void TransportPublish(MessageEnvelope envelope);

        /// <summary>
        /// Starts receiving topics with the given prefix
        /// </summary>
        protected abstract void TransportSubscribe(string topicPrefix);

        /// <summary>
        /// Stops receiving topics with the given prefix
        /// </summary>
        protected abstract void TransportUnsubscribe(string topicPrefix);

        /// <summary>
        /// Publishes a message
        /// </summary>
        public void Publish(MessageEnvelope envelope)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
            if (string.IsNullOrEmpty(envelope.Topic))
            {
                throw new ArgumentException("A message requires a topic", nameof(envelope));
            }
            envelope.SenderId ??= InstanceId;
            TransportPublish(envelope);
        }

        /// <summary>
        /// Subscribes to a topic prefix
        /// </summary>
        public IDisposable Subscribe(string topicPrefix, Action<MessageEnvelope> handler)
        {
            return AddSubscription(new Subscription(topicPrefix, handler, null));
        }

        /// <summary>
        /// Registers a request handler
        /// </summary>
        public IDisposable Respond(string topic, Func<MessageEnvelope, MessageEnvelope> handler)
        {
            return AddSubscription(new Subscription(topic, null, handler));
        }

        /// <summary>
        /// Sends a request and waits for its reply
        /// </summary>
        public async Task<MessageEnvelope> RequestAsync(MessageEnvelope request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            request.ReplyTo = InboxTopic;
            var completion = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[request.Id] = completion;
            try
            {
                Publish(request);
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                using (timeoutSource.Token.Register(() => completion.TrySetCanceled()))
                {
                    try
                    {
                        return await completion.Task.ConfigureAwait(false);
                    }
                    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new MessageBusException($"Request '{request.Topic}' timed out after {timeout.TotalSeconds}s");
                    }
                }
            }
            finally
            {
                _pendingRequests.TryRemove(request.Id, out _);
            }
        }

        /// <summary>
        /// Round trips ping messages through the transport until one is received
        /// </summary>
        public bool WaitForReady(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var ping = new MessageEnvelope { Topic = InboxTopic };
                ping.Headers[PingHeader] = "1";
                using var received = new ManualResetEventSlim();
                _pendingPings[ping.Id] = received;
                try
                {
                    Publish(ping);
                    if (received.Wait(TimeSpan.FromMilliseconds(250)))
                    {
                        return true;
                    }
                }
                finally
                {
                    _pendingPings.TryRemove(ping.Id, out _);
                }
            }
            return false;
        }

        /// <summary>
        /// Called by implementations for every message received from the transport
        /// </summary>
        protected void OnMessageReceived(MessageEnvelope envelope)
        {
            if (envelope.Topic == InboxTopic)
            {
                if (envelope.Headers.ContainsKey(PingHeader))
                {
                    if (_pendingPings.TryGetValue(envelope.Id, out var ping))
                    {
                        ping.Set();
                    }
                }
                else if (envelope.CorrelationId != null && _pendingRequests.TryGetValue(envelope.CorrelationId, out var completion))
                {
                    completion.TrySetResult(envelope);
                }
                return;
            }

            Subscription[] matches;
            lock (_subscriptionsLock)
            {
                matches = _subscriptions.Where(s => s.Matches(envelope.Topic)).ToArray();
            }
            foreach (var subscription in matches)
            {
                if (subscription.Responder != null)
                {
                    if (envelope.ReplyTo != null)
                    {
                        Task.Run(() => HandleRequest(subscription, envelope));
                    }
                }
                else if (!_dispatchQueue.IsAddingCompleted)
                {
                    _dispatchQueue.Add((subscription, envelope));
                }
            }
        }

        /// <summary>
        /// Disposes of the bus
        /// </summary>
        public virtual void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _dispatchQueue.CompleteAdding();
            foreach (var pending in _pendingRequests.Values)
            {
                pending.TrySetCanceled();
            }
            _dispatchThread.Join(TimeSpan.FromSeconds(5));
            GC.SuppressFinalize(this);
        }

        private void HandleRequest(Subscription subscription, MessageEnvelope request)
        {
            MessageEnvelope reply;
            try
            {
                reply = subscription.Responder(request) ?? request.CreateReply<object>(null);
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"MessageBus.HandleRequest({request.Topic})");
                reply = request.CreateErrorReply(exception.Message);
            }
            reply.Topic = request.ReplyTo;
            reply.CorrelationId = request.Id;
            if (!_disposed)
            {
                Publish(reply);
            }
        }

        private void Dispatch()
        {
            foreach (var (subscription, envelope) in _dispatchQueue.GetConsumingEnumerable())
            {
                if (subscription.Disposed)
                {
                    continue;
                }
                try
                {
                    subscription.Handler(envelope);
                }
                catch (Exception exception)
                {
                    Log.Error(exception, $"MessageBus.Dispatch({envelope.Topic})");
                }
            }
        }

        private IDisposable AddSubscription(Subscription subscription)
        {
            bool first;
            lock (_subscriptionsLock)
            {
                first = !_subscriptions.Any(s => s.Prefix == subscription.Prefix);
                _subscriptions.Add(subscription);
            }
            if (first)
            {
                TransportSubscribe(subscription.Prefix);
            }
            subscription.OnDispose = () =>
            {
                bool last;
                lock (_subscriptionsLock)
                {
                    _subscriptions.Remove(subscription);
                    last = !_subscriptions.Any(s => s.Prefix == subscription.Prefix);
                }
                if (last && !_disposed)
                {
                    TransportUnsubscribe(subscription.Prefix);
                }
            };
            return subscription;
        }

        private sealed class Subscription : IDisposable
        {
            public string Prefix { get; }
            public Action<MessageEnvelope> Handler { get; }
            public Func<MessageEnvelope, MessageEnvelope> Responder { get; }
            public Action OnDispose { get; set; }
            public bool Disposed { get; private set; }

            public Subscription(string prefix, Action<MessageEnvelope> handler, Func<MessageEnvelope, MessageEnvelope> responder)
            {
                Prefix = prefix;
                Handler = handler;
                Responder = responder;
            }

            public bool Matches(string topic)
            {
                // responders are bound to an exact topic, subscribers to a prefix
                return Responder != null ? topic == Prefix : topic.StartsWith(Prefix, StringComparison.Ordinal);
            }

            public void Dispose()
            {
                if (!Disposed)
                {
                    Disposed = true;
                    OnDispose?.Invoke();
                }
            }
        }
    }
}
