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

using System.Linq;
using Moq;
using NUnit.Framework;
using QuantConnect.Commands;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Common.Commands
{
    [TestFixture]
    public class CommandsCoverageTests
    {
        [Test]
        public void AddSecurityCommandDefaultsAndRun()
        {
            var command = new AddSecurityCommand { SecurityType = SecurityType.Equity, Symbol = "AAPL" };
            Assert.AreEqual(Resolution.Minute, command.Resolution);
            Assert.IsNull(command.Market);
            Assert.IsTrue(command.FillDataForward);
            Assert.AreEqual(Security.NullLeverage, command.Leverage);
            Assert.IsFalse(command.ExtendedMarketHours);

            var algorithm = new AlgorithmStub();
            var result = (AddSecurityCommand.Result)command.Run(algorithm);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(Symbols.AAPL, result.Symbol);
            Assert.IsTrue(algorithm.Securities.ContainsKey(Symbols.AAPL));
        }

        [TestCase(AlgorithmStatus.Liquidated)]
        [TestCase(AlgorithmStatus.Deleted)]
        public void AlgorithmStatusCommandSetsStatus(AlgorithmStatus status)
        {
            Assert.AreEqual(AlgorithmStatus.Running, new AlgorithmStatusCommand().Status);
            var algorithm = new AlgorithmStub();
            var result = new AlgorithmStatusCommand(status).Run(algorithm);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(status, algorithm.Status);
        }

        [Test]
        public void QuitCommandStopsAlgorithm()
        {
            var command = new QuitCommand();
            Assert.AreEqual(AlgorithmStatus.Stopped, command.Status);
            var algorithm = new AlgorithmStub();
            Assert.IsTrue(command.Run(algorithm).Success);
            Assert.AreEqual(AlgorithmStatus.Stopped, algorithm.Status);
        }

        private static AlgorithmStub CreateAlgorithmWithOpenOrder(out OrderTicket ticket)
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetFinishedWarmingUp();
            var security = algorithm.AddEquity("SPY");
            security.SetMarketPrice(new Tick { Value = 100 });
            ticket = algorithm.LimitOrder(Symbols.SPY, 10, 90m);
            return algorithm;
        }

        [Test]
        public void CancelOrderCommandCancelsExistingOrder()
        {
            var algorithm = CreateAlgorithmWithOpenOrder(out var ticket);
            var result = (CancelOrderCommand.Result)new CancelOrderCommand { OrderId = ticket.OrderId }.Run(algorithm);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(0m, result.QuantityFilled);
            Assert.IsNotNull(ticket.CancelRequest);
        }

        private static AlgorithmStub CreateAlgorithmRejectingUnknownOrders()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetFinishedWarmingUp();
            var orderProcessor = new Mock<IOrderProcessor>();
            orderProcessor.Setup(p => p.Process(It.IsAny<OrderRequest>()))
                .Returns<OrderRequest>(request => request is CancelOrderRequest cancel
                    ? OrderTicket.InvalidCancelOrderId(algorithm.Transactions, cancel)
                    : OrderTicket.InvalidUpdateOrderId(algorithm.Transactions, (UpdateOrderRequest)request));
            algorithm.Transactions.SetOrderProcessor(orderProcessor.Object);
            return algorithm;
        }

        [Test]
        public void CancelOrderCommandFailsForUnknownOrder()
        {
            var algorithm = CreateAlgorithmRejectingUnknownOrders();
            var result = (CancelOrderCommand.Result)new CancelOrderCommand { OrderId = 12345 }.Run(algorithm);
            Assert.IsFalse(result.Success);
            Assert.AreEqual(0m, result.QuantityFilled);
        }

        [Test]
        public void UpdateOrderCommandFailsForUnknownOrder()
        {
            var algorithm = CreateAlgorithmRejectingUnknownOrders();
            var result = new UpdateOrderCommand { OrderId = 12345, Quantity = 5, LimitPrice = 1m, StopPrice = 2m, Tag = "tag" }.Run(algorithm);
            Assert.IsFalse(result.Success);
        }

        private static AlgorithmStub CreateAlgorithmWithHoldings()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetFinishedWarmingUp();
            foreach (var ticker in new[] { "SPY", "AAPL" })
            {
                var security = algorithm.AddEquity(ticker);
                security.SetMarketPrice(new Tick { Value = 100 });
                security.Holdings.SetHoldings(100m, 10);
            }
            return algorithm;
        }

        [Test]
        public void LiquidateCommandLiquidatesAllWhenNoSymbolGiven()
        {
            var algorithm = CreateAlgorithmWithHoldings();
            var result = new LiquidateCommand().Run(algorithm);
            Assert.IsTrue(result.Success);
            var tickets = algorithm.Transactions.GetOrderTickets().ToList();
            CollectionAssert.AreEquivalent(new[] { Symbols.SPY, Symbols.AAPL }, tickets.Select(t => t.Symbol));
            Assert.IsTrue(tickets.All(t => t.Quantity == -10));
        }

        [Test]
        public void LiquidateCommandLiquidatesSpecificSymbol()
        {
            var algorithm = CreateAlgorithmWithHoldings();
            var result = new LiquidateCommand { Ticker = "SPY", SecurityType = SecurityType.Equity, Market = Market.USA }.Run(algorithm);
            Assert.IsTrue(result.Success);
            var ticket = algorithm.Transactions.GetOrderTickets().Single();
            Assert.AreEqual(Symbols.SPY, ticket.Symbol);
            Assert.AreEqual(-10m, ticket.Quantity);
        }
    }
}
