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
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Common.Orders.Fees
{
    [TestFixture]
    public class InteractiveBrokersFeeModelCoverageTests
    {
        private static readonly DateTime OrderTime = new DateTime(2021, 1, 4, 10, 0, 0);
        private static readonly DateTime Expiry = new DateTime(2026, 6, 19);

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

        private static OrderFee GetFee(IFeeModel model, Security security, Order order)
        {
            return model.GetOrderFee(new OrderFeeParameters(security, order));
        }

        private static OrderFee GetFee(IFeeModel model, Security security, decimal quantity)
        {
            return GetFee(model, security, new MarketOrder(security.Symbol, quantity, OrderTime));
        }

        private static Symbol EquityOption(string market = QuantConnect.Market.USA)
        {
            var underlying = Symbol.Create("ZZZ", SecurityType.Equity, market);
            return Symbol.CreateOption(underlying, market, OptionStyle.American, OptionRight.Call, 10m, Expiry);
        }

        [TestCase(0, 22, 2)]
        [TestCase(1500000000, 16.5, 1.5)]
        [TestCase(3000000000d, 11, 1.25)]
        [TestCase(6000000000d, 8.8, 1)]
        public void ForexFeesFollowMonthlyVolumeTiers(decimal monthlyVolume, decimal expectedLargeOrderFee, decimal expectedMinimumFee)
        {
            var model = new InteractiveBrokersFeeModel(monthlyVolume);
            var security = CreateSecurity(Symbols.EURUSD, 1.1m);

            var large = GetFee(model, security, 1000000);
            Assert.AreEqual(expectedLargeOrderFee, large.Value.Amount);
            Assert.AreEqual(Currencies.USD, large.Value.Currency);
            Assert.AreEqual(expectedMinimumFee, GetFee(model, security, -1000).Value.Amount);
        }

        [TestCase(0, 0.2, 10, 6.5)]
        [TestCase(0, 0.07, 10, 5)]
        [TestCase(0, 0.01, 10, 2.5)]
        [TestCase(0, 0.01, 1, 1)]
        [TestCase(20000, 0.07, 10, 5)]
        [TestCase(20000, 0.01, 10, 2.5)]
        [TestCase(60000, 0.2, 10, 2.5)]
        [TestCase(200000, 0.2, 10, 1.5)]
        [TestCase(200000, 0.2, 2, 1)]
        public void OptionFeesFollowMonthlyContractTiers(decimal monthlyContracts, decimal premium, decimal quantity, decimal expected)
        {
            var model = new InteractiveBrokersFeeModel(0, monthlyContracts);
            var security = CreateSecurity(EquityOption(), premium);

            var fee = GetFee(model, security, quantity);
            Assert.AreEqual(expected, fee.Value.Amount);
            Assert.AreEqual(Currencies.USD, fee.Value.Currency);
        }

        [Test]
        public void OptionPremiumIsTakenFromOrderPrices()
        {
            var model = new InteractiveBrokersFeeModel();
            var symbol = EquityOption();
            var security = CreateSecurity(symbol, 1m);

            // premiums below 0.05 are charged 0.25, between 0.05 and 0.1 are charged 0.5
            Assert.AreEqual(2.5m, GetFee(model, security, new LimitOrder(symbol, 10, 0.01m, OrderTime)).Value.Amount);
            Assert.AreEqual(5m, GetFee(model, security, new StopMarketOrder(symbol, 10, 0.07m, OrderTime)).Value.Amount);
            Assert.AreEqual(2.5m, GetFee(model, security, new StopLimitOrder(symbol, 10, 1m, 0.02m, OrderTime)).Value.Amount);
            Assert.AreEqual(5m, GetFee(model, security, new TrailingStopOrder(symbol, -10, 0.06m, 0.01m, false, OrderTime)).Value.Amount);
            Assert.AreEqual(6.5m, GetFee(model, security, new MarketOrder(symbol, -10, OrderTime)).Value.Amount);
        }

        [Test]
        public void EquityOptionExerciseIsFree()
        {
            var model = new InteractiveBrokersFeeModel();
            var symbol = EquityOption();
            var security = CreateSecurity(symbol, 1m);
            var fee = GetFee(model, security, new OptionExerciseOrder(symbol, 10, OrderTime));
            Assert.AreEqual(OrderFee.Zero, fee);
        }

        [Test]
        public void OptionOnUnsupportedMarketThrows()
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(EquityOption(QuantConnect.Market.India), 1m);
            Assert.Throws<KeyNotFoundException>(() => GetFee(model, security, 1));
        }

        [TestCase("ZZZ", 2.47)]
        [TestCase("MES", 0.57)]
        [TestCase("BTC", 11.02)]
        public void UnitedStatesFutureFees(string ticker, decimal expectedPerContract)
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(Symbol.CreateFuture(ticker, QuantConnect.Market.CME, Expiry), 100m);
            var fee = GetFee(model, security, -2);
            Assert.AreEqual(2 * expectedPerContract, fee.Value.Amount);
            Assert.AreEqual(Currencies.USD, fee.Value.Currency);
        }

        [TestCase("ZZZ", 2.47)]
        [TestCase("ES", 1.42)]
        [TestCase("MES", 0.47)]
        public void UnitedStatesFutureOptionFees(string ticker, decimal expectedPerContract)
        {
            var model = new InteractiveBrokersFeeModel();
            var future = Symbol.CreateFuture(ticker, QuantConnect.Market.CME, Expiry);
            var symbol = Symbol.CreateOption(future, QuantConnect.Market.CME, OptionStyle.American, OptionRight.Put, 100m, Expiry.AddDays(-5));
            var security = CreateSecurity(symbol, 1m);
            Assert.AreEqual(3 * expectedPerContract, GetFee(model, security, 3).Value.Amount);
            // future option exercise is charged the standard commission
            Assert.AreEqual(3 * expectedPerContract, GetFee(model, security, new OptionExerciseOrder(symbol, 3, OrderTime)).Value.Amount);
        }

        [TestCase("HSI", Currencies.HKD, 40)]
        [TestCase("MHI", Currencies.HKD, 30)]
        [TestCase("CUS", Currencies.CNH, 19.5)]
        [TestCase("UCN", Currencies.USD, 3.6)]
        public void HongKongFutureFees(string ticker, string quoteCurrency, decimal expectedPerContract)
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(Symbol.CreateFuture(ticker, QuantConnect.Market.HKFE, Expiry), 100m, quoteCurrency);
            var fee = GetFee(model, security, 1);
            Assert.AreEqual(expectedPerContract, fee.Value.Amount);
            Assert.AreEqual(ticker == "HSI" ? Currencies.HKD : quoteCurrency, fee.Value.Currency);
        }

        [Test]
        public void HongKongFutureFeesRejectUnexpectedQuoteCurrency()
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(Symbol.CreateFuture("MHI", QuantConnect.Market.HKFE, Expiry), 100m, Currencies.EUR);
            Assert.Throws<ArgumentException>(() => GetFee(model, security, 1));
        }

        [Test]
        public void EurexFutureFeesUseDefaultsForUnknownContracts()
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(Symbol.CreateFuture("ZZZ", QuantConnect.Market.EUREX, Expiry), 100m, Currencies.EUR);
            var fee = GetFee(model, security, 4);
            Assert.AreEqual(4 * 1.02m, fee.Value.Amount);
            Assert.AreEqual(Currencies.EUR, fee.Value.Currency);
        }

        [Test]
        public void EurexFutureOptionsAreNotSupported()
        {
            var model = new InteractiveBrokersFeeModel();
            var future = Symbol.CreateFuture("FESX", QuantConnect.Market.EUREX, Expiry);
            var symbol = Symbol.CreateOption(future, QuantConnect.Market.EUREX, OptionStyle.European, OptionRight.Call, 100m, Expiry);
            var security = CreateSecurity(symbol, 1m, Currencies.EUR);
            Assert.Throws<ArgumentException>(() => GetFee(model, security, 1));
        }

        [Test]
        public void FutureOnUnsupportedMarketThrows()
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(Symbol.CreateFuture("ZZZ", QuantConnect.Market.India, Expiry), 100m);
            Assert.Throws<KeyNotFoundException>(() => GetFee(model, security, 1));
        }

        [TestCase(Currencies.USD, 2000, 100, 4)]
        [TestCase(Currencies.USD, 10, 1, 1)]
        [TestCase("JPY", 10, 1, 40)]
        [TestCase(Currencies.HKD, 10, 1, 10)]
        public void CfdFeesHaveCurrencyMinimums(string quoteCurrency, decimal price, decimal quantity, decimal expected)
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(Symbol.Create("XAU" + quoteCurrency, SecurityType.Cfd, QuantConnect.Market.Oanda), price, quoteCurrency);
            var fee = GetFee(model, security, quantity);
            Assert.AreEqual(expected, fee.Value.Amount);
            Assert.AreEqual(quoteCurrency, fee.Value.Currency);
        }

        [Test]
        public void UnsupportedSecurityTypeThrows()
        {
            var model = new InteractiveBrokersFeeModel();
            var security = CreateSecurity(Symbol.Create("BTCUSD", SecurityType.Crypto, QuantConnect.Market.Coinbase), 100m);
            Assert.Throws<ArgumentException>(() => GetFee(model, security, 1));
        }
    }
}
