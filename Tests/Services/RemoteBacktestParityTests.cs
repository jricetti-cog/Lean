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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Algorithm.CSharp;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine.DataFeeds.Remote;
using QuantConnect.Messaging.Bus;
using QuantConnect.Services;
using QuantConnect.Services.BacktestWorker;
using QuantConnect.Services.MarketData;

namespace QuantConnect.Tests.Services
{
    /// <summary>
    /// Runs regression algorithms with every data file served by the Market Data Service over the bus
    /// and asserts the exact regression statistics (including the order list hash) of the in-process run
    /// </summary>
    [TestFixture, NonParallelizable]
    public class RemoteBacktestParityTests
    {
        [TestCase("in-memory", nameof(BasicTemplateAlgorithm))]
        [TestCase("netmq", nameof(BasicTemplateAlgorithm))]
        [TestCase("in-memory", nameof(BasicTemplateDailyAlgorithm))]
        public void RemoteDataProducesIdenticalBacktest(string bus, string algorithm)
        {
            var cache = Path.Combine(Path.GetTempPath(), "lean-remote-cache-" + Guid.NewGuid().ToString("N"));
            (MessageBroker Broker, string PublishAddress, string SubscribeAddress) broker = default;
            var configuration = new Dictionary<string, string>
            {
                ["data-provider"] = typeof(RemoteDataProvider).FullName,
                ["remote-data-cache-folder"] = cache,
                ["message-bus"] = bus
            };
            if (bus == "netmq")
            {
                broker = ServiceTestHelpers.StartBroker();
                configuration["message-bus-publish-address"] = broker.PublishAddress;
                configuration["message-bus-subscribe-address"] = broker.SubscribeAddress;
            }
            foreach (var (key, value) in configuration)
            {
                Config.Set(key, value);
            }

            // the engine sees only an empty cache folder, filled through the bus exactly like a worker's
            var dataFolder = Config.Get("data-folder");
            var serviceBus = MessageBusFactory.Create("parity-market-data");
            var service = new MarketDataService(serviceBus, new MarketDataStore(Globals.DataFolder)).Start();
            try
            {
                using (var workerBus = MessageBusFactory.Create("parity-worker"))
                {
                    Assert.Greater(RemoteDataProvider.SynchronizeFolders(workerBus, cache, RemoteDataProvider.DefaultReferenceData.ToArray()), 0);
                }
                Config.Set("data-folder", cache + Path.DirectorySeparatorChar);
                Globals.Reset();

                var expected = ((Interfaces.IRegressionAlgorithmDefinition)Activator.CreateInstance(
                    typeof(BasicTemplateAlgorithm).Assembly.GetType("QuantConnect.Algorithm.CSharp." + algorithm))).ExpectedStatistics;
                AlgorithmRunner.RunLocalBacktest(algorithm, expected, Language.CSharp, AlgorithmStatus.Completed,
                    customConfigurations: configuration);

                var fetched = Directory.GetFiles(cache, "*.zip", SearchOption.AllDirectories);
                Assert.IsNotEmpty(fetched, "Expected the backtest data to be served by the Market Data Service");
            }
            finally
            {
                Config.Set("data-folder", dataFolder);
                Globals.Reset();
                service.Dispose();
                serviceBus.Dispose();
                broker.Broker?.Dispose();
                Config.Set("data-provider", "DefaultDataProvider");
                Config.Set("remote-data-cache-folder", "");
                Config.Set("message-bus", "");
                Config.Set("message-bus-publish-address", "");
                Config.Set("message-bus-subscribe-address", "");
                if (Directory.Exists(cache))
                {
                    Directory.Delete(cache, true);
                }
            }
        }

        [Test]
        public void BacktestWorkerProcessProducesIdenticalBacktest()
        {
            var launcher = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Launcher", "bin", "Release"));
            if (!File.Exists(Path.Combine(launcher, "QuantConnect.Lean.Launcher.dll")))
            {
                Assert.Ignore($"Launcher build not found in {launcher}");
            }
            var root = Path.Combine(Path.GetTempPath(), "lean-worker-parity-" + Guid.NewGuid().ToString("N"));
            var broker = ServiceTestHelpers.StartBroker();
            var busSettings = new Dictionary<string, string>
            {
                ["message-bus"] = "netmq",
                ["message-bus-publish-address"] = broker.PublishAddress,
                ["message-bus-subscribe-address"] = broker.SubscribeAddress
            };
            foreach (var (key, value) in busSettings)
            {
                Config.Set(key, value);
            }
            var serviceBus = MessageBusFactory.Create("parity-market-data");
            var workerBus = MessageBusFactory.Create("parity-worker");
            var service = new MarketDataService(serviceBus, new MarketDataStore(Globals.DataFolder)).Start();
            try
            {
                var worker = new BacktestWorker(workerBus, new BacktestWorkerSettings
                {
                    LauncherDirectory = launcher,
                    LeanConfigTemplate = Path.Combine(launcher, "config.json"),
                    CacheFolder = Path.Combine(root, "cache"),
                    ResultsFolder = Path.Combine(root, "results"),
                    EngineSettings = busSettings
                });
                var job = new JobRecord
                {
                    JobId = "parity-" + Guid.NewGuid().ToString("N"),
                    Submission = new JobSubmission { Type = JobType.Backtest, AlgorithmTypeName = nameof(BasicTemplateAlgorithm) }
                };

                // the final statistics, including OrderListHash, reach the bus from the engine process through BusMessagingHandler
                Dictionary<string, string> statistics = null;
                using var finished = new ManualResetEventSlim();
                using var events = serviceBus.Subscribe<JobEvent>(Topics.JobEvents(job.JobId), (jobEvent, _) =>
                {
                    if (jobEvent.Statistics?.ContainsKey("OrderListHash") == true)
                    {
                        statistics = jobEvent.Statistics;
                        finished.Set();
                    }
                });

                Assert.AreEqual(JobStatus.Completed, worker.Execute(job));
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(10)), "Expected the final statistics on the job event topic");
                foreach (var (name, value) in new BasicTemplateAlgorithm().ExpectedStatistics)
                {
                    Assert.AreEqual(value, statistics[name], name);
                }
                var result = JObject.Parse(File.ReadAllText(Path.Combine(root, "results", job.JobId, job.JobId + ".json")));
                Assert.AreEqual(statistics["Net Profit"], result["statistics"]["Net Profit"].ToString());
                Assert.IsNotEmpty(Directory.GetFiles(Path.Combine(root, "cache"), "*.zip", SearchOption.AllDirectories),
                    "Expected the backtest data to be served by the Market Data Service");
            }
            finally
            {
                service.Dispose();
                serviceBus.Dispose();
                workerBus.Dispose();
                broker.Broker.Dispose();
                foreach (var key in busSettings.Keys)
                {
                    Config.Set(key, "");
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
