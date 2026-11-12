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

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Blocks a service process until it receives Ctrl+C or SIGTERM
    /// </summary>
    public static class ServiceLifetime
    {
        private static readonly ManualResetEventSlim Stopping = new();

        static ServiceLifetime()
        {
            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                Stopping.Set();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stopping.Set();
        }

        /// <summary>
        /// Signalled when the process is asked to stop
        /// </summary>
        public static WaitHandle StoppingHandle => Stopping.WaitHandle;

        /// <summary>
        /// True once the process is asked to stop
        /// </summary>
        public static bool IsStopping => Stopping.IsSet;

        /// <summary>
        /// Blocks until the process is asked to stop
        /// </summary>
        public static void WaitForShutdown()
        {
            Stopping.Wait();
        }

        /// <summary>
        /// Requests the process to stop
        /// </summary>
        public static void RequestShutdown()
        {
            Stopping.Set();
        }
    }
}
