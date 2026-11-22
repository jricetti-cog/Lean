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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using QuantConnect.Services.BacktestWorker;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Tests.Services.BacktestWorker
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    [TestFixture]
    public class BacktestWorkerTests
    {
        private const string LauncherConfig = @"{
  // comments and trailing commas are allowed in the Launcher config
  ""environment"": ""backtesting"",
  ""algorithm-type-name"": ""BasicTemplateFrameworkAlgorithm"",
  ""job-queue-handler"": ""QuantConnect.Queues.JobQueue"",
  ""messaging-handler"": ""QuantConnect.Messaging.Messaging"",
  ""data-folder"": ""../../../Data/"",
  ""environments"": { ""backtesting"": { ""live-mode"": false } },
}";

        private string _root;
        private WorkerOptions _options;
        private FakePublisher _publisher;
        private FakeAttempts _attempts;
        private FakeStore _store;
        private FakeLauncher _launcher;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "backtest-worker-tests", Guid.NewGuid().ToString("N"));
            var launcher = Directory.CreateDirectory(Path.Combine(_root, "launcher")).FullName;
            File.WriteAllText(Path.Combine(launcher, "config.json"), LauncherConfig);
            _options = new WorkerOptions
            {
                WorkerId = "worker-1",
                LauncherDirectory = launcher,
                WorkRoot = Path.Combine(_root, "work"),
                DataFolder = "/Lean/Data/",
                DefaultRamMb = 2048,
                MemoryHeadroomMb = 512,
                PollIntervalMilliseconds = 10,
                HeartbeatIntervalSeconds = 1,
            };
            _publisher = new FakePublisher();
            _attempts = new FakeAttempts();
            _store = new FakeStore();
            _launcher = new FakeLauncher();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private BacktestJobRunner Runner() => new(_publisher, _attempts, _store, _launcher,
            new EngineEnvironment(new Dictionary<string, string> { ["LEAN_BUS_RABBITMQ_URI"] = "amqp://rabbitmq/lean" }),
            Options.Create(_options), TimeProvider.System, NullLogger<BacktestJobRunner>.Instance);

        private static JobSubmitted Job(int? maxRuntimeSeconds = null, int? maxRamMb = null) => new()
        {
            JobId = Guid.NewGuid(), Kind = JobKind.Backtest, Attempt = 1, SubmittedAt = DateTimeOffset.UtcNow, SubmittedBy = "tests",
            Backtest = new BacktestSpec
            {
                Algorithm = new BuiltinAlgorithmRef { TypeName = "BasicTemplateAlgorithm" },
                MaxRuntimeSeconds = maxRuntimeSeconds,
                MaxRamMb = maxRamMb,
            },
        };

        private static ReceivedMessage Delivery(JobSubmitted job, long deliveryCount = 0)
        {
            var envelope = MessageEnvelope.Create(MessageTypes.BacktestRequested, job.JobId, job.Attempt, AppIds.Orchestrator, job.Kind);
            return new ReceivedMessage
            {
                Queue = Queues.Dispatch(JobKind.Backtest), Exchange = Exchanges.Jobs, RoutingKey = RoutingKeys.Dispatch(JobKind.Backtest),
                Envelope = envelope with { Headers = envelope.Headers with { DeliveryCount = deliveryCount } },
                Body = ContractJson.SerializeToUtf8Bytes(job),
            };
        }

        private static string ResultJson(string status, string runtimeError = null) =>
            $"{{\"statistics\":{{\"Total Orders\":\"1\",\"Net Profit\":\"1.2%\"}},\"state\":{{\"Status\":\"{status}\"" +
            (runtimeError == null ? "" : $",\"RuntimeError\":\"{runtimeError}\",\"StackTrace\":\"at Algo.OnData\"") + "}}";

        /// <summary>Engine stand-in: writes what the Launcher writes into the attempt folder and, like BusMessagingHandler, uploads the final result.</summary>
        private void EngineStoresResult(string status, int exitCode, bool upload = true, string runtimeError = null)
        {
            _launcher.OnStart = info =>
            {
                File.WriteAllText(info.LogPath, "TRACE:: Engine.Run(): running\n" + (runtimeError == null ? "" : $"ERROR:: Runtime Error: {runtimeError}\n"));
                var job = ContractJson.Deserialize<JobSubmitted>(File.ReadAllText(Path.Combine(info.WorkingDirectory, "job.json")));
                var result = Encoding.UTF8.GetBytes(ResultJson(status, runtimeError));
                File.WriteAllBytes(Path.Combine(info.WorkingDirectory, $"{job.JobId:D}.json"), result);
                if (upload)
                {
                    _store.Objects[(job.Results!.Bucket, $"{job.Results.Prefix}{job.JobId:D}.json")] = result;
                    _store.Objects[(job.Results.Bucket, $"{job.Results.Prefix}{job.JobId:D}-order-events.json")] = Encoding.UTF8.GetBytes("[]");
                }
                return exitCode;
            };
        }

        [Test]
        public void BuildConfigSelectsTheBusHandlersAndKeepsTheLauncherSettings()
        {
            var config = JsonNode.Parse(JobWorkspace.BuildConfig(LauncherConfig, new Dictionary<string, JsonNode>
            {
                ["job-queue-handler"] = JobWorkspace.JobQueueHandler,
                ["close-automatically"] = true,
            }))!.AsObject();

            Assert.AreEqual(JobWorkspace.JobQueueHandler, (string)config["job-queue-handler"]);
            Assert.AreEqual("QuantConnect.Messaging.Messaging", (string)config["messaging-handler"]);
            Assert.AreEqual(true, (bool)config["close-automatically"]);
            Assert.AreEqual(false, (bool)config["environments"]!["backtesting"]!["live-mode"]);
        }

        [Test]
        public async Task CompletedJobIsReportedAfterTheEngineUploadAndThenAcked()
        {
            EngineStoresResult("Completed", 0);
            var job = Job(maxRamMb: 1024);
            var granted = ResultsPrefix.For(job.JobId, 2);
            _attempts.Grant = new AttemptGrant(AttemptOutcome.Granted, new JobAttempt { JobId = job.JobId, Attempt = 2, Results = granted });

            var disposition = await Runner().HandleAsync(Delivery(job, deliveryCount: 1), CancellationToken.None);

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            Assert.AreEqual((job.JobId, "worker-1", 1L, (int?)1), _attempts.Calls.Single());
            CollectionAssert.AreEqual(new[] { MessageTypes.JobStarted, MessageTypes.JobCompleted }, _publisher.Types);
            Assert.IsTrue(_publisher.Messages.All(m => m.Envelope.Headers.Attempt == 2 && m.Envelope.AppId == AppIds.BacktestWorker && m.Exchange == Exchanges.Events));

            var completed = ContractJson.Deserialize<JobCompleted>(_publisher.Messages[1].Body.Span);
            Assert.AreEqual($"{granted.Prefix}{job.JobId:D}.json", completed.Result.Key);
            Assert.AreEqual(ResultStoreExtensions.RefOf(granted.Bucket, completed.Result.Key, _store.Objects[(granted.Bucket, completed.Result.Key)], "application/json"), completed.Result);
            Assert.AreEqual("1", completed.Statistics!["Total Orders"]);
            CollectionAssert.AreEquivalent(new[] { $"{granted.Prefix}engine.log", $"{granted.Prefix}{job.JobId:D}-order-events.json" }, completed.Artifacts.Select(a => a.Key));
            Assert.AreEqual(0, completed.ExitCode);

            // The child gets the stock Launcher, a per-attempt config with the bus handlers, the bus settings and the RAM cap.
            var info = _launcher.Info!;
            Assert.AreEqual(new[] { Path.Combine(_options.LauncherDirectory, "QuantConnect.Lean.Launcher.dll"), "--config", Path.Combine(info.WorkingDirectory, "config.json") }, info.Arguments);
            Assert.AreEqual(Path.Combine(_options.WorkRoot, job.JobId.ToString("D"), "2"), info.WorkingDirectory);
            Assert.AreEqual("amqp://rabbitmq/lean", info.Environment["LEAN_BUS_RABBITMQ_URI"]);
            Assert.AreEqual((1536L * 1024 * 1024).ToString("X"), info.Environment["DOTNET_GCHeapHardLimit"]);
            Assert.AreEqual(1024, _launcher.Job!.Backtest!.MaxRamMb);
            Assert.AreEqual(2, _launcher.Job.Attempt);
            Assert.AreEqual(JobWorkspace.JobQueueHandler, (string)_launcher.Config!["job-queue-handler"]);
            Assert.AreEqual(JobWorkspace.MessagingHandler, (string)_launcher.Config["messaging-handler"]);
            Assert.AreEqual("/Lean/Data/", (string)_launcher.Config["data-folder"]);
            Assert.AreEqual(info.WorkingDirectory, (string)_launcher.Config["results-destination-folder"]);
            Assert.IsFalse(Directory.Exists(info.WorkingDirectory), "the attempt folder is removed");
        }

        [Test]
        public async Task NonZeroExitAfterACompletedAndUploadedResultStillCompletes()
        {
            // pythonnet can abort during finalization (exit 134) after the result is stored and uploaded (ADR 0005).
            EngineStoresResult("Completed", 134);
            var disposition = await Runner().HandleAsync(Delivery(Job()), CancellationToken.None);

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            Assert.AreEqual(134, ContractJson.Deserialize<JobCompleted>(_publisher.Messages.Last().Body.Span).ExitCode);
        }

        [Test]
        public async Task CompletedButNotUploadedIsARetryableUploadFailure()
        {
            EngineStoresResult("Completed", 0, upload: false);
            await Runner().HandleAsync(Delivery(Job()), CancellationToken.None);

            var failed = ContractJson.Deserialize<JobFailed>(_publisher.Messages.Last().Body.Span);
            Assert.AreEqual(ErrorCode.UploadFailed, failed.Error.Code);
            Assert.IsFalse(failed.Final);
        }

        [Test]
        public async Task AlgorithmRuntimeErrorIsAFinalFailure()
        {
            EngineStoresResult("RuntimeError", 1, runtimeError: "division by zero");
            var disposition = await Runner().HandleAsync(Delivery(Job()), CancellationToken.None);

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            var failed = ContractJson.Deserialize<JobFailed>(_publisher.Messages.Last().Body.Span);
            Assert.AreEqual(ErrorCode.RuntimeError, failed.Error.Code);
            Assert.AreEqual("division by zero", failed.Error.Message);
            Assert.AreEqual(1, failed.Error.ExitCode);
            Assert.IsTrue(failed.Final);
            Assert.That(failed.Artifacts!.Single().Key, Does.EndWith("engine.log"));
        }

        [Test]
        public async Task EngineCrashIsRetryableAndDoesNotReachTheWorker()
        {
            _launcher.OnStart = info => 139;
            var runner = Runner();
            var disposition = await runner.HandleAsync(Delivery(Job()), CancellationToken.None);

            Assert.AreEqual(MessageDisposition.Ack, disposition);
            var failed = ContractJson.Deserialize<JobFailed>(_publisher.Messages.Last().Body.Span);
            Assert.AreEqual(ErrorCode.EngineCrash, failed.Error.Code);
            Assert.AreEqual(139, failed.Error.ExitCode);
            Assert.IsFalse(failed.Final);

            // The same worker takes the next job.
            EngineStoresResult("Completed", 0);
            Assert.AreEqual(MessageDisposition.Ack, await runner.HandleAsync(Delivery(Job()), CancellationToken.None));
            Assert.AreEqual(MessageTypes.JobCompleted, _publisher.Types.Last());
            Assert.AreEqual(1, runner.Status.JobsCompleted);
            Assert.AreEqual(1, runner.Status.JobsFailed);
        }

        [Test]
        public async Task RunTimeLimitKillsTheEngineAndHeartbeatsWhileItRuns()
        {
            _launcher.OnStart = info => null;
            _launcher.Rss = 100 * 1024 * 1024;
            var started = DateTime.UtcNow;
            await Runner().HandleAsync(Delivery(Job(maxRuntimeSeconds: 2)), CancellationToken.None);

            Assert.IsTrue(_launcher.Process!.Killed);
            Assert.Less(DateTime.UtcNow - started, TimeSpan.FromSeconds(30));
            var failed = ContractJson.Deserialize<JobFailed>(_publisher.Messages.Last().Body.Span);
            Assert.AreEqual(ErrorCode.Timeout, failed.Error.Code);
            Assert.IsTrue(failed.Final);
            var heartbeat = ContractJson.Deserialize<JobHeartbeat>(_publisher.Messages.First(m => m.Envelope.Type == MessageTypes.JobHeartbeat).Body.Span);
            Assert.AreEqual("worker-1", heartbeat.WorkerId);
            Assert.AreEqual(100 * 1024 * 1024, heartbeat.RssBytes);
            Assert.AreEqual(true, heartbeat.EngineAlive);
        }

        [Test]
        public async Task RamLimitKillsTheEngine()
        {
            _launcher.OnStart = info => null;
            _launcher.Rss = (256L + 512 + 1) * 1024 * 1024;
            await Runner().HandleAsync(Delivery(Job(maxRamMb: 256)), CancellationToken.None);

            Assert.IsTrue(_launcher.Process!.Killed);
            var failed = ContractJson.Deserialize<JobFailed>(_publisher.Messages.Last().Body.Span);
            Assert.AreEqual(ErrorCode.RuntimeError, failed.Error.Code);
            Assert.That(failed.Error.Message, Does.Contain("256 MB RAM limit"));
            Assert.IsTrue(failed.Final);
        }

        [Test]
        public async Task WorkerShutdownKillsTheEngineAndRequeuesTheDelivery()
        {
            _launcher.OnStart = info => null;
            using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.ThrowsAsync<TaskCanceledException>(() => Runner().HandleAsync(Delivery(Job()), stopping.Token));
            Assert.IsTrue(_launcher.Process!.Killed);
            CollectionAssert.AreEqual(new[] { MessageTypes.JobStarted }, _publisher.Types);
            await Task.CompletedTask;
        }

        [TestCase(AttemptOutcome.Gone, MessageDisposition.Ack)]
        [TestCase(AttemptOutcome.Rejected, MessageDisposition.DeadLetter)]
        public async Task DeliveriesWithoutAnAttemptAreNotRun(AttemptOutcome outcome, MessageDisposition expected)
        {
            _attempts.Grant = new AttemptGrant(outcome, Detail: "job is completed");
            Assert.AreEqual(expected, await Runner().HandleAsync(Delivery(Job()), CancellationToken.None));
            Assert.IsNull(_launcher.Info);
            CollectionAssert.IsEmpty(_publisher.Messages);
        }

        [Test]
        public async Task NonBacktestDeliveriesAreDeadLettered()
        {
            var job = Job() with { Kind = JobKind.Live, Backtest = null };
            Assert.AreEqual(MessageDisposition.DeadLetter, await Runner().HandleAsync(Delivery(job), CancellationToken.None));
            CollectionAssert.IsEmpty(_attempts.Calls);
        }

        [Test]
        public void WorkersTakeOneJobAtATime()
        {
            Assert.AreEqual(1, BacktestWorkerService.Consumer.Prefetch);
        }

        private sealed class FakePublisher : IMessagePublisher
        {
            public List<OutgoingMessage> Messages { get; } = new();
            public IEnumerable<string> Types => Messages.Select(m => m.Envelope.Type);

            public Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
            {
                lock (Messages) Messages.Add(message);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeAttempts : IAttemptClient
        {
            public AttemptGrant Grant { get; set; }
            public List<(Guid, string, long, int?)> Calls { get; } = new();

            public Task<AttemptGrant> StartAttemptAsync(Guid jobId, string workerId, long deliveryCount, int? attempt, CancellationToken cancellationToken)
            {
                Calls.Add((jobId, workerId, deliveryCount, attempt));
                return Task.FromResult(Grant ?? new AttemptGrant(AttemptOutcome.Granted,
                    new JobAttempt { JobId = jobId, Attempt = attempt ?? 1, Results = ResultsPrefix.For(jobId, attempt ?? 1) }));
            }
        }

        private sealed class FakeStore : IResultStore
        {
            public ConcurrentDictionary<(string, string), byte[]> Objects { get; } = new();

            public Task<byte[]> TryGetAsync(string bucket, string key, CancellationToken cancellationToken) =>
                Task.FromResult(Objects.TryGetValue((bucket, key), out var content) ? content : null);

            public Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken)
            {
                Objects[(bucket, key)] = content;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeLauncher : IEngineLauncher
        {
            /// <summary>Returns the exit code, or null to keep running until killed.</summary>
            public Func<EngineStartInfo, int?> OnStart { get; set; } = _ => 0;
            public long Rss { get; set; }
            public EngineStartInfo Info { get; private set; }
            public JobSubmitted Job { get; private set; }
            public JsonObject Config { get; private set; }
            public FakeProcess Process { get; private set; }

            public IEngineProcess Start(EngineStartInfo info)
            {
                Info = info;
                Job = ContractJson.Deserialize<JobSubmitted>(File.ReadAllText(Path.Combine(info.WorkingDirectory, "job.json")));
                Config = JsonNode.Parse(File.ReadAllText(Path.Combine(info.WorkingDirectory, "config.json")))!.AsObject();
                return Process = new FakeProcess(OnStart(info), Rss);
            }
        }

        private sealed class FakeProcess : IEngineProcess
        {
            private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly long _rss;

            public FakeProcess(int? exitCode, long rss)
            {
                _rss = rss;
                if (exitCode is { } code) _exited.SetResult(code);
            }

            public int Id => 4242;
            public Task<int> Exited => _exited.Task;
            public long RssBytes => _exited.Task.IsCompleted ? 0 : _rss;
            public bool Killed { get; private set; }

            public void Kill()
            {
                Killed = true;
                _exited.TrySetResult(137);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
