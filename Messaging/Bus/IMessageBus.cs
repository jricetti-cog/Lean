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
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Services;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Topic based publish/subscribe message bus used for communication between LEAN services.
    /// Request/reply is layered on top of publish/subscribe using a private per-instance reply topic.
    /// </summary>
    public interface IMessageBus : IDisposable
    {
        /// <summary>
        /// Unique id of this bus connection, used as the sender id and reply address
        /// </summary>
        string InstanceId { get; }

        /// <summary>
        /// Publishes a message to every subscriber of its topic
        /// </summary>
        void Publish(MessageEnvelope envelope);

        /// <summary>
        /// Subscribes to every topic starting with <paramref name="topicPrefix"/>.
        /// Handlers of a bus instance are invoked sequentially in arrival order.
        /// </summary>
        /// <returns>Dispose to unsubscribe</returns>
        IDisposable Subscribe(string topicPrefix, Action<MessageEnvelope> handler);

        /// <summary>
        /// Registers a request handler for a topic. Requests are handled concurrently and
        /// the returned envelope is sent back to the requester. Exceptions are returned as error replies.
        /// </summary>
        /// <returns>Dispose to unregister</returns>
        IDisposable Respond(string topic, Func<MessageEnvelope, MessageEnvelope> handler);

        /// <summary>
        /// Sends a request and waits for the reply
        /// </summary>
        Task<MessageEnvelope> RequestAsync(MessageEnvelope request, TimeSpan timeout, CancellationToken cancellationToken = default);

        /// <summary>
        /// Blocks until messages round trip through the bus, which also means earlier subscriptions are active
        /// </summary>
        bool WaitForReady(TimeSpan timeout);
    }
}
