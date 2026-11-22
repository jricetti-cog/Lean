using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Results.Storage;

namespace QuantConnect.Services.Results;

/// <summary>Reads behind the results API (<c>docs/architecture/contracts/openapi/results.openapi.yaml</c>).</summary>
public sealed class ResultsQueries(ResultsDatabase database, IResultObjectStore store, ResultsSettings settings)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    private const string SummaryColumns = """
        job_id, attempt, kind, parent_job_id, status, algorithm_id, progress, parameters, statistics, runtime_statistics,
        order_count, result_ref, updated_at, order_list_hash
        """;

    private NpgsqlDataSource Db => database.DataSource;

    /// <summary>The requested attempt, else the latest completed attempt, else the latest attempt; null when unknown.</summary>
    public async Task<int?> ResolveAttemptAsync(Guid jobId, int? attempt, CancellationToken cancellationToken = default)
    {
        await using var command = Db.CreateCommand("""
            SELECT attempt FROM results WHERE job_id = @job AND (@attempt::integer IS NULL OR attempt = @attempt)
            ORDER BY (status = 'completed') DESC, attempt DESC LIMIT 1
            """).With("job", jobId).With("attempt", attempt);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as int?;
    }

    public async Task<Page<ResultSummary>> ListAsync(Guid? parentJobId, Guid[]? jobIds, string? cursor, int? limit, CancellationToken cancellationToken = default)
    {
        var take = ClampLimit(limit);
        Guid? after = cursor == null ? null : Guid.Parse(DecodeCursor(cursor));
        await using var command = Db.CreateCommand($"""
            SELECT DISTINCT ON (job_id) {SummaryColumns} FROM results
            WHERE (@parent::uuid IS NULL OR parent_job_id = @parent)
              AND (@ids::uuid[] IS NULL OR job_id = ANY(@ids))
              AND (@after::uuid IS NULL OR job_id > @after)
            ORDER BY job_id, (status = 'completed') DESC, attempt DESC
            LIMIT @take
            """).With("parent", parentJobId).With("after", after).With("take", take + 1);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = jobIds is { Length: > 0 } ? jobIds : DBNull.Value });
        var items = await ReadSummaries(command, cancellationToken).ConfigureAwait(false);
        return Paged(items, take, s => s.JobId.ToString("D"));
    }

    public async Task<ResultSummary?> GetAsync(Guid jobId, int attempt, CancellationToken cancellationToken = default)
    {
        await using var command = Db.CreateCommand($"SELECT {SummaryColumns} FROM results WHERE job_id = @job AND attempt = @attempt")
            .With("job", jobId).With("attempt", attempt);
        return (await ReadSummaries(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<Page<JsonNode>> OrdersAsync(Guid jobId, int attempt, string? cursor, int? limit, CancellationToken cancellationToken = default)
    {
        var take = ClampLimit(limit);
        int? after = cursor == null ? null : int.Parse(DecodeCursor(cursor));
        await using var command = Db.CreateCommand("""
            SELECT order_id, body FROM orders WHERE job_id = @job AND attempt = @attempt AND (@after::integer IS NULL OR order_id > @after)
            ORDER BY order_id LIMIT @take
            """).With("job", jobId).With("attempt", attempt).With("after", after).With("take", take + 1);
        var rows = new List<(string Key, JsonNode Body)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetInt32(0).ToString(), JsonNode.Parse(reader.GetString(1))!));
        }
        return PagedRows(rows, take);
    }

    public async Task<Page<JsonNode>> OrderEventsAsync(Guid jobId, int attempt, string? cursor, int? limit, CancellationToken cancellationToken = default)
    {
        var take = ClampLimit(limit);
        DateTime? afterTime = null;
        int afterOrder = 0, afterEvent = 0;
        if (cursor != null)
        {
            var parts = DecodeCursor(cursor).Split('|');
            afterTime = new DateTime(long.Parse(parts[0]), DateTimeKind.Utc);
            afterOrder = int.Parse(parts[1]);
            afterEvent = int.Parse(parts[2]);
        }
        await using var command = Db.CreateCommand("""
            SELECT utc_time, order_id, event_id, body FROM order_events
            WHERE job_id = @job AND attempt = @attempt
              AND (@time::timestamptz IS NULL OR (utc_time, order_id, event_id) > (@time, @order, @event))
            ORDER BY utc_time, order_id, event_id LIMIT @take
            """).With("job", jobId).With("attempt", attempt).With("time", afterTime).With("order", afterOrder).With("event", afterEvent).With("take", take + 1);
        var rows = new List<(string Key, JsonNode Body)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(($"{reader.GetDateTime(0).Ticks}|{reader.GetInt32(1)}|{reader.GetInt32(2)}", JsonNode.Parse(reader.GetString(3))!));
        }
        return PagedRows(rows, take);
    }

    public async Task<Page<LogLine>> LogsAsync(Guid jobId, int attempt, string? level, string? cursor, int? limit, CancellationToken cancellationToken = default)
    {
        var take = ClampLimit(limit);
        long? after = cursor == null ? null : long.Parse(DecodeCursor(cursor));
        await using var command = Db.CreateCommand("""
            SELECT id, time, type, message, stack_trace FROM logs
            WHERE job_id = @job AND attempt = @attempt AND (@level::text IS NULL OR type = @level) AND (@after::bigint IS NULL OR id > @after)
            ORDER BY id LIMIT @take
            """).With("job", jobId).With("attempt", attempt).With("level", level).With("after", after).With("take", take + 1);
        var rows = new List<(long Id, LogLine Line)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetInt64(0), new LogLine
            {
                Time = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)),
                Type = reader.GetString(2),
                Message = reader.GetString(3),
                StackTrace = reader.IsDBNull(4) ? null : reader.GetString(4),
            }));
        }
        var page = Paged(rows, take, r => r.Id.ToString());
        return new Page<LogLine> { Items = page.Items.Select(r => r.Line).ToList(), Next = page.Next };
    }

    public async Task<List<JsonObject>> ChartsAsync(Guid jobId, int attempt, CancellationToken cancellationToken = default)
    {
        await using var command = Db.CreateCommand("""
            SELECT name, COALESCE((SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body -> 'series') AS k), '{}') FROM charts
            WHERE job_id = @job AND attempt = @attempt ORDER BY name
            """).With("job", jobId).With("attempt", attempt);
        var charts = new List<JsonObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            charts.Add(new JsonObject
            {
                ["name"] = reader.GetString(0),
                ["series"] = new JsonArray(reader.GetFieldValue<string[]>(1).Select(s => (JsonNode)s).ToArray()),
            });
        }
        return charts;
    }

    /// <summary>
    /// One chart. The PostgreSQL copy is downsampled to <see cref="ResultsSettings.ChartMaxPoints"/> per series; when the
    /// caller wants more points than that copy holds, the chart is read at full resolution from the result JSON in MinIO.
    /// </summary>
    public async Task<JsonObject?> ChartAsync(Guid jobId, int attempt, string name, int? maxPoints, CancellationToken cancellationToken = default)
    {
        await using var command = Db.CreateCommand("""
            SELECT c.body, c.point_count, r.result_ref, r.final_indexed FROM charts c JOIN results r USING (job_id, attempt)
            WHERE c.job_id = @job AND c.attempt = @attempt AND c.name = @name
            """).With("job", jobId).With("attempt", attempt).With("name", name);
        JsonObject chart;
        int fullPoints;
        ObjectRef? resultRef;
        bool finalIndexed;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            chart = (JsonObject)JsonNode.Parse(reader.GetString(0))!;
            fullPoints = reader.GetInt32(1);
            resultRef = reader.Json<ObjectRef>(2);
            finalIndexed = reader.GetBoolean(3);
        }

        var stored = ResultsIngestor.LongestSeries(chart);
        var wanted = maxPoints ?? int.MaxValue;
        if (fullPoints > stored && wanted > stored && finalIndexed && resultRef != null)
        {
            var content = LeanJson.ParseObject(await store.GetVerifiedAsync(resultRef, cancellationToken).ConfigureAwait(false));
            if (ResultsIngestor.ResultFileRoot(content).GetObject("Charts")?.GetObject(name) is { } full)
            {
                chart = LeanJson.NormalizeChart(name, full);
            }
        }
        if (maxPoints is { } max)
        {
            Downsampler.DownsampleChart(chart, max);
        }
        return chart;
    }

    /// <summary>The newest stored live portfolio of a live deployment across its attempts; null when none was received.</summary>
    public async Task<LiveState?> LiveStateAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var command = Db.CreateCommand($"""
            SELECT attempt, state -> '{ResultsIngestor.LiveStateKey}' FROM results
            WHERE job_id = @job AND state ? '{ResultsIngestor.LiveStateKey}'
            ORDER BY attempt DESC LIMIT 1
            """).With("job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.Json<JsonObject>(1) is not { } live)
        {
            return null;
        }
        return new LiveState
        {
            JobId = jobId,
            Attempt = reader.GetInt32(0),
            Holdings = live.GetArray("holdings")?.OfType<JsonObject>().Select(h => (JsonObject)h.DeepClone()).ToList() ?? [],
            Cash = live.GetArray("cash")?.OfType<JsonObject>()
                .Select(c => new LiveCashAmount { Currency = c.GetString("currency") ?? "", Amount = (decimal)(c.GetDouble("amount") ?? 0) }).ToList() ?? [],
            UpdatedAt = LeanJson.AsTime(live.Get("at")),
        };
    }

    public async Task<List<ObjectRef>> ArtifactsAsync(Guid jobId, int attempt, CancellationToken cancellationToken = default)
    {
        await using var command = Db.CreateCommand("SELECT results_prefix, result_ref, artifacts FROM results WHERE job_id = @job AND attempt = @attempt")
            .With("job", jobId).With("attempt", attempt);
        ResultsPrefix prefix = ResultsPrefix.For(jobId, attempt) with { Bucket = settings.ResultsBucket };
        var known = new Dictionary<string, ObjectRef>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                prefix = reader.Json<ResultsPrefix>(0) ?? prefix;
                foreach (var reference in (reader.Json<List<ObjectRef>>(2) ?? []).Append(reader.Json<ObjectRef>(1)).OfType<ObjectRef>())
                {
                    known[reference.Key] = reference;
                }
            }
        }
        return await store.ListAsync(prefix.Bucket, prefix.Prefix, known, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<ResultSummary>> ReadSummaries(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var items = new List<ResultSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var statistics = reader.Json<Dictionary<string, string>>(8) ?? [];
            // OrderListHash is captured from the final packet (Messaging.Send adds it there) for parity checks.
            if (!reader.IsDBNull(13))
            {
                statistics.TryAdd(ResultsIngestor.OrderListHashKey, reader.GetString(13));
            }
            items.Add(new ResultSummary
            {
                JobId = reader.GetGuid(0),
                Attempt = reader.GetInt32(1),
                Kind = Sql.Parse<JobKind>(reader.GetString(2)),
                ParentJobId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                Status = Sql.Parse<JobStatus>(reader.GetString(4)),
                AlgorithmId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Progress = reader.GetDouble(6),
                Parameters = reader.Json<Dictionary<string, string>>(7),
                Statistics = statistics,
                RuntimeStatistics = reader.Json<Dictionary<string, string>>(9) ?? [],
                OrderCount = reader.GetInt64(10),
                Result = reader.Json<ObjectRef>(11),
                UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc)),
            });
        }
        return items;
    }

    private static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private static Page<T> Paged<T>(List<T> rows, int take, Func<T, string> key) => new()
    {
        Items = rows.Take(take).ToList(),
        Next = rows.Count > take ? EncodeCursor(key(rows[take - 1])) : null,
    };

    private static Page<JsonNode> PagedRows(List<(string Key, JsonNode Body)> rows, int take)
    {
        var page = Paged(rows, take, r => r.Key);
        return new Page<JsonNode> { Items = page.Items.Select(r => r.Body).ToList(), Next = page.Next };
    }

    public static string EncodeCursor(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string DecodeCursor(string cursor)
    {
        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
