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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Custom;
using QuantConnect.Data.Custom.IconicTypes;
using QuantConnect.Data.Custom.Intrinio;
using QuantConnect.Data.Custom.Tiingo;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Util;

namespace QuantConnect.Tests.Common.Data.Custom
{
    [TestFixture]
    public class CustomDataCoverageTests
    {
        private static readonly DateTime Date = new DateTime(2020, 1, 2);

        private static SubscriptionDataConfig GetConfig(Type type, Symbol symbol, Resolution resolution = Resolution.Daily)
        {
            return new SubscriptionDataConfig(type, symbol, resolution, TimeZones.Utc, TimeZones.Utc, false, false, false);
        }

        private static void AssertDailySparseUtc(BaseData data, bool requiresMapping)
        {
            Assert.IsTrue(data.IsSparseData());
            Assert.AreEqual(requiresMapping, data.RequiresMapping());
            Assert.AreEqual(TimeZones.Utc, data.DataTimeZone());
            Assert.AreEqual(Resolution.Daily, data.DefaultResolution());
            CollectionAssert.AreEqual(new[] { Resolution.Daily }, data.SupportedResolutions());
        }

        [Test]
        public void LinkedDataUsesUnderlyingTickerAndCreatesInstances()
        {
            var symbol = Symbol.CreateBase(typeof(LinkedData), Symbols.SPY, QuantConnect.Market.USA);
            var config = GetConfig(typeof(LinkedData), symbol);
            var data = new LinkedData();

            var source = data.GetSource(config, Date, false);
            Assert.AreEqual(Path.Combine("TestData", "linked", "spy.csv"), source.Source);
            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, source.TransportMedium);
            Assert.AreEqual(FileFormat.Csv, source.Format);

            var read = (LinkedData)data.Reader(config, "line", Date, false);
            Assert.AreEqual(10, read.Count);
            Assert.AreEqual(symbol, read.Symbol);
            Assert.AreEqual(Date, read.EndTime);
            AssertDailySparseUtc(data, true);
        }

        [Test]
        public void IndexedLinkedDataSources()
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Base, QuantConnect.Market.USA);
            var config = GetConfig(typeof(IndexedLinkedData), symbol);
            var data = new IndexedLinkedData();

            var index = data.GetSource(config, Date, false);
            Assert.AreEqual(Path.Combine("TestData", "indexlinked", "aapl", "20200102.csv"), index.Source);
            Assert.AreEqual(FileFormat.Index, index.Format);

            var content = data.GetSourceForAnIndex(config, Date, "file.csv", false);
            Assert.AreEqual(Path.Combine("TestData", "indexlinked", "content", "20200102.zip#file.csv"), content.Source);
            Assert.AreEqual(FileFormat.Csv, content.Format);
            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, content.TransportMedium);

            var read = (IndexedLinkedData)data.Reader(config, "line", Date, false);
            Assert.AreEqual(10, read.Count);
            Assert.AreEqual(symbol, read.Symbol);
            Assert.AreEqual(Date, read.EndTime);
            AssertDailySparseUtc(data, true);
        }

        [Test]
        public void IndexedLinkedData2SourcesAndLiveModeNotSupported()
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Base, QuantConnect.Market.USA);
            var config = GetConfig(typeof(IndexedLinkedData2), symbol);
            var data = new IndexedLinkedData2();

            var index = data.GetSource(config, Date, false);
            Assert.AreEqual(Path.Combine("TestData", "indexlinked2", "aapl", "20200102.csv"), index.Source);
            Assert.AreEqual(FileFormat.Index, index.Format);
            Assert.Throws<NotImplementedException>(() => data.GetSource(config, Date, true));

            var content = data.GetSourceForAnIndex(config, Date, "x.csv", false);
            Assert.AreEqual(Path.Combine("TestData", "indexlinked2", "content", "20200102.zip#x.csv"), content.Source);

            var read = (IndexedLinkedData2)data.Reader(config, "line", Date, false);
            Assert.AreEqual(10, read.Count);
            Assert.AreEqual(symbol, read.Symbol);
            Assert.AreEqual(Date, read.EndTime);
            AssertDailySparseUtc(data, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnlinkedDataRespectsAnyTicker(bool anyTicker)
        {
            var previous = UnlinkedData.AnyTicker;
            try
            {
                UnlinkedData.AnyTicker = anyTicker;
                var symbol = Symbol.Create("IBM", SecurityType.Base, QuantConnect.Market.USA);
                var config = GetConfig(typeof(UnlinkedData), symbol);
                var data = new UnlinkedData();

                var source = data.GetSource(config, Date, false);
                Assert.AreEqual(Path.Combine("TestData", "unlinked", anyTicker ? "data.csv" : "ibm.csv"), source.Source);

                var read = (UnlinkedData)data.Reader(config, "line", Date, false);
                Assert.AreEqual(anyTicker ? "ANY" : "IBM", read.Ticker);
                Assert.AreEqual(symbol, read.Symbol);
                Assert.AreEqual(Date, read.EndTime);
                AssertDailySparseUtc(data, false);
            }
            finally
            {
                UnlinkedData.AnyTicker = previous;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnlinkedDataTradeBarRespectsAnyTicker(bool anyTicker)
        {
            var previous = UnlinkedDataTradeBar.AnyTicker;
            try
            {
                UnlinkedDataTradeBar.AnyTicker = anyTicker;
                var symbol = Symbol.Create("IBM", SecurityType.Base, QuantConnect.Market.USA);
                var config = GetConfig(typeof(UnlinkedDataTradeBar), symbol);
                var data = new UnlinkedDataTradeBar();
                Assert.AreEqual(MarketDataType.Base, data.DataType);
                Assert.AreEqual(TimeSpan.FromDays(1), data.Period);

                var source = data.GetSource(config, Date, false);
                Assert.AreEqual(Path.Combine("TestData", "unlinkedtradebar", anyTicker ? "data.csv" : "ibm.csv"), source.Source);

                var read = (UnlinkedDataTradeBar)data.Reader(config, "line", Date, false);
                Assert.AreEqual(1m, read.Open);
                Assert.AreEqual(2m, read.High);
                Assert.AreEqual(1m, read.Low);
                Assert.AreEqual(1.5m, read.Close);
                Assert.AreEqual(0m, read.Volume);
                Assert.AreEqual(symbol, read.Symbol);
                AssertDailySparseUtc(data, false);
            }
            finally
            {
                UnlinkedDataTradeBar.AnyTicker = previous;
            }
        }

        [TestCase(Resolution.Minute, "M1", 1)]
        [TestCase(Resolution.Hour, "H1", 2)]
        [TestCase(Resolution.Daily, "D1", 3)]
        public void FxcmVolumeLiveSource(Resolution resolution, string interval, int offerId)
        {
            var ticker = offerId == 1 ? "EURUSD" : offerId == 2 ? "USDJPY" : "GBPUSD";
            var symbol = Symbol.Create(ticker + "_VOL", SecurityType.Base, QuantConnect.Market.FXCM);
            var config = GetConfig(typeof(FxcmVolume), symbol, resolution);

            var source = new FxcmVolume().GetSource(config, Date, true);

            StringAssert.Contains($"interval={interval}", source.Source);
            StringAssert.Contains($"offerID={offerId}", source.Source);
            StringAssert.Contains("sid=quantconnect", source.Source);
            Assert.AreEqual(SubscriptionTransportMedium.Rest, source.TransportMedium);
        }

        [Test]
        public void FxcmVolumeBacktestSourcePaths()
        {
            var symbol = Symbol.Create("EURUSD_VOL", SecurityType.Base, QuantConnect.Market.FXCM);

            var minute = new FxcmVolume().GetSource(GetConfig(typeof(FxcmVolume), symbol, Resolution.Minute), Date, false);
            Assert.AreEqual(Path.Combine(Globals.DataFolder, "forex", "fxcm", "minute", "eurusd", "20200102_volume.zip"), minute.Source);
            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, minute.TransportMedium);

            var hour = new FxcmVolume().GetSource(GetConfig(typeof(FxcmVolume), symbol, Resolution.Hour), Date, false);
            Assert.AreEqual(Path.Combine(Globals.DataFolder, "forex", "fxcm", "hour", "eurusd_volume.zip"), hour.Source);
        }

        [Test]
        public void FxcmVolumeThrowsForUnsupportedTickerOrResolution()
        {
            var unsupported = Symbol.Create("XYZABC", SecurityType.Base, QuantConnect.Market.FXCM);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new FxcmVolume().GetSource(GetConfig(typeof(FxcmVolume), unsupported, Resolution.Minute), Date, true));

            var supported = Symbol.Create("EURUSD", SecurityType.Base, QuantConnect.Market.FXCM);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new FxcmVolume().GetSource(GetConfig(typeof(FxcmVolume), supported, Resolution.Second), Date, true));
        }

        [Test]
        public void FxcmVolumeReadsBacktestLines()
        {
            var symbol = Symbol.Create("EURUSD", SecurityType.Base, QuantConnect.Market.FXCM);

            var minute = (FxcmVolume)new FxcmVolume().Reader(GetConfig(typeof(FxcmVolume), symbol, Resolution.Minute), "60000,1500,25", Date, false);
            Assert.AreEqual(Date.AddMinutes(1), minute.Time);
            Assert.AreEqual(1500, minute.Volume);
            Assert.AreEqual(25, minute.Transactions);
            Assert.AreEqual(1500m, minute.Value);
            Assert.AreEqual(symbol, minute.Symbol);

            var hour = (FxcmVolume)new FxcmVolume().Reader(GetConfig(typeof(FxcmVolume), symbol, Resolution.Hour), "20200102 13:00,700,7", Date, false);
            Assert.AreEqual(new DateTime(2020, 1, 2, 13, 0, 0), hour.Time);
            Assert.AreEqual(700, hour.Volume);
            Assert.AreEqual(7, hour.Transactions);
        }

        [Test]
        public void FxcmVolumeReadsLiveLine()
        {
            var symbol = Symbol.Create("EURUSD", SecurityType.Base, QuantConnect.Market.FXCM);
            var fields = Enumerable.Range(0, 34).Select(i => i.ToStringInvariant()).ToArray();
            fields[0] = "XX:202001021305";
            var line = "header\nheader2\n" + string.Join(";", fields);

            var data = (FxcmVolume)new FxcmVolume().Reader(GetConfig(typeof(FxcmVolume), symbol, Resolution.Minute), line, Date, true);

            Assert.AreEqual(new DateTime(2020, 1, 2, 13, 5, 0), data.Time);
            Assert.AreEqual(26 + 28 + 30 + 32, data.Volume);
            Assert.AreEqual(27 + 29 + 31 + 33, data.Transactions);
            Assert.AreEqual(data.Volume, (long)data.Value);
        }

        [Test]
        public void FxcmVolumeReturnsNullForInvalidLiveLine()
        {
            var symbol = Symbol.Create("EURUSD", SecurityType.Base, QuantConnect.Market.FXCM);
            var data = new FxcmVolume().Reader(GetConfig(typeof(FxcmVolume), symbol, Resolution.Minute), "invalid", Date, true);
            Assert.IsNull(data);
        }

        [Test]
        public void IntrinioConfigRequiresUserAndPassword()
        {
            var user = IntrinioConfig.User;
            var password = IntrinioConfig.Password;
            try
            {
                IntrinioConfig.User = string.Empty;
                IntrinioConfig.Password = string.Empty;
                Assert.IsFalse(IntrinioConfig.IsInitialized);
                Assert.Throws<InvalidOperationException>(() => new IntrinioEconomicData());
                Assert.Throws<InvalidOperationException>(() => IntrinioConfig.SetUserAndPassword("user", " "));

                IntrinioConfig.SetUserAndPassword("user", "pass");
                Assert.IsTrue(IntrinioConfig.IsInitialized);
                Assert.AreEqual("user", IntrinioConfig.User);
                Assert.AreEqual("pass", IntrinioConfig.Password);
            }
            finally
            {
                IntrinioConfig.User = user;
                IntrinioConfig.Password = password;
            }
        }

        [TestCase(IntrinioDataTransformation.Roc, "change")]
        [TestCase(IntrinioDataTransformation.AnnualyRoc, "yr_change")]
        [TestCase(IntrinioDataTransformation.CompoundedAnnualRoc, "c_annual_roc")]
        [TestCase(IntrinioDataTransformation.AnnualyCCRoc, "cc_annual_roc")]
        [TestCase(IntrinioDataTransformation.CCRoc, "cc_roc")]
        [TestCase(IntrinioDataTransformation.Level, "level")]
        [TestCase(IntrinioDataTransformation.Ln, "log")]
        [TestCase(IntrinioDataTransformation.Pc, "percent_change")]
        [TestCase(IntrinioDataTransformation.AnnualyPc, "yr_percent_change")]
        public void IntrinioEconomicDataBuildsSourceOnlyOnceInBacktest(IntrinioDataTransformation transformation, string item)
        {
            var user = IntrinioConfig.User;
            var password = IntrinioConfig.Password;
            var rateGate = IntrinioConfig.RateGate;
            try
            {
                IntrinioConfig.SetUserAndPassword("user", "pass");
                IntrinioConfig.SetTimeIntervalBetweenCalls(TimeSpan.FromMilliseconds(1));
                Assert.AreNotSame(rateGate, IntrinioConfig.RateGate);

                var symbol = Symbol.Create("DCOILWTICO", SecurityType.Base, QuantConnect.Market.USA);
                var config = GetConfig(typeof(IntrinioEconomicData), symbol);
                var data = new IntrinioEconomicData(transformation);

                var first = data.GetSource(config, Date, false);
                Assert.AreEqual($"https://api.intrinio.com/historical_data.csv?identifier=DCOILWTICO&item={item}&sort_order=asc", first.Source);
                Assert.AreEqual(SubscriptionTransportMedium.RemoteFile, first.TransportMedium);
                Assert.AreEqual(FileFormat.Csv, first.Format);
                var header = first.Headers.Single();
                Assert.AreEqual("Authorization", header.Key);
                Assert.AreEqual($"Basic ({Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("user:pass"))})", header.Value);

                var second = data.GetSource(config, Date.AddDays(1), false);
                Assert.AreEqual(string.Empty, second.Source);
                Assert.AreEqual(SubscriptionTransportMedium.LocalFile, second.TransportMedium);
            }
            finally
            {
                IntrinioConfig.RateGate.DisposeSafely();
                IntrinioConfig.RateGate = rateGate;
                IntrinioConfig.User = user;
                IntrinioConfig.Password = password;
            }
        }

        [Test]
        public void IntrinioEconomicDataLiveModeUsesDescendingOrderEveryTime()
        {
            var user = IntrinioConfig.User;
            var password = IntrinioConfig.Password;
            var rateGate = IntrinioConfig.RateGate;
            try
            {
                IntrinioConfig.SetUserAndPassword("user", "pass");
                IntrinioConfig.SetTimeIntervalBetweenCalls(TimeSpan.FromMilliseconds(1));
                var symbol = Symbol.Create("GDP", SecurityType.Base, QuantConnect.Market.USA);
                var config = GetConfig(typeof(IntrinioEconomicData), symbol);
                var data = new IntrinioEconomicData();

                for (var i = 0; i < 2; i++)
                {
                    var source = data.GetSource(config, Date, true);
                    StringAssert.EndsWith("identifier=GDP&item=level&sort_order=desc", source.Source);
                }

                var read = data.Reader(config, "2020-01-03,12.5", Date, false);
                Assert.AreEqual(new DateTime(2020, 1, 4), read.EndTime);
                Assert.AreEqual(12.5m, read.Value);
                Assert.AreEqual(symbol, read.Symbol);

                Assert.IsNull(data.Reader(config, "date,value", Date, false));
            }
            finally
            {
                IntrinioConfig.RateGate.DisposeSafely();
                IntrinioConfig.RateGate = rateGate;
                IntrinioConfig.User = user;
                IntrinioConfig.Password = password;
            }
        }

        [Test]
        public void TiingoAuthCodeIgnoresEmptyValues()
        {
            var previous = Tiingo.AuthCode;
            var previousIsSet = Tiingo.IsAuthCodeSet;
            try
            {
                Tiingo.SetAuthCode("  ");
                Assert.AreEqual(previous, Tiingo.AuthCode);
                Tiingo.SetAuthCode("my-token");
                Assert.AreEqual("my-token", Tiingo.AuthCode);
                Assert.IsTrue(Tiingo.IsAuthCodeSet);
            }
            finally
            {
                typeof(Tiingo).GetProperty(nameof(Tiingo.AuthCode)).SetValue(null, previous);
                typeof(Tiingo).GetProperty(nameof(Tiingo.IsAuthCodeSet)).SetValue(null, previousIsSet);
            }
        }

        [Test]
        public void TiingoSymbolMapperRoundTrips()
        {
            var symbol = Symbol.Create("BRK.A", SecurityType.Equity, QuantConnect.Market.USA);
            Assert.AreEqual("BRK-A", TiingoSymbolMapper.GetTiingoTicker(symbol));
            Assert.AreEqual("BRK.A", TiingoSymbolMapper.GetLeanTicker("BRK-A"));
        }

        [Test]
        public void TiingoPriceSourceKeepsFirstStartDate()
        {
            var symbol = Symbol.Create("BRK.A", SecurityType.Base, QuantConnect.Market.USA);
            var config = GetConfig(typeof(TiingoPrice), symbol);
            var data = new TiingoPrice();

            var first = data.GetSource(config, Date, false);
            var second = data.GetSource(config, Date.AddDays(5), false);

            StringAssert.StartsWith("https://api.tiingo.com/tiingo/daily/BRK-A/prices?startDate=2020-01-02&token=", first.Source);
            Assert.AreEqual(first.Source, second.Source);
            Assert.AreEqual(FileFormat.UnfoldingCollection, first.Format);
            Assert.AreEqual(SubscriptionTransportMedium.RemoteFile, first.TransportMedium);
            Assert.IsTrue(data.RequiresMapping());
            Assert.AreEqual(TimeZones.Utc, data.DataTimeZone());
            Assert.AreEqual(Resolution.Daily, data.DefaultResolution());
            CollectionAssert.AreEqual(new[] { Resolution.Daily }, data.SupportedResolutions());
        }

        [Test]
        public void TiingoPriceReaderParsesJsonCollection()
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Base, QuantConnect.Market.USA);
            var config = GetConfig(typeof(TiingoPrice), symbol);
            var json = "[{\"date\":\"2020-01-02T00:00:00\",\"open\":1,\"high\":3,\"low\":0.5,\"close\":2,\"volume\":100," +
                "\"adjOpen\":1.1,\"adjHigh\":3.1,\"adjLow\":0.6,\"adjClose\":2.1,\"adjVolume\":101,\"divCash\":0.2,\"splitFactor\":1}]";

            var collection = (BaseDataCollection)new TiingoPrice().Reader(config, json, Date, false);

            var item = (TiingoPrice)collection.Data.Single();
            Assert.AreEqual(symbol, item.Symbol);
            Assert.AreEqual(Date, item.Time);
            Assert.AreEqual(Date.AddDays(1), item.EndTime);
            Assert.AreEqual(TimeSpan.FromDays(1), item.Period);
            Assert.AreEqual(2m, item.Value);
            Assert.AreEqual(1m, item.Open);
            Assert.AreEqual(3m, item.High);
            Assert.AreEqual(0.5m, item.Low);
            Assert.AreEqual(100m, item.Volume);
            Assert.AreEqual(1.1m, item.AdjustedOpen);
            Assert.AreEqual(3.1m, item.AdjustedHigh);
            Assert.AreEqual(0.6m, item.AdjustedLow);
            Assert.AreEqual(2.1m, item.AdjustedClose);
            Assert.AreEqual(101, item.AdjustedVolume);
            Assert.AreEqual(0.2m, item.Dividend);
            Assert.AreEqual(1m, item.SplitFactor);

            item.EndTime = Date.AddDays(3);
            Assert.AreEqual(Date.AddDays(2), item.Time);
        }
    }
}
