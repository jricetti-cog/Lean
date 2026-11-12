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
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Messaging.Bus;
using QuantConnect.Packets;
using QuantConnect.Services;

namespace QuantConnect.Tests.Services
{
    [TestFixture]
    public class MessageBusTests
    {
        private static IMessageBus[] CreatePair(string kind, out IDisposable broker)
        {
            broker = null;
            if (kind == "in-memory")
            {
                var hub = new InMemoryMessageHub();
                return new IMessageBus[] { new InMemoryMessageBus(hub), new InMemoryMessageBus(hub) };
            }
            var started = ServiceTestHelpers.StartBroker();
            broker = started.Broker;
            return new IMessageBus[]
            {
                new NetMqMessageBus(started.PublishAddress, started.SubscribeAddress),
                new NetMqMessageBus(started.PublishAddress, started.SubscribeAddress)
            };
        }

        [TestCase("in-memory")]
        [TestCase("netmq")]
        public void RequestReplyRoundTripsBodyAndPayload(string kind)
        {
            var buses = CreatePair(kind, out var broker);
            try
            {
                using var responder = buses[0].Respond<DataFetchRequest, DataFetchResponse>("test.fetch", (request, _) =>
                    new DataFetchResponse { Key = request.Key.ToUpperInvariant(), Found = true });
                Assert.IsTrue(buses[0].WaitForReady(TimeSpan.FromSeconds(10)));
                Assert.IsTrue(buses[1].WaitForReady(TimeSpan.FromSeconds(10)));

                var payload = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
                using var raw = buses[0].Respond("test.echo", request => request.CreateReply<object>(null, request.Payload));
                buses[0].WaitForReady(TimeSpan.FromSeconds(10));

                var reply = buses[1].Request<DataFetchRequest, DataFetchResponse>("test.fetch", new DataFetchRequest { Key = "abc" }, ContractVersions.MarketDataV1);
                Assert.AreEqual("ABC", reply.Key);
                Assert.IsTrue(reply.Found);

                var echo = buses[1].Request("test.echo", new object(), ContractVersions.MarketDataV1, payload);
                CollectionAssert.AreEqual(payload, echo.Payload);
            }
            finally
            {
                foreach (var bus in buses)
                {
                    bus.Dispose();
                }
                broker?.Dispose();
            }
        }

        [TestCase("in-memory")]
        [TestCase("netmq")]
        public void ResponderErrorsAreReturnedToTheRequester(string kind)
        {
            var buses = CreatePair(kind, out var broker);
            try
            {
                using var responder = buses[0].Respond("test.fail", _ => throw new InvalidOperationException("boom"));
                buses[0].WaitForReady(TimeSpan.FromSeconds(10));
                buses[1].WaitForReady(TimeSpan.FromSeconds(10));
                var exception = Assert.Throws<MessageBusException>(() => buses[1].Request("test.fail", new object(), ContractVersions.JobsV1));
                StringAssert.Contains("boom", exception.Message);
            }
            finally
            {
                foreach (var bus in buses)
                {
                    bus.Dispose();
                }
                broker?.Dispose();
            }
        }

        [Test]
        public void RequestWithoutResponderTimesOut()
        {
            using var bus = new InMemoryMessageBus(new InMemoryMessageHub());
            Assert.Throws<MessageBusException>(() => bus.Request("nobody.listens", new object(), ContractVersions.JobsV1, timeout: TimeSpan.FromMilliseconds(200)));
        }

        [TestCase("in-memory")]
        [TestCase("netmq")]
        public void SubscribersReceiveMessagesInOrderByPrefix(string kind)
        {
            var buses = CreatePair(kind, out var broker);
            try
            {
                var received = new BlockingCollection<int>();
                using var subscription = buses[0].Subscribe<JobEvent>("test.events.", (jobEvent, _) => received.Add(int.Parse(jobEvent.Message)));
                buses[0].WaitForReady(TimeSpan.FromSeconds(10));
                buses[1].WaitForReady(TimeSpan.FromSeconds(10));
                for (var i = 0; i < 100; i++)
                {
                    buses[1].Publish($"test.events.job{i % 3}", new JobEvent { Message = i.ToStringInvariant() }, ContractVersions.JobsV1);
                }
                buses[1].Publish("test.other", new JobEvent { Message = "-1" }, ContractVersions.JobsV1);

                var values = Enumerable.Range(0, 100).Select(_ => received.TryTake(out var value, TimeSpan.FromSeconds(10)) ? value : -2).ToList();
                CollectionAssert.AreEqual(Enumerable.Range(0, 100), values);
                Assert.IsFalse(received.TryTake(out _, TimeSpan.FromMilliseconds(300)));
            }
            finally
            {
                foreach (var bus in buses)
                {
                    bus.Dispose();
                }
                broker?.Dispose();
            }
        }

        [Test]
        public void BusMessagingHandlerPublishesJobEvents()
        {
            var hub = new InMemoryMessageHub();
            using var listener = new InMemoryMessageBus(hub);
            using var engine = new InMemoryMessageBus(hub);
            var received = new BlockingCollection<JobEvent>();
            using var subscription = listener.Subscribe<JobEvent>(Topics.JobEvents("job-1"), (jobEvent, _) => received.Add(jobEvent));

            var handler = new BusMessagingHandler(engine, "job-1");
            handler.Send(new DebugPacket(0, "1", "1", "hello"));
            var result = new BacktestResultPacket { Progress = 0.5m };
            result.Results.Statistics = new System.Collections.Generic.Dictionary<string, string> { ["Total Orders"] = "1" };
            handler.Send(result);

            Assert.IsTrue(received.TryTake(out var debug, TimeSpan.FromSeconds(5)));
            Assert.AreEqual("hello", debug.Message);
            Assert.AreEqual(nameof(PacketType.Debug), debug.PacketType);
            Assert.IsTrue(received.TryTake(out var progress, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0.5m, progress.Progress);
            Assert.AreEqual("1", progress.Statistics["Total Orders"]);
        }
    }
}
