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
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Defaults;
using QuantConnect.Services.EngineHandlers;
using QuantConnect.Services.MarketData;
using QuantConnect.Services.MarketData.Fetch;
using QuantConnect.Services.MarketData.Live;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;

namespace QuantConnect.Tests.Services.MarketData
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    /// <summary>
    /// market-data (plan step s3.1). Writes go to a temporary volume and use synthetic tickers only; the repository's
    /// Data/ folder is only ever read (the real equity/usa/daily/spy.zip is hashed before and after).
    /// </summary>
    [TestFixture, Category("Services"), NonParallelizable]
    public class MarketDataServiceTests
    {
        private static readonly string RepoData = Path.GetFullPath(Globals.DataFolder);
        private static readonly string RealSpy = Path.Combine(RepoData, "equity", "usa", "daily", "spy.zip");
        private string _root;
        private string _spyHash;
        private LeanDataVolume _volume;
        private InMemoryFetchTaskStore _store;

        [SetUp]
        public void SetUp()
        {
            _spyHash = Hash(RealSpy);
            _root = Path.Combine(Path.GetTempPath(), "lean-data-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _volume = new LeanDataVolume(_root);
            _store = new InMemoryFetchTaskStore();
        }

        [TearDown]
        public void TearDown()
        {
            LeanDataVolume.TryDelete(_root);
            Assert.AreEqual(_spyHash, Hash(RealSpy), "the real spy.zip must never be rewritten");
        }

        private FetchExecutor Executor(MarketDataFetchOptions options = null) => new(_volume, _store, options ?? new MarketDataFetchOptions());

        private static MarketDataFetchSpec Synthetic(string ticker, ContractResolution resolution, DateOnly start, DateOnly end, ContractTickType? tickType = null,
            ContractSecurityType securityType = ContractSecurityType.Equity, string market = "usa") => new()
        {
            Mode = FetchMode.GenerateSynthetic, SecurityType = securityType, Market = market, Tickers = new() { ticker }, Resolution = resolution,
            TickType = tickType, StartDate = start, EndDate = end,
        };

        [Test]
        public async Task SyntheticMinuteFetchPublishesLeanZipsAndTheCatalogSeesThem()
        {
            var files = await Executor().ExecuteAsync(Guid.NewGuid(), Synthetic("SYNMDA", ContractResolution.Minute, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 8)),
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "equity/usa/minute/synmda/20131007_trade.zip", "equity/usa/minute/synmda/20131008_trade.zip" }, files);
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(_volume.StagingRoot).Any(), "staging is removed after the atomic publish");
            var lines = ReadZipLines(Path.Combine(_root, files[0]));
            Assert.AreEqual(390, lines.Count, "one bar per regular-hours minute");
            Assert.AreEqual(5, lines[0].Split(',').Length - 1, "LEAN minute trade bar: ms,o,h,l,c,v");
            Assert.AreEqual("34200000", lines[0].Split(',')[0], "first bar at 09:30 exchange time");

            var catalog = new DataCatalog(_volume);
            var entry = catalog.List(SecurityType.Equity, "usa", Resolution.Minute).Single();
            Assert.AreEqual("SYNMDA", entry.Ticker);
            Assert.AreEqual(2, entry.Files);
            CollectionAssert.AreEqual(new[] { ContractTickType.Trade }, entry.TickTypes);
            Assert.AreEqual(new DateOnly(2013, 10, 7), entry.Start);
            Assert.AreEqual(new DateOnly(2013, 10, 8), entry.End);

            var coverage = catalog.Coverage(SecurityType.Equity, "usa", "SYNMDA", Resolution.Minute, TickType.Trade, new DateOnly(2013, 10, 4), new DateOnly(2013, 10, 9));
            Assert.AreEqual(1, coverage.Ranges.Count);
            Assert.AreEqual(new DateOnly(2013, 10, 7), coverage.Ranges[0].Start);
            Assert.AreEqual(new DateOnly(2013, 10, 8), coverage.Ranges[0].End);
            CollectionAssert.AreEqual(new[] { new DateOnly(2013, 10, 4), new DateOnly(2013, 10, 9) }, coverage.Missing, "weekend days are not missing");
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("market-data.openapi.yaml", "Coverage", ContractSchemas.ToNode(coverage)), "coverage");
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("market-data.openapi.yaml", "CatalogEntry", ContractSchemas.ToNode(entry)), "catalog entry");
        }

        [Test]
        public async Task SyntheticDailyFetchMergesWithThePublishedFile()
        {
            var executor = Executor();
            await executor.ExecuteAsync(Guid.NewGuid(), Synthetic("SYNMDB", ContractResolution.Daily, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 8)), CancellationToken.None);
            var first = ReadZipLines(Path.Combine(_root, "equity/usa/daily/synmdb.zip"));
            await executor.ExecuteAsync(Guid.NewGuid(), Synthetic("SYNMDB", ContractResolution.Daily, new DateOnly(2013, 10, 9), new DateOnly(2013, 10, 10)), CancellationToken.None);
            var merged = ReadZipLines(Path.Combine(_root, "equity/usa/daily/synmdb.zip"));

            Assert.AreEqual(2, first.Count);
            Assert.AreEqual(4, merged.Count);
            CollectionAssert.AreEqual(first, merged.Take(2), "existing rows are kept");
            CollectionAssert.AreEqual(new[] { "20131007 00:00", "20131008 00:00", "20131009 00:00", "20131010 00:00" }, merged.Select(l => l.Split(',')[0]));
            CollectionAssert.AreEqual(Enumerable.Range(7, 4).Select(d => new DateOnly(2013, 10, d)),
                new DataCatalog(_volume).AvailableDates(SecurityType.Equity, "usa", "SYNMDB", Resolution.Daily, TickType.Trade));
        }

        [Test]
        public async Task SyntheticQuoteAndTickDataUseLeanFormats()
        {
            var executor = Executor();
            var quotes = await executor.ExecuteAsync(Guid.NewGuid(),
                Synthetic("SYNFXA", ContractResolution.Minute, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 7), ContractTickType.Quote, ContractSecurityType.Forex, "oanda"),
                CancellationToken.None);
            var ticks = await executor.ExecuteAsync(Guid.NewGuid(),
                Synthetic("SYNMDC", ContractResolution.Tick, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 7), ContractTickType.Trade), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "forex/oanda/minute/synfxa/20131007_quote.zip" }, quotes);
            Assert.AreEqual(10, ReadZipLines(Path.Combine(_root, quotes[0]))[0].Split(',').Length - 1, "LEAN quote bar: ms + bid ohlc + ask ohlc (forex has no sizes)");
            CollectionAssert.AreEqual(new[] { "equity/usa/tick/synmdc/20131007_trade.zip" }, ticks);
            Assert.AreEqual(6.5 * 360, ReadZipLines(Path.Combine(_root, ticks[0])).Count, "one trade every 10 s during regular hours");
        }

        [Test]
        public void SyntheticDataRefusesRealSymbols()
        {
            foreach (var path in new[] { "equity/usa/daily/spy.zip", "equity/usa/map_files/spy.csv" })
            {
                var target = Path.Combine(_root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(RepoData, path), target);
            }
            var before = Hash(Path.Combine(_root, "equity/usa/daily/spy.zip"));

            var error = Assert.ThrowsAsync<FetchFailedException>(() =>
                Executor().ExecuteAsync(Guid.NewGuid(), Synthetic("SPY", ContractResolution.Daily, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 8)), CancellationToken.None));

            Assert.AreEqual(ErrorCode.InvalidRequest, error!.Code);
            Assert.IsFalse(error.Retryable);
            StringAssert.Contains("synthetic ticker", error.Message);
            Assert.AreEqual(before, Hash(Path.Combine(_root, "equity/usa/daily/spy.zip")));
        }

        [Test]
        public async Task FetchFilesCopiesFromTheUpstreamFolder()
        {
            var files = await Executor(new MarketDataFetchOptions { SourceFolder = RepoData }).ExecuteAsync(Guid.NewGuid(), new MarketDataFetchSpec
            {
                Mode = FetchMode.FetchFiles, SecurityType = ContractSecurityType.Equity, Market = "usa", Resolution = ContractResolution.Daily,
                Paths = new() { "equity/usa/daily/aapl.zip" },
            }, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "equity/usa/daily/aapl.zip" }, files);
            Assert.AreEqual(Hash(Path.Combine(RepoData, "equity/usa/daily/aapl.zip")), Hash(Path.Combine(_root, "equity/usa/daily/aapl.zip")));
        }

        [Test]
        public void DownloadWithoutAConfiguredDownloaderFailsWithoutRetry()
        {
            var error = Assert.ThrowsAsync<FetchFailedException>(() => Executor().ExecuteAsync(Guid.NewGuid(), new MarketDataFetchSpec
            {
                Mode = FetchMode.Download, SecurityType = ContractSecurityType.Equity, Market = "usa", Tickers = new() { "SYNMDD" },
                Resolution = ContractResolution.Daily, StartDate = new DateOnly(2013, 10, 7), EndDate = new DateOnly(2013, 10, 8),
            }, CancellationToken.None));
            Assert.AreEqual(ErrorCode.InvalidRequest, error!.Code);
            Assert.IsFalse(error.Retryable);
            StringAssert.Contains("No data downloader configured", error.Message);
        }

        [Test]
        public async Task DownloadModeRunsANamedDownloaderThroughTheDownloaderDataProvider()
        {
            var executor = new FetchExecutor(_volume, _store, new MarketDataFetchOptions(), name => name == "Synthetic" ? new SyntheticDataDownloader() : throw new KeyNotFoundException(name));
            var files = await executor.ExecuteAsync(Guid.NewGuid(), new MarketDataFetchSpec
            {
                Mode = FetchMode.Download, SecurityType = ContractSecurityType.Crypto, Market = "coinbase", Tickers = new() { "SYNUSD" },
                Resolution = ContractResolution.Hour, TickType = ContractTickType.Trade, StartDate = new DateOnly(2020, 1, 1), EndDate = new DateOnly(2020, 1, 1), Downloader = "Synthetic",
            }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "crypto/coinbase/hour/synusd_trade.zip" }, files);
            Assert.AreEqual(24, ReadZipLines(Path.Combine(_root, files[0])).Count, "crypto trades around the clock");

            var unknown = Assert.ThrowsAsync<FetchFailedException>(() => executor.ExecuteAsync(Guid.NewGuid(), new MarketDataFetchSpec
            {
                Mode = FetchMode.Download, SecurityType = ContractSecurityType.Equity, Market = "usa", Tickers = new() { "SYNMDE" },
                Resolution = ContractResolution.Daily, StartDate = new DateOnly(2013, 10, 7), EndDate = new DateOnly(2013, 10, 8), Downloader = "Nope",
            }, CancellationToken.None));
            StringAssert.Contains("Unknown data downloader 'Nope'", unknown!.Message);
        }

        [TestCase(FetchMode.FetchFiles, "../etc/passwd", null, "relative path")]
        [TestCase(FetchMode.FetchFiles, ".staging/x.zip", null, "relative path")]
        [TestCase(FetchMode.Download, null, null, "needs fetch.tickers")]
        [TestCase(FetchMode.GenerateSynthetic, null, "BAD/TICKER", "invalid ticker")]
        public void ValidationRejectsBadRequests(FetchMode mode, string path, string ticker, string expected)
        {
            var spec = new MarketDataFetchSpec
            {
                Mode = mode, SecurityType = ContractSecurityType.Equity, Market = "usa", Resolution = ContractResolution.Daily,
                Paths = path == null ? null : new() { path }, Tickers = ticker == null ? null : new() { ticker },
                StartDate = new DateOnly(2013, 10, 7), EndDate = new DateOnly(2013, 10, 8),
            };
            StringAssert.Contains(expected, FetchExecutor.Validate(spec));
        }

        [Test]
        public async Task FetchTaskRunsFromTheOutboxThroughTheQueueToCompletedEvents()
        {
            var bus = new InMemoryMessageBus();
            var artifacts = new FileSystemArtifactStore(Path.Combine(_root, ".artifacts-test"));
            var relay = new OutboxRelay(_store, bus, NullLogger<OutboxRelay>.Instance);
            var runner = new FetchTaskRunner(bus, _store, Executor(), artifacts, NullLogger<FetchTaskRunner>.Instance);
            var spec = Synthetic("synmdf", ContractResolution.Daily, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 11));

            var (task, created) = await _store.CreateAsync(spec, null, "tests", "key-1", CancellationToken.None);
            var (same, createdAgain) = await _store.CreateAsync(spec with { Tickers = new() { "SYNMDF" } }, null, "tests", null, CancellationToken.None);
            var (byKey, _) = await _store.CreateAsync(spec with { EndDate = new DateOnly(2013, 10, 9) }, null, "tests", "key-1", CancellationToken.None);
            Assert.IsTrue(created);
            Assert.IsFalse(createdAgain, "identical pending request is coalesced");
            Assert.AreEqual(task.Id, same.Id);
            Assert.AreEqual(task.Id, byKey.Id, "Idempotency-Key returns the original task");
            Assert.AreEqual("SYNMDF", task.Fetch.Tickers!.Single());

            Assert.AreEqual(1, await relay.RelayOnceAsync(CancellationToken.None));
            Assert.AreEqual(0, await relay.RelayOnceAsync(CancellationToken.None));
            var dispatched = bus.Peek(Queues.MarketDataFetch).Single();
            Assert.AreEqual(MessageTypes.MarketDataFetchRequested, dispatched.Envelope.Type);
            Assert.AreEqual(task.Id, dispatched.Envelope.CorrelationId);

            await using (await bus.ConsumeAsync(Queues.MarketDataFetch, runner.HandleAsync))
            {
                Assert.AreEqual(1, await bus.DeliverAllAsync());
            }

            var done = await _store.GetAsync(task.Id, CancellationToken.None);
            Assert.AreEqual(JobStatus.Completed, done!.Status);
            Assert.AreEqual(1, done.FilesWritten);
            Assert.IsNotNull(done.FinishedAt);
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("market-data.openapi.yaml", "FetchTask", ContractSchemas.ToNode(done)), "task");
            Assert.AreEqual(5, ReadZipLines(Path.Combine(_root, "equity/usa/daily/synmdf.zip")).Count);

            var events = bus.Published.Where(p => p.Exchange == Exchanges.Events).ToList();
            CollectionAssert.AreEqual(new[] { MessageTypes.JobStarted, MessageTypes.JobCompleted }, events.Select(e => e.Envelope.Type));
            Assert.IsTrue(events.All(e => e.RoutingKey == $"job.marketdata.{task.Id:D}.status.v1" && e.Envelope.AppId == AppIds.MarketData));
            var completed = ContractJson.Deserialize<JobCompleted>(events[1].Body.Span);
            Assert.AreEqual($"{task.Id:D}/1/manifest.json", completed.Result.Key);
            var manifest = JsonNode.Parse(File.ReadAllText(artifacts.PathOf(completed.Result.Bucket, completed.Result.Key)));
            Assert.AreEqual("equity/usa/daily/synmdf.zip", manifest!["files"]![0]!.GetValue<string>());
            foreach (var e in events)
            {
                ContractSchemas.AssertValid(ContractSchemas.ValidateMessage(MessageTypes.SchemaFile(e.Envelope.Type), JsonNode.Parse(e.Body.Span)), e.Envelope.Type);
            }

            var (next, createdNew) = await _store.CreateAsync(spec, null, "tests", null, CancellationToken.None);
            Assert.IsTrue(createdNew, "a finished task does not coalesce new requests");
            Assert.AreNotEqual(task.Id, next.Id);
        }

        [Test]
        public async Task InvalidTaskFailsFinallyWithAFailedEvent()
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "equity/usa/map_files")).FullName, "realx.csv"), "20000101,realx\n");
            var bus = new InMemoryMessageBus();
            var runner = new FetchTaskRunner(bus, _store, Executor(), new FileSystemArtifactStore(Path.Combine(_root, ".artifacts-test")), NullLogger<FetchTaskRunner>.Instance);
            var (task, _) = await _store.CreateAsync(Synthetic("REALX", ContractResolution.Daily, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 8)), null, "tests", null, CancellationToken.None);
            await new OutboxRelay(_store, bus, NullLogger<OutboxRelay>.Instance).RelayOnceAsync(CancellationToken.None);

            await using (await bus.ConsumeAsync(Queues.MarketDataFetch, runner.HandleAsync))
            {
                await bus.DeliverAllAsync();
            }

            var failed = await _store.GetAsync(task.Id, CancellationToken.None);
            Assert.AreEqual(JobStatus.Failed, failed!.Status);
            Assert.AreEqual(ErrorCode.InvalidRequest, failed.Error!.Code);
            Assert.AreEqual(0, bus.Peek(Queues.MarketDataFetch).Count, "a final failure is acked, not redelivered");
            var last = bus.Published.Last();
            Assert.AreEqual(MessageTypes.JobFailed, last.Envelope.Type);
            Assert.IsTrue(ContractJson.Deserialize<JobFailed>(last.Body.Span).Final);
        }

        [Test]
        public void HistoryReadsTheVolumeThroughSubscriptionDataReaderHistoryProvider()
        {
            var history = new HistoryService();
            var minute = history.Get(SecurityType.Equity, "usa", "SPY", Resolution.Minute, TickType.Trade, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 7));
            var rows = ReadZipLines(Path.Combine(RepoData, "equity/usa/minute/spy/20131007_trade.zip"));
            Assert.AreEqual(rows.Count, minute!.Count);
            var first = rows[0].Split(',');
            Assert.AreEqual(new DateTimeOffset(2013, 10, 7, 4, 0, 0, TimeSpan.Zero).AddMilliseconds(long.Parse(first[0], System.Globalization.CultureInfo.InvariantCulture)), minute[0].Time);
            Assert.AreEqual(decimal.Parse(first[1], System.Globalization.CultureInfo.InvariantCulture) / 10000m, minute[0].Open, "raw prices, deci-cents scaled");
            Assert.IsTrue(minute.Zip(minute.Skip(1)).All(p => p.First.Time < p.Second.Time));

            var daily = history.Get(SecurityType.Equity, "usa", "SPY", Resolution.Daily, TickType.Trade, new DateOnly(2013, 10, 7), new DateOnly(2013, 10, 11));
            Assert.AreEqual(5, daily!.Count);
            foreach (var bar in daily)
            {
                ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("market-data.openapi.yaml", "Bar", ContractSchemas.ToNode(bar)), "bar");
            }
            var quotes = history.Get(SecurityType.Forex, "oanda", "EURUSD", Resolution.Minute, TickType.Quote, new DateOnly(2014, 5, 7), new DateOnly(2014, 5, 7));
            Assert.Greater(quotes!.Count, 0);
            Assert.IsNull(quotes[0].Volume);
        }

        [Test]
        public async Task LiveFanoutPublishesFakeDataQueueTicksOnMarketDataTopics()
        {
            var bus = new InMemoryMessageBus();
            bus.DeclareQueue("md.tests");
            bus.Bind(Exchanges.MarketDataLive, "md.tests", "md.equity.usa.*.v1");
            using var fanout = new LiveDataFanout(bus, new LiveFanoutOptions
            {
                Symbols = new() { "Equity/usa/SYNLVA", "Forex/oanda/SYNFXB" }, Resolution = Resolution.Tick, DataPointsPerSecond = 3,
            }, NullLogger<LiveDataFanout>.Instance);
            fanout.Start();
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            await fanout.FlushAsync(CancellationToken.None);

            var published = bus.Published.Where(p => p.Exchange == Exchanges.MarketDataLive).ToList();
            CollectionAssert.AreEquivalent(new[] { "md.equity.usa.synlva.v1", "md.forex.oanda.synfxb.v1" }, published.Select(p => p.RoutingKey).Distinct());
            Assert.Greater(bus.Peek("md.tests").Count, 0, "workers bind their own queues to md.<securityType>.<market>.<ticker>.v1");
            Assert.IsTrue(bus.Peek("md.tests").All(m => m.RoutingKey == "md.equity.usa.synlva.v1"));
            foreach (var message in published)
            {
                Assert.AreEqual(MessageTypes.LiveData, message.Envelope.Type);
                Assert.AreEqual(MessageEnvelope.Transient, message.Envelope.DeliveryMode);
                Assert.AreEqual(fanout.FeedId, message.Envelope.CorrelationId);
                ContractSchemas.AssertValid(ContractSchemas.ValidateMessage("marketdata.live.schema.json", JsonNode.Parse(message.Body.Span)), message.RoutingKey);
            }
            var equity = published.Where(p => p.RoutingKey.Contains("synlva")).Select(p => ContractJson.Deserialize<LiveDataBatch>(p.Body.Span)).ToList();
            var points = equity.SelectMany(b => b.Points).ToList();
            Assert.IsTrue(points.Any(p => p.TickType == ContractTickType.Trade) && points.Any(p => p.TickType == ContractTickType.Quote), "equity streams trades and quotes");
            Assert.IsTrue(points.All(p => p.DataType == LiveDataType.Tick));
            CollectionAssert.AreEqual(Enumerable.Range(0, equity.Count).Select(i => (long)i), equity.Select(b => b.Sequence));
            Assert.AreEqual("SYNLVA", equity[0].Symbol.Ticker);
        }

        [Test]
        public async Task LiveFanoutPublishesAggregatedBars()
        {
            var bus = new InMemoryMessageBus();
            using var fanout = new LiveDataFanout(bus, new LiveFanoutOptions { Symbols = new() { "Equity/usa/SYNLVB" }, Resolution = Resolution.Second, DataPointsPerSecond = 5 },
                NullLogger<LiveDataFanout>.Instance);
            fanout.Start();
            var points = new List<LiveDataPoint>();
            for (var i = 0; i < 8 && !(points.Any(p => p.DataType == LiveDataType.TradeBar) && points.Any(p => p.DataType == LiveDataType.QuoteBar)); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                await fanout.FlushAsync(CancellationToken.None);
                points = bus.Published.SelectMany(p => ContractJson.Deserialize<LiveDataBatch>(p.Body.Span).Points).ToList();
            }
            var bar = points.First(p => p.DataType == LiveDataType.TradeBar);
            Assert.AreEqual(TimeSpan.FromSeconds(1), bar.EndTime - bar.Time);
            Assert.IsTrue(points.Any(p => p.DataType == LiveDataType.QuoteBar && p.BidPrice < p.AskPrice));
        }

        [Test]
        public async Task HttpApiServesCatalogCoverageHistoryAndFetches()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["MarketData:DataFolder"] = RepoData;
            builder.Configuration["MarketData:RunTasks"] = "false";
            builder.Configuration["MarketData:Store"] = "memory";
            builder.Configuration["MarketData:Bus"] = "memory";
            builder.AddLeanServiceDefaults();
            builder.AddMarketData();
            await using var app = builder.Build();
            app.MapMarketData();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };

            var catalog = JsonNode.Parse(await client.GetStringAsync("/internal/v1/data/catalog?securityType=Equity&market=usa&resolution=Daily"))!.AsArray();
            var spy = catalog.Single(e => e!["ticker"]!.GetValue<string>() == "SPY")!;
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("market-data.openapi.yaml", "CatalogEntry", spy), "catalog");

            var coverage = JsonNode.Parse(await client.GetStringAsync("/internal/v1/data/coverage?securityType=Equity&market=usa&ticker=SPY&resolution=Minute&start=2013-10-07&end=2013-10-11"))!;
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("market-data.openapi.yaml", "Coverage", coverage), "coverage");
            Assert.AreEqual("2013-10-07", coverage["ranges"]![0]!["start"]!.GetValue<string>());
            Assert.AreEqual("2013-10-11", coverage["ranges"]![0]!["end"]!.GetValue<string>());
            Assert.AreEqual(0, coverage["missing"]!.AsArray().Count);

            var history = JsonNode.Parse(await client.GetStringAsync("/internal/v1/data/history?securityType=Equity&market=usa&ticker=SPY&resolution=Daily&start=2013-10-07&end=2013-10-11"))!.AsArray();
            Assert.AreEqual(5, history.Count);
            Assert.AreEqual("2013-10-07T13:30:00Z", history[0]!["time"]!.GetValue<string>());

            var bad = await client.GetAsync("/internal/v1/data/history?securityType=Stock&market=usa&ticker=SPY&resolution=Daily&start=2013-10-07&end=2013-10-11");
            Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.AreEqual("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("common.yaml", "Problem", JsonNode.Parse(await bad.Content.ReadAsStringAsync())!), "problem");
            var missing = await client.GetAsync("/internal/v1/data/history?securityType=Equity&market=usa&ticker=NOSUCHSYN&resolution=Daily&start=2013-10-07&end=2013-10-11");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

            var body = """{"fetch":{"mode":"generate-synthetic","securityType":"Equity","market":"usa","tickers":["SYNHTTP"],"resolution":"Daily","startDate":"2013-10-07","endDate":"2013-10-11"}}""";
            using var post = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/data/fetches") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            post.Headers.Add("Idempotency-Key", "http-1");
            var accepted = await client.SendAsync(post);
            Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
            var task = JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!;
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("market-data.openapi.yaml", "FetchTask", task), "task");
            Assert.AreEqual("queued", task["status"]!.GetValue<string>());

            var id = task["id"]!.GetValue<string>();
            Assert.AreEqual(id, JsonNode.Parse(await client.GetStringAsync($"/internal/v1/data/fetches/{id}"))!["id"]!.GetValue<string>());
            var page = JsonNode.Parse(await client.GetStringAsync("/internal/v1/data/fetches?status=queued&limit=10"))!;
            Assert.AreEqual(id, page["items"]![0]!["id"]!.GetValue<string>());
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/v1/data/fetches/{Guid.NewGuid()}")).StatusCode);

            var invalid = await client.PostAsync("/internal/v1/data/fetches", new StringContent("""{"fetch":{"mode":"download","securityType":"Equity","market":"usa","resolution":"Daily"}}""", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            await app.StopAsync();
        }

        private static List<string> ReadZipLines(string zip)
        {
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.Entries.Single().Open());
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        }

        private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    }
}
