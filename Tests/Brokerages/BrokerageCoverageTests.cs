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
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.CrossZero;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Brokerages
{
    [TestFixture]
    public class BrokerageCoverageTests
    {
        [Test]
        public void EventInvocatorsRaiseTheirEvents()
        {
            using var brokerage = new TestBrokerage();
            var orderEvents = new List<OrderEvent>();
            var updates = new List<OrderUpdateEvent>();
            var idChanges = new List<BrokerageOrderIdChangedEvent>();
            var assignments = new List<OrderEvent>();
            var optionNotifications = new List<OptionNotificationEventArgs>();
            var newOrders = new List<NewBrokerageOrderNotificationEventArgs>();
            var delistings = new List<DelistingNotificationEventArgs>();
            var accountChanges = new List<AccountEvent>();
            brokerage.OrdersStatusChanged += (sender, e) => { Assert.AreSame(brokerage, sender); orderEvents.AddRange(e); };
            brokerage.OrderUpdated += (_, e) => updates.Add(e);
            brokerage.OrderIdChanged += (_, e) => idChanges.Add(e);
            brokerage.OptionPositionAssigned += (_, e) => assignments.Add(e);
            brokerage.OptionNotification += (_, e) => optionNotifications.Add(e);
            brokerage.NewBrokerageOrderNotification += (_, e) => newOrders.Add(e);
            brokerage.DelistingNotification += (_, e) => delistings.Add(e);
            brokerage.AccountChanged += (_, e) => accountChanges.Add(e);

            var orderEvent = new OrderEvent(1, Symbols.SPY, DateTime.UtcNow, OrderStatus.Submitted, OrderDirection.Buy, 0, 0, OrderFee.Zero);
            brokerage.RaiseOrderEvent(orderEvent);
            brokerage.RaiseOrderUpdated(new OrderUpdateEvent { OrderId = 1, TrailingStopPrice = 10m });
            brokerage.RaiseOrderIdChanged(new BrokerageOrderIdChangedEvent { OrderId = 1, BrokerId = new List<string> { "abc" } });
            brokerage.RaiseOptionPositionAssigned(orderEvent);
            brokerage.RaiseOptionNotification(new OptionNotificationEventArgs(Symbols.SPY_C_192_Feb19_2016, 5));
            brokerage.RaiseNewBrokerageOrderNotification(new NewBrokerageOrderNotificationEventArgs(new MarketOrder(Symbols.SPY, 1, DateTime.UtcNow)));
            brokerage.RaiseDelistingNotification(new DelistingNotificationEventArgs(Symbols.AAPL));
            brokerage.RaiseAccountChanged(new AccountEvent("USD", 100m));

            Assert.AreSame(orderEvent, orderEvents.Single());
            Assert.AreEqual(10m, updates.Single().TrailingStopPrice);
            CollectionAssert.AreEqual(new[] { "abc" }, idChanges.Single().BrokerId);
            Assert.AreSame(orderEvent, assignments.Single());
            Assert.AreEqual(5, optionNotifications.Single().Position);
            Assert.AreEqual(Symbols.SPY, newOrders.Single().Order.Symbol);
            Assert.AreEqual(Symbols.AAPL, delistings.Single().Symbol);
            Assert.AreEqual(100m, accountChanges.Single().CashBalance);
        }

        [Test]
        public void ExceptionsThrownByEventHandlersAreSwallowed()
        {
            using var brokerage = new TestBrokerage();
            brokerage.OrdersStatusChanged += (_, _) => throw new InvalidOperationException();
            brokerage.OrderUpdated += (_, _) => throw new InvalidOperationException();
            brokerage.OrderIdChanged += (_, _) => throw new InvalidOperationException();
            brokerage.OptionPositionAssigned += (_, _) => throw new InvalidOperationException();
            brokerage.OptionNotification += (_, _) => throw new InvalidOperationException();
            brokerage.NewBrokerageOrderNotification += (_, _) => throw new InvalidOperationException();
            brokerage.DelistingNotification += (_, _) => throw new InvalidOperationException();
            brokerage.AccountChanged += (_, _) => throw new InvalidOperationException();
            brokerage.Message += (_, _) => throw new InvalidOperationException();

            var orderEvent = new OrderEvent(1, Symbols.SPY, DateTime.UtcNow, OrderStatus.Filled, OrderDirection.Buy, 1, 1, OrderFee.Zero);
            Assert.DoesNotThrow(() =>
            {
                brokerage.RaiseOrderEvent(orderEvent);
                brokerage.RaiseOrderUpdated(new OrderUpdateEvent { OrderId = 1 });
                brokerage.RaiseOrderIdChanged(new BrokerageOrderIdChangedEvent { OrderId = 1 });
                brokerage.RaiseOptionPositionAssigned(orderEvent);
                brokerage.RaiseOptionNotification(new OptionNotificationEventArgs(Symbols.SPY_C_192_Feb19_2016, 0));
                brokerage.RaiseNewBrokerageOrderNotification(new NewBrokerageOrderNotificationEventArgs(new MarketOrder(Symbols.SPY, 1, DateTime.UtcNow)));
                brokerage.RaiseDelistingNotification(new DelistingNotificationEventArgs(Symbols.AAPL));
                brokerage.RaiseAccountChanged(new AccountEvent("USD", 1m));
                brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "code", "message"));
            });
        }

        [Test]
        public void OnlyConnectionStateChangesAreForwarded()
        {
            using var brokerage = new TestBrokerage();
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, e) => messages.Add(e);

            // the brokerage starts connected: a reconnect is not a state change
            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, "r1", "reconnect"));
            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "d1", "disconnect"));
            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "d2", "disconnect again"));
            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "e1", "error"));
            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, "r2", "reconnect"));
            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, "r3", "reconnect again"));

            CollectionAssert.AreEqual(new[] { "d1", "e1", "r2" }, messages.Select(x => x.Code));
        }

        [Test]
        public void AuthenticationCallbacksChangeTheConnectionState()
        {
            using var brokerage = new TestBrokerage();
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, e) => messages.Add(e);

            brokerage.RaiseAuthenticationFailed(new InvalidOperationException("bad token"));
            brokerage.RaiseAuthenticationFailed(new InvalidOperationException("bad token again"));
            brokerage.RaiseAuthenticationSucceeded();

            Assert.AreEqual(2, messages.Count);
            Assert.AreEqual(BrokerageMessageType.Disconnect, messages[0].Type);
            Assert.AreEqual("OAuthenticationFailed", messages[0].Code);
            Assert.AreEqual("bad token", messages[0].Message);
            Assert.AreEqual(BrokerageMessageType.Reconnect, messages[1].Type);
            Assert.AreEqual("OAuthenticationSucceeded", messages[1].Code);
        }

        [Test]
        public void ShouldPerformCashSyncDependsOnTimeOfDayAndConnectionState()
        {
            using var brokerage = new TestBrokerage();
            // 7:00 and 8:00 New York time
            var beforeSyncTime = new DateTime(2026, 6, 22, 11, 0, 0);
            var afterSyncTime = new DateTime(2026, 6, 22, 12, 0, 0);

            Assert.IsFalse(brokerage.ShouldPerformCashSync(beforeSyncTime));
            Assert.IsTrue(brokerage.ShouldPerformCashSync(afterSyncTime));

            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "d", "disconnected"));
            Assert.IsFalse(brokerage.ShouldPerformCashSync(afterSyncTime));

            brokerage.RaiseMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, "r", "reconnected"));
            Assert.IsTrue(brokerage.ShouldPerformCashSync(afterSyncTime));
        }

        [Test]
        public void GetAccountHoldingsUsesBrokerageDataOnlyOnce()
        {
            using var brokerage = new TestBrokerage();
            var holdings = new List<Holding> { new Holding { Symbol = Symbols.SPY, Quantity = 10, AveragePrice = 100m, CurrencySymbol = "$" } };
            var brokerageData = new Dictionary<string, string> { { "live-holdings", JsonConvert.SerializeObject(holdings) } };

            var result = brokerage.GetHoldings(brokerageData, null);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(10, result[0].Quantity);
            Assert.AreEqual(100m, result[0].AveragePrice);
            Assert.IsFalse(brokerageData.ContainsKey("live-holdings"));

            // the key was consumed, without securities there are no holdings
            CollectionAssert.IsEmpty(brokerage.GetHoldings(brokerageData, null));
        }

        [Test]
        public void GetAccountHoldingsHandlesNullJsonAndFallsBackToSecurities()
        {
            using var brokerage = new TestBrokerage();

            CollectionAssert.IsEmpty(brokerage.GetHoldings(new Dictionary<string, string> { { "live-holdings", "null" } }, null));

            var algorithm = new AlgorithmStub();
            var spy = algorithm.AddEquity("SPY").Holdings;
            spy.SetHoldings(100m, 5);
            algorithm.AddEquity("AAPL");

            var result = brokerage.GetHoldings(new Dictionary<string, string>(), algorithm.Securities.Values);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(Symbols.SPY, result[0].Symbol);
            Assert.AreEqual(5, result[0].Quantity);
            Assert.AreEqual(100m, result[0].AveragePrice);
        }

        [Test]
        public void GetCashBalanceUsesBrokerageDataOnlyOnce()
        {
            using var brokerage = new TestBrokerage();
            var cash = new List<CashAmount> { new CashAmount(123m, "EUR") };
            var brokerageData = new Dictionary<string, string> { { "live-cash-balance", JsonConvert.SerializeObject(cash) } };

            var result = brokerage.GetCash(brokerageData, null);

            Assert.AreEqual(new CashAmount(123m, "EUR"), result.Single());
            Assert.IsFalse(brokerageData.ContainsKey("live-cash-balance"));
            CollectionAssert.IsEmpty(brokerage.GetCash(brokerageData, null));
            CollectionAssert.IsEmpty(brokerage.GetCash(new Dictionary<string, string> { { "live-cash-balance", "null" } }, null));
        }

        [Test]
        public void GetCashBalanceFallsBackToTheCashBook()
        {
            using var brokerage = new TestBrokerage();
            var cashBook = new CashBook();
            cashBook.Add("EUR", 50m, 1.1m);
            cashBook["USD"].SetAmount(1000m);

            var result = brokerage.GetCash(null, cashBook);

            CollectionAssert.AreEquivalent(new[] { new CashAmount(1000m, "USD"), new CashAmount(50m, "EUR") }, result);
        }

        [Test]
        public void DefaultMembers()
        {
            using var brokerage = new TestBrokerage();

            Assert.AreEqual("TestBrokerage", brokerage.Name);
            Assert.IsFalse(brokerage.AccountInstantlyUpdated);
            Assert.IsFalse(brokerage.ConcurrencyEnabled);
            Assert.IsNull(brokerage.AccountBaseCurrency);
            CollectionAssert.IsEmpty(brokerage.GetHistory(null));
            Assert.AreEqual(OrderPosition.BuyToOpen, brokerage.OrderPosition(OrderDirection.Buy, 0));
            Assert.AreEqual(OrderPosition.SellToClose, brokerage.OrderPosition(OrderDirection.Sell, 10));
            Assert.AreEqual(OrderPosition.BuyToClose, brokerage.OrderPosition(OrderDirection.Buy, -10));
            Assert.AreEqual(OrderPosition.SellToOpen, brokerage.OrderPosition(OrderDirection.Sell, 0));
        }

        [Test]
        public void CrossZeroHelpersValidateArgumentsAndHandleNonCrossingOrders()
        {
            using var brokerage = new TestBrokerage();
            var order = new MarketOrder(Symbols.SPY, 10, DateTime.UtcNow);

            Assert.Throws<ArgumentNullException>(() => brokerage.TryCrossZero(null, 0));
            Assert.Throws<ArgumentNullException>(() => brokerage.TryGetUpdateQuantity(null, out _));
            Assert.Throws<NotImplementedException>(() => brokerage.PlaceCrossZero(null));

            // a buy order on a long position doesn't cross zero
            Assert.IsNull(brokerage.TryCrossZero(order, 5));
            Assert.IsTrue(brokerage.TryGetUpdateQuantity(order, out var quantity));
            Assert.AreEqual(10, quantity);
        }

        private class TestBrokerage : Brokerage
        {
            public TestBrokerage() : base("TestBrokerage")
            {
            }

            public void RaiseOrderEvent(OrderEvent e) => OnOrderEvent(e);
            public void RaiseOrderUpdated(OrderUpdateEvent e) => OnOrderUpdated(e);
            public void RaiseOrderIdChanged(BrokerageOrderIdChangedEvent e) => OnOrderIdChangedEvent(e);
            public void RaiseOptionPositionAssigned(OrderEvent e) => OnOptionPositionAssigned(e);
            public void RaiseOptionNotification(OptionNotificationEventArgs e) => OnOptionNotification(e);
            public void RaiseNewBrokerageOrderNotification(NewBrokerageOrderNotificationEventArgs e) => OnNewBrokerageOrderNotification(e);
            public void RaiseDelistingNotification(DelistingNotificationEventArgs e) => OnDelistingNotification(e);
            public void RaiseAccountChanged(AccountEvent e) => OnAccountChanged(e);
            public void RaiseMessage(BrokerageMessageEvent e) => OnMessage(e);
            public void RaiseAuthenticationFailed(Exception e) => OnAuthenticationFailed(e);
            public void RaiseAuthenticationSucceeded() => OnAuthenticationSucceeded();
            public List<Holding> GetHoldings(Dictionary<string, string> data, IEnumerable<Security> securities) => GetAccountHoldings(data, securities);
            public List<CashAmount> GetCash(Dictionary<string, string> data, CashBook cashBook) => GetCashBalance(data, cashBook);
            public OrderPosition OrderPosition(OrderDirection direction, decimal holdings) => GetOrderPosition(direction, holdings);
            public bool? TryCrossZero(Order order, decimal holdings) => TryCrossZeroPositionOrder(order, holdings);
            public bool TryGetUpdateQuantity(Order order, out decimal quantity) => TryGetUpdateCrossZeroOrderQuantity(order, out quantity);
            public CrossZeroOrderResponse PlaceCrossZero(CrossZeroFirstOrderRequest request) => PlaceCrossZeroOrder(request);

            public override bool IsConnected => true;
            public override bool PlaceOrder(Order order) => false;
            public override bool UpdateOrder(Order order) => false;
            public override bool CancelOrder(Order order) => false;
            public override void Connect() { }
            public override void Disconnect() { }
            public override List<Order> GetOpenOrders() => new();
            public override List<Holding> GetAccountHoldings() => new();
            public override List<CashAmount> GetCashBalance() => new();
        }
    }
}
