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
using System.Threading;
using NUnit.Framework;
using QuantConnect.Brokerages;

namespace QuantConnect.Tests.Brokerages
{
    [TestFixture]
    public class DefaultConnectionHandlerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Test]
        public void DefaultsAndInitialization()
        {
            using var handler = new DefaultConnectionHandler();

            Assert.AreEqual(TimeSpan.FromSeconds(5), handler.MaximumIdleTimeSpan);
            Assert.AreEqual(1, handler.MinimumSecondsForNextReconnectionAttempt);
            Assert.AreEqual(60, handler.MaximumSecondsForNextReconnectionAttempt);
            Assert.IsNull(handler.ConnectionId);

            handler.Initialize("connection-1");

            Assert.AreEqual("connection-1", handler.ConnectionId);
            Assert.IsFalse(handler.IsConnectionLost);
        }

        [Test]
        public void RaisesConnectionLostThenReconnectRequestedThenRestored()
        {
            using var lost = new ManualResetEventSlim();
            using var reconnect = new ManualResetEventSlim();
            using var restored = new ManualResetEventSlim();
            using var handler = new DefaultConnectionHandler
            {
                MaximumIdleTimeSpan = TimeSpan.FromMilliseconds(1),
                MinimumSecondsForNextReconnectionAttempt = 0,
                MaximumSecondsForNextReconnectionAttempt = 0
            };
            handler.ConnectionLost += (sender, _) => { Assert.AreSame(handler, sender); lost.Set(); };
            handler.ReconnectRequested += (_, _) => reconnect.Set();
            handler.ConnectionRestored += (_, _) => restored.Set();

            handler.Initialize("id");
            handler.EnableMonitoring(true);
            // no data has been received for a long time
            handler.KeepAlive(DateTime.UtcNow.AddHours(-1));

            Assert.IsTrue(lost.Wait(Timeout), "ConnectionLost was not raised");
            Assert.IsTrue(handler.IsConnectionLost);
            Assert.IsTrue(reconnect.Wait(Timeout), "ReconnectRequested was not raised");
            Assert.IsFalse(restored.IsSet);

            // data is flowing again
            handler.MaximumIdleTimeSpan = TimeSpan.FromHours(1);
            handler.KeepAlive(DateTime.UtcNow);

            Assert.IsTrue(restored.Wait(Timeout), "ConnectionRestored was not raised");
            Assert.IsFalse(handler.IsConnectionLost);
        }

        [Test]
        public void DisabledMonitoringNeverReportsConnectionLost()
        {
            using var handler = new TestConnectionHandler { MaximumIdleTimeSpan = TimeSpan.FromMilliseconds(1) };
            handler.Initialize("id");
            handler.KeepAlive(DateTime.UtcNow.AddHours(-1));

            // the monitor ticks roughly every second, give it a few chances to (not) fire
            Assert.IsFalse(handler.LostCalled.Wait(TimeSpan.FromSeconds(1.5)));
            Assert.IsFalse(handler.IsConnectionLost);
        }

        [Test]
        public void EnablingMonitoringResetsTheLastDataReceivedTime()
        {
            using var handler = new TestConnectionHandler { MaximumIdleTimeSpan = TimeSpan.FromMinutes(10) };
            handler.Initialize("id");
            handler.KeepAlive(DateTime.UtcNow.AddHours(-1));

            // switching from disabled to enabled sets the last data time to now, so the connection is not lost
            handler.EnableMonitoring(true);

            Assert.IsFalse(handler.LostCalled.Wait(TimeSpan.FromSeconds(1.5)));
            Assert.IsFalse(handler.IsConnectionLost);

            handler.EnableMonitoring(false);
        }

        [Test]
        public void DisposeStopsTheMonitor()
        {
            var handler = new TestConnectionHandler { MaximumIdleTimeSpan = TimeSpan.FromMilliseconds(1) };
            handler.Initialize("id");
            handler.Dispose();

            handler.EnableMonitoring(true);
            handler.KeepAlive(DateTime.UtcNow.AddHours(-1));

            Assert.IsFalse(handler.LostCalled.Wait(TimeSpan.FromSeconds(1.5)));
        }

        [Test]
        public void ExceptionsInEventHandlersAreCaughtByTheMonitor()
        {
            using var secondLost = new ManualResetEventSlim();
            var lostCount = 0;
            using var handler = new DefaultConnectionHandler
            {
                MaximumIdleTimeSpan = TimeSpan.FromMilliseconds(1),
                MinimumSecondsForNextReconnectionAttempt = 0,
                MaximumSecondsForNextReconnectionAttempt = 0
            };
            handler.ConnectionLost += (_, _) =>
            {
                if (Interlocked.Increment(ref lostCount) >= 2)
                {
                    secondLost.Set();
                }
                throw new InvalidOperationException("boom");
            };
            handler.ConnectionRestored += (_, _) => throw new InvalidOperationException("boom");

            handler.Initialize("id");
            handler.EnableMonitoring(true);
            handler.KeepAlive(DateTime.UtcNow.AddHours(-1));

            // the first lost notification throws, the monitor survives, the connection gets restored and lost again
            SpinWait.SpinUntil(() => handler.IsConnectionLost, Timeout);
            Assert.IsTrue(handler.IsConnectionLost);
            handler.KeepAlive(DateTime.UtcNow.AddHours(1));
            SpinWait.SpinUntil(() => !handler.IsConnectionLost, Timeout);
            Assert.IsFalse(handler.IsConnectionLost);
            handler.KeepAlive(DateTime.UtcNow.AddHours(-1));

            Assert.IsTrue(secondLost.Wait(Timeout));
        }

        private class TestConnectionHandler : DefaultConnectionHandler
        {
            public ManualResetEventSlim LostCalled { get; } = new();

            protected override void OnConnectionLost()
            {
                LostCalled.Set();
                base.OnConnectionLost();
            }
        }
    }
}
