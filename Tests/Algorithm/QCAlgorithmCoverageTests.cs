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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Brokerages;
using QuantConnect.Commands;
using QuantConnect.Data;
using QuantConnect.Data.Custom.IconicTypes;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Statistics;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class QCAlgorithmCoverageTests
    {
        private AlgorithmStub _algorithm;
        private Symbol _spy;

        [SetUp]
        public void Setup()
        {
            _algorithm = new AlgorithmStub();
            _algorithm.SetDateTime(new DateTime(2013, 10, 07, 14, 0, 0));
            _spy = _algorithm.AddEquity("SPY").Symbol;
        }

        [Test]
        public void BaseInitializeThrowsNotImplemented()
        {
            var algorithm = new QCAlgorithm();
            Assert.Throws<NotImplementedException>(() => algorithm.Initialize());
        }

        [Test]
        public void PostInitializeThrowsWhenEndDateBeforeStartDate()
        {
            _algorithm.SetStartDate(2013, 10, 10);
            _algorithm.SetEndDate(2013, 10, 05);
            Assert.Throws<ArgumentException>(() => _algorithm.PostInitialize());
        }

        [Test]
        public void SettersThrowOnceAlgorithmIsLocked()
        {
            _algorithm.SetLocked();

            Assert.Throws<InvalidOperationException>(() => _algorithm.SetCash(1000m));
            Assert.Throws<InvalidOperationException>(() => _algorithm.SetCash("EUR", 1000m, 1.1m));
            Assert.Throws<InvalidOperationException>(() => _algorithm.SetAccountCurrency("EUR"));
            Assert.Throws<InvalidOperationException>(() => _algorithm.SetStartDate(new DateTime(2013, 10, 01)));
            Assert.Throws<InvalidOperationException>(() => _algorithm.SetEndDate(new DateTime(2013, 10, 01)));
            Assert.Throws<InvalidOperationException>(() => _algorithm.SetTimeZone(DateTimeZone.Utc));
            Assert.Throws<InvalidOperationException>(() => _algorithm.SetBenchmark(_spy));
            Assert.Throws<InvalidOperationException>(() => _algorithm.SetBenchmark(time => 1m));
            Assert.Throws<InvalidOperationException>(() => _algorithm.AddSecurityInitializer(new BrokerageModelSecurityInitializer()));
            Assert.Throws<Exception>(() => _algorithm.SetSecurityInitializer(new BrokerageModelSecurityInitializer()));
        }

        [Test]
        public void SetCashDoubleSetsAccountCurrencyAmount()
        {
            _algorithm.SetCash(12345.5d);
            Assert.AreEqual(12345.5m, _algorithm.Portfolio.Cash);
        }

        [Test]
        public void SetSecurityInitializerTwiceWarns()
        {
            _algorithm.SetSecurityInitializer(new BrokerageModelSecurityInitializer());
            _algorithm.SetSecurityInitializer(new BrokerageModelSecurityInitializer());
            Assert.IsTrue(_algorithm.DebugMessages.Any(x => x.Contains("SetSecurityInitializer() has already been called")));
        }

        [Test]
        public void SetTimeZoneWithUnknownIdThrows()
        {
            var exception = Assert.Throws<ArgumentException>(() => _algorithm.SetTimeZone("Not/AZone"));
            StringAssert.Contains("Not/AZone", exception.Message);
        }

        [Test]
        public void SetStartAndEndDateValidation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _algorithm.SetStartDate(new DateTime(1899, 12, 31)));
            Assert.Throws<ArgumentOutOfRangeException>(() => _algorithm.SetStartDate(DateTime.UtcNow.AddYears(5)));
            Assert.Throws<ArgumentException>(() => _algorithm.SetStartDate(2013, 13, 1));
            Assert.Throws<ArgumentException>(() => _algorithm.SetEndDate(2013, 2, 30));
        }

        [Test]
        public void SetDeploymentDetailsNullThrows()
        {
            Assert.Throws<ArgumentNullException>(() => _algorithm.SetDeploymentDetails(null));
        }

        [Test]
        public void SetHistoryProviderAndRunTimeErrorRejectNull()
        {
            Assert.Throws<ArgumentNullException>(() => _algorithm.SetHistoryProvider(null));
            Assert.Throws<ArgumentNullException>(() => _algorithm.SetRunTimeError(null));
        }

        [Test]
        public void NumericDebugLogAndErrorOverloadsEnqueueMessages()
        {
            _algorithm.DebugMessages = new ConcurrentQueue<string>();
            _algorithm.LogMessages = new ConcurrentQueue<string>();
            _algorithm.ErrorMessages = new ConcurrentQueue<string>();

            _algorithm.Debug(1);
            _algorithm.Debug(2.5d);
            _algorithm.Debug(3.25m);
            _algorithm.Log(4);
            _algorithm.Log(5.5d);
            _algorithm.Log(6.25m);
            _algorithm.Error(7);
            _algorithm.Error(8.5d);
            _algorithm.Error(9.25m);
            _algorithm.Error(new InvalidOperationException("boom"));

            AssertMessages(new[] { "1", "2.5", "3.25" }, _algorithm.DebugMessages);
            AssertMessages(new[] { "4", "5.5", "6.25" }, _algorithm.LogMessages);
            AssertMessages(new[] { "7", "8.5", "9.25", "boom" }, _algorithm.ErrorMessages);
        }

        [Test]
        public void SetQuitStopsAlgorithm()
        {
            _algorithm.SetQuit(false);
            Assert.AreNotEqual(AlgorithmStatus.Stopped, _algorithm.Status);
            _algorithm.SetQuit(true);
            Assert.AreEqual(AlgorithmStatus.Stopped, _algorithm.Status);
        }

        [Test]
        public void AlgorithmIdAndStatisticsDefaults()
        {
            Assert.IsNotNull(_algorithm.AlgorithmId);
            Assert.IsNotNull(_algorithm.Statistics);
            Assert.IsEmpty(_algorithm.Statistics.Summary);
        }

        [Test]
        public void TagsUpdatedEventRaisedOnlyAfterLocked()
        {
            var updates = 0;
            _algorithm.TagsUpdated += (sender, tags) => updates++;

            _algorithm.AddTag("first");
            Assert.AreEqual(0, updates);

            _algorithm.SetLocked();
            Assert.AreEqual(1, updates);
            _algorithm.AddTag("second");
            Assert.AreEqual(2, updates);
            _algorithm.AddTag("second");
            Assert.AreEqual(2, updates);

            _algorithm.Tags = new HashSet<string> { "a", "b" };
            Assert.AreEqual(3, updates);
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, _algorithm.Tags);
        }

        [Test]
        public void DefaultEventHandlersDoNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                _algorithm.OnSplits(new Splits());
                _algorithm.OnSymbolChangedEvents(new SymbolChangedEvents());
                _algorithm.OnMarginCallWarning();
                _algorithm.OnBrokerageMessage(new BrokerageMessageEvent(BrokerageMessageType.Information, 0, "info"));
                _algorithm.OnBrokerageDisconnect();
                _algorithm.OnBrokerageReconnect();
            });
        }

        [Test]
        public void OnDataInvokesNonOverridingSliceHandler()
        {
            var algorithm = new HiddenOnDataAlgorithm();
            var slice = new Slice(new DateTime(2013, 10, 07), new List<BaseData>(), new DateTime(2013, 10, 07));

            ((QCAlgorithm)algorithm).OnData(slice);
            ((QCAlgorithm)algorithm).OnData(slice);

            Assert.AreEqual(2, algorithm.Calls);
        }

        [Test]
        public void AddFutureRejectsTooDeepContractOffset()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _algorithm.AddFuture("ES", contractDepthOffset: 10));
        }

        [Test]
        public void OptionAndFutureContractArgumentValidation()
        {
            var future = _algorithm.AddFuture("ES").Symbol;
            var contract = Symbol.CreateFuture("ES", Market.CME, new DateTime(2013, 12, 20));

            Assert.Throws<ArgumentException>(() => _algorithm.AddFutureOption(contract));
            Assert.Throws<ArgumentException>(() => _algorithm.AddFutureOptionContract(future));
            Assert.Throws<ArgumentException>(() => _algorithm.AddIndexOption(_spy, "SPXW"));
            Assert.Throws<ArgumentException>(() => _algorithm.AddIndexOptionContract(_spy));
            Assert.Throws<ArgumentException>(() => _algorithm.AddOptionContract(_spy));
            Assert.Throws<ArgumentException>(() => _algorithm.AddOptionContract(null));
        }

        [Test]
        public void AddIndexOptionWithoutTargetUsesIndexTicker()
        {
            var spx = _algorithm.AddIndex("SPX").Symbol;
            var option = _algorithm.AddIndexOption(spx);
            Assert.AreEqual(SecurityType.IndexOption, option.Symbol.SecurityType);
            Assert.AreEqual(spx, option.Symbol.Underlying);
        }

        [Test]
        public void AddOptionContractRequiresRawUnderlyingOnceLocked()
        {
            var aapl = _algorithm.AddEquity("AAPL", dataNormalizationMode: DataNormalizationMode.Adjusted).Symbol;
            _algorithm.SetLocked();
            var contract = Symbol.CreateOption(aapl, Market.USA, OptionStyle.American, OptionRight.Call, 500m, new DateTime(2013, 10, 19));

            var exception = Assert.Throws<ArgumentException>(() => _algorithm.AddOptionContract(contract));
            StringAssert.Contains("DataNormalizationMode.Raw", exception.Message);
        }

        [Test]
        public void RemoveSecurityOfUnknownSymbolReturnsFalse()
        {
            var unknown = Symbol.Create("UNKNOWN", SecurityType.Equity, Market.USA);
            Assert.IsFalse(_algorithm.RemoveSecurity(unknown));
        }

        [Test]
        public void RemoveOptionContractRemovesUnderlyingWhenNotUsedElsewhere()
        {
            var aapl = _algorithm.AddEquity("AAPL", dataNormalizationMode: DataNormalizationMode.Raw).Symbol;
            var contract = Symbol.CreateOption(aapl, Market.USA, OptionStyle.American, OptionRight.Call, 500m, new DateTime(2013, 10, 19));
            _algorithm.AddOptionContract(contract);

            Assert.IsTrue(_algorithm.RemoveOptionContract(contract));
            Assert.IsFalse(_algorithm.Securities[contract].IsTradable);
        }

        [Test]
        public void AddDataOverloadsLinkedToUnderlying()
        {
            var first = _algorithm.AddData<LinkedData>(_spy);
            Assert.AreEqual(_spy, first.Symbol.Underlying);
            Assert.AreEqual(typeof(LinkedData), _algorithm.SubscriptionManager.SubscriptionDataConfigService.GetSubscriptionDataConfigs(first.Symbol).Single().Type);

            var aapl = _algorithm.AddEquity("AAPL").Symbol;
            var second = _algorithm.AddData<LinkedData>(aapl, Resolution.Daily, true, 2m);
            Assert.AreEqual(Resolution.Daily, _algorithm.SubscriptionManager.SubscriptionDataConfigService.GetSubscriptionDataConfigs(second.Symbol).Single().Resolution);
            Assert.AreEqual(2m, second.Leverage);

            var ibm = _algorithm.AddEquity("IBM").Symbol;
            var third = _algorithm.AddData<LinkedData>(ibm, Resolution.Daily, DateTimeZone.Utc);
            Assert.AreEqual(DateTimeZone.Utc, third.Exchange.TimeZone);
        }

        [Test]
        public void AddDataWithCustomPropertiesAndHours()
        {
            var properties = new SymbolProperties("Custom Data", "USD", 2m, 0.05m, 10m, "CUSTOM");
            var hours = SecurityExchangeHours.AlwaysOpen(TimeZones.Utc);

            var security = _algorithm.AddData<UnlinkedData>("CUSTOMTICKER", properties, hours, Resolution.Daily);

            Assert.AreEqual(2m, security.SymbolProperties.ContractMultiplier);
            Assert.AreEqual(10m, security.SymbolProperties.LotSize);
            Assert.IsTrue(security.Exchange.Hours.IsMarketAlwaysOpen);
        }

        [Test]
        public void ShortableReturnsTrueWithoutShortableLimitsAndHonoursProvider()
        {
            Assert.IsTrue(_algorithm.Shortable(_spy));

            _algorithm.Securities[_spy].SetShortableProvider(new FixedShortableProvider(100));
            Assert.IsTrue(_algorithm.Shortable(_spy, 50));
            Assert.IsTrue(_algorithm.Shortable(_spy, -100));
            Assert.IsFalse(_algorithm.Shortable(_spy, 101));

            _algorithm.Securities[_spy].SetShortableProvider(new FixedShortableProvider(0));
            Assert.IsFalse(_algorithm.Shortable(_spy, 1));
        }

        [Test]
        public void RunCommandHandlesUnregisteredAndFailingCommands()
        {
            _algorithm.LogMessages = new ConcurrentQueue<string>();
            _algorithm.AddCommand<ThrowingCommand>();

            var unregistered = _algorithm.RunCommand(new CallbackCommand { Type = "Unknown", Payload = "{}" });
            Assert.IsNull(unregistered.Success);
            Assert.AreEqual("Unknown", unregistered.CommandName);

            var failing = _algorithm.RunCommand(new CallbackCommand { Type = nameof(ThrowingCommand), Payload = "{}" });
            Assert.IsNull(failing.Success);
            _algorithm.RunCommand(new CallbackCommand { Type = nameof(ThrowingCommand), Payload = "{}" });

            Assert.AreEqual(1, _algorithm.LogMessages.Count(x => x.Contains("Detected unregistered command type 'Unknown'")));
            Assert.AreEqual(1, _algorithm.LogMessages.Count(x => x.Contains($"Unexpected error running command '{nameof(ThrowingCommand)}'")));
        }

        [Test]
        public void LinkAndBroadcastRejectUnexpectedCommandTypes()
        {
            Assert.Throws<ArgumentException>(() => _algorithm.Link("not a command"));
            Assert.Throws<ArgumentException>(() => _algorithm.BroadcastCommand(42));
        }

        [Test]
        public void SummaryStatisticsAreBufferedUntilStatisticsServiceIsSet()
        {
            _algorithm.SetSummaryStatistic("string", "value");
            _algorithm.SetSummaryStatistic("int", 1);
            _algorithm.SetSummaryStatistic("double", 1.5d);
            _algorithm.SetSummaryStatistic("decimal", 2.5m);

            var service = new FakeStatisticsService();
            _algorithm.SetStatisticsService(service);

            Assert.AreEqual("value", service.Summary["string"]);
            Assert.AreEqual("1", service.Summary["int"]);
            Assert.AreEqual("1.5", service.Summary["double"]);
            Assert.AreEqual("2.5", service.Summary["decimal"]);

            _algorithm.SetSummaryStatistic("after", "direct");
            Assert.AreEqual("direct", service.Summary["after"]);

            var other = new FakeStatisticsService();
            _algorithm.SetStatisticsService(other);
            _algorithm.SetSummaryStatistic("again", "x");
            Assert.IsFalse(other.Summary.ContainsKey("again"));
            Assert.AreSame(service.StatisticsResults(), _algorithm.Statistics);
        }

        [Test]
        public void RuntimeStatisticsOverloadsUseInvariantFormatting()
        {
            _algorithm.SetRuntimeStatistic("string", "value");
            _algorithm.SetRuntimeStatistic("decimal", 1.25m);
            _algorithm.SetRuntimeStatistic("int", 3);
            _algorithm.SetRuntimeStatistic("double", 4.5d);

            Assert.AreEqual("value", _algorithm.RuntimeStatistics["string"]);
            Assert.AreEqual("1.25", _algorithm.RuntimeStatistics["decimal"]);
            Assert.AreEqual("3", _algorithm.RuntimeStatistics["int"]);
            Assert.AreEqual("4.5", _algorithm.RuntimeStatistics["double"]);
        }

        [Test]
        public void RecordAndScalarPlotOverloadsAddPoints()
        {
            _algorithm.Record("recordInt", 1);
            _algorithm.Record("recordDouble", 2.5d);
            _algorithm.Record("recordDecimal", 3.5m);
            _algorithm.Plot("plotFloat", 4.5f);
            _algorithm.Plot("Custom", "chartFloat", 5.5f);

            var charts = _algorithm.GetChartUpdates().ToList();
            Assert.AreEqual(1m, LastScalar(charts, "Strategy Equity", "recordInt"));
            Assert.AreEqual(2.5m, LastScalar(charts, "Strategy Equity", "recordDouble"));
            Assert.AreEqual(3.5m, LastScalar(charts, "Strategy Equity", "recordDecimal"));
            Assert.AreEqual(4.5m, LastScalar(charts, "Strategy Equity", "plotFloat"));
            Assert.AreEqual(5.5m, LastScalar(charts, "Custom", "chartFloat"));
        }

        [Test]
        public void CandlestickPlotOverloadsAddCandles()
        {
            _algorithm.Plot("candleDouble", 1d, 2d, 0.5d, 1.5d);
            _algorithm.Plot("candleFloat", 1f, 2f, 0.5f, 1.5f);
            _algorithm.Plot("candleInt", 1, 3, 0, 2);
            _algorithm.Plot("candleDecimal", 1m, 2m, 0.5m, 1.5m);
            _algorithm.Plot("Candles", "chartDouble", 1d, 2d, 0.5d, 1.5d);
            _algorithm.Plot("Candles", "chartFloat", 1f, 2f, 0.5f, 1.5f);
            _algorithm.Plot("Candles", "chartInt", 1, 3, 0, 2);
            var bar = new TradeBar(_algorithm.Time, _spy, 10m, 12m, 9m, 11m, 100);
            _algorithm.Plot("barSeries", bar);
            _algorithm.Plot("Candles", "chartBar", bar);

            var charts = _algorithm.GetChartUpdates().ToList();
            AssertCandle(charts, "Strategy Equity", "candleDouble", 1m, 2m, 0.5m, 1.5m);
            AssertCandle(charts, "Strategy Equity", "candleFloat", 1m, 2m, 0.5m, 1.5m);
            AssertCandle(charts, "Strategy Equity", "candleInt", 1m, 3m, 0m, 2m);
            AssertCandle(charts, "Strategy Equity", "candleDecimal", 1m, 2m, 0.5m, 1.5m);
            AssertCandle(charts, "Candles", "chartDouble", 1m, 2m, 0.5m, 1.5m);
            AssertCandle(charts, "Candles", "chartFloat", 1m, 2m, 0.5m, 1.5m);
            AssertCandle(charts, "Candles", "chartInt", 1m, 3m, 0m, 2m);
            AssertCandle(charts, "Strategy Equity", "barSeries", 10m, 12m, 9m, 11m);
            AssertCandle(charts, "Candles", "chartBar", 10m, 12m, 9m, 11m);
        }

        [TestCase("Assets Sales Volume", "anything")]
        [TestCase("Strategy Equity", "Equity")]
        [TestCase("Capacity", "Strategy Capacity")]
        public void PlottingReservedNamesThrows(string chart, string series)
        {
            Assert.Throws<ArgumentException>(() => _algorithm.Plot(chart, series, 1m));
        }

        [Test]
        public void AddSeriesCreatesChartWithRequestedSeriesType()
        {
            _algorithm.AddSeries("NewChart", "candles", SeriesType.Candle, "%");
            _algorithm.AddSeries("NewChart", "lines", SeriesType.Line);

            var chart = _algorithm.GetChartUpdates().Single(x => x.Name == "NewChart");
            Assert.IsInstanceOf<CandlestickSeries>(chart.Series["candles"]);
            Assert.AreEqual("%", chart.Series["candles"].Unit);
            Assert.AreEqual(SeriesType.Line, chart.Series["lines"].SeriesType);
        }

        [Test]
        public void AddAlphaAndRiskManagementComposeModels()
        {
            _algorithm.AddAlpha(new ConstantAlphaModel(InsightType.Price, InsightDirection.Up, TimeSpan.FromDays(1)));
            Assert.IsInstanceOf<ConstantAlphaModel>(_algorithm.Alpha);
            _algorithm.AddAlpha(new NullAlphaModel());
            Assert.IsInstanceOf<CompositeAlphaModel>(_algorithm.Alpha);
            _algorithm.AddAlpha(new NullAlphaModel());
            Assert.IsInstanceOf<CompositeAlphaModel>(_algorithm.Alpha);

            _algorithm.AddRiskManagement(new MaximumDrawdownPercentPerSecurity());
            Assert.IsInstanceOf<MaximumDrawdownPercentPerSecurity>(_algorithm.RiskManagement);
            _algorithm.AddRiskManagement(new MaximumDrawdownPercentPortfolio());
            Assert.IsInstanceOf<CompositeRiskManagementModel>(_algorithm.RiskManagement);
            _algorithm.AddRiskManagement(new MaximumDrawdownPercentPortfolio());
            Assert.IsInstanceOf<CompositeRiskManagementModel>(_algorithm.RiskManagement);

            _algorithm.AddUniverseSelection(new ManualUniverseSelectionModel(_spy));
            _algorithm.AddUniverseSelection(new ManualUniverseSelectionModel(_spy));
            Assert.IsInstanceOf<CompositeUniverseSelectionModel>(_algorithm.UniverseSelection);
            _algorithm.AddUniverseSelection(new ManualUniverseSelectionModel(_spy));
            Assert.IsInstanceOf<CompositeUniverseSelectionModel>(_algorithm.UniverseSelection);
        }

        [Test]
        public void EmitInsightsDuringWarmupIsIgnoredWithSingleWarning()
        {
            _algorithm.ErrorMessages = new ConcurrentQueue<string>();
            var emitted = 0;
            _algorithm.InsightsGenerated += (sender, args) => emitted++;
            Assume.That(_algorithm.IsWarmingUp, "Warm up period expected to be active");

            _algorithm.EmitInsights(Insight.Price(_spy, TimeSpan.FromDays(1), InsightDirection.Up));
            _algorithm.EmitInsights(Insight.Price(_spy, TimeSpan.FromDays(1), InsightDirection.Up));

            Assert.AreEqual(0, emitted);
            Assert.AreEqual(1, _algorithm.ErrorMessages.Count(x => x.Contains("insights emitted during algorithm warmup are ignored")));
        }

        [Test]
        public void EmitInsightsDiscardsDelistedSecuritiesAndSetsReferenceValues()
        {
            _algorithm.SetFinishedWarmingUp();
            var aapl = _algorithm.AddEquity("AAPL").Symbol;
            var ibm = _algorithm.AddEquity("IBM").Symbol;
            _algorithm.Securities[aapl].IsDelisted = true;
            _algorithm.Securities[ibm].SetMarketPrice(new TradeBar(_algorithm.Time, ibm, 1m, 1m, 1m, 1m, 1));
            Insight[] generated = null;
            _algorithm.InsightsGenerated += (sender, args) => generated = args.Insights;

            _algorithm.EmitInsights(
                Insight.Price(_spy, TimeSpan.FromDays(1), InsightDirection.Up),
                Insight.Price(aapl, TimeSpan.FromDays(1), InsightDirection.Up),
                new Insight(ibm, TimeSpan.FromDays(1), InsightType.Volatility, InsightDirection.Up));

            Assert.IsNotNull(generated);
            CollectionAssert.AreEqual(new[] { _spy, ibm }, generated.Select(x => x.Symbol));
            Assert.AreEqual(_algorithm.Securities[ibm].VolatilityModel.Volatility, generated[1].ReferenceValue);
        }

        private static void AssertMessages(string[] expected, ConcurrentQueue<string> messages)
        {
            var actual = messages.ToArray();
            Assert.AreEqual(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                StringAssert.EndsWith(" " + expected[i], actual[i]);
            }
        }

        private static decimal? LastScalar(List<Chart> charts, string chart, string series)
        {
            var points = charts.Single(x => x.Name == chart).Series[series].Values;
            return ((ChartPoint)points.Last()).y;
        }

        private static void AssertCandle(List<Chart> charts, string chart, string series, decimal open, decimal high, decimal low, decimal close)
        {
            var candle = (Candlestick)charts.Single(x => x.Name == chart).Series[series].Values.Last();
            Assert.AreEqual(open, candle.Open);
            Assert.AreEqual(high, candle.High);
            Assert.AreEqual(low, candle.Low);
            Assert.AreEqual(close, candle.Close);
        }

        private class HiddenOnDataAlgorithm : QCAlgorithm
        {
            public int Calls { get; private set; }

            public new void OnData(Slice slice)
            {
                Calls++;
            }
        }

        private class ThrowingCommand : Command
        {
            public override bool? Run(IAlgorithm algorithm)
            {
                throw new InvalidOperationException("command failure");
            }
        }

        private class FixedShortableProvider : IShortableProvider
        {
            private readonly long _quantity;

            public FixedShortableProvider(long quantity)
            {
                _quantity = quantity;
            }

            public decimal FeeRate(Symbol symbol, DateTime localTime) => 0m;

            public decimal RebateRate(Symbol symbol, DateTime localTime) => 0m;

            public long? ShortableQuantity(Symbol symbol, DateTime localTime) => _quantity;
        }

        private class FakeStatisticsService : IStatisticsService
        {
            private readonly StatisticsResults _results = new StatisticsResults();

            public Dictionary<string, string> Summary { get; } = new Dictionary<string, string>();

            public StatisticsResults StatisticsResults() => _results;

            public void SetSummaryStatistic(string name, string value) => Summary[name] = value;
        }
    }
}
