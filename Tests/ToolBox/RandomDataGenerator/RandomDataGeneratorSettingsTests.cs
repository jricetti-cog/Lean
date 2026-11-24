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
using QuantConnect.ToolBox.RandomDataGenerator;

namespace QuantConnect.Tests.ToolBox.RandomDataGenerator
{
    [TestFixture]
    public class RandomDataGeneratorSettingsTests
    {
        private static RandomDataGeneratorSettings Create(
            string start = "20200101",
            string end = "20200201",
            string symbolCount = "5",
            string market = "",
            string securityType = "",
            string resolution = "",
            string dataDensity = "",
            string includeCoarse = "",
            string quoteTradeRatio = "",
            string randomSeed = "",
            string ipo = "",
            string rename = "",
            string splits = "",
            string dividends = "",
            string dividendEveryQuarter = "",
            string optionPriceEngine = "",
            string volatilityResolution = "",
            string chainSymbolCount = "",
            List<string> tickers = null,
            double monthSplitPercentage = 5.0)
        {
            return RandomDataGeneratorSettings.FromCommandLineArguments(start, end, symbolCount, market, securityType, resolution,
                dataDensity, includeCoarse, quoteTradeRatio, randomSeed, ipo, rename, splits, dividends, dividendEveryQuarter,
                optionPriceEngine, volatilityResolution, chainSymbolCount, tickers, monthSplitPercentage);
        }

        [Test]
        public void UsesDefaultsForOptionalArguments()
        {
            var settings = Create();

            Assert.AreEqual(new DateTime(2020, 1, 1), settings.Start);
            Assert.AreEqual(new DateTime(2020, 2, 1), settings.End);
            Assert.AreEqual(5, settings.SymbolCount);
            Assert.AreEqual(Market.USA, settings.Market);
            Assert.AreEqual(SecurityType.Equity, settings.SecurityType);
            Assert.AreEqual(Resolution.Minute, settings.Resolution);
            Assert.AreEqual(Resolution.Daily, settings.VolatilityModelResolution);
            Assert.AreEqual(DataDensity.Dense, settings.DataDensity);
            Assert.IsTrue(settings.IncludeCoarse);
            Assert.AreEqual(1d, settings.QuoteTradeRatio);
            Assert.AreEqual(0, settings.RandomSeed);
            Assert.IsFalse(settings.RandomSeedSet);
            Assert.AreEqual(5d, settings.HasIpoPercentage);
            Assert.AreEqual(30d, settings.HasRenamePercentage);
            Assert.AreEqual(15d, settings.HasSplitsPercentage);
            Assert.AreEqual(60d, settings.HasDividendsPercentage);
            Assert.AreEqual(30d, settings.DividendEveryQuarterPercentage);
            Assert.AreEqual(5d, settings.MonthSplitPercentage);
            Assert.AreEqual(10, settings.ChainSymbolCount);
            Assert.IsNull(settings.Tickers);
        }

        [Test]
        public void ParsesExplicitArguments()
        {
            var tickers = new List<string> { "SPY", "AAPL" };
            var settings = Create(
                start: "2020-03-01",
                end: "202004",
                symbolCount: "ignored",
                market: "usa",
                securityType: "option",
                resolution: "minute",
                dataDensity: "sparse",
                includeCoarse: "true",
                quoteTradeRatio: "2.5",
                randomSeed: "42",
                ipo: "1",
                rename: "2",
                splits: "3",
                dividends: "4",
                dividendEveryQuarter: "6",
                optionPriceEngine: "BlackScholes",
                volatilityResolution: "hour",
                chainSymbolCount: "3",
                tickers: tickers,
                monthSplitPercentage: 7.5);

            Assert.AreEqual(new DateTime(2020, 3, 1), settings.Start);
            Assert.AreEqual(new DateTime(2020, 4, 1), settings.End);
            Assert.AreEqual(2, settings.SymbolCount);
            Assert.AreSame(tickers, settings.Tickers);
            Assert.AreEqual(SecurityType.Option, settings.SecurityType);
            Assert.AreEqual(Resolution.Minute, settings.Resolution);
            Assert.AreEqual(Resolution.Hour, settings.VolatilityModelResolution);
            Assert.AreEqual(DataDensity.Sparse, settings.DataDensity);
            Assert.IsTrue(settings.IncludeCoarse);
            Assert.AreEqual(2.5d, settings.QuoteTradeRatio);
            Assert.AreEqual(42, settings.RandomSeed);
            Assert.IsTrue(settings.RandomSeedSet);
            Assert.AreEqual(1d, settings.HasIpoPercentage);
            Assert.AreEqual(2d, settings.HasRenamePercentage);
            Assert.AreEqual(3d, settings.HasSplitsPercentage);
            Assert.AreEqual(4d, settings.HasDividendsPercentage);
            Assert.AreEqual(6d, settings.DividendEveryQuarterPercentage);
            Assert.AreEqual(7.5d, settings.MonthSplitPercentage);
            Assert.AreEqual("BlackScholes", settings.OptionPriceEngineName);
            Assert.AreEqual(3, settings.ChainSymbolCount);
        }

        [TestCase("future", Market.CME)]
        [TestCase("crypto", Market.Coinbase)]
        [TestCase("forex", Market.Oanda)]
        public void NonEquitySecurityTypesUseDefaultMarketAndNoCoarse(string securityType, string expectedMarket)
        {
            var settings = Create(securityType: securityType, includeCoarse: "");

            Assert.AreEqual(expectedMarket, settings.Market);
            Assert.IsFalse(settings.IncludeCoarse);
        }

        [Test]
        public void OptionDefaultsToSparseDensity()
        {
            var settings = Create(securityType: "Option", chainSymbolCount: "0");

            Assert.AreEqual(DataDensity.Sparse, settings.DataDensity);
            Assert.AreEqual(10, settings.ChainSymbolCount);
            Assert.AreEqual(Market.USA, settings.Market);
        }

        [Test]
        public void UnknownMarketIsRegistered()
        {
            const string market = "randomdatagensettingstestmarket";

            var settings = Create(market: market, securityType: "Crypto", includeCoarse: "false");

            Assert.AreEqual(market, settings.Market);
            Assert.IsNotNull(Market.Encode(market));
            Assert.AreEqual(market, Market.Decode(Market.Encode(market).Value));
            Assert.IsFalse(settings.IncludeCoarse);
        }
    }
}
