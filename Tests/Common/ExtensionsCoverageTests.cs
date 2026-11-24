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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace QuantConnect.Tests.Common
{
    [TestFixture]
    public class ExtensionsCoverageTests
    {
        [TestCase(0ul, "")]
        [TestCase(35ul, "Z")]
        [TestCase(36ul, "10")]
        [TestCase(1295ul, "ZZ")]
        public void Base36Encoding(ulong value, string expected)
        {
            Assert.AreEqual(expected, value.EncodeBase36());
            Assert.AreEqual(value, expected.DecodeBase36());
        }

        [Test]
        public void Base36RoundTripsLargeValues()
        {
            const ulong value = 1234567890123456789ul;
            Assert.AreEqual(value, value.EncodeBase36().DecodeBase36());
        }

        [Test]
        public void Md5HashAndHexString()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
            var hash = stream.GetMD5Hash();
            Assert.AreEqual("900150983cd24fb0d6963f7d28e17f72", hash.ToHexString().ToLowerInvariant());
            Assert.Throws<ArgumentException>(() => Array.Empty<byte>().ToHexString());
            Assert.Throws<ArgumentException>(() => ((byte[])null).ToHexString());
        }

        [TestCase(double.NaN, true, true)]
        [TestCase(double.PositiveInfinity, true, false)]
        [TestCase(0d, false, true)]
        [TestCase(1.5d, false, false)]
        public void NaNChecks(double value, bool nanOrInfinity, bool nanOrZero)
        {
            Assert.AreEqual(nanOrInfinity, value.IsNaNOrInfinity());
            Assert.AreEqual(nanOrZero, value.IsNaNOrZero());
        }

        [Test]
        public void LazyCaseConversionsReuseInstanceWhenPossible()
        {
            const string upper = "ABC";
            const string lower = "abc";
            Assert.AreSame(upper, upper.LazyToUpper());
            Assert.AreSame(lower, lower.LazyToLower());
            Assert.AreEqual("ABC", "aBc".LazyToUpper());
            Assert.AreEqual("abc", "aBc".LazyToLower());
        }

        [Test]
        public void EnumToLowerHelpers()
        {
            foreach (SecurityType securityType in Enum.GetValues(typeof(SecurityType)))
            {
                Assert.AreEqual(securityType.ToString().ToLowerInvariant(), securityType.SecurityTypeToLower());
            }
            foreach (Resolution resolution in Enum.GetValues(typeof(Resolution)))
            {
                Assert.AreEqual(resolution.ToString().ToLowerInvariant(), resolution.ResolutionToLower());
            }
            foreach (TickType tickType in Enum.GetValues(typeof(TickType)))
            {
                Assert.AreEqual(tickType.ToString().ToLowerInvariant(), tickType.TickTypeToLower());
            }
            Assert.AreEqual("call", OptionRight.Call.OptionRightToLower());
            Assert.AreEqual("put", OptionRight.Put.OptionRightToLower());
            Assert.AreEqual("Call", OptionRight.Call.ToStringPerformance());
            Assert.AreEqual("Put", OptionRight.Put.ToStringPerformance());
            Assert.AreEqual("american", OptionStyle.American.OptionStyleToLower());
            Assert.AreEqual("european", OptionStyle.European.OptionStyleToLower());
            Assert.Throws<ArgumentException>(() => ((OptionRight)99).OptionRightToLower());
            Assert.Throws<ArgumentException>(() => ((OptionStyle)99).OptionStyleToLower());
        }

        [Test]
        public void ParseOptionRightAndStyle()
        {
            Assert.AreEqual(OptionRight.Call, "Call".ParseOptionRight());
            Assert.AreEqual(OptionRight.Put, "put".ParseOptionRight());
            Assert.Throws<ArgumentException>(() => "straddle".ParseOptionRight());
            Assert.AreEqual(OptionStyle.American, "American".ParseOptionStyle());
            Assert.AreEqual(OptionStyle.European, "european".ParseOptionStyle());
            Assert.Throws<ArgumentException>(() => "asian".ParseOptionStyle());
        }

        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("0", DataMappingMode.LastTradingDay)]
        [TestCase("LastTradingDay", DataMappingMode.LastTradingDay)]
        [TestCase("1", DataMappingMode.FirstDayMonth)]
        [TestCase("firstdaymonth", DataMappingMode.FirstDayMonth)]
        [TestCase("2", DataMappingMode.OpenInterest)]
        [TestCase("OpenInterest", DataMappingMode.OpenInterest)]
        [TestCase("3", DataMappingMode.OpenInterestAnnual)]
        [TestCase("openinterestannual", DataMappingMode.OpenInterestAnnual)]
        public void ParseDataMappingMode(string input, DataMappingMode? expected)
        {
            Assert.AreEqual(expected, input.ParseDataMappingMode());
        }

        [Test]
        public void ParseDataMappingModeRejectsUnknown()
        {
            Assert.Throws<ArgumentException>(() => "unknown".ParseDataMappingMode());
        }

        [TestCase(DataMappingMode.OpenInterest, QuantConnect.Market.HKFE, false)]
        [TestCase(DataMappingMode.OpenInterestAnnual, QuantConnect.Market.EUREX, false)]
        [TestCase(DataMappingMode.OpenInterest, QuantConnect.Market.CME, true)]
        [TestCase(DataMappingMode.LastTradingDay, QuantConnect.Market.HKFE, true)]
        public void DataMappingModeAvailability(DataMappingMode mode, string market, bool expected)
        {
            Assert.AreEqual(expected, mode.IsAvailableForFutureMarket(market));
        }

        [Test]
        public void RoundUpToInterval()
        {
            var time = new DateTime(2021, 1, 1, 10, 7, 30);
            Assert.AreEqual(new DateTime(2021, 1, 1, 10, 10, 0), time.RoundUp(TimeSpan.FromMinutes(5)));
            Assert.AreEqual(new DateTime(2021, 1, 1, 10, 10, 0), new DateTime(2021, 1, 1, 10, 10, 0).RoundUp(TimeSpan.FromMinutes(5)));
            Assert.AreEqual(time, time.RoundUp(TimeSpan.Zero));
        }

        [Test]
        public void SafeMultiply100CapsAtMaxValue()
        {
            Assert.AreEqual(150m, 1.5m.SafeMultiply100());
            Assert.AreEqual(decimal.MaxValue, (decimal.MaxValue / 10m).SafeMultiply100());
        }

        [Test]
        public void ToQueryStringJoinsPairs()
        {
            var pairs = new Dictionary<string, object> { { "a", 1 }, { "b", "two" } };
            Assert.AreEqual("a=1&b=two", pairs.ToQueryString());
        }

        [Test]
        public void WithEmbeddedHtmlAnchorsWrapsLinks()
        {
            var result = "see https://www.quantconnect.com/docs for details".WithEmbeddedHtmlAnchors();
            Assert.AreEqual("see <a href='https://www.quantconnect.com/docs' target='blank'>https://www.quantconnect.com/docs</a> for details", result);
            Assert.AreEqual("no links here", "no links here".WithEmbeddedHtmlAnchors());
        }

        [TestCase(0, true, Resolution.Tick)]
        [TestCase(1, true, Resolution.Second)]
        [TestCase(60, true, Resolution.Minute)]
        [TestCase(3600, true, Resolution.Hour)]
        [TestCase(86400, true, Resolution.Daily)]
        [TestCase(0.5, false, Resolution.Tick)]
        [TestCase(30, false, Resolution.Second)]
        [TestCase(120, false, Resolution.Minute)]
        [TestCase(7200, false, Resolution.Hour)]
        [TestCase(172800, false, Resolution.Daily)]
        public void ToHigherResolutionEquivalent(double seconds, bool exact, Resolution expected)
        {
            Assert.AreEqual(expected, TimeSpan.FromSeconds(seconds).ToHigherResolutionEquivalent(exact));
        }

        [Test]
        public void ToHigherResolutionEquivalentRequiresExactMatch()
        {
            Assert.Throws<InvalidOperationException>(() => TimeSpan.FromMinutes(2).ToHigherResolutionEquivalent(true));
        }

        [Test]
        public void GetDecimalFromCsv()
        {
            Assert.AreEqual(2.5m, "1,2.5,3".GetDecimalFromCsv(1));
            Assert.AreEqual(0m, "1,2.5,3".GetDecimalFromCsv(5));
        }

        [TestCase(2021, 1, 1, true)]
        [TestCase(2021, 1, 2, false)]
        [TestCase(2021, 1, 3, false)]
        [TestCase(2021, 1, 4, true)]
        public void IsCommonBusinessDay(int year, int month, int day, bool expected)
        {
            Assert.AreEqual(expected, new DateTime(year, month, day).IsCommonBusinessDay());
        }

        [TestCase("file.zip", ".zip")]
        [TestCase("file.csv", ".csv")]
        [TestCase("file.tsv", ".tsv")]
        [TestCase("file.txt", ".custom")]
        [TestCase("a", ".custom")]
        public void GetExtension(string input, string expected)
        {
            Assert.AreEqual(expected, input.GetExtension());
        }

        [Test]
        [Ignore("Possible bug: Extensions.GetExtension only inspects the last 4 characters, so the allowed '.json' extension is never matched")]
        public void GetExtensionRecognizesJson()
        {
            Assert.AreEqual(".json", "data.json".GetExtension());
        }

        [Test]
        public void MatchesTypeName()
        {
            var type = typeof(Symbol);
            Assert.IsTrue(type.MatchesTypeName(type.AssemblyQualifiedName));
            Assert.IsTrue(type.MatchesTypeName(type.FullName));
            Assert.IsTrue(type.MatchesTypeName("Symbol"));
            Assert.IsFalse(type.MatchesTypeName("NotSymbol"));
        }

        [TestCase(SecurityType.Equity, true)]
        [TestCase(SecurityType.Future, true)]
        [TestCase(SecurityType.Index, true)]
        [TestCase(SecurityType.Forex, false)]
        [TestCase(SecurityType.Option, false)]
        public void HasOptions(SecurityType securityType, bool expected)
        {
            Assert.AreEqual(expected, securityType.HasOptions());
        }

        [Test]
        public void ProcessUntilEmptyDrainsCollection()
        {
            var queue = new ConcurrentQueue<int>(new[] { 1, 2, 3 });
            var processed = new List<int>();
            queue.ProcessUntilEmpty(processed.Add);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, processed);
            Assert.IsTrue(queue.IsEmpty);
        }

        [Test]
        public void IsOutOfDateUsesDatePatternAndWriteTime()
        {
            var directory = Directory.CreateTempSubdirectory("lean-extensions-coverage");
            try
            {
                var recent = Path.Combine(directory.FullName, "spy.csv");
                File.WriteAllText(recent, "x");
                Assert.IsFalse(recent.IsOutOfDate());

                var old = Path.Combine(directory.FullName, "aapl.csv");
                File.WriteAllText(old, "x");
                File.SetLastWriteTime(old, new DateTime(2000, 1, 1));
                Assert.IsTrue(old.IsOutOfDate());

                var dated = Path.Combine(directory.FullName, "20000101.csv");
                File.WriteAllText(dated, "x");
                File.SetLastWriteTime(dated, new DateTime(2000, 1, 1));
                Assert.IsFalse(dated.IsOutOfDate());
            }
            finally
            {
                directory.Delete(true);
            }
        }
    }
}
