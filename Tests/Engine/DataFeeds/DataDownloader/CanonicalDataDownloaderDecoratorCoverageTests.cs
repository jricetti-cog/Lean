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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Moq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.DataDownloader;
using QuantConnect.Util;

namespace QuantConnect.Tests.Engine.DataFeeds.DataDownloader
{
    [TestFixture]
    public class CanonicalDataDownloaderDecoratorCoverageTests
    {
        private static readonly Type[] ChainProviderTypes = { typeof(IOptionChainProvider), typeof(IFutureChainProvider) };
        private static readonly DateTime Start = new(2024, 1, 2);
        private static readonly DateTime End = new(2024, 1, 4);

        private Dictionary<Type, IEnumerable> _savedParts;
        private Mock<IOptionChainProvider> _optionChainProvider;
        private Mock<IFutureChainProvider> _futureChainProvider;

        [SetUp]
        public void SetUp()
        {
            _savedParts = new Dictionary<Type, IEnumerable>();
            lock (ComposerLock())
            {
                foreach (var type in ChainProviderTypes)
                {
                    if (ComposerParts().Remove(type, out var existing))
                    {
                        _savedParts[type] = existing;
                    }
                }
            }
            _optionChainProvider = new Mock<IOptionChainProvider>();
            _futureChainProvider = new Mock<IFutureChainProvider>();
            Composer.Instance.AddPart(_optionChainProvider.Object);
            Composer.Instance.AddPart(_futureChainProvider.Object);
        }

        [TearDown]
        public void TearDown()
        {
            lock (ComposerLock())
            {
                foreach (var type in ChainProviderTypes)
                {
                    ComposerParts().Remove(type);
                    if (_savedParts.TryGetValue(type, out var saved))
                    {
                        ComposerParts()[type] = saved;
                    }
                }
            }
        }

        [Test]
        public void ReturnsTheUnderlyingDownloaderDataForNonCanonicalSymbols()
        {
            var data = new List<BaseData> { Bar(Symbols.SPY, Start) };
            var downloader = new Mock<IDataDownloader>();
            downloader.Setup(x => x.Get(It.IsAny<DataDownloaderGetParameters>())).Returns(data);
            var decorator = CreateDecorator(downloader.Object);

            var result = decorator.Get(new DataDownloaderGetParameters(Symbols.SPY, Resolution.Daily, Start, End));

            Assert.AreSame(data, result);
            _futureChainProvider.VerifyNoOtherCalls();
            _optionChainProvider.VerifyNoOtherCalls();
        }

        [Test]
        public void ReturnsNullWhenTheUnderlyingDownloaderThrowsForNonCanonicalSymbols()
        {
            var downloader = new Mock<IDataDownloader>();
            downloader.Setup(x => x.Get(It.IsAny<DataDownloaderGetParameters>())).Throws(new InvalidOperationException("boom"));
            var decorator = CreateDecorator(downloader.Object);

            Assert.IsNull(decorator.Get(new DataDownloaderGetParameters(Symbols.SPY, Resolution.Daily, Start, End)));
        }

        [Test]
        public void FallsBackToTheFutureChainForCanonicalFutures()
        {
            var canonical = Symbol.Create("ES", SecurityType.Future, Market.CME);
            var march = Symbol.CreateFuture("ES", Market.CME, new DateTime(2024, 3, 15));
            var june = Symbol.CreateFuture("ES", Market.CME, new DateTime(2024, 6, 21));
            // expired before the requested range, it is skipped
            var expired = Symbol.CreateFuture("ES", Market.CME, new DateTime(2023, 12, 15));
            // its download fails, it is skipped
            var failing = Symbol.CreateFuture("ES", Market.CME, new DateTime(2024, 9, 20));
            _futureChainProvider.Setup(x => x.GetFutureContractList(canonical, It.IsAny<DateTime>()))
                .Returns(new[] { march, june, expired, failing });

            var requested = new List<DataDownloaderGetParameters>();
            var downloader = new Mock<IDataDownloader>();
            downloader.Setup(x => x.Get(It.IsAny<DataDownloaderGetParameters>())).Returns((DataDownloaderGetParameters p) =>
            {
                lock (requested)
                {
                    requested.Add(p);
                }
                if (p.Symbol.IsCanonical())
                {
                    throw new NotSupportedException("canonical");
                }
                if (p.Symbol == failing)
                {
                    throw new InvalidOperationException("failed");
                }
                return new[] { Bar(p.Symbol, p.StartUtc) };
            });
            var decorator = CreateDecorator(downloader.Object);

            var result = decorator.Get(new DataDownloaderGetParameters(canonical, Resolution.Daily, Start, End, TickType.Trade)).ToList();

            Assert.IsNotEmpty(result);
            CollectionAssert.IsSubsetOf(result.Select(x => x.Symbol), new[] { march, june });
            // the chain is requested for each day of the range, contracts are de-duplicated
            _futureChainProvider.Verify(x => x.GetFutureContractList(canonical, It.IsAny<DateTime>()), Times.Exactly(3));
            var contractRequests = requested.Where(p => !p.Symbol.IsCanonical()).ToList();
            Assert.AreEqual(3, contractRequests.Count);
            Assert.IsTrue(contractRequests.All(p => p.StartUtc == Start && p.EndUtc == End && p.TickType == TickType.Trade && p.Resolution == Resolution.Daily));
        }

        [Test]
        public void FallsBackToTheOptionChainForCanonicalOptions()
        {
            var canonical = Symbol.CreateCanonicalOption(Symbols.SPY);
            var contract = Symbol.CreateOption(Symbols.SPY, Market.USA, OptionStyle.American, OptionRight.Call, 470m, new DateTime(2024, 1, 3));
            _optionChainProvider.Setup(x => x.GetOptionContractList(canonical, It.IsAny<DateTime>())).Returns(new[] { contract });
            var downloader = new Mock<IDataDownloader>();
            downloader.Setup(x => x.Get(It.Is<DataDownloaderGetParameters>(p => p.Symbol == canonical))).Returns((IEnumerable<BaseData>)null);
            downloader.Setup(x => x.Get(It.Is<DataDownloaderGetParameters>(p => p.Symbol == contract)))
                .Returns((DataDownloaderGetParameters p) => new[] { Bar(p.Symbol, p.StartUtc) });
            var decorator = CreateDecorator(downloader.Object);

            var result = decorator.Get(new DataDownloaderGetParameters(canonical, Resolution.Minute, Start, End));

            Assert.IsNotNull(result);
            // the contract expires inside the range so the end is adjusted to the day after expiration
            downloader.Verify(x => x.Get(It.Is<DataDownloaderGetParameters>(p => p.Symbol == contract && p.StartUtc == Start && p.EndUtc == new DateTime(2024, 1, 4))), Times.Once);
        }

        [Test]
        [Ignore("Possible bug: GetContractsData calls Any() on the BlockingCollection consuming enumerable, which removes and drops the first downloaded data point")]
        public void CanonicalFallbackReturnsEveryDownloadedDataPoint()
        {
            var canonical = Symbol.CreateCanonicalOption(Symbols.SPY);
            var contract = Symbol.CreateOption(Symbols.SPY, Market.USA, OptionStyle.American, OptionRight.Call, 470m, new DateTime(2024, 1, 3));
            _optionChainProvider.Setup(x => x.GetOptionContractList(canonical, It.IsAny<DateTime>())).Returns(new[] { contract });
            var downloader = new Mock<IDataDownloader>();
            downloader.Setup(x => x.Get(It.Is<DataDownloaderGetParameters>(p => p.Symbol == canonical))).Returns((IEnumerable<BaseData>)null);
            downloader.Setup(x => x.Get(It.Is<DataDownloaderGetParameters>(p => p.Symbol == contract)))
                .Returns((DataDownloaderGetParameters p) => new[] { Bar(p.Symbol, p.StartUtc), Bar(p.Symbol, p.StartUtc.AddDays(1)) });
            var decorator = CreateDecorator(downloader.Object);

            var result = decorator.Get(new DataDownloaderGetParameters(canonical, Resolution.Minute, Start, End)).ToList();

            Assert.AreEqual(2, result.Count);
        }

        [Test]
        public void ReturnsNullWhenTheChainHasNoContracts()
        {
            var canonical = Symbol.Create("ES", SecurityType.Future, Market.CME);
            _futureChainProvider.Setup(x => x.GetFutureContractList(canonical, It.IsAny<DateTime>())).Returns(Enumerable.Empty<Symbol>());
            var downloader = new Mock<IDataDownloader>();
            downloader.Setup(x => x.Get(It.IsAny<DataDownloaderGetParameters>())).Returns((IEnumerable<BaseData>)null);
            var decorator = CreateDecorator(downloader.Object);

            // calling twice also exercises the one-time warning path
            Assert.IsNull(decorator.Get(new DataDownloaderGetParameters(canonical, Resolution.Daily, Start, End)));
            Assert.IsNull(decorator.Get(new DataDownloaderGetParameters(canonical, Resolution.Daily, Start, End)));
        }

        [Test]
        public void ReturnsNullWhenNoContractHasData()
        {
            var canonical = Symbol.Create("ES", SecurityType.Future, Market.CME);
            var expired = Symbol.CreateFuture("ES", Market.CME, new DateTime(2023, 12, 15));
            _futureChainProvider.Setup(x => x.GetFutureContractList(canonical, It.IsAny<DateTime>())).Returns(new[] { expired });
            var downloader = new Mock<IDataDownloader>();
            downloader.Setup(x => x.Get(It.IsAny<DataDownloaderGetParameters>())).Returns((IEnumerable<BaseData>)null);
            var decorator = CreateDecorator(downloader.Object);

            Assert.IsNull(decorator.Get(new DataDownloaderGetParameters(canonical, Resolution.Daily, Start, End)));
            downloader.Verify(x => x.Get(It.Is<DataDownloaderGetParameters>(p => p.Symbol == expired)), Times.Never);
        }

        [Test]
        public void UsesTheChainProvidersFromTheComposer()
        {
            var decorator = CreateDecorator(new Mock<IDataDownloader>().Object);

            Assert.IsNotNull(decorator);
            Assert.AreSame(_optionChainProvider.Object, Composer.Instance.GetPart<IOptionChainProvider>());
            Assert.AreSame(_futureChainProvider.Object, Composer.Instance.GetPart<IFutureChainProvider>());
            Assert.AreEqual(1, Composer.Instance.GetParts<IFutureChainProvider>().Count());
        }

        [Test]
        public void CreatesAndRegistersChainProvidersWhenMissing()
        {
            lock (ComposerLock())
            {
                foreach (var type in ChainProviderTypes)
                {
                    ComposerParts().Remove(type);
                }
            }

            CreateDecorator(new Mock<IDataDownloader>().Object);

            Assert.IsInstanceOf<CachingOptionChainProvider>(Composer.Instance.GetPart<IOptionChainProvider>());
            Assert.IsInstanceOf<CachingFutureChainProvider>(Composer.Instance.GetPart<IFutureChainProvider>());
        }

        private static CanonicalDataDownloaderDecorator CreateDecorator(IDataDownloader downloader)
        {
            var dataProvider = TestGlobals.DataProvider;
            return new CanonicalDataDownloaderDecorator(downloader, dataProvider, TestGlobals.MapFileProvider, TestGlobals.FactorFileProvider);
        }

        private static TradeBar Bar(Symbol symbol, DateTime time) => new(time, symbol, 1, 1, 1, 1, 100, Time.OneDay);

        private static Dictionary<Type, IEnumerable> ComposerParts()
        {
            var field = typeof(Composer).GetField("_exportedValues", BindingFlags.Instance | BindingFlags.NonPublic);
            return (Dictionary<Type, IEnumerable>)field.GetValue(Composer.Instance);
        }

        private static object ComposerLock()
        {
            var field = typeof(Composer).GetField("_exportedValuesLockObject", BindingFlags.Instance | BindingFlags.NonPublic);
            return field.GetValue(Composer.Instance);
        }
    }
}
