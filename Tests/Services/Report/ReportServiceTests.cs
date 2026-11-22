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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Report;
using QuantConnect.Services.Results;

namespace QuantConnect.Tests.Services.Report
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;
    using ReportRecord = QuantConnect.Services.Contracts.Http.Report;

    /// <summary>
    /// Report service end to end on the in-memory bus with a file-system object store standing in for MinIO: a stored
    /// backtest result (<c>TestData/test_report_data.json</c>) is published as <c>report.requested</c> and the service
    /// must publish <c>job.started</c> + <c>job.completed</c> with the uploaded report. The <c>LeanReport</c> tests run
    /// LEAN's real report pipeline (pythonnet), so run them inside <c>quantconnect/lean:foundation</c>.
    /// </summary>
    [TestFixture, Category("Services"), NonParallelizable]
    public class ReportServiceTests
    {
        private static readonly string Fixture = Path.Combine("TestData", "test_report_data.json");
        private string _root;
        private FileSystemResultObjectStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lean-report-tests-" + Guid.NewGuid().ToString("N"));
            _store = new FileSystemResultObjectStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public async Task RendersStoredBacktestResultThroughTheBus()
        {
            var renderer = new FakeRenderer();
            await using var service = await Start(renderer);
            var request = await Request(new List<ReportFormat> { ReportFormat.Html, ReportFormat.Pdf });

            await Dispatch(service, request);

            var events = Events(service.Bus, request.JobId);
            Assert.AreEqual(new[] { MessageTypes.JobStarted, MessageTypes.JobCompleted }, events.Select(e => e.Envelope.Type).ToArray());
            var started = ContractJson.Deserialize<JobStarted>(events[0].Body.Span);
            Assert.AreEqual(Buckets.Reports, started.Results.Bucket);
            Assert.AreEqual($"{request.JobId:D}/1/", started.Results.Prefix);
            Assert.AreEqual($"job.report.{request.JobId:D}.status.v1", events[1].RoutingKey);

            var completed = ContractJson.Deserialize<JobCompleted>(events[1].Body.Span);
            Assert.AreEqual(JobKind.Report, completed.Kind);
            Assert.AreEqual($"{request.JobId:D}/1/report.html", completed.Result.Key);
            Assert.AreEqual(Buckets.Reports, completed.Result.Bucket);
            StringAssert.Contains("Fixture strategy", Encoding.UTF8.GetString(await _store.GetVerifiedAsync(completed.Result)));
            CollectionAssert.AreEqual(new[] { "report.pdf", "report.log" }, completed.Artifacts.Select(a => Path.GetFileName(a.Key)).ToArray());
            Assert.AreEqual("%PDF-fake", Encoding.UTF8.GetString(await _store.GetVerifiedAsync(completed.Artifacts[0])));

            Assert.IsTrue(renderer.Last.Pdf);
            Assert.AreEqual(File.ReadAllText(Fixture), renderer.BacktestJson);
            Assert.IsNull(renderer.Last.LiveResultFile);

            using var get = await service.Http.GetAsync($"internal/v1/reports/{request.JobId:D}");
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            var report = ContractJson.Deserialize<ReportRecord>(await get.Content.ReadAsStringAsync());
            Assert.AreEqual(JobStatus.Completed, report.Status);
            Assert.AreEqual(completed.Result, report.Html);
            Assert.AreEqual(completed.Artifacts[0], report.Pdf);

            using var list = await service.Http.GetAsync($"internal/v1/reports?sourceJobId={request.Report.SourceJobId:D}");
            var page = JsonNode.Parse(await list.Content.ReadAsStringAsync())!;
            Assert.AreEqual(request.JobId.ToString("D"), (string)page["items"]![0]!["jobId"]);
            using var other = await service.Http.GetAsync($"internal/v1/reports?sourceJobId={Guid.NewGuid():D}");
            Assert.AreEqual(0, JsonNode.Parse(await other.Content.ReadAsStringAsync())!["items"]!.AsArray().Count);
            using var missing = await service.Http.GetAsync($"internal/v1/reports/{Guid.NewGuid():D}");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        [Test]
        public async Task DefaultsToHtmlAndPassesTheLiveResult()
        {
            var renderer = new FakeRenderer();
            await using var service = await Start(renderer);
            var request = await Request(null);
            var live = await _store.PutRefAsync(Buckets.Results, $"{Guid.NewGuid():D}/1/result.json", Encoding.UTF8.GetBytes("{}"), "application/json");
            request = request with { LiveResult = live };

            await Dispatch(service, request);

            Assert.IsFalse(renderer.Last.Pdf);
            Assert.AreEqual("{}", renderer.LiveJson);
            var completed = ContractJson.Deserialize<JobCompleted>(Events(service.Bus, request.JobId).Last().Body.Span);
            CollectionAssert.AreEqual(new[] { "report.log" }, completed.Artifacts.Select(a => Path.GetFileName(a.Key)).ToArray());
            Assert.IsFalse(Directory.Exists(renderer.Last.WorkDirectory), "the work directory is removed after the render");
        }

        [Test]
        public async Task TamperedResultFailsFinal()
        {
            var renderer = new FakeRenderer();
            await using var service = await Start(renderer);
            var request = await Request(null);
            request = request with { BacktestResult = request.BacktestResult with { Sha256 = new string('0', 64) } };

            await Dispatch(service, request);

            var failed = ContractJson.Deserialize<JobFailed>(Events(service.Bus, request.JobId).Last().Body.Span);
            Assert.IsTrue(failed.Final);
            Assert.AreEqual(ErrorCode.InvalidRequest, failed.Error.Code);
            Assert.IsNull(renderer.Last);
            var report = await service.App.Services.GetRequiredService<ReportCatalog>().GetAsync(request.JobId, 1, CancellationToken.None);
            Assert.AreEqual(JobStatus.Failed, report.Status);
        }

        [Test]
        public async Task RenderFailureIsRetryableUntilTheLastAttempt()
        {
            await using var service = await Start(new FakeRenderer { Fail = true });
            var first = await Request(null);
            await Dispatch(service, first);
            var failed = ContractJson.Deserialize<JobFailed>(Events(service.Bus, first.JobId).Last().Body.Span);
            Assert.IsFalse(failed.Final);
            Assert.AreEqual(ErrorCode.RuntimeError, failed.Error.Code);

            var last = (await Request(null)) with { Attempt = 3 };
            await Dispatch(service, last);
            failed = ContractJson.Deserialize<JobFailed>(Events(service.Bus, last.JobId).Last().Body.Span);
            Assert.IsTrue(failed.Final);
            Assert.AreEqual(ErrorCode.MaxAttemptsExceeded, failed.Error.Code);
            Assert.AreEqual(0, service.Bus.Peek(Queues.Report).Count, "failed dispatches are acked once job.failed is published");
        }

        [Test]
        public void RelativeDataFolderIsRejected()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Report:DataFolder"] = "../../../Data" }).Build();
            Assert.Throws<InvalidOperationException>(() => ReportSettings.From(configuration));
        }

        [Test, Category("LeanReport")]
        public async Task RendersStoredBacktestResultWithLeanReport()
        {
            var repository = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", ".."));
            var reportDirectory = Path.Combine(repository, "Report", "bin", "Release");
            var dataFolder = Path.Combine(repository, "Data") + Path.DirectorySeparatorChar;
            if (!File.Exists(Path.Combine(reportDirectory, LeanReportRenderer.ReportAssembly)))
            {
                Assert.Ignore($"{reportDirectory} has no {LeanReportRenderer.ReportAssembly}; build the solution first");
            }
            await using var service = await Start(null, new Dictionary<string, string>
            {
                ["Report:Directory"] = reportDirectory,
                ["Report:DataFolder"] = dataFolder,
            });
            var request = await Request(new List<ReportFormat> { ReportFormat.Html, ReportFormat.Pdf });

            await Dispatch(service, request);

            var events = Events(service.Bus, request.JobId);
            var terminal = events.Last();
            Assert.AreEqual(MessageTypes.JobCompleted, terminal.Envelope.Type, Encoding.UTF8.GetString(terminal.Body.Span));
            var completed = ContractJson.Deserialize<JobCompleted>(terminal.Body.Span);
            var html = Encoding.UTF8.GetString(await _store.GetVerifiedAsync(completed.Result));
            StringAssert.Contains("Fixture strategy", html);
            StringAssert.Contains("<html", html);
            StringAssert.Contains("data:image/png;base64", html, "ReportCharts.py rendered the charts through pythonnet");
            Assert.AreEqual("text/html", completed.Result.ContentType);
            if (completed.Artifacts.FirstOrDefault(a => a.Key.EndsWith("report.pdf", StringComparison.Ordinal)) is { } pdf)
            {
                Assert.AreEqual("%PDF", Encoding.ASCII.GetString((await _store.GetVerifiedAsync(pdf))[..4]));
            }
            var log = Encoding.UTF8.GetString(await _store.GetVerifiedAsync(completed.Artifacts.Single(a => a.Key.EndsWith("report.log", StringComparison.Ordinal))));
            StringAssert.Contains("Starting content compile", log);
            TestContext.Progress.WriteLine($"html {completed.Result.Size} bytes; artifacts: {string.Join(", ", completed.Artifacts.Select(a => $"{a.Key} ({a.Size} bytes)"))}");
        }

        private async Task<ReportRequested> Request(List<ReportFormat> formats)
        {
            var sourceJobId = Guid.NewGuid();
            var backtest = await _store.PutRefAsync(Buckets.Results, $"{sourceJobId:D}/1/result.json", File.ReadAllBytes(Fixture), "application/json");
            return new ReportRequested
            {
                JobId = Guid.NewGuid(),
                Attempt = 1,
                SubmittedAt = DateTimeOffset.UtcNow,
                SubmittedBy = "tests",
                Report = new ReportSpec { SourceJobId = sourceJobId, SourceAttempt = 1, StrategyName = "Fixture strategy", StrategyVersion = "v1", Formats = formats },
                BacktestResult = backtest,
            };
        }

        private static async Task Dispatch(ServiceUnderTest service, ReportRequested request)
        {
            await service.Bus.PublishAsync(OutgoingMessage.Create(Exchanges.Jobs, RoutingKeys.Dispatch(JobKind.Report),
                MessageEnvelope.Create(MessageTypes.ReportRequested, request.JobId, request.Attempt, AppIds.Orchestrator, JobKind.Report), request));
            Assert.AreEqual(1, await service.Bus.DeliverAllAsync());
        }

        private static List<InMemoryMessageBus.PublishedMessage> Events(InMemoryMessageBus bus, Guid jobId) =>
            bus.Published.Where(p => p.Exchange == Exchanges.Events && p.Envelope.Headers.JobId == jobId).ToList();

        private async Task<ServiceUnderTest> Start(IReportRenderer renderer, Dictionary<string, string> settings = null)
        {
            var bus = new InMemoryMessageBus();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            var configuration = new Dictionary<string, string>
            {
                ["Report:DataFolder"] = "/Lean/Data/",
                ["Report:WorkDirectory"] = Path.Combine(_root, "work"),
                ["Service:Name"] = "report",
                ["Report:Consume"] = "false",
            };
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                configuration[key] = value;
            }
            builder.Configuration.AddInMemoryCollection(configuration);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLeanInMemoryMessaging(bus);
            builder.AddReportService(_store, renderer, useRabbitMq: false);
            var app = builder.Build();
            app.MapReportApi();
            await app.StartAsync();
            // Subscribe the processor directly (the hosted ReportConsumer is off) so the dispatch is delivered synchronously.
            await bus.ConsumeAsync(Queues.Report, app.Services.GetRequiredService<ReportProcessor>().HandleAsync);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new ServiceUnderTest(app, bus, new HttpClient { BaseAddress = new Uri(address + "/") });
        }

        private sealed record ServiceUnderTest(WebApplication App, InMemoryMessageBus Bus, HttpClient Http) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                Http.Dispose();
                await App.StopAsync();
                await App.DisposeAsync();
            }
        }

        private sealed class FakeRenderer : IReportRenderer
        {
            public bool Fail { get; init; }
            public ReportRenderRequest Last { get; private set; }
            public string BacktestJson { get; private set; }
            public string LiveJson { get; private set; }

            public Task<ReportRenderOutput> RenderAsync(ReportRenderRequest request, CancellationToken cancellationToken)
            {
                Last = request;
                BacktestJson = File.ReadAllText(request.BacktestResultFile);
                LiveJson = request.LiveResultFile == null ? null : File.ReadAllText(request.LiveResultFile);
                if (Fail)
                {
                    throw new ReportFailedException(ErrorCode.RuntimeError, "render crashed", retryable: true, exitCode: 134);
                }
                return Task.FromResult(new ReportRenderOutput
                {
                    Html = Encoding.UTF8.GetBytes($"<html><body>{request.Spec.StrategyName}</body></html>"),
                    Pdf = request.Pdf ? Encoding.UTF8.GetBytes("%PDF-fake") : null,
                    Log = "rendered",
                    ExitCode = 0,
                });
            }
        }
    }
}
