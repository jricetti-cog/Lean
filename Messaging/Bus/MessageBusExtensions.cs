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
using QuantConnect.Services;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Typed helpers on top of <see cref="IMessageBus"/>
    /// </summary>
    public static class MessageBusExtensions
    {
        /// <summary>
        /// Default request timeout
        /// </summary>
        public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Publishes a typed message
        /// </summary>
        public static void Publish<T>(this IMessageBus bus, string topic, T body, string contractVersion, byte[] payload = null)
        {
            bus.Publish(MessageEnvelope.Create(topic, body, contractVersion, payload));
        }

        /// <summary>
        /// Sends a typed request, blocks for the reply and throws if the responder reported an error
        /// </summary>
        public static MessageEnvelope Request<T>(this IMessageBus bus, string topic, T body, string contractVersion,
            byte[] payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var request = MessageEnvelope.Create(topic, body, contractVersion, payload);
            MessageEnvelope reply;
            try
            {
                reply = bus.RequestAsync(request, timeout ?? DefaultTimeout, cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MessageBusException($"Request '{topic}' was cancelled");
            }
            if (reply.Error != null)
            {
                throw new MessageBusException($"Request '{topic}' failed: {reply.Error}");
            }
            return reply;
        }

        /// <summary>
        /// Sends a typed request and deserializes the typed reply
        /// </summary>
        public static TResponse Request<TRequest, TResponse>(this IMessageBus bus, string topic, TRequest body, string contractVersion,
            TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            return bus.Request(topic, body, contractVersion, null, timeout, cancellationToken).GetBody<TResponse>();
        }

        /// <summary>
        /// Registers a typed request handler
        /// </summary>
        public static IDisposable Respond<TRequest, TResponse>(this IMessageBus bus, string topic, Func<TRequest, MessageEnvelope, TResponse> handler)
        {
            return bus.Respond(topic, request => request.CreateReply(handler(request.GetBody<TRequest>(), request)));
        }

        /// <summary>
        /// Registers a typed subscription
        /// </summary>
        public static IDisposable Subscribe<T>(this IMessageBus bus, string topicPrefix, Action<T, MessageEnvelope> handler)
        {
            return bus.Subscribe(topicPrefix, envelope => handler(envelope.GetBody<T>(), envelope));
        }
    }
}
