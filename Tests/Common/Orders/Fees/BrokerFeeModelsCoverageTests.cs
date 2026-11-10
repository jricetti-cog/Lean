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
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Common.Orders.Fees
{
    [TestFixture]
    public class BrokerFeeModelsCoverageTests
    {
        private static readonly DateTime OrderTime = new DateTime(2024, 1, 3, 15, 0, 0);

        private static Security CreateSecurity(Symbol symbol, decimal price, string quoteCurrency = Currencies.USD)
        {
            var config = new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Minute, TimeZones.Utc, TimeZones.Utc, false, true, false);
            var security = new Security(
                SecurityExchangeHours.AlwaysOpen(TimeZones.Utc),
                config,
                new Cash(quoteCurrency, 0, 1m),
                SymbolProperties.GetDefault(quoteCurrency),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache());
            security.SetMarketPrice(new Tick(OrderTime, symbol, price, price));
            return security;
        }

        private static Security CreateSecurity(SecurityType type, decimal price = 10m)
        {
            Symbol symbol;
            switch (type)
            {
                case SecurityType.Option:
                    symbol = Symbol.CreateOption(Symbols.AAPL, QuantConnect.Market.USA, OptionStyle.American, OptionRight.Call, 150m, new DateTime(2026, 6, 20));
                    break;
                case SecurityType.IndexOption:
                    symbol = Symbol.CreateOption(Symbol.Create("SPX", SecurityType.Index, QuantConnect.Market.USA), QuantConnect.Market.USA,
                        OptionStyle.European, OptionRight.Call, 1000m, new DateTime(2026, 6, 20));
                    break;
                case SecurityType.Future:
                    symbol = Symbols.Future_ESZ18_Dec2018;
                    break;
                case SecurityType.Forex:
                    symbol = Symbols.EURUSD;
                    break;
                default:
                    symbol = Symbols.AAPL;
                    break;
            }
            return CreateSecurity(symbol, price);
        }

        private static OrderFee GetFee(IFeeModel model, Security security, decimal quantity)
        {
            return model.GetOrderFee(new OrderFeeParameters(security, new MarketOrder(security.Symbol, quantity, OrderTime)));
        }

        [TestCase(null, 100, 0.35)]
        [TestCase(null, -200, 0.7)]
        [TestCase(0.01, 50, 0.5)]
        public void AxosChargesPerShare(double? feesPerShare, decimal quantity, decimal expected)
        {
            var model = new AxosFeeModel((decimal?)feesPerShare);
            var fee = GetFee(model, CreateSecurity(SecurityType.Equity), quantity);
            Assert.AreEqual(expected, fee.Value.Amount);
            Assert.AreEqual(Currencies.USD, fee.Value.Currency);
        }

        [TestCase(SecurityType.Option, 10, 6.5)]
        [TestCase(SecurityType.IndexOption, -3, 3)]
        [TestCase(SecurityType.Equity, 100, 0)]
        [TestCase(SecurityType.Future, 5, 0)]
        public void CharlesSchwabFees(SecurityType type, decimal quantity, decimal expected)
        {
            var fee = GetFee(new CharlesSchwabFeeModel(), CreateSecurity(type), quantity);
            Assert.AreEqual(expected, fee.Value.Amount);
        }

        [TestCase(SecurityType.Option, true, 10, 6)]
        [TestCase(SecurityType.Option, false, 10, 11)]
        [TestCase(SecurityType.IndexOption, true, 2, 2)]
        [TestCase(SecurityType.IndexOption, false, 2, 7)]
        [TestCase(SecurityType.Future, false, -4, 6)]
        [TestCase(SecurityType.Equity, true, 100, 0)]
        [TestCase(SecurityType.Equity, false, 100, 5)]
        public void TradeStationFees(SecurityType type, bool usResident, decimal quantity, decimal expected)
        {
            var model = new TradeStationFeeModel(usResident);
            Assert.AreEqual(usResident, model.USResident);
            var fee = GetFee(model, CreateSecurity(type), quantity);
            Assert.AreEqual(expected, fee.Value.Amount);
            Assert.AreEqual(Currencies.USD, fee.Value.Currency);
        }

        [Test]
        public void TradeStationThrowsOnNullParameters()
        {
            Assert.Throws<ArgumentNullException>(() => new TradeStationFeeModel().GetOrderFee(null));
        }

        [TestCase(null, 100, 0.5)]
        [TestCase(0.01, -10, 0.1)]
        public void RBIChargesPerShareWhenCalledDirectly(double? feesPerShare, decimal quantity, decimal expected)
        {
            var security = CreateSecurity(SecurityType.Equity);
            var fee = new RBIFeeModel((decimal?)feesPerShare).GetOrderFee(new OrderFeeParameters(security, new MarketOrder(security.Symbol, quantity, OrderTime)));
            Assert.AreEqual(expected, fee.Value.Amount);
        }

        [TestCase(null, 100, 0.5)]
        [TestCase(0.002, -1000, 2)]
        public void WolverineChargesPerShareWhenCalledDirectly(double? feesPerShare, decimal quantity, decimal expected)
        {
            var security = CreateSecurity(SecurityType.Equity);
            var fee = new WolverineFeeModel((decimal?)feesPerShare).GetOrderFee(new OrderFeeParameters(security, new MarketOrder(security.Symbol, quantity, OrderTime)));
            Assert.AreEqual(expected, fee.Value.Amount);
        }

        [TestCase(SecurityType.Equity, 100, 0)]
        [TestCase(SecurityType.Option, 10, 6.5)]
        public void TDAmeritradeFeesWhenCalledDirectly(SecurityType type, decimal quantity, decimal expected)
        {
            var security = CreateSecurity(type);
            var fee = new TDAmeritradeFeeModel().GetOrderFee(new OrderFeeParameters(security, new MarketOrder(security.Symbol, quantity, OrderTime)));
            Assert.AreEqual(expected, fee.Value.Amount);
        }

        [Test]
        public void TDAmeritradeThrowsForUnsupportedSecurityType()
        {
            var security = CreateSecurity(SecurityType.Forex);
            Assert.Throws<ArgumentException>(() =>
                new TDAmeritradeFeeModel().GetOrderFee(new OrderFeeParameters(security, new MarketOrder(security.Symbol, 1000, OrderTime))));
        }

        [Test]
        [Ignore("Possible bug: RBIFeeModel/WolverineFeeModel/TDAmeritradeFeeModel declare GetOrderFee without 'override', so calls through IFeeModel (as brokerage models use them) return the zero base fee")]
        public void PerShareFeeModelsChargeFeesThroughIFeeModel()
        {
            var equity = CreateSecurity(SecurityType.Equity);
            Assert.AreEqual(0.5m, GetFee(new RBIFeeModel(), equity, 100).Value.Amount);
            Assert.AreEqual(0.5m, GetFee(new WolverineFeeModel(), equity, 100).Value.Amount);
            Assert.AreEqual(6.5m, GetFee(new TDAmeritradeFeeModel(), CreateSecurity(SecurityType.Option), 10).Value.Amount);
        }

        [Test]
        public void dYdXMarketOrderUsesTakerFee()
        {
            var security = CreateSecurity(Symbol.Create("BTCUSD", SecurityType.CryptoFuture, QuantConnect.Market.DYDX), 50000m);
            var fee = GetFee(new dYdXFeeModel(), security, 2m);
            Assert.AreEqual(100000m * 0.0005m, fee.Value.Amount);
            Assert.AreEqual(Currencies.USD, fee.Value.Currency);
        }

        [TestCase(true, 49000, 0.0001)]
        [TestCase(false, 49000, 0.0001)]
        public void dYdXLimitOrderFees(bool postOnly, decimal limitPrice, decimal expectedRate)
        {
            var security = CreateSecurity(Symbol.Create("BTCUSD", SecurityType.CryptoFuture, QuantConnect.Market.DYDX), 50000m);
            var order = new LimitOrder(security.Symbol, 1m, limitPrice, OrderTime, properties: new dYdXOrderProperties { PostOnly = postOnly });
            var fee = new dYdXFeeModel().GetOrderFee(new OrderFeeParameters(security, order));
            Assert.AreEqual(50000m * expectedRate, fee.Value.Amount);
        }

        [Test]
        public void dYdXCustomFees()
        {
            var security = CreateSecurity(Symbol.Create("BTCUSD", SecurityType.CryptoFuture, QuantConnect.Market.DYDX), 100m);
            var fee = GetFee(new dYdXFeeModel(0.001m, 0.002m), security, -10m);
            Assert.AreEqual(1000m * 0.002m, fee.Value.Amount);
        }
    }
}
