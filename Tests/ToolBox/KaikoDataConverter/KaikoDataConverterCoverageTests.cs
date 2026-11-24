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
using System.Text;
using Ionic.Zip;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.ToolBox.KaikoDataConverter;

namespace QuantConnect.Tests.ToolBox.KaikoDataConverter
{
    [TestFixture]
    public class KaikoDataConverterCoverageTests
    {
        private const long Epoch = 1514764800000; // 2018-01-01 00:00:00 UTC

        private static readonly string[] TradeLines =
        {
            "id,exchange,symbol,date,price,amount,sell",
            $"1,bf,btcusd,{Epoch},13000.5,0.5,false",
            $"2,bf,btcusd,{Epoch + 1000},1.3e4,2e-1,true",
            $"3,bf,btcusd,{Epoch + 2000},not-a-price,1,true",
            "",
            $"4,bf,btcusd,{Epoch + 61000},13001,1,false"
        };

        private static readonly string[] QuoteLines =
        {
            "id,exchange,type,date,price,amount",
            $"1,bf,b,{Epoch},100,1",
            $"2,bf,b,{Epoch},101,2",
            $"3,bf,a,{Epoch},103,1",
            $"4,bf,a,{Epoch},102,3",
            $"5,bf,a,{Epoch + 1000},104,1",
            $"6,bf,a,{Epoch + 2000},bad,1",
            $"7,bf,x,{Epoch + 3000},104,1",
            $"8,bf,b,{Epoch + 61000},99,4",
            $"9,bf,b,{Epoch + 62000},98,5"
        };

        private string _tempDirectory;
        private Symbol _symbol;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "kaiko-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            _symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Bitfinex);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        private static byte[] Gzip(IEnumerable<string> lines)
        {
            using var memory = new MemoryStream();
            using (var gzip = new GZipStream(memory, CompressionMode.Compress, true))
            {
                var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
                gzip.Write(bytes, 0, bytes.Length);
            }
            return memory.ToArray();
        }

        private string CreateZip(string fileName, params (string EntryName, string[] Lines)[] entries)
        {
            var path = Path.Combine(_tempDirectory, fileName);
            using (var zip = new Ionic.Zip.ZipFile())
            {
                foreach (var (entryName, lines) in entries)
                {
                    zip.AddEntry(entryName, Gzip(lines));
                }
                zip.Save(path);
            }
            return path;
        }

        private List<BaseData> ReadEntry(TickType tickType, string[] lines)
        {
            var path = CreateZip("source.zip", ("entry.csv.gz", lines));
            using var zip = Ionic.Zip.ZipFile.Read(path);
            var reader = new KaikoDataReader(_symbol, tickType);
            return reader.GetTicksFromZipEntry(zip.Entries.Single()).ToList();
        }

        [Test]
        public void ReadsTradeTicksSkippingCorruptedLines()
        {
            var ticks = ReadEntry(TickType.Trade, TradeLines).Cast<Tick>().ToList();

            Assert.AreEqual(3, ticks.Count);
            Assert.IsTrue(ticks.All(t => t.TickType == TickType.Trade && t.Symbol == _symbol));

            Assert.AreEqual(new DateTime(2018, 1, 1), ticks[0].Time);
            Assert.AreEqual(13000.5m, ticks[0].Value);
            Assert.AreEqual(0.5m, ticks[0].Quantity);

            // scientific notation
            Assert.AreEqual(new DateTime(2018, 1, 1, 0, 0, 1), ticks[1].Time);
            Assert.AreEqual(13000m, ticks[1].Value);
            Assert.AreEqual(0.2m, ticks[1].Quantity);

            Assert.AreEqual(13001m, ticks[2].Value);
        }

        [Test]
        public void AggregatesQuoteSnapshotsIntoBestBidAndAsk()
        {
            var ticks = ReadEntry(TickType.Quote, QuoteLines).Cast<Tick>().ToList();

            Assert.AreEqual(3, ticks.Count);
            Assert.IsTrue(ticks.All(t => t.TickType == TickType.Quote && t.Symbol == _symbol));

            var first = ticks[0];
            Assert.AreEqual(new DateTime(2018, 1, 1), first.Time);
            Assert.AreEqual(101m, first.BidPrice);
            Assert.AreEqual(2m, first.BidSize);
            Assert.AreEqual(102m, first.AskPrice);
            Assert.AreEqual(3m, first.AskSize);

            // ask only snapshot
            var second = ticks[1];
            Assert.AreEqual(new DateTime(2018, 1, 1, 0, 0, 1), second.Time);
            Assert.AreEqual(104m, second.AskPrice);
            Assert.AreEqual(0m, second.BidPrice);

            // the corrupted line is skipped and the snapshot with an unknown direction produces no tick
            var third = ticks[2];
            Assert.AreEqual(new DateTime(2018, 1, 1, 0, 1, 1), third.Time);
            Assert.AreEqual(99m, third.BidPrice);
            Assert.AreEqual(4m, third.BidSize);
            Assert.AreEqual(0m, third.AskPrice);
        }

        [Test]
        [Ignore("Possible bug: KaikoDataReader.ParseKaikoQuoteFile never emits the quote tick for the last epoch in the file")]
        public void QuoteFileEmitsLastSnapshot()
        {
            var ticks = ReadEntry(TickType.Quote, QuoteLines).Cast<Tick>().ToList();

            Assert.AreEqual(4, ticks.Count);
            Assert.AreEqual(98m, ticks.Last().BidPrice);
        }

        [Test]
        public void ProgramThrowsForMissingSourceDirectory()
        {
            Assert.Throws<ArgumentException>(() =>
                KaikoDataConverterProgram.KaikoDataConverter(Path.Combine(_tempDirectory, "missing"), "20180101"));
        }

        [TestCase("")]
        [TestCase("Bitfinex")]
        public void ProgramConvertsTradeAndQuoteEntries(string exchange)
        {
            var source = Directory.CreateDirectory(Path.Combine(_tempDirectory, "source")).FullName;
            CreateZip(Path.Combine("source", "Bitfinex_BTCUSD.zip"),
                ("Bitfinex/trades/Bitfinex_BTCUSD_trades_2018_01_01.csv.gz", TradeLines),
                ("Bitfinex/quotes/Bitfinex_BTCUSD_ob_10_2018_01_01.csv.gz", QuoteLines),
                ("Bitfinex/trades/Bitfinex_BTCUSD_trades_2018_01_02.csv.gz", TradeLines));

            var dataFolder = RunWithDataFolder(() => KaikoDataConverterProgram.KaikoDataConverter(source, "20180101", exchange));

            var root = Path.Combine(dataFolder, "crypto", "bitfinex");
            foreach (var resolution in new[] { "tick", "second", "minute" })
            {
                var folder = Path.Combine(root, resolution, "btcusd");
                Assert.IsTrue(File.Exists(Path.Combine(folder, "20180101_trade.zip")), $"{resolution} trade");
                Assert.IsTrue(File.Exists(Path.Combine(folder, "20180101_quote.zip")), $"{resolution} quote");
                Assert.IsFalse(File.Exists(Path.Combine(folder, "20180102_trade.zip")), $"{resolution} next day");
            }

            using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(root, "tick", "btcusd", "20180101_trade.zip"));
            using var reader = new StreamReader(archive.Entries.Single().Open());
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(3, lines.Length);
        }

        [TestCase("gdax", "20180101")]
        [TestCase("", "20180105")]
        public void ProgramSkipsOtherExchangesAndMissingDates(string exchange, string date)
        {
            var source = Directory.CreateDirectory(Path.Combine(_tempDirectory, "source")).FullName;
            CreateZip(Path.Combine("source", "Bitfinex_BTCUSD.zip"),
                ("Bitfinex/trades/Bitfinex_BTCUSD_trades_2018_01_01.csv.gz", TradeLines));

            var dataFolder = RunWithDataFolder(() => KaikoDataConverterProgram.KaikoDataConverter(source, date, exchange));

            Assert.IsFalse(Directory.Exists(Path.Combine(dataFolder, "crypto")));
        }

        private string RunWithDataFolder(Action action)
        {
            var dataFolder = Directory.CreateDirectory(Path.Combine(_tempDirectory, "data")).FullName;
            var previous = Config.Get("data-folder");
            try
            {
                Config.Set("data-folder", dataFolder);
                Globals.Reset();
                action();
            }
            finally
            {
                Config.Set("data-folder", previous);
                Globals.Reset();
            }
            return dataFolder;
        }
    }
}
