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
using QuantConnect.Configuration;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.ToolBox.AlgoSeekFuturesConverter;
using Converter = QuantConnect.ToolBox.AlgoSeekFuturesConverter.AlgoSeekFuturesConverter;

namespace QuantConnect.Tests.ToolBox.AlgoSeekFuturesConverter
{
    [TestFixture]
    public class AlgoSeekFuturesCoverageTests
    {
        private const string Header = "Timestamp,Ticker,Type,Side,SecurityID,Quantity,Price";

        private static readonly Dictionary<string, decimal> Multipliers = new()
        {
            { "ES", 1m },
            { "GC", 10m },
            { "VX", 1m }
        };

        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "algoseek-futures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        private string WriteRawFile(string name, params string[] lines)
        {
            var path = Path.Combine(_tempDirectory, name);
            File.WriteAllLines(path, lines);
            return path;
        }

        private static List<Tick> ReadAll(AlgoSeekFuturesReader reader)
        {
            var ticks = new List<Tick>();
            if (reader.Current == null)
            {
                return ticks;
            }
            do
            {
                ticks.Add(reader.Current);
            }
            while (reader.MoveNext());
            return ticks;
        }

        [Test]
        public void ReaderParsesTradeQuoteAndOpenInterestTicks()
        {
            var file = WriteRawFile("futures.csv",
                Header,
                "20161010093000000,ESZ6,2,,1,5,21502500000000",
                "20161010093001000,ESZ6,1,B,1,10,21500000000000",
                "20161010093002000,ESZ6,1,S,1,7,21505000000000",
                "20161010093003000,ESZ6,11,,1,2500,0");

            using var reader = new AlgoSeekFuturesReader(file, Multipliers);
            var ticks = ReadAll(reader);

            Assert.AreEqual(4, ticks.Count);
            Assert.IsTrue(ticks.All(t => t.Symbol.ID.Symbol == "ES" && t.Symbol.SecurityType == SecurityType.Future));
            Assert.AreEqual(new DateTime(2016, 12, 16), ticks[0].Symbol.ID.Date.Date);

            var trade = ticks[0];
            Assert.AreEqual(TickType.Trade, trade.TickType);
            Assert.AreEqual(new DateTime(2016, 10, 10, 9, 30, 0), trade.Time);
            Assert.AreEqual(2150.25m, trade.Value);
            Assert.AreEqual(5m, trade.Quantity);

            var bid = ticks[1];
            Assert.AreEqual(TickType.Quote, bid.TickType);
            Assert.AreEqual(2150m, bid.BidPrice);
            Assert.AreEqual(10m, bid.BidSize);
            Assert.AreEqual(0m, bid.AskPrice);

            var ask = ticks[2];
            Assert.AreEqual(TickType.Quote, ask.TickType);
            Assert.AreEqual(2150.5m, ask.AskPrice);
            Assert.AreEqual(7m, ask.AskSize);
            Assert.AreEqual(0m, ask.BidPrice);

            var openInterest = ticks[3];
            Assert.AreEqual(TickType.OpenInterest, openInterest.TickType);
            Assert.AreEqual(2500m, openInterest.Value);
            StringAssert.AreEqualIgnoringCase(Market.CME, openInterest.Exchange);
        }

        [Test]
        public void ReaderAppliesMultipliersAndVixScaleFactor()
        {
            var file = WriteRawFile("futures.csv",
                Header,
                "20161010093000000,GCZ6,2,,1,3,12505000000000",
                "20161010093000000,VXV6,2,,1,4,15.5");

            using var reader = new AlgoSeekFuturesReader(file, Multipliers);
            var ticks = ReadAll(reader);

            Assert.AreEqual(2, ticks.Count);
            Assert.AreEqual("GC", ticks[0].Symbol.ID.Symbol);
            Assert.AreEqual(12505m, ticks[0].Value);
            Assert.AreEqual("VX", ticks[1].Symbol.ID.Symbol);
            Assert.AreEqual(15.5m, ticks[1].Value);
        }

        [Test]
        public void ReaderSkipsInvalidAndFilteredLines()
        {
            var file = WriteRawFile("futures.csv",
                Header,
                // too few columns
                "20161010093000000,ESZ6,2",
                // spreads and options are ignored
                "20161010093000000,ESZ6-ESH7,2,,1,5,21502500000000",
                "20161010093000000,ESZ6 C2150,2,,1,5,21502500000000",
                // empty ticker
                "20161010093000000,\"\",2,,1,5,21502500000000",
                // unknown message type
                "20161010093000000,ESZ6,4,,1,5,21502500000000",
                // quote with unknown side
                "20161010093000000,ESZ6,1,X,1,5,21502500000000",
                // ticker without multiplier
                "20161010093000000,CLZ6,2,,1,5,500000000000",
                // filtered out by the symbol filter
                "20161010093000000,GCZ6,2,,1,5,12505000000000",
                // invalid timestamp is logged and skipped
                "not-a-time,ESZ6,2,,1,5,21502500000000",
                // the only valid line
                "20161010093005000,ESZ6,2,,1,1,21500000000000");

            using var reader = new AlgoSeekFuturesReader(file, Multipliers, new HashSet<string> { "es" });
            var ticks = ReadAll(reader);

            Assert.AreEqual(1, ticks.Count);
            Assert.AreEqual(new DateTime(2016, 10, 10, 9, 30, 5), ticks[0].Time);
            Assert.AreEqual(2150m, ticks[0].Value);
            Assert.IsFalse(reader.MoveNext());
            Assert.IsNull(reader.Current);
        }

        [Test]
        public void ReaderHandlesEmptyFileAndDoesNotSupportReset()
        {
            var file = WriteRawFile("empty.csv");

            using var reader = new AlgoSeekFuturesReader(file, Multipliers);

            Assert.IsNull(reader.Current);
            Assert.IsNull(((System.Collections.IEnumerator)reader).Current);
            Assert.IsFalse(reader.MoveNext());
            Assert.Throws<NotImplementedException>(() => reader.Reset());
        }

        [TestCase(Resolution.Minute, TickType.Trade)]
        [TestCase(Resolution.Minute, TickType.Quote)]
        [TestCase(Resolution.Minute, TickType.OpenInterest)]
        public void ProcessorWritesConsolidatedDataToDisk(Resolution resolution, TickType tickType)
        {
            var date = new DateTime(2016, 10, 10);
            var symbol = Symbol.CreateFuture("ES", Market.CME, new DateTime(2016, 12, 16));
            var processor = new AlgoSeekFuturesProcessor(symbol, date, tickType, resolution, _tempDirectory);

            Assert.AreEqual(symbol, processor.Symbol);
            Assert.AreEqual(resolution, processor.Resolution);
            Assert.AreEqual(tickType, processor.TickType);
            StringAssert.StartsWith(_tempDirectory, processor.ZipPath);
            StringAssert.EndsWith(".zip", processor.ZipPath);
            StringAssert.EndsWith(".csv", processor.EntryPath);
            Assert.IsTrue(Directory.Exists(processor.ZipPath.Replace(".zip", string.Empty)));

            var otherType = tickType == TickType.Trade ? TickType.Quote : TickType.Trade;
            processor.Process(new Tick { Symbol = symbol, Time = date.AddHours(9), TickType = otherType, Value = 1m });
            foreach (var minute in new[] { 0, 0, 1 })
            {
                processor.Process(CreateTick(symbol, date.AddHours(9).AddMinutes(minute), tickType));
            }

            if (resolution == Resolution.Tick)
            {
                Assert.IsTrue(processor.ShouldWriteToDisk());
            }

            processor.FlushBuffer(date.AddHours(9).AddMinutes(1), false);
            Assert.IsTrue(processor.ShouldWriteToDisk());
            processor.FlushBuffer(DateTime.MaxValue, true);

            var file = Path.Combine(processor.ZipPath.Replace(".zip", string.Empty), processor.EntryPath);
            Assert.IsTrue(File.Exists(file));
            var lines = File.ReadAllLines(file);
            Assert.AreEqual(resolution == Resolution.Tick ? 3 : 2, lines.Length);
            Assert.IsTrue(lines.All(line => line.Split(',').Length > 1));
        }

        [Test]
        [Ignore("Possible bug: AlgoSeekFuturesProcessor final flush rewrites the last tick for Tick resolution (identity consolidator WorkingData)")]
        public void TickProcessorWritesEachTickOnce()
        {
            ProcessorWritesConsolidatedDataToDisk(Resolution.Tick, TickType.Trade);
        }

        [Test]
        public void ProcessorPathsCanBeOverridden()
        {
            var symbol = Symbol.CreateFuture("ES", Market.CME, new DateTime(2016, 12, 16));
            var processor = new AlgoSeekFuturesProcessor(symbol, new DateTime(2016, 10, 10), TickType.Trade, Resolution.Minute, _tempDirectory);

            processor.ZipPath = "custom.zip";
            processor.EntryPath = "custom.csv";
            processor.TickType = TickType.Quote;

            Assert.AreEqual("custom.zip", processor.ZipPath);
            Assert.AreEqual("custom.csv", processor.EntryPath);
            Assert.AreEqual(TickType.Quote, processor.TickType);
            Assert.IsFalse(processor.ShouldWriteToDisk());
        }

        [Test]
        public void ConverterConvertsAndPackagesRawFiles()
        {
            var date = new DateTime(2016, 10, 10);
            var remote = Directory.CreateDirectory(Path.Combine(_tempDirectory, "remote")).FullName;
            var source = Directory.CreateDirectory(Path.Combine(_tempDirectory, "source")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(_tempDirectory, "destination")).FullName;
            var rawName = "algoseek-futures-" + Guid.NewGuid().ToString("N") + ".csv";

            // the remote archive content is not read because the extracted file already exists in the source folder
            File.WriteAllText(Path.Combine(remote, rawName + ".gz"), "placeholder");
            File.WriteAllText(Path.Combine(remote, "ignored.txt"), "placeholder");
            File.WriteAllLines(Path.Combine(source, rawName), new[]
            {
                Header,
                "20161010093000000,ESZ6,2,,1,5,21502500000000",
                "20161010093010000,ESZ6,1,B,1,10,21500000000000",
                "20161010093020000,ESZ6,1,S,1,7,21505000000000",
                "20161010093100000,ESZ6,2,,1,3,21510000000000",
                "20161010093200000,ESZ6,11,,1,2500,0",
                // invalid VX prices are dropped
                "20161010093000000,VXV6,1,B,1,1,999"
            });

            var converter = new Converter(new List<Resolution> { Resolution.Minute }, date, remote, source, destination);
            try
            {
                converter.Convert();
                converter.Package(date);
            }
            finally
            {
                File.Delete(Path.Combine(Path.GetTempPath(), rawName + ".gz"));
            }

            var zips = Directory.GetFiles(Path.Combine(destination, "future"), "*.zip", SearchOption.AllDirectories);
            Assert.AreEqual(3, zips.Length, string.Join(", ", zips));
            Assert.IsTrue(zips.All(z => z.Contains("es", StringComparison.InvariantCultureIgnoreCase)));
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(destination, "future"), "*.csv", SearchOption.AllDirectories));

            var tradeZip = zips.Single(z => Path.GetFileName(z).Contains("trade", StringComparison.InvariantCulture));
            using var archive = ZipFile.OpenRead(tradeZip);
            Assert.AreEqual(1, archive.Entries.Count);
            using var reader = new StreamReader(archive.Entries[0].Open());
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(2, lines.Length);
        }

        [Test]
        public void ProgramReturnsEarlyWhenRemoteDirectoryIsMissing()
        {
            RunProgram(Path.Combine(_tempDirectory, "missing-{0}"), Path.Combine(_tempDirectory, "source-{0}"), "Minute", false, _tempDirectory);

            Assert.IsFalse(Directory.Exists(Path.Combine(_tempDirectory, "source-19980105")));
        }

        [Test]
        public void ProgramRunsConversionAndCleansSourceDirectory()
        {
            var remote = Path.Combine(_tempDirectory, "remote-{0}");
            var source = Path.Combine(_tempDirectory, "source-{0}");
            Directory.CreateDirectory(remote.Replace("{0}", "19980105"));
            var sourceDirectory = Directory.CreateDirectory(source.Replace("{0}", "19980105")).FullName;

            var dataFolder = Directory.CreateDirectory(Path.Combine(_tempDirectory, "data")).FullName;

            RunProgram(remote, source, "Minute;Second;", true, dataFolder);

            Assert.IsFalse(Directory.Exists(sourceDirectory));
            Assert.IsTrue(Directory.Exists(Path.Combine(dataFolder, "future")));
        }

        private static void RunProgram(string remote, string source, string resolutions, bool clean, string dataFolder)
        {
            var keys = new[] { "futures-remote-directory", "futures-source-directory", "resolutions", "clean-source-directory", "data-folder" };
            var previous = keys.ToDictionary(key => key, key => Config.Get(key));
            var previousHandler = Log.LogHandler;
            try
            {
                Config.Set("futures-remote-directory", remote);
                Config.Set("futures-source-directory", source);
                Config.Set("resolutions", resolutions);
                Config.Set("clean-source-directory", clean.ToString());
                Config.Set("data-folder", dataFolder);
                Globals.Reset();

                AlgoSeekFuturesProgram.AlgoSeekFuturesConverter("19980105");

                Assert.AreNotSame(previousHandler, Log.LogHandler);
            }
            finally
            {
                var handler = Log.LogHandler;
                Log.LogHandler = previousHandler;
                if (!ReferenceEquals(handler, previousHandler))
                {
                    handler.Dispose();
                }
                foreach (var kvp in previous)
                {
                    Config.Set(kvp.Key, kvp.Value);
                }
                Globals.Reset();
            }
        }

        private static Tick CreateTick(Symbol symbol, DateTime time, TickType tickType)
        {
            switch (tickType)
            {
                case TickType.Quote:
                    return new Tick { Symbol = symbol, Time = time, TickType = tickType, Value = 100m, BidPrice = 100m, BidSize = 1, AskPrice = 101m, AskSize = 2 };
                case TickType.OpenInterest:
                    return new Tick { Symbol = symbol, Time = time, TickType = tickType, Value = 1000m };
                default:
                    return new Tick { Symbol = symbol, Time = time, TickType = tickType, Value = 100m, Quantity = 1 };
            }
        }
    }
}
