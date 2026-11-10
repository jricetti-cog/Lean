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
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Data.Market;

namespace QuantConnect.Tests.Common
{
    [TestFixture]
    public class CommonCoreCoverageTests
    {
        private static readonly int[] Values = { 1, 2, 3, 4, 5 };

        private static IEnumerable<TestCaseData> FilterCases()
        {
            yield return new TestCaseData(BinaryComparison.Equal, new[] { 3 });
            yield return new TestCaseData(BinaryComparison.NotEqual, new[] { 1, 2, 4, 5 });
            yield return new TestCaseData(BinaryComparison.LessThan, new[] { 1, 2 });
            yield return new TestCaseData(BinaryComparison.LessThanOrEqual, new[] { 1, 2, 3 });
            yield return new TestCaseData(BinaryComparison.GreaterThan, new[] { 4, 5 });
            yield return new TestCaseData(BinaryComparison.GreaterThanOrEqual, new[] { 3, 4, 5 });
        }

        [TestCaseSource(nameof(FilterCases))]
        public void FilterCollection(BinaryComparison comparison, int[] expected)
        {
            var result = comparison.Filter<int, List<int>>(Values.ToList(), 3);
            CollectionAssert.AreEqual(expected, result);
        }

        [TestCaseSource(nameof(FilterCases))]
        public void FilterSortedDictionary(BinaryComparison comparison, int[] expected)
        {
            var dictionary = new SortedDictionary<int, string>(Values.ToDictionary(v => v, v => v.ToStringInvariant()));
            var result = comparison.Filter(dictionary, 3);
            CollectionAssert.AreEqual(expected, result.Keys);
            CollectionAssert.AreEqual(expected.Select(v => v.ToStringInvariant()), result.Values);
        }

        [TestCaseSource(nameof(FilterCases))]
        public void FilterImmutableSortedDictionary(BinaryComparison comparison, int[] expected)
        {
            var dictionary = Values.ToImmutableSortedDictionary(v => v, v => v * 10);
            var result = comparison.Filter(dictionary, 3);
            CollectionAssert.AreEqual(expected, result.Keys);
            CollectionAssert.AreEqual(expected.Select(v => v * 10), result.Values);
        }

        [TestCaseSource(nameof(FilterCases))]
        public void SplitByImmutableSortedDictionary(BinaryComparison comparison, int[] expected)
        {
            var dictionary = Values.ToImmutableSortedDictionary(v => v, v => v * 10);
            var (matching, remaining) = comparison.SplitBy(dictionary, 3);
            CollectionAssert.AreEqual(expected, matching.Keys);
            CollectionAssert.AreEqual(Values.Except(expected), remaining.Keys);
        }

        [Test]
        public void FilterWithReferenceNotPresent()
        {
            var dictionary = new SortedDictionary<int, int> { { 1, 1 }, { 5, 5 } };
            CollectionAssert.IsEmpty(BinaryComparison.Equal.Filter(dictionary, 3));
            CollectionAssert.AreEqual(new[] { 1, 5 }, BinaryComparison.NotEqual.Filter(dictionary, 3).Keys);
            CollectionAssert.AreEqual(new[] { 5 }, BinaryComparison.GreaterThan.Filter(dictionary, 3).Keys);
        }

        [Test]
        public void CandlestickSeriesConstructors()
        {
            var empty = new CandlestickSeries();
            Assert.AreEqual(SeriesType.Candle, empty.SeriesType);

            var named = new CandlestickSeries("name");
            Assert.AreEqual("name", named.Name);
            Assert.AreEqual(SeriesType.Candle, named.SeriesType);

            var indexed = new CandlestickSeries("name", 2);
            Assert.AreEqual(2, indexed.Index);
            Assert.AreEqual("$", indexed.Unit);

            var withUnit = new CandlestickSeries("name", 3, "#");
            Assert.AreEqual(3, withUnit.Index);
            Assert.AreEqual("#", withUnit.Unit);

            var nameUnit = new CandlestickSeries("name", "%");
            Assert.AreEqual("%", nameUnit.Unit);
            Assert.AreEqual(SeriesType.Candle, nameUnit.SeriesType);
        }

        [Test]
        public void CandlestickSeriesAddPointOverloads()
        {
            var series = new CandlestickSeries("name");
            var time = new DateTime(2020, 1, 1);
            series.AddPoint(time, 1, 4, 0.5m, 2);
            series.AddPoint(new TradeBar(time.AddDays(1), Symbols.SPY, 2, 5, 1, 3, 100));
            series.AddPoint(time.AddDays(2), new List<decimal> { 3, 6, 2, 4 });
            series.AddPoint(new Candlestick(time.AddDays(3), 4, 7, 3, 5));

            Assert.AreEqual(4, series.Values.Count);
            var second = (Candlestick)series.Values[1];
            Assert.AreEqual(2m, second.Open);
            Assert.AreEqual(5m, second.High);
            Assert.AreEqual(1m, second.Low);
            Assert.AreEqual(3m, second.Close);
            var third = (Candlestick)series.Values[2];
            Assert.AreEqual(6m, third.High);
        }

        [Test]
        public void CandlestickSeriesRejectsInvalidPoints()
        {
            var series = new CandlestickSeries("name");
            Assert.Throws<ArgumentException>(() => series.AddPoint(new ChartPoint(new DateTime(2020, 1, 1), 1m)));
            Assert.Throws<ArgumentException>(() => series.AddPoint(new DateTime(2020, 1, 1), new List<decimal> { 1, 2, 3 }));
            Assert.AreEqual(0, series.Values.Count);
        }

        [Test]
        public void CandlestickSeriesConsolidateChartPoints()
        {
            var series = new CandlestickSeries("name");
            Assert.IsNull(series.ConsolidateChartPoints());

            var time = new DateTime(2020, 1, 1);
            series.AddPoint(time, 1, 4, 0.5m, 2);
            series.AddPoint(new Candlestick(time.AddDays(1), null, 6, null, 3));

            var consolidated = (Candlestick)series.ConsolidateChartPoints();
            Assert.AreEqual(time.AddDays(1), consolidated.Time);
            Assert.AreEqual(1m, consolidated.Open);
            Assert.AreEqual(10m, consolidated.High);
            Assert.AreEqual(0.5m, consolidated.Low);
            Assert.AreEqual(5m, consolidated.Close);
        }

        [Test]
        public void OsPropertiesAreConsistent()
        {
            Assert.AreNotEqual(OS.IsLinux, OS.IsWindows);
            Assert.AreEqual(System.IO.Path.DirectorySeparatorChar.ToString(), OS.PathSeparation);
            Assert.GreaterOrEqual(OS.DriveSpaceRemaining, 0);
            Assert.GreaterOrEqual(OS.DriveSpaceUsed, 0);
            Assert.LessOrEqual(OS.DriveSpaceUsed, OS.DriveTotalSpace);
            Assert.Greater(OS.ApplicationMemoryUsed, 0);
            Assert.GreaterOrEqual(OS.TotalPhysicalMemoryUsed, 0);
            Assert.GreaterOrEqual(OS.CpuUsage, 0m);
        }

        [Test]
        public void OsInitializeAndDispose()
        {
            try
            {
                OS.Initialize();
                Assert.GreaterOrEqual(OS.CpuUsage, 0m);
                var statistics = OS.GetServerStatistics();
                Assert.IsNotEmpty(statistics);
            }
            finally
            {
                OS.Dispose();
            }
            Assert.GreaterOrEqual(OS.CpuUsage, 0m);
        }

        [Test]
        public void RealTimeSynchronizedTimerScannerDoesNothingWhenStopped()
        {
            var calls = 0;
            var timer = new RealTimeSynchronizedTimer(TimeSpan.FromMilliseconds(1), _ => calls++);
            timer.Stop();
            timer.Scanner();
            Assert.AreEqual(0, calls);

            var defaultTimer = new RealTimeSynchronizedTimer();
            defaultTimer.Stop();
            defaultTimer.Scanner();
        }

        [Test]
        public void RealTimeSynchronizedTimerInvokesCallbackAfterStart()
        {
            using var triggered = new ManualResetEventSlim(false);
            var triggerTimes = new List<DateTime>();
            var timer = new RealTimeSynchronizedTimer(TimeSpan.FromMilliseconds(20), time =>
            {
                lock (triggerTimes)
                {
                    triggerTimes.Add(time);
                }
                triggered.Set();
            });
            try
            {
                timer.Pause();
                timer.Resume();
                timer.Start();
                Assert.IsTrue(triggered.Wait(TimeSpan.FromSeconds(30)));
            }
            finally
            {
                timer.Stop();
            }

            lock (triggerTimes)
            {
                Assert.IsNotEmpty(triggerTimes);
                Assert.AreEqual(0, triggerTimes[0].Ticks % TimeSpan.FromMilliseconds(20).Ticks);
            }
        }
    }
}
