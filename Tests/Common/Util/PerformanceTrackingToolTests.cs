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
using QuantConnect.Tests.Engine.DataFeeds;
using QuantConnect.Util;

namespace QuantConnect.Tests.Common.Util
{
    [TestFixture]
    public class PerformanceTrackingToolTests
    {
        private static readonly DateTime Start = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        private static AlgorithmStub CreateAlgorithm(TimeSpan samplePeriod)
        {
            var algorithm = new AlgorithmStub();
            algorithm.Settings.PerformanceSamplePeriod = samplePeriod;
            algorithm.SetDateTime(Start);
            return algorithm;
        }

        [Test]
        public void DisabledSamplingOnlyCountsDataPoints()
        {
            var algorithm = CreateAlgorithm(TimeSpan.Zero);
            var tool = new PerformanceTrackingTool();
            tool.Initialize(algorithm);

            tool.Start(PerformanceTarget.OnData);
            tool.Stop(PerformanceTarget.OnData);
            tool.Sample(5, Start.AddDays(1));
            tool.Sample(3, Start.AddDays(2));
            tool.Shutdown();

            Assert.AreEqual(8, tool.DataPoints);
            Assert.AreEqual(0, tool.HistoryDataPoints);
            Assert.IsFalse(algorithm.GetChartUpdates().Any(chart => chart.Name == "Performance"));
            Assert.IsEmpty(algorithm.DebugMessages);
        }

        [Test]
        public void EnabledSamplingAddsPerformanceChartPoints()
        {
            var algorithm = CreateAlgorithm(TimeSpan.FromMinutes(1));
            var tool = new PerformanceTrackingTool();
            tool.Initialize(algorithm);

            foreach (PerformanceTarget target in Enum.GetValues(typeof(PerformanceTarget)))
            {
                tool.Start(target);
                tool.Stop(target);
            }

            tool.Sample(10, Start.AddSeconds(30));
            tool.Sample(15, Start.AddMinutes(1));
            tool.Sample(20, Start.AddMinutes(1).AddSeconds(30));
            tool.Sample(1, Start.AddMinutes(2));

            Assert.AreEqual(46, tool.DataPoints);
            var chart = algorithm.GetChartUpdates().Single(c => c.Name == "Performance");
            CollectionAssert.IsSubsetOf(new[] { "OnData", "Subscriptions", "Schedule", "Selection", "Slice", "Consolidators",
                "Securities", "Transactions", "SplitsDividendsDelisting", "WallTime", "ActiveSecurities", "DataPoints",
                "HistoryDataPoints", "CPU", "ManagedRAM", "TotalRAM" }, chart.Series.Keys);

            var dataPoints = chart.Series["DataPoints"].Values.Cast<ChartPoint>().ToList();
            Assert.AreEqual(2, dataPoints.Count);
            Assert.AreEqual(25m, dataPoints[0].y);
            Assert.AreEqual(21m, dataPoints[1].y);
            Assert.AreEqual(2, chart.Series["OnData"].Values.Count);

            tool.Shutdown();
            Assert.IsTrue(algorithm.DebugMessages.Any(m => m.Contains("Dps 46. HistoryDps 0.", StringComparison.InvariantCulture)));
        }

        [Test]
        public void InvalidTargetThrowsWhenSamplingEnabled()
        {
            var algorithm = CreateAlgorithm(TimeSpan.FromMinutes(1));
            var tool = new PerformanceTrackingTool();
            tool.Initialize(algorithm);

            Assert.Throws<ArgumentException>(() => tool.Start((PerformanceTarget)999));
            Assert.Throws<ArgumentException>(() => tool.Stop((PerformanceTarget)999));
        }
    }
}
