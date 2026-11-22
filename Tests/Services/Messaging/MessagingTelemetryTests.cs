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
using System.Diagnostics;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Tests.Services.Messaging
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    [TestFixture, Category("Services")]
    public class MessagingTelemetryTests
    {
        private static readonly Guid JobId = Guid.Parse("3f2b8c1e-6a4d-4e5f-9b7a-1c2d3e4f5a6b");
        private ActivityListener _listener;
        private readonly List<Activity> _stopped = new();

        [SetUp]
        public void SetUp()
        {
            Activity.Current = null;
            _stopped.Clear();
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == MessagingTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _stopped.Add(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        [TearDown]
        public void TearDown()
        {
            _listener.Dispose();
            Activity.Current = null;
        }

        [Test]
        public void EnvelopeCarriesTheCurrentW3CTraceContext()
        {
            Assert.IsNull(MessageEnvelope.Create(MessageTypes.JobCompleted, JobId, 1).Headers.TraceParent);

            using var span = new Activity("gateway request").SetIdFormat(ActivityIdFormat.W3C).Start();
            span.TraceStateString = "lean=1";
            var envelope = MessageEnvelope.Create(MessageTypes.JobCompleted, JobId, 1);

            Assert.AreEqual(span.Id, envelope.Headers.TraceParent);
            Assert.AreEqual("lean=1", envelope.Headers.TraceState);
            StringAssert.IsMatch("^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$", envelope.Headers.TraceParent);
        }

        [Test]
        public void TraceContextRoundTripsThroughAmqpHeaders()
        {
            using var span = new Activity("publish").SetIdFormat(ActivityIdFormat.W3C).Start();
            var headers = MessageEnvelope.Create(MessageTypes.BacktestRequested, JobId, 1).Headers;
            var table = AmqpHeaders.ToAmqp(headers);
            table["traceparent"] = System.Text.Encoding.UTF8.GetBytes((string)table["traceparent"]);

            var back = AmqpHeaders.FromAmqp(table);

            Assert.AreEqual(span.Id, back.TraceParent);
            Assert.IsNull(back.Extra);
        }

        [Test]
        public void WithTraceParentOnlyFillsAMissingContext()
        {
            const string worker = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
            var bare = MessageEnvelope.Create(MessageTypes.PacketRef, JobId, 1);
            Assert.AreEqual(worker, bare.WithTraceParent(worker).Headers.TraceParent);
            Assert.AreSame(bare, bare.WithTraceParent(null));

            using var span = new Activity("engine").SetIdFormat(ActivityIdFormat.W3C).Start();
            var stamped = MessageEnvelope.Create(MessageTypes.PacketRef, JobId, 1);
            Assert.AreEqual(span.Id, stamped.WithTraceParent(worker).Headers.TraceParent);
        }

        [Test]
        public void PublishAndProcessSpansJoinThePublishersTrace()
        {
            const string parent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
            var envelope = MessageEnvelope.Create(MessageTypes.BacktestRequested, JobId, 2, kind: JobKind.Backtest).WithTraceParent(parent);
            var outgoing = OutgoingMessage.Create(Exchanges.Jobs, RoutingKeys.Dispatch(JobKind.Backtest), envelope, new { });

            MessageHeaders published;
            using (var producer = MessagingTelemetry.StartPublish(outgoing))
            {
                Assert.IsNotNull(producer);
                Assert.AreEqual(ActivityKind.Producer, producer.Kind);
                Assert.AreEqual("0af7651916cd43dd8448eb211c80319c", producer.TraceId.ToString());
                Assert.AreEqual("b7ad6b7169203331", producer.ParentSpanId.ToString());
                Assert.AreEqual("rabbitmq", producer.GetTagItem("messaging.system"));
                Assert.AreEqual(JobId.ToString("D"), producer.GetTagItem("lean.job.id"));
                published = MessagingTelemetry.HeadersFor(envelope with { Headers = envelope.Headers with { DeliveryCount = 3 } }, producer);
                Assert.AreEqual(producer.Id, published.TraceParent);
                Assert.IsNull(published.DeliveryCount);
            }

            var received = new ReceivedMessage
            {
                Queue = Queues.Backtest,
                Exchange = Exchanges.Jobs,
                RoutingKey = outgoing.RoutingKey,
                Envelope = envelope with { Headers = published },
                Body = outgoing.Body,
            };
            using (var consumer = MessagingTelemetry.StartProcess(received))
            {
                Assert.AreEqual(ActivityKind.Consumer, consumer.Kind);
                Assert.AreEqual("0af7651916cd43dd8448eb211c80319c", consumer.TraceId.ToString());
                Assert.AreEqual(_stopped[0].SpanId, consumer.ParentSpanId);
                Assert.AreEqual($"{Queues.Backtest} process", consumer.DisplayName);
                MessagingTelemetry.RecordProcessed(consumer, received.Queue, envelope.Type, MessageDisposition.DeadLetter, TimeSpan.FromMilliseconds(5));
                Assert.AreEqual(ActivityStatusCode.Error, consumer.Status);
                Assert.AreEqual("dead_letter", consumer.GetTagItem("lean.message.disposition"));
            }
        }

        [Test]
        public void WithoutAListenerPublishingKeepsTheHeaderContext()
        {
            _listener.Dispose();
            const string parent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
            var envelope = MessageEnvelope.Create(MessageTypes.PacketRef, JobId, 1).WithTraceParent(parent);
            var outgoing = OutgoingMessage.Create(Exchanges.Events, RoutingKeys.EventFor(MessageTypes.PacketRef, JobKind.Backtest, JobId), envelope, new { });

            using var producer = MessagingTelemetry.StartPublish(outgoing);

            Assert.IsNull(producer);
            Assert.AreEqual(parent, MessagingTelemetry.HeadersFor(envelope, producer).TraceParent);
        }
    }
}
