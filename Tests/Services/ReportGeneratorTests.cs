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
using NUnit.Framework;
using QuantConnect.Services.Report;

namespace QuantConnect.Tests.Services
{
    [TestFixture]
    public class ReportGeneratorTests
    {
        private static readonly string BacktestJson = File.ReadAllText(Path.Combine("TestData", "services_backtest_result.json"));

        [Test]
        public void ParsesBacktestResultJson()
        {
            var (backtest, live) = ReportGenerator.ParseResults(BacktestJson);
            Assert.IsNotNull(backtest);
            Assert.IsNull(live);
            Assert.IsNotEmpty(backtest.Charts);
        }

        [Test]
        public void ParsesBacktestAndLiveEnvelope()
        {
            var (backtest, live) = ReportGenerator.ParseResults($"{{\"backtest\": {BacktestJson}}}");
            Assert.IsNotNull(backtest);
            Assert.IsNull(live);
        }

        [Test]
        public void ParsesBacktestResultPacket()
        {
            var (backtest, _) = ReportGenerator.ParseResults($"{{\"progress\": 1, \"results\": {BacktestJson}}}");
            Assert.IsNotNull(backtest);
            Assert.IsNotEmpty(backtest.Charts);
        }

        [TestCase("{}")]
        [TestCase("{\"foo\": 1}")]
        [TestCase("{\"backtest\": 1}")]
        public void RejectsNonResultJson(string json)
        {
            Assert.Throws<ArgumentException>(() => ReportGenerator.ParseResults(json));
        }

        [TestCase("abc-123_x", "abc-123_x")]
        [TestCase("../../etc/passwd", "etcpasswd")]
        public void SanitizesReportIds(string id, string expected)
        {
            Assert.AreEqual(expected, ReportGenerator.SanitizeId(id));
        }

        [TestCase("")]
        [TestCase("../")]
        public void RejectsEmptyReportIds(string id)
        {
            Assert.Throws<ArgumentException>(() => ReportGenerator.SanitizeId(id));
        }
    }
}
