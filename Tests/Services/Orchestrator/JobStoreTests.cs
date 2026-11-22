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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Orchestrator;

namespace QuantConnect.Tests.Services.Orchestrator
{
    using Exchanges = QuantConnect.Services.Messaging.Exchanges;
    using Queues = QuantConnect.Services.Messaging.Queues;

    /// <summary>
    /// The orchestrator against a real PostgreSQL (each test in its own schema) and the in-memory bus. Set
    /// <c>LEAN_TEST_POSTGRES</c> to a connection string (e.g. the compose <c>jobs</c> database); ignored otherwise.
    /// </summary>
    [TestFixture, Category("Services"), NonParallelizable]
    public class JobStoreTests
    {
        private static readonly CancellationToken Ct = CancellationToken.None;
        private NpgsqlDataSource _db;
        private string _schema;
        private ManualTime _time;
        private InMemoryMessageBus _bus;
        private JobStore _store;
        private OutboxRelay _relay;
        private StatusConsumer _consumer;
        private readonly OrchestratorOptions _options = new() { MaxAttempts = 2, HeartbeatTimeoutSeconds = 60 };

        [SetUp]
        public async Task SetUp()
        {
            var connectionString = Environment.GetEnvironmentVariable("LEAN_TEST_POSTGRES");
            if (string.IsNullOrEmpty(connectionString)) Assert.Ignore("LEAN_TEST_POSTGRES is not set");
            _schema = "t_" + Guid.NewGuid().ToString("N")[..12];
            await using (var admin = NpgsqlDataSource.Create(connectionString))
            await using (var create = admin.CreateCommand($"CREATE SCHEMA {_schema}"))
            {
                await create.ExecuteNonQueryAsync(Ct);
            }
            _db = new NpgsqlDataSourceBuilder(connectionString) { ConnectionStringBuilder = { SearchPath = _schema } }.Build();
            _time = new ManualTime();
            _bus = new InMemoryMessageBus();
            _store = new JobStore(_db, Options.Create(_options), _time, NullLogger<JobStore>.Instance);
            await _store.MigrateAsync(Ct);
            await _store.MigrateAsync(Ct);
            _relay = new OutboxRelay(_store, _bus, Options.Create(_options), NullLogger<OutboxRelay>.Instance);
            _consumer = new StatusConsumer(_store, _bus, NullLogger<StatusConsumer>.Instance);
        }

        [TearDown]
        public async Task TearDown()
        {
            if (_db == null) return;
            await using (var drop = _db.CreateCommand($"DROP SCHEMA {_schema} CASCADE"))
            {
                await drop.ExecuteNonQueryAsync(Ct);
            }
            await _db.DisposeAsync();
        }

        private async Task<Job> Create(JobRequest request, string key = null)
        {
            var result = await _store.CreateAsync(request, "alice", key, Ct);
            Assert.AreEqual(StoreOutcome.Ok, result.Outcome, result.Detail);
            return result.Value!.Job;
        }

        private async Task<Job> Get(Guid id) => (await _store.GetAsync(id, Ct))!;

        private async Task Event<T>(string type, Job job, int attempt, T body, MessageDisposition expected = MessageDisposition.Ack)
        {
            var envelope = MessageEnvelope.Create(type, job.Id, attempt, AppIds.BacktestWorker, job.Kind);
            var disposition = await _consumer.HandleAsync(new ReceivedMessage
            {
                Queue = Queues.OrchestratorStatus, Exchange = Exchanges.Events, RoutingKey = RoutingKeys.EventFor(type, job.Kind, job.Id),
                Envelope = envelope, Body = ContractJson.SerializeToUtf8Bytes(body),
            }, Ct);
            Assert.AreEqual(expected, disposition);
        }

        private Task Started(Job job, int attempt) => Event(MessageTypes.JobStarted, job, attempt, new JobStarted
        {
            JobId = job.Id, Kind = job.Kind, Attempt = attempt, StartedAt = _time.Now, WorkerId = "w1", Results = ResultsPrefix.For(job.Id, attempt),
        });

        private Task Heartbeat(Job job, int attempt) => Event(MessageTypes.JobHeartbeat, job, attempt, new JobHeartbeat
        {
            JobId = job.Id, Kind = job.Kind, Attempt = attempt, At = _time.Now, WorkerId = "w1",
        });

        private Task Completed(Job job, int attempt) => Event(MessageTypes.JobCompleted, job, attempt, new JobCompleted
        {
            JobId = job.Id, Kind = job.Kind, Attempt = attempt, CompletedAt = _time.Now, Artifacts = new(),
            Result = new ObjectRef { Bucket = Buckets.Results, Key = $"{job.Id:D}/{attempt}/result.json", Size = 10, Sha256 = new string('a', 64) },
        });

        [Test]
        public async Task BacktestRunsQueuedDispatchedRunningCompleted()
        {
            var job = await Create(Requests.Backtest());
            Assert.AreEqual((JobStatus.Queued, 1), (job.Status, job.Attempt));
            Assert.AreEqual(0, _bus.Peek(Queues.Backtest).Count, "nothing is published inside the transaction");

            Assert.AreEqual(1, await _relay.RelayOnceAsync(Ct));
            var dispatch = _bus.Peek(Queues.Backtest).Single();
            Assert.AreEqual(MessageTypes.BacktestRequested, dispatch.Envelope.Type);
            var submitted = ContractJson.Deserialize<JobSubmitted>(dispatch.Body.Span);
            Assert.AreEqual((job.Id, 1, "alice"), (submitted.JobId, submitted.Attempt, submitted.SubmittedBy));
            Assert.AreEqual(JobStatus.Dispatched, (await Get(job.Id)).Status);
            Assert.AreEqual(0, await _relay.RelayOnceAsync(Ct), "outbox rows are sent once");

            var attempt = await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct);
            Assert.AreEqual(StoreOutcome.Ok, attempt.Outcome);
            Assert.AreEqual((1, $"{job.Id:D}/1/"), (attempt.Value!.Attempt, attempt.Value.Results.Prefix));
            Assert.AreEqual(_time.Now.AddSeconds(_options.DefaultBacktestMaxRuntimeSeconds), attempt.Value.Deadline);

            await Started(job, 1);
            var running = await Get(job.Id);
            Assert.AreEqual((JobStatus.Running, "w1"), (running.Status, running.WorkerId));
            await Event(MessageTypes.JobProgress, job, 1, new JobProgress { JobId = job.Id, Kind = job.Kind, Attempt = 1, At = _time.Now, Progress = 0.5 });
            Assert.AreEqual(0.5, (await Get(job.Id)).Progress);

            await Completed(job, 1);
            var done = await Get(job.Id);
            Assert.AreEqual((JobStatus.Completed, 1d), (done.Status, done.Progress));
            Assert.IsNotNull(done.FinishedAt);
            Assert.AreEqual(StoreOutcome.Conflict, (await _store.StartAttemptAsync(job.Id, "w2", 1, 1, Ct)).Outcome, "terminal jobs refuse attempts");
        }

        [Test]
        public async Task IdempotencyKeyReplaysOrConflicts()
        {
            var first = await Create(Requests.Backtest(), "k1");
            var replay = await _store.CreateAsync(Requests.Backtest(), "alice", "k1", Ct);
            Assert.AreEqual((StoreOutcome.Ok, true, first.Id), (replay.Outcome, replay.Value!.Replayed, replay.Value.Job.Id));
            Assert.AreEqual(StoreOutcome.Conflict, (await _store.CreateAsync(Requests.Backtest("Other"), "alice", "k1", Ct)).Outcome);
            _time.Advance(TimeSpan.FromHours(25));
            var expired = await _store.CreateAsync(Requests.Backtest("Other"), "alice", "k1", Ct);
            Assert.AreNotEqual(first.Id, expired.Value!.Job.Id, "keys expire after 24 h");
            Assert.AreEqual(2, await _relay.RelayOnceAsync(Ct));
        }

        [Test]
        public async Task DuplicateAndStaleEventsAreIgnored()
        {
            var job = await Create(Requests.Backtest());
            await _relay.RelayOnceAsync(Ct);
            await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct);
            var envelope = MessageEnvelope.Create(MessageTypes.JobProgress, job.Id, 1, AppIds.BacktestWorker, job.Kind);
            var body = ContractJson.SerializeToUtf8Bytes(new JobProgress { JobId = job.Id, Kind = job.Kind, Attempt = 1, At = _time.Now, Progress = 0.3 });
            Assert.IsTrue(await _store.ApplyEventAsync(envelope, body, Ct));
            Assert.IsFalse(await _store.ApplyEventAsync(envelope, body, Ct), "same message id");
            Assert.IsFalse(await _store.ApplyEventAsync(MessageEnvelope.Create(MessageTypes.JobCompleted, job.Id, 7, AppIds.BacktestWorker, job.Kind), body, Ct), "other attempt");
            Assert.AreEqual(JobStatus.Dispatched, (await Get(job.Id)).Status);
            await Event(MessageTypes.JobCompleted, job, 1, new { garbage = true }, MessageDisposition.DeadLetter);
        }

        [Test]
        public async Task WorkerDeathRetriesUntilMaxAttempts()
        {
            var job = await Create(Requests.Backtest());
            await _relay.RelayOnceAsync(Ct);
            await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct);
            await Started(job, 1);

            _time.Advance(TimeSpan.FromSeconds(30));
            await Heartbeat(job, 1);
            _time.Advance(TimeSpan.FromSeconds(45));
            Assert.AreEqual(0, await _store.MonitorAsync(Ct), "heartbeat 45 s ago is within the 60 s timeout");

            _time.Advance(TimeSpan.FromSeconds(30));
            Assert.AreEqual(1, await _store.MonitorAsync(Ct));
            var retried = await Get(job.Id);
            Assert.AreEqual((JobStatus.Queued, 2), (retried.Status, retried.Attempt));
            Assert.IsNull(retried.WorkerId);
            await _relay.RelayOnceAsync(Ct);
            var cancel = _bus.Published.Single(p => p.Exchange == Exchanges.Control);
            Assert.AreEqual((1, $"job.{job.Id:D}.cancel.v1"), (cancel.Envelope.Headers.Attempt, cancel.RoutingKey), "the lost attempt is told to stop");
            Assert.AreEqual(new[] { 1, 2 }, _bus.Peek(Queues.Backtest).Select(m => m.Envelope.Headers.Attempt).ToArray());
            Assert.AreEqual(JobStatus.Dispatched, (await Get(job.Id)).Status);

            Assert.AreEqual(StoreOutcome.Conflict, (await _store.StartAttemptAsync(job.Id, "w9", 1, 1, Ct)).Outcome, "superseded dispatch is dropped");
            Assert.AreEqual(2, (await _store.StartAttemptAsync(job.Id, "w2", 0, 2, Ct)).Value!.Attempt);
            await Event(MessageTypes.JobCompleted, job, 1, new JobCompleted
            {
                JobId = job.Id, Kind = job.Kind, Attempt = 1, CompletedAt = _time.Now, Artifacts = new(),
                Result = new ObjectRef { Bucket = Buckets.Results, Key = "x", Size = 1, Sha256 = new string('a', 64) },
            });
            Assert.AreEqual(JobStatus.Dispatched, (await Get(job.Id)).Status, "late events of the lost attempt are ignored");

            _time.Advance(TimeSpan.FromSeconds(61));
            Assert.AreEqual(1, await _store.MonitorAsync(Ct));
            var failed = await Get(job.Id);
            Assert.AreEqual((JobStatus.Failed, 2, ErrorCode.MaxAttemptsExceeded), (failed.Status, failed.Attempt, failed.Error!.Code));
            await _relay.RelayOnceAsync(Ct);
            var final = _bus.Published.Last(p => p.Envelope.Type == MessageTypes.JobFailed);
            Assert.IsTrue(ContractJson.Deserialize<JobFailed>(final.Body.Span).Final);
        }

        [Test]
        public async Task RedeliveryOfAHeldAttemptStartsANewAttempt()
        {
            var job = await Create(Requests.Backtest());
            await _relay.RelayOnceAsync(Ct);
            Assert.AreEqual(1, (await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct)).Value!.Attempt);
            Assert.AreEqual(1, (await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct)).Value!.Attempt, "same worker retrying the call");
            Assert.AreEqual(2, (await _store.StartAttemptAsync(job.Id, "w2", 1, 1, Ct)).Value!.Attempt);
            Assert.AreEqual(StoreOutcome.Exhausted, (await _store.StartAttemptAsync(job.Id, "w3", 2, 1, Ct)).Outcome, "max attempts");
            Assert.AreEqual(JobStatus.Failed, (await Get(job.Id)).Status);
        }

        [Test]
        public async Task WorkerReportedFailureRetriesUnlessFinal()
        {
            var job = await Create(Requests.Backtest());
            await _relay.RelayOnceAsync(Ct);
            await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct);
            var error = new ErrorInfo { Code = ErrorCode.EngineCrash, Message = "boom" };
            await Event(MessageTypes.JobFailed, job, 1, new JobFailed { JobId = job.Id, Kind = job.Kind, Attempt = 1, FailedAt = _time.Now, Error = error, Final = false });
            Assert.AreEqual((JobStatus.Queued, 2), ((await Get(job.Id)).Status, (await Get(job.Id)).Attempt));
            await Event(MessageTypes.JobFailed, job, 2, new JobFailed { JobId = job.Id, Kind = job.Kind, Attempt = 2, FailedAt = _time.Now, Error = error, Final = true });
            var failed = await Get(job.Id);
            Assert.AreEqual((JobStatus.Failed, ErrorCode.EngineCrash), (failed.Status, failed.Error!.Code));
        }

        [Test]
        public async Task CancellationOfQueuedRunningAndOptimizationJobs()
        {
            var queued = await Create(Requests.Backtest());
            var cancelled = await _store.CancelAsync(queued.Id, null, "alice", Ct);
            Assert.AreEqual(JobStatus.Cancelled, cancelled.Value!.Status, "no worker holds it: cancelled directly");
            Assert.AreEqual(StoreOutcome.Ok, (await _store.CancelAsync(queued.Id, null, "alice", Ct)).Outcome, "idempotent");
            await _relay.RelayOnceAsync(Ct);
            Assert.AreEqual(StoreOutcome.Conflict, (await _store.StartAttemptAsync(queued.Id, "w1", 0, 1, Ct)).Outcome, "its dispatch is dropped");

            var optimization = await Create(Requests.Optimization());
            var child = await Create(Requests.Backtest(parent: optimization.Id));
            var pending = await Create(Requests.Backtest(parent: optimization.Id));
            await _relay.RelayOnceAsync(Ct);
            await _store.StartAttemptAsync(optimization.Id, "opt", 0, 1, Ct);
            await _store.StartAttemptAsync(child.Id, "w1", 0, 1, Ct);
            await Started(child, 1);

            var requested = await _store.CancelAsync(optimization.Id, "user stop", "alice", Ct);
            Assert.AreEqual(JobStatus.Dispatched, requested.Value!.Status, "a held job waits for job.cancelled");
            Assert.AreEqual(JobStatus.Running, (await Get(child.Id)).Status);
            Assert.AreEqual(JobStatus.Cancelled, (await Get(pending.Id)).Status);
            await _relay.RelayOnceAsync(Ct);
            var cancels = _bus.Published.Where(p => p.Envelope.Type == MessageTypes.CancelJob).Select(p => p.Envelope.Headers.JobId).ToList();
            CollectionAssert.AreEquivalent(new[] { optimization.Id, child.Id }, cancels);

            await Event(MessageTypes.JobCancelled, child, 1, new JobCancelled { JobId = child.Id, Kind = child.Kind, Attempt = 1, CancelledAt = _time.Now, Reason = "parent optimization cancelled" });
            Assert.AreEqual(JobStatus.Cancelled, (await Get(child.Id)).Status);
            _time.Advance(TimeSpan.FromSeconds(61));
            await _store.MonitorAsync(Ct);
            Assert.AreEqual(JobStatus.Cancelled, (await Get(optimization.Id)).Status, "a silent worker with a cancel pending ends cancelled, not retried");
            Assert.AreEqual(StoreOutcome.Conflict, (await _store.CreateAsync(Requests.Backtest(parent: optimization.Id), "alice", null, Ct)).Outcome);
        }

        [Test]
        public async Task HeartbeatFromAnAttemptWithAPendingCancelResendsTheCancel()
        {
            var job = await Create(Requests.Backtest());
            await _relay.RelayOnceAsync(Ct);
            await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct);
            await Started(job, 1);
            await Heartbeat(job, 1);
            await _relay.RelayOnceAsync(Ct);
            Assert.IsEmpty(_bus.Published.Where(p => p.Envelope.Type == MessageTypes.CancelJob), "no cancel without a request");

            await _store.CancelAsync(job.Id, "user stop", "alice", Ct);
            await _relay.RelayOnceAsync(Ct);
            await Heartbeat(job, 1);
            await _relay.RelayOnceAsync(Ct);
            var cancels = _bus.Published.Where(p => p.Envelope.Type == MessageTypes.CancelJob).ToList();
            Assert.AreEqual(2, cancels.Count, "the lost cancel is repeated on the next heartbeat");
            Assert.IsTrue(cancels.All(c => c.RoutingKey == RoutingKeys.Cancel(job.Id) && ContractJson.Deserialize<CancelJob>(c.Body.Span)!.Attempt == 1));
            Assert.AreEqual("user stop", ContractJson.Deserialize<CancelJob>(cancels[1].Body.Span)!.Reason);
        }

        [Test]
        public async Task LiveLeaseIsExclusiveUntilHeartbeatsStop()
        {
            var live = await Create(Requests.Live());
            await _relay.RelayOnceAsync(Ct);
            var lease = await _store.AcquireLeaseAsync(live.Id, "lw1", Ct);
            Assert.AreEqual((1L, 1, 15), (lease.Value!.LeaseEpoch, lease.Value.Attempt, lease.Value.HeartbeatIntervalSeconds));
            Assert.AreEqual(JobStatus.Running, (await Get(live.Id)).Status);
            Assert.AreEqual(StoreOutcome.Conflict, (await _store.AcquireLeaseAsync(live.Id, "lw2", Ct)).Outcome, "never runs twice");
            _time.Advance(TimeSpan.FromSeconds(61));
            var takeover = await _store.AcquireLeaseAsync(live.Id, "lw2", Ct);
            Assert.AreEqual((2L, 2), (takeover.Value!.LeaseEpoch, takeover.Value.Attempt));
            Assert.AreEqual(StoreOutcome.Conflict, (await _store.AcquireLeaseAsync((await Create(Requests.Backtest())).Id, "lw1", Ct)).Outcome);
        }

        [Test]
        public async Task ReportResolvesTheSourceResult()
        {
            var source = await Create(Requests.Backtest());
            var report = new JobRequest { Kind = JobKind.Report, Report = new ReportSpec { SourceJobId = source.Id } };
            Assert.AreEqual(StoreOutcome.Conflict, (await _store.CreateAsync(report, "alice", null, Ct)).Outcome, "source not completed");
            await _relay.RelayOnceAsync(Ct);
            await _store.StartAttemptAsync(source.Id, "w1", 0, 1, Ct);
            await Completed(source, 1);
            var job = await Create(report);
            await _relay.RelayOnceAsync(Ct);
            var requested = ContractJson.Deserialize<ReportRequested>(_bus.Peek(Queues.Report).Single().Body.Span);
            Assert.AreEqual($"{source.Id:D}/1/result.json", requested.BacktestResult.Key);
            Assert.AreEqual(job.Id, requested.JobId);
        }

        [Test]
        public async Task DeadlineFailsTheAttemptWithTimeout()
        {
            var job = await Create(Requests.Backtest() with { Backtest = Requests.Backtest().Backtest! with { MaxRuntimeSeconds = 30 } });
            await _relay.RelayOnceAsync(Ct);
            await _store.StartAttemptAsync(job.Id, "w1", 0, 1, Ct);
            _time.Advance(TimeSpan.FromSeconds(20));
            await Heartbeat(job, 1);
            _time.Advance(TimeSpan.FromSeconds(20));
            Assert.AreEqual(1, await _store.MonitorAsync(Ct));
            var failed = await Get(job.Id);
            Assert.AreEqual((JobStatus.Failed, ErrorCode.Timeout), (failed.Status, failed.Error!.Code));
        }

        [Test]
        public async Task ListPagesNewestFirstWithFilters()
        {
            for (var i = 0; i < 3; i++)
            {
                await Create(Requests.Backtest());
                _time.Advance(TimeSpan.FromSeconds(1));
            }
            await Create(Requests.Live());
            var page = await _store.ListAsync(JobKind.Backtest, null, null, "alice", null, 2, Ct);
            Assert.AreEqual(2, page.Items.Count);
            Assert.IsNotNull(page.Next);
            var rest = await _store.ListAsync(JobKind.Backtest, null, null, null, page.Next, 2, Ct);
            Assert.AreEqual(1, rest.Items.Count);
            Assert.IsNull(rest.Next);
            Assert.That(page.Items[0].CreatedAt, Is.GreaterThan(rest.Items[0].CreatedAt));
            Assert.AreEqual(4, (await _store.ListAsync(null, JobStatus.Queued, null, null, null, 50, Ct)).Items.Count);
        }
    }
}
