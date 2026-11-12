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
using NetMQ;
using NetMQ.Sockets;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Forwarding broker for <see cref="NetMqMessageBus"/>: publishers connect to the frontend,
    /// subscribers connect to the backend and subscriptions are forwarded upstream.
    /// </summary>
    public class MessageBroker : IDisposable
    {
        private readonly XSubscriberSocket _frontend;
        private readonly XPublisherSocket _backend;
        private readonly NetMQPoller _poller;
        private readonly Proxy _proxy;

        /// <summary>
        /// Binds the broker sockets and starts forwarding
        /// </summary>
        /// <param name="frontendAddress">Bind address for publishers, for example tcp://*:5560</param>
        /// <param name="backendAddress">Bind address for subscribers, for example tcp://*:5561</param>
        public MessageBroker(string frontendAddress, string backendAddress)
        {
            _frontend = new XSubscriberSocket();
            _frontend.Options.ReceiveHighWatermark = 100000;
            _frontend.Bind(frontendAddress);
            _backend = new XPublisherSocket();
            _backend.Options.SendHighWatermark = 100000;
            _backend.Bind(backendAddress);
            _poller = new NetMQPoller { _frontend, _backend };
            _proxy = new Proxy(_frontend, _backend, null, _poller);
            _proxy.Start();
            _poller.RunAsync("MessageBroker");
        }

        /// <summary>
        /// Stops forwarding
        /// </summary>
        public void Dispose()
        {
            _proxy.Stop();
            _poller.Stop();
            _poller.Dispose();
            _frontend.Dispose();
            _backend.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
