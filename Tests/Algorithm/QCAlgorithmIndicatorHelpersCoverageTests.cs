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
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture, Parallelizable(ParallelScope.Fixtures)]
    public class QCAlgorithmIndicatorHelpersCoverageTests
    {
        private QCAlgorithm _algorithm;
        private Symbol _spy;
        private Symbol _aapl;

        [SetUp]
        public void Setup()
        {
            _algorithm = new AlgorithmStub();
            _algorithm.SetDateTime(new DateTime(2013, 10, 07, 10, 0, 0));
            _spy = _algorithm.AddEquity("SPY").Symbol;
            _aapl = _algorithm.AddEquity("AAPL").Symbol;
        }

        private static IEnumerable<TestCaseData> SingleSymbolIndicators()
        {
            var cases = new (string Name, Func<QCAlgorithm, Symbol, Symbol, IndicatorBase> Factory)[]
            {
                ("A", (a, s, r) => a.A(s, r, 1, 10)),
                ("AR", (a, s, r) => a.AR(s, 5)),
                ("ADX", (a, s, r) => a.ADX(s, 5)),
                ("AO", (a, s, r) => a.AO(s, 3, 6, MovingAverageType.Simple)),
                ("ADXR", (a, s, r) => a.ADXR(s, 5)),
                ("ALMA", (a, s, r) => a.ALMA(s, 5)),
                ("APO", (a, s, r) => a.APO(s, 3, 6, MovingAverageType.Simple)),
                ("AROON", (a, s, r) => a.AROON(s, 5)),
                ("ATR", (a, s, r) => a.ATR(s, 5)),
                ("APS", (a, s, r) => a.APS(s, 3)),
                ("BB", (a, s, r) => a.BB(s, 5, 2m)),
                ("B", (a, s, r) => a.B(s, r, 5)),
                ("BOP", (a, s, r) => a.BOP(s)),
                ("CC", (a, s, r) => a.CC(s)),
                ("C", (a, s, r) => a.C(s, r, 5)),
                ("COV", (a, s, r) => a.COV(s, r, 5)),
                ("CCI", (a, s, r) => a.CCI(s, 5)),
                ("CHOP", (a, s, r) => a.CHOP(s, 5)),
                ("CKS", (a, s, r) => a.CKS(s, 5, 2m, 3)),
                ("CMF", (a, s, r) => a.CMF(s, 5)),
                ("CO", (a, s, r) => a.CO(s, 3, 6)),
                ("CMO", (a, s, r) => a.CMO(s, 5)),
                ("CRSI", (a, s, r) => a.CRSI(s, 3, 2, 5)),
                ("DEM", (a, s, r) => a.DEM(s, 5, MovingAverageType.Simple)),
                ("DCH", (a, s, r) => a.DCH(s, 3, 4)),
                ("DCH", (a, s, r) => a.DCH(s, 5)),
                ("DEMA", (a, s, r) => a.DEMA(s, 5)),
                ("DO", (a, s, r) => a.DO(s, 5, 3, 3, 3)),
                ("DPO", (a, s, r) => a.DPO(s, 5)),
                ("EMV", (a, s, r) => a.EMV(s)),
                ("FI", (a, s, r) => a.FI(s, 5)),
                ("FISH", (a, s, r) => a.FISH(s, 5)),
                ("FRAMA", (a, s, r) => a.FRAMA(s, 6, 20)),
                ("HA", (a, s, r) => a.HeikinAshi(s)),
                ("HE", (a, s, r) => a.HE(s, 25, 5)),
                ("HT", (a, s, r) => a.HT(s, 7, 0.635m, 0.338m)),
                ("HMA", (a, s, r) => a.HMA(s, 5)),
                ("IBS", (a, s, r) => a.IBS(s)),
                ("ICHIMOKU", (a, s, r) => a.ICHIMOKU(s, 9, 26, 17, 52, 26, 26)),
                ("KAMA", (a, s, r) => a.KAMA(s, 5)),
                ("KAMA", (a, s, r) => a.KAMA(s, 5, 2, 30)),
                ("KER", (a, s, r) => a.KER(s, 5)),
                ("KCH", (a, s, r) => a.KCH(s, 5, 2m)),
                ("KST", (a, s, r) => a.KST(s)),
                ("KVO", (a, s, r) => a.KVO(s, 3, 6)),
                ("LOGR", (a, s, r) => a.LOGR(s, 5)),
                ("LSMA", (a, s, r) => a.LSMA(s, 5)),
                ("LWMA", (a, s, r) => a.LWMA(s, 5)),
                ("MACD", (a, s, r) => a.MACD(s, 3, 6, 2)),
                ("MAD", (a, s, r) => a.MAD(s, 5)),
                ("MAMA", (a, s, r) => a.MAMA(s)),
                ("VP", (a, s, r) => a.VP(s, 2, 0.7m, 0.05m, Resolution.Minute)),
                ("TP", (a, s, r) => a.TP(s, 2, 0.7m, 0.05m, Resolution.Minute)),
                ("TSF", (a, s, r) => a.TSF(s, 5)),
                ("MAX", (a, s, r) => a.MAX(s, 5)),
                ("MASS", (a, s, r) => a.MASS(s)),
                ("MIDPOINT", (a, s, r) => a.MIDPOINT(s, 5)),
                ("MIDPRICE", (a, s, r) => a.MIDPRICE(s, 5)),
                ("MIN", (a, s, r) => a.MIN(s, 5)),
                ("MOM", (a, s, r) => a.MOM(s, 5)),
                ("MOMERSION", (a, s, r) => a.MOMERSION(s, 4, 6)),
                ("MOMP", (a, s, r) => a.MOMP(s, 5)),
                ("NATR", (a, s, r) => a.NATR(s, 5)),
                ("OBV", (a, s, r) => a.OBV(s)),
                ("PPO", (a, s, r) => a.PPO(s, 3, 6, MovingAverageType.Simple)),
                ("PSAR", (a, s, r) => a.PSAR(s)),
                ("RC", (a, s, r) => a.RC(s, 5, 2m)),
                ("RMA", (a, s, r) => a.RMA(s, 5)),
                ("ROC", (a, s, r) => a.ROC(s, 5)),
                ("ROCP", (a, s, r) => a.ROCP(s, 5)),
                ("ROCR", (a, s, r) => a.ROCR(s, 5)),
                ("RVI", (a, s, r) => a.RVI(s, 5)),
                ("RDV", (a, s, r) => a.RDV(s, 2, Resolution.Minute)),
                ("SRSI", (a, s, r) => a.SRSI(s, 5, 5, 3, 3)),
                ("STR", (a, s, r) => a.STR(s, 5, 2m)),
                ("SORTINO", (a, s, r) => a.SORTINO(s, 5)),
                ("SAREXT", (a, s, r) => a.SAREXT(s)),
                ("SM", (a, s, r) => a.SM(s)),
                ("STC", (a, s, r) => a.STC(s, 5, 3, 6)),
                ("SOBV", (a, s, r) => a.SOBV(s, 5)),
                ("TDD", (a, s, r) => a.TDD(s, 5)),
                ("TDS", (a, s, r) => a.TDS(s)),
                ("STO", (a, s, r) => a.STO(s, 5)),
                ("PSO", (a, s, r) => a.PSO(s, 5, 3)),
                ("SUM", (a, s, r) => a.SUM(s, 5)),
                ("SWISS", (a, s, r) => a.SWISS(s, 5, 0.1, SwissArmyKnifeTool.Gauss)),
                ("T3", (a, s, r) => a.T3(s, 5)),
                ("TEMA", (a, s, r) => a.TEMA(s, 5)),
                ("TSI", (a, s, r) => a.TSI(s)),
                ("TR", (a, s, r) => a.TR(s)),
                ("TRIMA", (a, s, r) => a.TRIMA(s, 5)),
                ("TRIX", (a, s, r) => a.TRIX(s, 5)),
                ("ULTOSC", (a, s, r) => a.ULTOSC(s, 3, 5, 7)),
                ("VIDYA", (a, s, r) => a.VIDYA(s, 5)),
                ("V", (a, s, r) => a.V(s, 5)),
                ("VAR", (a, s, r) => a.VAR(s, 5, 0.95)),
                ("VWAP", (a, s, r) => a.VWAP(s, 5)),
                ("VWAP", (a, s, r) => a.VWAP(s)),
                ("VWMA", (a, s, r) => a.VWMA(s, 5)),
                ("VTX", (a, s, r) => a.VTX(s, 5)),
                ("WILR", (a, s, r) => a.WILR(s, 5)),
                ("WWMA", (a, s, r) => a.WWMA(s, 5)),
                ("SI", (a, s, r) => a.SI(s, 1m, Resolution.Minute)),
                ("ASI", (a, s, r) => a.ASI(s, 1m, Resolution.Minute)),
                ("WTO", (a, s, r) => a.WTO(s, 5, 3, 2)),
                ("MGD", (a, s, r) => a.MGD(s, 5)),
                ("RSV", (a, s, r) => a.RSV(s, 5)),
                ("ZLEMA", (a, s, r) => a.ZLEMA(s, 5)),
                ("ZZ", (a, s, r) => a.ZZ(s)),
                ("Identity", (a, s, r) => a.Identity(s)),
                ("Identity", (a, s, r) => a.Identity(s, TimeSpan.FromMinutes(1))),
                ("FilteredIdentity", (a, s, r) => a.FilteredIdentity(s, (Func<IBaseData, IBaseDataBar>)null)),
                ("FilteredIdentity", (a, s, r) => a.FilteredIdentity(s, Resolution.Minute, (Func<IBaseData, IBaseDataBar>)null, x => true)),
                ("FilteredIdentity", (a, s, r) => a.FilteredIdentity(s, TimeSpan.FromMinutes(1), (Func<IBaseData, IBaseDataBar>)null, x => true)),
            };

            var index = 0;
            foreach (var (name, factory) in cases)
            {
                yield return new TestCaseData(factory).SetName($"IndicatorHelperRegistersAndUpdates_{index++:000}_{name}");
            }
        }

        [TestCaseSource(nameof(SingleSymbolIndicators))]
        public void IndicatorHelperRegistersAndUpdates(Func<QCAlgorithm, Symbol, Symbol, IndicatorBase> factory)
        {
            var indicator = factory(_algorithm, _spy, _aapl);

            Assert.IsNotNull(indicator);
            Assert.IsFalse(string.IsNullOrEmpty(indicator.Name));
            Assert.IsNotEmpty(indicator.Consolidators);

            var registered = GetRegisteredConsolidators(_spy).Concat(GetRegisteredConsolidators(_aapl)).ToHashSet();
            Assert.That(indicator.Consolidators, Is.SubsetOf(registered));

            FeedBar(indicator, _spy, 100m);
            FeedBar(indicator, _aapl, 50m);
            Assert.Greater(indicator.Samples, 0);

            _algorithm.DeregisterIndicator(indicator);
            Assert.IsEmpty(indicator.Consolidators);
            var remaining = GetRegisteredConsolidators(_spy).Concat(GetRegisteredConsolidators(_aapl)).ToList();
            Assert.IsEmpty(remaining);
        }

        private static IEnumerable<TestCaseData> MultiSymbolIndicators()
        {
            var cases = new (string Name, string Prefix, Func<QCAlgorithm, Symbol[], IndicatorBase> Factory)[]
            {
                ("NHNL", "NH/NL(5", (a, s) => a.NHNL(s.ToList(), 5)),
                ("NHNLV", "NH/NL Volume(5", (a, s) => a.NHNLV(s.ToList(), 5)),
                ("TRIN_Enumerable", "TRIN", (a, s) => a.TRIN(s.ToList())),
                ("TRIN_Array", "TRIN", (a, s) => a.TRIN(s)),
                ("ADR", "A/D Ratio", (a, s) => a.ADR(s.ToList())),
                ("ADVR", "A/D Volume Rate", (a, s) => a.ADVR(s.ToList())),
                ("ADDIFF", "A/D Difference", (a, s) => a.ADDIFF(s.ToList())),
                ("MOSC_Enumerable", "MO(3,6", (a, s) => a.MOSC(s.ToList(), 3, 6)),
                ("MOSC_Array", "MO(3,6", (a, s) => a.MOSC(s, 3, 6)),
                ("MSI_Enumerable", "MSI(3,6", (a, s) => a.MSI(s.ToList(), 3, 6)),
                ("MSI_Array", "MSI(3,6", (a, s) => a.MSI(s, 3, 6)),
            };
            foreach (var (name, prefix, factory) in cases)
            {
                yield return new TestCaseData(prefix, factory).SetName($"MultiSymbolIndicatorHelperRegistersAllSymbols_{name}");
            }
        }

        [TestCaseSource(nameof(MultiSymbolIndicators))]
        public void MultiSymbolIndicatorHelperRegistersAllSymbols(string expectedPrefix, Func<QCAlgorithm, Symbol[], IndicatorBase> factory)
        {
            var indicator = factory(_algorithm, new[] { _spy, _aapl });

            StringAssert.StartsWith(expectedPrefix, indicator.Name);
            StringAssert.Contains("min", indicator.Name);
            Assert.AreEqual(2, indicator.Consolidators.Count);
            CollectionAssert.IsNotEmpty(GetRegisteredConsolidators(_spy).Intersect(indicator.Consolidators));
            CollectionAssert.IsNotEmpty(GetRegisteredConsolidators(_aapl).Intersect(indicator.Consolidators));

            _algorithm.UnregisterIndicator(indicator);
            Assert.IsEmpty(indicator.Consolidators);
        }

        [TestCase(Resolution.Tick, "tick")]
        [TestCase(Resolution.Second, "sec")]
        [TestCase(Resolution.Minute, "min")]
        [TestCase(Resolution.Hour, "hr")]
        [TestCase(Resolution.Daily, "day")]
        public void CreateIndicatorNameUsesResolutionAbbreviation(Resolution resolution, string expected)
        {
            Assert.AreEqual($"SMA(5,SPY_{expected})", _algorithm.CreateIndicatorName(_spy, "SMA(5)", resolution));
            Assert.AreEqual($"EMA(3,SPY_{expected})", _algorithm.CreateIndicatorName(_spy, $"EMA({3})", resolution));
        }

        [Test]
        public void CreateIndicatorNameWithoutResolutionUsesSubscriptionResolution()
        {
            Assert.AreEqual("SMA(5,SPY_min)", _algorithm.CreateIndicatorName(_spy, "SMA(5)", null));
            Assert.AreEqual("TRIN(day)", _algorithm.CreateIndicatorName(Symbol.None, "TRIN", Resolution.Daily));
            Assert.AreEqual("TRIN()", _algorithm.CreateIndicatorName(Symbol.None, "TRIN", null));
        }

        [Test]
        public void CreateIndicatorNameThrowsOnInvalidResolution()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _algorithm.CreateIndicatorName(_spy, "SMA(5)", (Resolution)100));
        }

        [Test]
        public void IndicatorHelperAutoAddsSecurityThatWasNotSubscribed()
        {
            var ibm = Symbol.Create("IBM", SecurityType.Equity, Market.USA);
            Assert.IsFalse(_algorithm.Securities.ContainsKey(ibm));

            var sma = _algorithm.SMA(ibm, 5);

            Assert.IsTrue(_algorithm.Securities.ContainsKey(ibm));
            StringAssert.Contains("IBM", sma.Name);
        }

        [Test]
        public void IndicatorHelperThrowsWhenRequestedResolutionIsHigherThanSubscription()
        {
            var daily = _algorithm.AddEquity("GOOG", Resolution.Daily).Symbol;
            Assert.Throws<ArgumentException>(() => _algorithm.SMA(daily, 5, Resolution.Minute));
            Assert.Throws<ArgumentException>(() => _algorithm.ResolveConsolidator(daily, TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void RegisterIndicatorWithTimeSpanCreatesTimeConsolidator()
        {
            var sma = new SimpleMovingAverage(3);
            _algorithm.RegisterIndicator(_spy, sma, TimeSpan.FromMinutes(5));

            var consolidator = sma.Consolidators.Single();
            Assert.IsInstanceOf<TradeBarConsolidator>(consolidator);

            var start = new DateTime(2013, 10, 07, 10, 0, 0);
            for (var i = 0; i < 11; i++)
            {
                consolidator.Update(new TradeBar(start.AddMinutes(i), _spy, 10 + i, 10 + i, 10 + i, 10 + i, 100, TimeSpan.FromMinutes(1)));
            }
            Assert.AreEqual(2, sma.Samples);
        }

        [Test]
        public void RegisterIndicatorWithTimeSpanAndSelector()
        {
            var sma = new SimpleMovingAverage(3);
            _algorithm.RegisterIndicator(_spy, sma, (TimeSpan?)TimeSpan.FromMinutes(1), x => ((TradeBar)x).Volume);
            var consolidator = sma.Consolidators.Single();
            consolidator.Update(new TradeBar(new DateTime(2013, 10, 07, 10, 0, 0), _spy, 1, 2, 0.5m, 1.5m, 42, TimeSpan.FromMinutes(1)));
            Assert.AreEqual(42m, sma.Current.Value);
        }

        [Test]
        public void GenericRegisterIndicatorWithTimeSpanAndResolutionSelector()
        {
            var atr = new AverageTrueRange(3);
            _algorithm.RegisterIndicator<IBaseDataBar>(_spy, atr, TimeSpan.FromMinutes(1), x => (IBaseDataBar)x);
            Assert.AreEqual(1, atr.Consolidators.Count);

            var atr2 = new AverageTrueRange(3);
            _algorithm.RegisterIndicator<IBaseDataBar>(_spy, atr2, Resolution.Minute, x => (IBaseDataBar)x);
            Assert.AreEqual(1, atr2.Consolidators.Count);
        }

        [Test]
        public void GenericRegisterIndicatorThrowsOnConsolidatorTypeMismatch()
        {
            var atr = new AverageTrueRange(3);
            var consolidator = new QuoteBarConsolidator(TimeSpan.FromMinutes(1));
            Assert.Throws<ArgumentException>(() => _algorithm.RegisterIndicator<TradeBar>(_spy, new FakeTradeBarIndicator(), consolidator));
            Assert.AreEqual(0, atr.Consolidators.Count);
        }

        [Test]
        public void GenericRegisterIndicatorWithIndicatorDataPointUsesValueSelectorWhenConsolidatorOutputsBars()
        {
            var sma = new SimpleMovingAverage(2);
            var consolidator = new TradeBarConsolidator(1);
            _algorithm.RegisterIndicator<IndicatorDataPoint>(_spy, sma, consolidator);
            consolidator.Update(new TradeBar(new DateTime(2013, 10, 07, 10, 0, 0), _spy, 1, 2, 0.5m, 7m, 42, TimeSpan.FromMinutes(1)));
            Assert.AreEqual(7m, sma.Current.Value);
        }

        [Test]
        public void ConsolidateOverloadsReturnRegisteredConsolidators()
        {
            var tradeBars = new List<TradeBar>();
            var quoteBars = new List<QuoteBar>();

            var c1 = _algorithm.Consolidate(_spy, Resolution.Hour, (TradeBar bar) => tradeBars.Add(bar));
            var c2 = _algorithm.Consolidate(_spy, TimeSpan.FromMinutes(5), (TradeBar bar) => tradeBars.Add(bar));
            var c3 = _algorithm.Consolidate(_spy, Calendar.Weekly, (TradeBar bar) => tradeBars.Add(bar));
            var c4 = _algorithm.Consolidate<BaseData>(_spy, TimeSpan.FromMinutes(10), bar => { });
            var c5 = _algorithm.Consolidate<TradeBar>(_spy, Calendar.Monthly, bar => { });
            var c6 = _algorithm.Consolidate<BaseData>(_spy, Calendar.Monthly, bar => { });

            Assert.IsInstanceOf<TradeBarConsolidator>(c1);
            Assert.IsInstanceOf<TradeBarConsolidator>(c2);
            Assert.IsInstanceOf<TradeBarConsolidator>(c3);
            Assert.IsInstanceOf<TradeBarConsolidator>(c4);
            Assert.IsInstanceOf<TradeBarConsolidator>(c5);
            Assert.IsInstanceOf<TradeBarConsolidator>(c6);

            var registered = GetRegisteredConsolidators(_spy).ToList();
            Assert.That(new[] { c1, c2, c3, c4, c5, c6 }, Is.SubsetOf(registered));

            var start = new DateTime(2013, 10, 07, 10, 0, 0);
            for (var i = 0; i < 6; i++)
            {
                c2.Update(new TradeBar(start.AddMinutes(i), _spy, 1, 1, 1, 1, 1, TimeSpan.FromMinutes(1)));
            }
            Assert.AreEqual(1, tradeBars.Count);
        }

        [Test]
        public void ConsolidateQuoteBarOverloadsRequireQuoteSubscription()
        {
            var forex = _algorithm.AddForex("EURUSD").Symbol;
            var quoteBars = new List<QuoteBar>();

            var c1 = _algorithm.Consolidate(forex, Resolution.Hour, (QuoteBar bar) => quoteBars.Add(bar));
            var c2 = _algorithm.Consolidate(forex, TimeSpan.FromMinutes(5), (QuoteBar bar) => quoteBars.Add(bar));
            var c3 = _algorithm.Consolidate(forex, Calendar.Weekly, (QuoteBar bar) => quoteBars.Add(bar));

            Assert.IsInstanceOf<QuoteBarConsolidator>(c1);
            Assert.IsInstanceOf<QuoteBarConsolidator>(c2);
            Assert.IsInstanceOf<QuoteBarConsolidator>(c3);
        }

        [Test]
        public void ConsolidateWithMismatchedHandlerTypeThrows()
        {
            var forex = _algorithm.AddForex("EURUSD").Symbol;
            Assert.Throws<ArgumentException>(() => _algorithm.Consolidate(forex, TimeSpan.FromMinutes(5), (TradeBar bar) => { }));
        }

        [TestCase(typeof(RenkoBar), typeof(RenkoConsolidator))]
        [TestCase(typeof(VolumeRenkoBar), typeof(VolumeRenkoConsolidator))]
        [TestCase(typeof(RangeBar), typeof(RangeConsolidator))]
        [TestCase(typeof(TradeBar), typeof(TradeBarConsolidator))]
        [TestCase(typeof(QuoteBar), typeof(QuoteBarConsolidator))]
        [TestCase(typeof(BaseData), typeof(BaseDataConsolidator))]
        public void ConsolidateWithBarSizeCreatesExpectedConsolidator(Type dataType, Type expectedConsolidatorType)
        {
            var method = typeof(QCAlgorithm).GetMethods()
                .Single(m => m.Name == nameof(QCAlgorithm.Consolidate) && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 4 && m.GetParameters()[1].ParameterType == typeof(decimal));
            var generic = method.MakeGenericMethod(dataType);
            var actionType = typeof(Action<>).MakeGenericType(dataType);
            var handler = Delegate.CreateDelegate(actionType, typeof(QCAlgorithmIndicatorHelpersCoverageTests)
                .GetMethod(nameof(NoOp), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .MakeGenericMethod(dataType));

            var consolidator = (IDataConsolidator)generic.Invoke(_algorithm, new object[] { _spy, 10m, null, handler });

            Assert.IsInstanceOf(expectedConsolidatorType, consolidator);
            CollectionAssert.Contains(GetRegisteredConsolidators(_spy).ToList(), consolidator);
        }

        [TestCase(TickType.Trade, typeof(TickConsolidator))]
        [TestCase(TickType.Quote, typeof(TickQuoteBarConsolidator))]
        [TestCase(TickType.OpenInterest, typeof(OpenInterestConsolidator))]
        public void ConsolidateWithBarSizeOnTickSubscription(TickType tickType, Type expectedConsolidatorType)
        {
            var tickSymbol = _algorithm.AddEquity("IBM", Resolution.Tick).Symbol;
            var consolidator = _algorithm.Consolidate<Tick>(tickSymbol, 10m, tickType, null);

            Assert.IsInstanceOf(expectedConsolidatorType, consolidator);
            // no handler means it is not registered
            CollectionAssert.DoesNotContain(GetRegisteredConsolidators(tickSymbol).ToList(), consolidator);
        }

        [TestCase(typeof(TradeBar), null, typeof(TradeBarConsolidator))]
        [TestCase(typeof(QuoteBar), null, typeof(QuoteBarConsolidator))]
        [TestCase(typeof(Tick), TickType.Trade, typeof(TickConsolidator))]
        [TestCase(typeof(Tick), TickType.Quote, typeof(TickQuoteBarConsolidator))]
        [TestCase(typeof(Tick), TickType.OpenInterest, typeof(OpenInterestConsolidator))]
        [TestCase(typeof(DynamicData), null, typeof(DynamicDataConsolidator))]
        [TestCase(typeof(IndicatorDataPoint), null, typeof(BaseDataConsolidator))]
        public void StaticCreateConsolidatorSelectsConsolidatorByInputType(Type inputType, TickType? tickType, Type expected)
        {
            var consolidator = QCAlgorithm.CreateConsolidator(TimeSpan.FromMinutes(1), inputType, tickType);
            Assert.IsInstanceOf(expected, consolidator);
        }

        [TestCase(typeof(TradeBar), typeof(IdentityDataConsolidator<TradeBar>))]
        [TestCase(typeof(QuoteBar), typeof(IdentityDataConsolidator<QuoteBar>))]
        [TestCase(typeof(Tick), typeof(IdentityDataConsolidator<Tick>))]
        [TestCase(typeof(DynamicData), typeof(DynamicDataConsolidator))]
        [TestCase(typeof(IndicatorDataPoint), typeof(IdentityDataConsolidator<BaseData>))]
        public void StaticCreateConsolidatorWithZeroPeriodCreatesIdentity(Type inputType, Type expected)
        {
            var consolidator = QCAlgorithm.CreateConsolidator(TimeSpan.Zero, inputType);
            Assert.IsInstanceOf(expected, consolidator);
        }

        [Test]
        public void MultiSymbolIndicatorNamesUseNoSymbol()
        {
            Assert.AreEqual("B(5,min)", _algorithm.B(_spy, _aapl, 5, Resolution.Minute).Name);
            Assert.AreEqual("C(5,min)", _algorithm.C(_spy, _aapl, 5, resolution: Resolution.Minute).Name);
            Assert.AreEqual("COV(5,min)", _algorithm.COV(_spy, _aapl, 5, Resolution.Minute).Name);
        }

        [Test]
        [Ignore("Possible bug: QCAlgorithm.HT builds an indicator name but never passes it to HilbertTransform, so the name is not unique per symbol")]
        public void HilbertTransformHelperUsesSymbolSpecificName()
        {
            var indicator = _algorithm.HT(_spy, 7, 0.635m, 0.338m);
            StringAssert.Contains("SPY", indicator.Name);
        }

        [Test]
        public void ResolveConsolidatorForTickSubscriptionWithCalendarUsesTickConsolidators()
        {
            var tickSymbol = _algorithm.AddEquity("IBM", Resolution.Tick).Symbol;
            var trade = _algorithm.Consolidate<TradeBar>(tickSymbol, Calendar.Weekly, TickType.Trade, bar => { });
            Assert.IsInstanceOf<TickConsolidator>(trade);

            var identity = _algorithm.ResolveConsolidator(tickSymbol, (Resolution?)null);
            Assert.IsInstanceOf<IdentityDataConsolidator<Tick>>(identity);
        }

        [Test]
        public void ResolveConsolidatorDailyWarnsOnceWhenExtendedMarketHoursAreExcluded()
        {
            var extended = _algorithm.AddEquity("IBM", Resolution.Minute, extendedMarketHours: true).Symbol;
            _algorithm.Settings.DailyConsolidationUseExtendedMarketHours = false;

            var c1 = _algorithm.ResolveConsolidator(extended, Resolution.Daily);
            var c2 = _algorithm.ResolveConsolidator(extended, Resolution.Daily);

            Assert.IsInstanceOf<MarketHourAwareConsolidator>(c1);
            Assert.IsInstanceOf<MarketHourAwareConsolidator>(c2);
            Assert.AreEqual(1, _algorithm.DebugMessages.Count(x => x.Contains("DailyConsolidationUseExtendedMarketHours")));
        }

        [Test]
        public void WarmUpIndicatorWithNonWarmUpProviderLogsWarning()
        {
            var indicator = new FunctionalIndicator<IndicatorDataPoint>("custom", x => x.Value, _ => true);
            _algorithm.WarmUpIndicator(_spy, indicator, TimeSpan.FromMinutes(1));
            Assert.IsTrue(_algorithm.DebugMessages.Any(x => x.Contains("WarmUpIndicator")));
            Assert.IsFalse(indicator.IsReady && indicator.Samples > 0);
        }

        private IEnumerable<IDataConsolidator> GetRegisteredConsolidators(Symbol symbol)
        {
            return _algorithm.SubscriptionManager.SubscriptionDataConfigService
                .GetSubscriptionDataConfigs(symbol)
                .SelectMany(x => x.Consolidators);
        }

        private void FeedBar(IndicatorBase indicator, Symbol symbol, decimal price)
        {
            var registered = GetRegisteredConsolidators(symbol).ToHashSet();
            var time = new DateTime(2013, 10, 07, 10, 0, 0);
            foreach (var consolidator in indicator.Consolidators.Where(registered.Contains))
            {
                consolidator.Update(new TradeBar(time, symbol, price, price + 1, price - 1, price, 1000, TimeSpan.FromMinutes(1)));
            }
        }

        private static void NoOp<T>(T data)
        {
        }

        private class FakeTradeBarIndicator : TradeBarIndicator
        {
            public FakeTradeBarIndicator() : base("fake")
            {
            }

            public override bool IsReady => false;

            protected override decimal ComputeNextValue(TradeBar input) => input.Close;
        }
    }
}
