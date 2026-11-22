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

using NUnit.Framework;
using QuantConnect.Services.BacktestWorker;

namespace QuantConnect.Tests.Services.BacktestWorker
{
    [TestFixture]
    public class JobCancellationTests
    {
        [Test]
        public void UntargetedCancelStopsTheRunAtOnce()
        {
            using var cancellation = new JobCancellation();
            Assert.IsTrue(cancellation.Request("user"));
            Assert.IsTrue(cancellation.IsRequested);
            Assert.AreEqual("user", cancellation.Reason);
        }

        [Test]
        public void CancelOfTheSupersededAttemptArrivingDuringTheClaimIsIgnored()
        {
            using var cancellation = new JobCancellation();
            Assert.IsFalse(cancellation.Request("superseded by a new attempt", targetAttempt: 1));
            Assert.IsFalse(cancellation.IsRequested);

            cancellation.Attempt = 2;
            Assert.IsFalse(cancellation.IsRequested);
            Assert.IsNull(cancellation.Reason);
        }

        [Test]
        public void CancelOfTheGrantedAttemptArrivingDuringTheClaimAppliesOnceGranted()
        {
            using var cancellation = new JobCancellation();
            Assert.IsFalse(cancellation.Request("user", targetAttempt: 2));
            cancellation.Attempt = 2;
            Assert.IsTrue(cancellation.IsRequested);
            Assert.AreEqual("user", cancellation.Reason);
        }

        [Test]
        public void CancelOfAnOlderAttemptIsIgnoredAfterTheGrant()
        {
            using var cancellation = new JobCancellation { Attempt = 3 };
            Assert.IsFalse(cancellation.Request("old", targetAttempt: 2));
            Assert.IsFalse(cancellation.IsRequested);
            Assert.IsTrue(cancellation.Request("current", targetAttempt: 3));
            Assert.IsTrue(cancellation.IsRequested);
        }
    }
}
