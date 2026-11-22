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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Results;
using QuantConnect.Services.Results.Storage;

namespace QuantConnect.Tests.Services.Results
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    /// <summary>
    /// Results service against a real PostgreSQL (set <c>LEAN_TEST_RESULTS_POSTGRES</c> to an admin connection string;
    /// each fixture creates and drops its own database) with a file-system object store standing in for MinIO.
    /// </summary>
    [TestFixture, Category("Services"), NonParallelizable]
    public class ResultsServiceTests
    {
        private const string PostgresVariable = "LEAN_TEST_RESULTS_POSTGRES";
        private const string Hash = "6a7e4c1f3b2d9e8a";
        private string _adminConnection;
        private string _database;
        private string _root;
        private WebApplication _app;
        private HttpClient _http;
        private ResultsIngestor _ingestor;
        private FileSystemResultObjectStore _store;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _adminConnection = Environment.GetEnvironmentVariable(PostgresVariable);
            if (string.IsNullOrEmpty(_adminConnection))
            {
                Assert.Ignore($"{PostgresVariable} is not set: no PostgreSQL to run the results service against");
            }
            _database = "results_test_" + Guid.NewGuid().ToString("N")[..12];
            await using (var admin = new NpgsqlConnection(_adminConnection))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE {_database}", admin);
                await create.ExecuteNonQueryAsync();
            }

            _root = Path.Combine(Path.GetTempPath(), "lean-results-" + Guid.NewGuid().ToString("N"));
            _store = new FileSystemResultObjectStore(_root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
            {
                ["ConnectionStrings:Results"] = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _database }.ConnectionString,
                ["Results:Consume"] = "false",
                ["Results:ChartMaxPoints"] = "500",
                ["Results:MaxLogsPerAttempt"] = "5",
                ["Service:Name"] = "results",
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.AddResultsService(_store, useRabbitMq: false);
            _app = builder.Build();
            await _app.Services.GetRequiredService<ResultsDatabase>().MigrateAsync();
            // Migrations are idempotent: a second replica starting concurrently/after applies nothing.
            await _app.Services.GetRequiredService<ResultsDatabase>().MigrateAsync();
            _app.MapResultsApi();
            await _app.StartAsync();
            var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _http = new HttpClient { BaseAddress = new Uri(address) };
            _ingestor = _app.Services.GetRequiredService<ResultsIngestor>();
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            if (_app == null)
            {
                return;
            }
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using (var admin = new NpgsqlConnection(_adminConnection))
            {
                await admin.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
            Directory.Delete(_root, true);
        }

        [Test]
        public async Task PersistsPacketsAndServesTheResultsApi()
        {
            var job = Guid.NewGuid();
            var messages = BacktestRun(job, 1, orders: 3);
            foreach (var message in messages)
            {
                Assert.IsTrue(await _ingestor.IngestAsync(message), message.Envelope.Type);
            }

            var summary = await GetJson($"/internal/v1/results/{job:D}");
            Assert.AreEqual("completed", (string)summary["status"]);
            Assert.AreEqual("backtest", (string)summary["kind"]);
            Assert.AreEqual(1d, (double)summary["progress"]);
            Assert.AreEqual(3, (long)summary["orderCount"]);
            Assert.AreEqual("algo-" + job.ToString("N"), (string)summary["algorithmId"]);
            Assert.AreEqual(Hash, (string)summary["statistics"]["OrderListHash"], "OrderListHash from the final packet");
            Assert.AreEqual("3", (string)summary["statistics"]["Total Orders"]);
            Assert.AreEqual("$101,000.00", (string)summary["runtimeStatistics"]["Equity"]);
            Assert.AreEqual($"{job:D}/1/algo-{job:N}.json", (string)summary["result"]["key"]);
            Assert.AreEqual(Hash, await Scalar<string>("SELECT order_list_hash FROM results WHERE job_id = @job", job));

            var orders = await GetJson($"/internal/v1/results/{job:D}/orders");
            Assert.AreEqual(3, orders["items"].AsArray().Count);
            Assert.AreEqual(3, (int)orders["items"][0]["status"], "final state (Filled) of order 1");
            Assert.AreEqual("SPY", (string)orders["items"][0]["symbol"]["value"]);
            Assert.IsNull(orders["next"]);

            var events = await GetJson($"/internal/v1/results/{job:D}/order-events?limit=500");
            Assert.AreEqual(6, events["items"].AsArray().Count, "submitted + filled per order, inline and final copies merged");
            CollectionAssert.IsOrdered(events["items"].AsArray().Select(e => (string)e["utcTime"]).ToList());

            var logs = await GetJson($"/internal/v1/results/{job:D}/logs");
            CollectionAssert.AreEqual(new[] { "Log", "Debug", "RuntimeError" }, logs["items"].AsArray().Select(l => (string)l["type"]).ToArray());
            Assert.AreEqual("at Algorithm.OnData()", (string)logs["items"][2]["stackTrace"]);
            var debugOnly = await GetJson($"/internal/v1/results/{job:D}/logs?level=Debug");
            Assert.AreEqual(1, debugOnly["items"].AsArray().Count);

            var charts = await GetJson($"/internal/v1/results/{job:D}/charts");
            Assert.AreEqual("Strategy Equity", (string)charts[0]["name"]);
            CollectionAssert.AreEqual(new[] { "Equity" }, charts[0]["series"].AsArray().Select(s => (string)s).ToArray());

            var listed = await GetJson($"/internal/v1/results?jobId={job:D}");
            Assert.AreEqual(1, listed["items"].AsArray().Count);
            Assert.AreEqual(job.ToString("D"), (string)listed["items"][0]["jobId"]);

            var artifacts = await GetJson($"/internal/v1/results/{job:D}/artifacts");
            Assert.AreEqual($"{job:D}/1/algo-{job:N}.json", (string)artifacts[0]["key"]);
        }

        [Test]
        public async Task RedeliveryNeverDuplicatesOrdersEventsOrLogs()
        {
            var job = Guid.NewGuid();
            var messages = BacktestRun(job, 1, orders: 3);
            foreach (var message in messages)
            {
                await _ingestor.IngestAsync(message);
            }
            var before = await Counts(job);

            // Broker redelivery of every message (crash between commit and ack) ...
            foreach (var message in messages)
            {
                Assert.IsFalse(await _ingestor.IngestAsync(message with { Redelivered = true }), message.Envelope.Type);
            }
            // ... and an engine-side republish of the same packets under new message ids: the sequence is the key.
            foreach (var message in messages.Where(m => m.Envelope.Headers.Sequence != null))
            {
                var republished = message with { Envelope = message.Envelope with { MessageId = Guid.NewGuid() } };
                Assert.AreEqual(MessageDisposition.Ack, await _ingestor.HandleAsync(republished, default));
            }

            Assert.AreEqual(before, await Counts(job));
            Assert.AreEqual((3L, 6L, 3L), before);
            Assert.AreEqual(Hash, await Scalar<string>("SELECT order_list_hash FROM results WHERE job_id = @job", job));
        }

        [Test]
        public async Task StalePartialAfterTheFinalResultChangesNothing()
        {
            var job = Guid.NewGuid();
            var messages = BacktestRun(job, 1, orders: 2);
            var partial = messages.First(m => m.Envelope.Type == "packets.v1.BacktestResult");
            foreach (var message in messages.Where(m => m != partial))
            {
                await _ingestor.IngestAsync(message);
            }
            // The first partial arrives last (reordered after a requeue): older sequence, must not regress the result.
            Assert.IsTrue(await _ingestor.IngestAsync(partial));

            var summary = await GetJson($"/internal/v1/results/{job:D}");
            Assert.AreEqual(Hash, (string)summary["statistics"]["OrderListHash"]);
            Assert.AreEqual("$101,000.00", (string)summary["runtimeStatistics"]["Equity"]);
            var orders = await GetJson($"/internal/v1/results/{job:D}/orders");
            Assert.IsTrue(orders["items"].AsArray().All(o => (int)o["status"] == 3), "orders keep their final (Filled) state");
        }

        [Test]
        public async Task PagesOrdersAndLogsWithCursorsAndCapsLogs()
        {
            var job = Guid.NewGuid();
            foreach (var message in BacktestRun(job, 1, orders: 7, extraLogs: 4))
            {
                await _ingestor.IngestAsync(message);
            }

            var ids = new List<int>();
            string cursor = null;
            var pages = 0;
            do
            {
                var page = await GetJson($"/internal/v1/results/{job:D}/orders?limit=3" + (cursor == null ? "" : $"&cursor={cursor}"));
                ids.AddRange(page["items"].AsArray().Select(o => (int)o["id"]));
                cursor = (string)page["next"];
                pages++;
            } while (cursor != null);
            CollectionAssert.AreEqual(Enumerable.Range(1, 7).ToArray(), ids);
            Assert.AreEqual(3, pages);

            var eventIds = new List<string>();
            cursor = null;
            do
            {
                var page = await GetJson($"/internal/v1/results/{job:D}/order-events?limit=4" + (cursor == null ? "" : $"&cursor={cursor}"));
                eventIds.AddRange(page["items"].AsArray().Select(e => $"{e["orderId"]}-{e["id"]}"));
                cursor = (string)page["next"];
            } while (cursor != null);
            Assert.AreEqual(14, eventIds.Count);
            Assert.AreEqual(14, eventIds.Distinct().Count());

            // 3 logs + 4 extra, capped at the 5 most recent.
            var logs = await GetJson($"/internal/v1/results/{job:D}/logs?limit=2");
            Assert.AreEqual(2, logs["items"].AsArray().Count);
            Assert.IsNotNull(logs["next"]);
            Assert.AreEqual(5L, await Scalar<long>("SELECT count(*) FROM logs WHERE job_id = @job", job));

            Assert.AreEqual(HttpStatusCode.BadRequest, (await _http.GetAsync($"/internal/v1/results/{job:D}/orders?cursor=not-a-cursor")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await _http.GetAsync($"/internal/v1/results/{job:D}/logs?level=Trace")).StatusCode);
            var missing = await _http.GetAsync($"/internal/v1/results/{Guid.NewGuid():D}/orders");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.AreEqual("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        }

        [Test]
        public async Task DownsamplesChartsAndServesFullResolutionFromTheResultJson()
        {
            var job = Guid.NewGuid();
            foreach (var message in BacktestRun(job, 1, orders: 1, chartPoints: 3_000))
            {
                await _ingestor.IngestAsync(message);
            }

            Assert.AreEqual(3_000, await Scalar<int>("SELECT point_count FROM charts WHERE job_id = @job", job));
            Assert.AreEqual(500, await Scalar<int>("SELECT jsonb_array_length(body -> 'series' -> 'Equity' -> 'values') FROM charts WHERE job_id = @job", job));

            var small = await GetJson($"/internal/v1/results/{job:D}/charts/Strategy%20Equity?maxPoints=100");
            var smallValues = small["series"]["Equity"]["values"].AsArray();
            Assert.AreEqual(100, smallValues.Count);
            Assert.AreEqual(Start, (long)smallValues[0][0]);
            Assert.AreEqual(Start + 2_999 * 60, (long)smallValues[^1][0]);
            Assert.AreEqual("Strategy Equity", (string)small["name"]);

            var full = await GetJson($"/internal/v1/results/{job:D}/charts/Strategy%20Equity");
            Assert.AreEqual(3_000, full["series"]["Equity"]["values"].AsArray().Count, "full resolution from MinIO");

            Assert.AreEqual(HttpStatusCode.NotFound, (await _http.GetAsync($"/internal/v1/results/{job:D}/charts/Nope")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await _http.GetAsync($"/internal/v1/results/{job:D}/charts/Strategy%20Equity?maxPoints=2")).StatusCode);
        }

        [Test]
        public async Task JobCompletedIndexesTheResultWhenTheFinalPacketRefWasMissed()
        {
            var job = Guid.NewGuid();
            var messages = BacktestRun(job, 1, orders: 2);
            foreach (var message in messages.Where(m => m.Envelope.Type != MessageTypes.PacketRef))
            {
                await _ingestor.IngestAsync(message);
            }

            var summary = await GetJson($"/internal/v1/results/{job:D}");
            Assert.AreEqual("completed", (string)summary["status"]);
            Assert.AreEqual(Hash, (string)summary["statistics"]["OrderListHash"], "from job.completed statistics");
            Assert.AreEqual(2, (long)summary["orderCount"]);
            Assert.AreEqual(3_0, (await GetJson($"/internal/v1/results/{job:D}/charts/Strategy%20Equity"))["series"]["Equity"]["values"].AsArray().Count);
        }

        [Test]
        public async Task InlineFinalPacketIsStoredInMinioAndAttemptsAreSelectable()
        {
            var job = Guid.NewGuid();
            foreach (var message in BacktestRun(job, 1, orders: 1).Take(2))
            {
                await _ingestor.IngestAsync(message);
            }
            await _ingestor.IngestAsync(Message(MessageTypes.JobFailed, job, 1, null, new JobFailed
            {
                JobId = job, Kind = JobKind.Backtest, Attempt = 1, FailedAt = DateTimeOffset.UtcNow, Final = false,
                Error = new ErrorInfo { Code = ErrorCode.LeaseLost, Message = "worker lost" },
            }));

            var final = PacketJson(job, 1, 1m, Orders(1, 3), Events(1, 1, 3, 0), Charts(10), new Dictionary<string, string> { ["Total Orders"] = "1", ["OrderListHash"] = Hash });
            Assert.IsTrue(await _ingestor.IngestAsync(Message("packets.v1.BacktestResult", job, 2, 1, final)));

            var latest = await GetJson($"/internal/v1/results/{job:D}");
            Assert.AreEqual(2, (int)latest["attempt"]);
            Assert.AreEqual(Hash, (string)latest["statistics"]["OrderListHash"]);
            Assert.AreEqual($"{job:D}/2/algo-{job:N}-final-packet.json", (string)latest["result"]["key"]);
            var stored = JsonNode.Parse(await _store.GetAsync(Buckets.Results, (string)latest["result"]["key"]));
            Assert.AreEqual(1d, (double)stored["Progress"], "the full final BacktestResultPacket JSON");

            var first = await GetJson($"/internal/v1/results/{job:D}?attempt=1");
            Assert.AreEqual("failed", (string)first["status"]);
            Assert.AreEqual(HttpStatusCode.NotFound, (await _http.GetAsync($"/internal/v1/results/{job:D}?attempt=9")).StatusCode);

            var artifacts = await GetJson($"/internal/v1/results/{job:D}/artifacts?attempt=2");
            Assert.AreEqual((string)latest["result"]["sha256"], (string)artifacts[0]["sha256"]);
        }

        [Test]
        public async Task ConsumesFromTheBusAndDeadLettersPoison()
        {
            var bus = new InMemoryMessageBus();
            await using var subscription = await bus.ConsumeAsync(Queues.ResultsPackets, _ingestor.HandleAsync);
            var job = Guid.NewGuid();
            var messages = BacktestRun(job, 1, orders: 2);
            foreach (var message in messages)
            {
                await bus.PublishAsync(new OutgoingMessage
                {
                    Exchange = message.Exchange,
                    RoutingKey = message.RoutingKey,
                    Envelope = message.Envelope,
                    Body = message.Body,
                });
            }
            Assert.AreEqual(messages.Count, await bus.DeliverAllAsync());
            Assert.AreEqual(2L, await Scalar<long>("SELECT order_count FROM results WHERE job_id = @job", job));

            var poison = Message("packets.v1.Log", job, 1, 99, null) with { Body = Encoding.UTF8.GetBytes("{not json") };
            Assert.AreEqual(MessageDisposition.DeadLetter, await _ingestor.HandleAsync(poison, default));
        }

        private const long Start = 1_380_000_000;

        /// <summary>
        /// A backtest attempt as BusMessagingHandler publishes it: job.started, a partial result, an order event, logs,
        /// a second partial, the final PacketRef (result file in MinIO, statistics with OrderListHash) and job.completed.
        /// </summary>
        private List<ReceivedMessage> BacktestRun(Guid job, int attempt, int orders, int extraLogs = 0, int chartPoints = 30)
        {
            var messages = new List<ReceivedMessage>();
            long sequence = 0;
            messages.Add(Message(MessageTypes.JobStarted, job, attempt, null, new JobStarted
            {
                JobId = job, Kind = JobKind.Backtest, Attempt = attempt, StartedAt = DateTimeOffset.UtcNow, WorkerId = "worker-1",
                Results = ResultsPrefix.For(job, attempt),
            }));
            messages.Add(Message("packets.v1.BacktestResult", job, attempt, ++sequence,
                PacketJson(job, attempt, 0.5m, Orders(orders, 1), Events(orders, 1, 1, 0), Charts(chartPoints / 2), new Dictionary<string, string>(),
                    new Dictionary<string, string> { ["Equity"] = "$100,500.00" })));
            messages.Add(Message("packets.v1.OrderEvent", job, attempt, ++sequence, new JsonObject
            {
                ["Event"] = Events(1, 1, 3, 1)[0]!.DeepClone(), ["AlgorithmId"] = "algo", ["Type"] = "OrderEvent",
            }));
            messages.Add(Message("packets.v1.Log", job, attempt, ++sequence, new JsonObject { ["Message"] = "hello", ["Type"] = "Log" }));
            messages.Add(Message("packets.v1.Debug", job, attempt, ++sequence, new JsonObject { ["Message"] = "debug", ["Type"] = "Debug" }));
            messages.Add(Message("packets.v1.RuntimeError", job, attempt, ++sequence,
                new JsonObject { ["Message"] = "boom", ["StackTrace"] = "at Algorithm.OnData()", ["Type"] = "RuntimeError" }));
            for (var i = 0; i < extraLogs; i++)
            {
                messages.Add(Message("packets.v1.Log", job, attempt, ++sequence, new JsonObject { ["Message"] = $"extra {i}", ["Type"] = "Log" }));
            }
            messages.Add(Message("packets.v1.BacktestResult", job, attempt, ++sequence,
                PacketJson(job, attempt, 0.9m, Orders(orders, 3), Events(orders, 2, 3, 1), Charts(chartPoints, chartPoints / 2), new Dictionary<string, string>(),
                    new Dictionary<string, string> { ["Equity"] = "$100,900.00" })));

            // Final: the engine's camelCase result file in MinIO (without OrderListHash, which Messaging.Send adds to the
            // packet afterwards) and the PacketRef carrying the packet's statistics.
            var statistics = new Dictionary<string, string> { ["Total Orders"] = orders.ToString(), ["Net Profit"] = "1.000%" };
            var resultFile = new JsonObject
            {
                ["charts"] = CamelCharts(Charts(chartPoints)),
                ["orders"] = LeanJson.ToCamelCase(Orders(orders, 3)),
                ["orderEvents"] = LeanJson.ToCamelCase(Events(orders, 1, 1, 0).Concat(Events(orders, 2, 3, 1)).ToJsonArray()),
                ["statistics"] = JsonSerializer.SerializeToNode(statistics),
                ["runtimeStatistics"] = new JsonObject { ["Equity"] = "$101,000.00" },
                ["algorithmConfiguration"] = new JsonObject { ["name"] = "BasicTemplateAlgorithm" },
            };
            var key = $"{job:D}/{attempt}/algo-{job:N}.json";
            var resultRef = _store.PutRefAsync(Buckets.Results, key, Encoding.UTF8.GetBytes(resultFile.ToJsonString()), "application/json").GetAwaiter().GetResult();
            var packetStatistics = new Dictionary<string, string>(statistics) { ["OrderListHash"] = Hash };
            messages.Add(Message(MessageTypes.PacketRef, job, attempt, ++sequence, new PacketRef
            {
                PacketType = "BacktestResult", JobId = job, Attempt = attempt, AlgorithmId = $"algo-{job:N}", Final = true, Progress = 1,
                Object = resultRef, Format = PacketRef.FinalFormat, Statistics = packetStatistics,
            }));
            messages.Add(Message(MessageTypes.JobCompleted, job, attempt, null, new JobCompleted
            {
                JobId = job, Kind = JobKind.Backtest, Attempt = attempt, CompletedAt = DateTimeOffset.UtcNow, Result = resultRef,
                Artifacts = [resultRef], Statistics = packetStatistics, AlgorithmId = $"algo-{job:N}", ExitCode = 0,
            }));
            return messages;
        }

        private static JsonObject PacketJson(Guid job, int attempt, decimal progress, JsonObject orders, JsonArray events, JsonObject charts,
            Dictionary<string, string> statistics, Dictionary<string, string> runtime = null) => new()
        {
            ["Type"] = "BacktestResult",
            ["BacktestId"] = $"algo-{job:N}",
            ["Progress"] = progress,
            ["Results"] = new JsonObject
            {
                ["Charts"] = charts,
                ["Orders"] = orders,
                ["OrderEvents"] = events,
                ["Statistics"] = JsonSerializer.SerializeToNode(statistics),
                ["RuntimeStatistics"] = JsonSerializer.SerializeToNode(runtime ?? new Dictionary<string, string> { ["Equity"] = "$101,000.00" }),
            },
        };

        private static JsonObject Orders(int count, int status)
        {
            var orders = new JsonObject();
            for (var id = 1; id <= count; id++)
            {
                orders[id.ToString()] = new JsonObject
                {
                    ["Id"] = id, ["Status"] = status, ["Quantity"] = 10 * id, ["Type"] = 0,
                    ["Symbol"] = new JsonObject { ["value"] = "SPY", ["id"] = "SPY R735QTJ8XC9X", ["permtick"] = "SPY" },
                    ["Time"] = $"2013-10-07T13:{30 + id:00}:00Z",
                };
            }
            return orders;
        }

        private static JsonArray Events(int orders, int eventId, int status, int minuteOffset) =>
            Enumerable.Range(1, orders).Select(id => (JsonNode)new JsonObject
            {
                ["OrderId"] = id, ["Id"] = eventId, ["Status"] = status, ["FillQuantity"] = status == 3 ? 10 * id : 0,
                ["UtcTime"] = $"2013-10-07T14:{id:00}:{minuteOffset:00}Z",
            }).ToJsonArray();

        private static JsonObject CamelCharts(JsonObject charts) =>
            new(charts.Select(c => KeyValuePair.Create(c.Key, (JsonNode)LeanJson.NormalizeChart(c.Key, (JsonObject)c.Value))));

        private static JsonObject Charts(int points, int from = 0) => new()
        {
            ["Strategy Equity"] = ResultsJsonTests.Chart(new JsonArray(Enumerable.Range(from, points - from)
                .Select(i => (JsonNode)new JsonArray(Start + i * 60, 100_000 + i)).ToArray())),
        };

        private static ReceivedMessage Message(string type, Guid job, int attempt, long? sequence, object body)
        {
            var packetType = type.StartsWith(MessageTypes.PacketPrefix, StringComparison.Ordinal) ? type[MessageTypes.PacketPrefix.Length..] : null;
            var bytes = body switch
            {
                null => Array.Empty<byte>(),
                JsonNode node => Encoding.UTF8.GetBytes(node.ToJsonString()),
                _ => ContractJson.SerializeToUtf8Bytes(body),
            };
            return new ReceivedMessage
            {
                Queue = Queues.ResultsPackets,
                Exchange = Exchanges.Events,
                RoutingKey = RoutingKeys.EventFor(type, JobKind.Backtest, job),
                Envelope = MessageEnvelope.Create(type, job, attempt, AppIds.BacktestWorker, JobKind.Backtest, packetType: packetType, sequence: sequence),
                Body = bytes,
            };
        }

        private async Task<JsonNode> GetJson(string path)
        {
            var response = await _http.GetAsync(path);
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{path}: {text}");
            return JsonNode.Parse(text);
        }

        private async Task<(long Orders, long Events, long Logs)> Counts(Guid job) => (
            await Scalar<long>("SELECT count(*) FROM orders WHERE job_id = @job", job),
            await Scalar<long>("SELECT count(*) FROM order_events WHERE job_id = @job", job),
            await Scalar<long>("SELECT count(*) FROM logs WHERE job_id = @job", job));

        private async Task<T> Scalar<T>(string sql, Guid job)
        {
            var dataSource = _app.Services.GetRequiredService<NpgsqlDataSource>();
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("job", job);
            return (T)await command.ExecuteScalarAsync();
        }
    }

    internal static class JsonTestExtensions
    {
        public static JsonArray ToJsonArray(this IEnumerable<JsonNode> nodes) => new(nodes.Select(n => n?.DeepClone()).ToArray());
    }
}
