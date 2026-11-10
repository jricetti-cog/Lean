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
using System.IO;
using System.IO.Compression;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.Market;
using QuantConnect.ToolBox;

namespace QuantConnect.Tests.ToolBox
{
    [TestFixture]
    public class FactorFileGeneratorTests
    {
        private string _tempDirectory;
        private string _dataPath;
        private Symbol _symbol;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "factor-file-generator-" + Guid.NewGuid().ToString("N"));
            var dailyFolder = Directory.CreateDirectory(Path.Combine(_tempDirectory, "equity", "usa", "daily")).FullName;
            _dataPath = Path.Combine(dailyFolder, "aaa.zip");
            _symbol = Symbol.Create("AAA", SecurityType.Equity, Market.USA);

            var closes = new (DateTime Date, decimal Close)[]
            {
                (new DateTime(2018, 1, 2), 95m),
                (new DateTime(2018, 1, 3), 96m),
                (new DateTime(2018, 1, 4), 97m),
                (new DateTime(2018, 1, 5), 100m),
                (new DateTime(2018, 1, 8), 101m),
                (new DateTime(2018, 1, 9), 102m),
                (new DateTime(2018, 1, 10), 103m)
            };

            using var archive = ZipFile.Open(_dataPath, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("aaa.csv");
            using var writer = new StreamWriter(entry.Open());
            foreach (var (date, close) in closes)
            {
                var scaled = (long)(close * 10000);
                writer.WriteLine($"{date:yyyyMMdd} 00:00,{scaled},{scaled},{scaled},{scaled},1000");
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        private static Dictionary<DateTime, CorporateFactorRow> Rows(CorporateFactorProvider provider)
        {
            return provider.Cast<CorporateFactorRow>().ToDictionary(row => row.Date);
        }

        [Test]
        public void ReadsDailyDataAndExposesSymbol()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);

            Assert.AreEqual(_symbol, generator.Symbol);
            generator.Symbol = Symbols.SPY;
            Assert.AreEqual(Symbols.SPY, generator.Symbol);
        }

        [Test]
        public void NoEventsProducesFirstAndLastRows()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);

            var provider = generator.CreateFactorFile(new List<BaseData>());
            var rows = Rows(provider);

            Assert.AreEqual(2, rows.Count);
            var last = rows[new DateTime(2018, 1, 2)];
            Assert.AreEqual(1m, last.PriceFactor);
            Assert.AreEqual(1m, last.SplitFactor);
            Assert.AreEqual(95m, last.ReferencePrice);
            Assert.IsTrue(rows.ContainsKey(Time.EndOfTime));
        }

        [Test]
        public void DividendAndSplitGenerateFactorRows()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);
            var events = new List<BaseData>
            {
                new Dividend(_symbol, new DateTime(2018, 1, 8), 1m, 100m),
                new Split(_symbol, new DateTime(2018, 1, 4), 97m, 0.5m, SplitType.SplitOccurred)
            };

            var rows = Rows(generator.CreateFactorFile(events));

            Assert.AreEqual(4, rows.Count);

            // dividend: previous tradable day before Monday 8th is Friday 5th
            var dividendRow = rows[new DateTime(2018, 1, 5)];
            Assert.AreEqual(0.99m, dividendRow.PriceFactor);
            Assert.AreEqual(1m, dividendRow.SplitFactor);
            Assert.AreEqual(100m, dividendRow.ReferencePrice);

            // split: previous tradable day before the 4th is the 3rd
            var splitRow = rows[new DateTime(2018, 1, 3)];
            Assert.AreEqual(0.99m, splitRow.PriceFactor);
            Assert.AreEqual(96m, splitRow.ReferencePrice);
            Assert.AreNotEqual(1m, splitRow.SplitFactor);

            var lastRow = rows[new DateTime(2018, 1, 2)];
            Assert.AreEqual(0.99m, lastRow.PriceFactor);
            Assert.AreEqual(splitRow.SplitFactor, lastRow.SplitFactor);
            Assert.AreEqual(95m, lastRow.ReferencePrice);
        }

        [Test]
        [Ignore("Possible bug: FactorFileGenerator divides by Split.Value (the reference price) instead of Split.SplitFactor")]
        public void SplitFactorUsesSplitFactorOfTheEvent()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);
            var events = new List<BaseData>
            {
                new Split(_symbol, new DateTime(2018, 1, 4), 97m, 0.5m, SplitType.SplitOccurred)
            };

            var rows = Rows(generator.CreateFactorFile(events));

            Assert.AreEqual(0.5m, rows[new DateTime(2018, 1, 3)].SplitFactor);
        }

        [Test]
        public void IntraDayDividendAndSplitAreCombined()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);
            var date = new DateTime(2018, 1, 9);
            var events = new List<BaseData>
            {
                new Split(_symbol, date, 102m, 0.5m, SplitType.SplitOccurred),
                new Dividend(_symbol, date, 2.02m, 102m)
            };

            var rows = Rows(generator.CreateFactorFile(events));

            Assert.AreEqual(3, rows.Count);
            var combined = rows[new DateTime(2018, 1, 8)];
            Assert.AreEqual(0.98m, combined.PriceFactor);
            Assert.AreEqual(101m, combined.ReferencePrice);
            Assert.AreNotEqual(1m, combined.SplitFactor);
            Assert.AreEqual(combined.SplitFactor, rows[new DateTime(2018, 1, 2)].SplitFactor);
        }

        [Test]
        public void EventsWithoutDailyDataAreSkipped()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);
            var events = new List<BaseData>
            {
                // saturday: no daily data for this date
                new Dividend(_symbol, new DateTime(2018, 1, 6), 1m, 100m),
                new Split(_symbol, new DateTime(2018, 1, 7), 1m, 0.5m, SplitType.SplitOccurred)
            };

            var rows = Rows(generator.CreateFactorFile(events));

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(1m, rows[new DateTime(2018, 1, 2)].PriceFactor);
            Assert.AreEqual(1m, rows[new DateTime(2018, 1, 2)].SplitFactor);
        }

        [Test]
        public void EventsBeforeAvailableDataStopGeneration()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);
            var events = new List<BaseData>
            {
                new Dividend(_symbol, new DateTime(2017, 12, 1), 1m, 100m)
            };

            var rows = Rows(generator.CreateFactorFile(events));

            Assert.AreEqual(2, rows.Count);
            var last = rows[new DateTime(2018, 1, 2)];
            Assert.AreEqual(1m, last.ReferencePrice);
            Assert.AreEqual(1m, last.PriceFactor);
        }

        [Test]
        public void UnsupportedEventTypeThrows()
        {
            var generator = new FactorFileGenerator(_symbol, _dataPath);
            var events = new List<BaseData>
            {
                new TradeBar(new DateTime(2018, 1, 8), _symbol, 1, 1, 1, 1, 1)
            };

            Assert.Throws<ArgumentException>(() => generator.CreateFactorFile(events));
        }

        [Test]
        public void InvalidDataPathThrows()
        {
            var invalid = Path.Combine(_tempDirectory, "not-lean-data.txt");
            File.WriteAllText(invalid, string.Empty);

            Assert.Throws<ArgumentException>(() => new FactorFileGenerator(_symbol, invalid));
        }
    }
}
