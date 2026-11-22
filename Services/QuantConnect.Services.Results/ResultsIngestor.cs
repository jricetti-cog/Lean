using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Results.Storage;

namespace QuantConnect.Services.Results;

/// <summary>
/// Applies <c>results.packets</c> deliveries (inline LEAN packets, <c>PacketRef</c>s and <c>job.*</c> status events) to the
/// results database. Each message is applied in one transaction together with its idempotency key — the packet
/// <c>sequence</c> header, or the message id for messages without one — so a redelivery (after a crash between commit
/// and ack, or a broker requeue) is acknowledged without being applied twice. The delivery is acked only after commit.
/// </summary>
public sealed class ResultsIngestor(ResultsDatabase database, IResultObjectStore store, ResultsSettings settings, ILogger<ResultsIngestor> logger)
{
    /// <summary>Orders from the final result always win over (redelivered) partial packets.</summary>
    public const long FinalOrderSequence = long.MaxValue;

    private static readonly string[] LogPacketTypes = ["Log", "Debug", "SystemDebug", "RuntimeError", "HandledError"];

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var applied = await IngestAsync(message, cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                logger.LogInformation("Duplicate {Type} for job {JobId} attempt {Attempt} (sequence {Sequence}) acked without reapplying",
                    message.Envelope.Type, message.Envelope.Headers.JobId, message.Envelope.Headers.Attempt, message.Envelope.Headers.Sequence);
            }
            return MessageDisposition.Ack;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException)
        {
            logger.LogError(e, "Unprocessable {Type} {MessageId}: dead-lettering", message.Envelope.Type, message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to apply {Type} {MessageId}: retrying", message.Envelope.Type, message.Envelope.MessageId);
            return MessageDisposition.Retry;
        }
    }

    /// <summary>Applies one message; false when it was already applied (duplicate sequence / message id).</summary>
    public async Task<bool> IngestAsync(ReceivedMessage message, CancellationToken cancellationToken = default)
    {
        var envelope = message.Envelope;
        var headers = envelope.Headers;
        var context = new IngestContext(
            headers.JobId,
            headers.Attempt,
            headers.JobKind ?? JobKind.Backtest,
            headers.ParentJobId,
            headers.Sequence,
            headers.Sequence is { } sequence ? $"seq:{sequence}" : $"msg:{envelope.MessageId:D}",
            envelope.Type,
            DateTimeOffset.FromUnixTimeSeconds(envelope.Timestamp));

        var apply = await PrepareAsync(context, message.Body.ToArray(), cancellationToken).ConfigureAwait(false);
        if (apply == null)
        {
            return true;
        }

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.Command(transaction, """
            INSERT INTO results (job_id, attempt, kind, parent_job_id, status, results_prefix)
            VALUES (@job, @attempt, @kind, @parent, 'running', @prefix)
            ON CONFLICT (job_id, attempt) DO NOTHING
            """)
            .With("job", context.JobId).With("attempt", context.Attempt).With("kind", Sql.Name(context.Kind)).With("parent", context.ParentJobId)
            .WithJson("prefix", ResultsPrefix.For(context.JobId, context.Attempt) with { Bucket = settings.ResultsBucket })
            .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var inserted = await connection.Command(transaction, """
            INSERT INTO processed_messages (job_id, attempt, dedup_key, message_type) VALUES (@job, @attempt, @key, @type)
            ON CONFLICT DO NOTHING
            """)
            .With("job", context.JobId).With("attempt", context.Attempt).With("key", context.DedupKey).With("type", context.Type)
            .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        ResultRow row;
        await using (var command = connection.Command(transaction,
            "SELECT last_sequence, final_indexed FROM results WHERE job_id = @job AND attempt = @attempt FOR UPDATE")
            .With("job", context.JobId).With("attempt", context.Attempt))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            row = new ResultRow(reader.GetInt64(0), reader.GetBoolean(1));
        }

        await apply(new Session(connection, transaction, context, row, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Parses the message and does the object-store I/O outside the transaction.</summary>
    private async Task<Func<Session, Task>?> PrepareAsync(IngestContext context, byte[] body, CancellationToken cancellationToken)
    {
        switch (context.Type)
        {
            case MessageTypes.JobStarted:
            {
                var started = ContractJson.Deserialize<JobStarted>(body);
                return session => session.Execute("""
                    UPDATE results SET kind = @kind, parent_job_id = COALESCE(@parent, parent_job_id), results_prefix = @prefix,
                        status = CASE WHEN status IN ('completed', 'failed', 'cancelled') THEN status ELSE 'running' END, updated_at = now()
                    WHERE job_id = @job AND attempt = @attempt
                    """, c => c.With("kind", Sql.Name(started.Kind)).With("parent", started.ParentJobId).WithJson("prefix", started.Results));
            }
            case MessageTypes.JobCompleted:
            {
                var completed = ContractJson.Deserialize<JobCompleted>(body);
                // job.completed finalizes the attempt even when its final PacketRef was missed: index the result file then.
                JsonObject? resultFile = null;
                if (!await IsFinalIndexedAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    resultFile = await ReadResultFileAsync(completed.Result, cancellationToken).ConfigureAwait(false);
                }
                return async session =>
                {
                    if (resultFile != null && !session.Row.FinalIndexed)
                    {
                        await ApplyResultAsync(session, resultFile, final: true, 1, completed.AlgorithmId, completed.Statistics, completed.Result).ConfigureAwait(false);
                    }
                    var statistics = completed.Statistics ?? new Dictionary<string, string>();
                    await session.Execute("""
                        UPDATE results SET status = 'completed', progress = 1, kind = @kind, parent_job_id = COALESCE(@parent, parent_job_id),
                            algorithm_id = COALESCE(algorithm_id, @algorithm), result_ref = COALESCE(result_ref, @result), artifacts = @artifacts,
                            statistics = @statistics || statistics, order_list_hash = COALESCE(order_list_hash, @hash), updated_at = now()
                        WHERE job_id = @job AND attempt = @attempt
                        """, c => c.With("kind", Sql.Name(completed.Kind)).With("parent", completed.ParentJobId).With("algorithm", completed.AlgorithmId)
                            .WithJson("result", completed.Result).WithJson("artifacts", completed.Artifacts).WithJson("statistics", statistics)
                            .With("hash", statistics.GetValueOrDefault(OrderListHashKey))).ConfigureAwait(false);
                };
            }
            case MessageTypes.JobFailed:
            {
                var failed = ContractJson.Deserialize<JobFailed>(body);
                return session => SetStatus(session, JobStatus.Failed, failed.Error);
            }
            case MessageTypes.JobCancelled:
            {
                var cancelled = ContractJson.Deserialize<JobCancelled>(body);
                return session => SetStatus(session, JobStatus.Cancelled, new { reason = cancelled.Reason });
            }
            case MessageTypes.PacketRef:
            {
                var reference = ContractJson.Deserialize<PacketRef>(body);
                var content = LeanJson.ParseObject(await store.GetVerifiedAsync(reference.Object, cancellationToken).ConfigureAwait(false));
                if (reference.Final && reference.Format == PacketRef.FinalFormat)
                {
                    var root = await WithOrderEventsAsync(reference.Object, ResultFileRoot(content), cancellationToken).ConfigureAwait(false);
                    return session => ApplyResultAsync(session, root, final: true, reference.Progress ?? 1, reference.AlgorithmId, reference.Statistics, reference.Object);
                }
                return await PreparePacketAsync(context, reference.PacketType, content, cancellationToken).ConfigureAwait(false);
            }
            case var type when type.StartsWith(MessageTypes.PacketPrefix, StringComparison.Ordinal):
                return await PreparePacketAsync(context, type[MessageTypes.PacketPrefix.Length..], LeanJson.ParseObject(body), cancellationToken).ConfigureAwait(false);
            default:
                logger.LogDebug("Ignoring {Type}", context.Type);
                return null;
        }
    }

    private async Task<Func<Session, Task>?> PreparePacketAsync(IngestContext context, string packetType, JsonObject packet, CancellationToken cancellationToken)
    {
        switch (packetType)
        {
            case "BacktestResult" or "LiveResult":
            {
                var results = packet.GetObject("Results") ?? new JsonObject();
                var progress = packet.GetDouble("Progress");
                var algorithmId = packet.GetString("BacktestId") ?? packet.GetString("DeployId") ?? packet.GetString("AlgorithmId");
                var final = packetType == "BacktestResult" && progress == 1;
                ObjectRef? finalRef = null;
                if (final)
                {
                    // An inline final packet (no PacketRef, e.g. from a producer without the result file): keep the full
                    // BacktestResultPacket JSON in MinIO next to the attempt's other outputs.
                    var prefix = ResultsPrefix.For(context.JobId, context.Attempt).Prefix;
                    finalRef = await store.PutRefAsync(settings.ResultsBucket, $"{prefix}{algorithmId ?? context.JobId.ToString("D")}-final-packet.json",
                        Encoding.UTF8.GetBytes(packet.ToJsonString()), "application/json", cancellationToken).ConfigureAwait(false);
                }
                var live = packetType == "LiveResult" ? LiveStateOf(results) : null;
                if (live == null)
                {
                    return session => ApplyResultAsync(session, results, final, progress, algorithmId, null, finalRef);
                }
                return async session =>
                {
                    await ApplyResultAsync(session, results, final, progress, algorithmId, null, finalRef).ConfigureAwait(false);
                    await StoreLiveStateAsync(session, live).ConfigureAwait(false);
                };
            }
            case "OrderEvent":
            {
                var orderEvent = packet.GetObject("Event");
                return orderEvent == null ? null : session => InsertOrderEventsAsync(session, [orderEvent]);
            }
            case var type when LogPacketTypes.Contains(type):
            {
                var line = new LogEntry(type, packet.GetString("Message") ?? string.Empty, packet.GetString("StackTrace"));
                return session => InsertLogAsync(session, line);
            }
            case "AlgorithmStatus":
            {
                var status = packet.Get("Status")?.DeepClone();
                return session => session.Execute(
                    "UPDATE results SET state = COALESCE(state, '{}'::jsonb) || @state, updated_at = now() WHERE job_id = @job AND attempt = @attempt",
                    c => c.WithJson("state", new JsonObject { ["AlgorithmStatus"] = status }));
            }
            default:
                logger.LogDebug("Ignoring packet type {PacketType}", packetType);
                return null;
        }
    }

    public const string OrderListHashKey = "OrderListHash";

    /// <summary>Key under <c>results.state</c> holding the latest live portfolio (served by <c>GET .../live-state</c>).</summary>
    public const string LiveStateKey = "Live";

    /// <summary>
    /// The invested holdings and cash book of a <c>LiveResult</c> that carries them (<c>LiveTradingResultHandler</c> sends both
    /// together), in the shapes LEAN's <c>live-holdings</c> / <c>live-cash-balance</c> read back; null for other partial packets.
    /// </summary>
    public static JsonObject? LiveStateOf(JsonObject results)
    {
        var holdings = results.GetObject("Holdings");
        var cash = results.GetObject("Cash");
        if (holdings == null || cash == null)
        {
            return null;
        }
        var invested = new JsonArray();
        foreach (var (id, holding) in holdings)
        {
            if (holding is JsonObject h && (AsDecimal(h.Get("q")) ?? AsDecimal(h.Get("Quantity")) ?? 0) != 0)
            {
                // LEAN writes the symbol only as the dictionary key (its SecurityIdentifier); Holding reads it back from "symbol".
                var entry = (JsonObject)h.DeepClone();
                if (entry["symbol"] == null && entry["Symbol"] == null)
                {
                    entry["symbol"] = new JsonObject { ["value"] = id.Split(' ')[0], ["id"] = id };
                }
                invested.Add(entry);
            }
        }
        var amounts = new JsonArray();
        foreach (var (currency, value) in cash)
        {
            if (value is JsonObject c && AsDecimal(c.Get("Amount")) is { } amount)
            {
                amounts.Add(new JsonObject { ["currency"] = c.GetString("Symbol") ?? currency, ["amount"] = amount });
            }
        }
        return new JsonObject { ["holdings"] = invested, ["cash"] = amounts };
    }

    private static decimal? AsDecimal(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<decimal>(out var number) ? number
        : value.TryGetValue<string>(out var text) && decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) ? number : null
        : null;

    /// <summary>Keeps the newest live state by publish sequence, so a redelivered or reordered packet never rolls the portfolio back.</summary>
    private static Task StoreLiveStateAsync(Session session, JsonObject live)
    {
        var sequence = session.Context.Sequence ?? 0;
        live["sequence"] = sequence;
        live["at"] = session.Context.Time.UtcDateTime;
        return session.Execute("""
            UPDATE results SET state = COALESCE(state, '{}'::jsonb) || jsonb_build_object('Live', @live::jsonb), updated_at = now()
            WHERE job_id = @job AND attempt = @attempt AND COALESCE((state -> 'Live' ->> 'sequence')::bigint, -1) <= @sequence
            """, c => c.WithJson("live", live).With("sequence", sequence));
    }

    /// <summary>
    /// Applies a LEAN <c>Result</c> (partial packet delta or final result): statistics, runtime statistics, orders, order
    /// events and charts. Partial packets older than the last applied sequence never overwrite the summary, and the
    /// final result supersedes every partial.
    /// </summary>
    private async Task ApplyResultAsync(Session session, JsonObject results, bool final, double? progress, string? algorithmId,
        Dictionary<string, string>? extraStatistics, ObjectRef? resultRef)
    {
        var sequence = session.Context.Sequence ?? 0;
        if (session.Row.FinalIndexed && !final)
        {
            // A partial redelivered/reordered after the final result: only order events can still be new.
            await InsertOrderEventsAsync(session, results.GetArray("OrderEvents")?.OfType<JsonObject>() ?? []).ConfigureAwait(false);
            return;
        }

        var statistics = results.GetStringMap("Statistics");
        foreach (var (key, value) in extraStatistics ?? [])
        {
            statistics[key] = value;
        }
        var runtimeStatistics = results.GetStringMap("RuntimeStatistics");
        var newer = final || sequence > session.Row.LastSequence;

        if (newer)
        {
            await session.Execute("""
                UPDATE results SET
                    progress = COALESCE(@progress, progress),
                    algorithm_id = COALESCE(@algorithm, algorithm_id),
                    statistics = CASE WHEN @statistics <> '{}'::jsonb THEN @statistics ELSE statistics END,
                    runtime_statistics = CASE WHEN @runtime <> '{}'::jsonb THEN @runtime ELSE runtime_statistics END,
                    state = CASE WHEN @state IS NULL THEN state ELSE COALESCE(state, '{}'::jsonb) || @state END,
                    algorithm_configuration = COALESCE(@configuration, algorithm_configuration),
                    order_list_hash = CASE WHEN @final THEN COALESCE(@hash, order_list_hash) ELSE order_list_hash END,
                    result_ref = COALESCE(@result, result_ref),
                    final_indexed = final_indexed OR @final,
                    last_sequence = GREATEST(last_sequence, @sequence),
                    updated_at = now()
                WHERE job_id = @job AND attempt = @attempt
                """, c => c.With("progress", progress).With("algorithm", algorithmId).WithJson("statistics", statistics)
                    .WithJson("runtime", runtimeStatistics).WithJson("state", results.GetObject("State"))
                    .WithJson("configuration", LeanJson.ToCamelCase(results.GetObject("AlgorithmConfiguration")))
                    .With("final", final).With("hash", statistics.GetValueOrDefault(OrderListHashKey)).WithJson("result", resultRef)
                    .With("sequence", sequence)).ConfigureAwait(false);
        }

        if (results.GetObject("Orders") is { Count: > 0 } orders)
        {
            var ids = new List<int>();
            var bodies = new List<string>();
            foreach (var (key, value) in orders)
            {
                if (value is JsonObject order && (int.TryParse(key, out var id) || order.GetInt("Id") is { } parsedId && (id = parsedId) >= 0))
                {
                    ids.Add(id);
                    bodies.Add(LeanJson.ToCamelCase(order)!.ToJsonString());
                }
            }
            await session.Execute("""
                INSERT INTO orders (job_id, attempt, order_id, sequence, body)
                SELECT @job, @attempt, o.id, @sequence, o.body FROM unnest(@ids, @bodies) AS o(id, body)
                ON CONFLICT (job_id, attempt, order_id) DO UPDATE SET body = excluded.body, sequence = excluded.sequence, updated_at = now()
                WHERE orders.sequence <= excluded.sequence
                """, c =>
            {
                c.With("sequence", final ? FinalOrderSequence : sequence).With("ids", ids.ToArray());
                c.Parameters.Add(new NpgsqlParameter("bodies", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = bodies.ToArray() });
            }).ConfigureAwait(false);
            await session.Execute(
                "UPDATE results SET order_count = (SELECT count(*) FROM orders WHERE job_id = @job AND attempt = @attempt) WHERE job_id = @job AND attempt = @attempt",
                _ => { }).ConfigureAwait(false);
        }

        await InsertOrderEventsAsync(session, results.GetArray("OrderEvents")?.OfType<JsonObject>() ?? []).ConfigureAwait(false);

        if (results.GetObject("Charts") is { Count: > 0 } charts)
        {
            await UpsertChartsAsync(session, charts, final).ConfigureAwait(false);
        }
    }

    private async Task UpsertChartsAsync(Session session, JsonObject charts, bool final)
    {
        foreach (var (name, value) in charts)
        {
            if (value is not JsonObject chart)
            {
                continue;
            }
            var normalized = LeanJson.NormalizeChart(name, chart);
            int fullPoints;
            if (final)
            {
                fullPoints = Downsampler.DownsampleChart(normalized, settings.ChartMaxPoints);
            }
            else
            {
                JsonObject? stored = null;
                var storedFull = 0;
                await using (var command = session.Connection.Command(session.Transaction,
                    "SELECT body, point_count FROM charts WHERE job_id = @job AND attempt = @attempt AND name = @name")
                    .With("job", session.Context.JobId).With("attempt", session.Context.Attempt).With("name", name))
                await using (var reader = await command.ExecuteReaderAsync(session.CancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(session.CancellationToken).ConfigureAwait(false))
                    {
                        stored = JsonNode.Parse(reader.GetString(0)) as JsonObject;
                        storedFull = reader.GetInt32(1);
                    }
                }
                var storedLongest = stored == null ? 0 : LongestSeries(stored);
                normalized = Downsampler.Merge(stored, normalized);
                var mergedLongest = Downsampler.DownsampleChart(normalized, settings.ChartMaxPoints);
                fullPoints = storedFull + Math.Max(mergedLongest - storedLongest, 0);
            }
            await session.Execute("""
                INSERT INTO charts (job_id, attempt, name, body, point_count) VALUES (@job, @attempt, @name, @body, @points)
                ON CONFLICT (job_id, attempt, name) DO UPDATE SET body = excluded.body, point_count = excluded.point_count, updated_at = now()
                """, c => c.With("name", name).WithJson("body", normalized).With("points", fullPoints)).ConfigureAwait(false);
        }
    }

    internal static int LongestSeries(JsonObject chart) =>
        (chart["series"] as JsonObject)?.Select(s => (s.Value as JsonObject)?["values"] is JsonArray values ? values.Count : 0).DefaultIfEmpty(0).Max() ?? 0;

    private static async Task InsertOrderEventsAsync(Session session, IEnumerable<JsonObject> orderEvents)
    {
        var orderIds = new List<int>();
        var eventIds = new List<int>();
        var times = new List<DateTime>();
        var bodies = new List<string>();
        foreach (var orderEvent in orderEvents)
        {
            // OrderEventJsonConverter form: "id" is "<algorithmId>-<orderId>-<orderEventId>", "time" is unix seconds.
            var camel = (JsonObject)LeanJson.ToCamelCase(orderEvent)!;
            if (camel.GetInt("orderId") is not { } orderId || (camel.GetInt("orderEventId") ?? camel.GetInt("id")) is not { } eventId)
            {
                continue;
            }
            orderIds.Add(orderId);
            eventIds.Add(eventId);
            times.Add((LeanJson.AsTime(camel.Get("time")) ?? LeanJson.AsTime(camel.Get("utcTime")) ?? session.Context.Time).UtcDateTime);
            bodies.Add(camel.ToJsonString());
        }
        if (orderIds.Count == 0)
        {
            return;
        }
        await session.Execute("""
            INSERT INTO order_events (job_id, attempt, order_id, event_id, utc_time, body)
            SELECT @job, @attempt, e.order_id, e.event_id, e.utc_time, e.body FROM unnest(@orders, @events, @times, @bodies) AS e(order_id, event_id, utc_time, body)
            ON CONFLICT DO NOTHING
            """, c =>
        {
            c.With("orders", orderIds.ToArray()).With("events", eventIds.ToArray());
            c.Parameters.Add(new NpgsqlParameter("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = times.ToArray() });
            c.Parameters.Add(new NpgsqlParameter("bodies", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = bodies.ToArray() });
        }).ConfigureAwait(false);
    }

    private async Task InsertLogAsync(Session session, LogEntry line)
    {
        await session.Execute("""
            INSERT INTO logs (job_id, attempt, sequence, type, message, stack_trace, time) VALUES (@job, @attempt, @sequence, @type, @message, @stack, @time)
            """, c => c.With("sequence", session.Context.Sequence).With("type", line.Type).With("message", line.Message)
                .With("stack", line.StackTrace).With("time", session.Context.Time.UtcDateTime)).ConfigureAwait(false);
        // The attempt's row is locked by this transaction (IngestAsync), so log_count is exact: trim the oldest lines only
        // past the cap, through the (job_id, attempt, id) index, instead of scanning the newest cap lines on every insert.
        long count;
        await using (var counted = session.Connection.Command(session.Transaction,
            "UPDATE results SET log_count = log_count + 1 WHERE job_id = @job AND attempt = @attempt RETURNING log_count")
            .With("job", session.Context.JobId).With("attempt", session.Context.Attempt))
        {
            count = await counted.ExecuteScalarAsync(session.CancellationToken).ConfigureAwait(false) is long n ? n : 0;
        }
        if (count > settings.MaxLogsPerAttempt)
        {
            await session.Execute("""
                WITH trimmed AS (
                    DELETE FROM logs WHERE id IN (
                        SELECT id FROM logs WHERE job_id = @job AND attempt = @attempt ORDER BY id LIMIT @excess)
                    RETURNING 1)
                UPDATE results SET log_count = log_count - (SELECT count(*) FROM trimmed) WHERE job_id = @job AND attempt = @attempt
                """, c => c.With("excess", count - settings.MaxLogsPerAttempt)).ConfigureAwait(false);
        }
    }

    private static Task SetStatus(Session session, JobStatus status, object error) => session.Execute("""
        UPDATE results SET status = @status, error = @error, updated_at = now()
        WHERE job_id = @job AND attempt = @attempt AND status <> 'completed'
        """, c => c.With("status", Sql.Name(status)).WithJson("error", error));

    /// <summary>The final result file with its order events (see <see cref="WithOrderEventsAsync"/>).</summary>
    private async Task<JsonObject> ReadResultFileAsync(ObjectRef reference, CancellationToken cancellationToken) =>
        await WithOrderEventsAsync(reference, ResultFileRoot(LeanJson.ParseObject(await store.GetVerifiedAsync(reference, cancellationToken).ConfigureAwait(false))),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// LEAN writes the final result file without order events and stores them in <c>&lt;algorithmId&gt;-order-events.json</c>
    /// next to it (<c>BacktestingResultHandler.StoreResult</c>); the bus handler uploads that file before the final PacketRef.
    /// </summary>
    private async Task<JsonObject> WithOrderEventsAsync(ObjectRef resultRef, JsonObject results, CancellationToken cancellationToken)
    {
        if (results.GetArray("OrderEvents") is { Count: > 0 } || !resultRef.Key.EndsWith(".json", StringComparison.Ordinal))
        {
            return results;
        }
        var key = resultRef.Key[..^".json".Length] + "-order-events.json";
        if (await store.TryGetAsync(resultRef.Bucket, key, cancellationToken).ConfigureAwait(false) is { } content
            && JsonNode.Parse(content) is JsonArray orderEvents)
        {
            results["orderEvents"] = orderEvents;
        }
        return results;
    }

    private async Task<bool> IsFinalIndexedAsync(IngestContext context, CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand("SELECT final_indexed FROM results WHERE job_id = @job AND attempt = @attempt")
            .With("job", context.JobId).With("attempt", context.Attempt);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>The <c>Result</c> object of a result file (camelCase <c>Result</c>) or of a full packet (<c>Results</c>).</summary>
    internal static JsonObject ResultFileRoot(JsonObject content) =>
        content.GetObject("Results") is { } results && content.Get("Type") != null ? results : content;

    private sealed record IngestContext(Guid JobId, int Attempt, JobKind Kind, Guid? ParentJobId, long? Sequence, string DedupKey, string Type, DateTimeOffset Time);

    private sealed record ResultRow(long LastSequence, bool FinalIndexed);

    private sealed record LogEntry(string Type, string Message, string? StackTrace);

    private sealed record Session(NpgsqlConnection Connection, NpgsqlTransaction Transaction, IngestContext Context, ResultRow Row, CancellationToken CancellationToken)
    {
        public async Task Execute(string sql, Action<NpgsqlCommand> parameters)
        {
            await using var command = Connection.Command(Transaction, sql).With("job", Context.JobId).With("attempt", Context.Attempt);
            parameters(command);
            await command.ExecuteNonQueryAsync(CancellationToken).ConfigureAwait(false);
        }
    }
}
