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

using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;

namespace QuantConnect.Services.MessageBroker
{
    /// <summary>
    /// Message broker entry point
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Binds <c>message-broker-frontend</c> (publishers, default tcp://*:5560) and
        /// <c>message-broker-backend</c> (subscribers, default tcp://*:5561)
        /// </summary>
        public static void Main(string[] args)
        {
            MessageBusFactory.ApplyServiceConfiguration(args);
            Log.LogHandler = new ConsoleLogHandler();
            var frontend = Config.Get("message-broker-frontend", "tcp://*:5560");
            var backend = Config.Get("message-broker-backend", "tcp://*:5561");
            using var broker = new Messaging.Bus.MessageBroker(frontend, backend);
            Log.Trace($"MessageBroker: forwarding {frontend} -> {backend}");
            ServiceLifetime.WaitForShutdown();
        }
    }
}
