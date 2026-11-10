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
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.Results.Analysis;
using QuantConnect.Lean.Engine.Results.Analysis.Analyses;
using QuantConnect.Lean.Engine.Results.Analysis.Analyses.Messages;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Engine.Results.Analysis
{
    [TestFixture]
    public class ResultsAnalysesCoverageTests
    {
        private static readonly Language[] Languages = { Language.CSharp, Language.Python };

        // Analyses that aggregate sub-analyses or match by regex instead of ExpectedMessageText
        private static readonly HashSet<Type> SpecialMessageAnalyses = new()
        {
            typeof(BrokerageModelRefusedToSubmitOrderOrderResponseErrorAnalysis),
            typeof(BrokerageModelRefusedToUpdateOrderOrderResponseErrorAnalysis),
            typeof(AlgorithmWarmingUpOrderResponseErrorAnalysis),
        };

        private static IEnumerable<Type> MessageAnalysisTypes() => typeof(MessageAnalysis).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsNested && typeof(MessageAnalysis).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) != null && !SpecialMessageAnalyses.Contains(t))
            .OrderBy(t => t.FullName);

        private static IEnumerable<TestCaseData> MessageAnalysisCases() => MessageAnalysisTypes()
            .SelectMany(t => Languages.Select(l => new TestCaseData(t, l).SetName($"MessageAnalysis_{t.Name}_{l}")));

        private static IEnumerable<TestCaseData> OrderResponseErrorAnalysisCases() => MessageAnalysisTypes()
            .Where(t => typeof(OrderResponseErrorAnalysis).IsAssignableFrom(t))
            .SelectMany(t => Languages.Select(l => new TestCaseData(t, l).SetName($"OrderResponseError_{t.Name}_{l}")));

        private static string[] GetExpectedMessageText(MessageAnalysis analysis)
        {
            var property = analysis.GetType().GetProperty("ExpectedMessageText", BindingFlags.Instance | BindingFlags.NonPublic);
            return (string[])property.GetValue(analysis);
        }

        private static string BuildMatchingMessage(MessageAnalysis analysis)
        {
            return "Order Error: " + string.Join("SPY 10", GetExpectedMessageText(analysis));
        }

        [TestCaseSource(nameof(MessageAnalysisCases))]
        public void MessageAnalysisMatchesItsExpectedMessage(Type analysisType, Language language)
        {
            var analysis = (MessageAnalysis)Activator.CreateInstance(analysisType);
            var message = BuildMatchingMessage(analysis);

            var finding = analysis.Run(new[] { "Unrelated log line", message }, language).Single();

            Assert.AreEqual(analysisType.Name, finding.Name);
            Assert.AreEqual(analysis.Issue, finding.Issue);
            Assert.AreEqual(message, finding.Sample);
            Assert.IsNull(finding.Count);
            Assert.IsNotEmpty(finding.Solutions);
            Assert.IsTrue(finding.Solutions.All(s => !string.IsNullOrWhiteSpace(s)));
        }

        [TestCaseSource(nameof(MessageAnalysisCases))]
        public void MessageAnalysisMatchingIsCaseInsensitiveAndCountsRepeatedMessages(Type analysisType, Language language)
        {
            var analysis = (MessageAnalysis)Activator.CreateInstance(analysisType);
            var message = BuildMatchingMessage(analysis);

            var finding = analysis.Run(new[] { message.ToUpperInvariant(), message.ToLowerInvariant(), message }, language).Single();

            Assert.AreEqual(message.ToUpperInvariant(), finding.Sample);
            Assert.AreEqual(3, finding.Count);
        }

        [TestCaseSource(nameof(MessageAnalysisCases))]
        public void MessageAnalysisIgnoresMessagesMissingAPart(Type analysisType, Language language)
        {
            var analysis = (MessageAnalysis)Activator.CreateInstance(analysisType);
            var parts = GetExpectedMessageText(analysis);
            // only the first part (if more than one), or nothing at all
            var partial = parts.Length > 1 ? parts[0] : "Something else entirely";

            var finding = analysis.Run(new[] { partial }, language).Single();

            Assert.IsNull(finding.Sample);
            Assert.IsNull(finding.Count);
            Assert.IsEmpty(finding.Solutions);
        }

        [TestCaseSource(nameof(MessageAnalysisCases))]
        public void MessageAnalysisRunsFromRunParameters(Type analysisType, Language language)
        {
            var analysis = (MessageAnalysis)Activator.CreateInstance(analysisType);
            var message = BuildMatchingMessage(analysis);
            var orderEvent = new OrderEvent(1, Symbols.SPY, new DateTime(2024, 1, 3), OrderStatus.Invalid, OrderDirection.Buy, 0, 0, OrderFee.Zero, message);
            var result = new Result { OrderEvents = new List<OrderEvent> { orderEvent }, Orders = new Dictionary<int, Order>() };
            var parameters = new ResultsAnalysisRunParameters(result, null, language, new[] { message }, new(), new());

            var finding = analysis.Run(parameters).Single();

            // order response error analyses read the order events, the rest read the logs: both contain the message
            Assert.AreEqual(message, finding.Sample);
            Assert.IsNotEmpty(finding.Solutions);
        }

        [TestCaseSource(nameof(OrderResponseErrorAnalysisCases))]
        public void OrderResponseErrorAnalysisOnlyMatchesInvalidOrderEvents(Type analysisType, Language language)
        {
            var analysis = (OrderResponseErrorAnalysis)Activator.CreateInstance(analysisType);
            var message = BuildMatchingMessage(analysis);
            var time = new DateTime(2024, 1, 3);
            var events = new List<OrderEvent>
            {
                new(1, Symbols.SPY, time, OrderStatus.Filled, OrderDirection.Buy, 1, 1, OrderFee.Zero, message),
                new(2, Symbols.SPY, time, OrderStatus.Invalid, OrderDirection.Buy, 0, 0, OrderFee.Zero, null),
                new(3, Symbols.SPY, time, OrderStatus.Invalid, OrderDirection.Buy, 0, 0, OrderFee.Zero, "unrelated"),
            };

            var noFinding = analysis.Run(events, language).Single();
            Assert.IsNull(noFinding.Sample);
            Assert.IsEmpty(noFinding.Solutions);

            events.Add(new(4, Symbols.SPY, time, OrderStatus.Invalid, OrderDirection.Buy, 0, 0, OrderFee.Zero, message));
            var finding = analysis.Run(events, language).Single();
            Assert.AreEqual(message, finding.Sample);
            Assert.IsNotEmpty(finding.Solutions);
        }

        [Test]
        public void MessageAnalysisSolutionsDifferByLanguageWhenTheyContainCode()
        {
            var analysis = new MarginCallsAnalysis();
            var python = analysis.Run(new[] { "Executed MarginCallOrder for SPY" }, Language.Python).Single();
            var csharp = analysis.Run(new[] { "Executed MarginCallOrder for SPY" }, Language.CSharp).Single();

            Assert.AreEqual(4, python.Solutions.Count);
            Assert.IsTrue(python.Solutions.Any(s => s.Contains("on_margin_call_warning")));
            Assert.IsTrue(python.Solutions.Any(s => s.Contains("self.portfolio.margin_call_model = MarginCallModel.NULL")));
            Assert.IsTrue(csharp.Solutions.Any(s => s.Contains("OnMarginCallWarning")));
            Assert.IsTrue(csharp.Solutions.Any(s => s.Contains("Portfolio.MarginCallModel = MarginCallModel.Null;")));
        }

        private static OrderEvent InvalidEvent(int id, string message)
            => new(id, Symbols.SPY, new DateTime(2024, 1, 3), OrderStatus.Invalid, OrderDirection.Buy, 0, 0, OrderFee.Zero, message);

        [TestCase(Language.CSharp)]
        [TestCase(Language.Python)]
        public void BrokerageModelRefusedToSubmitAggregatesTheMatchingSubAnalyses(Language language)
        {
            var analysis = new BrokerageModelRefusedToSubmitOrderOrderResponseErrorAnalysis();
            var tradier = "BrokerageModel declared unable to submit order: [1] Tradier does not support GTC orders for short sales.";
            var alpaca = "BrokerageModel declared unable to submit order: [2] Alpaca does not support extended market hours trading.";

            var findings = analysis.Run(new List<OrderEvent> { InvalidEvent(1, tradier), InvalidEvent(2, alpaca), InvalidEvent(3, alpaca) }, language);

            Assert.AreEqual(2, findings.Count);
            var alpacaFinding = findings.Single(f => f.Name.EndsWith("TradingOutsideRegularHoursNotSupportedAnalysis"));
            Assert.AreEqual(nameof(BrokerageModelRefusedToSubmitOrderOrderResponseErrorAnalysis) + " / TradingOutsideRegularHoursNotSupportedAnalysis", alpacaFinding.Name);
            Assert.AreEqual(alpaca, alpacaFinding.Sample);
            Assert.AreEqual(2, alpacaFinding.Count);
            var tradierFinding = findings.Single(f => f.Name.EndsWith("ShortOrderIsGtcAnalysis"));
            Assert.AreEqual(tradier, tradierFinding.Sample);
            Assert.IsNull(tradierFinding.Count);
        }

        [Test]
        public void BrokerageModelRefusedToSubmitReturnsNothingWithoutRefusals()
        {
            var analysis = new BrokerageModelRefusedToSubmitOrderOrderResponseErrorAnalysis();

            Assert.IsEmpty(analysis.Run(new List<OrderEvent> { InvalidEvent(1, "Insufficient buying power to complete order") }, Language.CSharp));
            Assert.IsEmpty(analysis.Run(new List<OrderEvent>(), Language.Python));
        }

        [Test]
        public void BrokerageModelRefusedToSubmitReturnsNothingWhenNoSubAnalysisMatches()
        {
            var analysis = new BrokerageModelRefusedToSubmitOrderOrderResponseErrorAnalysis();

            var findings = analysis.Run(new List<OrderEvent> { InvalidEvent(1, "BrokerageModel declared unable to submit order: [1] unknown reason") }, Language.CSharp);

            Assert.IsEmpty(findings);
        }

        [TestCase(Language.CSharp)]
        [TestCase(Language.Python)]
        public void BrokerageModelRefusedToUpdateAggregatesTheMatchingSubAnalyses(Language language)
        {
            var analysis = new BrokerageModelRefusedToUpdateOrderOrderResponseErrorAnalysis();
            var message = "BrokerageModel declared unable to update order: 7 Brokerage does not support order update. ";
            var result = new Result { OrderEvents = new List<OrderEvent> { InvalidEvent(7, message) } };

            var findings = analysis.Run(new ResultsAnalysisRunParameters(result, null, language, null, null, null));

            var finding = findings.Single();
            Assert.AreEqual(nameof(BrokerageModelRefusedToUpdateOrderOrderResponseErrorAnalysis) + " / OrderUpdateNotSupportedAnalysis", finding.Name);
            Assert.AreEqual(message, finding.Sample);
            Assert.IsNotEmpty(finding.Solutions);
            Assert.IsEmpty(analysis.Run(new List<OrderEvent> { InvalidEvent(1, "other") }, language));
        }

        [TestCase(Language.CSharp, "ExerciseOption(_contractSymbol, quantity);")]
        [TestCase(Language.Python, "self.exercise_option(self.contract_symbol, quantity)")]
        public void ExchangeNotOpenDetectsOptionExercise(Language language, string expectedCode)
        {
            var analysis = new ExchangeNotOpenOrderResponseErrorAnalysis();
            var log = "Order Error: OptionExercise order and exchange not open.";
            var result = analysis.Run(new ResultsAnalysisRunParameters(null, null, language, new[] { "noise", log }, null, null)).Single();

            Assert.AreEqual(log, result.Sample);
            Assert.AreEqual("ExerciseOptionWhileExchangeNotOpenAnalysis", result.Name);
            StringAssert.Contains(expectedCode, result.Solutions.Single());
        }

        [Test]
        public void ExchangeNotOpenDetectsMarketOnOpenForFutures()
        {
            var analysis = new ExchangeNotOpenOrderResponseErrorAnalysis();
            var log = "MarketOnOpen orders not supported for Future";

            var result = analysis.Run(new[] { log }, Language.CSharp).Single();

            Assert.AreEqual(log, result.Sample);
            Assert.AreEqual("MOCOrderForFutureOrFOPAnalysis", result.Name);
            Assert.AreEqual("Check if the exchange is open before you place the order.", result.Solutions.Single());
        }

        [Test]
        public void ExchangeNotOpenReturnsEmptyFindingWithoutMatches()
        {
            var analysis = new ExchangeNotOpenOrderResponseErrorAnalysis();

            var result = analysis.Run(new[] { "nothing to see" }, Language.CSharp).Single();

            Assert.AreEqual(nameof(ExchangeNotOpenOrderResponseErrorAnalysis), result.Name);
            Assert.IsNull(result.Sample);
            Assert.IsEmpty(result.Solutions);
        }

        [TestCase(Language.CSharp, "ShortableQuantity(_symbol)")]
        [TestCase(Language.Python, "self.shortable_quantity(self._symbol)")]
        public void ExceedsShortableQuantityDetectsTheMessage(Language language, string expectedCode)
        {
            var analysis = new ExceedsShortableQuantityOrderResponseErrorAnalysis();
            var message = "Order exceeds shortable quantity 100 for Symbol GME requested 200";
            var events = new List<OrderEvent> { InvalidEvent(1, message), InvalidEvent(2, message), InvalidEvent(3, null) };

            var result = analysis.Run(new ResultsAnalysisRunParameters(new Result { OrderEvents = events }, null, language, null, null, null)).Single();

            Assert.AreSame(events[0], result.Sample);
            Assert.AreEqual(2, result.Count);
            StringAssert.Contains(expectedCode, result.Solutions.Single());
            Assert.IsNull(analysis.Run(new List<OrderEvent> { InvalidEvent(1, "Order exceeds shortable quantity") }, language).Single().Sample);
        }

        [TestCase(Language.CSharp, "Settings.StalePriceTimeSpan = TimeSpan.FromMinutes(10);")]
        [TestCase(Language.Python, "self.settings.stale_price_time_span = timedelta(minutes=10)")]
        public void StaleOrderFillsDetectsStaleFills(Language language, string expectedCode)
        {
            var analysis = new StaleOrderFillsAnalysis();
            var stale = new OrderEvent(1, Symbols.SPY, new DateTime(2024, 1, 3), OrderStatus.Filled, OrderDirection.Buy, 1, 1, OrderFee.Zero,
                "WARNING: Fill at stale price (100), last data was 2 hours ago");
            var fresh = new OrderEvent(2, Symbols.SPY, new DateTime(2024, 1, 3), OrderStatus.Filled, OrderDirection.Buy, 1, 1, OrderFee.Zero, null);

            var result = analysis.Run(new ResultsAnalysisRunParameters(new Result { OrderEvents = new List<OrderEvent> { fresh, stale } }, null, language, null, null, null)).Single();

            Assert.AreSame(stale, result.Sample);
            Assert.IsNull(result.Count);
            Assert.AreEqual(2, result.Solutions.Count);
            StringAssert.Contains(expectedCode, result.Solutions[0]);
            Assert.IsNull(analysis.Run(new List<OrderEvent> { fresh }, language).Single().Sample);
        }

        [TestCase(Language.CSharp, "OnMarginCallWarning")]
        [TestCase(Language.Python, "on_margin_call_warning")]
        public void InsufficientBuyingPowerFormatsCodeForTheLanguage(Language language, string unused)
        {
            var analysis = new InsufficientBuyingPowerOrderResponseErrorAnalysis();
            var result = analysis.Run(new List<OrderEvent> { InvalidEvent(1, "Order Error: id: 1, Insufficient buying power to complete order (Value:1000)") }, language).Single();

            Assert.AreEqual(2, result.Solutions.Count);
            if (language == Language.Python)
            {
                StringAssert.Contains("self.settings.free_portfolio_value_percentage = 0.05", result.Solutions[1]);
                StringAssert.Contains("`set_holdings`", result.Solutions[1]);
            }
            else
            {
                StringAssert.Contains("Settings.FreePortfolioValuePercentage = 0.05m;", result.Solutions[1]);
                StringAssert.Contains("`SetHoldings`", result.Solutions[1]);
            }
        }

        #region TakeProfitAndStopLossOrdersAnalysis

        private static readonly DateTime OrderTime = new(2024, 1, 3, 15, 0, 0);

        private static (LimitOrder TakeProfit, StopMarketOrder StopLoss) CreatePair(OrderStatus tpStatus, OrderStatus slStatus, int id, DateTime? time = null)
        {
            var createdTime = time ?? OrderTime;
            var tp = new LimitOrder(Symbols.SPY, -10, 110m, createdTime) { Id = id, Status = tpStatus };
            var sl = new StopMarketOrder(Symbols.SPY, -10, 90m, createdTime) { Id = id + 1, Status = slStatus };
            return (tp, sl);
        }

        [TestCase(Language.CSharp, "Liquidate(symbol: \"SPY\", -1, tag: \"take profit\");")]
        [TestCase(Language.Python, "self.liquidate(symbol=\"SPY\", -1, tag=\"take profit\")")]
        public void TakeProfitAndStopLossDetectsBothFilled(Language language, string expectedCode)
        {
            var (tp, sl) = CreatePair(OrderStatus.Filled, OrderStatus.Filled, 1);
            var orders = new Dictionary<int, Order> { [tp.Id] = tp, [sl.Id] = sl };
            var analysis = new TakeProfitAndStopLossOrdersAnalysis();

            var findings = analysis.Run(new ResultsAnalysisRunParameters(new Result { Orders = orders }, null, language, null, null, null));

            var finding = findings.Single();
            Assert.AreEqual(nameof(TakeProfitAndStopLossOrdersAnalysis) + " / TakeProfitAndStopLossBothFilledAnalysis", finding.Name);
            CollectionAssert.AreEquivalent(new Order[] { tp, sl }, (IEnumerable<Order>)finding.Sample);
            Assert.IsNull(finding.Count);
            Assert.AreEqual(3, finding.Solutions.Count);
            StringAssert.Contains(expectedCode, finding.Solutions[2]);
            Assert.IsTrue(analysis.IsStateBased);
        }

        [TestCase(Language.CSharp, "_stopLoss?.Cancel();")]
        [TestCase(Language.Python, "self._stop_loss.cancel()")]
        public void TakeProfitAndStopLossDetectsDanglingOrders(Language language, string expectedCode)
        {
            // the take profit filled and the stop loss was left submitted
            var (tp1, sl1) = CreatePair(OrderStatus.Filled, OrderStatus.Submitted, 1);
            // the take profit filled and the stop loss was canceled later on
            var (tp2, sl2) = CreatePair(OrderStatus.Filled, OrderStatus.Canceled, 3, OrderTime.AddHours(1));
            tp2.LastFillTime = OrderTime.AddHours(2);
            sl2.CanceledTime = OrderTime.AddHours(3);

            var findings = new TakeProfitAndStopLossOrdersAnalysis().Run(new List<Order> { tp1, sl1, tp2, sl2 }, language);

            var finding = findings.Single();
            Assert.AreEqual(nameof(TakeProfitAndStopLossOrdersAnalysis) + " / TakeProfitOrStopLossNotCanceledAnalysis", finding.Name);
            CollectionAssert.AreEqual(new Order[] { tp1, sl1 }, (IEnumerable<Order>)finding.Sample);
            Assert.AreEqual(2, finding.Count);
            StringAssert.Contains(expectedCode, finding.Solutions.Single());
        }

        [Test]
        public void TakeProfitAndStopLossAcceptsPairsCanceledWhenTheOtherFilled()
        {
            var (tp, sl) = CreatePair(OrderStatus.Canceled, OrderStatus.Filled, 1);
            sl.LastFillTime = OrderTime.AddMinutes(5);
            tp.CanceledTime = OrderTime.AddMinutes(5);
            // neither filled yet
            var (tp2, sl2) = CreatePair(OrderStatus.Submitted, OrderStatus.Submitted, 3, OrderTime.AddDays(1));

            var findings = new TakeProfitAndStopLossOrdersAnalysis().Run(new List<Order> { tp, sl, tp2, sl2 }, Language.CSharp);

            Assert.IsEmpty(findings);
        }

        [Test]
        public void TakeProfitAndStopLossIgnoresOrdersThatAreNotPairs()
        {
            // two limit orders with the same fingerprint are not a TP/SL pair
            var limit1 = new LimitOrder(Symbols.SPY, -10, 110m, OrderTime) { Id = 1, Status = OrderStatus.Filled };
            var limit2 = new LimitOrder(Symbols.SPY, -10, 120m, OrderTime) { Id = 2, Status = OrderStatus.Filled };
            // a TP/SL with different quantities
            var tp = new LimitOrder(Symbols.AAPL, -10, 110m, OrderTime) { Id = 3, Status = OrderStatus.Filled };
            var sl = new StopMarketOrder(Symbols.AAPL, -5, 90m, OrderTime) { Id = 4, Status = OrderStatus.Filled };
            // a lone market order
            var market = new MarketOrder(Symbols.SPY, 10, OrderTime.AddDays(1)) { Id = 5, Status = OrderStatus.Filled };

            var findings = new TakeProfitAndStopLossOrdersAnalysis().Run(new List<Order> { limit1, limit2, tp, sl, market }, Language.Python);

            Assert.IsEmpty(findings);
        }

        #endregion

        #region CrisisEventsAnalysis

        private static SortedList<DateTime, decimal> BuildCurve(DateTime start, DateTime end, Func<int, decimal> dailyReturn)
        {
            var curve = new SortedList<DateTime, decimal>();
            var value = 100000m;
            var i = 0;
            for (var date = start; date <= end; date = date.AddDays(1), i++)
            {
                curve[date] = value;
                value *= 1 + dailyReturn(i);
            }
            return curve;
        }

        private static QCAlgorithmForAnalysis CreateAlgorithm()
        {
            var algorithm = new QCAlgorithmForAnalysis();
            algorithm.SetRiskFreeInterestRateModel(new ConstantRiskFreeRateInterestRateModel(0m));
            return algorithm;
        }

        private class QCAlgorithmForAnalysis : QuantConnect.Algorithm.QCAlgorithm
        {
        }

        [Test]
        public void CrisisEventsReportsUnderperformanceDuringCrises()
        {
            var start = new DateTime(2010, 1, 1);
            var end = new DateTime(2012, 12, 31);
            // the strategy goes nowhere while the benchmark grinds higher
            var strategy = BuildCurve(start, end, i => i % 2 == 0 ? 0.01m : -0.01m);
            var benchmark = BuildCurve(start, end, i => i % 2 == 0 ? 0.01m : 0.005m);
            var analysis = new CrisisEventsAnalysis();

            var result = analysis.Run(new ResultsAnalysisRunParameters(null, CreateAlgorithm(), Language.CSharp, null, strategy, benchmark)).Single();

            Assert.IsFalse(analysis.RunsInRun);
            Assert.IsNotNull(result.Sample);
            Assert.AreEqual("Flash Crash 2010", result.Sample.GetType().GetProperty("CrisisEvent").GetValue(result.Sample));
            var backtestSharpe = (double)result.Sample.GetType().GetProperty("BacktestSharpe").GetValue(result.Sample);
            var benchmarkSharpe = (double)result.Sample.GetType().GetProperty("BenchmarkSharpe").GetValue(result.Sample);
            Assert.Less(backtestSharpe, benchmarkSharpe);
            // Flash Crash, Fukushima, U.S. Credit Downgrade, ECB IR Event and the 2010-2012 recovery fall inside the backtest
            Assert.AreEqual(5, result.Count);
            Assert.AreEqual(1, result.Solutions.Count);
        }

        [Test]
        public void CrisisEventsDoesNotReportWhenTheStrategyOutperforms()
        {
            var start = new DateTime(2020, 1, 1);
            var end = new DateTime(2020, 12, 31);
            var strategy = BuildCurve(start, end, i => i % 2 == 0 ? 0.01m : 0.005m);
            var benchmark = BuildCurve(start, end, i => i % 2 == 0 ? 0.01m : -0.01m);

            var result = new CrisisEventsAnalysis().Run(CreateAlgorithm(), strategy, benchmark).Single();

            Assert.IsNull(result.Sample);
            Assert.IsNull(result.Count);
            Assert.IsEmpty(result.Solutions);
        }

        [Test]
        public void CrisisEventsSkipsEventsOutsideTheBacktest()
        {
            // too short to contain any crisis event
            var start = new DateTime(2013, 1, 1);
            var end = new DateTime(2013, 6, 1);
            var strategy = BuildCurve(start, end, i => -0.001m * (i % 3));
            var benchmark = BuildCurve(start, end, i => 0.002m * (i % 3));

            var result = new CrisisEventsAnalysis().Run(CreateAlgorithm(), strategy, benchmark).Single();

            Assert.IsNull(result.Sample);
        }

        [Test]
        public void CrisisEventsHandlesEmptyCurves()
        {
            var curve = BuildCurve(new DateTime(2020, 1, 1), new DateTime(2020, 12, 31), i => 0.001m);

            Assert.IsNull(new CrisisEventsAnalysis().Run(null, new SortedList<DateTime, decimal>(), curve).Single().Sample);
            Assert.IsNull(new CrisisEventsAnalysis().Run(null, curve, new SortedList<DateTime, decimal>()).Single().Sample);
        }

        [Test]
        public void CrisisEventsSharpeRatioIsZeroForEmptySeries()
        {
            var curve = BuildCurve(new DateTime(2020, 1, 1), new DateTime(2020, 1, 10), i => 0.001m);
            var model = new ConstantRiskFreeRateInterestRateModel(0m);

            Assert.AreEqual((0d, 0d), CrisisEventsAnalysis.CalculateSharpeRatio(new SortedList<DateTime, decimal>(), curve, model));
            Assert.AreEqual((0d, 0d), CrisisEventsAnalysis.CalculateSharpeRatio(curve, new SortedList<DateTime, decimal>(), model));

            var (backtest, benchmark) = CrisisEventsAnalysis.CalculateSharpeRatio(curve, curve, model);
            Assert.AreEqual(backtest, benchmark);
        }

        #endregion

        #region MonteCarloPercentileAnalysis

        [Test]
        public void MonteCarloReturnsEmptyFindingForShortCurves()
        {
            var analysis = new MonteCarloPercentileAnalysis();

            Assert.IsNull(analysis.Run(new SortedList<DateTime, decimal>()).Single().Sample);
            Assert.IsNull(analysis.Run(new SortedList<DateTime, decimal> { [new DateTime(2024, 1, 1)] = 100m }).Single().Sample);
            Assert.IsFalse(analysis.RunsInRun);
        }

        [Test]
        public void MonteCarloDoesNotFlagCurvesWhereEverySimulationMatches()
        {
            // with 20 or fewer returns the single block is the whole curve, so every simulation equals the backtest
            var curve = BuildCurve(new DateTime(2024, 1, 1), new DateTime(2024, 1, 15), i => i % 2 == 0 ? 0.02m : -0.01m);

            var result = new MonteCarloPercentileAnalysis().Run(new ResultsAnalysisRunParameters(null, null, Language.CSharp, null, curve, null)).Single();

            Assert.IsNull(result.Sample);
            Assert.IsEmpty(result.Solutions);
        }

        [Test]
        public void MonteCarloFlagsALuckyEquityCurve()
        {
            // small losses every day and a single huge win on the last day, which the block bootstrap rarely samples
            var start = new DateTime(2024, 1, 1);
            var curve = BuildCurve(start, start.AddDays(60), i => i == 59 ? 1m : -0.001m);

            var result = new MonteCarloPercentileAnalysis().Run(curve).Single();

            Assert.IsNotNull(result.Sample);
            Assert.AreEqual(100m, result.Sample.GetType().GetProperty("Percentile").GetValue(result.Sample));
            Assert.AreEqual(1, result.Solutions.Count);
        }

        [Test]
        public void MonteCarloDoesNotFlagAnUnluckyEquityCurve()
        {
            // small wins every day and a single huge loss on the last day
            var start = new DateTime(2024, 1, 1);
            var curve = BuildCurve(start, start.AddDays(60), i => i == 59 ? -0.5m : 0.001m);

            var result = new MonteCarloPercentileAnalysis().Run(curve).Single();

            Assert.IsNull(result.Sample);
        }

        #endregion

        #region ParameterCountAnalysis

        [TestCase(10, Language.CSharp, false)]
        [TestCase(11, Language.CSharp, true)]
        [TestCase(25, Language.Python, true)]
        public void ParameterCountFlagsTooManyParameters(int count, Language language, bool expected)
        {
            var algorithm = CreateAlgorithm();
            algorithm.SetParameters(Enumerable.Range(0, count).ToDictionary(i => $"p{i}", i => i.ToString()));
            var analysis = new ParameterCountAnalysis();

            var result = analysis.Run(new ResultsAnalysisRunParameters(null, algorithm, language, null, null, null)).Single();

            Assert.IsFalse(analysis.RunsInRun);
            if (!expected)
            {
                Assert.IsNull(result.Sample);
                Assert.IsEmpty(result.Solutions);
                return;
            }
            Assert.AreEqual($"{count} Parameters Detected", result.Sample);
            var solution = result.Solutions.Single();
            StringAssert.Contains("| Numeric Comparison |", solution);
            StringAssert.Contains(language == Language.Python ? "`set_start_date`" : "`SetStartDate`", solution);
        }

        #endregion

        #region OrderFillsDuringExtendedMarketHoursAnalysis

        private static OrderEvent Fill(int id, Symbol symbol, DateTime utcTime, string message = null)
            => new(id, symbol, utcTime, OrderStatus.Filled, OrderDirection.Buy, 100m, 1, OrderFee.Zero, message);

        [TestCase(Language.CSharp, "if (IsMarketOpen(_symbol))")]
        [TestCase(Language.Python, "if self.is_market_open(self._symbol):")]
        public void OrderFillsDuringExtendedMarketHoursFlagsPreMarketFills(Language language, string expectedCode)
        {
            var algorithm = new AlgorithmStub();
            var spy = algorithm.AddEquity("SPY", Resolution.Minute, extendedMarketHours: true).Symbol;
            // 2024-01-03 is a Wednesday, New York is UTC-5
            var preMarket = Fill(1, spy, new DateTime(2024, 1, 3, 12, 0, 0));
            var afterHours = Fill(2, spy, new DateTime(2024, 1, 3, 23, 0, 0));
            var events = new List<OrderEvent>
            {
                Fill(3, spy, new DateTime(2024, 1, 3, 15, 0, 0)),
                // at the close counts as open
                Fill(4, spy, new DateTime(2024, 1, 3, 21, 0, 0)),
                // not filled
                new(5, spy, new DateTime(2024, 1, 3, 12, 0, 0), OrderStatus.Submitted, OrderDirection.Buy, 0, 0, OrderFee.Zero),
                // delisting liquidations are expected outside market hours
                Fill(6, spy, new DateTime(2024, 1, 3, 12, 0, 0), BrokerageTransactionHandler.LiquidateFromDelistingTag),
                // unknown security
                Fill(7, Symbols.AAPL, new DateTime(2024, 1, 3, 12, 0, 0)),
                preMarket,
                afterHours,
            };

            var analysis = new OrderFillsDuringExtendedMarketHoursAnalysis();
            var result = analysis.Run(new ResultsAnalysisRunParameters(new Result { OrderEvents = events }, algorithm, language, null, null, null)).Single();

            Assert.IsFalse(analysis.RunsInRun);
            Assert.AreSame(preMarket, result.Sample);
            Assert.AreEqual(2, result.Count);
            StringAssert.Contains(expectedCode, result.Solutions.Single());
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void OrderFillsDuringExtendedMarketHoursHandlesDailyMidnightFills(bool dailyPreciseEndTime, bool expectFinding)
        {
            var algorithm = new AlgorithmStub();
            algorithm.Settings.DailyPreciseEndTime = dailyPreciseEndTime;
            var security = algorithm.AddEquity("SPY", Resolution.Daily);
            security.SetMarketPrice(new TradeBar(new DateTime(2024, 1, 3), security.Symbol, 100, 100, 100, 100, 1000, Time.OneDay));
            // midnight in New York after the trading day of 2024-01-03
            var midnightFill = Fill(1, security.Symbol, new DateTime(2024, 1, 4, 5, 0, 0));

            var result = new OrderFillsDuringExtendedMarketHoursAnalysis().Run(algorithm, new List<OrderEvent> { midnightFill }, Language.CSharp).Single();

            Assert.AreEqual(expectFinding, result.Sample != null);
        }

        [Test]
        public void OrderFillsDuringExtendedMarketHoursReturnsEmptyFindingWithoutFills()
        {
            var result = new OrderFillsDuringExtendedMarketHoursAnalysis().Run(new AlgorithmStub(), new List<OrderEvent>(), Language.CSharp).Single();

            Assert.IsNull(result.Sample);
            Assert.IsEmpty(result.Solutions);
        }

        #endregion
    }
}
