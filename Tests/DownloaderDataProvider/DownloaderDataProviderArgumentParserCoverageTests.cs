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

using System.Collections.Generic;
using NUnit.Framework;
using QuantConnect.DownloaderDataProvider.Launcher;
using QuantConnect.DownloaderDataProvider.Launcher.Models.Constants;

namespace QuantConnect.Tests.DownloaderDataProvider
{
    [TestFixture]
    public class DownloaderDataProviderArgumentParserCoverageTests
    {
        [Test]
        public void ParsesAllDownloaderOptions()
        {
            var result = DownloaderDataProviderArgumentParser.ParseArguments(new[]
            {
                "--data-downloader", "TestDownloader",
                "--data-type", "Trade",
                "--tickers", "AAPL,SPY",
                "--security-type", "Equity",
                "--market", "usa",
                "--resolution", "Daily",
                "--start-date", "20240101",
                "--end-date", "20240201"
            });

            Assert.AreEqual("TestDownloader", result[DownloaderCommandArguments.CommandDownloaderDataDownloader]);
            Assert.AreEqual("Trade", result[DownloaderCommandArguments.CommandDataType]);
            Assert.AreEqual("Equity", result[DownloaderCommandArguments.CommandSecurityType]);
            Assert.AreEqual("usa", result[DownloaderCommandArguments.CommandMarketName]);
            Assert.AreEqual("Daily", result[DownloaderCommandArguments.CommandResolution]);
            Assert.AreEqual("20240101", result[DownloaderCommandArguments.CommandStartDate]);
            Assert.AreEqual("20240201", result[DownloaderCommandArguments.CommandEndDate]);
            var tickers = (Dictionary<string, string>)result[DownloaderCommandArguments.CommandTickers];
            CollectionAssert.AreEquivalent(new[] { "AAPL", "SPY" }, tickers.Keys);
        }

        [Test]
        public void OmittedOptionsAreNotReturned()
        {
            var result = DownloaderDataProviderArgumentParser.ParseArguments(new[] { "--market", "usa" });
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("usa", result[DownloaderCommandArguments.CommandMarketName]);
        }
    }
}
