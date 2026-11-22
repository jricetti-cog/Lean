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
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Gateway;
using QuantConnect.Services.Messaging;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace QuantConnect.Tests.Services.Gateway
{
    /// <summary>
    /// <c>GET /api/v1/jobs/{id}/events</c> on Kestrel with a fake orchestrator and an in-process
    /// <see cref="IJobEventSource"/> standing in for the per-request RabbitMQ queue.
    /// </summary>
    [TestFixture, Category("Services"), NonParallelizable]
    public class GatewayEventsTests
    {
        private const string AliceKey = "alice-events-key";
        private const string BobKey = "bob-events-key";
        private const string AdminKey = "admin-events-key";
        private static readonly Guid RunningJob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid DoneJob = Guid.Parse("33333333-3333-3333-3333-333333333333");

        private WebApplication _upstream;
        private WebApplication _gateway;
        private HttpClient _http;
        private FakeEventSource _source;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _upstream = builder.Build();
            Job JobFor(Guid id, JobStatus status) => new() { Id = id, Kind = JobKind.Backtest, Status = status, Attempt = 1, SubmittedBy = "alice", CreatedAt = DateTimeOffset.UtcNow, Progress = 0.25 };
            _upstream.MapGet("/internal/v1/jobs/{id:guid}", (Guid id) =>
                id == RunningJob ? Json(JobFor(id, JobStatus.Running))
                : id == DoneJob ? Json(JobFor(id, JobStatus.Completed))
                : HttpResults.NotFound());
            await _upstream.StartAsync();

            _source = new FakeEventSource();
            _gateway = GatewayHost.Build(Array.Empty<string>(), b =>
            {
                b.WebHost.UseUrls("http://127.0.0.1:0");
                b.Configuration.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Gateway:Upstreams:Orchestrator"] = _upstream.Urls.First(),
                    ["Gateway:ApiKeys:0:Key"] = AliceKey,
                    ["Gateway:ApiKeys:0:User"] = "alice",
                    ["Gateway:ApiKeys:1:Key"] = BobKey,
                    ["Gateway:ApiKeys:1:User"] = "bob",
                    ["Gateway:ApiKeys:2:Key"] = AdminKey,
                    ["Gateway:ApiKeys:2:User"] = "ops",
                    ["Gateway:ApiKeys:2:Admin"] = "true",
                    ["Gateway:RateLimit:PermitLimit"] = "1000",
                    ["Gateway:Events:KeepAliveSeconds"] = "1",
                    ["S3:AccessKey"] = "test-access",
                    ["S3:SecretKey"] = "test-secret",
                    ["OTEL_SDK_DISABLED"] = "true",
                });
                b.Services.AddSingleton<IJobEventSource>(_source);
            });
            await _gateway.StartAsync();
            _http = new HttpClient { BaseAddress = new Uri(_gateway.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            _http?.Dispose();
            if (_gateway != null) await _gateway.DisposeAsync();
            if (_upstream != null) await _upstream.DisposeAsync();
        }

        [SetUp]
        public void SetUp() => _source.Reset();

        [Test]
        public async Task StreamsSnapshotThenRelaysJobMessagesUntilTheTerminalStatus()
        {
            using var response = await Open($"/api/v1/jobs/{RunningJob}/events", AliceKey, "1:41");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.IsTrue(response.Headers.CacheControl?.NoCache);
            var reader = new SseReader(await response.Content.ReadAsStreamAsync());

            var snapshot = await reader.NextAsync();
            Assert.AreEqual("snapshot", snapshot.Event);
            Assert.AreEqual("1:0", snapshot.Id);
            Assert.AreEqual(JobEventsEndpoint.SnapshotType, snapshot.Data.GetProperty("type").GetString());
            Assert.AreEqual("1:41", snapshot.Data.GetProperty("lastEventId").GetString());
            Assert.AreEqual("running", snapshot.Data.GetProperty("body").GetProperty("status").GetString());
            Assert.AreEqual(RunningJob, _source.SubscribedJob);

            const string trace = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
            await _source.PublishAsync(MessageTypes.JobProgress, "progress", null, "{\"progress\":0.5}");
            await _source.PublishAsync(MessageTypes.PacketPrefix + "BacktestResult", "result", 7,
                "{\n  \"Results\": {\"Charts\": {\"Strategy Equity\": {\"Series\": {\"Equity\": {\"Values\": [[1, 100000]]}}}}}\n}", trace);
            await _source.PublishAsync(MessageTypes.PacketPrefix + "Log", "log", 8, "{\"Message\":\"hello\"}");
            await _source.PublishAsync(MessageTypes.JobHeartbeat, "heartbeat", null, "{}");
            await _source.PublishAsync(MessageTypes.PacketPrefix + "OrderEvent", "order", 9, "{\"OrderId\":1}");
            await _source.PublishAsync(MessageTypes.JobCompleted, "status", null, "{\"status\":\"completed\"}");

            var frames = await reader.ReadToEndAsync();
            CollectionAssert.AreEqual(new[] { "progress", "result", "log", "order", "status" }, frames.Select(f => f.Event).ToArray(),
                "heartbeat is excluded by default and the stream ends after the terminal status");
            var result = frames[1];
            Assert.AreEqual("1:7", result.Id);
            Assert.AreEqual(7, result.Data.GetProperty("sequence").GetInt64());
            Assert.AreEqual(trace, result.Data.GetProperty("traceparent").GetString(), "the publisher's trace context is kept");
            Assert.AreEqual(100000, result.Data.GetProperty("body").GetProperty("Results").GetProperty("Charts").GetProperty("Strategy Equity")
                .GetProperty("Series").GetProperty("Equity").GetProperty("Values")[0][1].GetInt32());
            Assert.AreEqual("hello", frames[2].Data.GetProperty("body").GetProperty("Message").GetString());
            Assert.AreEqual(RunningJob.ToString("D"), frames[4].Data.GetProperty("jobId").GetString());
            await _source.WaitDisposedAsync();
        }

        [TestCase(MessageTypes.JobCancelled, "cancelled")]
        [TestCase(MessageTypes.JobFailed, "failed")]
        public async Task CancelledOrFailedStatusEndsTheStream(string type, string status)
        {
            using var response = await Open($"/api/v1/jobs/{RunningJob}/events", AliceKey);
            var reader = new SseReader(await response.Content.ReadAsStreamAsync());
            await _source.WaitSubscribedAsync();
            await _source.PublishAsync(MessageTypes.JobProgress, "progress", null, "{\"progress\":0.5}");
            await _source.PublishAsync(type, "status", null, $"{{\"status\":\"{status}\"}}");
            await _source.PublishAsync(MessageTypes.JobProgress, "progress", null, "{\"progress\":0.6}");

            var frames = await reader.ReadToEndAsync();
            Assert.AreEqual("snapshot", frames[0].Event);
            Assert.AreEqual(type, frames[^1].Data.GetProperty("type").GetString(), "the stream ends with the terminal status");
            Assert.AreEqual(1, frames.Count(f => f.Event == "status"));
            Assert.IsFalse(frames.Any(f => f.Event == "progress" && f.Data.GetProperty("body").GetProperty("progress").GetDouble() > 0.5),
                "nothing published after the terminal status is relayed, even when latest-wins moved it into the same batch");
            await _source.WaitDisposedAsync();
        }

        [Test]
        public async Task EventsFilterSelectsTheRelayedEvents()
        {
            using var response = await Open($"/api/v1/jobs/{RunningJob}/events?events=heartbeat,status", AliceKey);
            var reader = new SseReader(await response.Content.ReadAsStreamAsync());
            await _source.WaitSubscribedAsync();
            await _source.PublishAsync(MessageTypes.JobProgress, "progress", null, "{}");
            await _source.PublishAsync(MessageTypes.JobHeartbeat, "heartbeat", null, "{}");
            await _source.PublishAsync(MessageTypes.JobCancelled, "status", null, "{}");
            var frames = await reader.ReadToEndAsync();
            CollectionAssert.AreEqual(new[] { "heartbeat", "status" }, frames.Select(f => f.Event).ToArray(), "no snapshot either: it was not requested");

            using var invalid = await Open($"/api/v1/jobs/{RunningJob}/events?events=progress,bogus", AliceKey);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        [Test]
        public async Task OtherUsersGetNotFoundAndAdminsMayWatchAnyJob()
        {
            using var bob = await Open($"/api/v1/jobs/{RunningJob}/events", BobKey);
            Assert.AreEqual(HttpStatusCode.NotFound, bob.StatusCode);
            await _source.WaitDisposedAsync();

            _source.Reset();
            using var missing = await Open($"/api/v1/jobs/{Guid.NewGuid()}/events", AliceKey);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            await _source.WaitDisposedAsync();

            _source.Reset();
            using var admin = await Open($"/api/v1/jobs/{DoneJob}/events", AdminKey);
            Assert.AreEqual(HttpStatusCode.OK, admin.StatusCode);
            var frames = await new SseReader(await admin.Content.ReadAsStreamAsync()).ReadToEndAsync();
            Assert.AreEqual("snapshot", frames.Single().Event, "a terminal job gets its snapshot and the stream ends");
            await _source.WaitDisposedAsync();
        }

        [Test]
        public async Task AcceptsAccessTokenQueryOnlyForTheEventStream()
        {
            using var anonymous = await _http.GetAsync($"/api/v1/jobs/{DoneJob}/events");
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.AreEqual(0, _source.Subscriptions, "unauthenticated requests never touch the bus");

            using var token = await _http.GetAsync($"/api/v1/jobs/{DoneJob}/events?access_token={AliceKey}", HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, token.StatusCode);

            using var elsewhere = await _http.GetAsync($"/api/v1/jobs/{DoneJob}?access_token={AliceKey}");
            Assert.AreEqual(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
        }

        [Test]
        public async Task SendsKeepAlivesAndDeletesTheQueueWhenTheClientDisconnects()
        {
            var response = await Open($"/api/v1/jobs/{RunningJob}/events", AliceKey);
            var stream = await response.Content.ReadAsStreamAsync();
            var reader = new StreamReader(stream);
            var lines = new List<string>();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (!lines.Contains(": keep-alive")) lines.Add(await reader.ReadLineAsync(timeout.Token));
            }
            Assert.AreEqual(1, _source.Active);
            response.Dispose();
            await _source.WaitDisposedAsync();
            Assert.AreEqual(0, _source.Active);
        }

        [Test]
        public async Task BusOutageIsServiceUnavailable()
        {
            _source.Fail = true;
            using var response = await Open($"/api/v1/jobs/{RunningJob}/events", AliceKey);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);
        }

        [Test]
        public void BufferIsLatestWinsForChartsAndBoundedForLogs()
        {
            var buffer = new JobEventBuffer(new EventStreamOptions { LogBuffer = 2, OrderBuffer = 2 });
            SseFrame Frame(string name, long sequence) => new(name, "t", RunningJob, 1, sequence, Encoding.UTF8.GetBytes("{}"));
            buffer.Add(Frame("result", 1));
            buffer.Add(Frame("log", 2));
            buffer.Add(Frame("progress", 3));
            buffer.Add(Frame("result", 4));
            buffer.Add(Frame("log", 5));
            buffer.Add(Frame("log", 6));
            buffer.Add(Frame("result", 7));
            buffer.Add(Frame("status", 8));
            buffer.Add(Frame("log", 9));

            var frames = buffer.TakeAsync(TimeSpan.Zero, CancellationToken.None).Result;
            CollectionAssert.AreEqual(new[] { "progress:3:0", "log:6:1", "result:7:2", "status:8:0", "log:9:1" },
                frames.Select(f => $"{f.Event}:{f.Sequence}:{f.Dropped}").ToArray());
            Assert.AreEqual(0, buffer.Count);

            buffer.Add(Frame("result", 10));
            Assert.AreEqual(0, buffer.TakeAsync(TimeSpan.Zero, CancellationToken.None).Result.Single().Dropped, "drops are only reported once");
        }

        [Test]
        public void FormatsOneLineDataFrames()
        {
            var frame = new SseFrame("log", MessageTypes.PacketPrefix + "Log", RunningJob, 2, 12, Encoding.UTF8.GetBytes("{\n \"Message\": \"a\\nb\"\n}"), Dropped: 3);
            var text = Encoding.UTF8.GetString(JobEventsEndpoint.Format(frame));
            StringAssert.StartsWith("id: 2:12\nevent: log\ndata: {", text);
            StringAssert.EndsWith("}\n\n", text);
            Assert.AreEqual(5, text.Split('\n').Length, "id, event, one data line, then the blank line that ends the frame");
            using var data = JsonDocument.Parse(text.Split('\n')[2]["data: ".Length..]);
            Assert.AreEqual(3, data.RootElement.GetProperty("dropped").GetInt32());
            Assert.AreEqual("a\nb", data.RootElement.GetProperty("body").GetProperty("Message").GetString());
        }

        [Test]
        public void PerRequestQueueIsServerNamedAndBoundToEveryEventOfTheJob()
        {
            var a = RabbitMqJobEventSource.QueueName("api-gateway-1");
            var b = RabbitMqJobEventSource.QueueName("api-gateway-1");
            StringAssert.StartsWith("sse.api-gateway-1.", a, "matches the sse.* queue policy (64 MiB, drop-head)");
            Assert.AreNotEqual(a, b);
            var binding = RabbitMqJobEventSource.BindingKey(RunningJob);
            Assert.AreEqual($"job.*.{RunningJob:D}.#", binding);
            foreach (var segment in new[] { "status", "heartbeat", "progress", "result", "log", "order", "error" })
            {
                Assert.IsTrue(RoutingKeys.TopicMatches(binding, RoutingKeys.Event(JobKind.Backtest, RunningJob, segment)), segment);
                Assert.IsTrue(RoutingKeys.TopicMatches(binding, RoutingKeys.Event(JobKind.Live, RunningJob, segment)), segment);
            }
            Assert.IsFalse(RoutingKeys.TopicMatches(binding, RoutingKeys.Event(JobKind.Backtest, DoneJob, "progress")));
        }

        private async Task<HttpResponseMessage> Open(string path, string key, string lastEventId = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (lastEventId != null) request.Headers.Add("Last-Event-ID", lastEventId);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        private static IResult Json<T>(T value) => HttpResults.Text(ContractJson.Serialize(value), "application/json");

        private sealed record Frame(string Id, string Event, JsonElement Data);

        private sealed class SseReader(Stream stream)
        {
            private readonly StreamReader _reader = new(stream);

            public async Task<Frame> NextAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                string id = null, name = null, data = null;
                while (await _reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (data != null) return new Frame(id, name, JsonDocument.Parse(data).RootElement.Clone());
                        continue;
                    }
                    if (line.StartsWith(':')) continue;
                    if (line.StartsWith("id: ")) id = line[4..];
                    else if (line.StartsWith("event: ")) name = line[7..];
                    else if (line.StartsWith("data: ")) data = line[6..];
                }
                return null;
            }

            public async Task<List<Frame>> ReadToEndAsync()
            {
                var frames = new List<Frame>();
                while (await NextAsync() is { } frame) frames.Add(frame);
                return frames;
            }
        }

        private sealed class FakeEventSource : IJobEventSource
        {
            private readonly object _lock = new();
            private Func<JobBusEvent, ValueTask> _handler;
            private TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Fail;
            public Guid SubscribedJob;
            public int Subscriptions;
            public int Active;

            public void Reset()
            {
                lock (_lock)
                {
                    _handler = null;
                    Fail = false;
                    Subscriptions = 0;
                    _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            public Task<IAsyncDisposable> SubscribeAsync(Guid jobId, Func<JobBusEvent, ValueTask> onEvent, CancellationToken cancellationToken)
            {
                if (Fail) throw new InvalidOperationException("broker unreachable");
                lock (_lock)
                {
                    SubscribedJob = jobId;
                    _handler = onEvent;
                    Subscriptions++;
                    Interlocked.Increment(ref Active);
                    _subscribed.TrySetResult();
                    var disposed = _disposed;
                    return Task.FromResult<IAsyncDisposable>(new Subscription(() =>
                    {
                        Interlocked.Decrement(ref Active);
                        disposed.TrySetResult();
                    }));
                }
            }

            public Task WaitSubscribedAsync() => _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            public Task WaitDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            public async Task PublishAsync(string type, string segment, long? sequence, string body, string traceParent = null)
            {
                await WaitSubscribedAsync();
                var headers = new MessageHeaders { JobId = SubscribedJob, Attempt = 1, Sequence = sequence, TraceParent = traceParent };
                await _handler(new JobBusEvent(type, RoutingKeys.Event(JobKind.Backtest, SubscribedJob, segment), headers, Encoding.UTF8.GetBytes(body)));
            }

            private sealed class Subscription(Action dispose) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }
    }
}
