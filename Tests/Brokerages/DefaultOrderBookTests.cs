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

using System.Collections.Generic;
using NUnit.Framework;
using QuantConnect.Brokerages;

namespace QuantConnect.Tests.Brokerages
{
    [TestFixture]
    public class DefaultOrderBookTests
    {
        [Test]
        public void NewOrderBookIsEmpty()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);

            Assert.AreEqual(Symbols.BTCUSD, book.Symbol);
            Assert.AreEqual(0m, book.BestBidPrice);
            Assert.AreEqual(0m, book.BestBidSize);
            Assert.AreEqual(0m, book.BestAskPrice);
            Assert.AreEqual(0m, book.BestAskSize);
        }

        [Test]
        public void UpdateBidRowTracksHighestBid()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            var events = new List<BestBidAskUpdatedEventArgs>();
            book.BestBidAskUpdated += (_, e) => events.Add(e);

            book.UpdateBidRow(100m, 1m);
            book.UpdateBidRow(101m, 2m);
            // a lower bid does not change the best bid nor raise an event
            book.UpdateBidRow(99m, 5m);

            Assert.AreEqual(101m, book.BestBidPrice);
            Assert.AreEqual(2m, book.BestBidSize);
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(Symbols.BTCUSD, events[1].Symbol);
            Assert.AreEqual(101m, events[1].BestBidPrice);
            Assert.AreEqual(2m, events[1].BestBidSize);
            Assert.AreEqual(0m, events[1].BestAskPrice);
            Assert.AreEqual(0m, events[1].BestAskSize);
        }

        [Test]
        public void UpdateAskRowTracksLowestAsk()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            var events = new List<BestBidAskUpdatedEventArgs>();
            book.BestBidAskUpdated += (_, e) => events.Add(e);

            book.UpdateAskRow(105m, 1m);
            book.UpdateAskRow(103m, 3m);
            // a higher ask does not change the best ask
            book.UpdateAskRow(110m, 7m);

            Assert.AreEqual(103m, book.BestAskPrice);
            Assert.AreEqual(3m, book.BestAskSize);
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(103m, events[1].BestAskPrice);
            Assert.AreEqual(3m, events[1].BestAskSize);
        }

        [Test]
        public void UpdatingBestBidSizeAtSamePriceRaisesEvent()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            var count = 0;
            book.BestBidAskUpdated += (_, _) => count++;

            book.UpdateBidRow(100m, 1m);
            book.UpdateBidRow(100m, 4m);
            book.UpdateAskRow(101m, 1m);
            book.UpdateAskRow(101m, 6m);

            Assert.AreEqual(4, count);
            Assert.AreEqual(4m, book.BestBidSize);
            Assert.AreEqual(6m, book.BestAskSize);
        }

        [Test]
        public void RemovingBestBidPromotesNextHighestBid()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            book.UpdateBidRow(98m, 1m);
            book.UpdateBidRow(99m, 2m);
            book.UpdateBidRow(100m, 3m);
            BestBidAskUpdatedEventArgs last = null;
            book.BestBidAskUpdated += (_, e) => last = e;

            book.RemoveBidRow(100m);

            Assert.AreEqual(99m, book.BestBidPrice);
            Assert.AreEqual(2m, book.BestBidSize);
            Assert.IsNotNull(last);
            Assert.AreEqual(99m, last.BestBidPrice);
        }

        [Test]
        public void RemovingNonBestBidDoesNotRaiseEvent()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            book.UpdateBidRow(98m, 1m);
            book.UpdateBidRow(100m, 3m);
            var raised = false;
            book.BestBidAskUpdated += (_, _) => raised = true;

            book.RemoveBidRow(98m);

            Assert.IsFalse(raised);
            Assert.AreEqual(100m, book.BestBidPrice);
            Assert.AreEqual(3m, book.BestBidSize);
        }

        [Test]
        public void RemovingBestAskPromotesNextLowestAsk()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            book.UpdateAskRow(103m, 3m);
            book.UpdateAskRow(102m, 2m);
            book.UpdateAskRow(101m, 1m);
            BestBidAskUpdatedEventArgs last = null;
            book.BestBidAskUpdated += (_, e) => last = e;

            book.RemoveAskRow(101m);

            Assert.AreEqual(102m, book.BestAskPrice);
            Assert.AreEqual(2m, book.BestAskSize);
            Assert.AreEqual(102m, last.BestAskPrice);
        }

        [Test]
        public void RemovingLastLevelResetsBestPricesToZero()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            book.UpdateBidRow(100m, 1m);
            book.UpdateAskRow(101m, 1m);

            book.RemoveBidRow(100m);
            book.RemoveAskRow(101m);

            Assert.AreEqual(0m, book.BestBidPrice);
            Assert.AreEqual(0m, book.BestBidSize);
            Assert.AreEqual(0m, book.BestAskPrice);
            Assert.AreEqual(0m, book.BestAskSize);
        }

        [Test]
        public void RemovePriceLevelRemovesFromTheMatchingSide()
        {
            var book = new DefaultOrderBook(Symbols.BTCUSD);
            book.UpdateBidRow(99m, 1m);
            book.UpdateBidRow(100m, 2m);
            book.UpdateAskRow(101m, 3m);
            book.UpdateAskRow(102m, 4m);

            book.RemovePriceLevel(101m);
            Assert.AreEqual(102m, book.BestAskPrice);
            Assert.AreEqual(4m, book.BestAskSize);
            Assert.AreEqual(100m, book.BestBidPrice);

            book.RemovePriceLevel(100m);
            Assert.AreEqual(99m, book.BestBidPrice);
            Assert.AreEqual(1m, book.BestBidSize);

            // unknown levels are ignored
            book.RemovePriceLevel(500m);
            Assert.AreEqual(99m, book.BestBidPrice);
            Assert.AreEqual(102m, book.BestAskPrice);
        }

        [Test]
        public void ClearResetsTheBook()
        {
            var book = new TestOrderBook(Symbols.BTCUSD);
            book.UpdateBidRow(100m, 1m);
            book.UpdateAskRow(101m, 1m);
            Assert.AreEqual(1, book.BidCount);
            Assert.AreEqual(1, book.AskCount);

            book.Clear();

            Assert.AreEqual(0m, book.BestBidPrice);
            Assert.AreEqual(0m, book.BestBidSize);
            Assert.AreEqual(0m, book.BestAskPrice);
            Assert.AreEqual(0m, book.BestAskSize);
            Assert.AreEqual(0, book.BidCount);
            Assert.AreEqual(0, book.AskCount);

            // after clearing, any bid becomes the best again
            book.UpdateBidRow(50m, 1m);
            Assert.AreEqual(50m, book.BestBidPrice);
        }

        [Test]
        public void BestBidAskUpdatedEventArgsExposesValues()
        {
            var args = new BestBidAskUpdatedEventArgs(Symbols.SPY, 1m, 2m, 3m, 4m);

            Assert.AreEqual(Symbols.SPY, args.Symbol);
            Assert.AreEqual(1m, args.BestBidPrice);
            Assert.AreEqual(2m, args.BestBidSize);
            Assert.AreEqual(3m, args.BestAskPrice);
            Assert.AreEqual(4m, args.BestAskSize);
        }

        private class TestOrderBook : DefaultOrderBook
        {
            public int BidCount => Bids.Count;
            public int AskCount => Asks.Count;

            public TestOrderBook(Symbol symbol) : base(symbol)
            {
            }
        }
    }
}
