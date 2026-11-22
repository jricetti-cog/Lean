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
                    resultFile = ResultFileRoot(LeanJson.ParseObject(await store.GetVerifiedAsync(completed.Result, cancellationToken).ConfigureAwait(false)));
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
                    var root = ResultFileRoot(content);
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
                return session => ApplyResultAsync(session, results, final, progress, algorithmId, null, finalRef);
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
            var camel = (JsonObject)LeanJson.ToCamelCase(orderEvent)!;
            if (camel.GetInt("orderId") is not { } orderId || camel.GetInt("id") is not { } eventId)
            {
                continue;
            }
            orderIds.Add(orderId);
            eventIds.Add(eventId);
            times.Add((LeanJson.AsTime(camel.Get("utcTime")) ?? session.Context.Time).UtcDateTime);
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
        await session.Execute("""
            DELETE FROM logs WHERE job_id = @job AND attempt = @attempt AND id <= (
                SELECT id FROM logs WHERE job_id = @job AND attempt = @attempt ORDER BY id DESC OFFSET @cap LIMIT 1)
            """, c => c.With("cap", settings.MaxLogsPerAttempt)).ConfigureAwait(false);
    }

    private static Task SetStatus(Session session, JobStatus status, object error) => session.Execute("""
        UPDATE results SET status = @status, error = @error, updated_at = now()
        WHERE job_id = @job AND attempt = @attempt AND status <> 'completed'
        """, c => c.With("status", Sql.Name(status)).WithJson("error", error));

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
