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
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
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
using QuantConnect.Services.Messaging.InMemory;

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

        private BacktestJobRunner Runner(ITransientSubscriber control = null) => new(_publisher, _attempts, _store, _launcher,
            new EngineEnvironment(new Dictionary<string, string> { ["LEAN_BUS_RABBITMQ_URI"] = "amqp://rabbitmq/lean" }),
            Options.Create(_options), TimeProvider.System, NullLogger<BacktestJobRunner>.Instance, control);

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
        [TestCase(AttemptOutcome.Exhausted, MessageDisposition.DeadLetter)]
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

        private static OutgoingMessage Cancel(Guid jobId, int? attempt, string reason = "cancelled by tests") => OutgoingMessage.Create(Exchanges.Control, RoutingKeys.Cancel(jobId),
            MessageEnvelope.Create(MessageTypes.CancelJob, jobId, attempt ?? 1, AppIds.Orchestrator, JobKind.Backtest),
            new CancelJob { JobId = jobId, Attempt = attempt, RequestedAt = DateTimeOffset.UtcNow, RequestedBy = "tests", Reason = reason }) with { Mandatory = false };

        private static async Task WaitFor(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > until) Assert.Fail("Timed out");
                await Task.Delay(10);
            }
        }

        /// <summary>Collects the worker's <c>backtest run</c> spans of one job.</summary>
        private static ActivityListener RunSpans(Guid jobId, List<Activity> spans)
        {
            var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == WorkerTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if ((string)activity.GetTagItem("lean.job.id") == jobId.ToString("D")) lock (spans) spans.Add(activity);
                },
            };
            ActivitySource.AddActivityListener(listener);
            return listener;
        }

        [Test]
        public async Task CancelCommandKillsTheRunningEngineReportsCancelledAndTakesTheNextJob()
        {
            var bus = new InMemoryMessageBus();
            var runner = Runner(bus);
            _launcher.OnStart = _ => null;
            var job = Job();
            var spans = new List<Activity>();
            using var listener = RunSpans(job.JobId, spans);

            var handling = runner.HandleAsync(Delivery(job), CancellationToken.None);
            await WaitFor(() => _launcher.Process != null);
            await bus.PublishAsync(Cancel(job.JobId, attempt: 0));
            await bus.DeliverAllAsync();
            await Task.Delay(100);
            Assert.IsFalse(_launcher.Process!.Killed, "a cancel for an older attempt must not stop the running attempt");

            await bus.PublishAsync(Cancel(job.JobId, attempt: 1, reason: "Cancelled by user"));
            await bus.DeliverAllAsync();
            Assert.AreEqual(MessageDisposition.Ack, await handling.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.IsTrue(_launcher.Process.Killed);
            var types = _publisher.Types.Where(t => t != MessageTypes.JobHeartbeat).ToList();
            CollectionAssert.AreEqual(new[] { MessageTypes.JobStarted, MessageTypes.JobCancelled }, types);
            var cancelled = ContractJson.Deserialize<JobCancelled>(_publisher.Messages.Last().Body.Span);
            Assert.AreEqual(job.JobId, cancelled.JobId);
            Assert.AreEqual("Cancelled by user", cancelled.Reason);
            Assert.AreEqual(1, runner.Status.JobsCancelled);
            Assert.AreEqual(0, runner.Status.JobsFailed);
            Assert.IsNull(runner.Status.CurrentJobId);
            var span = spans.Single();
            Assert.AreEqual("cancelled", span.GetTagItem("lean.job.outcome"));
            Assert.AreNotEqual(ActivityStatusCode.Error, span.Status);

            // The per-job control queue is gone and the worker runs the next delivery.
            await bus.PublishAsync(Cancel(job.JobId, attempt: 1));
            Assert.AreEqual(0, await bus.DeliverAllAsync());
            EngineStoresResult("Completed", 0);
            _publisher.Messages.Clear();
            Assert.AreEqual(MessageDisposition.Ack, await runner.HandleAsync(Delivery(Job()), CancellationToken.None));
            Assert.AreEqual(MessageTypes.JobCompleted, _publisher.Types.Last());
            Assert.AreEqual(1, runner.Status.JobsCompleted);
        }

        [Test]
        public async Task CancelReceivedBeforeTheEngineStartsNeverStartsIt()
        {
            var bus = new InMemoryMessageBus();
            var job = Job();
            _attempts.OnStart = () =>
            {
                bus.PublishAsync(Cancel(job.JobId, attempt: null)).GetAwaiter().GetResult();
                bus.DeliverAllAsync().GetAwaiter().GetResult();
            };

            Assert.AreEqual(MessageDisposition.Ack, await Runner(bus).HandleAsync(Delivery(job), CancellationToken.None));

            Assert.IsNull(_launcher.Info);
            CollectionAssert.AreEqual(new[] { MessageTypes.JobStarted, MessageTypes.JobCancelled }, _publisher.Types);
        }

        private const string EditedAlgorithm = @"using QuantConnect.Data;
namespace QuantConnect.Algorithm.CSharp
{
    public class EditedAlgorithm : QCAlgorithm
    {
        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            AddEquity(""SPY"");
        }

        public override void OnData(Slice slice)
        {
            if (!Portfolio.Invested) SetHoldings(""SPY"", 0.5);
        }
    }
}";

        /// <summary>Points the worker at the test output folder, which holds the LEAN assemblies uploaded C# compiles against.</summary>
        private void UseLeanAssemblies()
        {
            foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            {
                File.CreateSymbolicLink(Path.Combine(_options.LauncherDirectory, Path.GetFileName(dll)), dll);
            }
        }

        private UploadedAlgorithmRef Upload(string key, byte[] content, string typeName = null)
        {
            _store.Objects[(Buckets.Algorithms, key)] = content;
            return new UploadedAlgorithmRef
            {
                Language = AlgorithmLanguage.CSharp, TypeName = typeName,
                Object = new ObjectRef { Bucket = Buckets.Algorithms, Key = key, Size = content.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)) },
            };
        }

        [Test]
        public async Task UploadedCSharpSourceIsCompiledAndHandedToTheEngine()
        {
            UseLeanAssemblies();
            EngineStoresResult("Completed", 0);
            var job = Job() with { Backtest = Job().Backtest! with { Algorithm = Upload("tests/Main.cs", Encoding.UTF8.GetBytes(EditedAlgorithm)) } };

            Assert.AreEqual(MessageDisposition.Ack, await Runner().HandleAsync(Delivery(job), CancellationToken.None));

            Assert.AreEqual(MessageTypes.JobCompleted, _publisher.Types.Last());
            var location = (string)_launcher.Config[JobWorkspace.CompiledAlgorithmLocation];
            Assert.AreEqual("QuantConnect.Algorithm.CSharp.EditedAlgorithm", (string)_launcher.Config[JobWorkspace.CompiledAlgorithmTypeName]);
            StringAssert.StartsWith(Path.Combine(_launcher.Info!.WorkingDirectory, "algorithm"), location);
            StringAssert.EndsWith(".dll", location);
        }

        [Test]
        public async Task UploadedCSharpWithASyntaxErrorFailsWithCompileErrorAndDiagnosticsInTheLogs()
        {
            UseLeanAssemblies();
            var broken = EditedAlgorithm.Replace("AddEquity(\"SPY\");", "AddEquity(\"SPY\")", StringComparison.Ordinal);
            var job = Job() with { Backtest = Job().Backtest! with { Algorithm = Upload("tests/Main.cs", Encoding.UTF8.GetBytes(broken)) } };
            var spans = new List<Activity>();
            using var listener = RunSpans(job.JobId, spans);

            Assert.AreEqual(MessageDisposition.Ack, await Runner().HandleAsync(Delivery(job), CancellationToken.None));

            var span = spans.Single();
            Assert.AreEqual(ActivityStatusCode.Error, span.Status);
            Assert.AreEqual("compile-error", span.StatusDescription);
            Assert.AreEqual("failed", span.GetTagItem("lean.job.outcome"));
            Assert.AreEqual("compile-error", span.GetTagItem("lean.job.error.code"));

            Assert.IsNull(_launcher.Info, "no engine is started for code that does not compile");
            var failed = ContractJson.Deserialize<JobFailed>(_publisher.Messages.Last().Body.Span);
            Assert.AreEqual(ErrorCode.CompileError, failed.Error.Code);
            Assert.IsTrue(failed.Final);
            StringAssert.Contains("Main.cs(10,29): error CS1002: ; expected", failed.Error.Message);
            var logs = _publisher.Messages.Where(m => m.Envelope.Type == MessageTypes.PacketPrefix + "Log")
                .Select(m => (string)JsonNode.Parse(m.Body.Span)!["Message"]).ToList();
            Assert.IsTrue(logs.Any(l => l.Contains("CS1002", StringComparison.Ordinal)), string.Join("\n", logs));
            CollectionAssert.AreEqual(Enumerable.Range(1, logs.Count).Select(i => (long?)i),
                _publisher.Messages.Where(m => m.Envelope.Type == MessageTypes.PacketPrefix + "Log").Select(m => m.Envelope.Headers.Sequence));
            var log = _store.Objects[(job.Results?.Bucket ?? Buckets.Results, $"{ResultsPrefix.For(job.JobId, 1).Prefix.TrimEnd('/')}/engine.log")];
            StringAssert.Contains("CS1002", Encoding.UTF8.GetString(log));
            Assert.AreEqual("\"compile-error\"", ContractJson.Serialize(ErrorCode.CompileError));
        }

        [Test]
        public void ProjectZipsCompileEverySourceOutsideBinAndObj()
        {
            byte[] zip;
            using (var buffer = new MemoryStream())
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    void Add(string name, string text)
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                        writer.Write(text);
                    }
                    Add("MyProject/Main.cs", EditedAlgorithm.Replace("SetHoldings(\"SPY\", 0.5)", "SetHoldings(\"SPY\", Sizing.Weight)", StringComparison.Ordinal));
                    Add("MyProject/Sizing.cs", "namespace QuantConnect.Algorithm.CSharp { public static class Sizing { public const decimal Weight = 0.5m; } }");
                    Add("MyProject/MyProject.csproj", "<Project />");
                    Add("MyProject/obj/Generated.cs", "this does not compile");
                }
                zip = buffer.ToArray();
            }

            var sources = CSharpAlgorithmCompiler.ReadSources("project.zip", zip);
            CollectionAssert.AreEqual(new[] { "MyProject/Main.cs", "MyProject/Sizing.cs" }, sources.Select(s => s.Path));
            var result = CSharpAlgorithmCompiler.Compile(sources, AppContext.BaseDirectory, Path.Combine(_root, "out"), "ZipAlgorithm");
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
            Assert.AreEqual("QuantConnect.Algorithm.CSharp.EditedAlgorithm", result.TypeName);
            Assert.IsTrue(File.Exists(result.AssemblyPath));
        }

        [Test]
        public void SourcesWithSeveralAlgorithmsNeedATypeName()
        {
            var second = EditedAlgorithm.Replace("EditedAlgorithm", "OtherAlgorithm", StringComparison.Ordinal);
            var sources = new List<(string, string)> { ("A.cs", EditedAlgorithm), ("B.cs", second) };

            var ambiguous = CSharpAlgorithmCompiler.Compile(sources, AppContext.BaseDirectory, Path.Combine(_root, "a"), "Ambiguous");
            Assert.IsFalse(ambiguous.Success);
            StringAssert.Contains("several algorithm classes", ambiguous.Diagnostics.Single());

            var named = CSharpAlgorithmCompiler.Compile(sources, AppContext.BaseDirectory, Path.Combine(_root, "b"), "Named", typeName: "OtherAlgorithm");
            Assert.AreEqual("QuantConnect.Algorithm.CSharp.OtherAlgorithm", named.TypeName);

            var missing = CSharpAlgorithmCompiler.Compile(sources, AppContext.BaseDirectory, Path.Combine(_root, "c"), "Missing", typeName: "Nope");
            StringAssert.Contains("'Nope' not found", missing.Diagnostics.Single());
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
            public Action OnStart { get; set; }

            public Task<AttemptGrant> StartAttemptAsync(Guid jobId, string workerId, long deliveryCount, int? attempt, CancellationToken cancellationToken)
            {
                Calls.Add((jobId, workerId, deliveryCount, attempt));
                OnStart?.Invoke();
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
