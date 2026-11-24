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
using System.Net.Http;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;
using RestSharp;

namespace QuantConnect.Tests.Brokerages
{
    [TestFixture]
    public class BaseWebsocketsBrokerageTests
    {
        [Test]
        public void ClientsThrowBeforeInitialization()
        {
            using var brokerage = new TestWebsocketsBrokerage();

            Assert.IsFalse(brokerage.Initialized);
            Assert.Throws<InvalidOperationException>(() => _ = brokerage.GetHttpClient());
            Assert.Throws<InvalidOperationException>(() => _ = brokerage.GetRestClient());
        }

        [Test]
        public void InitializeSetsUpTheWebSocketOnlyOnce()
        {
            using var brokerage = new TestWebsocketsBrokerage();
            var webSocket = new FakeWebSocket();
            using var httpClient = new HttpClient();

            brokerage.InitializeWithHttpClient("wss://fake", webSocket, httpClient, "key", "secret");

            Assert.IsTrue(brokerage.Initialized);
            Assert.AreEqual("wss://fake", webSocket.Url);
            Assert.AreSame(webSocket, brokerage.GetWebSocket());
            Assert.AreSame(httpClient, brokerage.GetHttpClient());
            Assert.AreEqual("key", brokerage.GetApiKey());
            Assert.AreEqual("secret", brokerage.GetApiSecret());
            Assert.IsNotNull(brokerage.CachedOrderIDs);
            Assert.AreEqual(Newtonsoft.Json.FloatParseHandling.Decimal, brokerage.GetJsonSettings().FloatParseHandling);

            // a second initialization is ignored
            using var otherClient = new HttpClient();
            brokerage.InitializeWithHttpClient("wss://other", new FakeWebSocket(), otherClient, "key2", "secret2");

            Assert.AreSame(webSocket, brokerage.GetWebSocket());
            Assert.AreSame(httpClient, brokerage.GetHttpClient());
            Assert.AreEqual("key", brokerage.GetApiKey());
        }

        [Test]
        public void ObsoleteInitializeSetsTheRestClient()
        {
            using var brokerage = new TestWebsocketsBrokerage();
            var restClient = new RestClient("http://localhost");

            brokerage.InitializeWithRestClient("wss://fake", new FakeWebSocket(), restClient, "key", "secret");

            Assert.AreSame(restClient, brokerage.GetRestClient());
            Assert.Throws<InvalidOperationException>(() => _ = brokerage.GetHttpClient());
        }

        [Test]
        public void ConnectOpensTheWebSocketAndSubscribes()
        {
            using var brokerage = new TestWebsocketsBrokerage();
            var webSocket = new FakeWebSocket();
            using var httpClient = new HttpClient();
            brokerage.InitializeWithHttpClient("wss://fake", webSocket, httpClient, "key", "secret");
            var subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            subscriptionManager.SubscribeImpl = (_, _) => true;
            subscriptionManager.Subscribe(new SubscriptionDataConfig(typeof(Tick), Symbols.BTCUSD, Resolution.Tick, TimeZones.Utc,
                TimeZones.Utc, false, false, false, false, TickType.Trade));
            brokerage.SetSubscriptionManager(subscriptionManager);

            Assert.IsFalse(brokerage.IsConnected);
            brokerage.Connect();

            Assert.IsTrue(brokerage.IsConnected);
            Assert.AreEqual(1, webSocket.ConnectCount);
            CollectionAssert.AreEqual(new[] { Symbols.BTCUSD }, brokerage.Subscriptions.Single());

            // already connected: nothing happens
            brokerage.Connect();
            Assert.AreEqual(1, webSocket.ConnectCount);
            Assert.AreEqual(1, brokerage.Subscriptions.Count);
        }

        [Test]
        public void WithoutSubscriptionManagerSubscribesToNothing()
        {
            using var brokerage = new TestWebsocketsBrokerage();
            var webSocket = new FakeWebSocket();
            using var httpClient = new HttpClient();
            brokerage.InitializeWithHttpClient("wss://fake", webSocket, httpClient, "key", "secret");

            brokerage.Connect();

            CollectionAssert.IsEmpty(brokerage.Subscriptions.Single());
        }

        [Test]
        public void WebSocketMessagesAreRoutedToTheBrokerage()
        {
            using var brokerage = new TestWebsocketsBrokerage();
            var webSocket = new FakeWebSocket();
            using var httpClient = new HttpClient();
            brokerage.InitializeWithHttpClient("wss://fake", webSocket, httpClient, "key", "secret");

            var message = new WebSocketMessage(webSocket, new WebSocketClientWrapper.TextMessage { Message = "data" });
            webSocket.RaiseMessage(message);

            Assert.AreSame(message, brokerage.Messages.Single());
        }

        private class TestWebsocketsBrokerage : BaseWebsocketsBrokerage
        {
            public List<List<Symbol>> Subscriptions { get; } = new();
            public List<WebSocketMessage> Messages { get; } = new();

            public TestWebsocketsBrokerage() : base("TestWebsocketsBrokerage")
            {
            }

            public bool Initialized => IsInitialized;
            public IWebSocket GetWebSocket() => WebSocket;
            public HttpClient GetHttpClient() => HttpClient;
#pragma warning disable CS0618
            public IRestClient GetRestClient() => RestClient;
            public void InitializeWithRestClient(string url, IWebSocket webSocket, IRestClient restClient, string apiKey, string apiSecret)
                => Initialize(url, webSocket, restClient, apiKey, apiSecret);
#pragma warning restore CS0618
            public void InitializeWithHttpClient(string url, IWebSocket webSocket, HttpClient httpClient, string apiKey, string apiSecret)
                => Initialize(url, webSocket, httpClient, apiKey, apiSecret);
            public string GetApiKey() => ApiKey;
            public string GetApiSecret() => ApiSecret;
            public Newtonsoft.Json.JsonSerializerSettings GetJsonSettings() => JsonSettings;
            public void SetSubscriptionManager(DataQueueHandlerSubscriptionManager manager) => SubscriptionManager = manager;

            public override bool IsConnected => WebSocket?.IsOpen == true;

            protected override void OnMessage(object sender, WebSocketMessage e)
            {
                Messages.Add(e);
            }

            protected override bool Subscribe(IEnumerable<Symbol> symbols)
            {
                Subscriptions.Add(symbols.ToList());
                return true;
            }

            public override bool PlaceOrder(Order order) => false;
            public override bool UpdateOrder(Order order) => false;
            public override bool CancelOrder(Order order) => false;
            public override void Disconnect() { }
            public override List<Order> GetOpenOrders() => new();
            public override List<Holding> GetAccountHoldings() => new();
            public override List<CashAmount> GetCashBalance() => new();
        }

        private class FakeWebSocket : IWebSocket
        {
            public string Url { get; private set; }
            public int ConnectCount { get; private set; }
            public bool IsOpen { get; private set; }

            public event EventHandler<WebSocketMessage> Message;
            public event EventHandler<WebSocketError> Error;
            public event EventHandler Open;
            public event EventHandler<WebSocketCloseData> Closed;

            public void Initialize(string url, string sessionToken = null)
            {
                Url = url;
            }

            public void Send(string data)
            {
            }

            public void Connect()
            {
                ConnectCount++;
                IsOpen = true;
                Open?.Invoke(this, EventArgs.Empty);
            }

            public void Close()
            {
                IsOpen = false;
                Closed?.Invoke(this, new WebSocketCloseData(0, string.Empty, true));
                Error?.Invoke(this, null);
            }

            public void RaiseMessage(WebSocketMessage message)
            {
                Message?.Invoke(this, message);
            }
        }
    }
}
