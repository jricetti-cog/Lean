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
 *
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Custom.IconicTypes;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class QCAlgorithmTradingCoverageTests
    {
        private AlgorithmStub _algorithm;
        private Symbol _spy;

        [SetUp]
        public void Setup()
        {
            _algorithm = new AlgorithmStub();
            _algorithm.SetDateTime(new DateTime(2013, 10, 07, 14, 0, 0));
            _algorithm.SetFinishedWarmingUp();
            _algorithm.SetCash(100000);
            _spy = _algorithm.AddEquity("SPY").Symbol;
            SetPrice(_spy, 100m);
        }

        [Test]
        public void ExceedingMaximumOrdersStopsAlgorithm()
        {
            _algorithm.SetMaximumOrders(-1);

            var second = _algorithm.MarketOrder(_spy, 1, asynchronous: true);
            Assert.AreEqual(OrderStatus.Invalid, second.Status);
            Assert.AreEqual(OrderResponseErrorCode.ExceededMaximumOrders, second.SubmitRequest.Response.ErrorCode);
            Assert.AreEqual(AlgorithmStatus.Stopped, _algorithm.Status);
        }

        [Test]
        public void SetMaximumOrdersIgnoredOnceLocked()
        {
            _algorithm.SetLocked();
            _algorithm.SetMaximumOrders(-1);

            var second = _algorithm.MarketOrder(_spy, 1, asynchronous: true);
            Assert.AreNotEqual(OrderStatus.Invalid, second.Status);
        }

        [Test]
        public void OrderFailsWhenQuoteCurrencyConversionRateIsZero()
        {
            var btceur = _algorithm.AddCrypto("BTCEUR").Symbol;
            SetPrice(btceur, 10m);
            _algorithm.Portfolio.CashBook["EUR"].ConversionRate = 0m;

            var ticket = _algorithm.MarketOrder(btceur, 1, asynchronous: true);

            Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
            Assert.AreEqual(OrderResponseErrorCode.ConversionRateZero, ticket.SubmitRequest.Response.ErrorCode);
        }

        [Test]
        public void OrderFailsWhenQuoteCurrencyIsMissingFromCashBook()
        {
            var btceur = _algorithm.AddCrypto("BTCEUR").Symbol;
            SetPrice(btceur, 10m);
            _algorithm.Portfolio.CashBook.Remove("EUR");

            var ticket = _algorithm.MarketOrder(btceur, 1, asynchronous: true);

            Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
            Assert.AreEqual(OrderResponseErrorCode.QuoteCurrencyRequired, ticket.SubmitRequest.Response.ErrorCode);
        }

        [Test]
        public void OrderFailsWhenBaseCurrencyConversionRateIsZero()
        {
            var btcusd = _algorithm.AddCrypto("BTCUSD").Symbol;
            SetPrice(btcusd, 10m);
            _algorithm.Portfolio.CashBook["BTC"].ConversionRate = 0m;

            var ticket = _algorithm.MarketOrder(btcusd, 1, asynchronous: true);

            Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
            Assert.AreEqual(OrderResponseErrorCode.ForexConversionRateZero, ticket.SubmitRequest.Response.ErrorCode);
        }

        [Test]
        public void OrderFailsWhenBaseCurrencyIsMissingFromCashBook()
        {
            var btcusd = _algorithm.AddCrypto("BTCUSD").Symbol;
            SetPrice(btcusd, 10m);
            _algorithm.Portfolio.CashBook.Remove("BTC");

            var ticket = _algorithm.MarketOrder(btcusd, 1, asynchronous: true);

            Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
            Assert.AreEqual(OrderResponseErrorCode.ForexBaseAndQuoteCurrenciesRequired, ticket.SubmitRequest.Response.ErrorCode);
        }

        [Test]
        public void ExerciseNonOptionSecurityThrows()
        {
            Assert.Throws<ArgumentException>(() => _algorithm.ExerciseOption(_spy, 1));
        }

        [Test]
        public void ExerciseWhenExchangeClosedIsRejected()
        {
            var contract = AddOptionContract();
            _algorithm.SetDateTime(new DateTime(2013, 10, 06, 15, 0, 0));
            Assume.That(!_algorithm.Securities[contract].Exchange.ExchangeOpen);

            var ticket = _algorithm.ExerciseOption(contract, 1, asynchronous: true);

            Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
            Assert.AreEqual(OrderResponseErrorCode.ExchangeNotOpen, ticket.SubmitRequest.Response.ErrorCode);
        }

        [Test]
        public void ExerciseMoreThanHeldIsRejected()
        {
            var contract = AddOptionContract();

            var ticket = _algorithm.ExerciseOption(contract, 1, asynchronous: true);

            Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
            Assert.AreEqual(OrderResponseErrorCode.UnsupportedRequestType, ticket.SubmitRequest.Response.ErrorCode);
            StringAssert.Contains("Cannot exercise more contracts", ticket.SubmitRequest.Response.ErrorMessage);
        }

        [Test]
        public void ExerciseShortOptionPositionIsRejected()
        {
            var contract = AddOptionContract();
            _algorithm.Securities[contract].Holdings.SetHoldings(1m, -1);

            var ticket = _algorithm.ExerciseOption(contract, 1, asynchronous: true);

            Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
            Assert.AreEqual(OrderResponseErrorCode.UnsupportedRequestType, ticket.SubmitRequest.Response.ErrorCode);
            StringAssert.Contains("short option position", ticket.SubmitRequest.Response.ErrorMessage);
        }

        [Test]
        public void OrderingNonTradableIndexThrows()
        {
            var spx = _algorithm.AddIndex("SPX").Symbol;
            SetPrice(spx, 4000m);

            var exception = Assert.Throws<InvalidOperationException>(() => _algorithm.MarketOrder(spx, 1, asynchronous: true));
            StringAssert.Contains("cannot be re-added", exception.Message);
        }

        [Test]
        public void OrderingRemovedSecurityReAddsIt()
        {
            _algorithm.Settings.SeedInitialPrices = true;
            _algorithm.RemoveSecurity(_spy);
            Assert.IsFalse(_algorithm.Securities[_spy].IsTradable);

            _algorithm.MarketOrder(_spy, 1, asynchronous: true);

            Assert.IsTrue(_algorithm.Securities[_spy].IsTradable);
        }

        [Test]
        public void SetHoldingsForUnknownSymbolReportsErrorAndReturnsNoTickets()
        {
            _algorithm.ErrorMessages = new ConcurrentQueue<string>();
            var unknown = Symbol.Create("UNKNOWN", SecurityType.Equity, Market.USA);

            var tickets = _algorithm.SetHoldings(unknown, 0.5m);

            Assert.IsEmpty(tickets);
            Assert.IsTrue(_algorithm.ErrorMessages.Any(x => x.Contains("not found in portfolio")));
        }

        [Test]
        public void IsMarketOpenUsesSecurityOrSymbolHours()
        {
            _algorithm.SetDateTime(new DateTime(2013, 10, 07, 15, 0, 0));
            Assert.IsTrue(_algorithm.IsMarketOpen(_spy));

            var notAdded = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            Assert.IsTrue(_algorithm.IsMarketOpen(notAdded));

            _algorithm.SetDateTime(new DateTime(2013, 10, 06, 15, 0, 0));
            Assert.IsFalse(_algorithm.IsMarketOpen(notAdded));
        }

        [Test]
        public void AddUniverseGenericOverloadsCreateCustomUniverses()
        {
            var settings = new UniverseSettings(_algorithm.UniverseSettings) { Leverage = 3m };
            Func<IEnumerable<BaseData>, IEnumerable<Symbol>> symbolSelector = data => new[] { _spy };
            Func<IEnumerable<BaseData>, IEnumerable<string>> stringSelector = data => new[] { "AAPL" };

            var universes = new List<Universe>
            {
                _algorithm.AddUniverse<UnlinkedData>(symbolSelector),
                _algorithm.AddUniverse<UnlinkedData>(stringSelector),
                _algorithm.AddUniverse<UnlinkedData>("u-string", stringSelector),
                _algorithm.AddUniverse<UnlinkedData>("u-settings-symbol", settings, symbolSelector),
                _algorithm.AddUniverse<UnlinkedData>("u-settings-string", settings, stringSelector),
                _algorithm.AddUniverse<UnlinkedData>("u-res-string", Resolution.Hour, stringSelector),
                _algorithm.AddUniverse<UnlinkedData>("u-res-settings-symbol", Resolution.Hour, settings, symbolSelector),
                _algorithm.AddUniverse<UnlinkedData>("u-res-settings-string", Resolution.Hour, settings, stringSelector),
                _algorithm.AddUniverse<UnlinkedData>("u-res-market-symbol", Resolution.Hour, Market.USA, symbolSelector),
                _algorithm.AddUniverse<UnlinkedData>(SecurityType.Equity, "u-type-string", Resolution.Hour, Market.USA, stringSelector),
                _algorithm.AddUniverse<UnlinkedData>(SecurityType.Equity, "u-type-settings-string", Resolution.Hour, Market.USA, settings, stringSelector),
            };

            foreach (var universe in universes)
            {
                Assert.AreEqual(typeof(UnlinkedData), universe.Configuration.Type);
                Assert.IsTrue(_algorithm.UniverseManager.ContainsKey(universe.Configuration.Symbol));
            }

            Assert.AreEqual(Resolution.Hour, universes[5].Configuration.Resolution);
            Assert.AreEqual(3m, universes[3].UniverseSettings.Leverage);
            StringAssert.StartsWith("unlinkeddata-usa-", universes[0].Configuration.Symbol.Value.ToLowerInvariant());

            var data = new BaseDataCollection(_algorithm.UtcTime, _spy, new List<BaseData>());
            var selected = universes[2].SelectSymbols(_algorithm.UtcTime, data).Single();
            Assert.AreEqual("AAPL", selected.Value);
            Assert.AreEqual(SecurityType.Equity, selected.SecurityType);
            Assert.AreEqual(Market.USA, selected.ID.Market);
        }

        [Test]
        public void AddUniverseWithDateRuleSchedulesFundamentalSelection()
        {
            var dateRule = _algorithm.DateRules.EveryDay();
            var universe = _algorithm.AddUniverse(dateRule, fundamentals => new[] { _spy });

            Assert.IsInstanceOf<FundamentalUniverseFactory>(universe);
            Assert.IsTrue(universe.UniverseSettings.Schedule.Initialized);
            Assert.IsFalse(_algorithm.UniverseSettings.Schedule.Initialized);
        }

        [Test]
        public void AddUniverseWithDateSelectorUsesEquityMarket()
        {
            var universe = _algorithm.AddUniverse("date-universe", Resolution.Daily, date => new[] { "AAPL" });

            Assert.IsInstanceOf<UserDefinedUniverse>(universe);
            Assert.AreEqual(SecurityType.Equity, universe.Configuration.SecurityType);
            Assert.AreEqual(Market.USA, universe.Configuration.Market);
        }

        [Test]
        public void AddUniverseOptionsAddsUnderlyingButFailsWithoutUnderlyingUniverse()
        {
            var aapl = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            Assert.IsFalse(_algorithm.Securities.ContainsKey(aapl));

            var exception = Assert.Throws<InvalidOperationException>(() => _algorithm.AddUniverseOptions(aapl, filter => filter));

            StringAssert.Contains("Universe not found for underlying Symbol", exception.Message);
            Assert.IsTrue(_algorithm.Securities.ContainsKey(aapl));
        }

        [Test]
        public void ReAddingRemovedSecurityMakesItTradableAgain()
        {
            Assert.IsTrue(_algorithm.RemoveSecurity(_spy));
            Assert.IsFalse(_algorithm.Securities[_spy].IsTradable);

            var security = _algorithm.AddEquity("SPY");

            Assert.IsTrue(security.IsTradable);
            Assert.AreSame(security, _algorithm.Securities[_spy]);
        }

        private Symbol AddOptionContract()
        {
            var aapl = _algorithm.AddEquity("AAPL", dataNormalizationMode: DataNormalizationMode.Raw).Symbol;
            SetPrice(aapl, 500m);
            var contract = Symbol.CreateOption(aapl, Market.USA, OptionStyle.American, OptionRight.Call, 500m, new DateTime(2013, 10, 19));
            _algorithm.AddOptionContract(contract);
            SetPrice(contract, 5m);
            return contract;
        }

        private void SetPrice(Symbol symbol, decimal price)
        {
            _algorithm.Securities[symbol].SetMarketPrice(new TradeBar(_algorithm.Time, symbol, price, price, price, price, 1000));
        }
    }
}
