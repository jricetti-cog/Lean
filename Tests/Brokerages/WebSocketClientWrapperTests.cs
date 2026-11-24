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
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Util;

namespace QuantConnect.Tests.Brokerages
{
    [TestFixture]
    public class WebSocketClientWrapperTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private LoopbackWebSocketServer _server;

        [SetUp]
        public void SetUp()
        {
            _server = new LoopbackWebSocketServer();
        }

        [TearDown]
        public void TearDown()
        {
            _server.Dispose();
        }

        [Test]
        public void ConnectsSendsReceivesAndCloses()
        {
            var webSocket = new WebSocketClientWrapper();
            using var opened = new ManualResetEventSlim();
            using var closed = new ManualResetEventSlim();
            var messages = new BlockingCollection<WebSocketMessage>();
            webSocket.Open += (sender, _) => { Assert.AreSame(webSocket, sender); opened.Set(); };
            webSocket.Closed += (_, _) => closed.Set();
            webSocket.Message += (_, message) => messages.Add(message);

            Assert.IsFalse(webSocket.IsOpen);
            webSocket.Initialize(_server.Url, "my-session-token");
            webSocket.Connect();

            Assert.IsTrue(opened.Wait(Timeout));
            Assert.IsTrue(webSocket.IsOpen);
            Assert.IsTrue(_server.SessionTokens.TryTake(out var token, Timeout));
            Assert.AreEqual("my-session-token", token);

            webSocket.Send("ping");
            Assert.IsTrue(messages.TryTake(out var echo, Timeout));
            Assert.AreSame(webSocket, echo.WebSocket);
            var text = (WebSocketClientWrapper.TextMessage)echo.Data;
            Assert.AreEqual(WebSocketMessageType.Text, text.MessageType);
            Assert.AreEqual("echo:ping", text.Message);

            webSocket.Send("binary");
            Assert.IsTrue(messages.TryTake(out var binaryMessage, Timeout));
            var binary = (WebSocketClientWrapper.BinaryMessage)binaryMessage.Data;
            Assert.AreEqual(WebSocketMessageType.Binary, binary.MessageType);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, binary.Data);
            Assert.AreEqual(3, binary.Count);

            webSocket.Close();

            Assert.IsTrue(closed.IsSet);
            Assert.IsFalse(webSocket.IsOpen);
        }

        [Test]
        public void ReceivesMessagesLargerThanTheReceiveBuffer()
        {
            var webSocket = new WebSocketClientWrapper();
            var messages = new BlockingCollection<WebSocketMessage>();
            webSocket.Message += (_, message) => messages.Add(message);
            webSocket.Initialize(_server.Url);
            webSocket.Connect();
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => webSocket.IsOpen, Timeout));
                Assert.IsTrue(_server.SessionTokens.TryTake(out var token, Timeout));
                Assert.IsNull(token);

                webSocket.Send("big");

                Assert.IsTrue(messages.TryTake(out var message, Timeout));
                var text = ((WebSocketClientWrapper.TextMessage)message.Data).Message;
                Assert.AreEqual(LoopbackWebSocketServer.BigMessageLength, text.Length);
                Assert.IsTrue(text.All(c => c == 'x'));
            }
            finally
            {
                webSocket.Close();
            }
        }

        [Test]
        public void ReconnectsWhenTheServerClosesTheConnection()
        {
            var webSocket = new WebSocketClientWrapper();
            var openCount = 0;
            webSocket.Open += (_, _) => Interlocked.Increment(ref openCount);
            webSocket.Initialize(_server.Url);
            webSocket.Connect();
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref openCount) == 1 && webSocket.IsOpen, Timeout));

                webSocket.Send("close");

                // the server closed the socket: the wrapper connects again on its own
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref openCount) == 2, Timeout));
                Assert.IsTrue(SpinWait.SpinUntil(() => _server.ConnectionCount == 2, Timeout));
            }
            finally
            {
                webSocket.Close();
            }
        }

        [Test]
        public void RaisesErrorWhenTheConnectionFails()
        {
            var url = _server.Url;
            _server.Dispose();

            var webSocket = new WebSocketClientWrapper();
            var errors = new BlockingCollection<QuantConnect.Brokerages.WebSocketError>();
            var openCount = 0;
            webSocket.Error += (_, error) => errors.Add(error);
            webSocket.Open += (_, _) => Interlocked.Increment(ref openCount);
            webSocket.Initialize(url);
            webSocket.Connect();
            try
            {
                Assert.IsTrue(errors.TryTake(out var error, Timeout));
                Assert.IsNotNull(error.Exception);
                Assert.IsFalse(string.IsNullOrEmpty(error.Message));
                Assert.IsFalse(webSocket.IsOpen);
                Assert.AreEqual(0, openCount);
            }
            finally
            {
                webSocket.Close();
            }
        }

        [Test]
        public void ConnectWhileConnectedDoesNotStartAnotherConnection()
        {
            var webSocket = new WebSocketClientWrapper();
            webSocket.Initialize(_server.Url);
            webSocket.Connect();
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => webSocket.IsOpen, Timeout));

                webSocket.Connect();

                Assert.IsTrue(webSocket.IsOpen);
                Assert.AreEqual(1, _server.ConnectionCount);
            }
            finally
            {
                webSocket.Close();
            }
        }

        [Test]
        public void CloseBeforeConnectDoesNotRaiseClosed()
        {
            var webSocket = new WebSocketClientWrapper();
            var closed = false;
            webSocket.Closed += (_, _) => closed = true;
            webSocket.Initialize(_server.Url);

            webSocket.Close();

            Assert.IsFalse(closed);
            Assert.IsFalse(webSocket.IsOpen);
        }

        [Test]
        public void MultiWebSocketSubscriptionManagerResubscribesAfterReconnection()
        {
            var subscribed = new BlockingCollection<(IWebSocket WebSocket, Symbol Symbol)>();
            var messages = new BlockingCollection<WebSocketMessage>();
            WebSocketClientWrapper webSocket = null;
            using var rateGate = new RateGate(100, TimeSpan.FromSeconds(1));
            var manager = new BrokerageMultiWebSocketSubscriptionManager(
                _server.Url,
                10,
                0,
                null,
                () => webSocket = new WebSocketClientWrapper(),
                (ws, symbol) => { subscribed.Add((ws, symbol)); ws.Send($"subscribe:{symbol.Value}"); return true; },
                (_, _) => true,
                message => messages.Add(message),
                TimeSpan.Zero,
                rateGate);
            try
            {
                manager.Subscribe(new SubscriptionDataConfig(typeof(Tick), Symbols.BTCUSD, Resolution.Tick, TimeZones.Utc,
                    TimeZones.Utc, false, false, false, false, TickType.Trade));

                Assert.IsTrue(subscribed.TryTake(out var first, Timeout));
                Assert.AreSame(webSocket, first.WebSocket);
                Assert.IsTrue(messages.TryTake(out var echo, Timeout));
                Assert.AreEqual("echo:subscribe:BTCUSD", ((WebSocketClientWrapper.TextMessage)echo.Data).Message);

                // the server drops the connection, on reconnection the manager resubscribes the symbols of that websocket
                webSocket.Send("close");

                Assert.IsTrue(subscribed.TryTake(out var second, Timeout));
                Assert.AreEqual(Symbols.BTCUSD, second.Symbol);
                Assert.AreSame(webSocket, second.WebSocket);
                Assert.IsTrue(messages.TryTake(out echo, Timeout));
                Assert.AreEqual("echo:subscribe:BTCUSD", ((WebSocketClientWrapper.TextMessage)echo.Data).Message);
            }
            finally
            {
                manager.Dispose();
            }
            Assert.IsFalse(webSocket.IsOpen);
        }

        /// <summary>
        /// Minimal websocket server listening on the loopback interface
        /// </summary>
        private class LoopbackWebSocketServer : IDisposable
        {
            public const int BigMessageLength = 20000;

            private readonly HttpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private int _connectionCount;
            private bool _disposed;

            public string Url { get; }
            public BlockingCollection<string> SessionTokens { get; } = new();
            public int ConnectionCount => Volatile.Read(ref _connectionCount);

            public LoopbackWebSocketServer()
            {
                var port = GetFreePort();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Url = $"ws://127.0.0.1:{port}/";
                Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }
                    _ = Task.Run(() => HandleClient(context));
                }
            }

            private async Task HandleClient(HttpListenerContext context)
            {
                try
                {
                    var webSocketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                    Interlocked.Increment(ref _connectionCount);
                    SessionTokens.Add(context.Request.Headers["x-session-token"]);
                    var webSocket = webSocketContext.WebSocket;
                    var buffer = new byte[1024];
                    while (webSocket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        switch (message)
                        {
                            case "close":
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                                return;
                            case "binary":
                                await Send(webSocket, new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary).ConfigureAwait(false);
                                break;
                            case "big":
                                await Send(webSocket, Encoding.UTF8.GetBytes(new string('x', BigMessageLength)), WebSocketMessageType.Text).ConfigureAwait(false);
                                break;
                            default:
                                await Send(webSocket, Encoding.UTF8.GetBytes($"echo:{message}"), WebSocketMessageType.Text).ConfigureAwait(false);
                                break;
                        }
                    }
                }
                catch
                {
                    // client went away or the server is shutting down
                }
            }

            private Task Send(WebSocket webSocket, byte[] data, WebSocketMessageType type)
            {
                return webSocket.SendAsync(new ArraySegment<byte>(data), type, true, _cts.Token);
            }

            private static int GetFreePort()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _cts.Cancel();
                try
                {
                    _listener.Close();
                }
                catch
                {
                    // ignored
                }
                _cts.Dispose();
            }
        }
    }
}
