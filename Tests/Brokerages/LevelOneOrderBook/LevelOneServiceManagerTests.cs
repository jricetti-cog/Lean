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
using QuantConnect.Data.Market;
using QuantConnect.Brokerages.LevelOneOrderBook;

namespace QuantConnect.Tests.Brokerages.LevelOneOrderBook
{
    [TestFixture]
    public class LevelOneServiceManagerTests
    {
        private static readonly DateTime Now = new DateTime(2026, 6, 20, 14, 30, 0);

        [Test]
        public void SubscribeRegistersSymbolAndForwardsQuotesToTheAggregator()
        {
            var aggregator = new FakeDataAggregator();
            var subscribed = new List<Symbol>();
            using var manager = new LevelOneServiceManager(aggregator,
                (symbols, _) => { subscribed.AddRange(symbols); return true; },
                (_, _) => true);

            Assert.IsTrue(manager.IsEmpty);
            manager.Subscribe(CreateConfig(Symbols.AAPL));

            Assert.IsFalse(manager.IsEmpty);
            Assert.AreEqual(1, manager.Count);
            CollectionAssert.AreEqual(new[] { Symbols.AAPL }, subscribed);
            CollectionAssert.AreEqual(new[] { Symbols.AAPL }, manager.GetSubscribedSymbols().ToList());

            manager.HandleQuote(Symbols.AAPL, Now, 100m, 1m, 101m, 2m);

            Assert.AreEqual(1, aggregator.Data.Count);
            var tick = (Tick)aggregator.Data[0];
            Assert.AreEqual(Symbols.AAPL, tick.Symbol);
            Assert.AreEqual(TickType.Quote, tick.TickType);
            Assert.AreEqual(100m, tick.BidPrice);
            Assert.AreEqual(101m, tick.AskPrice);
        }

        [Test]
        public void ForwardsTradesAndOpenInterest()
        {
            var aggregator = new FakeDataAggregator();
            using var manager = new LevelOneServiceManager(aggregator, (_, _) => true, (_, _) => true);
            manager.Subscribe(CreateConfig(Symbols.SPY_C_192_Feb19_2016));

            manager.HandleLastTrade(Symbols.SPY_C_192_Feb19_2016, Now, 3m, 1.5m, "cond", "exch");
            manager.HandleOpenInterest(Symbols.SPY_C_192_Feb19_2016, Now, 1000m);

            Assert.AreEqual(2, aggregator.Data.Count);
            var trade = (Tick)aggregator.Data[0];
            Assert.AreEqual(TickType.Trade, trade.TickType);
            Assert.AreEqual(1.5m, trade.Value);
            Assert.AreEqual(3m, trade.Quantity);
            var openInterest = (Tick)aggregator.Data[1];
            Assert.AreEqual(TickType.OpenInterest, openInterest.TickType);
            Assert.AreEqual(1000m, openInterest.Value);
        }

        [Test]
        public void IgnoresUpdatesForUnknownSymbols()
        {
            var aggregator = new FakeDataAggregator();
            using var manager = new LevelOneServiceManager(aggregator, (_, _) => true, (_, _) => true);

            manager.HandleQuote(Symbols.AAPL, Now, 1m, 1m, 2m, 2m);
            manager.HandleLastTrade(Symbols.AAPL, Now, 1m, 1m);
            manager.HandleOpenInterest(Symbols.AAPL, Now, 1m);
            manager.SetIgnoreZeroSizeUpdates(Symbols.AAPL, true);

            Assert.AreEqual(0, aggregator.Data.Count);
            Assert.IsTrue(manager.IsEmpty);
        }

        [Test]
        public void FailedSubscriptionDoesNotRegisterSymbol()
        {
            var aggregator = new FakeDataAggregator();
            using var manager = new LevelOneServiceManager(aggregator, (_, _) => false, (_, _) => true);

            manager.Subscribe(CreateConfig(Symbols.AAPL));

            Assert.IsTrue(manager.IsEmpty);
            Assert.AreEqual(0, manager.Count);
            CollectionAssert.IsEmpty(manager.GetSubscribedSymbols());
            manager.HandleQuote(Symbols.AAPL, Now, 1m, 1m, 2m, 2m);
            Assert.AreEqual(0, aggregator.Data.Count);
        }

        [Test]
        public void UnsubscribeRemovesSymbolAndStopsForwarding()
        {
            var aggregator = new FakeDataAggregator();
            var unsubscribed = new List<Symbol>();
            using var manager = new LevelOneServiceManager(aggregator, (_, _) => true,
                (symbols, _) => { unsubscribed.AddRange(symbols); return true; });
            var config = CreateConfig(Symbols.AAPL);
            manager.Subscribe(config);

            manager.Unsubscribe(config);

            Assert.IsTrue(manager.IsEmpty);
            CollectionAssert.AreEqual(new[] { Symbols.AAPL }, unsubscribed);
            manager.HandleQuote(Symbols.AAPL, Now, 1m, 1m, 2m, 2m);
            Assert.AreEqual(0, aggregator.Data.Count);
        }

        [Test]
        public void FailedUnsubscribeKeepsTheMarketDataInstance()
        {
            var aggregator = new FakeDataAggregator();
            using var manager = new LevelOneServiceManager(aggregator, (_, _) => true, (_, _) => false);
            var config = CreateConfig(Symbols.AAPL);
            manager.Subscribe(config);

            manager.Unsubscribe(config);

            // the callback failed: the level one instance is still tracked and keeps forwarding data
            Assert.AreEqual(1, manager.Count);
            manager.HandleQuote(Symbols.AAPL, Now, 1m, 1m, 2m, 2m);
            Assert.AreEqual(1, aggregator.Data.Count);
        }

        [Test]
        public void SetIgnoreZeroSizeUpdatesIsAppliedToTheSymbol()
        {
            var aggregator = new FakeDataAggregator();
            using var manager = new LevelOneServiceManager(aggregator, (_, _) => true, (_, _) => true);
            manager.Subscribe(CreateConfig(Symbols.AAPL));
            manager.HandleQuote(Symbols.AAPL, Now, 100m, 1m, 101m, 1m);
            Assert.AreEqual(1, aggregator.Data.Count);

            manager.SetIgnoreZeroSizeUpdates(Symbols.AAPL, true);
            manager.HandleQuote(Symbols.AAPL, Now.AddSeconds(1), 100m, 0m, 102m, 0m);

            // zero sizes are ignored: the new prices are applied keeping the previous sizes
            Assert.AreEqual(2, aggregator.Data.Count);
            var tick = (Tick)aggregator.Data[1];
            Assert.AreEqual(100m, tick.BidPrice);
            Assert.AreEqual(1m, tick.BidSize);
            Assert.AreEqual(102m, tick.AskPrice);
            Assert.AreEqual(1m, tick.AskSize);

            manager.SetIgnoreZeroSizeUpdates(Symbols.AAPL, false);
            manager.HandleQuote(Symbols.AAPL, Now.AddSeconds(2), 100m, 0m, 102m, 0m);

            // zero sizes are now applied
            Assert.AreEqual(3, aggregator.Data.Count);
            tick = (Tick)aggregator.Data[2];
            Assert.AreEqual(0m, tick.BidSize);
            Assert.AreEqual(0m, tick.AskSize);
        }

        [Test]
        public void MultipleSubscriptionsToSameSymbolOnlySubscribeOnce()
        {
            var aggregator = new FakeDataAggregator();
            var subscribeCalls = 0;
            var unsubscribeCalls = 0;
            using var manager = new LevelOneServiceManager(aggregator,
                (_, _) => { subscribeCalls++; return true; },
                (_, _) => { unsubscribeCalls++; return true; });
            var config = CreateConfig(Symbols.AAPL);

            manager.Subscribe(config);
            manager.Subscribe(config);
            manager.Unsubscribe(config);

            Assert.AreEqual(1, subscribeCalls);
            Assert.AreEqual(0, unsubscribeCalls);
            Assert.AreEqual(1, manager.Count);

            manager.Unsubscribe(config);
            Assert.AreEqual(1, unsubscribeCalls);
            Assert.IsTrue(manager.IsEmpty);
        }

        private static SubscriptionDataConfig CreateConfig(Symbol symbol)
        {
            return new SubscriptionDataConfig(typeof(Tick), symbol, Resolution.Tick, TimeZones.NewYork, TimeZones.NewYork,
                false, false, false, false, TickType.Quote);
        }

        private class FakeDataAggregator : IDataAggregator
        {
            public List<BaseData> Data { get; } = new();

            public void Initialize(DataAggregatorInitializeParameters parameters)
            {
            }

            public IEnumerator<BaseData> Add(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
            {
                return Enumerable.Empty<BaseData>().GetEnumerator();
            }

            public bool Remove(SubscriptionDataConfig dataConfig)
            {
                return true;
            }

            public void Update(BaseData input)
            {
                Data.Add(input);
            }

            public void Dispose()
            {
            }
        }
    }
}
