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
using System.Linq;
using Deedle;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Report;

namespace QuantConnect.Tests.Report
{
    [TestFixture]
    public class ReportCoverageTests
    {
        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "report-coverage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        [Test]
        public void CompilesReportWithLiveResultAndWritesPortfolios()
        {
            var backtest = GetBacktestResult();
            var live = CreateLiveResult(new DateTime(2021, 1, 5), 260);
            var destination = Path.Combine(_tempDirectory, "report.html");

            var report = new QuantConnect.Report.Report("Strategy", "Description", "v1.0.0", backtest, live, destination);
            report.Compile(out var html, out var statistics);

            Assert.IsNotEmpty(html);
            StringAssert.Contains("Strategy", html);
            Assert.IsTrue(File.Exists(Path.Combine(_tempDirectory, "report-backtesting-portfolio.json")));
            Assert.IsFalse(File.Exists(Path.Combine(_tempDirectory, "report-live-portfolio.json")));

            var parsed = JObject.Parse(statistics);
            // live statistics are computed from the trailing live equity curve
            Assert.AreEqual(259, parsed["backtest-days"].Value<int>());
            Assert.Greater(parsed["psr"].Value<double>(), 0d);
            Assert.LessOrEqual(parsed["psr"].Value<double>(), 1d);
            Assert.Greater(parsed["sharpe"].Value<double>(), 0d);
            Assert.Greater(parsed["sortino"].Value<double>(), 0d);
            Assert.AreEqual(JTokenType.Integer, parsed["drawdown-recovery"].Type);
        }

        [Test]
        public void CompilesReportWithCustomTemplateAndCss()
        {
            var backtest = GetBacktestResult();
            const string template = "<html><style>{{$REPORT-STYLESHEET}}</style><body>{{$TEXT-STRATEGY-NAME}}|{{$TEXT-STRATEGY-VERSION}}</body></html>";

            var report = new QuantConnect.Report.Report("MyStrategy", "Description", "v9", backtest, null,
                cssOverride: "body { color: red; }", htmlCustom: template);
            report.Compile(out var html, out _);

            StringAssert.Contains("MyStrategy|v9", html);
            StringAssert.Contains("body { color: red; }", html);
            StringAssert.DoesNotContain("{{$", html);
        }

        [Test]
        public void MetricsReturnEmptyResultsWithoutOrders()
        {
            var curve = new Series<DateTime, double>(new[] { new DateTime(2019, 1, 3) }, new[] { 100000d });
            var noOrders = new List<Order>();
            var emptyCurve = new Series<DateTime, double>(new DateTime[0], new double[0]);

            Assert.IsTrue(Metrics.LeverageUtilization(curve, noOrders).IsEmpty);
            Assert.IsTrue(Metrics.AssetAllocations(curve, noOrders).IsEmpty);
            Assert.AreEqual(0, Metrics.Exposure(curve, noOrders, OrderDirection.Buy).RowCount);
            Assert.IsTrue(Metrics.LeverageUtilization(emptyCurve, CreateOrders()).IsEmpty);
            Assert.IsTrue(Metrics.AssetAllocations(emptyCurve, CreateOrders()).IsEmpty);
            Assert.AreEqual(0, Metrics.Exposure(emptyCurve, CreateOrders(), OrderDirection.Sell).RowCount);
        }

        [Test]
        public void MetricsAreComputedFromOrders()
        {
            var curve = new Series<DateTime, double>(new SortedList<DateTime, double>
            {
                { new DateTime(2019, 1, 3, 5, 0, 5), 100000 },
                { new DateTime(2019, 1, 4, 5, 0, 5), 90000 },
            });

            var leverage = Metrics.LeverageUtilization(curve, CreateOrders());
            Assert.IsFalse(leverage.IsEmpty);

            var allocations = Metrics.AssetAllocations(curve, CreateOrders());
            Assert.IsFalse(allocations.IsEmpty);
            Assert.AreEqual(Symbols.SPY, allocations.Keys.Single());

            var longExposure = Metrics.Exposure(curve, CreateOrders(), OrderDirection.Buy);
            Assert.Greater(longExposure.RowCount, 0);
            Assert.IsTrue(longExposure.ColumnKeys.All(key => key.Item2 == OrderDirection.Buy));
        }

        [Test]
        public void PortfolioLooperGetsHistoryForSymbols()
        {
            var slices = PortfolioLooper.GetHistory(
                new List<Symbol> { Symbols.SPY },
                new DateTime(2013, 10, 7),
                new DateTime(2013, 10, 11),
                Resolution.Daily).ToList();

            Assert.IsNotEmpty(slices);
            Assert.IsTrue(slices.All(slice => slice.Bars.ContainsKey(Symbols.SPY)));
            Assert.IsTrue(slices.All(slice => slice.Bars[Symbols.SPY].Close > 0));
        }

        private static List<Order> CreateOrders()
        {
            var entryOrder = Order.CreateOrder(new SubmitOrderRequest(OrderType.Market, SecurityType.Equity, Symbols.SPY, 1, 0, 0,
                new DateTime(2019, 1, 3, 5, 0, 5), string.Empty));
            var exitOrder = Order.CreateOrder(new SubmitOrderRequest(OrderType.Market, SecurityType.Equity, Symbols.SPY, -1, 0, 0,
                new DateTime(2019, 1, 4, 5, 0, 5), string.Empty));

            entryOrder.LastFillTime = new DateTime(2019, 1, 3, 5, 0, 5);
            exitOrder.LastFillTime = new DateTime(2019, 1, 4, 5, 0, 5);
            entryOrder.GetType().GetProperty("Id").SetValue(entryOrder, 1);
            entryOrder.GetType().GetProperty("Price").SetValue(entryOrder, 100000m);
            entryOrder.GetType().GetProperty("Status").SetValue(entryOrder, OrderStatus.Filled);
            exitOrder.GetType().GetProperty("Id").SetValue(exitOrder, 2);
            exitOrder.GetType().GetProperty("Price").SetValue(exitOrder, 80000m);
            exitOrder.GetType().GetProperty("Status").SetValue(exitOrder, OrderStatus.Filled);

            return new List<Order> { entryOrder, exitOrder };
        }

        private static LiveResult CreateLiveResult(DateTime start, int days)
        {
            var equity = new QuantConnect.Series(BaseResultsHandler.EquityKey, SeriesType.Line);
            var value = 1000000m;
            for (var i = 0; i < days; i++)
            {
                // deterministic oscillating returns with a positive drift
                value *= 1m + (decimal)(0.002 * Math.Sin(i) + 0.0005);
                equity.AddPoint(start.AddDays(i), value);
            }

            var chart = new Chart(BaseResultsHandler.StrategyEquityKey);
            chart.AddSeries(equity);

            return new LiveResult
            {
                Charts = new Dictionary<string, Chart> { [chart.Name] = chart },
                Orders = new Dictionary<int, Order>()
            };
        }

        private static BacktestResult GetBacktestResult()
        {
            var settings = new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new NullResultValueTypeJsonConverter<BacktestResult>() },
                FloatParseHandling = FloatParseHandling.Decimal
            };
            return JsonConvert.DeserializeObject<BacktestResult>(
                File.ReadAllText(Path.Combine("TestData", "test_report_data.json")), settings);
        }
    }
}
