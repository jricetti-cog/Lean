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
using System.IO;
using System.Text;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Tests.Common.Data.Market
{
    [TestFixture]
    public class TradeBarCoverageTests
    {
        private static readonly DateTime Date = new DateTime(2020, 1, 2);
        private const string ScaledLine = "60000,1000000,2000000,500000,1500000,100";
        private const string UnscaledLine = "60000,100,200,50,150,100";
        private const string UnscaledLineNoVolume = "60000,100,200,50,150";

        private static SubscriptionDataConfig GetConfig(Symbol symbol, Resolution resolution = Resolution.Minute)
        {
            return new SubscriptionDataConfig(typeof(TradeBar), symbol, resolution, TimeZones.Utc, TimeZones.Utc, false, false, false);
        }

        private static StreamReader ToStream(string line)
        {
            return new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));
        }

        private static void AssertBar(TradeBar bar, Symbol symbol, decimal volume, DateTime? time = null)
        {
            Assert.AreEqual(symbol, bar.Symbol);
            Assert.AreEqual(time ?? Date.AddMinutes(1), bar.Time);
            Assert.AreEqual(100m, bar.Open);
            Assert.AreEqual(200m, bar.High);
            Assert.AreEqual(50m, bar.Low);
            Assert.AreEqual(150m, bar.Close);
            Assert.AreEqual(volume, bar.Volume);
        }

        [Test]
        public void ParseEquityFromStringAndStream()
        {
            var config = GetConfig(Symbols.SPY);
            AssertBar(TradeBar.ParseEquity(config, ScaledLine, Date), Symbols.SPY, 100);
            AssertBar(TradeBar.ParseEquity<TradeBar>(config, ScaledLine, Date), Symbols.SPY, 100);
            using var reader = ToStream(ScaledLine);
            AssertBar(TradeBar.ParseEquity(config, reader, Date), Symbols.SPY, 100);
            AssertBar(TradeBar.Parse(config, ScaledLine, Date), Symbols.SPY, 100);
        }

        [Test]
        public void ParseForexFromStringAndStream()
        {
            var config = GetConfig(Symbols.EURUSD);
            AssertBar(TradeBar.ParseForex(config, UnscaledLineNoVolume, Date), Symbols.EURUSD, 0);
            AssertBar(TradeBar.ParseForex<TradeBar>(config, UnscaledLineNoVolume, Date), Symbols.EURUSD, 0);
            using var reader = ToStream(UnscaledLineNoVolume);
            AssertBar(TradeBar.ParseForex(config, reader, Date), Symbols.EURUSD, 0);
            AssertBar(TradeBar.Parse(config, UnscaledLineNoVolume, Date), Symbols.EURUSD, 0);
        }

        [Test]
        public void ParseCfdFromStringAndStream()
        {
            var symbol = Symbol.Create("XAUUSD", SecurityType.Cfd, QuantConnect.Market.Oanda);
            var config = GetConfig(symbol);
            AssertBar(TradeBar.ParseCfd(config, UnscaledLineNoVolume, Date), symbol, 0);
            AssertBar(TradeBar.ParseCfd<TradeBar>(config, UnscaledLineNoVolume, Date), symbol, 0);
            using var reader = ToStream(UnscaledLineNoVolume);
            AssertBar(TradeBar.ParseCfd(config, reader, Date), symbol, 0);
            AssertBar(TradeBar.Parse(config, UnscaledLineNoVolume, Date), symbol, 0);
        }

        [Test]
        public void ParseCryptoFromStringAndStream()
        {
            var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, QuantConnect.Market.Coinbase);
            var config = GetConfig(symbol);
            AssertBar(TradeBar.ParseCrypto(config, UnscaledLine, Date), symbol, 100);
            AssertBar(TradeBar.ParseCrypto<TradeBar>(config, UnscaledLine, Date), symbol, 100);
            using var reader = ToStream(UnscaledLine);
            AssertBar(TradeBar.ParseCrypto(config, reader, Date), symbol, 100);
        }

        [Test]
        public void ParseIndexFromStringAndStream()
        {
            var symbol = Symbol.Create("SPX", SecurityType.Index, QuantConnect.Market.USA);
            var config = GetConfig(symbol);
            AssertBar(TradeBar.ParseIndex(config, UnscaledLine, Date), symbol, 100);
            using var reader = ToStream(UnscaledLine);
            AssertBar(TradeBar.ParseIndex(config, reader, Date), symbol, 100);
        }

        [Test]
        public void ParseDailyIndexUsesDateFormat()
        {
            var symbol = Symbol.Create("SPX", SecurityType.Index, QuantConnect.Market.USA);
            var config = GetConfig(symbol, Resolution.Daily);
            const string line = "20200102 00:00,100,200,50,150,100";
            AssertBar(TradeBar.ParseIndex(config, line, Date), symbol, 100, Date);
            using var reader = ToStream(line);
            AssertBar(TradeBar.ParseIndex(config, reader, Date), symbol, 100, Date);
        }

        [Test]
        public void ParseFutureFromStringAndStream()
        {
            var symbol = Symbols.Future_ESZ18_Dec2018;
            var config = GetConfig(symbol);
            AssertBar(TradeBar.ParseFuture(config, UnscaledLine, Date), symbol, 100);
            AssertBar(TradeBar.ParseFuture<TradeBar>(config, UnscaledLine, Date), symbol, 100);
            using var reader = ToStream(UnscaledLine);
            AssertBar(TradeBar.ParseFuture(config, reader, Date), symbol, 100);
            using var reader2 = ToStream(UnscaledLine);
            AssertBar(TradeBar.ParseFuture<TradeBar>(config, reader2, Date), symbol, 100);
        }

        [Test]
        public void ParseOptionFromStringAndStreamAgree()
        {
            var symbol = Symbols.SPY_C_192_Feb19_2016;
            var config = GetConfig(symbol);
            var fromString = TradeBar.ParseOption(config, ScaledLine, Date);
            var fromGeneric = TradeBar.ParseOption<TradeBar>(config, ScaledLine, Date);
            using var reader = ToStream(ScaledLine);
            var fromStream = TradeBar.ParseOption(config, reader, Date);
            using var reader2 = ToStream(ScaledLine);
            var fromGenericStream = TradeBar.ParseOption<TradeBar>(config, reader2, Date);

            foreach (var bar in new[] { fromGeneric, fromStream, fromGenericStream })
            {
                Assert.AreEqual(fromString.Time, bar.Time);
                Assert.AreEqual(fromString.Open, bar.Open);
                Assert.AreEqual(fromString.High, bar.High);
                Assert.AreEqual(fromString.Low, bar.Low);
                Assert.AreEqual(fromString.Close, bar.Close);
                Assert.AreEqual(fromString.Volume, bar.Volume);
            }
            Assert.AreEqual(symbol, fromString.Symbol);
            Assert.AreEqual(Date.AddMinutes(1), fromString.Time);
            Assert.AreEqual(fromString.Open * 2, fromString.High);
        }

        [Test]
        public void ParseReturnsNullForUnsupportedSecurityType()
        {
            var config = GetConfig(Symbols.SPY_C_192_Feb19_2016);
            Assert.IsNull(TradeBar.Parse(config, ScaledLine, Date));
        }

        [Test]
        public void ReaderFromStringAndStream()
        {
            var config = GetConfig(Symbols.SPY);
            var bar = (TradeBar)new TradeBar().Reader(config, ScaledLine, Date, false);
            AssertBar(bar, Symbols.SPY, 100);
            using var reader = ToStream(ScaledLine);
            var streamBar = (TradeBar)new TradeBar().Reader(config, reader, Date, false);
            AssertBar(streamBar, Symbols.SPY, 100);
        }

        [Test]
        public void UpdateTracksHighLowCloseAndVolume()
        {
            var bar = new TradeBar { Symbol = Symbols.SPY };
            bar.Update(10, 0, 0, 5, 0, 0);
            bar.Update(12, 0, 0, 2, 0, 0);
            bar.Update(8, 0, 0, 3, 0, 0);
            bar.Update(9, 0, 0, 1, 0, 0);

            Assert.AreEqual(10m, bar.Open);
            Assert.AreEqual(12m, bar.High);
            Assert.AreEqual(8m, bar.Low);
            Assert.AreEqual(9m, bar.Close);
            Assert.AreEqual(11m, bar.Volume);
            Assert.AreEqual(9m, bar.Value);
        }

        [Test]
        public void CloneAndCopyConstructor()
        {
            var bar = new TradeBar(Date, Symbols.SPY, 1, 3, 0.5m, 2, 100, TimeSpan.FromHours(1));
            Assert.AreEqual(Date.AddHours(1), bar.EndTime);

            var copy = new TradeBar(bar);
            var clone = (TradeBar)bar.Clone();
            var fillForward = (TradeBar)bar.Clone(true);
            foreach (var other in new[] { copy, clone, fillForward })
            {
                Assert.AreNotSame(bar, other);
                Assert.AreEqual(bar.Symbol, other.Symbol);
                Assert.AreEqual(bar.Time, other.Time);
                Assert.AreEqual(bar.Period, other.Period);
                Assert.AreEqual(bar.Open, other.Open);
                Assert.AreEqual(bar.High, other.High);
                Assert.AreEqual(bar.Low, other.Low);
                Assert.AreEqual(bar.Close, other.Close);
            }
            Assert.IsTrue(fillForward.IsFillForward);
            Assert.AreEqual(0m, fillForward.Volume);
            StringAssert.Contains("SPY", bar.ToString());
        }

        [Test]
        public void GetSourceForLiveIsLocalFileForBacktest()
        {
            var config = GetConfig(Symbols.SPY);
            var live = new TradeBar().GetSource(config, Date, true);
            Assert.AreEqual(string.Empty, live.Source);
            Assert.AreEqual(SubscriptionTransportMedium.Streaming, live.TransportMedium);
            var backtest = new TradeBar().GetSource(config, Date, false);
            StringAssert.EndsWith("20200102_trade.zip", backtest.Source);
            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, backtest.TransportMedium);

            var option = new TradeBar().GetSource(GetConfig(Symbols.SPY_C_192_Feb19_2016), Date, false);
            StringAssert.Contains("20200102_trade_american.zip#", option.Source);
        }
    }
}
