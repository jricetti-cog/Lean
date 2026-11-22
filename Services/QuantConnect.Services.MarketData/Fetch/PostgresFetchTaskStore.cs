using Npgsql;
using NpgsqlTypes;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.MarketData.Fetch;

/// <summary>
/// <see cref="IFetchTaskStore"/> on the <c>marketdata</c> PostgreSQL database. The schema is applied idempotently at
/// startup; task row and outbox row are written in one transaction, coalescing under an advisory lock on the request.
/// </summary>
public sealed class PostgresFetchTaskStore : IFetchTaskStore, IAsyncDisposable
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS fetch_tasks (
            id uuid PRIMARY KEY,
            status text NOT NULL,
            attempt integer NOT NULL DEFAULT 1,
            spec jsonb NOT NULL,
            dedupe_key text NOT NULL,
            requesting_job_id uuid NULL,
            submitted_by text NOT NULL,
            idempotency_key text NULL UNIQUE,
            files_written bigint NULL,
            error jsonb NULL,
            created_at timestamptz NOT NULL,
            finished_at timestamptz NULL);
        CREATE INDEX IF NOT EXISTS fetch_tasks_pending ON fetch_tasks (dedupe_key) WHERE status IN ('queued', 'running');
        CREATE INDEX IF NOT EXISTS fetch_tasks_created ON fetch_tasks (created_at DESC, id DESC);
        CREATE TABLE IF NOT EXISTS fetch_task_files (
            task_id uuid NOT NULL REFERENCES fetch_tasks (id) ON DELETE CASCADE,
            path text NOT NULL,
            PRIMARY KEY (task_id, path));
        CREATE TABLE IF NOT EXISTS outbox (
            id uuid PRIMARY KEY,
            exchange text NOT NULL,
            routing_key text NOT NULL,
            type text NOT NULL,
            job_id uuid NOT NULL,
            attempt integer NOT NULL,
            body bytea NOT NULL,
            created_at timestamptz NOT NULL,
            sent_at timestamptz NULL);
        CREATE INDEX IF NOT EXISTS outbox_pending ON outbox (created_at) WHERE sent_at IS NULL;
        CREATE TABLE IF NOT EXISTS synthetic_tickers (
            security_type text NOT NULL,
            market text NOT NULL,
            ticker text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (security_type, market, ticker));
        """;

    private const string Columns = "id, status, attempt, spec, requesting_job_id, files_written, error, created_at, finished_at";
    private readonly NpgsqlDataSource _dataSource;

    public PostgresFetchTaskStore(string connectionString) => _dataSource = NpgsqlDataSource.Create(connectionString);

    public NpgsqlDataSource DataSource => _dataSource;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(Schema);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(FetchTask Task, bool Created)> CreateAsync(MarketDataFetchSpec spec, Guid? requestingJobId, string submittedBy, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var normalized = FetchTasks.Normalize(spec);
        var key = FetchTasks.DedupeKey(normalized);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", idempotencyKey ?? key);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (idempotencyKey != null)
        {
            await using var lock2 = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx);
            lock2.Parameters.AddWithValue("k", key);
            await lock2.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var find = new NpgsqlCommand(
            $"SELECT {Columns} FROM fetch_tasks WHERE (@ik::text IS NOT NULL AND idempotency_key = @ik) " +
            "OR (dedupe_key = @k AND status IN ('queued', 'running')) ORDER BY (idempotency_key = @ik) DESC NULLS LAST, created_at DESC LIMIT 1", conn, tx))
        {
            find.Parameters.AddWithValue("ik", NpgsqlDbType.Text, (object?)idempotencyKey ?? DBNull.Value);
            find.Parameters.AddWithValue("k", key);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existing = Read(reader);
                await reader.DisposeAsync().ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (existing, false);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var task = new FetchTask { Id = Guid.NewGuid(), Status = JobStatus.Queued, Attempt = 1, Fetch = normalized, RequestingJobId = requestingJobId, CreatedAt = now };
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO fetch_tasks (id, status, attempt, spec, dedupe_key, requesting_job_id, submitted_by, idempotency_key, created_at) " +
            "VALUES (@id, 'queued', 1, @spec, @k, @rj, @by, @ik, @at)", conn, tx))
        {
            insert.Parameters.AddWithValue("id", task.Id);
            insert.Parameters.AddWithValue("spec", NpgsqlDbType.Jsonb, ContractJson.Serialize(normalized));
            insert.Parameters.AddWithValue("k", key);
            insert.Parameters.AddWithValue("rj", NpgsqlDbType.Uuid, (object?)requestingJobId ?? DBNull.Value);
            insert.Parameters.AddWithValue("by", submittedBy);
            insert.Parameters.AddWithValue("ik", NpgsqlDbType.Text, (object?)idempotencyKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var message = FetchTasks.Requested(task.Id, normalized, requestingJobId, submittedBy, now);
        await using (var outbox = new NpgsqlCommand(
            "INSERT INTO outbox (id, exchange, routing_key, type, job_id, attempt, body, created_at) VALUES (@id, @ex, @rk, @t, @j, @a, @b, @at)", conn, tx))
        {
            outbox.Parameters.AddWithValue("id", message.Id);
            outbox.Parameters.AddWithValue("ex", message.Exchange);
            outbox.Parameters.AddWithValue("rk", message.RoutingKey);
            outbox.Parameters.AddWithValue("t", message.Type);
            outbox.Parameters.AddWithValue("j", message.JobId);
            outbox.Parameters.AddWithValue("a", message.Attempt);
            outbox.Parameters.AddWithValue("b", message.Body);
            outbox.Parameters.AddWithValue("at", now);
            await outbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (task, true);
    }

    public async Task<FetchTask?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand($"SELECT {Columns} FROM fetch_tasks WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<(List<FetchTask> Items, string? Next)> ListAsync(JobStatus? status, string? cursor, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT {Columns} FROM fetch_tasks t WHERE (@s::text IS NULL OR status = @s) " +
            "AND (@c::uuid IS NULL OR (created_at, id) < (SELECT created_at, id FROM fetch_tasks WHERE id = @c)) " +
            "ORDER BY created_at DESC, id DESC LIMIT @n");
        cmd.Parameters.AddWithValue("s", NpgsqlDbType.Text, status.HasValue ? FetchTasks.StatusText(status.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Uuid, Guid.TryParse(cursor, out var c) ? c : DBNull.Value);
        cmd.Parameters.AddWithValue("n", limit + 1);
        var items = new List<FetchTask>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Read(reader));
        }
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
            next = items[^1].Id.ToString("D");
        }
        return (items, next);
    }

    public async Task<bool> TryStartAsync(Guid id, int attempt, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(
            "UPDATE fetch_tasks SET status = 'running', attempt = @a, error = NULL WHERE id = @id AND status IN ('queued', 'running')");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("a", attempt);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task CompleteAsync(Guid id, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand(
            "UPDATE fetch_tasks SET status = 'completed', files_written = @n, error = NULL, finished_at = now() WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", (long)files.Count);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (files.Count > 0)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO fetch_task_files (task_id, path) SELECT @id, unnest(@p) ON CONFLICT DO NOTHING", conn, tx);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("p", files.ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(Guid id, ErrorInfo error, bool final, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(
            "UPDATE fetch_tasks SET status = @s, error = @e, finished_at = CASE WHEN @f THEN now() ELSE NULL END WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", FetchTasks.StatusText(final ? JobStatus.Failed : JobStatus.Queued));
        cmd.Parameters.AddWithValue("e", NpgsqlDbType.Jsonb, ContractJson.Serialize(error));
        cmd.Parameters.AddWithValue("f", final);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> PendingOutboxAsync(int max, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id, exchange, routing_key, type, job_id, attempt, body FROM outbox WHERE sent_at IS NULL ORDER BY created_at LIMIT @n");
        cmd.Parameters.AddWithValue("n", max);
        var messages = new List<OutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new OutboxMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetGuid(4), reader.GetInt32(5), (byte[])reader[6]));
        }
        return messages;
    }

    public async Task MarkSentAsync(Guid outboxId, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand("UPDATE outbox SET sent_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("id", outboxId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSyntheticAsync(string securityType, string market, string ticker, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(
            "INSERT INTO synthetic_tickers (security_type, market, ticker) VALUES (@s, @m, @t) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue("s", securityType.ToLowerInvariant());
        cmd.Parameters.AddWithValue("m", market.ToLowerInvariant());
        cmd.Parameters.AddWithValue("t", ticker.ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsSyntheticAsync(string securityType, string market, string ticker, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM synthetic_tickers WHERE security_type = @s AND market = @m AND ticker = @t)");
        cmd.Parameters.AddWithValue("s", securityType.ToLowerInvariant());
        cmd.Parameters.AddWithValue("m", market.ToLowerInvariant());
        cmd.Parameters.AddWithValue("t", ticker.ToLowerInvariant());
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static FetchTask Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Status = FetchTasks.ParseStatus(reader.GetString(1)),
        Attempt = reader.GetInt32(2),
        Fetch = ContractJson.Deserialize<MarketDataFetchSpec>(reader.GetString(3)),
        RequestingJobId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
        FilesWritten = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        Error = reader.IsDBNull(6) ? null : ContractJson.Deserialize<ErrorInfo>(reader.GetString(6)),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
        FinishedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
    };

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
