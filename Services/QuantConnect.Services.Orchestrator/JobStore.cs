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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.Orchestrator;

public enum StoreOutcome { Ok, NotFound, Conflict, Invalid }

public sealed record StoreResult<T>(StoreOutcome Outcome, T? Value = default, string? Detail = null)
{
    public static StoreResult<T> Ok(T value) => new(StoreOutcome.Ok, value);
    public static StoreResult<T> NotFound() => new(StoreOutcome.NotFound);
    public static StoreResult<T> Conflict(string detail) => new(StoreOutcome.Conflict, default, detail);
    public static StoreResult<T> Invalid(string detail) => new(StoreOutcome.Invalid, default, detail);
}

public sealed record CreatedJob(Job Job, bool Replayed);

public sealed record OutboxRow(long Id, Guid JobId, OutgoingMessage Message);

/// <summary>
/// The jobs database (PostgreSQL). Every state change locks the job row (<c>FOR UPDATE</c>) and writes the
/// messages announcing it to the outbox in the same transaction (ADR 0002), so a committed change is never lost
/// and a crash between commit and publish only delays the publish.
/// </summary>
public sealed class JobStore
{
    private const string Columns = "id, kind, status, attempt, parent_job_id, name, submitted_by, created_at, started_at, finished_at, deadline, " +
        "progress, lease_epoch, worker_id, attempt_taken_at, last_heartbeat_at, cancel_requested_at, cancel_reason, error, result, inputs, spec";

    private readonly NpgsqlDataSource _db;
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<JobStore> _logger;

    /// <summary>Signalled after a commit that wrote outbox rows, so the relay publishes without waiting for its poll.</summary>
    public event Action? OutboxWritten;

    public JobStore(NpgsqlDataSource db, IOptions<OrchestratorOptions> options, TimeProvider time, ILogger<JobStore> logger)
    {
        _db = db;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Applies the embedded <c>Migrations/*.sql</c> scripts not yet recorded in <c>schema_migrations</c>.</summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, "SELECT pg_advisory_xact_lock(7413001)", ct);
        await Exec(conn, tx, "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL)", ct);
        var assembly = typeof(JobStore).Assembly;
        const string prefix = "QuantConnect.Services.Orchestrator.Migrations.";
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var version = name[prefix.Length..];
            await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE version = @v", conn, tx))
            {
                check.Parameters.AddWithValue("v", version);
                if (await check.ExecuteScalarAsync(ct) != null) continue;
            }
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await Exec(conn, tx, await reader.ReadToEndAsync(ct), ct);
            await using var record = new NpgsqlCommand("INSERT INTO schema_migrations (version, applied_at) VALUES (@v, now())", conn, tx);
            record.Parameters.AddWithValue("v", version);
            await record.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Applied migration {Version}", version);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = _db.CreateCommand("SELECT 1");
            return await cmd.ExecuteScalarAsync(ct) is 1;
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- jobs API

    public async Task<StoreResult<CreatedJob>> CreateAsync(JobRequest request, string submittedBy, string? idempotencyKey, CancellationToken ct)
    {
        var now = Now;
        var specJson = ContractJson.Serialize(request);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(submittedBy + "\n" + specJson)));
        return await InTransaction(async (conn, tx) =>
        {
            if (idempotencyKey != null)
            {
                await using var find = new NpgsqlCommand($"SELECT request_hash, created_at, {Columns} FROM jobs WHERE idempotency_key = @k FOR UPDATE", conn, tx);
                find.Parameters.AddWithValue("k", idempotencyKey);
                await using var reader = await find.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var previousHash = reader.GetString(0);
                    var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
                    var existing = JobRow.Read(reader, 2);
                    await reader.CloseAsync();
                    if (createdAt > now.AddHours(-24))
                    {
                        return previousHash == hash
                            ? StoreResult<CreatedJob>.Ok(new CreatedJob(existing.ToJob(), true))
                            : StoreResult<CreatedJob>.Conflict("Idempotency-Key was used with a different request");
                    }
                    await using var expire = new NpgsqlCommand("UPDATE jobs SET idempotency_key = NULL WHERE id = @id", conn, tx);
                    expire.Parameters.AddWithValue("id", existing.Id);
                    await expire.ExecuteNonQueryAsync(ct);
                }
            }

            if (request.ParentJobId is { } parentId)
            {
                var parent = await Lock(conn, tx, parentId, ct, forUpdate: false);
                if (parent == null) return StoreResult<CreatedJob>.Invalid($"parent job {parentId} does not exist");
                if (parent.Kind != JobKind.Optimization) return StoreResult<CreatedJob>.Invalid("the parent of a child backtest must be an optimization");
                if (JobStateMachine.IsTerminal(parent.Status)) return StoreResult<CreatedJob>.Conflict($"parent optimization is {JobStateMachine.ToWire(parent.Status)}");
            }

            JsonElement? inputs = null;
            if (request.Report is { } report)
            {
                var resolved = await ResolveReportInputs(conn, tx, report, ct);
                if (resolved.Outcome != StoreOutcome.Ok) return new StoreResult<CreatedJob>(resolved.Outcome, null, resolved.Detail);
                inputs = JsonSerializer.SerializeToElement(resolved.Value, ContractJson.Options);
            }

            var row = new JobRow
            {
                Id = Guid.NewGuid(), Kind = request.Kind, Status = JobStatus.Queued, Attempt = 1, ParentJobId = request.ParentJobId,
                Name = request.Backtest?.Name ?? request.Live?.Name ?? request.Optimization?.Name ?? request.Report?.StrategyName,
                SubmittedBy = submittedBy, CreatedAt = now, Spec = request, Inputs = inputs,
            };
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO jobs (id, kind, status, attempt, parent_job_id, name, submitted_by, created_at, spec, inputs, idempotency_key, request_hash, updated_at) " +
                "VALUES (@id, @kind, @status, 1, @parent, @name, @by, @now, @spec, @inputs, @key, @hash, @now)", conn, tx))
            {
                insert.Parameters.AddWithValue("id", row.Id);
                insert.Parameters.AddWithValue("kind", JobStateMachine.ToWire(row.Kind));
                insert.Parameters.AddWithValue("status", JobStateMachine.ToWire(row.Status));
                insert.Parameters.AddWithValue("parent", (object?)row.ParentJobId ?? DBNull.Value);
                insert.Parameters.AddWithValue("name", (object?)row.Name ?? DBNull.Value);
                insert.Parameters.AddWithValue("by", submittedBy);
                insert.Parameters.AddWithValue("now", now);
                insert.Parameters.Add(new NpgsqlParameter("spec", NpgsqlDbType.Jsonb) { Value = specJson });
                insert.Parameters.Add(new NpgsqlParameter("inputs", NpgsqlDbType.Jsonb) { Value = inputs?.GetRawText() ?? (object)DBNull.Value });
                insert.Parameters.AddWithValue("key", (object?)idempotencyKey ?? DBNull.Value);
                insert.Parameters.AddWithValue("hash", hash);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await Enqueue(conn, tx, row.Id, JobStateMachine.Dispatch(row.Id, request, 1, submittedBy, now, inputs), ct);
            _logger.LogInformation("Job {JobId} ({Kind}) queued by {SubmittedBy}", row.Id, JobStateMachine.ToWire(row.Kind), submittedBy);
            return StoreResult<CreatedJob>.Ok(new CreatedJob(row.ToJob(), false));
        }, ct);
    }

    private async Task<StoreResult<ReportInputs>> ResolveReportInputs(NpgsqlConnection conn, NpgsqlTransaction tx, ReportSpec report, CancellationToken ct)
    {
        var source = await Lock(conn, tx, report.SourceJobId, ct, forUpdate: false);
        if (source == null) return StoreResult<ReportInputs>.Invalid($"source job {report.SourceJobId} does not exist");
        if (source.Status != JobStatus.Completed || source.Result == null)
        {
            return StoreResult<ReportInputs>.Conflict($"source job {report.SourceJobId} is {JobStateMachine.ToWire(source.Status)}, not completed");
        }
        if (report.SourceAttempt is { } attempt && attempt != source.Attempt)
        {
            return StoreResult<ReportInputs>.Conflict($"attempt {attempt} of the source job did not complete (attempt {source.Attempt} did)");
        }
        ObjectRef? live = null;
        if (report.LiveJobId is { } liveId)
        {
            var liveJob = await Lock(conn, tx, liveId, ct, forUpdate: false);
            if (liveJob is not { Kind: JobKind.Live }) return StoreResult<ReportInputs>.Invalid($"live job {liveId} does not exist");
            live = liveJob.Result;
        }
        return StoreResult<ReportInputs>.Ok(new ReportInputs { BacktestResult = source.Result, LiveResult = live });
    }

    public async Task<Job?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await Lock(conn, null, id, ct, forUpdate: false))?.ToJob();
    }

    public async Task<Page<Job>> ListAsync(JobKind? kind, JobStatus? status, Guid? parentJobId, string? submittedBy, string? cursor, int limit, CancellationToken ct)
    {
        var sql = new StringBuilder($"SELECT {Columns} FROM jobs WHERE true");
        await using var cmd = _db.CreateCommand();
        if (kind != null) { sql.Append(" AND kind = @kind"); cmd.Parameters.AddWithValue("kind", JobStateMachine.ToWire(kind.Value)); }
        if (status != null) { sql.Append(" AND status = @status"); cmd.Parameters.AddWithValue("status", JobStateMachine.ToWire(status.Value)); }
        if (parentJobId != null) { sql.Append(" AND parent_job_id = @parent"); cmd.Parameters.AddWithValue("parent", parentJobId.Value); }
        if (submittedBy != null) { sql.Append(" AND submitted_by = @by"); cmd.Parameters.AddWithValue("by", submittedBy); }
        if (DecodeCursor(cursor) is var (at, after))
        {
            sql.Append(" AND (created_at, id) < (@at, @after)");
            cmd.Parameters.AddWithValue("at", at);
            cmd.Parameters.AddWithValue("after", after);
        }
        sql.Append(" ORDER BY created_at DESC, id DESC LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit + 1);
        cmd.CommandText = sql.ToString();
        var rows = new List<JobRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) rows.Add(JobRow.Read(reader, 0));
        }
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            next = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.CreatedAt.UtcTicks}:{last.Id:N}"));
        }
        return new Page<Job> { Items = rows.Select(r => r.ToJob()).ToList(), Next = next };
    }

    private static (DateTimeOffset, Guid)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return (new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero), Guid.ParseExact(parts[1], "N"));
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException("invalid cursor", nameof(cursor), e);
        }
    }

    /// <summary>
    /// Cancels a job (idempotent). A job no worker holds yet is cancelled directly; a held one gets <c>control.v1.CancelJob</c>
    /// and becomes <c>cancelled</c> when the worker reports <c>job.cancelled</c>. Children of an optimization are cancelled too.
    /// </summary>
    public async Task<StoreResult<Job>> CancelAsync(Guid id, string? reason, string? requestedBy, CancellationToken ct, bool liquidate = false)
    {
        var now = Now;
        reason = string.IsNullOrWhiteSpace(reason) ? "cancelled by request" : reason;
        return await InTransaction(async (conn, tx) =>
        {
            var row = await Lock(conn, tx, id, ct);
            if (row == null) return StoreResult<Job>.NotFound();
            if (row.Status == JobStatus.Cancelled) return StoreResult<Job>.Ok(row.ToJob());
            if (JobStateMachine.IsTerminal(row.Status)) return StoreResult<Job>.Conflict($"job is already {JobStateMachine.ToWire(row.Status)}");
            await CancelLocked(conn, tx, row, reason, requestedBy, now, ct, liquidate);
            if (row.Kind == JobKind.Optimization)
            {
                await using var children = new NpgsqlCommand(
                    $"SELECT {Columns} FROM jobs WHERE parent_job_id = @id AND status NOT IN ('completed','failed','cancelled') FOR UPDATE", conn, tx);
                children.Parameters.AddWithValue("id", id);
                var rows = new List<JobRow>();
                await using (var reader = await children.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct)) rows.Add(JobRow.Read(reader, 0));
                }
                foreach (var child in rows) await CancelLocked(conn, tx, child, "parent optimization cancelled", requestedBy, now, ct);
            }
            return StoreResult<Job>.Ok((await Lock(conn, tx, id, ct, forUpdate: false))!.ToJob());
        }, ct);
    }

    private async Task CancelLocked(NpgsqlConnection conn, NpgsqlTransaction tx, JobRow row, string reason, string? requestedBy, DateTimeOffset now, CancellationToken ct,
        bool liquidate = false)
    {
        if (row.WorkerId == null)
        {
            await Update(conn, tx, row.Id, "status = 'cancelled', finished_at = @now, cancel_requested_at = @now, cancel_reason = @reason", now, ct, ("reason", reason));
            await Enqueue(conn, tx, row.Id, JobStateMachine.Cancelled(row.Id, row.Kind, row.Attempt, row.ParentJobId, reason, now), ct);
            _logger.LogInformation("Job {JobId} cancelled before a worker took it", row.Id);
            return;
        }
        if (row.CancelRequestedAt != null) return;
        await Update(conn, tx, row.Id, "cancel_requested_at = @now, cancel_reason = @reason", now, ct, ("reason", reason));
        await Enqueue(conn, tx, row.Id, JobStateMachine.Cancel(row.Id, row.Kind, row.Attempt, row.LeaseEpoch, reason, requestedBy, now, liquidate), ct);
        _logger.LogInformation("Job {JobId} attempt {Attempt}: cancellation sent to worker {WorkerId}", row.Id, row.Attempt, row.WorkerId);
    }

    /// <summary>Live deployments: sends <c>control.v1.LiquidateJob</c> to the worker holding the lease; the algorithm keeps running.</summary>
    public async Task<StoreResult<Job>> LiquidateAsync(Guid id, string? requestedBy, CancellationToken ct)
    {
        var now = Now;
        return await InTransaction(async (conn, tx) =>
        {
            var row = await Lock(conn, tx, id, ct);
            if (row == null) return StoreResult<Job>.NotFound();
            if (row.Kind != JobKind.Live) return StoreResult<Job>.Conflict("only live deployments can be liquidated");
            if (row.Status is not (JobStatus.Running or JobStatus.Stale) || row.WorkerId == null || row.CancelRequestedAt != null)
            {
                return StoreResult<Job>.Conflict($"live deployment is {JobStateMachine.ToWire(row.Status)}{(row.CancelRequestedAt != null ? " (stopping)" : "")}");
            }
            await Enqueue(conn, tx, row.Id, JobStateMachine.Liquidate(row.Id, row.Attempt, row.LeaseEpoch, requestedBy, now), ct);
            _logger.LogInformation("Job {JobId} attempt {Attempt}: liquidation sent to live-worker {WorkerId}", row.Id, row.Attempt, row.WorkerId);
            return StoreResult<Job>.Ok(row.ToJob());
        }, ct);
    }

    // ---------------------------------------------------------------- worker API

    /// <summary>
    /// A worker took a dispatch. First delivery of the current attempt gets that attempt; a redelivery (the attempt is already
    /// held by a worker that died before acking) gets a new attempt; a dispatch of a superseded attempt or of a finished or
    /// cancelled job gets 409 so the worker acks and drops it.
    /// </summary>
    public async Task<StoreResult<JobAttempt>> StartAttemptAsync(Guid id, string workerId, long deliveryCount, int? dispatchedAttempt, CancellationToken ct)
    {
        var now = Now;
        return await InTransaction(async (conn, tx) =>
        {
            var row = await Lock(conn, tx, id, ct);
            if (row == null) return StoreResult<JobAttempt>.NotFound();
            if (JobStateMachine.IsTerminal(row.Status) || row.CancelRequestedAt != null)
            {
                return StoreResult<JobAttempt>.Conflict($"job is {JobStateMachine.ToWire(row.Status)}{(row.CancelRequestedAt != null ? " (cancel requested)" : "")}");
            }
            if (dispatchedAttempt is { } a && a < row.Attempt && await LatestDispatchAttempt(conn, tx, id, ct) is var latest && a < latest)
            {
                return StoreResult<JobAttempt>.Conflict($"attempt {a} was superseded by the dispatch of attempt {latest}");
            }
            var attempt = row.Attempt;
            if (row.WorkerId != null)
            {
                if (row.WorkerId == workerId && deliveryCount == 0) return StoreResult<JobAttempt>.Ok(AttemptOf(row));
                attempt = row.Attempt + 1;
                if (attempt > _options.MaxAttempts)
                {
                    await FailLocked(conn, tx, row, new ErrorInfo { Code = ErrorCode.MaxAttemptsExceeded, Message = $"redelivered after {row.Attempt} attempts" }, now, ct);
                    return StoreResult<JobAttempt>.Conflict($"job failed: {_options.MaxAttempts} attempts exhausted");
                }
                await Enqueue(conn, tx, row.Id, JobStateMachine.Cancel(row.Id, row.Kind, row.Attempt, row.LeaseEpoch, "superseded by a new attempt", AppIds.Orchestrator, now), ct);
                _logger.LogWarning("Job {JobId}: redelivery to {WorkerId}, attempt {Old} held by {Previous} is superseded by {Attempt}", id, workerId, row.Attempt, row.WorkerId, attempt);
            }
            var deadline = row.Kind == JobKind.Backtest
                ? now.AddSeconds(row.Spec.Backtest?.MaxRuntimeSeconds ?? _options.DefaultBacktestMaxRuntimeSeconds)
                : (DateTimeOffset?)null;
            await Update(conn, tx, id,
                "status = CASE WHEN status = 'queued' THEN 'dispatched' ELSE status END, attempt = @attempt, worker_id = @worker, " +
                "attempt_taken_at = @now, last_heartbeat_at = @now, deadline = @deadline, progress = NULL, error = NULL",
                now, ct, ("attempt", attempt), ("worker", workerId), ("deadline", deadline));
            _logger.LogInformation("Job {JobId}: attempt {Attempt} granted to {WorkerId}", id, attempt, workerId);
            return StoreResult<JobAttempt>.Ok(AttemptOf(row with { Attempt = attempt, Deadline = deadline }));
        }, ct);
    }

    private static JobAttempt AttemptOf(JobRow row) => new() { JobId = row.Id, Attempt = row.Attempt, Results = ResultsPrefix.For(row.Id, row.Attempt), Deadline = row.Deadline };

    /// <summary>Live jobs: grants lease epoch N+1 and moves the job to <c>running</c>; 409 while another worker's lease is valid.</summary>
    public async Task<StoreResult<Lease>> AcquireLeaseAsync(Guid id, string workerId, CancellationToken ct)
    {
        var now = Now;
        return await InTransaction(async (conn, tx) =>
        {
            var row = await Lock(conn, tx, id, ct);
            if (row == null) return StoreResult<Lease>.NotFound();
            if (row.Kind != JobKind.Live) return StoreResult<Lease>.Conflict("leases are for live jobs only");
            if (JobStateMachine.IsTerminal(row.Status) || row.CancelRequestedAt != null) return StoreResult<Lease>.Conflict($"job is {JobStateMachine.ToWire(row.Status)}");
            var alive = row.LastHeartbeatAt is { } beat && now - beat < _options.HeartbeatTimeout;
            if (row.WorkerId == workerId && row.LeaseEpoch != null && alive) return StoreResult<Lease>.Ok(LeaseOf(row));
            if (row.WorkerId != null && row.LeaseEpoch != null && alive) return StoreResult<Lease>.Conflict($"lease epoch {row.LeaseEpoch} is held by {row.WorkerId}");
            var attempt = row.Attempt;
            if (row.LeaseEpoch != null)
            {
                attempt++;
                if (attempt > _options.MaxAttempts)
                {
                    await FailLocked(conn, tx, row, new ErrorInfo { Code = ErrorCode.MaxAttemptsExceeded, Message = $"lease lost after {row.Attempt} attempts" }, now, ct);
                    return StoreResult<Lease>.Conflict($"job failed: {_options.MaxAttempts} attempts exhausted");
                }
            }
            var epoch = (row.LeaseEpoch ?? 0) + 1;
            await Update(conn, tx, id,
                "status = 'running', attempt = @attempt, lease_epoch = @epoch, worker_id = @worker, attempt_taken_at = @now, last_heartbeat_at = @now, " +
                "started_at = COALESCE(started_at, @now)", now, ct, ("attempt", attempt), ("epoch", epoch), ("worker", workerId));
            _logger.LogInformation("Job {JobId}: lease epoch {Epoch} (attempt {Attempt}) granted to {WorkerId}", id, epoch, attempt, workerId);
            return StoreResult<Lease>.Ok(LeaseOf(row with { Attempt = attempt, LeaseEpoch = epoch }));
        }, ct);
    }

    private Lease LeaseOf(JobRow row) => new()
    {
        JobId = row.Id, Attempt = row.Attempt, LeaseEpoch = row.LeaseEpoch!.Value, HeartbeatIntervalSeconds = _options.HeartbeatIntervalSeconds,
        Results = ResultsPrefix.For(row.Id, row.Attempt),
    };

    // ---------------------------------------------------------------- lean.events

    /// <summary>Applies one <c>orchestrator.status</c> delivery. Returns false when it was a duplicate, stale or unknown (still acked).</summary>
    public async Task<bool> ApplyEventAsync(MessageEnvelope envelope, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var now = Now;
        var jobId = envelope.Headers.JobId;
        var attempt = envelope.Headers.Attempt;
        return await InTransaction(async (conn, tx) =>
        {
            await using (var seen = new NpgsqlCommand(
                "INSERT INTO job_events (message_id, job_id, attempt, type, received_at, applied) VALUES (@m, @j, @a, @t, @now, false) ON CONFLICT DO NOTHING", conn, tx))
            {
                seen.Parameters.AddWithValue("m", envelope.MessageId);
                seen.Parameters.AddWithValue("j", jobId);
                seen.Parameters.AddWithValue("a", attempt);
                seen.Parameters.AddWithValue("t", envelope.Type);
                seen.Parameters.AddWithValue("now", now);
                if (await seen.ExecuteNonQueryAsync(ct) == 0) return false;
            }
            var row = await Lock(conn, tx, jobId, ct);
            if (row == null || attempt != row.Attempt || JobStateMachine.IsTerminal(row.Status) || envelope.AppId == AppIds.Orchestrator) return false;

            switch (envelope.Type)
            {
                case MessageTypes.JobStarted:
                    var started = ContractJson.Deserialize<JobStarted>(body.Span);
                    await Update(conn, tx, jobId,
                        "status = 'running', started_at = COALESCE(started_at, @at), worker_id = @worker, last_heartbeat_at = @now, " +
                        "attempt_taken_at = COALESCE(attempt_taken_at, @now), lease_epoch = COALESCE(@epoch, lease_epoch)",
                        now, ct, ("at", started.StartedAt), ("worker", started.WorkerId), ("epoch", started.LeaseEpoch));
                    break;
                case MessageTypes.JobHeartbeat:
                    var beat = ContractJson.Deserialize<JobHeartbeat>(body.Span);
                    if (row.LeaseEpoch != null && beat.LeaseEpoch != null && beat.LeaseEpoch != row.LeaseEpoch) return false;
                    await Update(conn, tx, jobId, "last_heartbeat_at = @now, worker_id = COALESCE(worker_id, @worker), " +
                        "status = CASE WHEN status = 'stale' THEN 'running' ELSE status END", now, ct, ("worker", beat.WorkerId));
                    break;
                case MessageTypes.JobProgress:
                    var progress = ContractJson.Deserialize<JobProgress>(body.Span);
                    await Update(conn, tx, jobId, "progress = @progress, last_heartbeat_at = @now", now, ct, ("progress", Math.Clamp(progress.Progress, 0, 1)));
                    break;
                case MessageTypes.JobCompleted:
                    var completed = ContractJson.Deserialize<JobCompleted>(body.Span);
                    await Update(conn, tx, jobId, "status = 'completed', finished_at = @at, progress = 1, error = NULL, result = @result",
                        now, ct, ("at", completed.CompletedAt), ("result", Json(completed.Result)));
                    _logger.LogInformation("Job {JobId} attempt {Attempt} completed", jobId, attempt);
                    break;
                case MessageTypes.JobFailed:
                    var failed = ContractJson.Deserialize<JobFailed>(body.Span);
                    if (failed.Final || row.CancelRequestedAt != null || row.Attempt >= _options.MaxAttempts)
                    {
                        await Update(conn, tx, jobId, "status = 'failed', finished_at = @at, error = @error", now, ct, ("at", failed.FailedAt), ("error", Json(failed.Error)));
                        _logger.LogWarning("Job {JobId} attempt {Attempt} failed: {Code} {Message}", jobId, attempt, failed.Error.Code, failed.Error.Message);
                    }
                    else
                    {
                        await RetryLocked(conn, tx, row, $"attempt failed ({failed.Error.Message})", now, ct);
                    }
                    break;
                case MessageTypes.JobCancelled:
                    var cancelled = ContractJson.Deserialize<JobCancelled>(body.Span);
                    await Update(conn, tx, jobId, "status = 'cancelled', finished_at = @at, cancel_reason = COALESCE(cancel_reason, @reason)",
                        now, ct, ("at", cancelled.CancelledAt), ("reason", cancelled.Reason));
                    _logger.LogInformation("Job {JobId} attempt {Attempt} cancelled: {Reason}", jobId, attempt, cancelled.Reason);
                    break;
                default:
                    return false;
            }
            await using var applied = new NpgsqlCommand("UPDATE job_events SET applied = true WHERE message_id = @m", conn, tx);
            applied.Parameters.AddWithValue("m", envelope.MessageId);
            await applied.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    // ---------------------------------------------------------------- liveness

    /// <summary>
    /// One monitor pass: an attempt held by a worker whose heartbeat is older than the timeout is dead; it is retried as a new
    /// attempt (or the job fails with <c>max-attempts-exceeded</c>). An attempt past its deadline is cancelled and failed with <c>timeout</c>.
    /// </summary>
    public async Task<int> MonitorAsync(CancellationToken ct)
    {
        var now = Now;
        return await InTransaction(async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT {Columns} FROM jobs WHERE status IN ('dispatched','running','stale') AND worker_id IS NOT NULL " +
                "AND (COALESCE(last_heartbeat_at, attempt_taken_at) < @cutoff OR deadline < @now) FOR UPDATE SKIP LOCKED", conn, tx);
            cmd.Parameters.AddWithValue("cutoff", now - _options.HeartbeatTimeout);
            cmd.Parameters.AddWithValue("now", now);
            var rows = new List<JobRow>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) rows.Add(JobRow.Read(reader, 0));
            }
            foreach (var row in rows)
            {
                if (row.Deadline is { } deadline && deadline < now)
                {
                    await Enqueue(conn, tx, row.Id, JobStateMachine.Cancel(row.Id, row.Kind, row.Attempt, row.LeaseEpoch, "deadline exceeded", AppIds.Orchestrator, now), ct);
                    await FailLocked(conn, tx, row, new ErrorInfo { Code = ErrorCode.Timeout, Message = $"attempt {row.Attempt} exceeded its deadline {deadline:O}" }, now, ct);
                }
                else if (row.CancelRequestedAt != null)
                {
                    await Update(conn, tx, row.Id, "status = 'cancelled', finished_at = @now", now, ct);
                    await Enqueue(conn, tx, row.Id, JobStateMachine.Cancelled(row.Id, row.Kind, row.Attempt, row.ParentJobId, row.CancelReason ?? "cancelled", now), ct);
                }
                else
                {
                    var silentFor = now - (row.LastHeartbeatAt ?? row.AttemptTakenAt ?? now);
                    await RetryLocked(conn, tx, row, $"no heartbeat from {row.WorkerId} for {silentFor.TotalSeconds:0}s", now, ct);
                }
            }
            return rows.Count;
        }, ct);
    }

    private async Task RetryLocked(NpgsqlConnection conn, NpgsqlTransaction tx, JobRow row, string why, DateTimeOffset now, CancellationToken ct)
    {
        var redeploy = row.Kind != JobKind.Live || row.Spec.Live?.RedeployOnStale != false;
        if (row.Attempt >= _options.MaxAttempts || !redeploy)
        {
            var code = redeploy ? ErrorCode.MaxAttemptsExceeded : ErrorCode.LeaseLost;
            await FailLocked(conn, tx, row, new ErrorInfo { Code = code, Message = $"attempt {row.Attempt}/{_options.MaxAttempts}: {why}" }, now, ct);
            return;
        }
        var next = row.Attempt + 1;
        await Enqueue(conn, tx, row.Id, JobStateMachine.Cancel(row.Id, row.Kind, row.Attempt, row.LeaseEpoch, $"superseded: {why}", AppIds.Orchestrator, now), ct);
        await Update(conn, tx, row.Id,
            "status = 'queued', attempt = @attempt, worker_id = NULL, attempt_taken_at = NULL, last_heartbeat_at = NULL, deadline = NULL, progress = NULL",
            now, ct, ("attempt", next));
        await Enqueue(conn, tx, row.Id, JobStateMachine.Dispatch(row.Id, row.Spec, next, row.SubmittedBy, now, row.Inputs), ct);
        _logger.LogWarning("Job {JobId}: attempt {Attempt} lost ({Why}); retrying as attempt {Next}/{Max}", row.Id, row.Attempt, why, next, _options.MaxAttempts);
    }

    private async Task FailLocked(NpgsqlConnection conn, NpgsqlTransaction tx, JobRow row, ErrorInfo error, DateTimeOffset now, CancellationToken ct)
    {
        await Update(conn, tx, row.Id, "status = 'failed', finished_at = @now, error = @error", now, ct, ("error", Json(error)));
        await Enqueue(conn, tx, row.Id, JobStateMachine.Failed(row.Id, row.Kind, row.Attempt, row.ParentJobId, error, now), ct);
        _logger.LogWarning("Job {JobId} failed on attempt {Attempt}: {Code} {Message}", row.Id, row.Attempt, error.Code, error.Message);
    }

    // ---------------------------------------------------------------- outbox

    public async Task<List<OutboxRow>> UnsentAsync(int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT id, job_id, exchange, routing_key, envelope, body, mandatory FROM outbox WHERE sent_at IS NULL ORDER BY id LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit);
        var rows = new List<OutboxRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OutboxRow(reader.GetInt64(0), reader.GetGuid(1), new OutgoingMessage
            {
                Exchange = reader.GetString(2), RoutingKey = reader.GetString(3),
                Envelope = ContractJson.Deserialize<MessageEnvelope>(reader.GetString(4)), Body = reader.GetFieldValue<byte[]>(5), Mandatory = reader.GetBoolean(6),
            }));
        }
        return rows;
    }

    /// <summary>Marks a confirmed publish; a confirmed dispatch of the job's current attempt moves it <c>queued → dispatched</c>.</summary>
    public async Task MarkSentAsync(OutboxRow row, CancellationToken ct)
    {
        var now = Now;
        await InTransaction(async (conn, tx) =>
        {
            await using (var sent = new NpgsqlCommand("UPDATE outbox SET sent_at = @now, publish_attempts = publish_attempts + 1, last_error = NULL WHERE id = @id", conn, tx))
            {
                sent.Parameters.AddWithValue("now", now);
                sent.Parameters.AddWithValue("id", row.Id);
                await sent.ExecuteNonQueryAsync(ct);
            }
            if (JobStateMachine.IsDispatch(row.Message.Envelope.Type))
            {
                await using var dispatched = new NpgsqlCommand(
                    "UPDATE jobs SET status = 'dispatched', updated_at = @now WHERE id = @id AND attempt = @attempt AND status = 'queued'", conn, tx);
                dispatched.Parameters.AddWithValue("now", now);
                dispatched.Parameters.AddWithValue("id", row.JobId);
                dispatched.Parameters.AddWithValue("attempt", row.Message.Envelope.Headers.Attempt);
                await dispatched.ExecuteNonQueryAsync(ct);
            }
            return 0;
        }, ct, notify: false);
    }

    public async Task MarkFailedAsync(OutboxRow row, string error, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("UPDATE outbox SET publish_attempts = publish_attempts + 1, last_error = @error WHERE id = @id");
        cmd.Parameters.AddWithValue("error", error.Length > 2000 ? error[..2000] : error);
        cmd.Parameters.AddWithValue("id", row.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Job counts per kind and status, for the <c>lean_jobs</c> metric.</summary>
    public async Task<List<(string Kind, string Status, long Count)>> CountsAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT kind, status, count(*) FROM jobs GROUP BY kind, status ORDER BY kind, status");
        var counts = new List<(string, string, long)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) counts.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return counts;
    }

    public async Task<long> OutboxBacklogAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT count(*) FROM outbox WHERE sent_at IS NULL");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<T> InTransaction<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct, bool notify = true)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        if (notify) OutboxWritten?.Invoke();
        return result;
    }

    private async Task Enqueue(NpgsqlConnection conn, NpgsqlTransaction tx, Guid jobId, OutgoingMessage message, CancellationToken ct)
    {
        message.Validate();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO outbox (job_id, exchange, routing_key, envelope, body, mandatory, created_at) VALUES (@job, @exchange, @key, @envelope, @body, @mandatory, @now)", conn, tx);
        cmd.Parameters.AddWithValue("job", jobId);
        cmd.Parameters.AddWithValue("exchange", message.Exchange);
        cmd.Parameters.AddWithValue("key", message.RoutingKey);
        cmd.Parameters.Add(new NpgsqlParameter("envelope", NpgsqlDbType.Jsonb) { Value = ContractJson.Serialize(message.Envelope) });
        cmd.Parameters.AddWithValue("body", message.Body.ToArray());
        cmd.Parameters.AddWithValue("mandatory", message.Mandatory);
        cmd.Parameters.AddWithValue("now", Now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Attempt of the newest dispatch message written for the job (a redelivered older dispatch may still be the live one).</summary>
    private static async Task<int> LatestDispatchAttempt(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT coalesce(max((envelope->'headers'->>'attempt')::int), 1) FROM outbox WHERE job_id = @id AND exchange = @exchange", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("exchange", Exchanges.Jobs);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<JobRow?> Lock(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct, bool forUpdate = true)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM jobs WHERE id = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? JobRow.Read(reader, 0) : null;
    }

    private static async Task Update(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string set, DateTimeOffset now, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand($"UPDATE jobs SET {set}, updated_at = @now WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", now);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.Add(value is JsonText json
                ? new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = json.Value }
                : new NpgsqlParameter(name, value ?? DBNull.Value));
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record JsonText(string Value);

    private static JsonText Json<T>(T value) => new(ContractJson.Serialize(value));

    private sealed record JobRow
    {
        public Guid Id { get; init; }
        public JobKind Kind { get; init; }
        public JobStatus Status { get; init; }
        public int Attempt { get; init; }
        public Guid? ParentJobId { get; init; }
        public string? Name { get; init; }
        public required string SubmittedBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public DateTimeOffset? Deadline { get; init; }
        public double? Progress { get; init; }
        public long? LeaseEpoch { get; init; }
        public string? WorkerId { get; init; }
        public DateTimeOffset? AttemptTakenAt { get; init; }
        public DateTimeOffset? LastHeartbeatAt { get; init; }
        public DateTimeOffset? CancelRequestedAt { get; init; }
        public string? CancelReason { get; init; }
        public ErrorInfo? Error { get; init; }
        public ObjectRef? Result { get; init; }
        public JsonElement? Inputs { get; init; }
        public required JobRequest Spec { get; init; }

        public static JobRow Read(NpgsqlDataReader r, int o)
        {
            T? N<T>(int i) => r.IsDBNull(o + i) ? default : r.GetFieldValue<T>(o + i);
            DateTimeOffset? D(int i) => r.IsDBNull(o + i) ? null : r.GetFieldValue<DateTimeOffset>(o + i);
            return new JobRow
            {
                Id = r.GetGuid(o), Kind = JobStateMachine.ParseKind(r.GetString(o + 1)), Status = JobStateMachine.ParseStatus(r.GetString(o + 2)),
                Attempt = r.GetInt32(o + 3), ParentJobId = r.IsDBNull(o + 4) ? null : r.GetGuid(o + 4), Name = N<string>(5), SubmittedBy = r.GetString(o + 6),
                CreatedAt = r.GetFieldValue<DateTimeOffset>(o + 7), StartedAt = D(8), FinishedAt = D(9), Deadline = D(10),
                Progress = r.IsDBNull(o + 11) ? null : r.GetDouble(o + 11), LeaseEpoch = r.IsDBNull(o + 12) ? null : r.GetInt64(o + 12), WorkerId = N<string>(13),
                AttemptTakenAt = D(14), LastHeartbeatAt = D(15), CancelRequestedAt = D(16), CancelReason = N<string>(17),
                Error = r.IsDBNull(o + 18) ? null : ContractJson.Deserialize<ErrorInfo>(r.GetString(o + 18)),
                Result = r.IsDBNull(o + 19) ? null : ContractJson.Deserialize<ObjectRef>(r.GetString(o + 19)),
                Inputs = r.IsDBNull(o + 20) ? null : JsonDocument.Parse(r.GetString(o + 20)).RootElement.Clone(),
                Spec = ContractJson.Deserialize<JobRequest>(r.GetString(o + 21)),
            };
        }

        public Job ToJob() => new()
        {
            Id = Id, Kind = Kind, Status = Status, Attempt = Attempt, ParentJobId = ParentJobId, Name = Name, SubmittedBy = SubmittedBy,
            CreatedAt = CreatedAt, StartedAt = StartedAt, FinishedAt = FinishedAt, Deadline = Deadline, Progress = Progress,
            LeaseEpoch = LeaseEpoch, WorkerId = WorkerId, Error = Error, Spec = Spec,
        };
    }
}
