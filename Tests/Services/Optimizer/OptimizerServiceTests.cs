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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuantConnect.Optimizer.Strategies;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Optimizer;
using QuantConnect.Services.Results;

namespace QuantConnect.Tests.Services.Optimizer
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;
    using ContractParameter = QuantConnect.Services.Contracts.OptimizationParameter;

    /// <summary>
    /// The optimizer service on the in-memory bus with a fake job-orchestrator and a file-system object store standing in for
    /// MinIO. A fake worker answers every submitted child backtest with a result JSON whose statistics are a known function of
    /// its parameters, so the best parameter set (after constraints) is known in advance.
    /// </summary>
    [TestFixture, Category("Services"), NonParallelizable]
    public class OptimizerServiceTests
    {
        private const string Sharpe = "TotalPerformance.PortfolioStatistics.SharpeRatio";
        private string _root;
        private FileSystemResultObjectStore _store;
        private InMemoryMessageBus _bus;
        private OptimizationCatalog _catalog;
        private FakeOrchestrator _orchestrator;
        private OptimizerSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lean-optimizer-tests-" + Guid.NewGuid().ToString("N"));
            _store = new FileSystemResultObjectStore(_root);
            _bus = new InMemoryMessageBus();
            _settings = new OptimizerSettings { MaxConcurrentBacktests = 8, HeartbeatInterval = TimeSpan.FromMinutes(5), ReconcileInterval = TimeSpan.FromMinutes(5) };
            _catalog = new OptimizationCatalog(_settings);
            _orchestrator = new FakeOrchestrator(_catalog);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public async Task GridSearchRunsEveryParameterSetAsAChildBacktestAndPublishesTheBestOne()
        {
            var request = Request(maxConcurrent: 3, constraints: new List<OptimizationConstraint>
            {
                new() { Target = "TotalPerformance.PortfolioStatistics.Drawdown", Operator = ConstraintOperator.LessOrEqual, TargetValue = 0.15m },
            });

            var disposition = await RunWithWorker(request);

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            Assert.AreEqual(9, _orchestrator.Submitted.Count, "3x3 grid");
            Assert.LessOrEqual(_orchestrator.MaxInFlight, 3, "maximumConcurrentBacktests bounds the children in flight");
            Assert.AreEqual(3, _orchestrator.MaxInFlight);
            CollectionAssert.AreEquivalent(
                new[] { 10, 20, 30 }.SelectMany(fast => new[] { 100, 150, 200 }.Select(slow => $"{fast}/{slow}")),
                _orchestrator.Submitted.Select(s => $"{s.Request.Backtest.Parameters["ema-fast"]}/{s.Request.Backtest.Parameters["ema-slow"]}"));
            foreach (var (_, child, key) in _orchestrator.Submitted)
            {
                Assert.AreEqual(JobKind.Backtest, child.Kind);
                Assert.AreEqual(request.JobId, child.ParentJobId);
                Assert.AreEqual("ParameterizedAlgorithm", ((BuiltinAlgorithmRef)child.Backtest.Algorithm).TypeName);
                Assert.AreEqual("1", child.Backtest.Parameters["extra"], "template parameters are kept");
                StringAssert.StartsWith($"{request.JobId:D}-1-", key);
                StringAssert.StartsWith("EMA grid [ema-fast:", child.Backtest.Name);
            }

            var events = Events(request.JobId);
            Assert.AreEqual(MessageTypes.JobStarted, events.First().Envelope.Type);
            Assert.AreEqual(MessageTypes.JobCompleted, events.Last().Envelope.Type);
            Assert.That(events.Any(e => e.Envelope.Type == MessageTypes.JobProgress), "job.progress published");
            Assert.That(events.All(e => e.Envelope.AppId == AppIds.Optimizer && e.Envelope.Headers.JobKind == JobKind.Optimization));

            var completed = ContractJson.Deserialize<JobCompleted>(events.Last().Body.Span);
            // Best Sharpe is (30, 200) = 5, but its drawdown breaks the constraint: (30, 150) = 4.5 wins.
            Assert.AreEqual("30", completed.Statistics["Best Parameter ema-fast"]);
            Assert.AreEqual("150", completed.Statistics["Best Parameter ema-slow"]);
            Assert.AreEqual("4.5", completed.Statistics["Best Objective"]);
            Assert.AreEqual("9", completed.Statistics["Completed Backtests"]);
            Assert.AreEqual($"{request.JobId:D}/1/optimization-result.json", completed.Result.Key);
            Assert.AreEqual(Buckets.Results, completed.Result.Bucket);

            var document = JsonNode.Parse(await _store.GetVerifiedAsync(completed.Result));
            Assert.AreEqual("completed", (string)document["status"]);
            Assert.AreEqual("30", (string)document["best"]["parameters"]["ema-fast"]);
            Assert.AreEqual(9, document["children"].AsArray().Count);
            Assert.AreEqual(completed.Statistics["Best Backtest"], (string)document["best"]["backtestId"]);

            var progress = events.Where(e => e.Envelope.Type == MessageTypes.JobProgress).Select(e => ContractJson.Deserialize<JobProgress>(e.Body.Span)).Last();
            Assert.AreEqual(1, progress.Progress);
            Assert.AreEqual(9, progress.Optimization.Total);
            Assert.AreEqual(9, progress.Optimization.Completed);
            Assert.AreEqual(Guid.Parse(completed.Statistics["Best Backtest"]), progress.Optimization.BestJobId);

            var state = _catalog.Get(request.JobId);
            Assert.AreEqual(JobStatus.Completed, state.Status);
            Assert.AreEqual(9, state.Completed);
            Assert.AreEqual(new Dictionary<string, string> { ["extra"] = "1", ["ema-fast"] = "30", ["ema-slow"] = "150" }, state.Best.Parameters);
            var children = _catalog.Children(request.JobId);
            Assert.AreEqual(5d, children.First().Objective, "children rank by the objective, best first");
        }

        [Test]
        public async Task EulerSearchRunsThroughTheSameChildJobs()
        {
            var request = Request(maxConcurrent: 4, strategy: typeof(EulerSearchOptimizationStrategy).FullName, minStep: 5);
            var packet = OptimizationPacketFactory.Create(request.JobId, request.Optimization, _settings);
            Assert.IsInstanceOf<StepBaseOptimizationStrategySettings>(packet.OptimizationStrategySettings);

            var disposition = await RunWithWorker(request);

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            Assert.Greater(_orchestrator.Submitted.Count, 9, "Euler search refines around the best grid point");
            Assert.LessOrEqual(_orchestrator.MaxInFlight, 4);
            var completed = ContractJson.Deserialize<JobCompleted>(Events(request.JobId).Last().Body.Span);
            Assert.AreEqual(30m, decimal.Parse(completed.Statistics["Best Parameter ema-fast"], CultureInfo.InvariantCulture));
            Assert.AreEqual(200m, decimal.Parse(completed.Statistics["Best Parameter ema-slow"], CultureInfo.InvariantCulture));
        }

        [Test]
        public async Task FailedChildrenCountAsFailedBacktestsAndOnlyFinalFailuresEndThem()
        {
            var request = Request(maxConcurrent: 9);
            Guid? retried = null, reconciled = null;
            var disposition = await RunWithWorker(request, async (childId, child) =>
            {
                var fast = child.Backtest.Parameters["ema-fast"];
                var slow = child.Backtest.Parameters["ema-slow"];
                if (fast == "10" && slow == "100")
                {
                    // A worker failure the orchestrator retries: ignored by the optimizer; the final verdict comes from the orchestrator.
                    retried = childId;
                    await PublishChild(MessageTypes.JobFailed, childId, request.JobId, AppIds.BacktestWorker, new JobFailed
                    {
                        JobId = childId, Kind = JobKind.Backtest, Attempt = 1, ParentJobId = request.JobId, FailedAt = DateTimeOffset.UtcNow, Final = false,
                        Error = new ErrorInfo { Code = ErrorCode.EngineCrash, Message = "boom" },
                    });
                    await PublishChild(MessageTypes.JobFailed, childId, request.JobId, AppIds.Orchestrator, new JobFailed
                    {
                        JobId = childId, Kind = JobKind.Backtest, Attempt = 3, ParentJobId = request.JobId, FailedAt = DateTimeOffset.UtcNow, Final = true,
                        Error = new ErrorInfo { Code = ErrorCode.MaxAttemptsExceeded, Message = "attempt 3/3" },
                    });
                    return true;
                }
                if (fast == "20" && slow == "100")
                {
                    // Fails without any final event: only reconciliation against the orchestrator ends it.
                    reconciled = childId;
                    _orchestrator.Listed[JobStatus.Failed] = new List<Job> { Job(childId, JobStatus.Failed) };
                    return true;
                }
                return false;
            });

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            Assert.IsNotNull(retried);
            Assert.IsNotNull(reconciled);
            var completed = ContractJson.Deserialize<JobCompleted>(Events(request.JobId).Last().Body.Span);
            Assert.AreEqual("7", completed.Statistics["Completed Backtests"]);
            Assert.AreEqual("2", completed.Statistics["Failed Backtests"]);
            var children = _catalog.Children(request.JobId);
            Assert.AreEqual(JobStatus.Failed, children.Single(c => c.JobId == retried).Status);
            Assert.AreEqual(JobStatus.Failed, children.Single(c => c.JobId == reconciled).Status);
        }

        [Test]
        public async Task CancellingTheOptimizationThroughTheOrchestratorCancelsIt()
        {
            var request = Request(maxConcurrent: 2);
            var disposition = await RunWithWorker(request, async (childId, child) =>
            {
                await PublishChild(MessageTypes.JobCancelled, childId, request.JobId, AppIds.Orchestrator, new JobCancelled
                {
                    JobId = childId, Kind = JobKind.Backtest, Attempt = 1, ParentJobId = request.JobId, CancelledAt = DateTimeOffset.UtcNow,
                    Reason = ChildEventsHandler.ParentCancelledReason,
                });
                return true;
            });

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            var last = Events(request.JobId).Last();
            Assert.AreEqual(MessageTypes.JobCancelled, last.Envelope.Type);
            Assert.AreEqual(ChildEventsHandler.ParentCancelledReason, ContractJson.Deserialize<JobCancelled>(last.Body.Span).Reason);
            Assert.AreEqual(2, _orchestrator.Submitted.Count, "nothing more is submitted after the cancellation");
            Assert.That(_orchestrator.Cancelled.All(c => c.Reason == BusLeanOptimizer.AbortReason));
            Assert.AreEqual(JobStatus.Cancelled, _catalog.Get(request.JobId).Status);
        }

        [Test]
        public async Task InvalidStrategyFailsFinally()
        {
            var request = Request(strategy: "QuantConnect.Optimizer.Strategies.NoSuchStrategy");
            var disposition = await RunWithWorker(request);

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            Assert.AreEqual(0, _orchestrator.Submitted.Count);
            var failed = ContractJson.Deserialize<JobFailed>(Events(request.JobId).Last().Body.Span);
            Assert.IsTrue(failed.Final);
            Assert.AreEqual(ErrorCode.InvalidRequest, failed.Error.Code);
            StringAssert.Contains("NoSuchStrategy", failed.Error.Message);
        }

        [Test]
        public async Task DispatchClaimsTheAttemptAndDropsGoneJobs()
        {
            var request = Request(maxConcurrent: 9);
            var runner = Runner();
            _orchestrator.Gone = true;
            var message = Dispatch(request);
            Assert.AreEqual(MessageDisposition.Ack, await runner.HandleAsync(message, CancellationToken.None));
            Assert.IsEmpty(Events(request.JobId));

            _orchestrator.Gone = false;
            _orchestrator.GrantAttempt = 2;
            Assert.AreEqual(MessageDisposition.Ack, await RunWithWorker(request, null, () => runner.HandleAsync(message, CancellationToken.None)));
            var events = Events(request.JobId);
            Assert.That(events.All(e => e.Envelope.Headers.Attempt == 2), "events carry the attempt granted by job-orchestrator");
            Assert.AreEqual($"{request.JobId:D}/2/optimization-result.json", ContractJson.Deserialize<JobCompleted>(events.Last().Body.Span).Result.Key);
            StringAssert.StartsWith($"{request.JobId:D}-2-", _orchestrator.Submitted[0].Key);
        }

        [Test]
        public async Task ApiServesEstimateStateAndChildren()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string> { ["Optimizer:Consume"] = "false" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLeanInMemoryMessaging(_bus);
            builder.AddOptimizerService(_orchestrator, _store, useRabbitMq: false);
            await using var app = builder.Build();
            app.MapOptimizerApi();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(address + "/") };

            var request = Request(maxConcurrent: 3);
            var estimate = await http.PostAsync("internal/v1/optimizations/estimate",
                new StringContent(ContractJson.Serialize(request.Optimization), Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.OK, estimate.StatusCode);
            Assert.AreEqual(9, (int)JsonNode.Parse(await estimate.Content.ReadAsStringAsync())["totalBacktests"]);

            var invalid = await http.PostAsync("internal/v1/optimizations/estimate",
                new StringContent(ContractJson.Serialize(request.Optimization with { Strategy = "Nope" }), Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

            Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync($"internal/v1/optimizations/{request.JobId:D}")).StatusCode);

            _catalog = app.Services.GetRequiredService<OptimizationCatalog>();
            _orchestrator.Catalog = _catalog;
            var runner = app.Services.GetRequiredService<OptimizationRunner>();
            var handler = app.Services.GetRequiredService<ChildEventsHandler>();
            await RunWithWorker(request, null, () => runner.RunAsync(request, 1, request.Results, CancellationToken.None), handler);

            var state = ContractJson.Deserialize<OptimizationState>(await http.GetByteArrayAsync($"internal/v1/optimizations/{request.JobId:D}"));
            Assert.AreEqual(JobStatus.Completed, state.Status);
            Assert.AreEqual(9, state.TotalBacktests);
            Assert.AreEqual(9, state.Completed);
            Assert.AreEqual("30", state.Best.Parameters["ema-fast"]);
            Assert.AreEqual($"{request.JobId:D}/1/optimization-result.json", state.Result.Key);

            var page = ContractJson.Deserialize<Page<ChildBacktest>>(await http.GetByteArrayAsync($"internal/v1/optimizations/{request.JobId:D}/children?limit=4"));
            Assert.AreEqual(4, page.Items.Count);
            Assert.AreEqual(5d, page.Items[0].Objective);
            Assert.AreEqual("4", page.Next);
            var rest = ContractJson.Deserialize<Page<ChildBacktest>>(await http.GetByteArrayAsync($"internal/v1/optimizations/{request.JobId:D}/children?cursor=4&status=completed"));
            Assert.AreEqual(5, rest.Items.Count);
            Assert.IsNull(rest.Next);
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("openapi/v1.json")).StatusCode);
            await app.StopAsync();
        }

        [Test]
        public void EstimateAndParameterMapping()
        {
            var spec = Request().Optimization;
            Assert.AreEqual(9, OptimizationPacketFactory.Estimate(spec, _settings));
            var packet = OptimizationPacketFactory.Create(Guid.NewGuid(), spec with
            {
                MaximumConcurrentBacktests = 100,
                Parameters = spec.Parameters.Append(new ContractParameter { Name = "fixed", Value = "abc" }).ToList(),
            }, _settings);
            Assert.AreEqual(_settings.MaxConcurrentBacktests, packet.MaximumConcurrentBacktests, "capped by Optimizer:MaxConcurrentBacktests");
            Assert.AreEqual(3, packet.OptimizationParameters.Count);
            Assert.AreEqual(OptimizationPacketFactory.DefaultStrategy, packet.OptimizationStrategy);
            Assert.AreEqual(9, OptimizationPacketFactory.Estimate(spec with { Parameters = spec.Parameters.Append(new ContractParameter { Name = "fixed", Value = "abc" }).ToList() }, _settings));
            Assert.Throws<InvalidOptimizationException>(() => OptimizationPacketFactory.Create(Guid.NewGuid(), spec with
            {
                Parameters = new List<ContractParameter> { new() { Name = "broken", Min = 1 } },
            }, _settings));
            var settingsJson = System.Text.Json.JsonDocument.Parse("{\"default-segment-amount\": 3, \"$type\": \"System.IO.FileInfo, System.IO\"}").RootElement;
            var euler = OptimizationPacketFactory.Create(Guid.NewGuid(), spec with { Strategy = typeof(EulerSearchOptimizationStrategy).FullName, StrategySettings = settingsJson }, _settings);
            Assert.AreEqual(3, ((StepBaseOptimizationStrategySettings)euler.OptimizationStrategySettings).DefaultSegmentAmount, "settings bind without type-name handling");
        }

        private OptimizationRunner Runner() => new(_bus, _orchestrator, _store, _catalog, _settings, NullLoggerFactory.Instance);

        /// <summary>
        /// Runs the optimization while playing the backtest workers: every new child gets <paramref name="outcome"/> (return true when it
        /// published the child's events itself), otherwise job.started + job.completed with <see cref="ResultJson"/> uploaded to the store.
        /// </summary>
        private async Task<MessageDisposition> RunWithWorker(OptimizationRequested request, Func<Guid, JobRequest, Task<bool>> outcome = null,
            Func<Task<MessageDisposition>> run = null, ChildEventsHandler handler = null)
        {
            handler ??= new ChildEventsHandler(_catalog, _store, NullLogger<ChildEventsHandler>.Instance);
            var runner = Runner();
            await using var subscription = await _bus.ConsumeAsync(Queues.OptimizerChildren, handler.HandleAsync);
            var task = run != null ? Task.Run(run) : Task.Run(() => runner.RunAsync(request, 1, request.Results, CancellationToken.None));
            var answered = new HashSet<Guid>();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                List<(Guid Id, JobRequest Request, string Key)> pending;
                lock (_orchestrator.Submitted)
                {
                    pending = _orchestrator.Submitted.Where(s => !answered.Contains(s.Id)).ToList();
                }
                foreach (var (childId, child, _) in pending)
                {
                    answered.Add(childId);
                    if (outcome == null || !await outcome(childId, child))
                    {
                        await CompleteChild(childId, child, request.JobId);
                    }
                }
                await _bus.DeliverAllAsync();
                if (_orchestrator.Listed.Count > 0 && _catalog.TryGetRunning(request.JobId, out var optimizer))
                {
                    await runner.ReconcileOnceAsync(optimizer, CancellationToken.None);
                }
                await Task.WhenAny(task, Task.Delay(10));
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"optimization did not finish; {_orchestrator.Submitted.Count} children submitted");
                }
            }
            return await task;
        }

        private async Task CompleteChild(Guid childId, JobRequest child, Guid parentId)
        {
            var fast = decimal.Parse(child.Backtest.Parameters["ema-fast"], CultureInfo.InvariantCulture);
            var slow = decimal.Parse(child.Backtest.Parameters["ema-slow"], CultureInfo.InvariantCulture);
            var result = await _store.PutRefAsync(Buckets.Results, $"{childId:D}/1/{childId:D}.json", Encoding.UTF8.GetBytes(ResultJson(fast, slow)), "application/json");
            await PublishChild(MessageTypes.JobStarted, childId, parentId, AppIds.BacktestWorker, new JobStarted
            {
                JobId = childId, Kind = JobKind.Backtest, Attempt = 1, ParentJobId = parentId, StartedAt = DateTimeOffset.UtcNow, WorkerId = "backtest-worker-1",
                Results = ResultsPrefix.For(childId, 1),
            });
            await PublishChild(MessageTypes.JobCompleted, childId, parentId, AppIds.BacktestWorker, new JobCompleted
            {
                JobId = childId, Kind = JobKind.Backtest, Attempt = 1, ParentJobId = parentId, CompletedAt = DateTimeOffset.UtcNow, Result = result, Artifacts = new(),
                Statistics = new Dictionary<string, string> { ["Sharpe Ratio"] = Objective(fast, slow).ToString(CultureInfo.InvariantCulture) },
            });
        }

        /// <summary>Sharpe = fast/10 + slow/100 (best at 30/200); (30, 200) also has a 20% drawdown.</summary>
        private static decimal Objective(decimal fast, decimal slow) => fast / 10m + slow / 100m;

        private static string ResultJson(decimal fast, decimal slow) => new JsonObject
        {
            ["statistics"] = new JsonObject { ["Sharpe Ratio"] = Objective(fast, slow).ToString(CultureInfo.InvariantCulture), ["Total Orders"] = "1" },
            ["totalPerformance"] = new JsonObject
            {
                ["portfolioStatistics"] = new JsonObject { ["sharpeRatio"] = Objective(fast, slow), ["drawdown"] = fast == 30 && slow == 200 ? 0.2m : 0.1m },
            },
        }.ToJsonString();

        private Task PublishChild<T>(string type, Guid childId, Guid parentId, string appId, T body) =>
            _bus.PublishAsync(OutgoingMessage.Create(Exchanges.Events, RoutingKeys.EventFor(type, JobKind.Backtest, childId),
                MessageEnvelope.Create(type, childId, 1, appId, JobKind.Backtest, parentId), body));

        private List<InMemoryMessageBus.PublishedMessage> Events(Guid jobId) =>
            _bus.Published.Where(m => m.Exchange == Exchanges.Events && m.Envelope.Headers.JobId == jobId && m.Envelope.Type != MessageTypes.JobHeartbeat).ToList();

        private static ReceivedMessage Dispatch(OptimizationRequested request) => new()
        {
            Queue = Queues.Optimization, Exchange = Exchanges.Jobs, RoutingKey = RoutingKeys.Dispatch(JobKind.Optimization),
            Envelope = MessageEnvelope.Create(MessageTypes.OptimizationRequested, request.JobId, request.Attempt, AppIds.Orchestrator, JobKind.Optimization),
            Body = ContractJson.SerializeToUtf8Bytes(request),
        };

        private static Job Job(Guid id, JobStatus status) => new() { Id = id, Kind = JobKind.Backtest, Status = status, Attempt = 3, SubmittedBy = "optimizer", CreatedAt = DateTimeOffset.UtcNow };

        private static OptimizationRequested Request(int? maxConcurrent = 3, string strategy = null, List<OptimizationConstraint> constraints = null, decimal? minStep = null)
        {
            var jobId = Guid.NewGuid();
            return new OptimizationRequested
            {
                JobId = jobId, Attempt = 1, SubmittedAt = DateTimeOffset.UtcNow, SubmittedBy = "user-42", Results = ResultsPrefix.For(jobId, 1),
                Optimization = new OptimizationSpec
                {
                    Name = "EMA grid",
                    Backtest = new BacktestSpec { Algorithm = new BuiltinAlgorithmRef { TypeName = "ParameterizedAlgorithm" }, Parameters = new() { ["extra"] = "1" } },
                    Strategy = strategy,
                    Criterion = new OptimizationCriterion { Target = Sharpe, Extremum = Extremum.Max },
                    Constraints = constraints,
                    Parameters = new List<ContractParameter>
                    {
                        new() { Name = "ema-fast", Min = 10, Max = 30, Step = 10, MinStep = minStep },
                        new() { Name = "ema-slow", Min = 100, Max = 200, Step = 50, MinStep = minStep },
                    },
                    MaximumConcurrentBacktests = maxConcurrent,
                },
            };
        }

        private sealed class FakeOrchestrator(OptimizationCatalog catalog) : IOrchestratorJobs
        {
            public OptimizationCatalog Catalog { get; set; } = catalog;
            public List<(Guid Id, JobRequest Request, string Key)> Submitted { get; } = new();
            public List<(Guid Id, string Reason)> Cancelled { get; } = new();
            public Dictionary<JobStatus, List<Job>> Listed { get; } = new();
            public int MaxInFlight { get; private set; }
            public bool Gone { get; set; }
            public int? GrantAttempt { get; set; }

            public Task<AttemptGrant> StartAttemptAsync(Guid jobId, string workerId, long deliveryCount, int? attempt, CancellationToken cancellationToken)
            {
                if (Gone) return Task.FromResult(new AttemptGrant(AttemptOutcome.Gone, Detail: "job is cancelled"));
                var granted = GrantAttempt ?? attempt ?? 1;
                return Task.FromResult(new AttemptGrant(AttemptOutcome.Granted, new JobAttempt { JobId = jobId, Attempt = granted, Results = ResultsPrefix.For(jobId, granted) }));
            }

            public Task<Guid> SubmitAsync(JobRequest request, string idempotencyKey, CancellationToken cancellationToken)
            {
                var id = Guid.NewGuid();
                lock (Submitted)
                {
                    Submitted.Add((id, request, idempotencyKey));
                    // Children LEAN is waiting on when this one is submitted, plus this one.
                    var inFlight = (Catalog.TryGetRunning(request.ParentJobId!.Value, out var optimizer) ? optimizer.RunningChildIds.Count : 0) + 1;
                    MaxInFlight = Math.Max(MaxInFlight, inFlight);
                }
                return Task.FromResult(id);
            }

            public Task CancelAsync(Guid jobId, string reason, CancellationToken cancellationToken)
            {
                lock (Cancelled) Cancelled.Add((jobId, reason));
                return Task.CompletedTask;
            }

            public Task<List<Job>> ListChildrenAsync(Guid parentJobId, JobStatus status, CancellationToken cancellationToken) =>
                Task.FromResult(Listed.TryGetValue(status, out var jobs) ? jobs : new List<Job>());
        }
    }
}
