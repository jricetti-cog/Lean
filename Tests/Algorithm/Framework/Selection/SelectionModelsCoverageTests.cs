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
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Tests.Engine.DataFeeds;
using static QuantConnect.Data.UniverseSelection.CoarseFundamentalDataProvider;

namespace QuantConnect.Tests.Algorithm.Framework.Selection
{
    [TestFixture]
    public class SelectionModelsCoverageTests
    {
        private static readonly DateTime FarFuture = new DateTime(2100, 1, 1);

        private static IEnumerable<TestCaseData> ETFUniverseCases()
        {
            yield return new TestCaseData(new EnergyETFUniverse(), "XLE", new DateTime(1998, 12, 22)).SetName("EnergyETFUniverse");
            yield return new TestCaseData(new MetalsETFUniverse(), "GLD", null).SetName("MetalsETFUniverse");
            yield return new TestCaseData(new TechnologyETFUniverse(), "QQQ", null).SetName("TechnologyETFUniverse");
            yield return new TestCaseData(new USTreasuriesETFUniverse(), "IEF", null).SetName("USTreasuriesETFUniverse");
            yield return new TestCaseData(new VolatilityETFUniverse(), "SQQQ", null).SetName("VolatilityETFUniverse");
            yield return new TestCaseData(new SP500SectorsETFUniverse(), "XLK", null).SetName("SP500SectorsETFUniverse");
            yield return new TestCaseData(new LiquidETFUniverse(), "XLE", null).SetName("LiquidETFUniverse");
        }

        [TestCaseSource(nameof(ETFUniverseCases))]
        public void ETFUniversesAddTickersAsTheirInceptionDatePasses(InceptionDateUniverseSelectionModel model, string expectedTicker, DateTime? expectedTickerInception)
        {
            var algorithm = new QCAlgorithm();

            if (model is not LiquidETFUniverse)
            {
                Assert.AreSame(Universe.Unchanged, model.Select(algorithm, new DateTime(1900, 1, 1)));
            }

            var all = model.Select(algorithm, FarFuture).ToList();
            CollectionAssert.Contains(all, expectedTicker);
            CollectionAssert.AllItemsAreUnique(all);
            Assert.Greater(all.Count, 1);

            // nothing new left to add
            Assert.AreSame(Universe.Unchanged, model.Select(algorithm, FarFuture.AddDays(1)));

            if (expectedTickerInception.HasValue)
            {
                var fresh = new EnergyETFUniverse();
                Assert.AreSame(Universe.Unchanged, fresh.Select(algorithm, expectedTickerInception.Value.AddDays(-1)));
                CollectionAssert.AreEqual(new[] { expectedTicker }, fresh.Select(algorithm, expectedTickerInception.Value).ToList());
            }
        }

        [Test]
        public void InceptionDateModelAccumulatesTickersInDateOrder()
        {
            var algorithm = new QCAlgorithm();
            var model = new InceptionDateUniverseSelectionModel("test", new Dictionary<string, DateTime>
            {
                { "AAA", new DateTime(2000, 1, 1) },
                { "BBB", new DateTime(2001, 1, 1) },
                { "CCC", new DateTime(2001, 6, 1) }
            });

            Assert.AreSame(Universe.Unchanged, model.Select(algorithm, new DateTime(1999, 12, 31)));
            CollectionAssert.AreEqual(new[] { "AAA" }, model.Select(algorithm, new DateTime(2000, 1, 1)).ToList());
            Assert.AreSame(Universe.Unchanged, model.Select(algorithm, new DateTime(2000, 6, 1)));
            CollectionAssert.AreEqual(new[] { "AAA", "BBB", "CCC" }, model.Select(algorithm, new DateTime(2002, 1, 1)).ToList());
        }

        [Test]
        public void LiquidETFUniverseCombinesAllGroupings()
        {
            var groupings = new[]
            {
                LiquidETFUniverse.SP500Sectors, LiquidETFUniverse.Energy, LiquidETFUniverse.Metals,
                LiquidETFUniverse.Technology, LiquidETFUniverse.Treasuries, LiquidETFUniverse.Volatility
            };
            var expected = groupings.SelectMany(x => x).Select(x => x.Value).ToList();

            var selected = new LiquidETFUniverse().Select(new QCAlgorithm(), FarFuture).ToList();

            CollectionAssert.AreEquivalent(expected, selected);
            foreach (var grouping in groupings)
            {
                Assert.AreEqual(grouping.Long.Count + grouping.Inverse.Count, grouping.Count);
                CollectionAssert.AreEqual(grouping.Long.Concat(grouping.Inverse), grouping);
                Assert.IsTrue(grouping.All(x => x.SecurityType == SecurityType.Equity && x.ID.Market == Market.USA));
            }
            CollectionAssert.Contains(LiquidETFUniverse.Energy.Inverse.Select(x => x.Value), "ERY");
            CollectionAssert.Contains(LiquidETFUniverse.Energy.Long.Select(x => x.Value), "USO");
        }

        [Test]
        public void LiquidETFGroupingToStringDescribesLongAndInverseSymbols()
        {
            Assert.AreEqual("No Symbols", new LiquidETFUniverse.Grouping(Array.Empty<string>(), Array.Empty<string>()).ToString());
            Assert.AreEqual("2 symbols: Long: AAA,BBB", new LiquidETFUniverse.Grouping(new[] { "AAA", "BBB" }, Array.Empty<string>()).ToString());
            Assert.AreEqual("1 symbols: Inverse: CCC", new LiquidETFUniverse.Grouping(Array.Empty<string>(), new[] { "CCC" }).ToString());
            Assert.AreEqual("2 symbols: Long: AAA Inverse: CCC", new LiquidETFUniverse.Grouping(new[] { "AAA" }, new[] { "CCC" }).ToString());
            StringAssert.StartsWith($"{LiquidETFUniverse.Energy.Count} symbols: Long: VDE,USO", LiquidETFUniverse.Energy.ToString());
        }

        [Test]
        public void ScheduledUniverseSelectionModelCreatesScheduledUniverse()
        {
            var algorithm = new AlgorithmStub();
            var settings = new UniverseSettings(Resolution.Hour, 2m, true, false, TimeSpan.FromDays(1));
            var calls = new List<DateTime>();
            Func<DateTime, IEnumerable<Symbol>> selector = time =>
            {
                calls.Add(time);
                return new[] { Symbols.SPY };
            };

            var withSettings = new ScheduledUniverseSelectionModel(algorithm.DateRules.EveryDay(), algorithm.TimeRules.At(10, 0), selector, settings);
            var universe = withSettings.CreateUniverses(algorithm).Single();
            Assert.IsInstanceOf<ScheduledUniverse>(universe);
            Assert.AreSame(settings, universe.UniverseSettings);
            Assert.AreEqual(TimeZones.Utc, universe.Configuration.ExchangeTimeZone);

            var utcTime = new DateTime(2020, 1, 2, 10, 0, 0);
            CollectionAssert.AreEqual(new[] { Symbols.SPY }, universe.SelectSymbols(utcTime, null).ToList());
            CollectionAssert.AreEqual(new[] { utcTime }, calls);

            var withTimeZone = new ScheduledUniverseSelectionModel(TimeZones.NewYork, algorithm.DateRules.EveryDay(), algorithm.TimeRules.At(10, 0), selector);
            var nyUniverse = withTimeZone.CreateUniverses(algorithm).Single();
            Assert.AreSame(algorithm.UniverseSettings, nyUniverse.UniverseSettings);
            Assert.AreEqual(TimeZones.NewYork, nyUniverse.Configuration.ExchangeTimeZone);
        }

        [Test]
        public void EmaCrossUniverseSelectionModelSelectsUptrendingSymbolsOrderedByDelta()
        {
            var algorithm = new QCAlgorithm();
            var model = new EmaCrossUniverseSelectionModel(fastPeriod: 2, slowPeriod: 4, universeCount: 1);
            var strongUp = Symbol.Create("UPA", SecurityType.Equity, Market.USA);
            var weakUp = Symbol.Create("UPB", SecurityType.Equity, Market.USA);
            var down = Symbol.Create("DWN", SecurityType.Equity, Market.USA);

            List<Symbol> selected = null;
            var start = new DateTime(2020, 1, 1);
            for (var day = 0; day < 6; day++)
            {
                var time = start.AddDays(day);
                var coarse = new[]
                {
                    Coarse(strongUp, time, 100m * (decimal)Math.Pow(1.2, day)),
                    Coarse(weakUp, time, 100m * (decimal)Math.Pow(1.05, day)),
                    Coarse(down, time, 100m * (decimal)Math.Pow(0.9, day))
                };
                selected = model.SelectCoarse(algorithm, coarse).ToList();
                if (day < 3)
                {
                    // slow EMA not ready yet
                    Assert.IsEmpty(selected);
                }
            }

            CollectionAssert.AreEqual(new[] { strongUp }, selected);

            var unlimited = new EmaCrossUniverseSelectionModel(2, 4, 10);
            for (var day = 0; day < 6; day++)
            {
                var time = start.AddDays(day);
                selected = unlimited.SelectCoarse(algorithm, new[]
                {
                    Coarse(weakUp, time, 100m * (decimal)Math.Pow(1.05, day)),
                    Coarse(strongUp, time, 100m * (decimal)Math.Pow(1.2, day)),
                    Coarse(down, time, 100m * (decimal)Math.Pow(0.9, day))
                }).ToList();
            }
            CollectionAssert.AreEqual(new[] { strongUp, weakUp }, selected);
        }

        [Test]
        public void CoarseAndFineModelsDelegateToTheirSelectors()
        {
            var algorithm = new QCAlgorithm();
            var coarseInput = new[] { Coarse(Symbols.SPY, new DateTime(2020, 1, 1), 10), Coarse(Symbols.AAPL, new DateTime(2020, 1, 1), 20) };
            var fineInput = new[] { new FineFundamental(new DateTime(2020, 1, 1), Symbols.AAPL) };

            var coarseModel = new CoarseFundamentalUniverseSelectionModel(c => c.Where(x => x.Price > 15).Select(x => x.Symbol));
            CollectionAssert.AreEqual(new[] { Symbols.AAPL }, coarseModel.SelectCoarse(algorithm, coarseInput).ToList());

            var fineModel = new FineFundamentalUniverseSelectionModel(c => c.Select(x => x.Symbol), f => f.Select(x => x.Symbol).Reverse());
            CollectionAssert.AreEqual(new[] { Symbols.SPY, Symbols.AAPL }, fineModel.SelectCoarse(algorithm, coarseInput).ToList());
            CollectionAssert.AreEqual(new[] { Symbols.AAPL }, fineModel.SelectFine(algorithm, fineInput).ToList());
        }

        [Test]
        public void FundamentalModelFactoriesAndDefaultSelectBehavior()
        {
            var algorithm = new QCAlgorithm();
            var coarse = new[] { Coarse(Symbols.SPY, new DateTime(2020, 1, 1), 10) };

            var coarseModel = (FundamentalUniverseSelectionModel)FundamentalUniverseSelectionModel.Coarse(c => c.Select(x => x.Symbol));
            Assert.IsInstanceOf<CoarseFundamentalUniverseSelectionModel>(coarseModel);
            CollectionAssert.AreEqual(new[] { Symbols.SPY }, coarseModel.SelectCoarse(algorithm, coarse).ToList());

            var fineModel = (FundamentalUniverseSelectionModel)FundamentalUniverseSelectionModel.Fine(c => c.Select(x => x.Symbol), f => f.Select(x => x.Symbol));
            Assert.IsInstanceOf<FineFundamentalUniverseSelectionModel>(fineModel);

            var fundamentalModel = (FundamentalUniverseSelectionModel)FundamentalUniverseSelectionModel.Fundamental(f => new[] { Symbols.IBM });
            CollectionAssert.AreEqual(new[] { Symbols.IBM }, fundamentalModel.Select(algorithm, Enumerable.Empty<Fundamental>()).ToList());

            var noSelector = new FundamentalUniverseSelectionModel();
            Assert.Throws<NotImplementedException>(() => noSelector.Select(algorithm, Enumerable.Empty<Fundamental>()).ToList());
            Assert.Throws<NotImplementedException>(() => noSelector.SelectCoarse(algorithm, coarse).ToList());
        }

        [Test]
        public void FundamentalModelCreatesUniverses()
        {
            var algorithm = new AlgorithmStub();
            var settings = new UniverseSettings(Resolution.Daily, 1m, true, false, TimeSpan.FromDays(1));

            var fundamental = new FundamentalUniverseSelectionModel(Market.USA, f => new[] { Symbols.SPY }, settings);
            var universe = fundamental.CreateUniverses(algorithm).Single();
            Assert.AreSame(settings, universe.UniverseSettings);

            var coarse = new CoarseFundamentalUniverseSelectionModel(c => c.Select(x => x.Symbol), settings);
            var coarseUniverse = coarse.CreateUniverses(algorithm).Single();
            Assert.AreSame(settings, coarseUniverse.UniverseSettings);

            var fine = new FineFundamentalUniverseSelectionModel(c => c.Select(x => x.Symbol), f => f.Select(x => x.Symbol), settings);
            var fineUniverse = fine.CreateUniverses(algorithm).Single();
            Assert.IsInstanceOf<FineFundamentalFilteredUniverse>(fineUniverse);

            var asyncSettings = new UniverseSettings(settings) { Asynchronous = true };
            var asyncFine = new FineFundamentalUniverseSelectionModel(c => c.Select(x => x.Symbol), f => f.Select(x => x.Symbol), asyncSettings);
            Assert.Throws<ArgumentException>(() => asyncFine.CreateUniverses(algorithm).ToList());
        }

        [Test]
        public void OptionUniverseSelectionModelCreatesOneChainPerUnderlying()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetDateTime(new DateTime(2021, 1, 4, 15, 0, 0));
            var spyOption = Symbol.CreateCanonicalOption(Symbols.SPY);
            var spyOptionContract = Symbol.CreateOption(Symbols.SPY, Market.USA, OptionStyle.American, OptionRight.Call, 400m, new DateTime(2021, 3, 19));
            var aaplOption = Symbol.CreateCanonicalOption(Symbols.AAPL);
            var model = new OptionUniverseSelectionModel(TimeSpan.FromHours(6), _ => new[] { spyOption, spyOptionContract, aaplOption });

            Assert.AreEqual(DateTime.MinValue, model.GetNextRefreshTimeUtc());
            var universes = model.CreateUniverses(algorithm).ToList();

            Assert.AreEqual(2, universes.Count);
            Assert.AreEqual(algorithm.UtcTime.AddHours(6), model.GetNextRefreshTimeUtc());
            CollectionAssert.AreEquivalent(new[] { Symbols.SPY, Symbols.AAPL }, universes.Select(x => x.Configuration.Symbol.Underlying));
        }

        [Test]
        public void OptionUniverseSelectionModelRejectsNonOptionSymbols()
        {
            var algorithm = new AlgorithmStub();
            var model = new OptionUniverseSelectionModel(TimeSpan.FromDays(1), _ => new[] { Symbols.SPY }, null);

            var exception = Assert.Throws<ArgumentException>(() => model.CreateUniverses(algorithm).ToList());
            StringAssert.Contains("must return option", exception.Message);
        }

        [Test]
        public void FutureUniverseSelectionModelCreatesOneChainPerCanonical()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetDateTime(new DateTime(2021, 1, 4, 15, 0, 0));
            var es = Symbol.Create("ES", SecurityType.Future, Market.CME);
            var single = new FutureUniverseSelectionModel(TimeSpan.FromHours(1), _ => new[] { es }).CreateUniverses(algorithm).ToList();
            var model = new FutureUniverseSelectionModel(TimeSpan.FromHours(1), _ => new[] { es, es });

            var universes = model.CreateUniverses(algorithm).ToList();

            Assert.Greater(single.Count, 0);
            Assert.AreEqual(single.Count, universes.Count);
            Assert.AreEqual(algorithm.UtcTime.AddHours(1), model.GetNextRefreshTimeUtc());

            var invalid = new FutureUniverseSelectionModel(TimeSpan.FromHours(1), _ => new[] { Symbols.SPY });
            Assert.Throws<ArgumentException>(() => invalid.CreateUniverses(algorithm).ToList());
        }

        private static CoarseFundamental Coarse(Symbol symbol, DateTime time, decimal price)
        {
            return new CoarseFundamentalSource { Symbol = symbol, Time = time, EndTime = time, Value = price };
        }
    }
}
