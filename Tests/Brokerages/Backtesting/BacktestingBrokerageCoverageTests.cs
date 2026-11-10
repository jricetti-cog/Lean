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
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Backtesting;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Brokerages.Backtesting
{
    [TestFixture]
    public class BacktestingBrokerageCoverageTests
    {
        private AlgorithmStub _algorithm;

        [SetUp]
        public void SetUp()
        {
            _algorithm = new AlgorithmStub();
            _algorithm.SetDateTime(new DateTime(2026, 6, 22, 15, 0, 0));
        }

        [Test]
        public void ConnectionMembersAreNoOps()
        {
            using var brokerage = new BacktestingBrokerage(_algorithm);

            Assert.IsTrue(brokerage.IsConnected);
            Assert.AreEqual("Backtesting Brokerage", brokerage.Name);
            Assert.DoesNotThrow(brokerage.Connect);
            Assert.DoesNotThrow(brokerage.Disconnect);
            Assert.IsTrue(brokerage.IsConnected);
        }

        [Test]
        public void GetAccountHoldingsReturnsOnlyNonZeroPositionsSortedBySymbol()
        {
            using var brokerage = new BacktestingBrokerage(_algorithm);
            _algorithm.AddEquity("SPY").Holdings.SetHoldings(100m, 10);
            _algorithm.AddEquity("AAPL").Holdings.SetHoldings(50m, -3);
            _algorithm.AddEquity("IBM");

            var holdings = brokerage.GetAccountHoldings();

            CollectionAssert.AreEqual(new[] { Symbols.AAPL, Symbols.SPY }, holdings.Select(x => x.Symbol));
            Assert.AreEqual(-3, holdings[0].Quantity);
            Assert.AreEqual(50m, holdings[0].AveragePrice);
            Assert.AreEqual(10, holdings[1].Quantity);
            Assert.AreEqual(100m, holdings[1].AveragePrice);
        }

        [Test]
        public void GetCashBalanceReflectsTheCashBook()
        {
            using var brokerage = new BacktestingBrokerage(_algorithm);
            _algorithm.SetCash(2500m);
            _algorithm.SetCash("EUR", 100m, 1.1m);

            var cash = brokerage.GetCashBalance();

            CollectionAssert.AreEquivalent(new[] { new CashAmount(2500m, "USD"), new CashAmount(100m, "EUR") }, cash);
        }

        [Test]
        public void UpdateOfUnknownOrderFails()
        {
            using var brokerage = new BacktestingBrokerage(_algorithm);
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, e) => events.AddRange(e);

            var result = brokerage.UpdateOrder(new LimitOrder(Symbols.SPY, 1, 100m, _algorithm.UtcTime) { Id = 42 });

            Assert.IsFalse(result);
            CollectionAssert.IsEmpty(events);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancelOfUnknownOrderFailsButNotifiesCancellation(bool liveMode)
        {
            _algorithm.SetLiveMode(liveMode);
            using var brokerage = new BacktestingBrokerage(_algorithm);
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, e) => events.AddRange(e);

            var result = brokerage.CancelOrder(new LimitOrder(Symbols.SPY, 1, 100m, _algorithm.UtcTime) { Id = 42 });

            Assert.IsFalse(result);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(42, events[0].OrderId);
            Assert.AreEqual(OrderStatus.Canceled, events[0].Status);
        }

        [Test]
        public void DelistingWarningsAreIgnored()
        {
            using var brokerage = new BacktestingBrokerage(_algorithm);
            _algorithm.AddEquity("SPY");
            var delistingNotifications = 0;
            var optionNotifications = 0;
            brokerage.DelistingNotification += (_, _) => delistingNotifications++;
            brokerage.OptionNotification += (_, _) => optionNotifications++;
            var delistings = new Delistings
            {
                { Symbols.SPY, new Delisting(Symbols.SPY, _algorithm.UtcTime, 100m, DelistingType.Warning) }
            };

            brokerage.ProcessDelistings(delistings);

            Assert.AreEqual(0, delistingNotifications);
            Assert.AreEqual(0, optionNotifications);
            Assert.IsTrue(_algorithm.Securities.ContainsKey(Symbols.SPY));
        }

        [Test]
        public void ScanWithoutPendingOrdersDoesNothing()
        {
            using var brokerage = new BacktestingBrokerage(_algorithm);
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, e) => events.AddRange(e);

            brokerage.Scan();

            CollectionAssert.IsEmpty(events);
        }
    }
}
