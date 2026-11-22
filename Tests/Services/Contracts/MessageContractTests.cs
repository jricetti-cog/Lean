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
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Tests.Services.Contracts
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;

    [TestFixture, Category("Services")]
    public class MessageContractTests
    {
        private static readonly Guid JobId = Guid.Parse("3f2b8c1e-6a4d-4e5f-9b7a-1c2d3e4f5a6b");
        private static readonly Guid ChildId = Guid.Parse("7d9e0f1a-2b3c-4d5e-8f6a-7b8c9d0e1f2a");
        private static readonly DateTimeOffset At = new(2026, 11, 22, 13, 30, 0, 123, TimeSpan.Zero);
        private static readonly string Sha = new string('a', 64);

        private static IEnumerable<string> Examples() => ContractSchemas.ExampleFiles();

        [TestCaseSource(nameof(Examples))]
        public void ExampleEnvelopeRoundTripsAndMatchesSchema(string file)
        {
            var example = ContractSchemas.ReadExample(file);
            var original = example.GetProperty("envelope");

            var envelope = original.Deserialize<MessageEnvelope>(ContractJson.Options);
            var roundTripped = JsonSerializer.SerializeToElement(envelope, ContractJson.Options);

            ContractSchemas.AssertValid(ContractSchemas.ValidateMessage("envelope.schema.json", JsonNode.Parse(roundTripped.GetRawText())), file + " envelope");
            Assert.IsTrue(JsonElement.DeepEquals(original, roundTripped), $"{file}: {original}{Environment.NewLine}!={Environment.NewLine}{roundTripped}");
        }

        [TestCaseSource(nameof(Examples))]
        public void ExampleBodyRoundTripsThroughItsRecordAndMatchesSchema(string file)
        {
            var example = ContractSchemas.ReadExample(file);
            var type = example.GetProperty("envelope").GetProperty("type").GetString();
            var original = example.GetProperty("body");
            var schemaFile = MessageTypes.SchemaFile(type);
            Assert.AreEqual(example.GetProperty("schema").GetString() + ".schema.json", schemaFile, "catalogue schema for " + type);

            var bodyType = MessageTypes.BodyType(type);
            var roundTripped = original;
            if (bodyType != null)
            {
                var record = original.Deserialize(bodyType, ContractJson.Options);
                Assert.IsInstanceOf(bodyType, record);
                roundTripped = JsonSerializer.SerializeToElement(record, bodyType, ContractJson.Options);
                Assert.IsTrue(JsonElement.DeepEquals(original, roundTripped), $"{file}: {original}{Environment.NewLine}!={Environment.NewLine}{roundTripped}");
            }
            ContractSchemas.AssertValid(ContractSchemas.ValidateMessage(schemaFile, JsonNode.Parse(roundTripped.GetRawText())), file + " body");
        }

        [TestCaseSource(nameof(Examples))]
        public void ExampleRoutingMatchesRoutingKeyBuilders(string file)
        {
            var example = ContractSchemas.ReadExample(file);
            var envelope = example.GetProperty("envelope").Deserialize<MessageEnvelope>(ContractJson.Options);
            var exchange = example.GetProperty("exchange").GetString();
            var routingKey = example.GetProperty("routingKey").GetString();

            Assert.IsTrue(RoutingKeys.IsV1(routingKey), routingKey);
            if (exchange == Exchanges.Jobs)
            {
                Assert.AreEqual(routingKey, RoutingKeys.DispatchFor(envelope.Type));
            }
            else
            {
                Assert.AreEqual(Exchanges.Events, exchange);
                Assert.AreEqual(routingKey, RoutingKeys.EventFor(envelope.Type, envelope.Headers.JobKind!.Value, envelope.CorrelationId));
            }
            Assert.AreEqual(MessageTypes.IsTransient(envelope.Type) ? MessageEnvelope.Transient : MessageEnvelope.Persistent, envelope.DeliveryMode, envelope.Type);
        }

        [Test]
        public void CatalogueCoversEveryEnvelopeType()
        {
            var schema = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(ContractSchemas.MessagesDirectory, "envelope.schema.json")));
            var types = schema["properties"]!["type"]!["enum"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
            CollectionAssert.AreEquivalent(types, MessageTypes.All);
        }

        private static IEnumerable<TestCaseData> FullyPopulatedBodies()
        {
            var results = ResultsPrefix.For(JobId, 2);
            var obj = new ObjectRef { Bucket = Buckets.Results, Key = $"{JobId}/2/{JobId}.json", Size = 1234, Sha256 = Sha, ContentType = "application/json" };
            var upload = new UploadedAlgorithmRef
            {
                Language = AlgorithmLanguage.Python,
                Object = obj with { Bucket = Buckets.Algorithms, Key = "uploads/main.py" },
                TypeName = "MyAlgorithm",
            };
            var backtest = new BacktestSpec
            {
                Name = "bt",
                Algorithm = new BuiltinAlgorithmRef { TypeName = "BasicTemplateAlgorithm" },
                Parameters = new() { ["fast"] = "10" },
                StartDate = new DateOnly(2013, 10, 7),
                EndDate = new DateOnly(2013, 10, 11),
                Cash = 100000m,
                FetchMissingData = true,
                MaxRuntimeSeconds = 600,
            };
            var error = new ErrorInfo { Code = ErrorCode.RuntimeError, Message = "boom", StackTrace = "at X", ExitCode = 1 };

            yield return Case("job.submitted.schema.json", new JobSubmitted
            {
                JobId = JobId, Kind = JobKind.Backtest, Attempt = 2, SubmittedAt = At, SubmittedBy = "alice",
                ParentJobId = ChildId, Deadline = At.AddHours(1), Results = results, Backtest = backtest,
            }, "backtest");
            yield return Case("job.submitted.schema.json", new JobSubmitted
            {
                JobId = JobId, Kind = JobKind.Live, Attempt = 1, SubmittedAt = At, SubmittedBy = "alice", Results = results,
                Live = new LiveSpec
                {
                    Name = "live", Algorithm = upload, Parameters = new() { ["a"] = "b" }, Brokerage = "PaperBrokerage",
                    DataQueueHandlers = new() { "MarketDataServiceQueueHandler" }, RedeployOnStale = true,
                },
            }, "live");
            yield return Case("optimization.requested.schema.json", new OptimizationRequested
            {
                JobId = JobId, Attempt = 1, SubmittedAt = At, SubmittedBy = "alice", Results = results,
                Optimization = new OptimizationSpec
                {
                    Name = "opt", Backtest = backtest, Strategy = "QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy",
                    StrategySettings = JsonDocument.Parse("{\"$type\":\"QuantConnect.Optimizer.Strategies.StepBaseOptimizationStrategySettings, QuantConnect.Optimizer\"}").RootElement,
                    Criterion = new OptimizationCriterion { Target = "TotalPerformance.PortfolioStatistics.SharpeRatio", Extremum = Extremum.Max, TargetValue = 2m },
                    Constraints = new() { new OptimizationConstraint { Target = "TotalPerformance.PortfolioStatistics.Drawdown", Operator = ConstraintOperator.Less, TargetValue = 0.2m } },
                    Parameters = new()
                    {
                        new OptimizationParameter { Name = "fast", Min = 5, Max = 20, Step = 5, MinStep = 1 },
                        new OptimizationParameter { Name = "symbol", Value = "SPY" },
                    },
                    MaximumConcurrentBacktests = 4,
                },
            }, "optimization");
            yield return Case("report.requested.schema.json", new ReportRequested
            {
                JobId = ChildId, Attempt = 1, SubmittedAt = At, SubmittedBy = "alice", BacktestResult = obj, LiveResult = obj,
                Report = new ReportSpec
                {
                    SourceJobId = JobId, SourceAttempt = 2, LiveJobId = JobId, StrategyName = "s", StrategyDescription = "d",
                    StrategyVersion = "1", Formats = new() { ReportFormat.Html, ReportFormat.Pdf },
                },
            }, "report");
            yield return Case("marketdata.fetch.requested.schema.json", new MarketDataFetchRequested
            {
                JobId = ChildId, Attempt = 1, SubmittedAt = At, SubmittedBy = "orchestrator", RequestingJobId = JobId,
                Fetch = new MarketDataFetchSpec
                {
                    Mode = FetchMode.Download, SecurityType = ContractSecurityType.Equity, Market = "usa", Tickers = new() { "SPY" },
                    Resolution = ContractResolution.Minute, TickType = ContractTickType.Trade, StartDate = new DateOnly(2013, 10, 7),
                    EndDate = new DateOnly(2013, 10, 11), Paths = new() { "equity/usa/minute/spy/20131007_trade.zip" }, Downloader = "YahooDataDownloader",
                },
            }, "marketdata");
            yield return Case("job.started.schema.json", new JobStarted
            {
                JobId = JobId, Kind = JobKind.Live, Attempt = 2, ParentJobId = ChildId, StartedAt = At, WorkerId = "worker-1",
                LeaseEpoch = 3, Results = results, EngineVersion = "2.5.0.0",
            });
            yield return Case("job.heartbeat.schema.json", new JobHeartbeat
            {
                JobId = JobId, Kind = JobKind.Live, Attempt = 2, At = At, WorkerId = "worker-1", LeaseEpoch = 3, EngineAlive = true, RssBytes = 1 << 30,
            });
            yield return Case("job.progress.schema.json", new JobProgress
            {
                JobId = JobId, Kind = JobKind.Optimization, Attempt = 1, ParentJobId = ChildId, At = At, Progress = 0.5,
                AlgorithmTime = At, RuntimeStatistics = new() { ["Equity"] = "$100,000.00" },
                Optimization = new OptimizationProgress { Total = 10, Completed = 4, Failed = 1, Running = 2, BestJobId = ChildId, BestTargetValue = 1.5 },
            });
            yield return Case("job.completed.schema.json", new JobCompleted
            {
                JobId = JobId, Kind = JobKind.Backtest, Attempt = 2, ParentJobId = ChildId, CompletedAt = At, Result = obj,
                Artifacts = new() { obj with { Key = $"{JobId}/2/{JobId}-log.txt", ContentType = "text/plain" } },
                Statistics = new() { ["Total Orders"] = "1" }, AlgorithmId = "BasicTemplateAlgorithm", ExitCode = 0,
            });
            yield return Case("job.failed.schema.json", new JobFailed
            {
                JobId = JobId, Kind = JobKind.Backtest, Attempt = 2, ParentJobId = ChildId, FailedAt = At, Error = error, Final = true, Artifacts = new() { obj },
            });
            yield return Case("job.cancel.schema.json", new CancelJob
            {
                JobId = JobId, Attempt = 2, RequestedAt = At, RequestedBy = "alice", Reason = "user", LeaseEpoch = 3,
            });
            yield return Case("job.cancelled.schema.json", new JobCancelled
            {
                JobId = JobId, Kind = JobKind.Live, Attempt = 2, ParentJobId = ChildId, CancelledAt = At, Reason = "user", LeaseEpoch = 3, Artifacts = new() { obj },
            });
            yield return Case("result.packet.schema.json", new PacketRef
            {
                PacketType = "BacktestResult", JobId = JobId, Attempt = 2, AlgorithmId = "BasicTemplateAlgorithm", Final = true, Progress = 1,
                Object = obj, Format = PacketRef.FinalFormat, Statistics = new() { ["Sharpe Ratio"] = "1.2" },
            });
        }

        private static TestCaseData Case<T>(string schema, T body, string name = null) =>
            new TestCaseData(schema, typeof(T), body).SetName($"FullyPopulated({typeof(T).Name}{(name == null ? "" : "/" + name)})");

        [TestCaseSource(nameof(FullyPopulatedBodies))]
        public void FullyPopulatedBodyMatchesSchemaAndRoundTrips(string schemaFile, Type type, object body)
        {
            var json = JsonSerializer.SerializeToElement(body, type, ContractJson.Options);
            ContractSchemas.AssertValid(ContractSchemas.ValidateMessage(schemaFile, JsonNode.Parse(json.GetRawText())), type.Name);

            var back = json.Deserialize(type, ContractJson.Options);
            var again = JsonSerializer.SerializeToElement(back, type, ContractJson.Options);
            Assert.IsTrue(JsonElement.DeepEquals(json, again), $"{json}{Environment.NewLine}!={Environment.NewLine}{again}");
        }

        [Test]
        public void EnvelopeFactoryProducesSchemaValidEnvelope()
        {
            var transient = MessageEnvelope.Create(MessageTypes.JobHeartbeat, JobId, 2, AppIds.LiveWorker, JobKind.Live, leaseEpoch: 3, now: At);
            var persistent = MessageEnvelope.Create(MessageTypes.JobCompleted, JobId, 2, AppIds.BacktestWorker, JobKind.Backtest, ChildId, sequence: 9);

            Assert.AreEqual(MessageEnvelope.Transient, transient.DeliveryMode);
            Assert.AreEqual(MessageEnvelope.Persistent, persistent.DeliveryMode);
            Assert.AreEqual(At.ToUnixTimeSeconds(), transient.Timestamp);
            Assert.AreEqual(ContractVersion.Current, transient.Headers.ContractVersion);
            foreach (var envelope in new[] { transient, persistent })
            {
                ContractSchemas.AssertValid(ContractSchemas.ValidateMessage("envelope.schema.json", ContractSchemas.ToNode(envelope)), envelope.Type);
            }
        }

        [Test]
        public void TimestampsAreWrittenInUtcWithZ()
        {
            var json = ContractJson.Serialize(new CancelJob { JobId = JobId, RequestedAt = new DateTimeOffset(2026, 11, 22, 21, 30, 0, TimeSpan.FromHours(8)), Reason = "r" });
            StringAssert.Contains("\"requestedAt\":\"2026-11-22T13:30:00Z\"", json);
        }

        [Test]
        public void MissingRequiredPropertyFailsDeserialization()
        {
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<CancelJob>($"{{\"jobId\":\"{JobId}\",\"reason\":\"r\"}}"));
        }

        [Test]
        public void UnknownEnumValueFailsDeserialization()
        {
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<JobHeartbeat>(
                $"{{\"jobId\":\"{JobId}\",\"kind\":\"nope\",\"attempt\":1,\"at\":\"2026-11-22T13:30:00Z\",\"workerId\":\"w\"}}"));
        }

        [Test]
        public void AdditiveHeadersSurviveRoundTrip()
        {
            var json = $"{{\"contract-version\":\"1.3\",\"job-id\":\"{JobId}\",\"attempt\":1,\"x-future\":\"kept\"}}";
            var headers = ContractJson.Deserialize<MessageHeaders>(json);
            Assert.AreEqual("kept", headers.Extra["x-future"].GetString());
            StringAssert.Contains("\"x-future\":\"kept\"", ContractJson.Serialize(headers));
        }

        [Test]
        public void InvalidRecordIsRejectedBySchema()
        {
            var body = new JobFailed { JobId = JobId, Kind = JobKind.Backtest, Attempt = 0, FailedAt = At, Error = new ErrorInfo { Code = ErrorCode.Internal, Message = "x" }, Final = true };
            Assert.IsFalse(ContractSchemas.ValidateMessage("job.failed.schema.json", ContractSchemas.ToNode(body)).IsValid, "attempt must be >= 1");
        }
    }
}
