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
using System.Collections;
using System.Globalization;
using QuantConnect.Configuration;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Creates the message bus selected by configuration:
    /// <c>message-bus</c> = <c>netmq</c> (default) or <c>in-memory</c>,
    /// <c>message-bus-publish-address</c> and <c>message-bus-subscribe-address</c> for NetMQ.
    /// </summary>
    public static class MessageBusFactory
    {
        /// <summary>
        /// Default broker frontend address used by publishers
        /// </summary>
        public const string DefaultPublishAddress = "tcp://localhost:5560";

        /// <summary>
        /// Default broker backend address used by subscribers
        /// </summary>
        public const string DefaultSubscribeAddress = "tcp://localhost:5561";

        /// <summary>
        /// Creates and connects a bus from configuration, waiting until it is ready
        /// </summary>
        public static IMessageBus Create(string instanceId = null, TimeSpan? readyTimeout = null)
        {
            IMessageBus bus;
            var kind = Config.Get("message-bus", "netmq");
            if (kind.Equals("in-memory", StringComparison.OrdinalIgnoreCase))
            {
                bus = new InMemoryMessageBus(InMemoryMessageHub.Default, instanceId);
            }
            else
            {
                bus = new NetMqMessageBus(
                    Config.Get("message-bus-publish-address", DefaultPublishAddress),
                    Config.Get("message-bus-subscribe-address", DefaultSubscribeAddress),
                    instanceId);
            }

            var timeout = readyTimeout ?? TimeSpan.FromSeconds(Config.GetInt("message-bus-ready-timeout-seconds", 60));
            if (!bus.WaitForReady(timeout))
            {
                bus.Dispose();
                throw new MessageBusException($"Message bus '{kind}' was not reachable within {timeout.TotalSeconds}s");
            }
            return bus;
        }

        /// <summary>
        /// Applies service configuration overrides: environment variables prefixed with <c>LEAN_</c>
        /// (<c>LEAN_MESSAGE_BUS_PUBLISH_ADDRESS</c> sets <c>message-bus-publish-address</c>) followed by
        /// <c>--key value</c> command line arguments
        /// </summary>
        public static void ApplyServiceConfiguration(string[] args)
        {
            foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                var name = (string)variable.Key;
                if (name.StartsWith("LEAN_", StringComparison.Ordinal) && name.Length > 5)
                {
                    Config.Set(name[5..].ToLower(CultureInfo.InvariantCulture).Replace('_', '-'), (string)variable.Value);
                }
            }
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    Config.Set(args[i][2..], args[++i]);
                }
            }
        }
    }
}
