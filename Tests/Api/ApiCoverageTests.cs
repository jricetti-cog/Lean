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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Api;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Util;

namespace QuantConnect.Tests.API
{
    /// <summary>
    /// Exercises <see cref="Api.Api"/> and <see cref="ApiConnection"/> against an in-process fake HTTP handler,
    /// no network access nor real credentials are used
    /// </summary>
    [TestFixture]
    public class ApiCoverageTests
    {
        private const string BaseUrl = "http://fake.api.test/api/v2/";
        private FakeHandler _handler;
        private TestApi _api;
        private Func<JsonSerializerSettings> _previousJsonDefaults;

        [SetUp]
        public void SetUp()
        {
            _previousJsonDefaults = JsonConvert.DefaultSettings;
            _handler = new FakeHandler();
            _api = new TestApi(_handler);
            _api.Initialize(123, "secret-token", "/data/folder");
        }

        [TearDown]
        public void TearDown()
        {
            _api.Dispose();
            JsonConvert.DefaultSettings = _previousJsonDefaults;
        }

        [Test]
        public void InitializeConfiguresOrderConverterAsDefault()
        {
            var settings = JsonConvert.DefaultSettings();
            Assert.IsTrue(settings.Converters.Any(c => c.GetType().Name == "OrderJsonConverter"));
        }

        [Test]
        public void ConnectedUsesAuthenticateEndpointAndSetsAuthHeaders()
        {
            _handler.Respond("authenticate", "{\"success\":true}");

            Assert.IsTrue(_api.Connected);

            var request = _handler.Requests.Single();
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual(BaseUrl + "authenticate", request.Uri);
            Assert.AreEqual("Basic", request.AuthScheme);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.AuthParameter));
            var timestamp = int.Parse(request.Timestamp);
            Assert.AreEqual($"123:{Api.Api.CreateSecureHash(timestamp, "secret-token")}", decoded);
        }

        [Test]
        public void ConnectedIsFalseWhenAuthenticationFails()
        {
            _handler.Respond("authenticate", "{\"success\":false,\"errors\":[\"bad token\"]}");
            Assert.IsFalse(_api.Connected);
        }

        [Test]
        public void AuthenticatorIsReusedBetweenRequests()
        {
            _handler.Respond("authenticate", "{\"success\":true}");
            Assert.IsTrue(_api.Connected);
            Assert.IsTrue(_api.Connected);
            Assert.AreEqual(2, _handler.Requests.Count);
            Assert.AreEqual(_handler.Requests[0].AuthParameter, _handler.Requests[1].AuthParameter);
            Assert.AreEqual(_handler.Requests[0].Timestamp, _handler.Requests[1].Timestamp);
        }

        [Test]
        public void CreateSecureHashIsDeterministicSha256()
        {
            var hash = Api.Api.CreateSecureHash(1000, "token");
            Assert.AreEqual(64, hash.Length);
            Assert.AreEqual(hash, Api.Api.CreateSecureHash(1000, "token"));
            Assert.AreNotEqual(hash, Api.Api.CreateSecureHash(1001, "token"));
        }

        [Test]
        public void TryRequestReturnsFalseOnNonJsonPayload()
        {
            _handler.Respond("projects/read", "<html>bad gateway</html>", HttpStatusCode.BadGateway);
            Assert.IsNull(_api.ReadProject(1));
        }

        [Test]
        public void TryRequestReturnsResultOnHttpErrorWithJsonBody()
        {
            _handler.Respond("projects/read", "{\"success\":false,\"errors\":[\"not found\"]}", HttpStatusCode.NotFound);
            var result = _api.ReadProject(1);
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            CollectionAssert.AreEqual(new[] { "not found" }, result.Errors);
        }

        [Test]
        public void TryRequestReturnsFalseWhenTheHandlerThrows()
        {
            _handler.Throw("projects/read", new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable));
            Assert.IsNull(_api.ReadProject(1));
        }

        [Test]
        public void TryRequestReturnsFalseWhenTheRequestIsCanceled()
        {
            _handler.Throw("projects/read", new TaskCanceledException("canceled"));
            Assert.IsNull(_api.ReadProject(1));
        }

        [Test]
        public void TryRequestReturnsFalseOnUnexpectedException()
        {
            _handler.Throw("projects/read", new InvalidOperationException("boom", new IOException("inner")));
            Assert.IsNull(_api.ReadProject(1));
        }

        [Test]
        public void TryRequestTrimsLeadingSlashFromRelativeUri()
        {
            _handler.Respond("authenticate", "{\"success\":true}");
            using var connection = _api.GetConnection();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/authenticate", UriKind.Relative));
            Assert.IsTrue(connection.TryRequest(request, out RestResponse response));
            Assert.IsTrue(response.Success);
            Assert.AreEqual(BaseUrl + "authenticate", _handler.Requests.Single().Uri);
        }

        [TestCase(0)]
        [TestCase(30)]
        public void SetClientWithHeadersAndTimeout(int timeout)
        {
            using var connection = new ApiConnection(1, "token", "http://localhost:1/base",
                new Dictionary<string, string> { { "X-Custom", "value" } }, timeout);
            var client = GetHttpClient(connection);
            Assert.AreEqual("http://localhost:1/base/", client.BaseAddress.ToString());
            Assert.IsTrue(client.DefaultRequestHeaders.Contains("X-Custom"));
            Assert.AreEqual(timeout > 0 ? TimeSpan.FromSeconds(timeout) : Timeout.InfiniteTimeSpan, client.Timeout);
#pragma warning disable CS0618
            Assert.IsNotNull(connection.Client);
#pragma warning restore CS0618
        }

        [Test]
        public void ProjectEndpointsPostExpectedPayloads()
        {
            _handler.RespondToAll("{\"success\":true}");

            Assert.IsTrue(_api.CreateProject("name", Language.Python).Success);
            Assert.IsTrue(_api.CreateProject("name", Language.CSharp, "org").Success);
            Assert.IsTrue(_api.ReadProject(7).Success);
            Assert.IsTrue(_api.ListProjects().Success);
            Assert.IsTrue(_api.ListProjects(5, 10).Success);
            Assert.IsTrue(_api.UpdateProject(7).Success);
            Assert.IsTrue(_api.UpdateProject(7, "n", "d").Success);
            Assert.IsTrue(_api.AddProjectFile(7, "main.py", "code").Success);
            Assert.IsTrue(_api.UpdateProjectFileName(7, "a.py", "b.py").Success);
            Assert.IsTrue(_api.UpdateProjectFileContent(7, "a.py", "content").Success);
            Assert.IsTrue(_api.ReadProjectFiles(7).Success);
            Assert.IsTrue(_api.ReadProjectNodes(7).Success);
            Assert.IsTrue(_api.UpdateProjectNodes(7, new[] { "node" }).Success);
            Assert.IsTrue(_api.ReadProjectFile(7, "a.py").Success);
            Assert.IsTrue(_api.ReadLeanVersions().Success);
            Assert.IsTrue(_api.DeleteProjectFile(7, "a.py").Success);
            Assert.IsTrue(_api.DeleteProject(7).Success);

            var paths = _handler.Requests.Select(r => r.Path).ToList();
            CollectionAssert.AreEqual(new[]
            {
                "projects/create", "projects/create", "projects/read", "projects/read", "projects/read", "projects/update",
                "projects/update", "files/create", "files/update", "files/update", "files/read", "projects/nodes/read",
                "projects/nodes/update", "files/read", "lean/versions/read", "files/delete", "projects/delete"
            }, paths);

            Assert.IsFalse(_handler.Requests[0].Json.ContainsKey("organizationId"));
            Assert.AreEqual("org", (string)_handler.Requests[1].Json["organizationId"]);
            Assert.IsFalse(_handler.Requests[3].Json.ContainsKey("end"));
            Assert.AreEqual(10, (int)_handler.Requests[4].Json["end"]);
            Assert.IsFalse(_handler.Requests[5].Json.ContainsKey("name"));
            Assert.AreEqual("d", (string)_handler.Requests[6].Json["description"]);
            Assert.AreEqual("b.py", (string)_handler.Requests[8].Json["newName"]);
            Assert.AreEqual("content", (string)_handler.Requests[9].Json["content"]);
            Assert.AreEqual("a.py", (string)_handler.Requests[13].Json["name"]);
            Assert.AreEqual(HttpMethod.Post, _handler.Requests[14].Method);
        }

        [Test]
        public void CompileAndBacktestEndpoints()
        {
            _handler.Respond("compile/create", "{\"success\":true,\"compileId\":\"c-1\",\"projectId\":7}");
            _handler.Respond("compile/read", "{\"success\":true,\"compileId\":\"c-1\"}");
            _handler.Respond("backtests/create", "{\"success\":true,\"debugging\":true,\"backtest\":{\"backtestId\":\"b-1\"}}");
            _handler.RespondToAll("{\"success\":true}");

            Assert.AreEqual("c-1", _api.CreateCompile(7).CompileId);
            Assert.AreEqual("c-1", _api.ReadCompile(7, "c-1").CompileId);

            var backtest = _api.CreateBacktest(7, "c-1", "name");
            Assert.AreEqual("b-1", backtest.BacktestId);
            Assert.IsTrue(backtest.Success);
            Assert.IsTrue(backtest.Debugging);

            _api.CreateBacktest(7, "c-1", "name", new Dictionary<string, string> { { "p", "1" } });
            Assert.AreEqual("1", (string)_handler.Requests.Last().Json["parameters"]["p"]);
            Assert.IsFalse(_handler.Requests[2].Json.ContainsKey("parameters"));

            Assert.IsTrue(_api.UpdateBacktest(7, "b-1").Success);
            Assert.IsFalse(_handler.Requests.Last().Json.ContainsKey("name"));
            Assert.IsTrue(_api.UpdateBacktest(7, "b-1", "new", "note").Success);
            Assert.AreEqual("new", (string)_handler.Requests.Last().Json["name"]);
            Assert.IsTrue(_api.ListBacktests(7).Success);
            Assert.IsTrue(_api.DeleteBacktest(7, "b-1").Success);
            Assert.IsTrue(_api.UpdateBacktestTags(7, "b-1", new[] { "t" }).Success);
            Assert.IsTrue(_api.ReadBacktestInsights(7, "b-1").Success);
            Assert.AreEqual(100, (int)_handler.Requests.Last().Json["end"]);
            Assert.IsTrue(_api.ReadBacktestLog(7, "b-1", 10, 20, "Error").Success);
            Assert.AreEqual(20, (int)_handler.Requests.Last().Json["end"]);
            Assert.AreEqual("Error", (string)_handler.Requests.Last().Json["query"]);
        }

        [Test]
        public void SendNotificationIsNotSupported()
        {
            Assert.Throws<NotImplementedException>(() => _api.SendNotification(null, 1));
        }

        [Test]
        public void ReadBacktestReturnsNullWhenRequestFails()
        {
            _handler.Respond("backtests/read", "not json");
            Assert.IsNull(_api.ReadBacktest(7, "b-1"));
        }

        [Test]
        public void ReadBacktestReturnsPlaceholderOnFailure()
        {
            _handler.Respond("backtests/read", "{\"success\":false,\"errors\":[\"missing\"]}");
            var backtest = _api.ReadBacktest(7, "b-1");
            Assert.AreEqual("b-1", backtest.BacktestId);
            Assert.IsFalse(backtest.Success);
            CollectionAssert.AreEqual(new[] { "missing" }, backtest.Errors);
        }

        [Test]
        public void ReadBacktestFetchesEmptyChartsWhenCompleted()
        {
            _handler.Respond("backtests/read", "{\"success\":true,\"backtest\":{\"backtestId\":\"b-1\",\"completed\":true,\"charts\":{" +
                "\"Empty\":{\"name\":\"Empty\",\"series\":{}}}}}");
            _handler.Respond("backtests/chart/read", "{\"success\":true,\"chart\":{\"name\":\"Fetched\",\"series\":{}}}");

            var backtest = _api.ReadBacktest(7, "b-1");

            Assert.IsTrue(backtest.Success);
            Assert.AreEqual("Fetched", backtest.Charts["Empty"].Name);
            var chartRequest = _handler.Requests.Single(r => r.Path == "backtests/chart/read");
            Assert.AreEqual("Empty", (string)chartRequest.Json["name"]);
            Assert.AreEqual(100, (int)chartRequest.Json["count"]);
        }

        [Test]
        public void ReadBacktestSkipsChartsWhenNotRequested()
        {
            _handler.Respond("backtests/read", "{\"success\":true,\"backtest\":{\"backtestId\":\"b-1\",\"completed\":true,\"charts\":{" +
                "\"Empty\":{\"name\":\"Empty\",\"series\":{}}}}}");
            var backtest = _api.ReadBacktest(7, "b-1", getCharts: false);
            Assert.IsTrue(backtest.Success);
            Assert.IsFalse(_handler.Requests.Any(r => r.Path == "backtests/chart/read"));
        }

        [Test]
        public void ReadChartsReturnChartWhenAvailable()
        {
            _handler.Respond("backtests/chart/read", "{\"success\":true,\"chart\":{\"name\":\"Strategy Equity\"}}");
            _handler.Respond("live/chart/read", "{\"success\":true,\"chart\":{\"name\":\"Live Equity\"}}");

            Assert.AreEqual("Strategy Equity", _api.ReadBacktestChart(7, "Strategy Equity", 0, 10, 5, "b-1").Chart.Name);
            Assert.AreEqual("Live Equity", _api.ReadLiveChart(7, "Live Equity", 0, 10, 5).Chart.Name);
            Assert.AreEqual("b-1", (string)_handler.Requests[0].Json["backtestId"]);
            Assert.AreEqual(5, (int)_handler.Requests[1].Json["count"]);
        }

        [Test]
        public void OrdersEndpointsReturnResultOrThrow()
        {
            _handler.Respond("backtests/orders/read", "{\"success\":true,\"orders\":[],\"length\":0}");
            Assert.IsTrue(_api.ReadBacktestOrders(7, "b-1").Success);
            Assert.AreEqual(100, (int)_handler.Requests.Last().Json["end"]);

            _handler.Respond("live/orders/read", "{\"success\":false,\"errors\":[\"denied\"]}");
            var error = Assert.Throws<WebException>(() => _api.ReadLiveOrders(7, "L-1"));
            StringAssert.Contains("ReadLiveOrders api request failed. Errors: ['denied']", error.Message);
            Assert.AreEqual("L-1", (string)_handler.Requests.Last().Json["algorithmId"]);

            _handler.Respond("live/orders/read", "{\"success\":false}");
            error = Assert.Throws<WebException>(() => _api.ReadLiveOrders(7));
            Assert.AreEqual("ReadLiveOrders api request failed", error.Message);
            Assert.IsFalse(_handler.Requests.Last().Json.ContainsKey("algorithmId"));
        }

        [TestCase(0, 101)]
        [TestCase(10, 200)]
        public void PagingWindowsWiderThanMaximumThrow(int start, int end)
        {
            Assert.Throws<ArgumentException>(() => _api.ReadBacktestOrders(7, "b-1", start, end));
            Assert.Throws<ArgumentException>(() => _api.ReadLiveOrders(7, null, start, end));
            Assert.Throws<ArgumentException>(() => _api.ReadBacktestInsights(7, "b-1", start, end));
            Assert.Throws<ArgumentException>(() => _api.ReadLiveInsights(7, "L-1", start, end));
            Assert.Throws<ArgumentException>(() => _api.ReadBacktestLog(7, "b-1", start, end + 200));
            Assert.Throws<ArgumentException>(() => _api.ReadLiveLogs(7, "L-1", start, end + 200));
            Assert.IsEmpty(_handler.Requests);
        }

        [Test]
        public void LiveEndpointsPostExpectedPayloads()
        {
            _handler.RespondToAll("{\"success\":true}");

            Assert.IsTrue(_api.CreateLiveAlgorithm(7, "c-1", "node", new Dictionary<string, object> { { "id", "PaperBrokerage" } }).Success);
            Assert.AreEqual("live/create", _handler.Requests.Last().Path);
            Assert.IsTrue(_api.ListLiveAlgorithms().Success);
            Assert.IsNull(_handler.Requests.Last().Body);
            Assert.IsTrue(_api.ListLiveAlgorithms(AlgorithmStatus.Running, 7).Success);
            Assert.AreEqual("Running", (string)_handler.Requests.Last().Json["status"]);
            Assert.AreEqual(7, (int)_handler.Requests.Last().Json["projectId"]);
            Assert.IsTrue(_api.ReadLiveAlgorithm(7).Success);
            Assert.IsTrue(_api.ReadLivePortfolio(7).Success);
            Assert.IsTrue(_api.LiquidateLiveAlgorithm(7).Success);
            Assert.IsTrue(_api.StopLiveAlgorithm(7).Success);
            Assert.IsTrue(_api.CreateLiveCommand(7, new { type = "cmd" }).Success);
            Assert.IsTrue(_api.BroadcastLiveCommand("org", 3, new { name = "cmd" }).Success);
            Assert.AreEqual(3, (int)_handler.Requests.Last().Json["excludeProjectId"]);
            Assert.IsTrue(_api.ReadLiveLogs(7, "L-1", deploymentLogs: true).Success);
            Assert.AreEqual(200, (int)_handler.Requests.Last().Json["endLine"]);
            Assert.AreEqual("json", (string)_handler.Requests.Last().Json["format"]);
            Assert.IsTrue(_api.ReadLiveInsights(7, "L-1").Success);
            Assert.AreEqual("L-1", (string)_handler.Requests.Last().Json["algorithmId"]);
#pragma warning disable CS0618
            Assert.IsTrue(_api.ReadLiveInsights(7, 0, 50).Success);
#pragma warning restore CS0618
            Assert.IsFalse(_handler.Requests.Last().Json.ContainsKey("algorithmId"));
            Assert.AreEqual(50, (int)_handler.Requests.Last().Json["end"]);

            CollectionAssert.AreEqual(new[]
            {
                "live/create", "live/list", "live/list", "live/read", "live/portfolio/read", "live/update/liquidate",
                "live/update/stop", "live/commands/create", "live/commands/broadcast", "live/logs/read", "live/insights/read",
                "live/insights/read"
            }, _handler.Requests.Select(r => r.Path));
        }

        [TestCase(AlgorithmStatus.Completed)]
        [TestCase(AlgorithmStatus.Deleted)]
        [TestCase(AlgorithmStatus.InQueue)]
        public void ListLiveAlgorithmsRejectsUnsupportedStatus(AlgorithmStatus status)
        {
            Assert.Throws<ArgumentException>(() => _api.ListLiveAlgorithms(status));
        }

        [Test]
        public void DataEndpoints()
        {
            _handler.Respond("data/read", "{\"success\":true,\"link\":\"http://fake.download.test/file.zip\"}");
            _handler.Respond("data/list", "{\"success\":true,\"objects\":[\"a\",\"b\"]}");
            _handler.Respond("data/prices", "{\"success\":true}");

            Assert.AreEqual("http://fake.download.test/file.zip", _api.ReadDataLink(Globals.DataFolder.TrimEnd('/', '\\') + "/equity/usa/daily/spy.zip", "org").Link);
            Assert.AreEqual("equity/usa/daily/spy.zip", (string)_handler.Requests.Last().Json["filePath"]);
            Assert.AreEqual("link", (string)_handler.Requests.Last().Json["format"]);

            CollectionAssert.AreEqual(new[] { "a", "b" }, _api.ReadDataDirectory("equity/usa/daily/").AvailableData);
            Assert.IsTrue(_api.ReadDataPrices("org").Success);

            Assert.Throws<ArgumentException>(() => _api.ReadDataLink(null, "org"));
            Assert.Throws<ArgumentException>(() => _api.ReadDataDirectory(null));
            Assert.Throws<ArgumentException>(() => _api.ReadDataDirectory("equity/usa"));
        }

        [TestCase("C:\\Lean\\Data\\equity\\usa\\daily\\spy.zip", "C:\\Lean\\Data", "equity/usa/daily/spy.zip")]
        [TestCase("/Lean/Data/equity/usa/daily/spy.zip", "/Lean/Data/", "equity/usa/daily/spy.zip")]
        [TestCase("/equity/usa/daily/spy.zip", "/Other", "equity/usa/daily/spy.zip")]
        [TestCase(null, "/Lean/Data", null)]
        public void FormatPathForDataRequest(string path, string dataFolder, string expected)
        {
            Assert.AreEqual(expected, Api.Api.FormatPathForDataRequest(path, dataFolder));
        }

        [Test]
        public void DownloadDataWritesTheLinkedFile()
        {
            var directory = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var target = Path.Combine(directory, "sub", "file.zip");
                _handler.Respond("data/read", "{\"success\":true,\"link\":\"http://fake.download.test/file.zip\"}");
                _handler.RespondBytes("http://fake.download.test/file.zip", new byte[] { 1, 2, 3 });

                Assert.IsTrue(_api.DownloadData(target, "org"));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void DownloadDataReturnsFalseWhenLinkFails()
        {
            _handler.Respond("data/read", "{\"success\":false,\"errors\":[\"no credit\"]}");
            Assert.IsFalse(_api.DownloadData(Path.Combine(Path.GetTempPath(), "never.zip"), "org"));
        }

        [Test]
        public void DownloadDataReturnsFalseWhenDownloadFails()
        {
            var directory = Directory.CreateTempSubdirectory().FullName;
            try
            {
                _handler.Respond("data/read", "{\"success\":true,\"link\":\"http://fake.download.test/missing.zip\"}");
                _handler.Throw("http://fake.download.test/missing.zip", new HttpRequestException("unreachable"));
                Assert.IsFalse(_api.DownloadData(Path.Combine(directory, "file.zip"), "org"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void DownloadBytesSendsHeadersAndBasicCredentials()
        {
            _handler.RespondBytes("http://fake.download.test/data.csv", Encoding.UTF8.GetBytes("a,b"));

            var text = _api.Download("http://fake.download.test/data.csv",
                new[] { new KeyValuePair<string, string>("X-Test", "1") }, "user", "pass");

            Assert.AreEqual("a,b", text);
            var request = _handler.Requests.Single();
            Assert.AreEqual("1", request.Headers["X-Test"]);
            StringAssert.Contains("QCAlgorithm.Download()", request.Headers["User-Agent"]);
            Assert.AreEqual(Convert.ToBase64String(Encoding.ASCII.GetBytes("user:pass")), request.AuthParameter);
        }

        [Test]
        public void DownloadBytesKeepsCallerUserAgent()
        {
            _handler.RespondBytes("http://fake.download.test/data.csv", new byte[] { 9 });
            var bytes = _api.DownloadBytes("http://fake.download.test/data.csv",
                new[] { new KeyValuePair<string, string>("user-agent", "custom") }, null, null);
            CollectionAssert.AreEqual(new byte[] { 9 }, bytes);
            Assert.AreEqual("custom", _handler.Requests.Single().Headers["User-Agent"]);
            Assert.IsNull(_handler.Requests.Single().AuthScheme);
        }

        [Test]
        public void DownloadBytesWrapsFailuresAndMasksPassword()
        {
            _handler.Throw("http://fake.download.test/fail", new HttpRequestException("down"));
            var error = Assert.Throws<WebException>(() => _api.DownloadBytes("http://fake.download.test/fail", null, "user", "secret"));
            StringAssert.Contains("with username: user and password: ******", error.Message);
            StringAssert.DoesNotContain("secret", error.Message);

            error = Assert.Throws<WebException>(() => _api.DownloadBytes("not-a-url", null, null, null));
            StringAssert.DoesNotContain("username", error.Message);
        }

        [Test]
        public void AlgorithmStatusDefaults()
        {
            Assert.AreEqual("*", _api.GetAlgorithmStatus("id").ChartSubscription);
            Assert.DoesNotThrow(() => _api.SetAlgorithmStatus("id", AlgorithmStatus.Running));
            Assert.DoesNotThrow(() => _api.SendStatistics("id", 1, 2, 3, 4, 5, 6, 7, 8, 9));
        }

        [Test]
        public void AccountAndOrganizationEndpoints()
        {
            _handler.Respond("account/read", "{\"success\":true,\"organizationId\":\"org\"}");
            _handler.Respond("organizations/read", "{\"success\":true,\"organization\":{\"type\":\"enterprise\"}}");

            Assert.IsTrue(_api.ReadAccount().Success);
            Assert.IsNull(_handler.Requests.Last().Body);
            Assert.IsTrue(_api.ReadAccount("org").Success);
            Assert.AreEqual("org", (string)_handler.Requests.Last().Json["organizationId"]);
            Assert.AreEqual("enterprise", _api.ReadOrganization("org").Type);
            Assert.AreEqual("enterprise", _api.ReadOrganization().Type);
        }

        [Test]
        public void OptimizationEndpoints()
        {
            _handler.Respond("optimizations/estimate", "{\"success\":true,\"estimate\":{\"estimateId\":\"e-1\"}}");
            _handler.Respond("optimizations/create", "{\"success\":true,\"optimizations\":[{\"optimizationId\":\"o-1\"}]}");
            _handler.Respond("optimizations/list", "{\"success\":true,\"optimizations\":[{\"optimizationId\":\"o-1\"},{\"optimizationId\":\"o-2\"}]}");
            _handler.Respond("optimizations/read", "{\"success\":true,\"optimization\":{\"optimizationId\":\"o-1\"}}");
            _handler.RespondToAll("{\"success\":true}");

            var parameters = new HashSet<OptimizationParameter> { new OptimizationStepParameter("p", 1, 10, 1) };
            var constraints = new List<Constraint> { new Constraint("TotalPerformance.PortfolioStatistics.SharpeRatio", ComparisonOperatorTypes.Greater, 1) };

            var estimate = _api.EstimateOptimization(7, "opt", "Sharpe", "max", null, "grid", "c-1", parameters, constraints);
            Assert.AreEqual("e-1", estimate.EstimateId);
            Assert.AreEqual("p", (string)_handler.Requests.Last().Json["parameters"][0]["name"]);

            var created = _api.CreateOptimization(7, "opt", "Sharpe", "max", 2m, "grid", "c-1", parameters, constraints, 10m, "O2-8", 4);
            Assert.AreEqual("o-1", created.OptimizationId);
            Assert.AreEqual(4, (int)_handler.Requests.Last().Json["parallelNodes"]);

            Assert.AreEqual(2, _api.ListOptimizations(7).Count);
            Assert.AreEqual("o-1", _api.ReadOptimization("o-1").OptimizationId);
            Assert.IsTrue(_api.AbortOptimization("o-1").Success);
            Assert.IsTrue(_api.UpdateOptimization("o-1").Success);
            Assert.IsFalse(_handler.Requests.Last().Json.ContainsKey("name"));
            Assert.IsTrue(_api.UpdateOptimization("o-1", "renamed").Success);
            Assert.AreEqual("renamed", (string)_handler.Requests.Last().Json["name"]);
            Assert.IsTrue(_api.DeleteOptimization("o-1").Success);
        }

        [Test]
        public void ObjectStoreEndpoints()
        {
            _handler.Respond("object/properties", "{\"success\":false,\"errors\":[\"missing\"]}");
            _handler.RespondToAll("{\"success\":true}");

            var properties = _api.GetObjectStoreProperties("org", "key");
            Assert.IsFalse(properties.Success);

            Assert.IsTrue(_api.SetObjectStore("org", "key", new byte[] { 1, 2 }).Success);
            var setRequest = _handler.Requests.Last();
            Assert.AreEqual("object/set", setRequest.Path);
            StringAssert.Contains("multipart/form-data", setRequest.ContentType);
            StringAssert.Contains("name=organizationId", setRequest.Body);

            Assert.IsTrue(_api.DeleteObjectStore("org", "key").Success);
            Assert.IsTrue(_api.ListObjectStore("org", "/").Success);
            Assert.AreEqual("/", (string)_handler.Requests.Last().Json["path"]);
        }

        [Test]
        public void GetObjectStoreDownloadsAndUnzips()
        {
            var directory = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var zip = QuantConnect.Compression.ZipBytes(Encoding.UTF8.GetBytes("stored"), "key.txt");
                _handler.Respond("object/get", "{\"success\":true,\"jobId\":\"j-1\",\"url\":\"http://fake.download.test/store.zip\"}");
                _handler.RespondBytes("http://fake.download.test/store.zip", zip);

                Assert.IsTrue(_api.GetObjectStore("org", new List<string> { "key.txt" }, directory));
                Assert.AreEqual("stored", File.ReadAllText(Path.Combine(directory, "key.txt")));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void GetObjectStoreFailsWhenJobRequestFails()
        {
            _handler.Respond("object/get", "{\"success\":false,\"errors\":[\"denied\"]}");
            Assert.IsFalse(_api.GetObjectStore("org", new List<string> { "key" }));
            _handler.Respond("object/get", "garbage");
            Assert.IsFalse(_api.GetObjectStore("org", new List<string> { "key" }));
        }

        [Test]
        public void GetObjectStoreFailsWhenDownloadFails()
        {
            _handler.Respond("object/get", "{\"success\":true,\"jobId\":\"j-1\",\"url\":\"http://fake.download.test/broken.zip\"}");
            _handler.Throw("http://fake.download.test/broken.zip", new HttpRequestException("down"));
            Assert.IsFalse(_api.GetObjectStore("org", new List<string> { "key" }, Path.GetTempPath()));
        }

#pragma warning disable CS0618
        [Test]
        public void ObsoleteRestSharpRequestFailsGracefullyWithoutNetwork()
        {
            using var connection = new ApiConnection(1, "token", "http://127.0.0.1:1/");
            var request = new RestSharp.RestRequest("authenticate", RestSharp.Method.GET);
            Assert.IsFalse(connection.TryRequest(request, out RestResponse result));
            Assert.IsNull(result);
        }
#pragma warning restore CS0618

        private static HttpClient GetHttpClient(ApiConnection connection)
        {
            return (HttpClient)typeof(ApiConnection).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(connection);
        }

        private class TestApi : Api.Api
        {
            private readonly FakeHandler _handler;

            public TestApi(FakeHandler handler)
            {
                _handler = handler;
                // replace the download client pool with clients backed by the fake handler
                var pool = (BlockingCollection<Lazy<HttpClient>>)typeof(Api.Api)
                    .GetField("_clientPool", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this);
                var count = pool.Count;
                for (var i = 0; i < count; i++)
                {
                    pool.Take();
                    pool.Add(new Lazy<HttpClient>(() => new HttpClient(_handler, disposeHandler: false)));
                }
            }

            public ApiConnection GetConnection()
            {
                return CreateApiConnection(123, "secret-token");
            }

            protected override ApiConnection CreateApiConnection(int userId, string token)
            {
                var connection = new ApiConnection(userId, token, BaseUrl);
                var field = typeof(ApiConnection).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
                ((HttpClient)field.GetValue(connection)).Dispose();
                field.SetValue(connection, new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) });
                return connection;
            }
        }

        private class RecordedRequest
        {
            public HttpMethod Method { get; set; }
            public string Uri { get; set; }
            public string Path { get; set; }
            public string Body { get; set; }
            public string ContentType { get; set; }
            public string AuthScheme { get; set; }
            public string AuthParameter { get; set; }
            public string Timestamp { get; set; }
            public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
            public JObject Json => JObject.Parse(Body);
        }

        private class FakeHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new();
            private string _defaultResponse;

            public List<RecordedRequest> Requests { get; } = new();

            public void Respond(string key, string json, HttpStatusCode statusCode = HttpStatusCode.OK)
            {
                _responses[key] = () => new HttpResponseMessage(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            public void RespondBytes(string key, byte[] bytes)
            {
                _responses[key] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }

            public void Throw(string key, Exception exception)
            {
                _responses[key] = () => throw exception;
            }

            public void RespondToAll(string json)
            {
                _defaultResponse = json;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri.ToString();
                var path = uri.StartsWith(BaseUrl, StringComparison.Ordinal) ? uri.Substring(BaseUrl.Length) : uri;
                var recorded = new RecordedRequest
                {
                    Method = request.Method,
                    Uri = uri,
                    Path = path,
                    Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                    ContentType = request.Content?.Headers.ContentType?.MediaType,
                    AuthScheme = request.Headers.Authorization?.Scheme,
                    AuthParameter = request.Headers.Authorization?.Parameter,
                    Timestamp = request.Headers.TryGetValues("Timestamp", out var values) ? values.Single() : null
                };
                foreach (var header in request.Headers)
                {
                    recorded.Headers[header.Key] = string.Join(" ", header.Value);
                }
                Requests.Add(recorded);

                if (_responses.TryGetValue(path, out var factory))
                {
                    return factory();
                }
                if (_defaultResponse != null)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_defaultResponse, Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"success\":false}") };
            }
        }
    }
}
