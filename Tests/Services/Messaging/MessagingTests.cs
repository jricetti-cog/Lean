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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Messaging.RabbitMq;

namespace QuantConnect.Tests.Services.Messaging
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    [TestFixture, Category("Services")]
    public class RoutingKeysTests
    {
        private static readonly Guid JobId = Guid.Parse("3f2b8c1e-6a4d-4e5f-9b7a-1c2d3e4f5a6b");

        [TestCase(JobKind.Backtest, "jobs.backtest.v1", Queues.Backtest)]
        [TestCase(JobKind.Live, "jobs.live.v1", Queues.Live)]
        [TestCase(JobKind.Optimization, "jobs.optimization.v1", Queues.Optimization)]
        [TestCase(JobKind.Report, "jobs.report.v1", Queues.Report)]
        [TestCase(JobKind.MarketData, "marketdata.fetch.v1", Queues.MarketDataFetch)]
        public void DispatchKeysAreBoundToTheirQueue(JobKind kind, string key, string queue)
        {
            Assert.AreEqual(key, RoutingKeys.Dispatch(kind));
            Assert.AreEqual(queue, Queues.Dispatch(kind));
            Assert.IsTrue(TopologyDefinition.V1.Bindings.Any(b => b.Source == Exchanges.Jobs && b.Destination == queue && b.RoutingKey == key));
        }

        [Test]
        public void EventControlMarketDataAndDeadLetterKeys()
        {
            Assert.AreEqual($"job.backtest.{JobId}.status.v1", RoutingKeys.EventFor(MessageTypes.JobCompleted, JobKind.Backtest, JobId));
            Assert.AreEqual($"job.live.{JobId}.heartbeat.v1", RoutingKeys.EventFor(MessageTypes.JobHeartbeat, JobKind.Live, JobId));
            Assert.AreEqual($"job.optimization.{JobId}.progress.v1", RoutingKeys.EventFor(MessageTypes.JobProgress, JobKind.Optimization, JobId));
            Assert.AreEqual($"job.backtest.{JobId}.result.v1", RoutingKeys.EventFor(MessageTypes.PacketRef, JobKind.Backtest, JobId));
            Assert.AreEqual($"job.{JobId}.cancel.v1", RoutingKeys.Cancel(JobId));
            Assert.AreEqual("md.equity.usa.spy.v1", RoutingKeys.MarketData("Equity", "USA", "SPY"));
            Assert.AreEqual("jobs.backtest.dead.v1", RoutingKeys.DeadLetter(Queues.Backtest));
        }

        [TestCase("jobs.backtest.v1", true)]
        [TestCase("job.live.x.status.v1", true)]
        [TestCase("jobs.backtest.v2", false)]
        [TestCase("jobs.backtest", false)]
        [TestCase("job.*.v1", false)]
        [TestCase("job.#.v1", false)]
        [TestCase("jobs..v1", false)]
        [TestCase(".v1", false)]
        [TestCase("", false)]
        public void IsV1(string key, bool expected)
        {
            Assert.AreEqual(expected, RoutingKeys.IsV1(key));
        }

        [TestCase("job.*.*.status.v1", "job.live.abc.status.v1", true)]
        [TestCase("job.*.*.status.v1", "job.live.abc.progress.v1", false)]
        [TestCase("job.#", "job.live.abc.status.v1", true)]
        [TestCase("#.v1", "jobs.backtest.v1", true)]
        [TestCase("job.*.v1", "job.a.b.v1", false)]
        [TestCase("jobs.backtest.v1", "jobs.backtest.v1", true)]
        [TestCase("#", "", true)]
        public void TopicMatching(string pattern, string key, bool expected)
        {
            Assert.AreEqual(expected, RoutingKeys.TopicMatches(pattern, key));
        }
    }

    [TestFixture, Category("Services")]
    public class TopologyDefinitionTests
    {
        [Test]
        public void EmbeddedDefinitionsMatchTheCheckedInContract()
        {
            var topology = TopologyDefinition.V1;
            CollectionAssert.AreEquivalent(
                new[] { Exchanges.Jobs, Exchanges.JobsDeadLetter, Exchanges.Events, Exchanges.Control, Exchanges.MarketDataLive },
                topology.Exchanges.Select(e => e.Name));
            Assert.IsFalse(topology.Exchanges.Single(e => e.Name == Exchanges.MarketDataLive).Durable);
            Assert.IsTrue(topology.Exchanges.Where(e => e.Name != Exchanges.MarketDataLive).All(e => e.Durable && !e.AutoDelete));
            Assert.IsTrue(topology.Queues.All(q => q.Durable && !q.AutoDelete));

            foreach (var queue in new[] { Queues.Backtest, Queues.Live, Queues.Report, Queues.Optimization, Queues.MarketDataFetch })
            {
                var definition = topology.Queues.Single(q => q.Name == queue);
                Assert.AreEqual(3, definition.DeliveryLimit, queue);
                Assert.AreEqual(Exchanges.JobsDeadLetter, definition.DeadLetterExchange, queue);
                Assert.AreEqual(RoutingKeys.DeadLetter(queue), definition.DeadLetterRoutingKey, queue);
                Assert.IsTrue(topology.Queues.Any(q => q.Name == Queues.DeadLetter(queue)), queue);
            }
            foreach (var queue in new[] { Queues.ResultsPackets, Queues.OrchestratorStatus, Queues.OptimizerChildren })
            {
                Assert.AreEqual(5, topology.Queues.Single(q => q.Name == queue).DeliveryLimit, queue);
            }
            Assert.IsTrue(topology.Bindings.All(b => b.RoutingKey.EndsWith(".v1", StringComparison.Ordinal) || b.RoutingKey.EndsWith(".#", StringComparison.Ordinal)),
                string.Join(", ", topology.Bindings.Select(b => b.RoutingKey)));
        }
    }

    [TestFixture, Category("Services")]
    public class InMemoryMessageBusTests
    {
        private static readonly DateTimeOffset At = new(2026, 11, 22, 13, 30, 0, TimeSpan.Zero);

        internal static OutgoingMessage Dispatch(Guid jobId, JobKind kind = JobKind.Backtest)
        {
            var body = new JobSubmitted
            {
                JobId = jobId, Kind = kind, Attempt = 1, SubmittedAt = At, SubmittedBy = "test", Results = ResultsPrefix.For(jobId, 1),
                Backtest = new BacktestSpec { Algorithm = new BuiltinAlgorithmRef { TypeName = "BasicTemplateAlgorithm" } },
            };
            var type = kind == JobKind.Live ? MessageTypes.LiveRequested : MessageTypes.BacktestRequested;
            var envelope = MessageEnvelope.Create(type, jobId, 1, AppIds.Orchestrator, kind);
            return OutgoingMessage.Create(Exchanges.Jobs, RoutingKeys.Dispatch(kind), envelope, body);
        }

        internal static OutgoingMessage Event(Guid jobId)
        {
            var body = new JobStarted { JobId = jobId, Kind = JobKind.Backtest, Attempt = 1, StartedAt = At, WorkerId = "w", Results = ResultsPrefix.For(jobId, 1) };
            var envelope = MessageEnvelope.Create(MessageTypes.JobStarted, jobId, 1, AppIds.BacktestWorker, JobKind.Backtest);
            return OutgoingMessage.Create(Exchanges.Events, RoutingKeys.EventFor(MessageTypes.JobStarted, JobKind.Backtest, jobId), envelope, body);
        }

        [Test]
        public async Task ConfirmedPublishIsRoutedAndAckRemovesIt()
        {
            var bus = new InMemoryMessageBus();
            var jobId = Guid.NewGuid();
            await bus.PublishAsync(Dispatch(jobId));

            Assert.AreEqual(1, bus.Published.Count);
            Assert.AreEqual(1, bus.Peek(Queues.Backtest).Count);
            Assert.AreEqual(0, bus.Peek(Queues.Live).Count);

            var received = new List<ReceivedMessage>();
            await using var _ = await bus.ConsumeAsync(Queues.Backtest, (m, ct) => { received.Add(m); return Task.FromResult(MessageDisposition.Ack); });
            Assert.AreEqual(1, await bus.DeliverAllAsync());

            Assert.AreEqual(0, bus.Peek(Queues.Backtest).Count);
            var message = received.Single();
            Assert.AreEqual(MessageTypes.BacktestRequested, message.Envelope.Type);
            Assert.AreEqual(jobId, message.GetBody<JobSubmitted>().JobId);
            Assert.IsInstanceOf<JobSubmitted>(message.GetBody());
            Assert.IsFalse(message.Redelivered);
        }

        [Test]
        public async Task EventsFanOutToEveryBoundQueue()
        {
            var bus = new InMemoryMessageBus();
            await bus.PublishAsync(Event(Guid.NewGuid()));
            Assert.AreEqual(1, bus.Peek(Queues.OrchestratorStatus).Count);
            Assert.AreEqual(1, bus.Peek(Queues.ResultsPackets).Count);
            Assert.AreEqual(1, bus.Peek(Queues.OptimizerChildren).Count, "optimizer.children binds job.backtest.*.status.v1");

            var jobId = Guid.NewGuid();
            var heartbeat = MessageEnvelope.Create(MessageTypes.JobHeartbeat, jobId, 1, AppIds.LiveWorker, JobKind.Live);
            await bus.PublishAsync(OutgoingMessage.Create(Exchanges.Events, RoutingKeys.EventFor(MessageTypes.JobHeartbeat, JobKind.Live, jobId), heartbeat,
                new JobHeartbeat { JobId = jobId, Kind = JobKind.Live, Attempt = 1, At = At, WorkerId = "w" }));
            Assert.AreEqual(2, bus.Peek(Queues.OrchestratorStatus).Count);
            Assert.AreEqual(1, bus.Peek(Queues.ResultsPackets).Count);
        }

        [Test]
        public void NackedPublishThrowsAndIsNotRecorded()
        {
            var bus = new InMemoryMessageBus { NackWhen = _ => true };
            Assert.ThrowsAsync<MessagePublishException>(() => bus.PublishAsync(Dispatch(Guid.NewGuid())));
            Assert.AreEqual(0, bus.Published.Count);
            Assert.AreEqual(0, bus.Peek(Queues.Backtest).Count);
        }

        [Test]
        public void UnroutableMandatoryPublishThrows()
        {
            var bus = new InMemoryMessageBus();
            var message = Dispatch(Guid.NewGuid()) with { RoutingKey = "jobs.unknown.v1" };
            Assert.ThrowsAsync<UnroutableMessageException>(() => bus.PublishAsync(message));
            Assert.DoesNotThrowAsync(() => bus.PublishAsync(message with { Mandatory = false }));
        }

        [Test]
        public void InvalidMessagesAreRejectedBeforePublishing()
        {
            var bus = new InMemoryMessageBus();
            var valid = Dispatch(Guid.NewGuid());
            Assert.ThrowsAsync<ArgumentException>(() => bus.PublishAsync(valid with { RoutingKey = "jobs.backtest.v2" }));
            Assert.ThrowsAsync<ArgumentException>(() => bus.PublishAsync(valid with { RoutingKey = "jobs.*.v1" }));
            Assert.ThrowsAsync<ArgumentException>(() => bus.PublishAsync(valid with { Envelope = valid.Envelope with { Type = "jobs.v2.BacktestRequested" } }));
            Assert.ThrowsAsync<ArgumentException>(() => bus.PublishAsync(valid with { Envelope = valid.Envelope with { Headers = valid.Envelope.Headers with { ContractVersion = "2.0" } } }));
            Assert.ThrowsAsync<MessageTooLargeException>(() => bus.PublishAsync(valid with { Body = new byte[OutgoingMessage.MaxInlineBodyBytes + 1] }));
            Assert.AreEqual(0, bus.Published.Count);
        }

        [Test]
        public async Task RetryRequeuesUntilDeliveryLimitThenDeadLetters()
        {
            var bus = new InMemoryMessageBus();
            await bus.PublishAsync(Dispatch(Guid.NewGuid()));
            var seen = new List<(long Count, bool Redelivered)>();
            await using var _ = await bus.ConsumeAsync(Queues.Backtest, (m, ct) =>
            {
                seen.Add((m.DeliveryCount, m.Redelivered));
                return Task.FromResult(MessageDisposition.Retry);
            });

            Assert.AreEqual(4, await bus.DeliverAllAsync(), "x-delivery-limit 3: first delivery + 3 redeliveries");
            CollectionAssert.AreEqual(new[] { (0L, false), (1L, true), (2L, true), (3L, true) }, seen);
            Assert.AreEqual(0, bus.Peek(Queues.Backtest).Count);
            var dead = bus.Peek(Queues.DeadLetter(Queues.Backtest)).Single();
            Assert.AreEqual(RoutingKeys.DeadLetter(Queues.Backtest), dead.RoutingKey);
            Assert.AreEqual(Exchanges.JobsDeadLetter, dead.Exchange);
            Assert.AreEqual(MessageTypes.BacktestRequested, dead.Envelope.Type);
        }

        [Test]
        public async Task HandlerExceptionIsRetried()
        {
            var bus = new InMemoryMessageBus();
            await bus.PublishAsync(Dispatch(Guid.NewGuid()));
            var calls = 0;
            await using var _ = await bus.ConsumeAsync(Queues.Backtest, (m, ct) =>
                ++calls == 1 ? throw new InvalidOperationException("transient") : Task.FromResult(MessageDisposition.Ack));
            Assert.AreEqual(2, await bus.DeliverAllAsync());
            Assert.AreEqual(0, bus.Peek(Queues.DeadLetter(Queues.Backtest)).Count);
        }

        [Test]
        public async Task PoisonMessageIsDeadLetteredImmediately()
        {
            var bus = new InMemoryMessageBus();
            await bus.PublishAsync(Dispatch(Guid.NewGuid(), JobKind.Live));
            await using var _ = await bus.ConsumeAsync(Queues.Live, (m, ct) => Task.FromResult(MessageDisposition.DeadLetter));
            Assert.AreEqual(1, await bus.DeliverAllAsync());
            Assert.AreEqual(1, bus.Peek(Queues.DeadLetter(Queues.Live)).Count);
        }

        [Test]
        public async Task CompetingConsumersAreRoundRobinedAndDisposedConsumersStop()
        {
            var bus = new InMemoryMessageBus();
            for (var i = 0; i < 4; i++)
            {
                await bus.PublishAsync(Dispatch(Guid.NewGuid()));
            }
            var a = 0;
            var b = 0;
            var first = await bus.ConsumeAsync(Queues.Backtest, (m, ct) => { a++; return Task.FromResult(MessageDisposition.Ack); });
            var second = await bus.ConsumeAsync(Queues.Backtest, (m, ct) => { b++; return Task.FromResult(MessageDisposition.Ack); });
            await bus.DeliverAllAsync();
            Assert.AreEqual((2, 2), (a, b));

            await second.DisposeAsync();
            await bus.PublishAsync(Dispatch(Guid.NewGuid()));
            await bus.DeliverAllAsync();
            Assert.AreEqual((3, 2), (a, b));
            await first.DisposeAsync();
        }

        [Test]
        public async Task DependencyInjectionRegistrations()
        {
            await using var inMemory = new ServiceCollection().AddLeanInMemoryMessaging().BuildServiceProvider();
            Assert.AreSame(inMemory.GetRequiredService<InMemoryMessageBus>(), inMemory.GetRequiredService<IMessagePublisher>());
            Assert.AreSame(inMemory.GetRequiredService<IMessageBus>(), inMemory.GetRequiredService<IMessageConsumer>());

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["RabbitMQ:Uri"] = "amqp://u:p@rabbitmq:5672/lean",
                ["RabbitMQ:ClientName"] = "gateway",
                ["RabbitMQ:PublishTimeoutSeconds"] = "5",
            }).Build();
            await using var rabbit = new ServiceCollection().AddLogging().AddLeanRabbitMq(configuration).BuildServiceProvider();
            var options = rabbit.GetRequiredService<RabbitMqOptions>();
            Assert.AreEqual(new Uri("amqp://u:p@rabbitmq:5672/lean"), options.Uri);
            Assert.AreEqual("gateway", options.ClientName);
            Assert.AreEqual(TimeSpan.FromSeconds(5), options.PublishTimeout);
            Assert.IsInstanceOf<RabbitMqMessageBus>(rabbit.GetRequiredService<IMessageBus>());
        }

        [Test]
        public void AmqpHeadersRoundTrip()
        {
            var envelope = MessageEnvelope.Create(MessageTypes.JobHeartbeat, Guid.NewGuid(), 3, AppIds.LiveWorker, JobKind.Live, leaseEpoch: 7, sequence: 11);
            var table = AmqpHeaders.ToAmqp(envelope.Headers);
            Assert.AreEqual("1.0", table["contract-version"]);
            Assert.AreEqual("live", table["job-kind"]);
            Assert.AreEqual(7L, table["lease-epoch"]);

            // RabbitMQ.Client delivers strings as byte[]
            var wire = table.ToDictionary(kv => kv.Key, kv => kv.Value is string s ? (object)Encoding.UTF8.GetBytes(s) : kv.Value);
            wire["x-delivery-count"] = 2L;
            wire["x-death"] = new List<object> { new Dictionary<string, object>() };
            var back = AmqpHeaders.FromAmqp(wire);
            Assert.AreEqual(envelope.Headers with { DeliveryCount = 2 }, back with { Extra = envelope.Headers.Extra });
        }
    }
}
