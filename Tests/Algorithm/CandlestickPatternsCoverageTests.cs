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
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Indicators.CandlestickPatterns;
using QuantConnect.Tests.Engine.DataFeeds;
using AlgorithmCandlestickPatterns = QuantConnect.Algorithm.CandlestickPatterns;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class CandlestickPatternsCoverageTests
    {
        private AlgorithmStub _algorithm;
        private Symbol _spy;

        [SetUp]
        public void SetUp()
        {
            _algorithm = new AlgorithmStub();
            _algorithm.SetDateTime(new DateTime(2013, 10, 07, 10, 0, 0));
            _spy = _algorithm.AddEquity("SPY", Resolution.Minute).Symbol;
        }

        [TestCaseSource(nameof(PatternCases))]
        public void CreatesNamedPatternRegisteredAtSubscriptionResolution(PatternCase patternCase)
        {
            var before = GetConsolidators();

            var pattern = patternCase.Create(_algorithm.CandlestickPatterns, _spy, null, null);

            Assert.IsInstanceOf(patternCase.ExpectedType, pattern);
            Assert.AreEqual($"{patternCase.Prefix}(SPY_min)", pattern.Name);

            var added = GetConsolidators().Except(before).ToList();
            Assert.AreEqual(1, added.Count);
            Assert.AreEqual(typeof(TradeBar), added[0].OutputType);

            Assert.AreEqual(0, pattern.Samples);
            added[0].Update(CreateBar(_algorithm.Time));
            Assert.AreEqual(1, pattern.Samples);
            Assert.AreEqual(_algorithm.Time.AddMinutes(1), pattern.Current.EndTime);
        }

        [TestCaseSource(nameof(PatternCases))]
        public void CreatesPatternWithExplicitResolutionAndSelector(PatternCase patternCase)
        {
            var before = GetConsolidators();
            var selectorCalls = 0;
            Func<IBaseData, IBaseDataBar> selector = data =>
            {
                selectorCalls++;
                return (IBaseDataBar)data;
            };

            var pattern = patternCase.Create(_algorithm.CandlestickPatterns, _spy, Resolution.Daily, selector);

            Assert.IsInstanceOf(patternCase.ExpectedType, pattern);
            Assert.AreEqual($"{patternCase.Prefix}(SPY_day)", pattern.Name);

            var added = GetConsolidators().Except(before).ToList();
            Assert.AreEqual(1, added.Count);
            Assert.IsNotInstanceOf<IdentityDataConsolidator<TradeBar>>(added[0]);

            // a single minute bar does not complete the daily consolidation
            added[0].Update(CreateBar(new DateTime(2013, 10, 07, 10, 0, 0)));
            Assert.AreEqual(0, selectorCalls);
            Assert.AreEqual(0, pattern.Samples);

            // a bar on the next day closes the previous daily bar which is routed through the selector
            added[0].Update(CreateBar(new DateTime(2013, 10, 08, 10, 0, 0)));
            Assert.AreEqual(1, selectorCalls);
            Assert.AreEqual(1, pattern.Samples);
        }

        [Test]
        public void PatternsForDifferentSymbolsHaveDifferentNames()
        {
            var aapl = _algorithm.AddEquity("AAPL", Resolution.Hour).Symbol;

            var spyDoji = _algorithm.CandlestickPatterns.Doji(_spy);
            var aaplDoji = _algorithm.CandlestickPatterns.Doji(aapl);

            Assert.AreEqual("DOJI(SPY_min)", spyDoji.Name);
            Assert.AreEqual("DOJI(AAPL_hr)", aaplDoji.Name);
        }

        [Test]
        public void HelperCanBeConstructedForAnyAlgorithm()
        {
            var helper = new AlgorithmCandlestickPatterns(_algorithm);
            var pattern = helper.Engulfing(_spy, Resolution.Minute);

            Assert.IsInstanceOf<Engulfing>(pattern);
            Assert.AreEqual("ENGULFING(SPY_min)", pattern.Name);
            Assert.IsFalse(pattern.IsReady);
        }

        private List<IDataConsolidator> GetConsolidators()
        {
            return _algorithm.SubscriptionManager.Subscriptions
                .Where(config => config.Symbol == _spy)
                .SelectMany(config => config.Consolidators)
                .ToList();
        }

        private TradeBar CreateBar(DateTime time)
        {
            return new TradeBar(time, _spy, 100m, 101m, 99m, 100.5m, 1000, TimeSpan.FromMinutes(1));
        }

        private static PatternCase Case<T>(string prefix, Func<AlgorithmCandlestickPatterns, Symbol, Resolution?, Func<IBaseData, IBaseDataBar>, T> create)
            where T : IndicatorBase<IBaseDataBar>
        {
            return new PatternCase(prefix, typeof(T), (p, s, r, sel) => create(p, s, r, sel));
        }

        private static IEnumerable<TestCaseData> PatternCases()
        {
            return AllPatterns().Select(x => new TestCaseData(x).SetArgDisplayNames(x.Prefix));
        }

        private static IEnumerable<PatternCase> AllPatterns()
        {
            yield return Case<TwoCrows>("TWOCROWS", (p, s, r, sel) => p.TwoCrows(s, r, sel));
            yield return Case<ThreeBlackCrows>("THREEBLACKCROWS", (p, s, r, sel) => p.ThreeBlackCrows(s, r, sel));
            yield return Case<ThreeInside>("THREEINSIDE", (p, s, r, sel) => p.ThreeInside(s, r, sel));
            yield return Case<ThreeLineStrike>("THREELINESTRIKE", (p, s, r, sel) => p.ThreeLineStrike(s, r, sel));
            yield return Case<ThreeOutside>("THREEOUTSIDE", (p, s, r, sel) => p.ThreeOutside(s, r, sel));
            yield return Case<ThreeStarsInSouth>("THREESTARSINSOUTH", (p, s, r, sel) => p.ThreeStarsInSouth(s, r, sel));
            yield return Case<ThreeWhiteSoldiers>("THREEWHITESOLDIERS", (p, s, r, sel) => p.ThreeWhiteSoldiers(s, r, sel));
            yield return Case<AbandonedBaby>("ABANDONEDBABY", (p, s, r, sel) => p.AbandonedBaby(s, 0.3m, r, sel));
            yield return Case<AdvanceBlock>("ADVANCEBLOCK", (p, s, r, sel) => p.AdvanceBlock(s, r, sel));
            yield return Case<BeltHold>("BELTHOLD", (p, s, r, sel) => p.BeltHold(s, r, sel));
            yield return Case<Breakaway>("BREAKAWAY", (p, s, r, sel) => p.Breakaway(s, r, sel));
            yield return Case<ClosingMarubozu>("CLOSINGMARUBOZU", (p, s, r, sel) => p.ClosingMarubozu(s, r, sel));
            yield return Case<ConcealedBabySwallow>("CONCEALEDBABYSWALLOW", (p, s, r, sel) => p.ConcealedBabySwallow(s, r, sel));
            yield return Case<Counterattack>("COUNTERATTACK", (p, s, r, sel) => p.Counterattack(s, r, sel));
            yield return Case<DarkCloudCover>("DARKCLOUDCOVER", (p, s, r, sel) => p.DarkCloudCover(s, 0.5m, r, sel));
            yield return Case<Doji>("DOJI", (p, s, r, sel) => p.Doji(s, r, sel));
            yield return Case<DojiStar>("DOJISTAR", (p, s, r, sel) => p.DojiStar(s, r, sel));
            yield return Case<DragonflyDoji>("DRAGONFLYDOJI", (p, s, r, sel) => p.DragonflyDoji(s, r, sel));
            yield return Case<Engulfing>("ENGULFING", (p, s, r, sel) => p.Engulfing(s, r, sel));
            yield return Case<EveningDojiStar>("EVENINGDOJISTAR", (p, s, r, sel) => p.EveningDojiStar(s, 0.3m, r, sel));
            yield return Case<EveningStar>("EVENINGSTAR", (p, s, r, sel) => p.EveningStar(s, 0.3m, r, sel));
            yield return Case<GapSideBySideWhite>("GAPSIDEBYSIDEWHITE", (p, s, r, sel) => p.GapSideBySideWhite(s, r, sel));
            yield return Case<GravestoneDoji>("GRAVESTONEDOJI", (p, s, r, sel) => p.GravestoneDoji(s, r, sel));
            yield return Case<Hammer>("HAMMER", (p, s, r, sel) => p.Hammer(s, r, sel));
            yield return Case<HangingMan>("HANGINGMAN", (p, s, r, sel) => p.HangingMan(s, r, sel));
            yield return Case<Harami>("HARAMI", (p, s, r, sel) => p.Harami(s, r, sel));
            yield return Case<HaramiCross>("HARAMICROSS", (p, s, r, sel) => p.HaramiCross(s, r, sel));
            yield return Case<HighWaveCandle>("HIGHWAVECANDLE", (p, s, r, sel) => p.HighWaveCandle(s, r, sel));
            yield return Case<Hikkake>("HIKKAKE", (p, s, r, sel) => p.Hikkake(s, r, sel));
            yield return Case<HikkakeModified>("HIKKAKEMODIFIED", (p, s, r, sel) => p.HikkakeModified(s, r, sel));
            yield return Case<HomingPigeon>("HOMINGPIGEON", (p, s, r, sel) => p.HomingPigeon(s, r, sel));
            yield return Case<IdenticalThreeCrows>("IDENTICALTHREECROWS", (p, s, r, sel) => p.IdenticalThreeCrows(s, r, sel));
            yield return Case<InNeck>("INNECK", (p, s, r, sel) => p.InNeck(s, r, sel));
            yield return Case<InvertedHammer>("INVERTEDHAMMER", (p, s, r, sel) => p.InvertedHammer(s, r, sel));
            yield return Case<Kicking>("KICKING", (p, s, r, sel) => p.Kicking(s, r, sel));
            yield return Case<KickingByLength>("KICKINGBYLENGTH", (p, s, r, sel) => p.KickingByLength(s, r, sel));
            yield return Case<LadderBottom>("LADDERBOTTOM", (p, s, r, sel) => p.LadderBottom(s, r, sel));
            yield return Case<LongLeggedDoji>("LONGLEGGEDDOJI", (p, s, r, sel) => p.LongLeggedDoji(s, r, sel));
            yield return Case<LongLineCandle>("LONGLINECANDLE", (p, s, r, sel) => p.LongLineCandle(s, r, sel));
            yield return Case<Marubozu>("MARUBOZU", (p, s, r, sel) => p.Marubozu(s, r, sel));
            yield return Case<MatchingLow>("MATCHINGLOW", (p, s, r, sel) => p.MatchingLow(s, r, sel));
            yield return Case<MatHold>("MATHOLD", (p, s, r, sel) => p.MatHold(s, 0.5m, r, sel));
            yield return Case<MorningDojiStar>("MORNINGDOJISTAR", (p, s, r, sel) => p.MorningDojiStar(s, 0.3m, r, sel));
            yield return Case<MorningStar>("MORNINGSTAR", (p, s, r, sel) => p.MorningStar(s, 0.3m, r, sel));
            yield return Case<OnNeck>("ONNECK", (p, s, r, sel) => p.OnNeck(s, r, sel));
            yield return Case<Piercing>("PIERCING", (p, s, r, sel) => p.Piercing(s, r, sel));
            yield return Case<RickshawMan>("RICKSHAWMAN", (p, s, r, sel) => p.RickshawMan(s, r, sel));
            yield return Case<RiseFallThreeMethods>("RISEFALLTHREEMETHODS", (p, s, r, sel) => p.RiseFallThreeMethods(s, r, sel));
            yield return Case<SeparatingLines>("SEPARATINGLINES", (p, s, r, sel) => p.SeparatingLines(s, r, sel));
            yield return Case<ShootingStar>("SHOOTINGSTAR", (p, s, r, sel) => p.ShootingStar(s, r, sel));
            yield return Case<ShortLineCandle>("SHORTLINECANDLE", (p, s, r, sel) => p.ShortLineCandle(s, r, sel));
            yield return Case<SpinningTop>("SPINNINGTOP", (p, s, r, sel) => p.SpinningTop(s, r, sel));
            yield return Case<StalledPattern>("STALLEDPATTERN", (p, s, r, sel) => p.StalledPattern(s, r, sel));
            yield return Case<StickSandwich>("STICKSANDWICH", (p, s, r, sel) => p.StickSandwich(s, r, sel));
            yield return Case<Takuri>("TAKURI", (p, s, r, sel) => p.Takuri(s, r, sel));
            yield return Case<TasukiGap>("TASUKIGAP", (p, s, r, sel) => p.TasukiGap(s, r, sel));
            yield return Case<Thrusting>("THRUSTING", (p, s, r, sel) => p.Thrusting(s, r, sel));
            yield return Case<Tristar>("TRISTAR", (p, s, r, sel) => p.Tristar(s, r, sel));
            yield return Case<UniqueThreeRiver>("UNIQUETHREERIVER", (p, s, r, sel) => p.UniqueThreeRiver(s, r, sel));
            yield return Case<UpsideGapTwoCrows>("UPSIDEGAPTWOCROWS", (p, s, r, sel) => p.UpsideGapTwoCrows(s, r, sel));
            yield return Case<UpDownGapThreeMethods>("UPDOWNGAPTHREEMETHODS", (p, s, r, sel) => p.UpDownGapThreeMethods(s, r, sel));
        }

        public class PatternCase
        {
            public string Prefix { get; }
            public Type ExpectedType { get; }
            public Func<AlgorithmCandlestickPatterns, Symbol, Resolution?, Func<IBaseData, IBaseDataBar>, IndicatorBase<IBaseDataBar>> Create { get; }

            public PatternCase(string prefix, Type expectedType,
                Func<AlgorithmCandlestickPatterns, Symbol, Resolution?, Func<IBaseData, IBaseDataBar>, IndicatorBase<IBaseDataBar>> create)
            {
                Prefix = prefix;
                ExpectedType = expectedType;
                Create = create;
            }

            public override string ToString() => Prefix;
        }
    }
}
