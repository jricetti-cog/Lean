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
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds.Remote;
using QuantConnect.Messaging.Bus;
using QuantConnect.Services;
using QuantConnect.Services.MarketData;

namespace QuantConnect.Tests.Services
{
    [TestFixture]
    public class MarketDataServiceTests
    {
        private string _root;
        private InMemoryMessageHub _hub;
        private InMemoryMessageBus _serviceBus;
        private InMemoryMessageBus _clientBus;
        private MarketDataService _service;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lean-mds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "equity", "usa"));
            File.WriteAllText(Path.Combine(_root, "equity", "usa", "a.csv"), "a");
            File.WriteAllText(Path.Combine(_root, "equity", "usa", "b.csv"), "bb");
            _hub = new InMemoryMessageHub();
            _serviceBus = new InMemoryMessageBus(_hub);
            _clientBus = new InMemoryMessageBus(_hub);
            _service = new MarketDataService(_serviceBus, new MarketDataStore(_root)).Start();
        }

        [TearDown]
        public void TearDown()
        {
            _service.Dispose();
            _clientBus.Dispose();
            _serviceBus.Dispose();
            Directory.Delete(_root, true);
        }

        [Test]
        public void FetchListAndStore()
        {
            var fetched = _clientBus.Request(Topics.MarketDataFetch, new DataFetchRequest { Key = "equity/usa/b.csv" }, ContractVersions.MarketDataV1);
            Assert.IsTrue(fetched.GetBody<DataFetchResponse>().Found);
            Assert.AreEqual("bb", Encoding.UTF8.GetString(fetched.Payload));

            var missing = _clientBus.Request<DataFetchRequest, DataFetchResponse>(Topics.MarketDataFetch, new DataFetchRequest { Key = "equity/usa/c.csv" }, ContractVersions.MarketDataV1);
            Assert.IsFalse(missing.Found);

            var stored = _clientBus.Request(Topics.MarketDataStore, new DataStoreRequest { Key = "equity/usa/c.csv" }, ContractVersions.MarketDataV1, Encoding.UTF8.GetBytes("ccc"))
                .GetBody<DataStoreResponse>();
            Assert.AreEqual(3, stored.Size);

            var listing = _clientBus.Request<DataListRequest, DataListResponse>(Topics.MarketDataList, new DataListRequest { Prefix = "equity/usa/" }, ContractVersions.MarketDataV1);
            CollectionAssert.AreEqual(new[] { "equity/usa/a.csv", "equity/usa/b.csv", "equity/usa/c.csv" }, listing.Files.Select(f => f.Key));
        }

        [TestCase("../outside.txt")]
        [TestCase("/etc/passwd")]
        public void RejectsKeysOutsideTheStore(string key)
        {
            Assert.Throws<MessageBusException>(() => _clientBus.Request(Topics.MarketDataFetch, new DataFetchRequest { Key = key }, ContractVersions.MarketDataV1));
        }

        [Test]
        public void RemoteDataProviderReadsThroughCache()
        {
            var cache = Path.Combine(_root, "cache");
            var previous = Globals.DataFolder;
            try
            {
                QuantConnect.Configuration.Config.Set("data-folder", _root + Path.DirectorySeparatorChar);
                Globals.Reset();
                using var provider = new RemoteDataProvider(_clientBus, cache);
                using (var stream = provider.Fetch(Path.Combine(_root, "equity", "usa", "a.csv")))
                {
                    Assert.AreEqual("a", new StreamReader(stream).ReadToEnd());
                }
                Assert.IsTrue(File.Exists(Path.Combine(cache, "equity", "usa", "a.csv")));
                Assert.IsNull(provider.Fetch(Path.Combine(_root, "equity", "usa", "missing.csv")));
            }
            finally
            {
                QuantConnect.Configuration.Config.Set("data-folder", previous);
                Globals.Reset();
            }
        }

        [Test]
        public void SynchronizesReferenceFolders()
        {
            var cache = Path.Combine(_root, "sync");
            Assert.AreEqual(2, RemoteDataProvider.SynchronizeFolders(_clientBus, cache, "equity/usa/"));
            Assert.AreEqual(0, RemoteDataProvider.SynchronizeFolders(_clientBus, cache, "equity/usa/"));
            Assert.AreEqual("bb", File.ReadAllText(Path.Combine(cache, "equity", "usa", "b.csv")));
        }

        [Test]
        public void DownloadWritesLeanDataThroughTheDownloaderPipeline()
        {
            var symbol = Symbols.SPY;
            var downloader = new FixedDataDownloader(Enumerable.Range(0, 3).Select(i =>
                (BaseData)new TradeBar(new DateTime(2013, 10, 7, 9, 30 + i, 0), symbol, 100 + i, 101 + i, 99 + i, 100.5m + i, 1000, Time.OneMinute)).ToList());
            using var service = new MarketDataService(_serviceBus, new MarketDataStore(_root), () => downloader);
            service.Start();
            var response = service.Download(new DataDownloadRequest
            {
                Symbols = new List<SymbolContract> { SymbolContract.From(symbol) },
                Resolution = Resolution.Minute,
                Start = new DateTime(2013, 10, 7),
                End = new DateTime(2013, 10, 8)
            });
            Assert.AreEqual(3, response.DataPoints);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "equity", "usa", "minute", "spy", "20131007_trade.zip")));
        }

        [Test]
        public void MarketDataPointsRoundTrip()
        {
            var bar = new TradeBar(new DateTime(2020, 1, 1, 9, 30, 0), Symbols.SPY, 1, 2, 0.5m, 1.5m, 10, Time.OneMinute);
            var restored = (TradeBar)MarketDataPointSerializer.FromContract(ServiceJson.Deserialize<MarketDataPoint>(ServiceJson.Serialize(MarketDataPointSerializer.ToContract(bar))));
            Assert.AreEqual(bar.Symbol, restored.Symbol);
            Assert.AreEqual(bar.EndTime, restored.EndTime);
            Assert.AreEqual(bar.Close, restored.Close);
            Assert.AreEqual(bar.Volume, restored.Volume);

            var quote = new QuoteBar(bar.Time, Symbols.EURUSD, new Bar(1, 2, 0.5m, 1.5m), 3, new Bar(1.1m, 2.1m, 0.6m, 1.6m), 4, Time.OneMinute);
            var restoredQuote = (QuoteBar)MarketDataPointSerializer.FromContract(MarketDataPointSerializer.ToContract(quote));
            Assert.AreEqual(quote.Ask.Close, restoredQuote.Ask.Close);
            Assert.AreEqual(quote.LastBidSize, restoredQuote.LastBidSize);
            Assert.AreEqual(Symbols.EURUSD, restoredQuote.Symbol);
        }

        private class FixedDataDownloader : IDataDownloader
        {
            private readonly List<BaseData> _data;
            public FixedDataDownloader(List<BaseData> data) => _data = data;
            public IEnumerable<BaseData> Get(DataDownloaderGetParameters parameters) => _data;
        }
    }
}
