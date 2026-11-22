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
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Orchestrator;

namespace QuantConnect.Tests.Services.Orchestrator
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    internal sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    internal static class Requests
    {
        public static JobRequest Backtest(string type = "BasicTemplateAlgorithm", Guid? parent = null) => new()
        {
            Kind = JobKind.Backtest, ParentJobId = parent,
            Backtest = new BacktestSpec { Name = "bt", Algorithm = new BuiltinAlgorithmRef { TypeName = type } },
        };

        public static JobRequest Optimization() => new()
        {
            Kind = JobKind.Optimization,
            Optimization = new OptimizationSpec
            {
                Backtest = Backtest().Backtest!, Criterion = new OptimizationCriterion { Target = "TotalPerformance.PortfolioStatistics.SharpeRatio", Extremum = Extremum.Max },
                Parameters = new() { new OptimizationParameter { Name = "fast", Min = 5, Max = 10, Step = 5 } },
            },
        };

        public static JobRequest Live() => new()
        {
            Kind = JobKind.Live, Live = new LiveSpec { Algorithm = new BuiltinAlgorithmRef { TypeName = "BasicTemplateAlgorithm" }, Brokerage = "PaperBrokerage" },
        };
    }

    [TestFixture, Category("Services")]
    public class JobStateMachineTests
    {
        [TestCase(JobStatus.Queued, JobStatus.Dispatched, true)]
        [TestCase(JobStatus.Dispatched, JobStatus.Running, true)]
        [TestCase(JobStatus.Running, JobStatus.Completed, true)]
        [TestCase(JobStatus.Running, JobStatus.Failed, true)]
        [TestCase(JobStatus.Running, JobStatus.Cancelled, true)]
        [TestCase(JobStatus.Running, JobStatus.Queued, true)]
        [TestCase(JobStatus.Queued, JobStatus.Completed, false)]
        [TestCase(JobStatus.Completed, JobStatus.Running, false)]
        [TestCase(JobStatus.Failed, JobStatus.Queued, false)]
        [TestCase(JobStatus.Cancelled, JobStatus.Running, false)]
        public void Transitions(JobStatus from, JobStatus to, bool allowed)
        {
            Assert.AreEqual(allowed, JobStateMachine.CanTransition(from, to));
        }

        [Test]
        public void DispatchedIsOnTheWire()
        {
            Assert.AreEqual("dispatched", JobStateMachine.ToWire(JobStatus.Dispatched));
            Assert.AreEqual(JobStatus.Dispatched, JobStateMachine.ParseStatus("dispatched"));
        }

        [Test]
        public void ValidRequestsHaveNoErrors()
        {
            CollectionAssert.IsEmpty(JobStateMachine.Validate(Requests.Backtest(), Buckets.Algorithms));
            CollectionAssert.IsEmpty(JobStateMachine.Validate(Requests.Live(), Buckets.Algorithms));
            CollectionAssert.IsEmpty(JobStateMachine.Validate(Requests.Optimization(), Buckets.Algorithms));
        }

        [Test]
        public void InvalidRequestsAreRejectedPerField()
        {
            var errors = JobStateMachine.Validate(new JobRequest { Kind = JobKind.Backtest, Live = Requests.Live().Live }, Buckets.Algorithms);
            CollectionAssert.AreEquivalent(new[] { "backtest", "live" }, errors.Keys);

            var dates = Requests.Backtest() with
            {
                Backtest = Requests.Backtest().Backtest! with { StartDate = new DateOnly(2024, 2, 1), EndDate = new DateOnly(2024, 1, 1), Cash = -1 },
            };
            CollectionAssert.AreEquivalent(new[] { "backtest.endDate", "backtest.cash" }, JobStateMachine.Validate(dates, Buckets.Algorithms).Keys);

            var foreignBucket = Requests.Backtest() with
            {
                Backtest = new BacktestSpec
                {
                    Algorithm = new UploadedAlgorithmRef { Language = AlgorithmLanguage.Python, Object = new ObjectRef { Bucket = "elsewhere", Key = "a.py", Size = 1, Sha256 = new string('0', 64) } },
                },
            };
            CollectionAssert.AreEquivalent(new[] { "backtest.algorithm.object.bucket" }, JobStateMachine.Validate(foreignBucket, Buckets.Algorithms).Keys);
            CollectionAssert.Contains(JobStateMachine.Validate(Requests.Live() with { ParentJobId = Guid.NewGuid() }, Buckets.Algorithms).Keys, "parentJobId");
            CollectionAssert.Contains(JobStateMachine.Validate(new JobRequest { Kind = JobKind.MarketData }, Buckets.Algorithms).Keys, "kind");
        }

        [TestCase(JobKind.Backtest, MessageTypes.BacktestRequested, "jobs.backtest.v1")]
        [TestCase(JobKind.Live, MessageTypes.LiveRequested, "jobs.live.v1")]
        [TestCase(JobKind.Optimization, MessageTypes.OptimizationRequested, "jobs.optimization.v1")]
        public void DispatchMessagesFollowTheContract(JobKind kind, string type, string key)
        {
            var id = Guid.NewGuid();
            var request = kind switch { JobKind.Live => Requests.Live(), JobKind.Optimization => Requests.Optimization(), _ => Requests.Backtest() };
            var message = JobStateMachine.Dispatch(id, request, 2, "alice", DateTimeOffset.UtcNow, null);
            message.Validate();
            Assert.AreEqual(Exchanges.Jobs, message.Exchange);
            Assert.AreEqual(key, message.RoutingKey);
            Assert.AreEqual(type, message.Envelope.Type);
            Assert.AreEqual(2, message.Envelope.Headers.Attempt);
            Assert.AreEqual(id, message.Envelope.Headers.JobId);
            Assert.IsTrue(message.Mandatory);
            Assert.IsTrue(JobStateMachine.IsDispatch(type));
            if (kind != JobKind.Optimization)
            {
                var body = ContractJson.Deserialize<JobSubmitted>(message.Body.Span);
                Assert.AreEqual($"{id:D}/2/", body.Results!.Prefix);
                Assert.AreEqual("alice", body.SubmittedBy);
            }
        }

        [Test]
        public void CancelGoesToTheControlExchangeWithoutMandatoryRouting()
        {
            var id = Guid.NewGuid();
            var message = JobStateMachine.Cancel(id, JobKind.Backtest, 1, null, "user", "alice", DateTimeOffset.UtcNow);
            message.Validate();
            Assert.AreEqual(Exchanges.Control, message.Exchange);
            Assert.AreEqual($"job.{id:D}.cancel.v1", message.RoutingKey);
            Assert.IsFalse(message.Mandatory);
            Assert.AreEqual("user", ContractJson.Deserialize<CancelJob>(message.Body.Span).Reason);
        }

        [TestCase("algo.py", "algo.py")]
        [TestCase("../../etc/passwd", "passwd")]
        [TestCase("bad name.py", null)]
        [TestCase("", null)]
        public void AlgorithmFileNamesAreSanitized(string input, string expected)
        {
            Assert.AreEqual(expected, IAlgorithmStore.SanitizeFileName(input));
        }
    }

    [TestFixture, Category("Services")]
    public class QueueMetricsTests
    {
        private sealed class FakeDepth : IQueueDepthSource
        {
            public Dictionary<string, QueueDepth> Depths { get; } = new();
            public int Calls { get; private set; }

            public Task<QueueDepth> GetAsync(string queue, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(Depths.TryGetValue(queue, out var d) ? d : new QueueDepth(queue, 0, 0, 0));
            }
        }

        [Test]
        public async Task HintsReportDepthAndSuggestReplicas()
        {
            var source = new FakeDepth();
            source.Depths[Queues.Backtest] = new QueueDepth(Queues.Backtest, 7, 2, 1);
            source.Depths[Queues.Live] = new QueueDepth(Queues.Live, 1, 3, 0);
            var time = new ManualTime();
            var metrics = new QueueMetrics(source, Options.Create(new OrchestratorOptions { JobsPerReplica = 2, MinReplicas = 1, MaxReplicas = 4 }), time, NullLogger<QueueMetrics>.Instance);

            var hints = await metrics.GetHintsAsync(CancellationToken.None);
            CollectionAssert.AreEqual(new[] { Queues.Backtest, Queues.Live, Queues.Optimization, Queues.Report }, hints.Select(h => h.Queue));
            var backtest = hints[0];
            Assert.AreEqual((7L, 2L, 1L, 4), (backtest.Ready, backtest.Unacked, backtest.Dead, backtest.SuggestedReplicas));
            Assert.AreEqual(4, hints[1].SuggestedReplicas, "one live worker per live job");
            Assert.AreEqual(1, hints[2].SuggestedReplicas, "min replicas");

            await metrics.GetHintsAsync(CancellationToken.None);
            Assert.AreEqual(4, source.Calls, "cached for 5 s");
            time.Advance(TimeSpan.FromSeconds(6));
            await metrics.GetHintsAsync(CancellationToken.None);
            Assert.AreEqual(8, source.Calls);
        }
    }
}
