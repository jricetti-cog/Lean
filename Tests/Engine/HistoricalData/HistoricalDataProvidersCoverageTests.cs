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
using System.Collections.Generic;
using System.Linq;
using Moq;
using NodaTime;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;
using EngineFakeHistoryProvider = QuantConnect.Lean.Engine.HistoricalData.FakeHistoryProvider;

namespace QuantConnect.Tests.Engine.HistoricalData
{
    [TestFixture]
    public class HistoricalDataProvidersCoverageTests
    {
        private static readonly MarketHoursDatabase MarketHoursDatabase = MarketHoursDatabase.FromDataFolder();
        private static readonly DateTimeZone NewYork = TimeZones.NewYork;

        private static HistoryRequest CreateRequest(Symbol symbol, Type dataType, Resolution resolution, DateTime startLocal, DateTime endLocal,
            Resolution? fillForwardResolution = null, TickType tickType = TickType.Trade)
        {
            var exchangeHours = MarketHoursDatabase.GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType);
            var dataTimeZone = MarketHoursDatabase.GetDataTimeZone(symbol.ID.Market, symbol, symbol.SecurityType);
            return new HistoryRequest(startLocal.ConvertToUtc(exchangeHours.TimeZone), endLocal.ConvertToUtc(exchangeHours.TimeZone), dataType, symbol,
                resolution, exchangeHours, dataTimeZone, fillForwardResolution, false, false, DataNormalizationMode.Raw, tickType);
        }

        private static TradeBar Bar(Symbol symbol, DateTime time, decimal price, TimeSpan period)
            => new(time, symbol, price, price, price, price, 100, period);

        private static BrokerageHistoryProvider CreateBrokerageProvider(Mock<IBrokerage> brokerage, bool dailyPreciseEndTime = true)
        {
            var provider = new BrokerageHistoryProvider();
            provider.SetBrokerage(brokerage.Object);
            provider.Initialize(new HistoryProviderInitializeParameters(null, null, null, null, null, null, null, false,
                new DataPermissionManager(), null, new AlgorithmSettings { DailyPreciseEndTime = dailyPreciseEndTime }));
            return provider;
        }

        [Test]
        public void BrokerageHistoryProviderCanOnlyBeInitializedOnce()
        {
            var brokerage = new Mock<IBrokerage>();
            var provider = CreateBrokerageProvider(brokerage);

            brokerage.Verify(x => x.Connect(), Times.Once);
            Assert.Throws<InvalidOperationException>(() => provider.Initialize(new HistoryProviderInitializeParameters(null, null, null, null, null, null,
                null, false, new DataPermissionManager(), null, new AlgorithmSettings())));
            Assert.IsTrue(provider.AlgorithmSettings.DailyPreciseEndTime);
        }

        [Test]
        public void BrokerageHistoryProviderReturnsNullWhenTheBrokerageHasNoHistory()
        {
            var brokerage = new Mock<IBrokerage>();
            brokerage.Setup(x => x.GetHistory(It.IsAny<HistoryRequest>())).Returns((IEnumerable<BaseData>)null);
            var provider = CreateBrokerageProvider(brokerage);
            var request = CreateRequest(Symbols.SPY, typeof(TradeBar), Resolution.Hour, new DateTime(2024, 1, 3, 9, 0, 0), new DateTime(2024, 1, 3, 17, 0, 0));

            Assert.IsNull(provider.GetHistory(new[] { request }, NewYork));
            Assert.AreEqual(0, provider.DataPointCount);
        }

        [Test]
        public void BrokerageHistoryProviderSynchronizesTheBrokerageHistory()
        {
            var spyRequest = CreateRequest(Symbols.SPY, typeof(TradeBar), Resolution.Hour, new DateTime(2024, 1, 3, 9, 0, 0), new DateTime(2024, 1, 3, 17, 0, 0));
            var aaplRequest = CreateRequest(Symbols.AAPL, typeof(TradeBar), Resolution.Hour, new DateTime(2024, 1, 3, 9, 0, 0), new DateTime(2024, 1, 3, 17, 0, 0));
            var ibmRequest = CreateRequest(Symbols.IBM, typeof(TradeBar), Resolution.Hour, new DateTime(2024, 1, 3, 9, 0, 0), new DateTime(2024, 1, 3, 17, 0, 0));
            var brokerage = new Mock<IBrokerage>();
            brokerage.Setup(x => x.GetHistory(spyRequest)).Returns(new[]
            {
                Bar(Symbols.SPY, new DateTime(2024, 1, 3, 10, 0, 0), 1, Time.OneHour),
                Bar(Symbols.SPY, new DateTime(2024, 1, 3, 11, 0, 0), 2, Time.OneHour),
                Bar(Symbols.SPY, new DateTime(2024, 1, 3, 12, 0, 0), 3, Time.OneHour),
            });
            brokerage.Setup(x => x.GetHistory(aaplRequest)).Returns(new[]
            {
                Bar(Symbols.AAPL, new DateTime(2024, 1, 3, 11, 0, 0), 20, Time.OneHour),
            });
            // unsupported request
            brokerage.Setup(x => x.GetHistory(ibmRequest)).Returns((IEnumerable<BaseData>)null);
            var provider = CreateBrokerageProvider(brokerage);

            var slices = provider.GetHistory(new[] { spyRequest, aaplRequest, ibmRequest }, NewYork).ToList();

            Assert.AreEqual(3, slices.Count);
            CollectionAssert.AreEqual(new[] { 1m, 2m, 3m }, slices.Select(s => s.Bars[Symbols.SPY].Close));
            CollectionAssert.AreEqual(new[] { new DateTime(2024, 1, 3, 11, 0, 0), new DateTime(2024, 1, 3, 12, 0, 0), new DateTime(2024, 1, 3, 13, 0, 0) },
                slices.Select(s => s.Time));
            Assert.AreEqual(20m, slices[1].Bars[Symbols.AAPL].Close);
            Assert.IsFalse(slices[0].Bars.ContainsKey(Symbols.AAPL));
            Assert.IsFalse(slices.Any(s => s.Bars.ContainsKey(Symbols.IBM)));
            Assert.AreEqual(4, provider.DataPointCount);
        }

        [Test]
        public void BrokerageHistoryProviderFillsForwardQuoteBars()
        {
            var request = CreateRequest(Symbols.EURUSD, typeof(QuoteBar), Resolution.Hour, new DateTime(2024, 1, 3, 0, 0, 0), new DateTime(2024, 1, 3, 5, 0, 0),
                fillForwardResolution: Resolution.Hour, tickType: TickType.Quote);
            var brokerage = new Mock<IBrokerage>();
            brokerage.Setup(x => x.GetHistory(request)).Returns(new BaseData[]
            {
                new QuoteBar(new DateTime(2024, 1, 3, 0, 0, 0), Symbols.EURUSD, new Bar(1.1m, 1.1m, 1.1m, 1.1m), 0, new Bar(1.2m, 1.2m, 1.2m, 1.2m), 0, Time.OneHour),
                new QuoteBar(new DateTime(2024, 1, 3, 3, 0, 0), Symbols.EURUSD, new Bar(1.3m, 1.3m, 1.3m, 1.3m), 0, new Bar(1.4m, 1.4m, 1.4m, 1.4m), 0, Time.OneHour),
            });
            var provider = CreateBrokerageProvider(brokerage);

            var bars = provider.GetHistory(new[] { request }, NewYork).Select(s => s.QuoteBars[Symbols.EURUSD]).ToList();

            Assert.Greater(bars.Count, 2);
            var fillForward = bars.Where(b => b.IsFillForward).ToList();
            Assert.IsNotEmpty(fillForward);
            // bars between the two data points are filled forward from the first one
            var gap = fillForward.Where(b => b.EndTime <= new DateTime(2024, 1, 3, 3, 0, 0)).ToList();
            Assert.IsNotEmpty(gap);
            Assert.IsTrue(gap.All(b => b.Bid.Close == 1.1m && b.Ask.Close == 1.2m));
            Assert.IsTrue(fillForward.Except(gap).All(b => b.Bid.Close == 1.3m && b.Ask.Close == 1.4m));
            Assert.AreEqual(1, bars.Count(b => !b.IsFillForward && b.Bid.Close == 1.3m));
        }

        [TestCase(true, 16)]
        [TestCase(false, 0)]
        public void BrokerageHistoryProviderAppliesStrictDailyEndTimes(bool dailyPreciseEndTime, int expectedEndHour)
        {
            var request = CreateRequest(Symbols.SPY, typeof(TradeBar), Resolution.Daily, new DateTime(2024, 1, 2), new DateTime(2024, 1, 5));
            var brokerage = new Mock<IBrokerage>();
            brokerage.Setup(x => x.GetHistory(request)).Returns(new[] { Bar(Symbols.SPY, new DateTime(2024, 1, 3), 5m, Time.OneDay) });
            var provider = CreateBrokerageProvider(brokerage, dailyPreciseEndTime);

            var bar = provider.GetHistory(new[] { request }, NewYork).Single().Bars[Symbols.SPY];

            Assert.AreEqual(5m, bar.Close);
            var expectedEnd = dailyPreciseEndTime ? new DateTime(2024, 1, 3, expectedEndHour, 0, 0) : new DateTime(2024, 1, 4);
            Assert.AreEqual(expectedEnd, bar.EndTime);
        }

        [Test]
        public void BrokerageHistoryProviderFillsForwardOpenInterestUsingTheOriginalExchangeHours()
        {
            var future = Symbol.CreateFuture(Futures.Indices.SP500EMini, Market.CME, new DateTime(2024, 3, 15));
            var request = CreateRequest(future, typeof(OpenInterest), Resolution.Daily, new DateTime(2024, 1, 2), new DateTime(2024, 1, 6),
                fillForwardResolution: Resolution.Daily, tickType: TickType.OpenInterest);
            var brokerage = new Mock<IBrokerage>();
            brokerage.Setup(x => x.GetHistory(request)).Returns(new BaseData[] { new OpenInterest(new DateTime(2024, 1, 2), future, 1000m) });
            var provider = CreateBrokerageProvider(brokerage, dailyPreciseEndTime: false);

            var values = provider.GetHistory(new[] { request }, NewYork)
                .Select(s => s.Get<OpenInterest>())
                .Where(x => x.ContainsKey(future))
                .Select(x => x[future])
                .ToList();

            Assert.IsNotEmpty(values);
            Assert.IsTrue(values.All(x => x.Value == 1000m));
            Assert.IsTrue(values.Skip(1).All(x => x.IsFillForward));
        }

        [Test]
        public void MappedSynchronizingHistoryProviderSkipsEmptyHistory()
        {
            var provider = new TestMappedProvider();
            var request = CreateRequest(Symbols.EURUSD, typeof(QuoteBar), Resolution.Hour, new DateTime(2024, 1, 3, 0, 0, 0), new DateTime(2024, 1, 3, 5, 0, 0),
                tickType: TickType.Quote);

            Assert.IsNull(provider.GetHistory(new[] { request }, NewYork));
            Assert.AreEqual(1, provider.Requests.Count);
        }

        [Test]
        public void MappedSynchronizingHistoryProviderReturnsSynchronizedSlices()
        {
            var eurusd = CreateRequest(Symbols.EURUSD, typeof(QuoteBar), Resolution.Hour, new DateTime(2024, 1, 3, 0, 0, 0), new DateTime(2024, 1, 3, 5, 0, 0),
                tickType: TickType.Quote);
            var btcusd = CreateRequest(Symbols.BTCUSD, typeof(TradeBar), Resolution.Hour, new DateTime(2024, 1, 3, 0, 0, 0), new DateTime(2024, 1, 3, 5, 0, 0));
            var provider = new TestMappedProvider
            {
                History = request => request.Symbol == Symbols.EURUSD
                    ? new BaseData[]
                    {
                        new QuoteBar(new DateTime(2024, 1, 3, 1, 0, 0), Symbols.EURUSD, new Bar(1, 1, 1, 1), 0, new Bar(2, 2, 2, 2), 0, Time.OneHour),
                        new QuoteBar(new DateTime(2024, 1, 3, 2, 0, 0), Symbols.EURUSD, new Bar(3, 3, 3, 3), 0, new Bar(4, 4, 4, 4), 0, Time.OneHour),
                    }
                    : null
            };

            var slices = provider.GetHistory(new[] { eurusd, btcusd }, NewYork).ToList();

            Assert.AreEqual(2, slices.Count);
            CollectionAssert.AreEqual(new[] { 1m, 3m }, slices.Select(s => s.QuoteBars[Symbols.EURUSD].Bid.Close));
            Assert.AreEqual(2, provider.Requests.Count);
            Assert.AreEqual(2, provider.DataPointCount);
        }

        [Test]
        public void FakeHistoryProviderGeneratesTradeBarsWhileTheMarketIsOpen()
        {
            var provider = new EngineFakeHistoryProvider();
            provider.Initialize(null);
            var request = CreateRequest(Symbols.SPY, typeof(TradeBar), Resolution.Minute, new DateTime(2024, 1, 3, 9, 20, 0), new DateTime(2024, 1, 3, 9, 40, 0));

            var bars = provider.GetHistory(new[] { request }, NewYork).Select(s => s.Bars[Symbols.SPY]).ToList();

            // only 9:30 to 9:40 is open
            Assert.AreEqual(10, bars.Count);
            CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(i => (decimal)i), bars.Select(b => b.Close));
            Assert.AreEqual(new DateTime(2024, 1, 3, 9, 30, 0), bars[0].Time);
            Assert.AreEqual(Time.OneMinute, bars[0].Period);
            Assert.AreEqual(10, provider.DataPointCount);
        }

        [Test]
        public void FakeHistoryProviderGeneratesQuoteBars()
        {
            var provider = new EngineFakeHistoryProvider();
            var request = CreateRequest(Symbols.EURUSD, typeof(QuoteBar), Resolution.Hour, new DateTime(2024, 1, 3, 0, 0, 0), new DateTime(2024, 1, 3, 3, 0, 0),
                tickType: TickType.Quote);

            var bars = provider.GetHistory(new[] { request }, NewYork).Select(s => s.QuoteBars[Symbols.EURUSD]).ToList();

            Assert.AreEqual(3, bars.Count);
            CollectionAssert.AreEqual(new[] { 1m, 2m, 3m }, bars.Select(b => b.Bid.Close));
            CollectionAssert.AreEqual(new[] { 1m, 2m, 3m }, bars.Select(b => b.Ask.Close));
        }

        [Test]
        public void FakeHistoryProviderYieldsNothingForUnsupportedRequests()
        {
            var provider = new EngineFakeHistoryProvider();
            Assert.IsEmpty(provider.GetHistory(Array.Empty<HistoryRequest>(), NewYork));
            Assert.IsEmpty(provider.GetHistory(new[] { CreateRequest(Symbols.SPY, typeof(OpenInterest), Resolution.Minute,
                new DateTime(2024, 1, 3, 10, 0, 0), new DateTime(2024, 1, 3, 10, 5, 0)) }, NewYork));
            // the open interest data point is counted before the unsupported type ends the enumeration
            Assert.AreEqual(1, provider.DataPointCount);
        }

        [Test]
        public void SineHistoryProviderGeneratesPricesForKnownSecurities()
        {
            var algorithm = new AlgorithmStub();
            var spy = algorithm.AddEquity("SPY", Resolution.Hour);
            var provider = new SineHistoryProvider(algorithm.Securities);
            provider.Initialize(null);
            var spyRequest = CreateRequest(spy.Symbol, typeof(TradeBar), Resolution.Hour, new DateTime(2024, 1, 3, 9, 30, 0), new DateTime(2024, 1, 3, 16, 0, 0));
            // not part of the security manager, it is skipped
            var aaplRequest = CreateRequest(Symbols.AAPL, typeof(TradeBar), Resolution.Hour, new DateTime(2024, 1, 3, 9, 30, 0), new DateTime(2024, 1, 3, 16, 0, 0));

            var slices = provider.GetHistory(new[] { spyRequest, aaplRequest }, NewYork).ToList();

            Assert.AreEqual(0, provider.DataPointCount);
            Assert.AreEqual(7, slices.Count);
            Assert.IsTrue(slices.All(s => s.Bars.ContainsKey(spy.Symbol) && !s.Bars.ContainsKey(Symbols.AAPL)));
            Assert.IsTrue(slices.All(s => s.Bars[spy.Symbol].Close >= 90m && s.Bars[spy.Symbol].Close <= 110m));
            Assert.IsTrue(slices.All(s => s.Bars[spy.Symbol].High > s.Bars[spy.Symbol].Close));
            Assert.AreEqual(slices.Last().Bars[spy.Symbol].Close, spy.Price);
        }

        private class TestMappedProvider : MappedSynchronizingHistoryProvider
        {
            public List<HistoryRequest> Requests { get; } = new();

            public Func<HistoryRequest, IEnumerable<BaseData>> History { get; set; } = _ => null;

            public override void Initialize(HistoryProviderInitializeParameters parameters)
            {
            }

            public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
            {
                Requests.Add(request);
                return History(request);
            }
        }
    }
}
