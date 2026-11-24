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
using System.Linq;
using NodaTime;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Tests.Common.Data.UniverseSelection;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm.Framework.Alphas
{
    [TestFixture]
    public class PairsAndReturnsAlphaModelsCoverageTests
    {
        private static readonly DateTime Start = new DateTime(2021, 1, 4);

        #region HistoricalReturnsAlphaModel

        [Test]
        public void HistoricalReturnsEmitsInsightsFollowingTheRateOfChange()
        {
            var algorithm = CreateAlgorithm(new FixedHistoryProvider(_ => new[] { CreateSlice(Start.AddDays(-1), ("SPY", 100m)) }));
            var spy = algorithm.AddEquity("SPY", Resolution.Daily);
            var model = new HistoricalReturnsAlphaModel(1, Resolution.Daily);
            Assert.AreEqual("HistoricalReturnsAlphaModel(1,Daily)", model.Name);

            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy }, Array.Empty<Security>()));

            // only one sample from history: the indicator is not ready
            Assert.IsEmpty(model.Update(algorithm, null));

            PushBar(algorithm, spy.Symbol, Start, 110m);
            var up = model.Update(algorithm, null).Single();
            Assert.AreEqual(spy.Symbol, up.Symbol);
            Assert.AreEqual(InsightDirection.Up, up.Direction);
            Assert.AreEqual(InsightType.Price, up.Type);
            Assert.AreEqual(0.1, up.Magnitude.Value, 1e-9);
            Assert.AreEqual(TimeSpan.FromDays(1), up.Period);

            // no new sample: nothing new to emit
            Assert.IsEmpty(model.Update(algorithm, null));

            PushBar(algorithm, spy.Symbol, Start.AddDays(1), 99m);
            var down = model.Update(algorithm, null).Single();
            Assert.AreEqual(InsightDirection.Down, down.Direction);
            Assert.AreEqual(-0.1, down.Magnitude.Value, 1e-9);

            // a flat return cancels instead of emitting
            PushBar(algorithm, spy.Symbol, Start.AddDays(2), 99m);
            Assert.IsEmpty(model.Update(algorithm, null));
        }

        [Test]
        public void HistoricalReturnsStopsTrackingRemovedSecurities()
        {
            var algorithm = CreateAlgorithm(new FixedHistoryProvider(_ => Enumerable.Empty<Slice>()));
            var spy = algorithm.AddEquity("SPY", Resolution.Daily);
            var model = new HistoricalReturnsAlphaModel(2, Resolution.Daily);
            Assert.AreEqual("HistoricalReturnsAlphaModel(2,Daily)", model.Name);

            var consolidatorsBefore = ConsolidatorCount(algorithm, spy.Symbol);
            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy }, Array.Empty<Security>()));
            // adding the same security twice does not register it again
            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy }, Array.Empty<Security>()));
            Assert.AreEqual(consolidatorsBefore + 1, ConsolidatorCount(algorithm, spy.Symbol));

            for (var i = 0; i < 3; i++)
            {
                PushBar(algorithm, spy.Symbol, Start.AddDays(i), 100m + i);
            }
            var insight = model.Update(algorithm, null).Single();
            Assert.AreEqual(InsightDirection.Up, insight.Direction);
            Assert.AreEqual(TimeSpan.FromDays(2), insight.Period);

            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(Array.Empty<Security>(), new[] { spy }));
            Assert.AreEqual(consolidatorsBefore, ConsolidatorCount(algorithm, spy.Symbol));

            PushBar(algorithm, spy.Symbol, Start.AddDays(3), 200m);
            Assert.IsEmpty(model.Update(algorithm, null));
        }

        #endregion

        #region Pairs trading

        [Test]
        public void PearsonCorrelationSelectsTheMostCorrelatedPairAndTradesTheSpread()
        {
            var returns = new[] { 0.01, -0.02, 0.015, 0.03, -0.01, 0.02 };
            var noise = new[] { 0.001, -0.002, 0.003, -0.001, 0.002, -0.003 };
            var algorithm = CreateAlgorithm(new FixedHistoryProvider(_ => BuildHistory(returns, noise)));
            var spy = algorithm.AddEquity("SPY", Resolution.Daily);
            var aapl = algorithm.AddEquity("AAPL", Resolution.Daily);
            var ibm = algorithm.AddEquity("IBM", Resolution.Daily);
            var model = new PearsonCorrelationPairsTradingAlphaModel(returns.Length, Resolution.Daily, 1m, 0.5);

            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy, aapl, ibm }, Array.Empty<Security>()));

            CollectionAssert.AreEquivalent(new[] { spy, aapl, ibm }, model.Securities);
            var symbols = new[] { spy.Symbol, aapl.Symbol, ibm.Symbol };
            var passed = (from a in symbols from b in symbols where a != b && model.HasPassedTest(algorithm, a, b) select new[] { a, b }).ToList();
            Assert.AreEqual(1, passed.Count);
            CollectionAssert.AreEquivalent(new[] { spy.Symbol, aapl.Symbol }, passed[0]);

            AssertSpreadTrading(algorithm, model, spy.Symbol, aapl.Symbol);
            Assert.IsFalse(algorithm.DebugMessages.Any(x => x.Contains("does not have series of prices")));
        }

        [Test]
        public void PearsonCorrelationRequiresMinimumCorrelation()
        {
            var returns = new[] { 0.01, -0.02, 0.015, 0.03, -0.01, 0.02 };
            var noise = new[] { 0.001, -0.002, 0.003, -0.001, 0.002, -0.003 };
            var algorithm = CreateAlgorithm(new FixedHistoryProvider(_ => BuildHistory(returns, noise)));
            var spy = algorithm.AddEquity("SPY", Resolution.Daily);
            var aapl = algorithm.AddEquity("AAPL", Resolution.Daily);
            var ibm = algorithm.AddEquity("IBM", Resolution.Daily);
            var model = new PearsonCorrelationPairsTradingAlphaModel(returns.Length, Resolution.Daily, 1m, minimumCorrelation: 1.5);

            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy, aapl, ibm }, Array.Empty<Security>()));

            Assert.IsFalse(model.HasPassedTest(algorithm, spy.Symbol, aapl.Symbol));
            Assert.IsFalse(model.HasPassedTest(algorithm, aapl.Symbol, spy.Symbol));
            Assert.IsEmpty(model.Update(algorithm, null));
        }

        [Test]
        public void PearsonCorrelationLogsWhenHistoryIsNotAligned()
        {
            // each slice only contains one of the two symbols
            var algorithm = CreateAlgorithm(new FixedHistoryProvider(_ => new[]
            {
                CreateSlice(Start.AddDays(-2), ("SPY", 100m)),
                CreateSlice(Start.AddDays(-1), ("AAPL", 50m))
            }));
            var spy = algorithm.AddEquity("SPY", Resolution.Daily);
            var aapl = algorithm.AddEquity("AAPL", Resolution.Daily);
            var model = new PearsonCorrelationPairsTradingAlphaModel(2, Resolution.Daily);

            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy, aapl }, Array.Empty<Security>()));

            StringAssert.Contains("Current lookback: 2", algorithm.DebugMessages.Single());
            Assert.IsFalse(model.HasPassedTest(algorithm, spy.Symbol, aapl.Symbol));
            Assert.IsEmpty(model.Update(algorithm, null));
        }

        [Test]
        public void BasePairsTradingCreatesPairsAndDisposesThemOnRemoval()
        {
            var algorithm = CreateAlgorithm(new FixedHistoryProvider(_ => Enumerable.Empty<Slice>()));
            var spy = algorithm.AddEquity("SPY", Resolution.Daily);
            var aapl = algorithm.AddEquity("AAPL", Resolution.Daily);
            var model = new BasePairsTradingAlphaModel(2, Resolution.Daily, 1.5m);
            Assert.AreEqual("BasePairsTradingAlphaModel(2,Daily,1.5)", model.Name);
            Assert.IsTrue(model.HasPassedTest(algorithm, spy.Symbol, aapl.Symbol));

            var spyConsolidators = ConsolidatorCount(algorithm, spy.Symbol);
            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy, aapl }, Array.Empty<Security>()));
            // the pair already exists: nothing new is registered
            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { aapl }, Array.Empty<Security>()));
            Assert.AreEqual(spyConsolidators + 1, ConsolidatorCount(algorithm, spy.Symbol));

            var insights = AssertSpreadTrading(algorithm, model, spy.Symbol, aapl.Symbol, threshold: 1.5m);
            Assert.IsTrue(insights.All(x => x.Period == TimeSpan.FromDays(2)));

            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(Array.Empty<Security>(), new[] { spy }));
            Assert.AreEqual(spyConsolidators, ConsolidatorCount(algorithm, spy.Symbol));
            CollectionAssert.AreEquivalent(new[] { aapl }, model.Securities);
            Assert.IsEmpty(model.Update(algorithm, null));
        }

        [Test]
        public void BasePairsTradingSkipsPairsThatFailTheTest()
        {
            var algorithm = CreateAlgorithm(new FixedHistoryProvider(_ => Enumerable.Empty<Slice>()));
            var spy = algorithm.AddEquity("SPY", Resolution.Daily);
            var aapl = algorithm.AddEquity("AAPL", Resolution.Daily);
            var model = new RejectingPairsTradingAlphaModel();

            var spyConsolidators = ConsolidatorCount(algorithm, spy.Symbol);
            model.OnSecuritiesChanged(algorithm, SecurityChangesTests.CreateNonInternal(new[] { spy, aapl }, Array.Empty<Security>()));

            Assert.AreEqual(1, model.TestCalls);
            Assert.AreEqual(spyConsolidators, ConsolidatorCount(algorithm, spy.Symbol));
            Assert.IsEmpty(model.Update(algorithm, null));
        }

        /// <summary>
        /// Warms up the 500 period ratio mean, then moves the first symbol up and down to cross the thresholds
        /// </summary>
        private static List<Insight> AssertSpreadTrading(QCAlgorithm algorithm, IAlphaModel model, Symbol moving, Symbol constant, decimal threshold = 1m)
        {
            var day = Start;
            for (var i = 0; i < 500; i++)
            {
                PushBar(algorithm, moving, day, 100m);
                PushBar(algorithm, constant, day, 50m);
                day = day.AddDays(1);
                if (i < 498)
                {
                    Assert.IsEmpty(model.Update(algorithm, null));
                }
            }
            // ratio sits at the mean: no signal
            Assert.IsEmpty(model.Update(algorithm, null));

            var spike = 100m * (1 + 2 * threshold / 100m);
            PushBar(algorithm, moving, day, spike);
            PushBar(algorithm, constant, day, 50m);
            day = day.AddDays(1);
            var first = model.Update(algorithm, null).ToList();
            AssertGroup(first, moving, InsightDirection.Down, constant, InsightDirection.Up);

            // still above the threshold: no repeated signal
            Assert.IsEmpty(model.Update(algorithm, null));

            var drop = 100m * (1 - 2 * threshold / 100m);
            PushBar(algorithm, moving, day, drop);
            PushBar(algorithm, constant, day, 50m);
            var second = model.Update(algorithm, null).ToList();
            AssertGroup(second, moving, InsightDirection.Up, constant, InsightDirection.Down);

            return first.Concat(second).ToList();
        }

        private static void AssertGroup(List<Insight> insights, Symbol first, InsightDirection firstDirection, Symbol second, InsightDirection secondDirection)
        {
            Assert.AreEqual(2, insights.Count);
            Assert.AreEqual(firstDirection, insights.Single(x => x.Symbol == first).Direction);
            Assert.AreEqual(secondDirection, insights.Single(x => x.Symbol == second).Direction);
            Assert.IsNotNull(insights[0].GroupId);
            Assert.AreEqual(insights[0].GroupId, insights[1].GroupId);
        }

        #endregion

        private static IEnumerable<Slice> BuildHistory(double[] returns, double[] noise)
        {
            var spy = 100.0;
            var aapl = 50.0;
            var ibm = 80.0;
            var slices = new List<Slice> { CreateSlice(Start.AddDays(-returns.Length - 1), ("SPY", (decimal)spy), ("AAPL", (decimal)aapl), ("IBM", (decimal)ibm)) };
            for (var i = 0; i < returns.Length; i++)
            {
                spy *= Math.Exp(returns[i]);
                aapl *= Math.Exp(returns[i] + noise[i]);
                ibm *= Math.Exp(-returns[i] + 3 * noise[(i + 2) % noise.Length]);
                slices.Add(CreateSlice(Start.AddDays(i - returns.Length), ("SPY", (decimal)spy), ("AAPL", (decimal)aapl), ("IBM", (decimal)ibm)));
            }
            return slices;
        }

        private static AlgorithmStub CreateAlgorithm(IHistoryProvider historyProvider)
        {
            var algorithm = new AlgorithmStub();
            algorithm.HistoryProvider = historyProvider;
            algorithm.SetStartDate(Start);
            algorithm.SetDateTime(Start.ConvertToUtc(TimeZones.NewYork));
            return algorithm;
        }

        private static Slice CreateSlice(DateTime time, params (string Ticker, decimal Close)[] bars)
        {
            var data = bars.Select(x => (BaseData)new TradeBar(time, Symbol.Create(x.Ticker, SecurityType.Equity, Market.USA), x.Close, x.Close, x.Close, x.Close, 1000, Time.OneDay)).ToList();
            return new Slice(time.AddDays(1), data, time.AddDays(1).ConvertToUtc(TimeZones.NewYork));
        }

        private static void PushBar(QCAlgorithm algorithm, Symbol symbol, DateTime time, decimal close)
        {
            var bar = new TradeBar(time, symbol, close, close, close, close, 1000, Time.OneDay);
            foreach (var config in algorithm.SubscriptionManager.SubscriptionDataConfigService.GetSubscriptionDataConfigs(symbol))
            {
                foreach (var consolidator in config.Consolidators.ToList())
                {
                    consolidator.Update(bar);
                }
            }
        }

        private static int ConsolidatorCount(QCAlgorithm algorithm, Symbol symbol)
        {
            return algorithm.SubscriptionManager.SubscriptionDataConfigService.GetSubscriptionDataConfigs(symbol).Sum(x => x.Consolidators.Count);
        }

        private sealed class RejectingPairsTradingAlphaModel : BasePairsTradingAlphaModel
        {
            public int TestCalls { get; private set; }

            public override bool HasPassedTest(QCAlgorithm algorithm, Symbol asset1, Symbol asset2)
            {
                TestCalls++;
                return false;
            }
        }

        private sealed class FixedHistoryProvider : HistoryProviderBase
        {
            private readonly Func<IEnumerable<HistoryRequest>, IEnumerable<Slice>> _history;

            public override int DataPointCount => 0;

            public FixedHistoryProvider(Func<IEnumerable<HistoryRequest>, IEnumerable<Slice>> history)
            {
                _history = history;
            }

            public override void Initialize(HistoryProviderInitializeParameters parameters)
            {
            }

            public override IEnumerable<Slice> GetHistory(IEnumerable<HistoryRequest> requests, DateTimeZone sliceTimeZone)
            {
                return _history(requests.ToList()).ToList();
            }
        }
    }
}
