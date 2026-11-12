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
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Queues;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;
using QuantConnect.Util;

namespace QuantConnect.Services.MarketData
{
    /// <summary>
    /// Market Data Service entry point
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Starts the service. Settings: <c>data-folder</c>, <c>data-downloader</c> (optional IDataDownloader type),
        /// <c>data-queue-handler</c> (live source, default FakeDataQueue), message bus settings.
        /// Every setting can be passed as LEAN_* environment variable or --key value argument.
        /// </summary>
        public static void Main(string[] args)
        {
            MessageBusFactory.ApplyServiceConfiguration(args);
            Globals.Reset();
            Log.LogHandler = new ConsoleLogHandler();
            Log.DebuggingEnabled = Config.GetBool("debug-mode");

            using var bus = MessageBusFactory.Create("market-data-" + Environment.MachineName);
            using var service = new MarketDataService(bus, new MarketDataStore(Globals.DataFolder), CreateDownloaderFactory(), CreateDataQueueHandlerFactory()).Start();
            Log.Trace("MarketDataService: ready");
            ServiceLifetime.WaitForShutdown();
            Log.Trace("MarketDataService: stopping");
        }

        private static Func<IDataDownloader> CreateDownloaderFactory()
        {
            var downloader = Config.Get("data-downloader");
            if (string.IsNullOrEmpty(downloader))
            {
                return null;
            }
            return () => Composer.Instance.GetExportedValueByTypeName<IDataDownloader>(downloader);
        }

        private static Func<IDataQueueHandler> CreateDataQueueHandlerFactory()
        {
            var handler = Config.Get("data-queue-handler", nameof(FakeDataQueue));
            if (string.IsNullOrEmpty(handler))
            {
                return null;
            }
            return () =>
            {
                if (handler == nameof(FakeDataQueue))
                {
                    var aggregator = new AggregationManager();
                    aggregator.Initialize(new DataAggregatorInitializeParameters { AlgorithmSettings = new AlgorithmSettings() });
                    return new FakeDataQueue(aggregator, Config.GetInt("fake-data-points-per-second-per-symbol", 100));
                }
                return Composer.Instance.GetExportedValueByTypeName<IDataQueueHandler>(handler);
            };
        }
    }
}
