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
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Packets;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.EngineHandlers;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;

namespace QuantConnect.Tests.Services.EngineHandlers
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;

    [TestFixture, Category("Services"), NonParallelizable]
    public class BusEngineHandlersTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lean-engine-handlers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            EngineBus.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            EngineBus.Reset();
            Config.Reset();
            Directory.Delete(_root, true);
        }

        [TestCase("BasicTemplateAlgorithm")]
        [TestCase("BasicTemplateFrameworkAlgorithm")]
        [TestCase("UpdateOrderRegressionAlgorithm")]
        public void BusHandlersReproduceDefaultStatisticsAndOrderListHash(string algorithm)
        {
            var definition = (IRegressionAlgorithmDefinition)Activator.CreateInstance(
                Type.GetType($"QuantConnect.Algorithm.CSharp.{algorithm}, QuantConnect.Algorithm.CSharp", true));

            var defaultRun = AlgorithmRunner.RunLocalBacktest(algorithm, definition.ExpectedStatistics, Language.CSharp, definition.AlgorithmStatus);
            var defaultStatistics = new Dictionary<string, string>(((RegressionResultHandler)defaultRun.Results).FinalStatistics);
            Config.Reset();

            var bus = new InMemoryMessageBus();
            var store = new FileSystemArtifactStore(Path.Combine(_root, "minio"));
            EngineBus.Use(bus, store);
            var jobId = Guid.NewGuid();
            var jobFile = WriteJob(new JobSubmitted
            {
                JobId = jobId,
                Kind = JobKind.Backtest,
                Attempt = 1,
                SubmittedAt = DateTimeOffset.UtcNow,
                SubmittedBy = "tests",
                Backtest = new BacktestSpec { Algorithm = new BuiltinAlgorithmRef { TypeName = algorithm } },
            });

            var busRun = AlgorithmRunner.RunLocalBacktest(algorithm, definition.ExpectedStatistics, Language.CSharp, definition.AlgorithmStatus,
                customConfigurations: new Dictionary<string, string>
                {
                    { "messaging-handler", "QuantConnect.Services.EngineHandlers.BusMessagingHandler" },
                    { "job-queue-handler", "QuantConnect.Services.EngineHandlers.BusJobQueueHandler" },
                    { BusSettings.JobFile, jobFile },
                    { BusSettings.HeartbeatIntervalSeconds, "3600" },
                });
            var busStatistics = ((RegressionResultHandler)busRun.Results).FinalStatistics;

            var orderListHash = defaultStatistics["OrderListHash"];
            Assert.AreEqual(definition.ExpectedStatistics["OrderListHash"], orderListHash);
            Assert.AreEqual(orderListHash, busStatistics["OrderListHash"]);
            CollectionAssert.AreEquivalent(defaultStatistics, busStatistics);

            var published = bus.Published.Where(m => m.Envelope.Headers.JobId == jobId).ToList();
            Assert.IsNotEmpty(published);
            Assert.That(published.All(m => m.Exchange == Exchanges.Events && m.Envelope.Headers.Attempt == 1
                && m.RoutingKey.StartsWith($"job.backtest.{jobId:D}.", StringComparison.Ordinal)));
            Assert.IsTrue(published.Any(m => m.Envelope.Type == MessageTypes.JobHeartbeat));
            Assert.IsTrue(published.Any(m => m.Envelope.Type.StartsWith(MessageTypes.PacketPrefix + "Debug", StringComparison.Ordinal)
                || m.Envelope.Type == MessageTypes.PacketPrefix + "Log"));

            var sequences = published.Where(m => m.Envelope.Headers.Sequence != null).Select(m => m.Envelope.Headers.Sequence.Value).ToList();
            CollectionAssert.IsOrdered(sequences);
            CollectionAssert.AllItemsAreUnique(sequences);

            var final = published.Where(m => m.Envelope.Type == MessageTypes.PacketRef)
                .Select(m => ContractJson.Deserialize<PacketRef>(m.Body.Span))
                .Single(r => r.Progress == 1);
            Assert.AreEqual("BacktestResult", final.PacketType);
            Assert.IsTrue(final.Final);
            Assert.AreEqual("lean-result-file", final.Format);
            Assert.AreEqual(orderListHash, final.Statistics["OrderListHash"]);
            CollectionAssert.AreEquivalent(defaultStatistics, final.Statistics);

            Assert.AreEqual($"{jobId:D}/1/{final.AlgorithmId}.json", final.Object.Key);
            var stored = File.ReadAllBytes(store.PathOf(final.Object.Bucket, final.Object.Key));
            Assert.AreEqual(final.Object.Size, stored.Length);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(stored)), final.Object.Sha256);
            var resultFile = JObject.Parse(Encoding.UTF8.GetString(stored));
            var fileStatistics = resultFile.GetValue("statistics", StringComparison.OrdinalIgnoreCase).ToObject<Dictionary<string, string>>();
            CollectionAssert.AreEquivalent(defaultStatistics.Where(kvp => kvp.Key != "OrderListHash"), fileStatistics,
                "the engine's result file, uploaded unchanged; OrderListHash is only added to the packet");

            // The result file has no order events; the engine's separate order-events file is uploaded next to it.
            Assert.IsNull(resultFile.GetValue("orderEvents", StringComparison.OrdinalIgnoreCase));
            var orderEventsPath = store.PathOf(final.Object.Bucket, $"{jobId:D}/1/{final.AlgorithmId}-order-events.json");
            Assert.IsTrue(File.Exists(orderEventsPath), "order events uploaded before the final PacketRef");
            Assert.IsNotEmpty(JArray.Parse(File.ReadAllText(orderEventsPath)));

            Assert.IsFalse(published.Any(m => m.Envelope.Type == MessageTypes.PacketPrefix + "BacktestResult"
                && JsonConvert.DeserializeObject<BacktestResultPacket>(Encoding.UTF8.GetString(m.Body.Span)).Progress == 1),
                "the final result never goes inline");
        }

        [Test]
        public void NextJobBuildsThePacketFromTheDispatchedJobAndDownloadsTheUpload()
        {
            var store = new FileSystemArtifactStore(Path.Combine(_root, "minio"));
            EngineBus.Use(new InMemoryMessageBus(), store);
            var algorithmBytes = Encoding.UTF8.GetBytes("uploaded algorithm bytes");
            var obj = store.PutObjectAsync(Buckets.Algorithms, "uploads/abc/MyAlgorithm.dll", algorithmBytes, "application/octet-stream").Result;
            var jobId = Guid.NewGuid();
            var parent = Guid.NewGuid();
            Config.Set(BusSettings.WorkFolder, Path.Combine(_root, "work"));
            Config.Set(BusSettings.Job, ContractJson.Serialize(new JobSubmitted
            {
                JobId = jobId,
                Kind = JobKind.Backtest,
                Attempt = 2,
                ParentJobId = parent,
                SubmittedAt = DateTimeOffset.UtcNow,
                SubmittedBy = "tests",
                Backtest = new BacktestSpec
                {
                    Name = "my backtest",
                    Algorithm = new UploadedAlgorithmRef { Language = QuantConnect.Services.Contracts.AlgorithmLanguage.CSharp, Object = obj, TypeName = "My.Algorithm" },
                    Parameters = new Dictionary<string, string> { { "ema-fast", "10" } },
                    StartDate = new DateOnly(2013, 10, 7),
                    EndDate = new DateOnly(2013, 10, 11),
                    Cash = 250000m,
                },
            }));

            var handler = new BusJobQueueHandler();
            handler.Initialize(new Mock<IApi>().Object, null);
            var job = (BacktestNodePacket)handler.NextJob(out var algorithmPath);

            Assert.AreEqual(Path.Combine(_root, "work", jobId.ToString("D"), "2", "MyAlgorithm.dll"), algorithmPath);
            CollectionAssert.AreEqual(algorithmBytes, job.Algorithm);
            Assert.AreEqual(jobId.ToString("D"), job.BacktestId);
            Assert.AreEqual(jobId.ToString("D"), job.AlgorithmId);
            Assert.AreEqual(parent.ToString("D"), job.OptimizationId);
            Assert.AreEqual("my backtest", job.Name);
            Assert.AreEqual(Language.CSharp, job.Language);
            Assert.AreEqual("10", job.Parameters["ema-fast"]);
            Assert.AreEqual(new DateTime(2013, 10, 7), job.PeriodStart);
            Assert.AreEqual(new DateTime(2013, 10, 11), job.PeriodFinish);
            Assert.AreEqual(250000m, job.CashAmount.Value.Amount);
            Assert.AreEqual("My.Algorithm", Config.Get("algorithm-type-name"));
            Assert.AreEqual(jobId, EngineBus.Job.JobId);
            Assert.AreEqual(2, EngineBus.Job.Attempt);
            Assert.AreEqual($"{jobId:D}/2/", EngineBus.Job.Results.Prefix);
        }

        [Test]
        public void NextJobRejectsAnUploadWhoseHashDoesNotMatch()
        {
            var store = new FileSystemArtifactStore(Path.Combine(_root, "minio"));
            EngineBus.Use(new InMemoryMessageBus(), store);
            var obj = store.PutObjectAsync(Buckets.Algorithms, "uploads/abc/main.py", Encoding.UTF8.GetBytes("print(1)")).Result;
            Config.Set(BusSettings.WorkFolder, Path.Combine(_root, "work"));
            Config.Set(BusSettings.Job, ContractJson.Serialize(new JobSubmitted
            {
                JobId = Guid.NewGuid(),
                Kind = JobKind.Backtest,
                Attempt = 1,
                SubmittedAt = DateTimeOffset.UtcNow,
                SubmittedBy = "tests",
                Backtest = new BacktestSpec
                {
                    Algorithm = new UploadedAlgorithmRef { Language = QuantConnect.Services.Contracts.AlgorithmLanguage.Python, Object = obj with { Sha256 = new string('0', 64) } },
                },
            }));

            var handler = new BusJobQueueHandler();
            handler.Initialize(new Mock<IApi>().Object, null);
            Assert.Throws<InvalidDataException>(() => handler.NextJob(out _));
        }

        [Test]
        public void SendPublishesPacketsAndDisposeDrainsTheOutboxAndClosesTheBus()
        {
            var bus = new DisposableBus();
            var store = new FileSystemArtifactStore(Path.Combine(_root, "minio"));
            EngineBus.Use(bus, store, ownsBus: true);
            var job = Context();
            EngineBus.Job = job;

            var handler = new BusMessagingHandler();
            handler.SetAuthentication(new BacktestNodePacket { BacktestId = "algo" });
            handler.Send(new DebugPacket(0, "algo", "", "hello"));
            handler.Send(new LogPacket("algo", new string('x', OutgoingMessage.MaxInlineBodyBytes + 1)));
            handler.Send(new AlphaResultPacket());
            handler.Dispose();

            Assert.IsTrue(bus.Disposed);
            var types = bus.Inner.Published.Select(m => m.Envelope.Type).ToList();
            Assert.Contains(MessageTypes.JobHeartbeat, types);
            Assert.Contains(MessageTypes.PacketPrefix + "Debug", types);
            Assert.IsFalse(types.Any(t => t.EndsWith("AlphaResult", StringComparison.Ordinal)), "AlphaResult is not a v1 packet");

            var debug = bus.Inner.Published.Single(m => m.Envelope.Type == MessageTypes.PacketPrefix + "Debug");
            Assert.AreEqual($"job.backtest.{job.JobId:D}.log.v1", debug.RoutingKey);
            Assert.AreEqual("Debug", debug.Envelope.Headers.PacketType);
            Assert.AreEqual("hello", JsonConvert.DeserializeObject<DebugPacket>(Encoding.UTF8.GetString(debug.Body.Span)).Message);

            var oversize = ContractJson.Deserialize<PacketRef>(bus.Inner.Published.Single(m => m.Envelope.Type == MessageTypes.PacketRef).Body.Span);
            Assert.AreEqual("Log", oversize.PacketType);
            Assert.IsFalse(oversize.Final);
            Assert.AreEqual("lean-packet", oversize.Format);
            Assert.Greater(oversize.Object.Size, OutgoingMessage.MaxInlineBodyBytes);
            Assert.IsTrue(File.Exists(store.PathOf(oversize.Object.Bucket, oversize.Object.Key)));

            var heartbeat = ContractJson.Deserialize<JobHeartbeat>(bus.Inner.Published.First(m => m.Envelope.Type == MessageTypes.JobHeartbeat).Body.Span);
            Assert.AreEqual("worker-1", heartbeat.WorkerId);
            Assert.IsTrue(heartbeat.EngineAlive);
        }

        [Test]
        public void DisposeKeepsDrainingAnOutboxLongerThanTheShutdownTimeoutWhileItMakesProgress()
        {
            var bus = new DisposableBus { PublishDelay = TimeSpan.FromMilliseconds(100) };
            EngineBus.Use(bus, new FileSystemArtifactStore(Path.Combine(_root, "minio")), ownsBus: true);
            EngineBus.Job = Context();
            Config.Set(BusSettings.ShutdownTimeoutSeconds, "1");
            try
            {
                var handler = new BusMessagingHandler();
                handler.SetAuthentication(new BacktestNodePacket { BacktestId = "algo" });
                for (var i = 0; i < 25; i++)
                {
                    handler.Send(new DebugPacket(0, "algo", "", $"line {i}"));
                }
                handler.Dispose();
            }
            finally
            {
                Config.Set(BusSettings.ShutdownTimeoutSeconds, "30");
            }

            var debug = bus.Inner.Published.Where(m => m.Envelope.Type == MessageTypes.PacketPrefix + "Debug")
                .Select(m => JsonConvert.DeserializeObject<DebugPacket>(Encoding.UTF8.GetString(m.Body.Span)).Message).ToList();
            CollectionAssert.AreEqual(Enumerable.Range(0, 25).Select(i => $"line {i}"), debug, "every packet is published, in order");
        }

        [Test]
        public void WarmUpPythonConversionsBuildsPythonnetsLazyConverters()
        {
            BusJobQueueHandler.WarmUpPythonConversions();

            var lazies = typeof(global::Python.Runtime.PyObject).Assembly.GetType("Python.Runtime.Converter")!
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>))
                .ToDictionary(f => f.Name, f => (bool)f.FieldType.GetProperty("IsValueCreated")!.GetValue(f.GetValue(null))!);
            TestContext.Progress.WriteLine(string.Join(", ", lazies.Select(l => $"{l.Key}={l.Value}")));
            Assert.IsNotEmpty(lazies);
            Assert.IsTrue(lazies.Values.All(created => created), "every lazy converter is built before any data-feed thread");
        }

        [Test]
        public void PublishFailuresNeverReachTheEngine()
        {
            var bus = new InMemoryMessageBus { NackWhen = _ => true };
            EngineBus.Use(bus, new FileSystemArtifactStore(Path.Combine(_root, "minio")));
            EngineBus.Job = Context();

            var handler = new BusMessagingHandler();
            handler.SetAuthentication(new BacktestNodePacket { BacktestId = "algo" });
            Assert.DoesNotThrow(() => handler.Send(new DebugPacket(0, "algo", "", "hello")));
            Assert.DoesNotThrow(handler.Dispose);
            Assert.IsEmpty(bus.Published);
        }

        [Test]
        public void WithoutAJobTheHandlerBehavesLikeMessaging()
        {
            var bus = new InMemoryMessageBus();
            EngineBus.Use(bus);

            var handler = new BusMessagingHandler();
            handler.SetAuthentication(new BacktestNodePacket { BacktestId = "algo" });
            handler.Send(new DebugPacket(0, "algo", "", "hello"));
            handler.Dispose();

            Assert.IsNull(handler.Job);
            Assert.IsEmpty(bus.Published);
        }

        private static BusJobContext Context() => new()
        {
            JobId = Guid.NewGuid(),
            Kind = JobKind.Backtest,
            Attempt = 1,
            WorkerId = "worker-1",
            Results = new ResultsPrefix { Prefix = "results/" },
        };

        private string WriteJob(JobSubmitted job)
        {
            var path = Path.Combine(_root, "job.json");
            File.WriteAllText(path, ContractJson.Serialize(job));
            return path;
        }

        private sealed class DisposableBus : IMessageBus, IAsyncDisposable
        {
            public InMemoryMessageBus Inner { get; } = new();
            public bool Disposed { get; private set; }

            public TimeSpan PublishDelay { get; init; }

            public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
            {
                if (PublishDelay > TimeSpan.Zero)
                {
                    await Task.Delay(PublishDelay, cancellationToken);
                }
                if (Disposed) throw new ObjectDisposedException(nameof(DisposableBus));
                await Inner.PublishAsync(message, cancellationToken);
            }

            public Task<IAsyncDisposable> ConsumeAsync(string queue, MessageHandler handler, ConsumerOptions options = null, CancellationToken cancellationToken = default) =>
                Inner.ConsumeAsync(queue, handler, options, cancellationToken);

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
