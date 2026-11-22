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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Gateway;
using ReportRecord = QuantConnect.Services.Contracts.Http.Report;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace QuantConnect.Tests.Services.Gateway
{
    /// <summary>
    /// The gateway on Kestrel in front of a fake orchestrator/results upstream; report and optimizer point at a
    /// closed port to exercise the 503 path.
    /// </summary>
    [TestFixture, Category("Services"), NonParallelizable]
    public class GatewayTests
    {
        private const string AliceKey = "alice-test-key";
        private const string BobKey = "bob-test-key";
        private const string AdminKey = "admin-test-key";
        private const string SigningKey = "gateway-tests-signing-key-0123456789abcdef";
        private static readonly Guid AliceJob = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private WebApplication _upstream;
        private WebApplication _gateway;
        private HttpClient _http;
        private string _upstreamUrl;
        private readonly ConcurrentQueue<Captured> _captured = new();

        private sealed record Captured(string Method, string PathAndQuery, Dictionary<string, string> Headers, string Body, string ContentType);

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _upstream = BuildUpstream();
            await _upstream.StartAsync();
            _upstreamUrl = _upstream.Urls.First();
            (_gateway, _http) = await StartGateway(permitLimit: 1000);
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            _http?.Dispose();
            if (_gateway != null) await _gateway.DisposeAsync();
            if (_upstream != null) await _upstream.DisposeAsync();
        }

        [SetUp]
        public void SetUp() => _captured.Clear();

        [Test]
        public async Task RejectsMissingAndUnknownKeysWithProblemDetails()
        {
            using var missing = await _http.GetAsync("/api/v1/jobs");
            Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.AreEqual(Problems.ContentType, missing.Content.Headers.ContentType?.MediaType);

            using var unknown = await Send(HttpMethod.Get, "/api/v1/jobs", "not-a-key");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.StatusCode);
            StringAssert.Contains("invalid API key", await unknown.Content.ReadAsStringAsync());
            Assert.IsEmpty(_captured);
        }

        [Test]
        public async Task UploadedAlgorithmRefCanBeSubmittedAsJson()
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(Encoding.UTF8.GetBytes("class Algo(QCAlgorithm): pass")), "file", "main.py" },
                { new StringContent("Python"), "language" },
            };
            using var upload = await Send(HttpMethod.Post, "/api/v1/algorithms", AliceKey, form, x => x.Headers.Add("X-Api-Key", AliceKey));
            var algorithm = await upload.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Created, upload.StatusCode, algorithm);
            StringAssert.StartsWith("{\"source\":\"upload\"", algorithm, "the upload response is an AlgorithmRef with its discriminator");

            using var body = new StringContent($"{{\"algorithm\":{algorithm},\"fetchMissingData\":false,\"maxRuntimeSeconds\":60}}", Encoding.UTF8, "application/json");
            using var response = await Send(HttpMethod.Post, "/api/v1/backtests", AliceKey, body, x => x.Headers.Add("X-Api-Key", AliceKey));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            var job = _captured.Single(c => c.PathAndQuery == "/internal/v1/jobs" && c.Method == "POST");
            Assert.IsInstanceOf<UploadedAlgorithmRef>(ContractJson.Deserialize<JobRequest>(job.Body).Backtest.Algorithm);
        }

        [Test]
        public async Task SubmitsPythonSourceBacktestThroughTheOrchestrator()
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(Encoding.UTF8.GetBytes("class Algo(QCAlgorithm): pass")), "file", "main.py" },
                { new StringContent("2020-01-01"), "startDate" },
                { new StringContent("2020-02-01"), "endDate" },
                { new StringContent("{\"fast\":\"10\"}"), "parameters" },
            };
            using var response = await Send(HttpMethod.Post, "/api/v1/backtests", AliceKey, form, x => x.Headers.Add("X-Api-Key", AliceKey));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());

            var upload = _captured.Single(c => c.PathAndQuery.StartsWith("/internal/v1/algorithms"));
            Assert.AreEqual("/internal/v1/algorithms?fileName=main.py", upload.PathAndQuery);
            Assert.AreEqual(AlgorithmPackages.PythonSource, upload.ContentType);
            Assert.AreEqual("alice", upload.Headers["X-Lean-User"]);

            var job = _captured.Single(c => c.PathAndQuery == "/internal/v1/jobs" && c.Method == "POST");
            var request = ContractJson.Deserialize<JobRequest>(job.Body);
            Assert.AreEqual(JobKind.Backtest, request.Kind);
            var algorithm = (UploadedAlgorithmRef)request.Backtest.Algorithm;
            Assert.AreEqual(AlgorithmLanguage.Python, algorithm.Language);
            Assert.AreEqual(Buckets.Algorithms, algorithm.Object.Bucket);
            Assert.AreEqual(new DateOnly(2020, 1, 1), request.Backtest.StartDate);
            Assert.AreEqual("10", request.Backtest.Parameters["fast"]);
            Assert.AreEqual("alice", job.Headers["X-Lean-User"]);
        }

        [TestCase("algo.cs", AlgorithmLanguage.CSharp, AlgorithmPackages.CSharpSource)]
        [TestCase("Algorithm.dll", AlgorithmLanguage.CSharp, AlgorithmPackages.Assembly)]
        public async Task SubmitsCSharpBacktests(string fileName, AlgorithmLanguage language, string contentType)
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(Encoding.UTF8.GetBytes("public class A {}")), "file", fileName },
                { new StringContent("QuantConnect.Algorithm.CSharp.A"), "typeName" },
            };
            using var response = await Send(HttpMethod.Post, "/api/v1/backtests", AliceKey, form);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(contentType, _captured.Single(c => c.PathAndQuery.StartsWith("/internal/v1/algorithms")).ContentType);
            var request = ContractJson.Deserialize<JobRequest>(_captured.Single(c => c.PathAndQuery == "/internal/v1/jobs").Body);
            var algorithm = (UploadedAlgorithmRef)request.Backtest.Algorithm;
            Assert.AreEqual(language, algorithm.Language);
            Assert.AreEqual("QuantConnect.Algorithm.CSharp.A", algorithm.TypeName);
        }

        [Test]
        public void ClassifiesProjectZips()
        {
            Assert.AreEqual(AlgorithmLanguage.Python, AlgorithmPackages.Inspect("p.zip", Zip(("project/main.py", "x"), ("project/lib/util.py", "y")), null, out var error).Language);
            Assert.IsNull(error);
            Assert.AreEqual(AlgorithmLanguage.CSharp, AlgorithmPackages.Inspect("p.zip", Zip(("Main.cs", "x"), ("Helpers/Util.cs", "y")), null, out error).Language);
            Assert.IsNull(error);
            AlgorithmPackages.Inspect("p.zip", Zip(("../evil.py", "x")), null, out error);
            StringAssert.Contains("escapes", error);
            AlgorithmPackages.Inspect("p.zip", Zip(("readme.txt", "x")), null, out error);
            StringAssert.Contains("neither", error);
            AlgorithmPackages.Inspect("p.zip", Encoding.UTF8.GetBytes("not a zip"), null, out error);
            StringAssert.Contains("not a valid zip", error);
            AlgorithmPackages.Inspect("main.py", new byte[1], AlgorithmLanguage.CSharp, out error);
            StringAssert.Contains("does not match", error);
            AlgorithmPackages.Inspect("algo.exe", new byte[1], null, out error);
            StringAssert.Contains("unsupported", error);
        }

        [Test]
        public async Task RejectsUnsafeZipBeforeUploading()
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Zip(("../../etc/main.py", "x"))), "file", "project.zip" } };
            using var response = await Send(HttpMethod.Post, "/api/v1/backtests", AliceKey, form);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);
            Assert.IsEmpty(_captured);
        }

        [Test]
        public async Task AcceptsJwtBearerTokensFromAnIdentityProvider()
        {
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[] { new Claim("sub", "carol@idp") }),
                Issuer = "https://idp.test",
                Audience = "lean-api",
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
            });
            var body = "{\"algorithm\":{\"source\":\"builtin\",\"typeName\":\"BasicTemplateAlgorithm\"}}";
            using var response = await Send(HttpMethod.Post, "/api/v1/backtests", token, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            var job = _captured.Single(c => c.PathAndQuery == "/internal/v1/jobs");
            Assert.AreEqual("carol@idp", job.Headers["X-Lean-User"]);
            Assert.IsInstanceOf<BuiltinAlgorithmRef>(ContractJson.Deserialize<JobRequest>(job.Body).Backtest.Algorithm);

            using var badSignature = await Send(HttpMethod.Get, "/api/v1/jobs", token[..^4] + "AAAA");
            Assert.AreEqual(HttpStatusCode.Unauthorized, badSignature.StatusCode);
        }

        [Test]
        public async Task ProxiesResultsWithRequestIdAndWithoutCredentials()
        {
            var tagged = new ConcurrentBag<string>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem(RequestIdMiddleware.TagName) is string id) tagged.Add(id);
                },
            };
            ActivitySource.AddActivityListener(listener);

            using var response = await Send(HttpMethod.Get, $"/api/v1/jobs/{AliceJob}/result?attempt=1", AliceKey, configure: r => r.Headers.Add("X-Request-ID", "req-123"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual("req-123", response.Headers.GetValues("X-Request-ID").Single());
            var forwarded = _captured.Single(c => c.PathAndQuery.StartsWith("/internal/v1/results/"));
            Assert.AreEqual($"/internal/v1/results/{AliceJob}?attempt=1", forwarded.PathAndQuery);
            Assert.AreEqual("req-123", forwarded.Headers["X-Request-ID"]);
            Assert.AreEqual("alice", forwarded.Headers["X-Lean-User"]);
            Assert.IsFalse(forwarded.Headers.ContainsKey("Authorization"));
            Assert.IsFalse(forwarded.Headers.ContainsKey("X-Api-Key"));
            Assert.IsTrue(forwarded.Headers.ContainsKey("traceparent"));
            CollectionAssert.Contains(tagged, "req-123");

            using var charts = await Send(HttpMethod.Get, $"/api/v1/jobs/{AliceJob}/charts/Strategy%20Equity", AliceKey);
            Assert.AreEqual(HttpStatusCode.OK, charts.StatusCode);
            Assert.IsTrue(_captured.Any(c => c.PathAndQuery == $"/internal/v1/results/{AliceJob}/charts/Strategy%20Equity"));

            using var generated = await Send(HttpMethod.Get, "/api/v1/jobs", AliceKey);
            var id = generated.Headers.GetValues("X-Request-ID").Single();
            Assert.That(id, Has.Length.GreaterThan(8));
        }

        [Test]
        public async Task HidesOtherUsersJobs()
        {
            using var job = await Send(HttpMethod.Get, $"/api/v1/jobs/{AliceJob}", BobKey);
            Assert.AreEqual(HttpStatusCode.NotFound, job.StatusCode);
            using var result = await Send(HttpMethod.Get, $"/api/v1/jobs/{AliceJob}/result", BobKey);
            Assert.AreEqual(HttpStatusCode.NotFound, result.StatusCode);
            using var cancel = await Send(HttpMethod.Post, $"/api/v1/jobs/{AliceJob}/cancel", BobKey);
            Assert.AreEqual(HttpStatusCode.NotFound, cancel.StatusCode);
            Assert.IsFalse(_captured.Any(c => c.PathAndQuery.StartsWith("/internal/v1/results") || c.PathAndQuery.EndsWith("/cancel")));

            using var admin = await Send(HttpMethod.Get, $"/api/v1/jobs/{AliceJob}/result", AdminKey);
            Assert.AreEqual(HttpStatusCode.OK, admin.StatusCode);
            using var own = await Send(HttpMethod.Get, $"/api/v1/jobs/{AliceJob}", AliceKey);
            Assert.AreEqual(HttpStatusCode.OK, own.StatusCode);
            Assert.AreEqual("alice", ContractJson.Deserialize<Job>(await own.Content.ReadAsStringAsync()).SubmittedBy);
        }

        [Test]
        public async Task ListsOnlyTheCallersJobsUnlessAdmin()
        {
            using var alice = await Send(HttpMethod.Get, "/api/v1/jobs?kind=backtest&submittedBy=bob", AliceKey);
            Assert.AreEqual(HttpStatusCode.OK, alice.StatusCode);
            using var admin = await Send(HttpMethod.Get, "/api/v1/jobs?submittedBy=bob", AdminKey);
            var lists = _captured.Where(c => c.Method == "GET" && c.PathAndQuery.StartsWith("/internal/v1/jobs?")).Select(c => c.PathAndQuery).ToList();
            CollectionAssert.AreEqual(new[] { "/internal/v1/jobs?kind=backtest&submittedBy=alice", "/internal/v1/jobs?submittedBy=bob" }, lists);
        }

        [Test]
        public async Task DeploysAndStopsLive()
        {
            var body = "{\"algorithm\":{\"source\":\"builtin\",\"typeName\":\"BasicTemplateAlgorithm\"},\"brokerage\":\"PaperBrokerage\"}";
            using var deploy = await Send(HttpMethod.Post, "/api/v1/live", AliceKey, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Created, deploy.StatusCode, await deploy.Content.ReadAsStringAsync());
            Assert.AreEqual(JobKind.Live, ContractJson.Deserialize<JobRequest>(_captured.Single(c => c.PathAndQuery == "/internal/v1/jobs").Body).Kind);

            using var stop = await Send(HttpMethod.Post, $"/api/v1/live/{AliceJob}/stop", AliceKey);
            Assert.AreEqual(HttpStatusCode.Accepted, stop.StatusCode);
            var cancel = _captured.Single(c => c.PathAndQuery == $"/internal/v1/jobs/{AliceJob}/cancel");
            StringAssert.Contains("live deployment stopped", cancel.Body);
        }

        [TestCase("/api/v1/reports/11111111-1111-1111-1111-111111111111", "report")]
        [TestCase("/api/v1/reports?sourceJobId=11111111-1111-1111-1111-111111111111", "report")]
        [TestCase("/api/v1/optimizations/11111111-1111-1111-1111-111111111111", "optimizer")]
        [TestCase("/api/v1/reports/11111111-1111-1111-1111-111111111111/html", "report")]
        public async Task ReturnsServiceUnavailableProblemWhenUpstreamIsDown(string path, string upstream)
        {
            using var response = await Send(HttpMethod.Get, path, AliceKey);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);
            var problem = ContractJson.Deserialize<Problem>(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(503, problem.Status);
            StringAssert.Contains($"upstream '{upstream}' is unavailable", problem.Detail);
        }

        [Test]
        public async Task ProxiesReportsToTheReportServiceForTheJobOwner()
        {
            var (gateway, http) = await StartGateway(permitLimit: 1000, reportUrl: _upstreamUrl);
            try
            {
                using var report = await Send(http, HttpMethod.Get, $"/api/v1/reports/{AliceJob}", AliceKey);
                Assert.AreEqual(HttpStatusCode.OK, report.StatusCode);
                Assert.AreEqual(AliceJob, ContractJson.Deserialize<ReportRecord>(await report.Content.ReadAsStringAsync()).JobId);
                Assert.AreEqual("alice", _captured.Single(c => c.PathAndQuery == $"/internal/v1/reports/{AliceJob}").Headers["X-Lean-User"]);

                using var html = await Send(http, HttpMethod.Get, $"/api/v1/reports/{AliceJob}/html", AliceKey);
                Assert.AreEqual(HttpStatusCode.Redirect, html.StatusCode);
                StringAssert.Contains($"/{Buckets.Reports}/{AliceJob:D}/1/report.html", html.Headers.Location!.ToString());

                using var notOwner = await Send(http, HttpMethod.Get, $"/api/v1/reports/{AliceJob}", BobKey);
                Assert.AreEqual(HttpStatusCode.NotFound, notOwner.StatusCode);

                using var owned = await Send(http, HttpMethod.Get, $"/api/v1/reports?sourceJobId={AliceJob}", AliceKey);
                Assert.AreEqual(HttpStatusCode.OK, owned.StatusCode);
                Assert.IsTrue(_captured.Any(c => c.PathAndQuery == $"/internal/v1/reports?sourceJobId={AliceJob}"));
                using var unscoped = await Send(http, HttpMethod.Get, "/api/v1/reports", AliceKey);
                Assert.AreEqual(HttpStatusCode.BadRequest, unscoped.StatusCode);
                using var admin = await Send(http, HttpMethod.Get, "/api/v1/reports", AdminKey);
                Assert.AreEqual(HttpStatusCode.OK, admin.StatusCode);
            }
            finally
            {
                http.Dispose();
                await gateway.DisposeAsync();
            }
        }

        [TestCase(AliceKey, HttpStatusCode.Created)]
        [TestCase(BobKey, HttpStatusCode.NotFound)]
        public async Task RequestsReportsOnlyForOwnedSourceJobs(string key, HttpStatusCode expected)
        {
            var body = $"{{\"sourceJobId\":\"{AliceJob}\",\"formats\":[\"html\"]}}";
            using var response = await Send(HttpMethod.Post, "/api/v1/reports", key, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            var submitted = _captured.Where(c => c.PathAndQuery == "/internal/v1/jobs" && c.Method == "POST").ToList();
            Assert.AreEqual(expected == HttpStatusCode.Created ? 1 : 0, submitted.Count);
            if (submitted.Count == 1)
            {
                var request = ContractJson.Deserialize<JobRequest>(submitted[0].Body);
                Assert.AreEqual(JobKind.Report, request.Kind);
                Assert.AreEqual(AliceJob, request.Report!.SourceJobId);
            }
        }

        [Test]
        public async Task LimitsRequestsPerApiKey()
        {
            var (gateway, http) = await StartGateway(permitLimit: 2);
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    using var ok = await Send(http, HttpMethod.Get, "/api/v1/jobs", AliceKey);
                    Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
                }
                using var limited = await Send(http, HttpMethod.Get, "/api/v1/jobs", AliceKey);
                Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.AreEqual(Problems.ContentType, limited.Content.Headers.ContentType?.MediaType);
                Assert.IsTrue(limited.Headers.Contains("Retry-After"));
                using var otherKey = await Send(http, HttpMethod.Get, "/api/v1/jobs", BobKey);
                Assert.AreEqual(HttpStatusCode.OK, otherKey.StatusCode);
                using var health = await http.GetAsync("/healthz");
                Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
            }
            finally
            {
                http.Dispose();
                await gateway.DisposeAsync();
            }
        }

        [Test]
        public async Task ServesTheMergedOpenApiDocument()
        {
            using var response = await _http.GetAsync("/openapi");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            var paths = document["paths"]!.AsObject();
            foreach (var path in new[] { "/api/v1/backtests", "/api/v1/live", "/api/v1/live/{id}/stop", "/api/v1/jobs", "/api/v1/jobs/{id}", "/api/v1/algorithms" })
            {
                Assert.IsTrue(paths.ContainsKey(path), path);
            }
            Assert.IsNotNull(paths["/api/v1/jobs/{id}/result"]?["get"], "results operations are merged under their public path");
            Assert.IsNotNull(paths["/api/v1/jobs/{id}/charts"]?["get"]);
            Assert.IsFalse(paths.Any(p => p.Key.StartsWith("/internal/")));
            Assert.IsTrue(document["components"]!["schemas"]!.AsObject().ContainsKey("ResultSummary"));
            Assert.IsNotNull(document["components"]!["securitySchemes"]!["bearer"]);
            StringAssert.Contains("from the contract report.openapi.yaml", (string)document["x-lean-upstreams"]!["report"]);
            StringAssert.Contains("from the live document", (string)document["x-lean-upstreams"]!["results"]);
            Assert.IsNotNull(paths["/api/v1/reports/{id}"]?["get"], "report operations come from its contract while it is down");
            Assert.IsNotNull(paths["/api/v1/optimizations/{id}"]?["get"], "optimizer operations come from its contract");
            var json = document.ToJsonString();
            foreach (System.Text.RegularExpressions.Match reference in System.Text.RegularExpressions.Regex.Matches(json, "\"\\$ref\":\"([^\"]+)\""))
            {
                var target = reference.Groups[1].Value;
                StringAssert.StartsWith("#/components/", target);
                var parts = target[2..].Split('/');
                Assert.IsNotNull(document[parts[0]]?[parts[1]]?[parts[2]], $"unresolved {target}");
            }
        }

        [TestCase("/internal/v1/data/history", "market-data", "/api/v1/data/history")]
        [TestCase("/internal/v1/results/{id}", "results-summary", "/api/v1/jobs/{id}/result")]
        [TestCase("/internal/v1/results/{id}/logs", "results-logs", "/api/v1/jobs/{id}/logs")]
        [TestCase("/internal/v1/optimizations/estimate", "optimizer", "/api/v1/optimizations/estimate")]
        [TestCase("/internal/v1/jobs", "market-data", null)]
        public void MapsInternalPathsToPublicRoutes(string internalPath, string routeId, string expected)
        {
            Assert.AreEqual(expected, OpenApiMerger.PublicPath(GatewayProxy.Routes.Single(r => r.Id == routeId), internalPath));
        }

        private async Task<(WebApplication, HttpClient)> StartGateway(int permitLimit, string reportUrl = "http://127.0.0.1:1")
        {
            var app = GatewayHost.Build(Array.Empty<string>(), builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Gateway:Upstreams:Orchestrator"] = _upstreamUrl,
                    ["Gateway:Upstreams:Results"] = _upstreamUrl,
                    ["Gateway:Upstreams:MarketData"] = _upstreamUrl,
                    ["Gateway:Upstreams:Report"] = reportUrl,
                    ["Gateway:Upstreams:Optimizer"] = "http://127.0.0.1:1",
                    ["Gateway:ApiKeys:0:Key"] = AliceKey,
                    ["Gateway:ApiKeys:0:User"] = "alice",
                    ["Gateway:ApiKeys:1:KeySha256"] = GatewayAuth.Sha256Hex(BobKey),
                    ["Gateway:ApiKeys:1:User"] = "bob",
                    ["Gateway:ApiKeys:2:Key"] = AdminKey,
                    ["Gateway:ApiKeys:2:User"] = "ops",
                    ["Gateway:ApiKeys:2:Admin"] = "true",
                    ["Gateway:Jwt:SigningKey"] = SigningKey,
                    ["Gateway:Jwt:Issuer"] = "https://idp.test",
                    ["Gateway:Jwt:Audience"] = "lean-api",
                    ["Gateway:RateLimit:PermitLimit"] = permitLimit.ToString(),
                    ["Gateway:RateLimit:WindowSeconds"] = "60",
                    ["S3:AccessKey"] = "test-access",
                    ["S3:SecretKey"] = "test-secret",
                    ["OTEL_SDK_DISABLED"] = "true",
                });
            });
            await app.StartAsync();
            return (app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) });
        }

        private Task<HttpResponseMessage> Send(HttpMethod method, string path, string credential, HttpContent content = null, Action<HttpRequestMessage> configure = null) =>
            Send(_http, method, path, credential, content, configure);

        private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string path, string credential, HttpContent content = null, Action<HttpRequestMessage> configure = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            configure?.Invoke(request);
            return http.SendAsync(request);
        }

        private static byte[] Zip(params (string Name, string Content)[] entries)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                foreach (var (name, content) in entries)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(content);
                }
            }
            return buffer.ToArray();
        }

        private WebApplication BuildUpstream()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                http.Request.EnableBuffering();
                var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
                http.Request.Body.Position = 0;
                if (!http.Request.Path.StartsWithSegments("/openapi"))
                {
                    _captured.Enqueue(new Captured(http.Request.Method, http.Request.Path + http.Request.QueryString,
                        http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body, http.Request.ContentType));
                }
                await next();
            });
            IResult Json<T>(T value, int status = 200) => HttpResults.Text(ContractJson.Serialize(value), "application/json", Encoding.UTF8, status);
            Job JobFor(Guid id) => new() { Id = id, Kind = JobKind.Backtest, Status = JobStatus.Running, Attempt = 1, SubmittedBy = "alice", CreatedAt = DateTimeOffset.UtcNow };
            app.MapPost("/internal/v1/algorithms", (string fileName, HttpRequest request) =>
                Json(new ObjectRef { Bucket = Buckets.Algorithms, Key = $"alice/{fileName}", Size = request.ContentLength ?? 0, Sha256 = new string('0', 64), ContentType = request.ContentType }, 201));
            app.MapPost("/internal/v1/jobs", () => Json(JobFor(Guid.NewGuid()), 201));
            app.MapGet("/internal/v1/jobs", () => Json(new Page<Job> { Items = new List<Job> { JobFor(AliceJob) } }));
            app.MapGet("/internal/v1/jobs/{id:guid}", (Guid id) => id == AliceJob ? Json(JobFor(id)) : HttpResults.NotFound());
            app.MapPost("/internal/v1/jobs/{id:guid}/cancel", (Guid id) => Json(JobFor(id) with { Status = JobStatus.Cancelled }, 202));
            app.MapGet("/internal/v1/results/{id:guid}", (Guid id) => HttpResults.Text($"{{\"jobId\":\"{id}\"}}", "application/json"));
            app.MapGet("/internal/v1/results/{id:guid}/charts/{**chart}", (Guid id) => HttpResults.Text("{}", "application/json"));
            ReportRecord ReportFor(Guid id) => new()
            {
                JobId = id, Status = JobStatus.Completed, Attempt = 1, Spec = new ReportSpec { SourceJobId = AliceJob },
                Html = new ObjectRef { Bucket = Buckets.Reports, Key = $"{id:D}/1/report.html", Size = 10, Sha256 = new string('0', 64), ContentType = "text/html" },
            };
            app.MapGet("/internal/v1/reports", () => Json(new Page<ReportRecord> { Items = new List<ReportRecord> { ReportFor(AliceJob) } }));
            app.MapGet("/internal/v1/reports/{id:guid}", (Guid id) => Json(ReportFor(id)));
            app.MapGet("/openapi/v1.json", () => HttpResults.Text("""
                {"openapi":"3.1.1","info":{"title":"results","version":"v1"},
                 "paths":{"/internal/v1/results/{id}":{"get":{"operationId":"getResult","responses":{"200":{"description":"ok"}}}},
                          "/internal/v1/results/{id}/charts":{"get":{"operationId":"listCharts","responses":{"200":{"description":"ok"}}}}},
                 "components":{"schemas":{"ResultSummary":{"type":"object"}}}}
                """, "application/json"));
            return app;
        }
    }
}
