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
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Tests.Services.Contracts
{
    using Report = QuantConnect.Services.Contracts.Http.Report;

    [TestFixture, Category("Services")]
    public class HttpContractTests
    {
        private static readonly Guid JobId = Guid.Parse("3f2b8c1e-6a4d-4e5f-9b7a-1c2d3e4f5a6b");
        private static readonly DateTimeOffset At = new(2026, 11, 22, 13, 30, 0, TimeSpan.Zero);
        private static readonly ObjectRef Obj = new() { Bucket = Buckets.Reports, Key = $"{JobId}/1/report.html", Size = 10, Sha256 = new string('0', 64), ContentType = "text/html" };
        private static readonly ErrorInfo Error = new() { Code = ErrorCode.Timeout, Message = "late" };
        private static readonly BacktestSpec Backtest = new() { Algorithm = new BuiltinAlgorithmRef { TypeName = "BasicTemplateAlgorithm" } };

        private static IEnumerable<TestCaseData> Components()
        {
            var request = new JobRequest { Kind = JobKind.Backtest, ParentJobId = JobId, Backtest = Backtest };
            var job = new Job
            {
                Id = JobId, Kind = JobKind.Backtest, Status = JobStatus.Running, Attempt = 1, ParentJobId = JobId, Name = "bt", SubmittedBy = "alice",
                CreatedAt = At, StartedAt = At, FinishedAt = At, Deadline = At, Progress = 0.25, LeaseEpoch = 1, WorkerId = "w", Error = Error, Spec = request,
            };
            var child = new ChildBacktest { JobId = JobId, Parameters = new() { ["fast"] = "10" }, Status = JobStatus.Completed, Objective = 1.5, Statistics = new() { ["Sharpe Ratio"] = "1.5" } };

            yield return Case("common.yaml", "Problem", new Problem
            {
                Type = "https://contracts.lean.local/problems/validation", Title = "Invalid", Status = 400, Detail = "bad", Instance = "/api/v1/jobs",
                TraceId = "00-abc", Errors = new() { ["kind"] = new() { "required" } },
            });
            yield return Case("common.yaml", "JobRequest", request);
            yield return Case("common.yaml", "Job", job);
            yield return Case("common.yaml", "JobPage", new Page<Job> { Items = new() { job }, Next = "cursor" });
            yield return Case("common.yaml", "Health", new Health { Status = HealthStatus.Degraded, Checks = new() { ["rabbitmq"] = HealthStatus.Ok, ["postgres"] = HealthStatus.Down } });
            yield return Case("gateway.openapi.yaml", "SseEvent", new SseEvent
            {
                Event = SseEventName.Status, Type = MessageTypes.JobStarted, JobId = JobId, Attempt = 1, Sequence = 4,
                Body = JsonSerializer.SerializeToElement(new JobStarted { JobId = JobId, Kind = JobKind.Backtest, Attempt = 1, StartedAt = At, WorkerId = "w", Results = ResultsPrefix.For(JobId, 1) }, ContractJson.Options),
            });
            yield return Case("orchestrator.openapi.yaml", "Attempt", new JobAttempt { JobId = JobId, Attempt = 2, Results = ResultsPrefix.For(JobId, 2), Deadline = At });
            yield return Case("orchestrator.openapi.yaml", "Lease", new Lease { JobId = JobId, Attempt = 2, LeaseEpoch = 4, HeartbeatIntervalSeconds = 10, Results = ResultsPrefix.For(JobId, 2) });
            yield return Case("orchestrator.openapi.yaml", "QueueHint", new QueueHint { Queue = "jobs.backtest", Ready = 3, Unacked = 1, Dead = 0, SuggestedReplicas = 2 });
            yield return Case("market-data.openapi.yaml", "Coverage", new Coverage
            {
                SecurityType = "Equity", Market = "usa", Ticker = "SPY", Resolution = "Minute",
                Ranges = new() { new DateRange { Start = new DateOnly(2013, 10, 7), End = new DateOnly(2013, 10, 11) } },
                Missing = new() { new DateOnly(2013, 10, 12) },
            });
            yield return Case("market-data.openapi.yaml", "Bar", new Bar { Time = At, Open = 1.5m, High = 2, Low = 1, Close = 1.75m, Volume = 1000 });
            yield return Case("market-data.openapi.yaml", "FetchTask", new FetchTask
            {
                Id = JobId, Status = JobStatus.Failed, Attempt = 1, RequestingJobId = JobId, FilesWritten = 3, Error = Error, CreatedAt = At, FinishedAt = At,
                Fetch = new MarketDataFetchSpec { Mode = FetchMode.GenerateSynthetic, SecurityType = ContractSecurityType.Equity, Market = "usa", Resolution = ContractResolution.Daily },
            });
            yield return Case("optimizer.openapi.yaml", "ChildBacktest", child);
            yield return Case("optimizer.openapi.yaml", "OptimizationState", new OptimizationState
            {
                JobId = JobId, Status = JobStatus.Running, TotalBacktests = 10, Completed = 5, Failed = 1, Running = 2, Queued = 2, Best = child, Result = Obj with { Bucket = Buckets.Results },
            });
            yield return Case("report.openapi.yaml", "Report", new Report
            {
                JobId = JobId, Status = JobStatus.Completed, Attempt = 1, Spec = new ReportSpec { SourceJobId = JobId }, Html = Obj, Pdf = Obj with { Key = "x/report.pdf" }, Error = Error, CompletedAt = At,
            });
            yield return Case("results.openapi.yaml", "ResultSummary", new ResultSummary
            {
                JobId = JobId, Attempt = 1, Kind = JobKind.Backtest, ParentJobId = JobId, Status = JobStatus.Completed, AlgorithmId = "BasicTemplateAlgorithm", Progress = 1,
                Parameters = new() { ["fast"] = "10" }, Statistics = new() { ["Total Orders"] = "1" }, RuntimeStatistics = new() { ["Equity"] = "$1" }, OrderCount = 1,
                Result = Obj with { Bucket = Buckets.Results }, UpdatedAt = At,
            });
            yield return Case("results.openapi.yaml", "LogLine", new LogLine { Time = At, Type = "RuntimeError", Message = "boom", StackTrace = "at X" });
        }

        private static TestCaseData Case<T>(string file, string schema, T value) =>
            new TestCaseData(file, schema, typeof(T), value).SetName($"{file}#{schema}");

        [TestCaseSource(nameof(Components))]
        public void RecordMatchesOpenApiComponentAndRoundTrips(string file, string schema, Type type, object value)
        {
            var json = JsonSerializer.SerializeToElement(value, type, ContractJson.Options);
            ContractSchemas.AssertValid(ContractSchemas.ValidateComponent(file, schema, JsonNode.Parse(json.GetRawText())), $"{file}#{schema}");

            var again = JsonSerializer.SerializeToElement(json.Deserialize(type, ContractJson.Options), type, ContractJson.Options);
            Assert.IsTrue(JsonElement.DeepEquals(json, again), $"{json}{Environment.NewLine}!={Environment.NewLine}{again}");
        }

        [Test]
        public void ComponentValidationRejectsInvalidInstances()
        {
            var node = JsonNode.Parse("{\"status\":\"sideways\"}");
            Assert.IsFalse(ContractSchemas.ValidateComponent("common.yaml", "Health", node).IsValid);
        }
    }
}
