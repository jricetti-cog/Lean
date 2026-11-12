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

using System.Net;
using System.Net.Sockets;
using QuantConnect.Messaging.Bus;

namespace QuantConnect.Tests.Services
{
    internal static class ServiceTestHelpers
    {
        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static (MessageBroker Broker, string PublishAddress, string SubscribeAddress) StartBroker()
        {
            var frontend = FreePort();
            var backend = FreePort();
            var broker = new MessageBroker($"tcp://127.0.0.1:{frontend}", $"tcp://127.0.0.1:{backend}");
            return (broker, $"tcp://127.0.0.1:{frontend}", $"tcp://127.0.0.1:{backend}");
        }
    }
}
