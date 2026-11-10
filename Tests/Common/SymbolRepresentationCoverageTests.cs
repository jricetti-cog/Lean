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

namespace QuantConnect.Tests.Common
{
    [TestFixture]
    public class SymbolRepresentationCoverageTests
    {
        [Test]
        public void GenerateOptionTickerOsiCompact()
        {
            Assert.AreEqual("SPY160219C00192000",
                SymbolRepresentation.GenerateOptionTickerOSICompact("SPY", OptionRight.Call, 192m, new DateTime(2016, 2, 19)));
            Assert.AreEqual("SPY160219C00192000", Symbols.SPY_C_192_Feb19_2016.GenerateOptionTickerOSICompact());
            Assert.Throws<ArgumentException>(() => Symbols.SPY.GenerateOptionTickerOSICompact());
        }

        [Test]
        public void GenerateAndParseIqFeedOptionTicker()
        {
            var ticker = SymbolRepresentation.GenerateOptionTicker(Symbols.SPY_C_192_Feb19_2016);
            Assert.AreEqual("SPY1619B192", ticker);

            var properties = SymbolRepresentation.ParseOptionTickerIQFeed(ticker);
            Assert.AreEqual("SPY", properties.Underlying);
            Assert.AreEqual(OptionRight.Call, properties.OptionRight);
            Assert.AreEqual(192m, properties.OptionStrike);
            Assert.AreEqual(new DateTime(2016, 2, 19), properties.ExpirationDate);
        }

        [Test]
        public void ParseIqFeedPutOptionTicker()
        {
            var properties = SymbolRepresentation.ParseOptionTickerIQFeed("AAPL2105M150.5");
            Assert.AreEqual("AAPL", properties.Underlying);
            Assert.AreEqual(OptionRight.Put, properties.OptionRight);
            Assert.AreEqual(150.5m, properties.OptionStrike);
            Assert.AreEqual(new DateTime(2021, 1, 5), properties.ExpirationDate);
        }

        [Test]
        public void ParseIqFeedOptionTickerReturnsNullForInvalidStrike()
        {
            Assert.IsNull(SymbolRepresentation.ParseOptionTickerIQFeed("SPY1619B1.9.2"));
        }

        [Test]
        public void ParseTickerFromUserInputEquity()
        {
            var symbol = SymbolRepresentation.ParseTickerFromUserInput("AAPL", SecurityType.Equity, QuantConnect.Market.USA);
            Assert.AreEqual(Symbol.Create("AAPL", SecurityType.Equity, QuantConnect.Market.USA), symbol);
        }

        [Test]
        public void ParseTickerFromUserInputOption()
        {
            var symbol = SymbolRepresentation.ParseTickerFromUserInput("SPY   160219C00192000", SecurityType.Option, QuantConnect.Market.USA);
            Assert.AreEqual(SecurityType.Option, symbol.SecurityType);
            Assert.AreEqual("SPY", symbol.Underlying.Value);
            Assert.AreEqual(OptionRight.Call, symbol.ID.OptionRight);
            Assert.AreEqual(192m, symbol.ID.StrikePrice);
            Assert.AreEqual(new DateTime(2016, 2, 19), symbol.ID.Date);
        }

        [Test]
        public void ParseTickerFromUserInputIndexOption()
        {
            var symbol = SymbolRepresentation.ParseTickerFromUserInput("SPX   210115C03700000", SecurityType.IndexOption, QuantConnect.Market.USA);
            Assert.AreEqual(SecurityType.IndexOption, symbol.SecurityType);
            Assert.AreEqual("SPX", symbol.Underlying.Value);
            Assert.AreEqual(3700m, symbol.ID.StrikePrice);
            Assert.AreEqual(new DateTime(2021, 1, 15), symbol.ID.Date);
        }

        [Test]
        public void ParseTickerFromUserInputFuture()
        {
            var symbol = SymbolRepresentation.ParseTickerFromUserInput("ESZ18", SecurityType.Future, QuantConnect.Market.CME);
            Assert.AreEqual(SecurityType.Future, symbol.SecurityType);
            Assert.AreEqual("ES", symbol.ID.Symbol);
            Assert.AreEqual(2018, symbol.ID.Date.Year);
            Assert.AreEqual(12, symbol.ID.Date.Month);
        }

        [Test]
        public void ParseFutureSymbolUsesReferenceYear()
        {
            var symbol = SymbolRepresentation.ParseFutureSymbol("ESZ8", 2015);
            Assert.AreEqual(2018, symbol.ID.Date.Year);
            var doubleDigit = SymbolRepresentation.ParseFutureSymbol("ESZ36", 2020);
            Assert.AreEqual(2036, doubleDigit.ID.Date.Year);
        }
    }
}
