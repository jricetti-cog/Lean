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

using System.Collections.Generic;
using System.Linq;
using QuantConnect.Services;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Shared in-process transport connecting <see cref="InMemoryMessageBus"/> instances
    /// </summary>
    public class InMemoryMessageHub
    {
        private readonly object _lock = new();
        private readonly List<InMemoryMessageBus> _buses = new();

        /// <summary>
        /// The default hub used when no hub is specified
        /// </summary>
        public static InMemoryMessageHub Default { get; } = new();

        internal void Attach(InMemoryMessageBus bus)
        {
            lock (_lock)
            {
                _buses.Add(bus);
            }
        }

        internal void Detach(InMemoryMessageBus bus)
        {
            lock (_lock)
            {
                _buses.Remove(bus);
            }
        }

        internal void Publish(MessageEnvelope envelope)
        {
            // serialize like a network transport would so contracts are exercised end to end
            var metadata = envelope.SerializeMetadata();
            InMemoryMessageBus[] buses;
            lock (_lock)
            {
                buses = _buses.ToArray();
            }
            foreach (var bus in buses.Where(b => b.IsSubscribed(envelope.Topic)))
            {
                bus.Deliver(MessageEnvelope.Deserialize(metadata, envelope.Payload));
            }
        }
    }

    /// <summary>
    /// In-process message bus, used by tests and when all services are hosted in one process
    /// </summary>
    public class InMemoryMessageBus : MessageBusBase
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, int> _prefixes = new();
        private readonly InMemoryMessageHub _hub;

        /// <summary>
        /// Creates a new bus connected to the given hub (or the default hub)
        /// </summary>
        public InMemoryMessageBus(InMemoryMessageHub hub = null, string instanceId = null) : base(instanceId)
        {
            _hub = hub ?? InMemoryMessageHub.Default;
            _hub.Attach(this);
            SubscribeInbox();
        }

        internal bool IsSubscribed(string topic)
        {
            lock (_lock)
            {
                return _prefixes.Keys.Any(prefix => topic.StartsWith(prefix, System.StringComparison.Ordinal));
            }
        }

        internal void Deliver(MessageEnvelope envelope)
        {
            OnMessageReceived(envelope);
        }

        /// <summary>
        /// Sends the message to the hub
        /// </summary>
        protected override void TransportPublish(MessageEnvelope envelope)
        {
            _hub.Publish(envelope);
        }

        /// <summary>
        /// Adds a prefix
        /// </summary>
        protected override void TransportSubscribe(string topicPrefix)
        {
            lock (_lock)
            {
                _prefixes[topicPrefix] = _prefixes.TryGetValue(topicPrefix, out var count) ? count + 1 : 1;
            }
        }

        /// <summary>
        /// Removes a prefix
        /// </summary>
        protected override void TransportUnsubscribe(string topicPrefix)
        {
            lock (_lock)
            {
                if (_prefixes.TryGetValue(topicPrefix, out var count))
                {
                    if (count <= 1)
                    {
                        _prefixes.Remove(topicPrefix);
                    }
                    else
                    {
                        _prefixes[topicPrefix] = count - 1;
                    }
                }
            }
        }

        /// <summary>
        /// Detaches from the hub
        /// </summary>
        public override void Dispose()
        {
            _hub.Detach(this);
            base.Dispose();
        }
    }
}
