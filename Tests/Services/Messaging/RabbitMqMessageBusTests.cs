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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.RabbitMq;

namespace QuantConnect.Tests.Services.Messaging
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    /// <summary>
    /// Runs against a real broker when <c>LEAN_TEST_RABBITMQ_URI</c> is set (e.g. <c>amqp://guest:guest@localhost:5672/lean</c>); ignored otherwise.
    /// </summary>
    [TestFixture, Category("Services"), Category("RabbitMQ"), NonParallelizable]
    public class RabbitMqMessageBusTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private RabbitMqMessageBus _bus;

        [OneTimeSetUp]
        public async Task Connect()
        {
            var uri = Environment.GetEnvironmentVariable("LEAN_TEST_RABBITMQ_URI");
            if (string.IsNullOrEmpty(uri))
            {
                Assert.Ignore("LEAN_TEST_RABBITMQ_URI not set");
            }
            _bus = new RabbitMqMessageBus(new RabbitMqOptions { Uri = new Uri(uri), ClientName = "lean-services-tests", PublishTimeout = TimeSpan.FromSeconds(10) });
            await _bus.DeclareAsync(TopologyDefinition.V1);
        }

        [SetUp]
        public async Task Purge()
        {
            var connection = await _bus.EnsureConnectedAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in TopologyDefinition.V1.Queues)
            {
                await channel.QueuePurgeAsync(queue.Name);
            }
        }

        [OneTimeTearDown]
        public async Task Dispose()
        {
            if (_bus != null)
            {
                await _bus.DisposeAsync();
            }
        }

        private static async Task<T> Within<T>(Task<T> task) =>
            await task.WaitAsync(Timeout);

        [Test]
        public async Task ConfirmedPublishIsConsumedAndAcked()
        {
            var jobId = Guid.NewGuid();
            var received = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (await _bus.ConsumeAsync(Queues.Backtest, (m, ct) =>
            {
                received.TrySetResult(m);
                return Task.FromResult(MessageDisposition.Ack);
            }))
            {
                await _bus.PublishAsync(InMemoryMessageBusTests.Dispatch(jobId));
                var message = await Within(received.Task);

                Assert.AreEqual(MessageTypes.BacktestRequested, message.Envelope.Type);
                Assert.AreEqual(jobId, message.Envelope.CorrelationId);
                Assert.AreEqual(jobId, message.Envelope.Headers.JobId);
                Assert.AreEqual(JobKind.Backtest, message.Envelope.Headers.JobKind);
                Assert.AreEqual(MessageEnvelope.Persistent, message.Envelope.DeliveryMode);
                Assert.AreEqual(RoutingKeys.Dispatch(JobKind.Backtest), message.RoutingKey);
                Assert.AreEqual(jobId, message.GetBody<JobSubmitted>().JobId);
            }
            await Task.Delay(200);
            Assert.AreEqual(0u, await MessageCount(Queues.Backtest), "acked message must not be requeued");
        }

        [Test]
        public async Task RetryIsRedeliveredThenDeadLetteredAtDeliveryLimit()
        {
            var deliveries = new ConcurrentQueue<(long Count, bool Redelivered)>();
            var dead = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var deadConsumer = await _bus.ConsumeAsync(Queues.DeadLetter(Queues.Backtest), (m, ct) =>
            {
                dead.TrySetResult(m);
                return Task.FromResult(MessageDisposition.Ack);
            });
            await using (await _bus.ConsumeAsync(Queues.Backtest, (m, ct) =>
            {
                deliveries.Enqueue((m.DeliveryCount, m.Redelivered));
                return Task.FromResult(MessageDisposition.Retry);
            }))
            {
                await _bus.PublishAsync(InMemoryMessageBusTests.Dispatch(Guid.NewGuid()));
                var message = await Within(dead.Task);
                Assert.AreEqual(MessageTypes.BacktestRequested, message.Envelope.Type);
                Assert.AreEqual(RoutingKeys.DeadLetter(Queues.Backtest), message.RoutingKey);
            }
            var seen = deliveries.ToArray();
            Assert.AreEqual(4, seen.Length, "x-delivery-limit 3: first delivery + 3 redeliveries");
            Assert.IsFalse(seen[0].Redelivered);
            Assert.IsTrue(seen.Skip(1).All(d => d.Redelivered));
            CollectionAssert.AreEqual(new long[] { 0, 1, 2, 3 }, seen.Select(d => d.Count));
        }

        [Test]
        public async Task PoisonMessageIsDeadLetteredWithoutRedelivery()
        {
            var calls = 0;
            var dead = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var deadConsumer = await _bus.ConsumeAsync(Queues.DeadLetter(Queues.Live), (m, ct) =>
            {
                dead.TrySetResult(m);
                return Task.FromResult(MessageDisposition.Ack);
            });
            await using (await _bus.ConsumeAsync(Queues.Live, (m, ct) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(MessageDisposition.DeadLetter);
            }))
            {
                await _bus.PublishAsync(InMemoryMessageBusTests.Dispatch(Guid.NewGuid(), JobKind.Live));
                await Within(dead.Task);
            }
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void UnroutableMandatoryPublishIsReturned()
        {
            var message = InMemoryMessageBusTests.Dispatch(Guid.NewGuid()) with { RoutingKey = "jobs.unknown.v1" };
            Assert.ThrowsAsync<UnroutableMessageException>(() => _bus.PublishAsync(message));
        }

        [Test]
        public async Task EventFansOutOnTopicExchange()
        {
            await _bus.PublishAsync(InMemoryMessageBusTests.Event(Guid.NewGuid()));
            await Task.Delay(200);
            Assert.AreEqual(1u, await MessageCount(Queues.OrchestratorStatus));
            Assert.AreEqual(1u, await MessageCount(Queues.ResultsPackets));
            Assert.AreEqual(1u, await MessageCount(Queues.OptimizerChildren));
        }

        private async Task<uint> MessageCount(string queue)
        {
            var connection = await _bus.EnsureConnectedAsync();
            await using var channel = await connection.CreateChannelAsync();
            return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
        }
    }
}
