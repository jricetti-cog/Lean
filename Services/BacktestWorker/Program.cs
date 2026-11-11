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
using System.IO;
using System.Linq;
using System.Threading;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;

namespace QuantConnect.Services.BacktestWorker
{
    /// <summary>
    /// Backtest worker entry point
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Starts the worker. Settings: <c>worker-id</c>, <c>worker-launcher-directory</c>, <c>worker-lean-config</c>,
        /// <c>worker-cache-folder</c>, <c>worker-results-folder</c>, <c>worker-job-types</c>, <c>research-folder</c>,
        /// <c>research-command</c>, message bus settings
        /// </summary>
        public static void Main(string[] args)
        {
            MessageBusFactory.ApplyServiceConfiguration(args);
            Log.LogHandler = new ConsoleLogHandler();

            var launcherDirectory = Path.GetFullPath(Config.Get("worker-launcher-directory",
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Launcher", "bin", "Release")));
            var settings = new BacktestWorkerSettings
            {
                LauncherDirectory = launcherDirectory,
                LeanConfigTemplate = Config.Get("worker-lean-config", Path.Combine(launcherDirectory, "config.json")),
                CacheFolder = Path.GetFullPath(Config.Get("worker-cache-folder", "worker-data/cache")),
                ResultsFolder = Path.GetFullPath(Config.Get("worker-results-folder", "worker-data/results")),
                ResearchFolder = Config.Get("research-folder", launcherDirectory),
                ResearchCommand = Config.Get("research-command", "jupyter")
            };
            settings.WorkerId = Config.Get("worker-id", settings.WorkerId);
            var jobTypes = Config.Get("worker-job-types");
            if (!string.IsNullOrEmpty(jobTypes))
            {
                settings.JobTypes = jobTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => Enum.Parse<JobType>(t, true)).ToList();
            }
            foreach (var key in new[] { "message-bus", "message-bus-publish-address", "message-bus-subscribe-address" })
            {
                var value = Config.Get(key);
                if (!string.IsNullOrEmpty(value))
                {
                    settings.EngineSettings[key] = value;
                }
            }

            using var bus = MessageBusFactory.Create(settings.WorkerId);
            using var stopping = new CancellationTokenSource();
            ThreadPool.RegisterWaitForSingleObject(ServiceLifetime.StoppingHandle, (_, _) => stopping.Cancel(), null, Timeout.Infinite, true);
            new BacktestWorker(bus, settings).Run(stopping.Token);
        }
    }
}
