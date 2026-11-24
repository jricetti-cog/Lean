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
using System.IO;
using System.Reflection;
using Moq;
using NUnit.Framework;
using QuantConnect.Api;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Util;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    /// <summary>
    /// Exercises <see cref="ApiDataProvider"/> against a mocked <see cref="IApi"/>, no network access is performed
    /// </summary>
    [TestFixture]
    public class ApiDataProviderCoverageTests
    {
        private const int EquityPrice = 10;
        private const int ExpensivePrice = 1000;
        private const decimal Balance = 100m;

        private string _organizationId;
        private object _savedApiParts;
        private string _tempDirectory;
        private Mock<IApi> _subscribedApi;
        private Mock<IApi> _unsubscribedApi;
        private ApiDataProvider _subscribedProvider;
        private ApiDataProvider _unsubscribedProvider;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _organizationId = Globals.OrganizationID;
            _savedApiParts = TakeComposerParts(typeof(IApi));
            _tempDirectory = Path.Combine(Path.GetTempPath(), "ApiDataProviderCoverageTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);

            try
            {
                // the constructor reads the organization from the account when none is set
                Globals.OrganizationID = null;
                _subscribedApi = CreateApi(new[] { 37, 137, 172 });
                _subscribedProvider = CreateProvider(_subscribedApi);
                Assert.AreEqual("test-organization", Globals.OrganizationID);

                _unsubscribedApi = CreateApi(Array.Empty<int>());
                _unsubscribedProvider = CreateProvider(_unsubscribedApi);
            }
            finally
            {
                RestoreComposerParts(typeof(IApi), null);
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            Globals.OrganizationID = _organizationId;
            RestoreComposerParts(typeof(IApi), _savedApiParts);
            _subscribedProvider?.Dispose();
            _unsubscribedProvider?.Dispose();
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        [Test]
        public void ConstructorThrowsWhenDataAgreementIsNotSigned()
        {
            var api = CreateApi(new[] { 37 }, signed: false);
            var saved = TakeComposerParts(typeof(IApi));
            try
            {
                Composer.Instance.AddPart(api.Object);
                var exception = Assert.Throws<InvalidOperationException>(() => new ApiDataProvider());
                StringAssert.Contains("https://www.quantconnect.com/terms/data", exception.Message);
            }
            finally
            {
                RestoreComposerParts(typeof(IApi), saved);
            }
        }

        [Test]
        public void FetchDownloadsMissingFilesOnceAndReturnsTheirContent()
        {
            var path = DataPath("equity", "usa", "daily", "fetch_once.zip");

            using (var stream = _subscribedProvider.Fetch(path))
            using (var reader = new StreamReader(stream))
            {
                Assert.AreEqual("downloaded", reader.ReadToEnd());
            }
            // the file now exists and is up to date, no further download
            using (var stream = _subscribedProvider.Fetch(path))
            {
                Assert.IsNotNull(stream);
            }

            _subscribedApi.Verify(api => api.DownloadData(path, "test-organization"), Times.Once);
        }

        [Test]
        public void FetchReturnsNullWhenTheDownloadFails()
        {
            var path = DataPath("equity", "usa", "daily", "fail.zip");

            Assert.IsNull(_subscribedProvider.Fetch(path));
            _subscribedApi.Verify(api => api.DownloadData(path, It.IsAny<string>()), Times.Once);
        }

        [Test]
        public void FetchThrowsWhenThereIsNoPriceForTheFile()
        {
            var path = DataPath("crypto", "coinbase", "daily", "btcusd_trade.zip");

            var exception = Assert.Throws<ArgumentException>(() => _subscribedProvider.Fetch(path));
            StringAssert.Contains("No price found", exception.Message);
        }

        [Test]
        public void FetchThrowsWhenThePriceExceedsThePurchaseLimit()
        {
            // the purchase limit is capped to the organization balance
            var path = DataPath("equity", "usa", "hour", "expensive.zip");

            var exception = Assert.Throws<ArgumentException>(() => _subscribedProvider.Fetch(path));
            StringAssert.Contains($"Cost {ExpensivePrice}", exception.Message);
            StringAssert.Contains("exceeds remaining purchase limit", exception.Message);
        }

        [TestCase("future", "cme", "minute", "es.zip")]
        [TestCase("index", "usa", "daily", "spx.zip")]
        [TestCase("indexoption", "usa", "daily", "spxw.zip")]
        [TestCase("futureoption", "cme", "daily", "es.zip")]
        public void UnsupportedSecurityTypesAreNotDownloaded(string securityType, string market, string resolution, string file)
        {
            var path = DataPath(securityType, market, resolution, file);

            Assert.IsNull(_subscribedProvider.Fetch(path));
            Assert.IsNull(_subscribedProvider.Fetch(path));
            _subscribedApi.Verify(api => api.DownloadData(path, It.IsAny<string>()), Times.Never);
        }

        [TestCase("future", "cme", "map_files", "map_files_20240101.zip")]
        [TestCase("future", "cme", "factor_files", "es.csv")]
        [TestCase("option", "usa", "universes", "spy_20240103.csv")]
        [TestCase("future", "cme", "universes", "es_20240103.csv")]
        public void SupportedAuxiliaryAndUniverseDataIsDownloaded(string securityType, string market, string folder, string file)
        {
            var path = DataPath(securityType, market, folder, file);

            using var stream = _subscribedProvider.Fetch(path);

            Assert.IsNotNull(stream);
            _subscribedApi.Verify(api => api.DownloadData(path, It.IsAny<string>()), Times.Once);
        }

        [Test]
        public void FineFundamentalEquityDataIsNotDownloaded()
        {
            var path = DataPath("equity", "usa", "fundamental", "fine", "aapl", "20240103.zip");

            Assert.IsNull(_subscribedProvider.Fetch(path));
            _subscribedApi.Verify(api => api.DownloadData(path, It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void ExistingUpToDateFilesAreNotDownloaded()
        {
            var path = DataPath("equity", "usa", "daily", "existing.zip");
            File.WriteAllText(path, "existing");

            using (var stream = _unsubscribedProvider.Fetch(path))
            using (var reader = new StreamReader(stream))
            {
                Assert.AreEqual("existing", reader.ReadToEnd());
            }
            _unsubscribedApi.Verify(api => api.DownloadData(path, It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void NullKeyIsNotDownloaded()
        {
            Assert.Throws<ArgumentNullException>(() => _subscribedProvider.Fetch(null));
            _subscribedApi.Verify(api => api.DownloadData(null, It.IsAny<string>()), Times.Never);
        }

        [TestCase("equity", "usa", "daily", "spy.zip", "Equity data")]
        [TestCase("option", "usa", "minute", "spy.zip", "Equity data")]
        [TestCase("equity", "india", "daily", "sbin.zip", "India data")]
        [TestCase("future", "cme", "map_files", "es.zip", "Future auxiliary data")]
        public void MissingMapAndFactorFileSubscriptionThrows(string securityType, string market, string resolution, string file, string expected)
        {
            var path = DataPath(securityType, market, resolution, file);

            var exception = Assert.Throws<ArgumentException>(() => _unsubscribedProvider.Fetch(path));

            StringAssert.Contains("Must be subscribed to map and factor files", exception.Message);
            StringAssert.Contains(expected, exception.Message);
            _unsubscribedApi.Verify(api => api.DownloadData(path, It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void DataWithoutMapAndFactorFileRequirementsIsDownloadedWithoutSubscription()
        {
            var path = DataPath("forex", "oanda", "daily", "eurusd.zip");

            using var stream = _unsubscribedProvider.Fetch(path);

            Assert.IsNotNull(stream);
            _unsubscribedApi.Verify(api => api.DownloadData(path, It.IsAny<string>()), Times.Once);
        }

        private string DataPath(params string[] parts)
        {
            var path = Path.Combine(_tempDirectory, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }

        private static ApiDataProvider CreateProvider(Mock<IApi> api)
        {
            Composer.Instance.AddPart(api.Object);
            try
            {
                return new ApiDataProvider();
            }
            finally
            {
                TakeComposerParts(typeof(IApi));
            }
        }

        private static Mock<IApi> CreateApi(int[] productIds, bool signed = true)
        {
            var api = new Mock<IApi>();
            api.Setup(x => x.ReadAccount(It.IsAny<string>())).Returns(new Account { OrganizationId = "test-organization" });
            api.Setup(x => x.ReadDataPrices(It.IsAny<string>())).Returns(new DataPricesList
            {
                AgreementUrl = "https://www.quantconnect.com/terms/data",
                Prices = new List<PriceEntry>
                {
                    new() { RawRegEx = "equity/usa/hour/", Price = ExpensivePrice },
                    new() { RawRegEx = "(equity|option|forex|future)/", Price = EquityPrice },
                }
            });
            var items = new List<ProductItem>();
            foreach (var id in productIds)
            {
                items.Add(new ProductItem { Id = id });
            }
            api.Setup(x => x.ReadOrganization(It.IsAny<string>())).Returns(new Organization
            {
                DataAgreement = new DataAgreement { Signed = signed, EpochSignedTime = 1704067200 },
                Products = new List<Product>
                {
                    new() { Type = ProductType.Data, Items = items },
                    new() { Type = ProductType.Support, Items = new List<ProductItem> { new() { Id = 37 } } },
                },
                Credit = new Credit { Balance = Balance }
            });
            api.Setup(x => x.DownloadData(It.IsAny<string>(), It.IsAny<string>())).Returns((string filePath, string _) =>
            {
                if (Path.GetFileName(filePath) == "fail.zip")
                {
                    return false;
                }
                File.WriteAllText(filePath, "downloaded");
                return true;
            });
            return api;
        }

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

        private static object TakeComposerParts(Type type)
        {
            var parts = ComposerParts();
            lock (ComposerLock())
            {
                if (parts.Remove(type, out var existing))
                {
                    return existing;
                }
                return null;
            }
        }

        private static void RestoreComposerParts(Type type, object saved)
        {
            var parts = ComposerParts();
            lock (ComposerLock())
            {
                parts.Remove(type);
                if (saved != null)
                {
                    parts[type] = (IEnumerable)saved;
                }
            }
        }
    }
}
