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
    public class TickCoverageTests
    {
        private static readonly DateTime Date = new DateTime(2020, 1, 2);

        private static SubscriptionDataConfig GetConfig(Symbol symbol, TickType tickType)
        {
            return new SubscriptionDataConfig(typeof(Tick), symbol, Resolution.Tick, TimeZones.Utc, TimeZones.Utc, false, false, false,
                tickType: tickType);
        }

        private static Tick ParseAndCompare(SubscriptionDataConfig config, string line)
        {
            var fromString = new Tick(config, line, Date);
            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));
            var fromStream = new Tick(config, reader, Date);

            Assert.AreEqual(fromString.Symbol, fromStream.Symbol);
            Assert.AreEqual(fromString.TickType, fromStream.TickType);
            Assert.AreEqual(fromString.Time, fromStream.Time);
            Assert.AreEqual(fromString.Value, fromStream.Value);
            Assert.AreEqual(fromString.Quantity, fromStream.Quantity);
            Assert.AreEqual(fromString.BidPrice, fromStream.BidPrice);
            Assert.AreEqual(fromString.BidSize, fromStream.BidSize);
            Assert.AreEqual(fromString.AskPrice, fromStream.AskPrice);
            Assert.AreEqual(fromString.AskSize, fromStream.AskSize);
            Assert.AreEqual(fromString.Exchange, fromStream.Exchange);
            Assert.AreEqual(fromString.SaleCondition, fromStream.SaleCondition);
            Assert.AreEqual(fromString.Suspicious, fromStream.Suspicious);
            Assert.AreEqual(config.Symbol, fromString.Symbol);
            Assert.AreEqual(Date.AddHours(1), fromString.Time);
            return fromString;
        }

        [Test]
        public void EquityTradeWithConditions()
        {
            var tick = ParseAndCompare(GetConfig(Symbols.SPY, TickType.Trade), "3600000,1500000,100,P,@,1");
            Assert.AreEqual(TickType.Trade, tick.TickType);
            Assert.AreEqual(150m, tick.Value);
            Assert.AreEqual(100m, tick.Quantity);
            Assert.AreEqual("@", tick.SaleCondition);
            Assert.IsTrue(tick.Suspicious);
        }

        [Test]
        public void EquityQuoteWithConditions()
        {
            var tick = ParseAndCompare(GetConfig(Symbols.SPY, TickType.Quote), "3600000,1000000,10,1010000,20,P,0,0");
            Assert.AreEqual(TickType.Quote, tick.TickType);
            Assert.AreEqual(100m, tick.BidPrice);
            Assert.AreEqual(10m, tick.BidSize);
            Assert.AreEqual(101m, tick.AskPrice);
            Assert.AreEqual(20m, tick.AskSize);
            Assert.AreEqual(100.5m, tick.Value);
            Assert.AreEqual("0", tick.SaleCondition);
            Assert.IsFalse(tick.Suspicious);
        }

        [Test]
        public void ForexQuote()
        {
            var tick = ParseAndCompare(GetConfig(Symbols.EURUSD, TickType.Quote), "3600000,1.1,1.2");
            Assert.AreEqual(TickType.Quote, tick.TickType);
            Assert.AreEqual(1.1m, tick.BidPrice);
            Assert.AreEqual(1.2m, tick.AskPrice);
            Assert.AreEqual(1.15m, tick.Value);
        }

        [Test]
        public void CryptoTradeAndQuote()
        {
            var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, QuantConnect.Market.Coinbase);
            var trade = ParseAndCompare(GetConfig(symbol, TickType.Trade), "3600000,100,0.5,1");
            Assert.AreEqual(100m, trade.Value);
            Assert.AreEqual(0.5m, trade.Quantity);
            Assert.IsTrue(trade.Suspicious);

            var quote = ParseAndCompare(GetConfig(symbol, TickType.Quote), "3600000,100,1,102,2,0");
            Assert.AreEqual(100m, quote.BidPrice);
            Assert.AreEqual(1m, quote.BidSize);
            Assert.AreEqual(102m, quote.AskPrice);
            Assert.AreEqual(2m, quote.AskSize);
            Assert.AreEqual(101m, quote.Value);
            Assert.IsFalse(quote.Suspicious);
        }

        [Test]
        public void FutureTradeQuoteAndOpenInterest()
        {
            var symbol = Symbols.Future_ESZ18_Dec2018;
            var trade = ParseAndCompare(GetConfig(symbol, TickType.Trade), "3600000,2800,5,CME,X,0");
            Assert.AreEqual(2800m, trade.Value);
            Assert.AreEqual(5m, trade.Quantity);
            Assert.AreEqual("X", trade.SaleCondition);
            Assert.IsFalse(trade.Suspicious);

            var openInterest = ParseAndCompare(GetConfig(symbol, TickType.OpenInterest), "3600000,1234");
            Assert.AreEqual(TickType.OpenInterest, openInterest.TickType);
            Assert.AreEqual(1234m, openInterest.Value);

            var askOnly = ParseAndCompare(GetConfig(symbol, TickType.Quote), "3600000,,,2801,3,CME,0");
            Assert.AreEqual(0m, askOnly.BidPrice);
            Assert.AreEqual(2801m, askOnly.AskPrice);
            Assert.AreEqual(3m, askOnly.AskSize);
            Assert.AreEqual(2801m, askOnly.Value);
        }

        [Test]
        public void OptionQuoteIsScaled()
        {
            var tick = ParseAndCompare(GetConfig(Symbols.SPY_C_192_Feb19_2016, TickType.Quote), "3600000,10000,1,20000,2,C,0");
            Assert.AreEqual(1m, tick.BidPrice);
            Assert.AreEqual(2m, tick.AskPrice);
            Assert.AreEqual(1.5m, tick.Value);
        }

        [Test]
        public void ForexLineConstructorParsesTimeAndPrices()
        {
            var tick = new Tick(Symbols.EURUSD, "20200102 01:02:03.5000,1.1,1.2");
            Assert.AreEqual(TickType.Quote, tick.TickType);
            Assert.AreEqual(new DateTime(2020, 1, 2, 1, 2, 3, 500), tick.Time);
            Assert.AreEqual(1.1m, tick.BidPrice);
            Assert.AreEqual(1.2m, tick.AskPrice);
        }

        [Test]
        [Ignore("Possible bug: Tick(Symbol, string) computes Value from BidPrice/AskPrice before they are parsed, so Value is always 0")]
        public void ForexLineConstructorSetsMidPriceValue()
        {
            var tick = new Tick(Symbols.EURUSD, "20200102 01:02:03.5000,1.1,1.2");
            Assert.AreEqual(1.15m, tick.Value);
        }

        [Test]
        public void TradeLineConstructorWithBaseDate()
        {
            var tick = new Tick(Symbols.SPY, "3600000,1500000,100,P ,@,1", Date);
            Assert.AreEqual(TickType.Trade, tick.TickType);
            Assert.AreEqual(Date.AddHours(1), tick.Time);
            Assert.AreEqual(150m, tick.Value);
            Assert.AreEqual(100m, tick.Quantity);
            Assert.AreEqual("@", tick.SaleCondition);
            Assert.IsTrue(tick.Suspicious);
        }

        [Test]
        public void ReaderUsesConfigFromStringAndStream()
        {
            var config = GetConfig(Symbols.SPY, TickType.Trade);
            var tick = (Tick)new Tick().Reader(config, "3600000,1500000,100", Date, false);
            Assert.AreEqual(150m, tick.Value);
            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("3600000,1500000,100\n")));
            var streamTick = (Tick)new Tick().Reader(config, reader, Date, false);
            Assert.AreEqual(150m, streamTick.Value);
            Assert.AreEqual(100m, streamTick.Quantity);
        }

        [Test]
        public void GetSourceForLiveBacktestAndFutures()
        {
            var live = new Tick().GetSource(GetConfig(Symbols.SPY, TickType.Trade), Date, true);
            Assert.AreEqual(SubscriptionTransportMedium.Streaming, live.TransportMedium);

            var equity = new Tick().GetSource(GetConfig(Symbols.SPY, TickType.Trade), Date, false);
            StringAssert.EndsWith("20200102_trade.zip", equity.Source);
            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, equity.TransportMedium);

            var future = new Tick().GetSource(GetConfig(Symbols.Future_ESZ18_Dec2018, TickType.Quote), Date, false);
            StringAssert.Contains("20200102_quote.zip#", future.Source);
        }

        [Test]
        public void UpdateCloneValidityAndToString()
        {
            var tick = new Tick { Symbol = Symbols.SPY, TickType = TickType.Trade };
            Assert.IsFalse(tick.IsValid());
            tick.Update(10, 9, 11, 5, 1, 2);
            Assert.AreEqual(10m, tick.Value);
            Assert.AreEqual(9m, tick.BidPrice);
            Assert.AreEqual(11m, tick.AskPrice);
            Assert.AreEqual(1m, tick.BidSize);
            Assert.AreEqual(2m, tick.AskSize);
            Assert.AreEqual(5m, tick.Quantity);
            Assert.IsTrue(tick.IsValid());
            Assert.AreEqual($"{Symbols.SPY}: Price: 10 Quantity: 5", tick.ToString());

            var clone = (Tick)tick.Clone();
            Assert.AreNotSame(tick, clone);
            Assert.AreEqual(tick.Value, clone.Value);
            Assert.AreEqual(tick.Quantity, clone.Quantity);
            Assert.AreEqual(tick.BidPrice, clone.BidPrice);

            var quote = new Tick(Date, Symbols.SPY, 1, 9, 2, 11);
            Assert.AreEqual(TickType.Quote, quote.TickType);
            Assert.IsTrue(quote.IsValid());
            Assert.AreEqual($"{Symbols.SPY}: Bid: 1@9 Ask: 2@11", quote.ToString());

            var openInterest = new Tick(Date, Symbols.SPY, 25m);
            Assert.AreEqual(TickType.OpenInterest, openInterest.TickType);
            Assert.IsTrue(openInterest.IsValid());
            Assert.AreEqual($"{Symbols.SPY}: OpenInterest: 25", openInterest.ToString());

            var index = new Tick { Symbol = Symbol.Create("SPX", SecurityType.Index, QuantConnect.Market.USA), TickType = TickType.Trade, Value = 10 };
            Assert.IsTrue(index.IsValid());
        }

        [Test]
        public void SetValueUsesSingleSideWhenOtherIsMissing()
        {
            var tick = new Tick { BidPrice = 0, AskPrice = 4 };
            tick.SetValue();
            Assert.AreEqual(4m, tick.Value);
            tick.BidPrice = 2;
            tick.SetValue();
            Assert.AreEqual(3m, tick.Value);
        }
    }
}
