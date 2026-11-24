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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Util;

namespace QuantConnect.Tests.Brokerages
{
    [TestFixture]
    public class BrokerageMultiWebSocketSubscriptionManagerTests
    {
        private List<FakeWebSocket> _webSockets;
        private ConcurrentQueue<(IWebSocket WebSocket, Symbol Symbol)> _subscribed;
        private ConcurrentQueue<(IWebSocket WebSocket, Symbol Symbol)> _unsubscribed;
        private List<WebSocketMessage> _messages;
        private RateGate _rateGate;

        [SetUp]
        public void SetUp()
        {
            _webSockets = new List<FakeWebSocket>();
            _subscribed = new ConcurrentQueue<(IWebSocket, Symbol)>();
            _unsubscribed = new ConcurrentQueue<(IWebSocket, Symbol)>();
            _messages = new List<WebSocketMessage>();
            _rateGate = new RateGate(1000, TimeSpan.FromSeconds(1));
        }

        [TearDown]
        public void TearDown()
        {
            _rateGate.Dispose();
        }

        [Test]
        public void WithoutWeightingCreatesWebSocketsOnDemand()
        {
            using var manager = CreateManager(maximumSymbolsPerWebSocket: 2, maximumWebSocketConnections: 0);
            Assert.AreEqual(0, _webSockets.Count);

            manager.Subscribe(CreateConfig(Symbols.BTCUSD));
            manager.Subscribe(CreateConfig(Symbols.ETHUSD));

            Assert.AreEqual(1, _webSockets.Count);
            var first = _webSockets[0];
            Assert.AreEqual(1, first.ConnectCount);
            Assert.IsTrue(first.IsOpen);

            // third symbol exceeds the per websocket limit: a new websocket is created and connected
            manager.Subscribe(CreateConfig(Symbols.LTCUSD));

            Assert.AreEqual(2, _webSockets.Count);
            Assert.AreEqual(1, _webSockets[1].ConnectCount);
            var subscribed = _subscribed.ToList();
            Assert.AreEqual(3, subscribed.Count);
            Assert.AreSame(first, subscribed[0].WebSocket);
            Assert.AreSame(first, subscribed[1].WebSocket);
            Assert.AreSame(_webSockets[1], subscribed[2].WebSocket);
            CollectionAssert.AreEquivalent(new[] { Symbols.BTCUSD, Symbols.ETHUSD, Symbols.LTCUSD }, manager.GetSubscribedSymbols());
        }

        [Test]
        public void WithWeightingCreatesAllWebSocketsUpFrontAndBalancesByWeight()
        {
            var weights = new Dictionary<Symbol, int>
            {
                { Symbols.BTCUSD, 100 },
                { Symbols.ETHUSD, 50 },
                { Symbols.LTCUSD, 10 },
                { Symbols.BTCEUR, 5 }
            };
            using var manager = CreateManager(maximumSymbolsPerWebSocket: 2, maximumWebSocketConnections: 2, symbolWeights: weights);

            Assert.AreEqual(2, _webSockets.Count);
            Assert.IsTrue(_webSockets.All(x => x.ConnectCount == 0 && !x.IsOpen));

            manager.Subscribe(CreateConfig(Symbols.BTCUSD));
            manager.Subscribe(CreateConfig(Symbols.ETHUSD));
            manager.Subscribe(CreateConfig(Symbols.LTCUSD));

            var subscribed = _subscribed.ToList();
            var btcSocket = subscribed[0].WebSocket;
            var ethSocket = subscribed[1].WebSocket;
            // the heavy BTC weight pushes the next symbol to the other websocket, then LTC goes to the lighter one (ETH)
            Assert.AreNotSame(btcSocket, ethSocket);
            Assert.AreSame(ethSocket, subscribed[2].WebSocket);
            Assert.IsTrue(_webSockets.All(x => x.ConnectCount == 1));

            manager.Subscribe(CreateConfig(Symbols.BTCEUR));
            Assert.AreSame(btcSocket, _subscribed.Last().WebSocket);

            // all websockets are full and no new connection can be created
            var exception = Assert.Throws<NotSupportedException>(() => manager.Subscribe(CreateConfig(Symbols.EURUSD)));
            StringAssert.Contains("Maximum symbol count reached", exception.Message);
        }

        [Test]
        public void UnsubscribeUsesTheWebSocketOwningTheSymbol()
        {
            using var manager = CreateManager(maximumSymbolsPerWebSocket: 1, maximumWebSocketConnections: 0);
            manager.Subscribe(CreateConfig(Symbols.BTCUSD));
            manager.Subscribe(CreateConfig(Symbols.ETHUSD));

            manager.Unsubscribe(CreateConfig(Symbols.ETHUSD));

            var unsubscribed = _unsubscribed.Single();
            Assert.AreEqual(Symbols.ETHUSD, unsubscribed.Symbol);
            Assert.AreSame(_webSockets[1], unsubscribed.WebSocket);
            CollectionAssert.AreEquivalent(new[] { Symbols.BTCUSD }, manager.GetSubscribedSymbols());

            // the freed slot is reused before creating another websocket
            manager.Subscribe(CreateConfig(Symbols.LTCUSD));
            Assert.AreEqual(2, _webSockets.Count);
            Assert.AreSame(_webSockets[1], _subscribed.Last().WebSocket);
        }

        [Test]
        public void SubscribeReportsFailureFromTheSubscribeFunction()
        {
            using var manager = CreateManager(maximumSymbolsPerWebSocket: 5, maximumWebSocketConnections: 0, subscribeResult: false);

            manager.Subscribe(CreateConfig(Symbols.BTCUSD));

            // the subscribe function failed: the symbol is not considered subscribed
            CollectionAssert.IsEmpty(manager.GetSubscribedSymbols());
            Assert.AreEqual(1, _subscribed.Count);
        }

        [Test]
        public void MessagesAreForwardedToTheHandler()
        {
            using var manager = CreateManager(maximumSymbolsPerWebSocket: 5, maximumWebSocketConnections: 0);
            manager.Subscribe(CreateConfig(Symbols.BTCUSD));
            var webSocket = _webSockets[0];

            webSocket.RaiseMessage(new WebSocketClientWrapper.TextMessage { Message = "hello" });

            Assert.AreEqual(1, _messages.Count);
            Assert.AreSame(webSocket, _messages[0].WebSocket);
            Assert.AreEqual("hello", ((WebSocketClientWrapper.TextMessage)_messages[0].Data).Message);
        }

        [Test]
        public void ConnectionFailureThrows()
        {
            using var manager = CreateManager(maximumSymbolsPerWebSocket: 5, maximumWebSocketConnections: 0,
                webSocketFactory: () => new FakeWebSocket(throwOnConnect: true));

            // a websocket failing to connect bubbles up the error
            Assert.Throws<InvalidOperationException>(() => manager.Subscribe(CreateConfig(Symbols.BTCUSD)));
            CollectionAssert.IsEmpty(manager.GetSubscribedSymbols());
        }

        [Test]
        public void DisposeClosesAllWebSocketsAndDetachesHandlers()
        {
            var manager = CreateManager(maximumSymbolsPerWebSocket: 1, maximumWebSocketConnections: 0);
            manager.Subscribe(CreateConfig(Symbols.BTCUSD));
            manager.Subscribe(CreateConfig(Symbols.ETHUSD));
            _webSockets[1].ThrowOnClose = true;

            manager.Dispose();

            Assert.AreEqual(1, _webSockets[0].CloseCount);
            Assert.AreEqual(1, _webSockets[1].CloseCount);
            Assert.IsFalse(_webSockets[0].IsOpen);
            _webSockets[0].RaiseMessage(new WebSocketClientWrapper.TextMessage { Message = "ignored" });
            Assert.AreEqual(0, _messages.Count);
        }

        [Test]
        public void ReconnectTimerRestartsOpenWebSockets()
        {
            using var manager = CreateManager(maximumSymbolsPerWebSocket: 5, maximumWebSocketConnections: 0,
                connectionDuration: TimeSpan.FromMilliseconds(50));
            manager.Subscribe(CreateConfig(Symbols.BTCUSD));
            var webSocket = _webSockets[0];

            Assert.IsTrue(SpinWait.SpinUntil(() => webSocket.ConnectCount >= 2, TimeSpan.FromSeconds(20)));
            Assert.GreaterOrEqual(webSocket.CloseCount, 1);
        }

        private BrokerageMultiWebSocketSubscriptionManager CreateManager(
            int maximumSymbolsPerWebSocket,
            int maximumWebSocketConnections,
            Dictionary<Symbol, int> symbolWeights = null,
            bool subscribeResult = true,
            Func<FakeWebSocket> webSocketFactory = null,
            TimeSpan? connectionDuration = null)
        {
            webSocketFactory ??= () => new FakeWebSocket();
            return new BrokerageMultiWebSocketSubscriptionManager(
                "wss://fake",
                maximumSymbolsPerWebSocket,
                maximumWebSocketConnections,
                symbolWeights,
                () =>
                {
                    var webSocket = webSocketFactory();
                    lock (_webSockets)
                    {
                        _webSockets.Add(webSocket);
                    }
                    return webSocket;
                },
                (webSocket, symbol) => { _subscribed.Enqueue((webSocket, symbol)); return subscribeResult; },
                (webSocket, symbol) => { _unsubscribed.Enqueue((webSocket, symbol)); return true; },
                message => _messages.Add(message),
                connectionDuration ?? TimeSpan.Zero,
                _rateGate);
        }

        private static SubscriptionDataConfig CreateConfig(Symbol symbol)
        {
            return new SubscriptionDataConfig(typeof(Tick), symbol, Resolution.Tick, TimeZones.Utc, TimeZones.Utc,
                false, false, false, false, TickType.Trade);
        }

        /// <summary>
        /// Re-implements the <see cref="IWebSocket"/> connection members so no real connection is made
        /// </summary>
        private class FakeWebSocket : WebSocketClientWrapper, IWebSocket
        {
            private readonly bool _throwOnConnect;
            private volatile bool _isOpen;
            private int _connectCount;
            private int _closeCount;

            public int ConnectCount => Volatile.Read(ref _connectCount);
            public int CloseCount => Volatile.Read(ref _closeCount);
            public bool ThrowOnClose { get; set; }

            public FakeWebSocket(bool throwOnConnect = false)
            {
                _throwOnConnect = throwOnConnect;
            }

            public new event EventHandler Open;

            public new bool IsOpen => _isOpen;

            public new void Connect()
            {
                if (_throwOnConnect)
                {
                    throw new InvalidOperationException("connection refused");
                }
                Interlocked.Increment(ref _connectCount);
                _isOpen = true;
                Open?.Invoke(this, EventArgs.Empty);
            }

            public new void Close()
            {
                Interlocked.Increment(ref _closeCount);
                _isOpen = false;
                if (ThrowOnClose)
                {
                    throw new InvalidOperationException("close failed");
                }
            }

            public void RaiseMessage(MessageData data)
            {
                OnMessage(new WebSocketMessage(this, data));
            }
        }
    }
}
