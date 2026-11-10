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
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.CryptoFuture;
using QuantConnect.Tests.Brokerages;

namespace QuantConnect.Tests.Common.Brokerages
{
    [TestFixture]
    public class BrokerageModelsCoverageTests
    {
        private static readonly DateTime Time = new DateTime(2026, 6, 22, 15, 0, 0);

        private static IBrokerageModel CreateModel(string name)
        {
            return name switch
            {
                "RBI" => new RBIBrokerageModel(),
                "TDAmeritrade" => new TDAmeritradeBrokerageModel(),
                "Wolverine" => new WolverineBrokerageModel(),
                _ => throw new ArgumentException(name)
            };
        }

        [TestCase("RBI", typeof(RBIFeeModel))]
        [TestCase("TDAmeritrade", typeof(TDAmeritradeFeeModel))]
        [TestCase("Wolverine", typeof(WolverineFeeModel))]
        public void EquityModelsAcceptSupportedEquityOrders(string name, Type feeModelType)
        {
            var model = CreateModel(name);
            var security = GetEquity();

            Assert.IsTrue(model.CanSubmitOrder(security, new MarketOrder(security.Symbol, 10, Time), out var message));
            Assert.IsNull(message);
            Assert.IsTrue(model.CanSubmitOrder(security, new LimitOrder(security.Symbol, -10, 100m, Time), out message));
            Assert.IsTrue(model.CanSubmitOrder(security, new StopMarketOrder(security.Symbol, 10, 100m, Time), out message));
            Assert.IsTrue(model.CanSubmitOrder(security, new StopLimitOrder(security.Symbol, 10, 100m, 101m, Time), out message));
            Assert.IsInstanceOf(feeModelType, model.GetFeeModel(security));
        }

        [TestCase("RBI")]
        [TestCase("TDAmeritrade")]
        [TestCase("Wolverine")]
        public void EquityModelsRejectUnsupportedSecurityTypes(string name)
        {
            var model = CreateModel(name);
            var security = TestsHelpers.GetSecurity(securityType: SecurityType.Forex, symbol: "EURUSD", market: Market.Oanda);

            Assert.IsFalse(model.CanSubmitOrder(security, new MarketOrder(security.Symbol, 1000, Time), out var message));
            Assert.AreEqual(BrokerageMessageType.Warning, message.Type);
            Assert.AreEqual("NotSupported", message.Code);
        }

        [TestCase("RBI")]
        [TestCase("TDAmeritrade")]
        [TestCase("Wolverine")]
        public void EquityModelsRejectUnsupportedOrderTypes(string name)
        {
            var model = CreateModel(name);
            var security = GetEquity();

            Assert.IsFalse(model.CanSubmitOrder(security, new TrailingStopOrder(security.Symbol, 10, 100m, 1m, false, Time), out var message));
            Assert.AreEqual("NotSupported", message.Code);
        }

        [TestCase("RBI")]
        [TestCase("TDAmeritrade")]
        [TestCase("Wolverine")]
        public void EquityModelsRejectInvalidOrderSizes(string name)
        {
            var model = CreateModel(name);
            var security = TestsHelpers.GetSecurity(securityType: SecurityType.Crypto, symbol: "BTCUSD", market: Market.Coinbase);
            var quantity = security.SymbolProperties.MinimumOrderSize.Value / 10m;

            Assert.IsFalse(model.CanSubmitOrder(security, new MarketOrder(security.Symbol, quantity, Time), out var message));
            Assert.AreEqual(Messages.DefaultBrokerageModel.InvalidOrderQuantity(security, quantity), message.Message);
        }

        [TestCase("RBI", true)]
        [TestCase("TDAmeritrade", true)]
        [TestCase("Wolverine", false)]
        public void CanUpdateOrder(string name, bool expected)
        {
            var model = CreateModel(name);
            var security = GetEquity();
            var order = new LimitOrder(security.Symbol, 10, 100m, Time);

            var result = model.CanUpdateOrder(security, order, new UpdateOrderRequest(Time, order.Id, new UpdateOrderFields { LimitPrice = 99m }), out var message);

            Assert.AreEqual(expected, result);
            if (expected)
            {
                Assert.IsNull(message);
            }
            else
            {
                Assert.AreEqual(BrokerageMessageType.Warning, message.Type);
            }
        }

        [Test]
        public void WolverineSupportsMarketOnCloseAndOptions()
        {
            var model = new WolverineBrokerageModel();
            var equity = GetEquity();
            var option = TestsHelpers.GetSecurity(securityType: SecurityType.Option, symbol: "AAPL", market: Market.USA);

            Assert.IsTrue(model.CanSubmitOrder(equity, new MarketOnCloseOrder(equity.Symbol, 10, Time), out _));
            Assert.IsTrue(model.CanSubmitOrder(option, new MarketOrder(option.Symbol, 1, Time), out var message), message?.Message);
        }

        [Test]
        public void RBIAndTDAmeritradeRejectMarketOnClose()
        {
            var equity = GetEquity();
            var order = new MarketOnCloseOrder(equity.Symbol, 10, Time);

            Assert.IsFalse(new RBIBrokerageModel().CanSubmitOrder(equity, order, out var message));
            Assert.AreEqual("NotSupported", message.Code);
            Assert.IsFalse(new TDAmeritradeBrokerageModel().CanSubmitOrder(equity, order, out message));
            Assert.AreEqual("NotSupported", message.Code);
        }

        [Test]
        public void DydxOnlySupportsMarginAccounts()
        {
            Assert.Throws<ArgumentException>(() => new dYdXBrokerageModel(AccountType.Cash));
            Assert.DoesNotThrow(() => new dYdXBrokerageModel());
        }

        [Test]
        public void DydxModelsForNonCryptoFutures()
        {
            var model = new dYdXBrokerageModel();
            var equity = GetEquity();

            Assert.AreEqual(Market.DYDX, model.DefaultMarkets[SecurityType.CryptoFuture]);
            Assert.IsFalse(model.CanSubmitOrder(equity, new MarketOrder(equity.Symbol, 1, Time), out var message));
            Assert.AreEqual("NotSupported", message.Code);
            Assert.IsNotInstanceOf<dYdXFeeModel>(model.GetFeeModel(equity));
            Assert.IsNotInstanceOf<dYdXFutureMarginInterestRateModel>(model.GetMarginInterestRateModel(equity));
            Assert.IsNotNull(model.GetBuyingPowerModel(equity));

            var order = new LimitOrder(equity.Symbol, 1, 1m, Time);
            Assert.IsFalse(model.CanUpdateOrder(equity, order, new UpdateOrderRequest(Time, order.Id, new UpdateOrderFields()), out message));
            Assert.AreEqual("NotSupported", message.Code);
        }

        [Test]
        public void DydxModelsForCryptoFutures()
        {
            var model = new dYdXBrokerageModel();
            var security = GetCryptoFuture(Market.DYDX);

            Assert.IsInstanceOf<dYdXFeeModel>(model.GetFeeModel(security));
            Assert.IsInstanceOf<dYdXFutureMarginInterestRateModel>(model.GetMarginInterestRateModel(security));
            Assert.IsInstanceOf<SecurityMarginModel>(model.GetBuyingPowerModel(security));
            Assert.IsTrue(model.CanSubmitOrder(security, new LimitOrder(security.Symbol, 1, 100m, Time), out var message), message?.Message);
            Assert.IsFalse(model.CanSubmitOrder(security, new MarketOnCloseOrder(security.Symbol, 1, Time), out message));
            Assert.AreEqual("NotSupported", message.Code);
        }

        [Test]
        public void BinanceCoinFuturesUsesCoinFeesAndMargin()
        {
            var model = new BinanceCoinFuturesBrokerageModel(AccountType.Margin);
            var security = GetCryptoFuture(Market.Binance);

            Assert.IsInstanceOf<BinanceCoinFuturesFeeModel>(model.GetFeeModel(security));
            Assert.IsInstanceOf<CryptoFutureMarginModel>(model.GetBuyingPowerModel(security));
        }

        private static Security GetEquity()
        {
            return TestsHelpers.GetSecurity(securityType: SecurityType.Equity, symbol: "AAPL", market: Market.USA);
        }

        private static Security GetCryptoFuture(string market)
        {
            return TestsHelpers.GetSecurity(securityType: SecurityType.CryptoFuture, symbol: "BTCUSD", market: market, quoteCurrency: "USD");
        }
    }
}
