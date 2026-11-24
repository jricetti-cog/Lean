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
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture]
    public class DataFeedsCoverageTests
    {
        [Test]
        public void NullDataFeedThrowsByDefault()
        {
            var feed = new NullDataFeed();

            Assert.IsTrue(feed.ShouldThrow);
            Assert.Throws<NotImplementedException>(() => _ = feed.IsActive);
            Assert.Throws<NotImplementedException>(() => feed.Initialize(null, null, null, null, null, null, null, null, null));
            Assert.Throws<NotImplementedException>(() => feed.CreateSubscription(null));
            Assert.Throws<NotImplementedException>(() => feed.RemoveSubscription(null));
            Assert.Throws<NotImplementedException>(() => feed.Exit());
        }

        [Test]
        public void NullDataFeedIsANoOpWhenNotThrowing()
        {
            var feed = new NullDataFeed { ShouldThrow = false };

            Assert.IsTrue(feed.IsActive);
            Assert.DoesNotThrow(() => feed.Initialize(null, null, null, null, null, null, null, null, null));
            Assert.IsNull(feed.CreateSubscription(null));
            Assert.DoesNotThrow(() => feed.RemoveSubscription(null));
            Assert.DoesNotThrow(() => feed.Exit());
        }

        [Test]
        public void CreateStreamReaderErrorEventArgsExposesItsValues()
        {
            var source = new SubscriptionDataSource("some/file.csv", SubscriptionTransportMedium.LocalFile);
            var date = new DateTime(2024, 1, 3);

            var args = new CreateStreamReaderErrorEventArgs(date, source);

            Assert.AreEqual(date, args.Date);
            Assert.AreSame(source, args.Source);
        }

        [Test]
        public void ReaderErrorEventArgsExposesItsValues()
        {
            var exception = new FormatException("bad line");

            var args = new ReaderErrorEventArgs("1,2,3", exception);

            Assert.AreEqual("1,2,3", args.Line);
            Assert.AreSame(exception, args.Exception);
        }

        [TestFixture]
        public class CompositeDataProviderTests
        {
            private const string ConfigKey = "composite-data-providers";
            private string _previousConfig;
            private string _tempDirectory;

            [SetUp]
            public void SetUp()
            {
                _previousConfig = Config.Get(ConfigKey);
                _tempDirectory = Path.Combine(Path.GetTempPath(), "CompositeDataProviderTests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDirectory);
            }

            [TearDown]
            public void TearDown()
            {
                Config.Set(ConfigKey, _previousConfig);
                Directory.Delete(_tempDirectory, true);
            }

            [Test]
            public void ThrowsWithoutConfiguration()
            {
                Config.Set(ConfigKey, "");

                var exception = Assert.Throws<ArgumentException>(() => new CompositeDataProvider());
                StringAssert.Contains("requires 'composite-data-providers'", exception.Message);
            }

            [Test]
            public void ThrowsWithAnEmptyProviderList()
            {
                Config.Set(ConfigKey, "[]");

                var exception = Assert.Throws<ArgumentException>(() => new CompositeDataProvider());
                StringAssert.Contains("requires at least 1 valid data provider", exception.Message);
            }

            [Test]
            public void FetchesFromTheConfiguredProvidersAndForwardsRequests()
            {
                Config.Set(ConfigKey, "[\"DefaultDataProvider\"]");
                var path = Path.Combine(_tempDirectory, "data.csv");
                File.WriteAllText(path, "content");
                var provider = new CompositeDataProvider();
                var requests = new List<DataProviderNewDataRequestEventArgs>();
                provider.NewDataRequest += (_, e) => requests.Add(e);

                using (var stream = provider.Fetch(path))
                using (var reader = new StreamReader(stream))
                {
                    Assert.AreEqual("content", reader.ReadToEnd());
                }
                Assert.AreEqual(1, requests.Count);
                Assert.IsTrue(requests[0].Succeeded);
                Assert.AreEqual(path, requests[0].Path);

                Assert.IsNull(provider.Fetch(Path.Combine(_tempDirectory, "missing.csv")));
                Assert.AreEqual(2, requests.Count);
                Assert.IsFalse(requests[1].Succeeded);
            }
        }
    }
}
