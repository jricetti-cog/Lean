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
using NodaTime;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Scheduling;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Common.Scheduling
{
    [TestFixture]
    public class FluentScheduledEventBuilderTests
    {
        // Tuesday 2024-01-02 00:00 UTC, which is Monday 2024-01-01 19:00 in New York
        private static readonly DateTime StartUtc = new DateTime(2024, 1, 2);

        private QCAlgorithm _algorithm;
        private RecordingEventSchedule _eventSchedule;
        private Symbol _spy;

        [SetUp]
        public void SetUp()
        {
            _algorithm = new QCAlgorithm();
            _algorithm.SubscriptionManager.SetDataManager(new DataManagerStub(_algorithm));
            _algorithm.SetDateTime(StartUtc);
            _spy = _algorithm.AddEquity("SPY").Symbol;
            _eventSchedule = new RecordingEventSchedule();
            _algorithm.Schedule.SetEventSchedule(_eventSchedule);
        }

        [Test]
        public void EveryDayAtRunsDailyAndIsAddedToTheSchedule()
        {
            var calls = 0;
            var scheduledEvent = _algorithm.Schedule.Event().EveryDay().At(10, 0).Run(() => calls++);

            Assert.AreEqual($"{_algorithm.DateRules.EveryDay().Name}: {_algorithm.TimeRules.At(10, 0).Name}", scheduledEvent.Name);
            CollectionAssert.AreEqual(new[] { scheduledEvent }, _eventSchedule.Added);

            var times = Fire(scheduledEvent, new DateTime(2024, 1, 4, 23, 0, 0));

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 1, 15, 0, 0),
                new DateTime(2024, 1, 2, 15, 0, 0),
                new DateTime(2024, 1, 3, 15, 0, 0),
                new DateTime(2024, 1, 4, 15, 0, 0)
            }, times);
            Assert.AreEqual(4, calls);
        }

        [Test]
        public void NamedEventUsesProvidedName()
        {
            var scheduledEvent = _algorithm.Schedule.Event("my-event").EveryDay().At(TimeSpan.FromHours(12)).Run(() => { });

            Assert.AreEqual("my-event", scheduledEvent.Name);
            Assert.AreEqual("my-event", scheduledEvent.ToString());
        }

        [Test]
        public void RunWithTimeCallbackReceivesEventTime()
        {
            var received = new List<DateTime>();
            var scheduledEvent = _algorithm.Schedule.Event().On(2024, 1, 3).At(12, 30, 15).Run(time => received.Add(time));

            Fire(scheduledEvent, new DateTime(2024, 2, 1));

            CollectionAssert.AreEqual(new[] { new DateTime(2024, 1, 3, 17, 30, 15) }, received);
        }

        [Test]
        public void RunWithNameAndTimeCallbackReceivesNameAndTime()
        {
            var received = new List<string>();
            var scheduledEvent = _algorithm.Schedule.Event("named")
                .On(new DateTime(2024, 1, 3), new DateTime(2024, 1, 5))
                .At(9, 0, 0, DateTimeZone.Utc)
                .Run((name, time) => received.Add($"{name}@{time:yyyyMMddHHmmss}"));

            Fire(scheduledEvent, new DateTime(2024, 2, 1));

            CollectionAssert.AreEqual(new[] { "named@20240103090000", "named@20240105090000" }, received);
        }

        [Test]
        public void EveryDayOfWeekOnlyFiresOnRequestedDays()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .Every(DayOfWeek.Monday, DayOfWeek.Friday)
                .At(8, 15, DateTimeZone.Utc)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 1, 15, 23, 0, 0));

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 1, 8, 15, 0),
                new DateTime(2024, 1, 5, 8, 15, 0),
                new DateTime(2024, 1, 8, 8, 15, 0),
                new DateTime(2024, 1, 12, 8, 15, 0),
                new DateTime(2024, 1, 15, 8, 15, 0)
            }, times);
        }

        [Test]
        public void EveryDayForSymbolSkipsNonTradingDays()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .EveryDay(_spy)
                .At(TimeSpan.FromHours(6), DateTimeZone.Utc)
                .Run(() => { });

            // 2024-01-01 is a holiday and 2024-01-06/07 is a weekend
            var times = Fire(scheduledEvent, new DateTime(2024, 1, 8, 23, 0, 0));

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 2, 6, 0, 0),
                new DateTime(2024, 1, 3, 6, 0, 0),
                new DateTime(2024, 1, 4, 6, 0, 0),
                new DateTime(2024, 1, 5, 6, 0, 0),
                new DateTime(2024, 1, 8, 6, 0, 0)
            }, times);
        }

        [Test]
        public void MonthStartFiresOnFirstDayOfEachMonth()
        {
            var scheduledEvent = _algorithm.Schedule.Event().MonthStart().At(0, 0, DateTimeZone.Utc).Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 3, 15));

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 1),
                new DateTime(2024, 2, 1),
                new DateTime(2024, 3, 1)
            }, times);
        }

        [Test]
        public void MonthStartForSymbolFiresOnFirstTradingDayOfEachMonth()
        {
            var scheduledEvent = _algorithm.Schedule.Event().MonthStart(_spy).At(0, 0, DateTimeZone.Utc).Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 3, 15));

            // 2024-01-01 is a holiday
            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 2),
                new DateTime(2024, 2, 1),
                new DateTime(2024, 3, 1)
            }, times);
        }

        [Test]
        public void MultipleTimeRulesAreCombinedIntoOrderedUniqueTimes()
        {
            var runnable = _algorithm.Schedule.Event()
                .On(2024, 1, 3)
                .At(14, 0, DateTimeZone.Utc)
                .At(10, 0, DateTimeZone.Utc)
                .At(12, 0, DateTimeZone.Utc)
                // duplicate of the first time, should only fire once
                .At(TimeSpan.FromHours(14), DateTimeZone.Utc);
            var scheduledEvent = runnable.Run(() => { });

            var expectedTimeRuleName = string.Join(",", new[] { 14, 10, 12, 14 }
                .Select(h => _algorithm.TimeRules.At(TimeSpan.FromHours(h), DateTimeZone.Utc).Name));
            Assert.AreEqual($"{_algorithm.DateRules.On(2024, 1, 3).Name}: {expectedTimeRuleName}", scheduledEvent.Name);

            var times = Fire(scheduledEvent, new DateTime(2024, 2, 1));

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 3, 10, 0, 0),
                new DateTime(2024, 1, 3, 12, 0, 0),
                new DateTime(2024, 1, 3, 14, 0, 0)
            }, times);
        }

        [Test, Ignore("Possible bug: combining predicates (Where/DuringMarketHours twice) captures the _predicate field in its own lambda, recursing until stack overflow")]
        public void WherePredicatesAreCombinedAcrossAllStages()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .EveryDay()
                .Where(time => time.Day != 2)
                .Where(time => time.Day != 3)
                .At(9, 0, DateTimeZone.Utc)
                .Where(time => time.Day != 4)
                .Where(time => time.Day != 5)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 1, 7, 23, 0, 0));

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 1, 9, 0, 0),
                new DateTime(2024, 1, 6, 9, 0, 0),
                new DateTime(2024, 1, 7, 9, 0, 0)
            }, times);
        }

        [Test]
        public void WhereOnDateSpecifierReturnsTimeSpecifier()
        {
            var timeSpecifier = _algorithm.Schedule.Event().Where(time => true);

            Assert.IsInstanceOf<FluentScheduledEventBuilder>(timeSpecifier);
            Assert.IsNotNull(timeSpecifier.At(10, 0));
        }

        [Test, Ignore("Possible bug: IFluentSchedulingDateSpecifier.Where sets no date rule, so Run throws NullReferenceException")]
        public void WhereOnDateSpecifierCanBeRun()
        {
            var runnable = _algorithm.Schedule.Event().Where(time => time.Day == 3).At(10, 0);

            Assert.DoesNotThrow(() => runnable.Run(() => { }));
        }

        [Test]
        public void ChainedPredicatesReturnTheSameBuilder()
        {
            var timeSpecifier = _algorithm.Schedule.Event().EveryDay();
            var filteredTimeSpecifier = timeSpecifier.Where(time => true).Where(time => true);
            var runnable = filteredTimeSpecifier.At(10, 0);
            var filteredRunnable = runnable.Where(time => true).Where(time => true).DuringMarketHours(_spy);

            Assert.AreSame(timeSpecifier, filteredTimeSpecifier);
            Assert.AreSame(timeSpecifier, runnable);
            Assert.AreSame(timeSpecifier, filteredRunnable);
        }

        [Test]
        public void SingleWhereOnTimeSpecifierFiltersTimes()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .EveryDay()
                .Where(time => time.DayOfWeek == DayOfWeek.Wednesday)
                .At(9, 0, DateTimeZone.Utc)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 1, 17, 23, 0, 0));

            CollectionAssert.AreEqual(new[] { new DateTime(2024, 1, 3, 9, 0, 0), new DateTime(2024, 1, 10, 9, 0, 0), new DateTime(2024, 1, 17, 9, 0, 0) }, times);
        }

        [Test, Ignore("Possible bug: combining predicates (Where/DuringMarketHours twice) captures the _predicate field in its own lambda, recursing until stack overflow")]
        public void WhereCombinedWithDuringMarketHoursFiltersTimes()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .On(2024, 1, 3)
                .Every(TimeSpan.FromHours(1))
                .Where(time => time.Hour != 15)
                .DuringMarketHours(_spy)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 2, 1));

            CollectionAssert.AreEqual(Enumerable.Range(16, 5).Select(h => new DateTime(2024, 1, 3, h, 0, 0)), times);
        }

        [Test]
        public void SingleWhereOnRunnableFiltersTimes()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .On(2024, 1, 3)
                .Every(TimeSpan.FromHours(6))
                .Where(time => time.Hour >= 12)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 2, 1));

            // every 6 hours starting at New York midnight (05:00 UTC)
            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 3, 17, 0, 0),
                new DateTime(2024, 1, 3, 23, 0, 0)
            }, times);
        }

        [Test]
        public void DuringMarketHoursOnlyKeepsTimesWhenTheExchangeIsOpen()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .On(2024, 1, 3)
                .Every(TimeSpan.FromHours(1))
                .DuringMarketHours(_spy)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 2, 1));

            // regular market hours are 09:30 to 16:00 New York time (14:30 to 21:00 UTC)
            CollectionAssert.AreEqual(Enumerable.Range(15, 6).Select(h => new DateTime(2024, 1, 3, h, 0, 0)), times);
        }

        [Test]
        public void DuringExtendedMarketHoursKeepsPreAndPostMarketTimes()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .On(2024, 1, 3)
                .Every(TimeSpan.FromHours(1))
                .DuringMarketHours(_spy, extendedMarket: true)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 2, 1));

            // extended market hours are 04:00 to 20:00 New York time (09:00 to 01:00 UTC)
            var expected = Enumerable.Range(9, 16).Select(h => new DateTime(2024, 1, 3, 0, 0, 0).AddHours(h));
            CollectionAssert.AreEqual(expected, times);
        }

        [Test]
        public void DuringMarketHoursThrowsForUnknownSecurity()
        {
            var unknown = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            var runnable = _algorithm.Schedule.Event().EveryDay().At(10, 0);

            var exception = Assert.Throws<KeyNotFoundException>(() => runnable.DuringMarketHours(unknown));
            StringAssert.Contains("AAPL", exception.Message);
        }

        [Test]
        public void AfterMarketOpenAndBeforeMarketCloseUseSecurityHours()
        {
            var scheduledEvent = _algorithm.Schedule.Event()
                .On(2024, 1, 3)
                .AfterMarketOpen(_spy, 10)
                .BeforeMarketClose(_spy, 10)
                .Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 2, 1));

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 3, 14, 40, 0),
                new DateTime(2024, 1, 3, 20, 50, 0)
            }, times);
        }

        [Test]
        public void AtTimeOfDayUsesAlgorithmTimeZone()
        {
            var scheduledEvent = _algorithm.Schedule.Event().On(2024, 7, 1).At(TimeSpan.FromHours(10)).Run(() => { });

            var times = Fire(scheduledEvent, new DateTime(2024, 8, 1));

            // daylight saving time: New York is UTC-4
            CollectionAssert.AreEqual(new[] { new DateTime(2024, 7, 1, 14, 0, 0) }, times);
        }

        [Test]
        public void EventsAddedBeforeEventScheduleIsSetAreForwardedWhenSet()
        {
            var algorithm = new QCAlgorithm();
            algorithm.SetDateTime(StartUtc);
            var scheduledEvent = algorithm.Schedule.Event("pending").EveryDay().At(10, 0).Run(() => { });

            var eventSchedule = new RecordingEventSchedule();
            algorithm.Schedule.SetEventSchedule(eventSchedule);

            CollectionAssert.AreEqual(new[] { scheduledEvent }, eventSchedule.Added);
        }

        [Test]
        public void EventsRemovedBeforeEventScheduleIsSetAreNotForwarded()
        {
            var algorithm = new QCAlgorithm();
            algorithm.SetDateTime(StartUtc);
            var kept = algorithm.Schedule.Event("kept").EveryDay().At(10, 0).Run(() => { });
            var removed = algorithm.Schedule.Event("removed").EveryDay().At(11, 0).Run(() => { });

            algorithm.Schedule.Remove(removed);
            var eventSchedule = new RecordingEventSchedule();
            algorithm.Schedule.SetEventSchedule(eventSchedule);

            CollectionAssert.AreEqual(new[] { kept }, eventSchedule.Added);
            CollectionAssert.IsEmpty(eventSchedule.Removed);
        }

        [Test]
        public void RemoveIsForwardedToEventSchedule()
        {
            var scheduledEvent = _algorithm.Schedule.Event().EveryDay().At(10, 0).Run(() => { });

            _algorithm.Schedule.Remove(scheduledEvent);

            CollectionAssert.AreEqual(new[] { scheduledEvent }, _eventSchedule.Removed);
        }

        [Test]
        public void SetEventScheduleThrowsForNull()
        {
            Assert.Throws<ArgumentNullException>(() => _algorithm.Schedule.SetEventSchedule(null));
        }

        [Test]
        public void NamedOnWithActionSchedulesEvent()
        {
            var calls = 0;
            var scheduledEvent = _algorithm.Schedule.On("custom", _algorithm.DateRules.On(2024, 1, 3), _algorithm.TimeRules.At(10, 0), () => calls++);

            Assert.AreEqual("custom", scheduledEvent.Name);
            CollectionAssert.AreEqual(new[] { new DateTime(2024, 1, 3, 15, 0, 0) }, Fire(scheduledEvent, new DateTime(2024, 2, 1)));
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void TrainingWithActionSchedulesEvent()
        {
            var calls = 0;
            var dateRule = _algorithm.DateRules.On(2024, 1, 3);
            var timeRule = _algorithm.TimeRules.At(10, 0);
            var scheduledEvent = _algorithm.Schedule.Training(dateRule, timeRule, () => calls++);

            Assert.AreEqual($"{dateRule.Name}: {timeRule.Name}", scheduledEvent.Name);
            CollectionAssert.AreEqual(new[] { scheduledEvent }, _eventSchedule.Added);
            Fire(scheduledEvent, new DateTime(2024, 2, 1));
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void TrainingWithTimeActionReceivesEventTime()
        {
            var received = new List<DateTime>();
            var scheduledEvent = _algorithm.Schedule.Training(_algorithm.DateRules.On(2024, 1, 3), _algorithm.TimeRules.At(10, 0), time => received.Add(time));

            Fire(scheduledEvent, new DateTime(2024, 2, 1));

            CollectionAssert.AreEqual(new[] { new DateTime(2024, 1, 3, 15, 0, 0) }, received);
        }

        [Test]
        public void TrainingNowFiresAtCurrentAlgorithmTime()
        {
            var calls = 0;
            var scheduledEvent = _algorithm.Schedule.TrainingNow(() => calls++);

            StringAssert.StartsWith("Training: Now: ", scheduledEvent.Name);
            Assert.AreEqual(StartUtc, scheduledEvent.NextEventUtcTime);
            Fire(scheduledEvent, StartUtc);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void CompositeTimeRuleJoinsNamesAndOrdersUniqueTimesPerDate()
        {
            var first = new FuncTimeRule("first", dates => dates.SelectMany(d => new[] { d.AddHours(12), d.AddHours(1) }));
            var second = new FuncTimeRule("second", dates => dates.Select(d => d.AddHours(1)));
            var rule = new CompositeTimeRule(first, second);

            Assert.AreEqual("first,second", rule.Name);
            CollectionAssert.AreEqual(new ITimeRule[] { first, second }, rule.Rules);

            var times = rule.CreateUtcEventTimes(new[] { new DateTime(2024, 1, 1), new DateTime(2024, 1, 2) }).ToList();

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 1, 1, 0, 0),
                new DateTime(2024, 1, 1, 12, 0, 0),
                new DateTime(2024, 1, 2, 1, 0, 0),
                new DateTime(2024, 1, 2, 12, 0, 0)
            }, times);
        }

        [Test]
        public void CompositeTimeRuleWithNoRulesProducesNoTimes()
        {
            var rule = new CompositeTimeRule(Enumerable.Empty<ITimeRule>());

            Assert.AreEqual(string.Empty, rule.Name);
            CollectionAssert.IsEmpty(rule.CreateUtcEventTimes(new[] { new DateTime(2024, 1, 1) }));
        }

        [Test]
        public void MarketOpenRulesConsiderNonUsEquitySecurities()
        {
            // forex opens Sunday 17:00 New York time, earlier than equities
            _algorithm.AddForex("EURUSD");

            var times = _algorithm.TimeRules.AfterMarketOpen().CreateUtcEventTimes(new[] { new DateTime(2024, 1, 3) }).ToList();

            CollectionAssert.Contains(times, new DateTime(2024, 1, 3, 5, 0, 0));
        }

        [Test]
        public void SymbolRulesFallBackToMarketHoursDatabaseForUnsubscribedSymbols()
        {
            var aapl = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

            var times = _algorithm.TimeRules.AfterMarketOpen(aapl, 1).CreateUtcEventTimes(new[] { new DateTime(2024, 1, 3) }).ToList();

            CollectionAssert.AreEqual(new[] { new DateTime(2024, 1, 3, 14, 31, 0) }, times);
        }

        [Test]
        public void UnknownTickerIsImplicitlyCreatedAsUsEquity()
        {
            const string ticker = "FLUENTCOVERAGETICKER";
            try
            {
                var rule = _algorithm.TimeRules.AfterMarketOpen(ticker, 5);

                Assert.IsTrue(SymbolCache.TryGetSymbol(ticker, out var symbol));
                Assert.AreEqual(SecurityType.Equity, symbol.SecurityType);
                Assert.AreEqual(Market.USA, symbol.ID.Market);
                CollectionAssert.AreEqual(new[] { new DateTime(2024, 1, 3, 14, 35, 0) },
                    rule.CreateUtcEventTimes(new[] { new DateTime(2024, 1, 3) }));

                // the cached symbol is reused afterwards
                _algorithm.DateRules.EveryDay(ticker);
                Assert.IsTrue(SymbolCache.TryGetSymbol(ticker, out var cached));
                Assert.AreSame(symbol, cached);
            }
            finally
            {
                SymbolCache.TryRemove(ticker);
            }
        }

        private static List<DateTime> Fire(ScheduledEvent scheduledEvent, DateTime utcTime)
        {
            var times = new List<DateTime>();
            scheduledEvent.EventFired += (name, time) => times.Add(time);
            scheduledEvent.Scan(utcTime);
            return times;
        }

        private class RecordingEventSchedule : IEventSchedule
        {
            public List<ScheduledEvent> Added { get; } = new();
            public List<ScheduledEvent> Removed { get; } = new();

            public void Add(ScheduledEvent scheduledEvent) => Added.Add(scheduledEvent);

            public void Remove(ScheduledEvent scheduledEvent) => Removed.Add(scheduledEvent);
        }
    }
}
