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
using System.Linq;
using NUnit.Framework;

namespace QuantConnect.Tests.Common
{
    [TestFixture]
    public class TimeCoverageTests
    {
        private static readonly DateTime Epoch2021 = new DateTime(2021, 1, 1);

        [Test]
        public void NextLiveAuxiliaryDataDueTimeBeforeAndAfterOffset()
        {
            // 10:00 UTC = 05:00 New York (EST), offset is 08:00
            Assert.AreEqual(TimeSpan.FromHours(3), Time.GetNextLiveAuxiliaryDataDueTime(new DateTime(2021, 1, 4, 10, 0, 0)));
            // 15:00 UTC = 10:00 New York, next due time is tomorrow at 08:00
            Assert.AreEqual(TimeSpan.FromHours(22), Time.GetNextLiveAuxiliaryDataDueTime(new DateTime(2021, 1, 4, 15, 0, 0)));
        }

        [TestCase(1000, 1500)]
        [TestCase(50, 150)]
        [TestCase(500, 500)]
        [TestCase(1950, 2450)]
        public void SecondUnevenWaitAvoidsSecondBoundaries(int wait, int expected)
        {
            Assert.AreEqual(expected, Epoch2021.GetSecondUnevenWait(wait));
        }

        [Test]
        public void UnixTimestampConversionsRoundTrip()
        {
            Assert.AreEqual(Epoch2021, Time.UnixTimeStampToDateTime(1609459200d));
            Assert.AreEqual(Epoch2021.AddMilliseconds(500), Time.UnixTimeStampToDateTime(1609459200.5m));
            Assert.AreEqual(Epoch2021, Time.UnixTimeStampToDateTime(1609459200L));
            Assert.AreEqual(Epoch2021.AddMilliseconds(123), Time.UnixMillisecondTimeStampToDateTime(1609459200123m));
            Assert.AreEqual(Epoch2021.AddTicks(1), Time.UnixNanosecondTimeStampToDateTime(1609459200000000100L));

            Assert.AreEqual(1609459200d, Time.DateTimeToUnixTimeStamp(Epoch2021));
            Assert.AreEqual(1609459200000d, Time.DateTimeToUnixTimeStampMilliseconds(Epoch2021));
            Assert.AreEqual(1609459200000000000L, Time.DateTimeToUnixTimeStampNanoseconds(Epoch2021));
        }

        [Test]
        public void MaxMinMultiplyAndAbs()
        {
            var one = TimeSpan.FromMinutes(1);
            var two = TimeSpan.FromMinutes(2);
            Assert.AreEqual(two, Time.Max(one, two));
            Assert.AreEqual(one, Time.Min(one, two));
            Assert.AreEqual(Epoch2021.AddDays(1), Time.Max(Epoch2021, Epoch2021.AddDays(1)));
            Assert.AreEqual(Epoch2021, Time.Min(Epoch2021, Epoch2021.AddDays(1)));
            Assert.AreEqual(TimeSpan.FromMinutes(3), two.Multiply(1.5));
            Assert.AreEqual(two, (-two).Abs());
            Assert.AreEqual(two, two.Abs());
        }

        [TestCase("210304", 2021, 3, 4, 0, 0, 0)]
        [TestCase("20210304", 2021, 3, 4, 0, 0, 0)]
        [TestCase("20210304 05:06", 2021, 3, 4, 5, 6, 0)]
        [TestCase("2021-03-04T05:06:07.123Z", 2021, 3, 4, 5, 6, 7)]
        [TestCase("3/4/21 5:06 PM", 2021, 3, 4, 17, 6, 0)]
        [TestCase("3/4/21", 2021, 3, 4, 0, 0, 0)]
        [TestCase("3/4/2021 5:06:07 AM", 2021, 3, 4, 5, 6, 7)]
        [TestCase("3/4/2021", 2021, 3, 4, 0, 0, 0)]
        public void ParseDateSupportsKnownFormats(string input, int year, int month, int day, int hour, int minute, int second)
        {
            Assert.AreEqual(new DateTime(year, month, day, hour, minute, second), Time.ParseDate(input));
        }

        [Test]
        public void ParseFixUtcTimestampSupportsBothFormats()
        {
            Assert.AreEqual(new DateTime(2021, 3, 4, 5, 6, 7), Time.ParseFIXUtcTimestamp("20210304-05:06:07"));
            Assert.AreEqual(new DateTime(2021, 3, 4, 5, 6, 7, 89), Time.ParseFIXUtcTimestamp("20210304-05:06:07.089"));
        }

        [Test]
        public void DateTimeRangeAndEachDayAreInclusive()
        {
            var range = Time.DateTimeRange(Epoch2021, Epoch2021.AddHours(2), TimeSpan.FromHours(1)).ToList();
            CollectionAssert.AreEqual(new[] { Epoch2021, Epoch2021.AddHours(1), Epoch2021.AddHours(2) }, range);

            var days = Time.EachDay(Epoch2021, Epoch2021.AddDays(2)).ToList();
            CollectionAssert.AreEqual(new[] { Epoch2021, Epoch2021.AddDays(1), Epoch2021.AddDays(2) }, days);
        }

        [Test]
        public void NormalizationHelpers()
        {
            Assert.AreEqual(1d, Time.NormalizeInstantWithinRange(Epoch2021, Epoch2021, TimeSpan.Zero));
            Assert.AreEqual(0d, Time.NormalizeInstantWithinRange(Epoch2021, Epoch2021.AddSeconds(1), TimeSpan.Zero));
            Assert.AreEqual(0.5d, Time.NormalizeInstantWithinRange(Epoch2021, Epoch2021.AddMinutes(1), TimeSpan.FromMinutes(2)));
            Assert.AreEqual(0d, Time.NormalizeTimeStep(TimeSpan.Zero, TimeSpan.FromMinutes(1)));
            Assert.AreEqual(0.25d, Time.NormalizeTimeStep(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(1)));
        }
    }
}
