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
using System.Text;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Messaging.Bus;
using QuantConnect.Services;
using QuantConnect.Services.ApiGateway;
using QuantConnect.Services.BacktestWorker;

namespace QuantConnect.Tests.Services
{
    [TestFixture]
    public class GatewayAndWorkerTests
    {
        private static DefaultHttpContext Request(string userId, string token, int timestamp)
        {
            var context = new DefaultHttpContext();
            var hash = Api.Api.CreateSecureHash(timestamp, token);
            context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userId}:{hash}"));
            context.Request.Headers["Timestamp"] = timestamp.ToStringInvariant();
            return context;
        }

        [Test]
        public void AuthenticatesLeanApiHash()
        {
            var authenticator = new GatewayAuthenticator(new Dictionary<string, string> { ["7"] = "secret" }, TimeSpan.FromMinutes(5));
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.AreEqual("7", authenticator.Authenticate(Request("7", "secret", now).Request));
            Assert.IsNull(authenticator.Authenticate(Request("7", "wrong", now).Request));
            Assert.IsNull(authenticator.Authenticate(Request("8", "secret", now).Request));
            Assert.IsNull(authenticator.Authenticate(Request("7", "secret", now - 3600).Request));
            Assert.IsNull(authenticator.Authenticate(new DefaultHttpContext().Request));
        }

        [Test]
        public void DispatcherLeasesJobsAndTracksStatus()
        {
            var hub = new InMemoryMessageHub();
            using var gatewayBus = new InMemoryMessageBus(hub);
            using var workerBus = new InMemoryMessageBus(hub);
            using var dispatcher = new JobDispatcher(gatewayBus);

            Assert.Throws<ArgumentException>(() => dispatcher.Submit("1", new JobSubmission { Type = JobType.Research }));
            var research = dispatcher.Submit("1", new JobSubmission { Type = JobType.Research, Notebook = "a.ipynb" });
            var backtest = dispatcher.Submit("1", new JobSubmission { Type = JobType.Backtest, AlgorithmTypeName = "BasicTemplateAlgorithm" });

            var lease = workerBus.Request<JobLeaseRequest, JobLeaseResponse>(Topics.JobsNext,
                new JobLeaseRequest { WorkerId = "w1", JobTypes = new List<JobType> { JobType.Backtest } }, ContractVersions.JobsV1);
            Assert.AreEqual(backtest.JobId, lease.Job.JobId);
            Assert.AreEqual(JobStatus.Assigned, dispatcher.Get(backtest.JobId).Status);
            Assert.AreEqual(JobStatus.Queued, dispatcher.Get(research.JobId).Status);

            workerBus.Publish(Topics.JobEvents(backtest.JobId), new JobEvent { JobId = backtest.JobId, Progress = 1, Statistics = new Dictionary<string, string> { ["Total Orders"] = "1" } }, ContractVersions.JobsV1);
            workerBus.Publish(Topics.JobsStatus, new JobStatusUpdate { JobId = backtest.JobId, Status = JobStatus.Completed, ExitCode = 0 }, ContractVersions.JobsV1);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (dispatcher.Get(backtest.JobId).Status != JobStatus.Completed && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(20);
            }
            var job = dispatcher.Get(backtest.JobId);
            Assert.AreEqual(JobStatus.Completed, job.Status);
            Assert.AreEqual("1", job.Statistics["Total Orders"]);
            Assert.AreEqual(1m, job.Progress);

            Assert.AreEqual(JobStatus.Cancelled, dispatcher.Cancel(research.JobId).Status);
        }

        [Test]
        public void WorkerConfiguresEngineWithRemoteHandlers()
        {
            var folder = Path.Combine(Path.GetTempPath(), "lean-worker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var template = Path.Combine(folder, "template.json");
                File.WriteAllText(template, "{ // comment\n \"environment\": \"backtesting\", \"environments\": { \"live-paper\": { \"live-mode\": true } } }");
                var worker = new BacktestWorker(null, new BacktestWorkerSettings
                {
                    LeanConfigTemplate = template,
                    CacheFolder = Path.Combine(folder, "cache"),
                    ResultsFolder = folder,
                    EngineSettings = new Dictionary<string, string> { ["message-bus-publish-address"] = "tcp://broker:5560" }
                });
                var job = new JobRecord { JobId = "live-1", Submission = new JobSubmission { Type = JobType.Live, AlgorithmTypeName = "BasicTemplateAlgorithm" } };
                var config = JObject.Parse(File.ReadAllText(worker.WriteEngineConfiguration(job, folder)));

                Assert.AreEqual("live-paper", config["environment"].ToString());
                Assert.AreEqual("QuantConnect.Lean.Engine.DataFeeds.Remote.RemoteDataProvider", config["data-provider"].ToString());
                Assert.AreEqual("QuantConnect.Messaging.Bus.BusMessagingHandler", config["messaging-handler"].ToString());
                Assert.AreEqual("QuantConnect.Lean.Engine.DataFeeds.Remote.RemoteDataQueueHandler", config["environments"]["live-paper"]["data-queue-handler"][0].ToString());
                Assert.AreEqual("live-1", config["job-id"].ToString());
                Assert.AreEqual("tcp://broker:5560", config["message-bus-publish-address"].ToString());
                Assert.IsTrue(config["close-automatically"].Value<bool>());
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
