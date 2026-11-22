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
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using QuantConnect.Services.BacktestWorker;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.LiveWorker;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;
using BusQueues = QuantConnect.Services.Messaging.Queues;
using QuantConnect.Services.Messaging.InMemory;

namespace QuantConnect.Tests.Services.LiveWorker
{
    [TestFixture, Parallelizable(ParallelScope.None)]
    public class LiveWorkerTests
    {
        private const string LauncherConfig = @"{
  ""environment"": ""backtesting"",
  ""job-queue-handler"": ""QuantConnect.Queues.JobQueue"",
  ""live-holdings"": ""[]"",
  ""environments"": { ""backtesting"": { ""live-mode"": false }, ""live-paper"": { ""live-mode"": true } },
}";

        private string _root;
        private WorkerOptions _engine;
        private LiveWorkerOptions _options;
        private InMemoryMessageBus _bus;
        private FakeServices _services;
        private FakeStore _store;
        private FakeLauncher _launcher;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "live-worker-tests", Guid.NewGuid().ToString("N"));
            var launcher = Directory.CreateDirectory(Path.Combine(_root, "launcher")).FullName;
            File.WriteAllText(Path.Combine(launcher, "config.json"), LauncherConfig);
            _engine = new WorkerOptions { LauncherDirectory = launcher, WorkRoot = Path.Combine(_root, "work"), DataFolder = "/Lean/Data/" };
            _options = new LiveWorkerOptions { WorkerId = "lean-live-1", HeartbeatIntervalSeconds = 1, PollIntervalMilliseconds = 10, LiquidateGraceSeconds = 0, StopGraceSeconds = 5 };
            _bus = new InMemoryMessageBus();
            _services = new FakeServices();
            _store = new FakeStore();
            _launcher = new FakeLauncher();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private LiveJobRunner Runner() => new(_bus, _bus, _services, _store, _launcher,
            new LiveEngineEnvironment(new Dictionary<string, string> { ["LEAN_BUS_RABBITMQ_URI"] = "amqp://rabbitmq/lean" }),
            Options.Create(_engine), Options.Create(_options), TimeProvider.System, NullLogger<LiveJobRunner>.Instance);

        private static JobSubmitted Job(string brokerage = "PaperBrokerage", List<string> queues = null) => new()
        {
            JobId = Guid.NewGuid(), Kind = JobKind.Live, Attempt = 1, SubmittedAt = DateTimeOffset.UtcNow, SubmittedBy = "tests",
            Live = new LiveSpec { Algorithm = new BuiltinAlgorithmRef { TypeName = "LiveWorkerDemoAlgorithm" }, Brokerage = brokerage, DataQueueHandlers = queues },
        };

        private static LiveState State(Guid jobId) => new()
        {
            JobId = jobId, Attempt = 1, UpdatedAt = DateTimeOffset.UtcNow,
            Holdings = new List<JsonObject> { JsonNode.Parse(@"{""Symbol"":{""value"":""BTCUSD"",""id"":""BTCUSD 2XR"",""permtick"":""BTCUSD""},""a"":50000,""q"":1,""p"":50100}")!.AsObject() },
            Cash = new List<LiveCashAmount> { new() { Currency = "USD", Amount = 50000m } },
        };

        private IEnumerable<string> Types => _bus.Published.Select(m => m.Envelope.Type);

        private Task PublishControl<T>(string type, Guid jobId, string routingKey, T body) => _bus.PublishAsync(OutgoingMessage.Create(BusExchanges.Control, routingKey,
            MessageEnvelope.Create(type, jobId, 1, AppIds.Orchestrator, JobKind.Live), body)).ContinueWith(_ => _bus.DeliverAllAsync()).Unwrap();

        private static async Task WaitFor(Func<bool> condition, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
                await Task.Delay(10);
            }
        }

        [Test]
        public void ConfigRunsLiveModeWithTheBusHandlersAndTheRestoredPortfolio()
        {
            var job = Job();
            var workspace = LiveWorkspace.Create(_engine, job, "lean-live-1", 7, State(job.JobId));
            var config = JsonNode.Parse(File.ReadAllText(workspace.ConfigPath))!.AsObject();

            Assert.AreEqual(LiveWorkspace.Environment, (string)config["environment"]);
            Assert.IsNull(config["environments"]![LiveWorkspace.Environment], "the environment must not name a section that overrides the handlers");
            Assert.IsTrue((bool)config["live-mode"]);
            Assert.AreEqual("PaperBrokerage", (string)config["live-mode-brokerage"]);
            Assert.AreEqual("QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler", (string)config["setup-handler"]);
            Assert.AreEqual("QuantConnect.Lean.Engine.Results.LiveTradingResultHandler", (string)config["result-handler"]);
            Assert.AreEqual("QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed", (string)config["data-feed-handler"]);
            Assert.AreEqual("QuantConnect.Lean.Engine.TransactionHandlers.BacktestingTransactionHandler", (string)config["transaction-handler"]);
            Assert.AreEqual("QuantConnect.Services.EngineHandlers.BusDataQueueHandler", (string)config["data-queue-handler"]![0]);
            Assert.AreEqual(JobWorkspace.JobQueueHandler, (string)config["job-queue-handler"]);
            Assert.AreEqual(JobWorkspace.MessagingHandler, (string)config["messaging-handler"]);
            Assert.AreEqual("7", (string)config["bus-lease-epoch"]);
            Assert.AreEqual(1m, (decimal)JsonNode.Parse((string)config["live-holdings"])![0]!["q"]);
            var cash = JsonNode.Parse((string)config["live-cash-balance"])![0]!;
            Assert.AreEqual("USD", (string)cash["Currency"]);
            Assert.AreEqual(50000m, (decimal)cash["Amount"]);
        }

        [Test]
        public void FreshDeploymentsKeepTheAlgorithmCashAndRealBrokeragesUseTheBrokerageTransactionHandler()
        {
            var job = Job("InteractiveBrokersBrokerage", new List<string> { "InteractiveBrokersBrokerage" });
            var config = JsonNode.Parse(File.ReadAllText(LiveWorkspace.Create(_engine, job, "w", 1, null).ConfigPath))!.AsObject();
            Assert.AreEqual("[]", (string)config["live-holdings"]);
            Assert.AreEqual("", (string)config["live-cash-balance"]);
            Assert.AreEqual("QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler", (string)config["transaction-handler"]);
            Assert.AreEqual("InteractiveBrokersBrokerage", (string)config["data-queue-handler"]![0]);
        }

        [Test]
        public async Task NoLeaseNoEngine()
        {
            _services.Grant = new LeaseGrant(LeaseOutcome.Refused, Detail: "409 lease epoch 3 is held by lean-live-other");
            Assert.AreEqual(LiveJobRunner.ExitFinished, await Runner().RunAsync(Job(), CancellationToken.None));
            Assert.IsNull(_launcher.Info);
            CollectionAssert.IsEmpty(Types);
        }

        [Test]
        public async Task StopWithLiquidateLiquidatesThenQuitsAndReportsCancelled()
        {
            var job = Job();
            _services.State = State(job.JobId);
            _launcher.OnStart = _ => null;
            var runner = Runner();
            var run = runner.RunAsync(job, CancellationToken.None);
            await WaitFor(() => Types.Contains(MessageTypes.JobHeartbeat) && _launcher.Process != null);

            Assert.IsTrue(runner.Status.Restored);
            Assert.AreEqual(1, runner.Status.RestoredHoldings);
            var started = _bus.Published.First(m => m.Envelope.Type == MessageTypes.JobStarted);
            Assert.AreEqual(5, started.Envelope.Headers.LeaseEpoch);
            Assert.AreEqual(5, ContractJson.Deserialize<JobStarted>(started.Body.Span).LeaseEpoch);

            await PublishControl(MessageTypes.CancelJob, job.JobId, RoutingKeys.Cancel(job.JobId), new CancelJob
            {
                JobId = job.JobId, Attempt = 1, RequestedAt = DateTimeOffset.UtcNow, Reason = "live deployment stopped", LeaseEpoch = 5, Liquidate = true,
            });
            await WaitFor(() => _launcher.Commands.Any(c => c == LiveJobRunner.QuitCommandType));
            _launcher.Process.Exit(1);

            Assert.AreEqual(LiveJobRunner.ExitFinished, await run);
            CollectionAssert.AreEqual(new[] { LiveJobRunner.LiquidateCommandType, LiveJobRunner.QuitCommandType }, _launcher.Commands);
            var cancelled = ContractJson.Deserialize<JobCancelled>(_bus.Published.Single(m => m.Envelope.Type == MessageTypes.JobCancelled).Body.Span);
            Assert.AreEqual("live deployment stopped", cancelled.Reason);
            Assert.AreEqual(5, cancelled.LeaseEpoch);
            Assert.IsFalse(Directory.Exists(Path.Combine(_engine.WorkRoot, job.JobId.ToString("D"), "live")));
        }

        [Test]
        public async Task LiquidateKeepsTheAlgorithmRunningAndIgnoresOtherLeaseEpochs()
        {
            var job = Job();
            _launcher.OnStart = _ => null;
            using var stop = new CancellationTokenSource();
            var run = Runner().RunAsync(job, stop.Token);
            await WaitFor(() => _launcher.Process != null && Types.Contains(MessageTypes.JobStarted));

            await PublishControl(MessageTypes.LiquidateJob, job.JobId, RoutingKeys.Liquidate(job.JobId),
                new LiquidateJob { JobId = job.JobId, RequestedAt = DateTimeOffset.UtcNow, LeaseEpoch = 4 });
            await PublishControl(MessageTypes.LiquidateJob, job.JobId, RoutingKeys.Liquidate(job.JobId),
                new LiquidateJob { JobId = job.JobId, RequestedAt = DateTimeOffset.UtcNow, LeaseEpoch = 5, RequestedBy = "alice" });
            await WaitFor(() => _launcher.Commands.Count == 1);
            await Task.Delay(100);
            CollectionAssert.AreEqual(new[] { LiveJobRunner.LiquidateCommandType }, _launcher.Commands);
            Assert.IsFalse(_launcher.Process.Exited.IsCompleted);

            // Container shutdown: quit cleanly, no terminal event (the deployment resumes in the next container).
            stop.Cancel();
            await WaitFor(() => _launcher.Commands.Contains(LiveJobRunner.QuitCommandType));
            _launcher.Process.Exit(1);
            Assert.CatchAsync<OperationCanceledException>(async () => await run);
            CollectionAssert.IsEmpty(Types.Where(t => t is MessageTypes.JobCancelled or MessageTypes.JobFailed or MessageTypes.JobCompleted));
        }

        [Test]
        public async Task EngineCrashRestartsTheContainerWithoutATerminalEvent()
        {
            _launcher.OnStart = _ => 134;
            Assert.AreEqual(LiveJobRunner.ExitRestart, await Runner().RunAsync(Job(), CancellationToken.None));
            CollectionAssert.IsEmpty(Types.Where(t => t is MessageTypes.JobCancelled or MessageTypes.JobFailed or MessageTypes.JobCompleted));
        }

        [Test]
        public async Task EngineKilledWhileRunningRestartsEvenWithAPeriodicResultFile()
        {
            var job = Job();
            _launcher.OnStart = info =>
            {
                File.WriteAllText(Path.Combine(info.WorkingDirectory, $"{job.JobId:D}.json"), @"{""state"":{""Status"":""Running""}}");
                return 137;
            };
            Assert.AreEqual(LiveJobRunner.ExitRestart, await Runner().RunAsync(job, CancellationToken.None));
            CollectionAssert.IsEmpty(Types.Where(t => t is MessageTypes.JobCancelled or MessageTypes.JobFailed or MessageTypes.JobCompleted));
        }

        [Test]
        public async Task AlgorithmThatQuitsItselfCompletesTheDeployment()
        {
            var job = Job();
            _launcher.OnStart = info =>
            {
                File.WriteAllText(Path.Combine(info.WorkingDirectory, $"{job.JobId:D}.json"), @"{""state"":{""Status"":""Stopped""}}");
                return 1;
            };
            Assert.AreEqual(LiveJobRunner.ExitFinished, await Runner().RunAsync(job, CancellationToken.None));
            var completed = ContractJson.Deserialize<JobCompleted>(_bus.Published.Single(m => m.Envelope.Type == MessageTypes.JobCompleted).Body.Span);
            StringAssert.EndsWith($"{job.JobId:D}.json", completed.Result.Key);
        }

        [Test]
        public async Task AlgorithmRuntimeErrorIsAFinalFailure()
        {
            var job = Job();
            _launcher.OnStart = info =>
            {
                File.WriteAllText(Path.Combine(info.WorkingDirectory, $"{job.JobId:D}.json"), @"{""state"":{""Status"":""RuntimeError"",""RuntimeError"":""boom""}}");
                return 1;
            };
            Assert.AreEqual(LiveJobRunner.ExitFinished, await Runner().RunAsync(job, CancellationToken.None));
            var failed = ContractJson.Deserialize<JobFailed>(_bus.Published.Single(m => m.Envelope.Type == MessageTypes.JobFailed).Body.Span);
            Assert.IsTrue(failed.Final);
            Assert.AreEqual("boom", failed.Error.Message);
            Assert.IsTrue(_store.Objects.Keys.Any(k => k.Item2.EndsWith($"{job.JobId:D}.json")), "the final live result is kept in MinIO");
        }

        [Test]
        public void DispatchMustBeALiveJob()
        {
            var job = Job();
            Assert.AreEqual(job.JobId, LiveJobRunner.ParseDispatch(ContractJson.Serialize(job)).JobId);
            Assert.Throws<InvalidOperationException>(() => LiveJobRunner.ParseDispatch(null));
            Assert.Throws<InvalidOperationException>(() => LiveJobRunner.ParseDispatch(ContractJson.Serialize(job with { Kind = JobKind.Backtest, Live = null })));
        }

        private sealed class FakeServices : ILiveServices
        {
            public LeaseGrant Grant { get; set; }
            public LiveState State { get; set; }

            public Task<LeaseGrant> AcquireLeaseAsync(Guid jobId, string workerId, CancellationToken cancellationToken) =>
                Task.FromResult(Grant ?? new LeaseGrant(LeaseOutcome.Granted, new Lease
                {
                    JobId = jobId, Attempt = 1, LeaseEpoch = 5, HeartbeatIntervalSeconds = 10, Results = ResultsPrefix.For(jobId, 1),
                }));

            public Task<LiveState> LiveStateAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(State);
        }

        private sealed class FakeStore : IResultStore
        {
            public System.Collections.Concurrent.ConcurrentDictionary<(string, string), byte[]> Objects { get; } = new();

            public Task<byte[]> TryGetAsync(string bucket, string key, CancellationToken cancellationToken) =>
                Task.FromResult(Objects.TryGetValue((bucket, key), out var content) ? content : null);

            public Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken)
            {
                Objects[(bucket, key)] = content;
                return Task.CompletedTask;
            }
        }

        /// <summary>Engine stand-in; like FileCommandHandler it consumes the command files of its working directory.</summary>
        private sealed class FakeLauncher : IEngineLauncher
        {
            public Func<EngineStartInfo, int?> OnStart { get; set; } = _ => 0;
            public EngineStartInfo Info { get; private set; }
            public FakeProcess Process { get; private set; }
            public List<string> Commands { get; } = new();

            public IEngineProcess Start(EngineStartInfo info)
            {
                Info = info;
                var process = new FakeProcess(OnStart(info));
                _ = Task.Run(async () =>
                {
                    while (!process.Exited.IsCompleted)
                    {
                        if (Directory.Exists(info.WorkingDirectory))
                        {
                            foreach (var file in Directory.GetFiles(info.WorkingDirectory, "command*.json").OrderBy(f => f))
                            {
                                var type = (string)JsonNode.Parse(File.ReadAllText(file))!["$type"];
                                File.Delete(file);
                                lock (Commands) Commands.Add(type);
                            }
                        }
                        await Task.Delay(5);
                    }
                });
                return Process = process;
            }
        }

        private sealed class FakeProcess : IEngineProcess
        {
            private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public FakeProcess(int? exitCode)
            {
                if (exitCode is { } code) _exited.SetResult(code);
            }

            public int Id => 4243;
            public Task<int> Exited => _exited.Task;
            public long RssBytes => 1024;
            public void Exit(int code) => _exited.TrySetResult(code);
            public void Kill() => _exited.TrySetResult(137);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
