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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Api;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Authentication;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Brokerages.Authentication
{
    [TestFixture]
    public class LeanOAuthTokenHandlerCoverageTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Test]
        public void ReturnsCachedTokenUntilItExpires()
        {
            using var platform = new FakePlatform("first");
            using var handler = new FastRetryTokenHandler(platform.ApiClient, TimeSpan.FromHours(1));

            var first = handler.GetAccessToken(CancellationToken.None);
            Assert.AreEqual("first", first.AccessToken);
            Assert.AreEqual(TokenType.Bearer, first.TokenType);

            platform.Token = "second";
            Assert.AreSame(first, handler.GetAccessToken(CancellationToken.None));
            Assert.AreEqual(1, platform.Requests);

            // the token expires before its lifetime by the configured offset
            handler.OffsetBeforeExpiration = TimeSpan.FromHours(2);
            handler.InvalidateAccessToken();
            Assert.AreEqual("second", handler.GetAccessToken(CancellationToken.None).AccessToken);
            platform.Token = "third";
            Assert.AreEqual("third", handler.GetAccessToken(CancellationToken.None).AccessToken);
            Assert.AreEqual(3, platform.Requests);
        }

        [Test]
        public void RetriesBeforeSucceeding()
        {
            using var platform = new FakePlatform("token") { FailuresBeforeSuccess = 2 };
            using var handler = new FastRetryTokenHandler(platform.ApiClient, TimeSpan.FromHours(1), maxRetryCount: 3);
            var failures = 0;
            handler.AuthenticationFailed += (_, _) => failures++;

            var token = handler.GetAccessToken(CancellationToken.None);

            Assert.AreEqual("token", token.AccessToken);
            Assert.AreEqual(3, platform.Requests);
            Assert.AreEqual(0, failures);
        }

        [Test]
        public void FailsAfterExhaustingRetriesThenRecovers()
        {
            using var platform = new FakePlatform("token") { FailuresBeforeSuccess = int.MaxValue, ErrorMessage = "invalid grant" };
            using var handler = new FastRetryTokenHandler(platform.ApiClient, TimeSpan.FromHours(1), maxRetryCount: 1);
            Exception failure = null;
            using var succeeded = new ManualResetEventSlim();
            handler.AuthenticationFailed += (_, ex) => failure = ex;
            handler.AuthenticationSucceeded += (sender, _) => { Assert.AreSame(handler, sender); succeeded.Set(); };

            var exception = Assert.Throws<InvalidOperationException>(() => handler.GetAccessToken(CancellationToken.None));

            StringAssert.Contains("invalid grant", exception.Message);
            Assert.AreSame(exception, failure);
            Assert.AreEqual(2, platform.Requests);

            // while recovering, requests fail fast without reaching the platform
            var failFast = Assert.Throws<InvalidOperationException>(() => handler.GetAccessToken(CancellationToken.None));
            StringAssert.Contains("waiting for it to recover", failFast.Message);

            // the platform comes back: the recovery task gets a token and notifies
            platform.FailuresBeforeSuccess = 0;
            Assert.IsTrue(succeeded.Wait(Timeout));
            Assert.AreEqual("token", handler.GetAccessToken(CancellationToken.None).AccessToken);
        }

        [Test]
        public void FailureWithoutErrorDetailsReportsEmpty()
        {
            using var platform = new FakePlatform("token") { FailuresBeforeSuccess = int.MaxValue };
            using var handler = new FastRetryTokenHandler(platform.ApiClient, TimeSpan.FromHours(1), maxRetryCount: 0);

            var exception = Assert.Throws<InvalidOperationException>(() => handler.GetAccessToken(CancellationToken.None));

            StringAssert.Contains("Details: empty", exception.Message);
            Assert.AreEqual(1, platform.Requests);
        }

        [Test]
        public void CancellationDuringRetryWaitThrows()
        {
            using var platform = new FakePlatform("token") { FailuresBeforeSuccess = int.MaxValue };
            using var handler = new FastRetryTokenHandler(platform.ApiClient, TimeSpan.FromHours(1), maxRetryCount: 3,
                retryInterval: TimeSpan.FromMinutes(10));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => handler.GetAccessToken(cancellation.Token));
            Assert.AreEqual(1, platform.Requests);
        }

        [Test]
        public void DisposeStopsTheRecovery()
        {
            using var platform = new FakePlatform("token") { FailuresBeforeSuccess = int.MaxValue };
            var handler = new FastRetryTokenHandler(platform.ApiClient, TimeSpan.FromHours(1), maxRetryCount: 0,
                recoveryInterval: TimeSpan.FromMinutes(10));
            var succeeded = false;
            handler.AuthenticationSucceeded += (_, _) => succeeded = true;
            Assert.Throws<InvalidOperationException>(() => handler.GetAccessToken(CancellationToken.None));

            handler.Dispose();
            platform.FailuresBeforeSuccess = 0;

            Assert.IsFalse(succeeded);
            Assert.AreEqual(1, platform.Requests);
        }

        [Test]
        public void BrokerageCreatesHandlerWiredToItsConnectionState()
        {
            using var platform = new FakePlatform("token");
            using var brokerage = new TestBrokerage();
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, e) =>
            {
                lock (messages)
                {
                    messages.Add(e);
                }
            };
            using var handler = brokerage.CreateHandler(platform.ApiClient);

            Assert.IsInstanceOf<LeanOAuthTokenHandler<LeanTokenCredentials>>(handler);
            var token = handler.GetAccessToken(CancellationToken.None);

            Assert.AreEqual("token", token.AccessToken);
            Assert.AreEqual(1, platform.Requests);
            lock (messages)
            {
                // the brokerage starts connected, so a successful authentication is not a state change
                CollectionAssert.IsEmpty(messages);
            }
        }

        private class FastRetryTokenHandler : LeanOAuthTokenHandler<LeanTokenCredentials>
        {
            public FastRetryTokenHandler(ApiConnection apiClient, TimeSpan tokenLifetime, int maxRetryCount = 5,
                TimeSpan? retryInterval = null, TimeSpan? recoveryInterval = null)
                : base(apiClient, new OAuthTokenRequest("TestBrokerage", "account"), tokenLifetime)
            {
                MaxRetryCount = maxRetryCount;
                RetryInterval = retryInterval ?? TimeSpan.FromMilliseconds(1);
                RecoveryInterval = recoveryInterval ?? TimeSpan.FromMilliseconds(10);
            }
        }

        private class TestBrokerage : Brokerage
        {
            public TestBrokerage() : base("TestBrokerage")
            {
            }

            public LeanOAuthTokenHandler<LeanTokenCredentials> CreateHandler(ApiConnection apiClient)
            {
                var handler = CreateOAuthTokenHandler<LeanTokenCredentials>(apiClient, new OAuthTokenRequest("TestBrokerage", "account"), TimeSpan.FromHours(1));
                return handler;
            }

            public override bool IsConnected => true;
            public override bool PlaceOrder(Order order) => false;
            public override bool UpdateOrder(Order order) => false;
            public override bool CancelOrder(Order order) => false;
            public override void Connect() { }
            public override void Disconnect() { }
            public override List<Order> GetOpenOrders() => new();
            public override List<Holding> GetAccountHoldings() => new();
            public override List<CashAmount> GetCashBalance() => new();
        }

        private sealed class FakePlatform : IDisposable
        {
            private readonly HttpListener _listener = new();
            private int _requests;
            private int _failuresBeforeSuccess;

            public volatile string Token;
            public volatile string ErrorMessage;

            public int FailuresBeforeSuccess
            {
                get => Volatile.Read(ref _failuresBeforeSuccess);
                set => Volatile.Write(ref _failuresBeforeSuccess, value);
            }

            public int Requests => Volatile.Read(ref _requests);

            public ApiConnection ApiClient { get; }

            public FakePlatform(string token)
            {
                Token = token;
                var url = $"http://localhost:{GetFreePort()}/";
                _listener.Prefixes.Add(url);
                _listener.Start();
                Task.Run(Serve);
                ApiClient = new ApiConnection(1, "token", url);
            }

            private async Task Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    if (context.Request.Url.AbsolutePath != "/live/auth0/refresh")
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        context.Response.Close();
                        continue;
                    }

                    Interlocked.Increment(ref _requests);
                    object response;
                    var failures = FailuresBeforeSuccess;
                    if (failures > 0)
                    {
                        if (failures != int.MaxValue)
                        {
                            Interlocked.Decrement(ref _failuresBeforeSuccess);
                        }
                        response = ErrorMessage == null
                            ? new { success = false }
                            : new { success = false, errors = new[] { ErrorMessage } };
                    }
                    else
                    {
                        response = new { success = true, tokenType = "Bearer", accessToken = Token };
                    }
                    var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(response));
                    context.Response.ContentType = "application/json";
                    try
                    {
                        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        // the client went away
                    }
                }
            }

            private static int GetFreePort()
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                return ((IPEndPoint)socket.LocalEndPoint).Port;
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
                ApiClient.Dispose();
            }
        }
    }
}
