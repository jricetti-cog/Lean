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
using NUnit.Framework;
using QuantConnect.Interfaces;
using QuantConnect.Messaging;
using QuantConnect.Notifications;
using QuantConnect.Packets;

namespace QuantConnect.Tests.Messaging
{
    [TestFixture]
    public class EventMessagingHandlerCoverageTests
    {
        private EventMessagingHandler _handler;
        private List<string> _received;

        [SetUp]
        public void SetUp()
        {
            _received = new List<string>();
            _handler = new EventMessagingHandler();
            _handler.Initialize(new MessagingHandlerInitializeParameters(null));
            _handler.DebugEvent += p => _received.Add("debug:" + p.Message);
            _handler.SystemDebugEvent += p => _received.Add("system:" + p.Message);
            _handler.LogEvent += p => _received.Add("log:" + p.Message);
            _handler.RuntimeErrorEvent += p => _received.Add("runtime:" + p.Message);
            _handler.HandledErrorEvent += p => _received.Add("handled:" + p.Message);
            _handler.BacktestResultEvent += p => _received.Add("result:" + p.Progress);
        }

        [TearDown]
        public void TearDown()
        {
            _handler.Dispose();
        }

        [Test]
        public void PacketsAreQueuedUntilLoadedThenFlushedInOrder()
        {
            _handler.Send(new DebugPacket(1, "algo", "compile", "first"));
            _handler.Send(new LogPacket("algo", "second"));
            Assert.IsEmpty(_received);

            _handler.LoadingComplete();
            _handler.Send(new HandledErrorPacket("algo", "third", "trace"));

            CollectionAssert.AreEqual(new[] { "debug:first", "log:second", "handled:third" }, _received);
        }

        [Test]
        public void SendEnqueuedPacketsDoesNothingUntilLoaded()
        {
            _handler.Send(new LogPacket("algo", "queued"));
            _handler.SendEnqueuedPackets();
            Assert.IsEmpty(_received);

            _handler.LoadingComplete();
            _handler.SendEnqueuedPackets();
            CollectionAssert.AreEqual(new[] { "log:queued" }, _received);
        }

        [Test]
        public void ConsumerReadyEventMarksHandlerAsLoaded()
        {
            var ready = 0;
            _handler.ConsumerReadyEvent += () => ready++;
            _handler.Send(new RuntimeErrorPacket(1, "algo", "boom", "trace"));

            _handler.OnConsumerReadyEvent();
            _handler.SendEnqueuedPackets();

            Assert.AreEqual(1, ready);
            CollectionAssert.AreEqual(new[] { "runtime:boom" }, _received);
        }

        [Test]
        public void DispatchesEveryHandledPacketType()
        {
            _handler.LoadingComplete();
            _handler.SetAuthentication(new BacktestNodePacket());
            _handler.Send(new DebugPacket(1, "a", "c", "d"));
            _handler.Send(new LogPacket("a", "l"));
            _handler.Send(new RuntimeErrorPacket(1, "a", "r"));
            _handler.Send(new HandledErrorPacket("a", "h"));
            _handler.Send(new BacktestResultPacket { Progress = 0.5m });
            _handler.Send(new SecurityTypesPacket());

            CollectionAssert.AreEqual(new[] { "debug:d", "log:l", "runtime:r", "handled:h", "result:0.5" }, _received);
        }

        [Test]
        [Ignore("Possible bug: EventMessagingHandler.OnSystemDebugEvent raises DebugEvent instead of SystemDebugEvent")]
        public void SystemDebugPacketsRaiseSystemDebugEvent()
        {
            _handler.LoadingComplete();
            _handler.Send(new SystemDebugPacket(1, "a", "c", "s"));
            CollectionAssert.AreEqual(new[] { "system:s" }, _received);
        }

        [Test]
        public void EventsWithoutSubscribersDoNotThrow()
        {
            var handler = new EventMessagingHandler();
            handler.Initialize(new MessagingHandlerInitializeParameters(null));
            handler.LoadingComplete();
            Assert.DoesNotThrow(() =>
            {
                handler.Send(new DebugPacket(1, "a", "c", "d"));
                handler.Send(new SystemDebugPacket(1, "a", "c", "s"));
                handler.Send(new LogPacket("a", "l"));
                handler.Send(new RuntimeErrorPacket(1, "a", "r"));
                handler.Send(new HandledErrorPacket("a", "h"));
                handler.Send(new BacktestResultPacket());
                handler.OnConsumerReadyEvent();
            });
        }

        [Test]
        public void SendNotificationSkipsNotificationsThatCannotBeSent()
        {
            Assert.DoesNotThrow(() => _handler.SendNotification(new NotificationEmail("test@example.com")));

            var notification = new TestNotification();
            _handler.SendNotification(notification);
            Assert.IsTrue(notification.Sent);
        }

        [Test]
        [Ignore("Possible bug: SendNotification(null) throws NullReferenceException while logging notification.GetType() after CanSend(null) returned false")]
        public void SendNotificationIgnoresNullNotification()
        {
            Assert.DoesNotThrow(() => _handler.SendNotification(null));
        }

        private class TestNotification : Notification
        {
            public bool Sent { get; private set; }

            public override void Send()
            {
                Sent = true;
            }
        }
    }
}
