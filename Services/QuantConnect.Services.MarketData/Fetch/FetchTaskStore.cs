using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;
using BusQueues = QuantConnect.Services.Messaging.Queues;

namespace QuantConnect.Services.MarketData.Fetch;

/// <summary>A message to publish after the row that produced it committed (transactional outbox).</summary>
public sealed record OutboxMessage(Guid Id, string Exchange, string RoutingKey, string Type, Guid JobId, int Attempt, byte[] Body);

/// <summary>Fetch tasks, their outbox and the synthetic-ticker registry (the <c>marketdata</c> database).</summary>
public interface IFetchTaskStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Stores a queued task plus its <c>marketdata.fetch.requested</c> outbox row, or returns the identical pending task / the task already created for the idempotency key.</summary>
    Task<(FetchTask Task, bool Created)> CreateAsync(MarketDataFetchSpec spec, Guid? requestingJobId, string submittedBy, string? idempotencyKey, CancellationToken cancellationToken);

    Task<FetchTask?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(List<FetchTask> Items, string? Next)> ListAsync(JobStatus? status, string? cursor, int limit, CancellationToken cancellationToken);

    /// <summary>Marks a queued/running task running at <paramref name="attempt"/>; false when it already finished.</summary>
    Task<bool> TryStartAsync(Guid id, int attempt, CancellationToken cancellationToken);

    Task CompleteAsync(Guid id, IReadOnlyList<string> files, CancellationToken cancellationToken);

    /// <summary>Records a failed attempt: <c>failed</c> when <paramref name="final"/>, otherwise back to <c>queued</c> for the redelivery.</summary>
    Task FailAsync(Guid id, ErrorInfo error, bool final, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessage>> PendingOutboxAsync(int max, CancellationToken cancellationToken);

    Task MarkSentAsync(Guid outboxId, CancellationToken cancellationToken);

    Task MarkSyntheticAsync(string securityType, string market, string ticker, CancellationToken cancellationToken);

    Task<bool> IsSyntheticAsync(string securityType, string market, string ticker, CancellationToken cancellationToken);
}

public static class FetchTasks
{
    public const string SubmittedByHeader = "X-Submitted-By";

    /// <summary>Normalizes the spec (upper-case, sorted, distinct tickers) so identical requests coalesce.</summary>
    public static MarketDataFetchSpec Normalize(MarketDataFetchSpec spec) => spec with
    {
        Market = spec.Market.ToLowerInvariant(),
        Tickers = spec.Tickers?.Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList(),
        Paths = spec.Paths?.Select(p => p.Trim().Replace('\\', '/').TrimStart('/')).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList(),
    };

    public static string DedupeKey(MarketDataFetchSpec normalized) => ContractJson.Serialize(normalized);

    public static string StatusText(JobStatus status) => ContractJson.Serialize(status).Trim('"');

    public static JobStatus ParseStatus(string status) => ContractJson.Deserialize<JobStatus>($"\"{status}\"");

    public static OutboxMessage Requested(Guid taskId, MarketDataFetchSpec spec, Guid? requestingJobId, string submittedBy, DateTimeOffset now)
    {
        var body = new MarketDataFetchRequested
        {
            JobId = taskId, Attempt = 1, SubmittedAt = now, SubmittedBy = submittedBy, RequestingJobId = requestingJobId, Fetch = spec,
        };
        return new OutboxMessage(Guid.NewGuid(), BusExchanges.Jobs, RoutingKeys.Dispatch(JobKind.MarketData), MessageTypes.MarketDataFetchRequested,
            taskId, 1, ContractJson.SerializeToUtf8Bytes(body));
    }
}

/// <summary>Process-local store for tests and <c>MarketData__Store=memory</c>.</summary>
public sealed class InMemoryFetchTaskStore : IFetchTaskStore
{
    private sealed class Row
    {
        public required FetchTask Task;
        public required string DedupeKey;
        public string? IdempotencyKey;
    }

    private readonly object _lock = new();
    private readonly List<Row> _rows = new();
    private readonly List<(OutboxMessage Message, bool Sent)> _outbox = new();
    private readonly HashSet<string> _synthetic = new(StringComparer.Ordinal);

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<(FetchTask Task, bool Created)> CreateAsync(MarketDataFetchSpec spec, Guid? requestingJobId, string submittedBy, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var normalized = FetchTasks.Normalize(spec);
        var key = FetchTasks.DedupeKey(normalized);
        lock (_lock)
        {
            var existing = (idempotencyKey != null ? _rows.FirstOrDefault(r => r.IdempotencyKey == idempotencyKey) : null)
                ?? _rows.FirstOrDefault(r => r.DedupeKey == key && r.Task.Status is JobStatus.Queued or JobStatus.Running);
            if (existing != null) return Task.FromResult((existing.Task, false));

            var now = DateTimeOffset.UtcNow;
            var task = new FetchTask { Id = Guid.NewGuid(), Status = JobStatus.Queued, Attempt = 1, Fetch = normalized, RequestingJobId = requestingJobId, CreatedAt = now };
            _rows.Add(new Row { Task = task, DedupeKey = key, IdempotencyKey = idempotencyKey });
            _outbox.Add((FetchTasks.Requested(task.Id, normalized, requestingJobId, submittedBy, now), false));
            return Task.FromResult((task, true));
        }
    }

    public Task<FetchTask?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_rows.FirstOrDefault(r => r.Task.Id == id)?.Task);
    }

    public Task<(List<FetchTask> Items, string? Next)> ListAsync(JobStatus? status, string? cursor, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var ordered = _rows.Select(r => r.Task).Where(t => status == null || t.Status == status)
                .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).ToList();
            var start = 0;
            if (Guid.TryParse(cursor, out var after))
            {
                start = ordered.FindIndex(t => t.Id == after) + 1;
            }
            var page = ordered.Skip(start).Take(limit).ToList();
            var next = start + page.Count < ordered.Count && page.Count > 0 ? page[^1].Id.ToString("D") : null;
            return Task.FromResult((page, next));
        }
    }

    public Task<bool> TryStartAsync(Guid id, int attempt, CancellationToken cancellationToken) =>
        Update(id, t => t.Status is JobStatus.Queued or JobStatus.Running ? t with { Status = JobStatus.Running, Attempt = attempt, Error = null } : null);

    public Task CompleteAsync(Guid id, IReadOnlyList<string> files, CancellationToken cancellationToken) =>
        Update(id, t => t with { Status = JobStatus.Completed, FilesWritten = files.Count, Error = null, FinishedAt = DateTimeOffset.UtcNow });

    public Task FailAsync(Guid id, ErrorInfo error, bool final, CancellationToken cancellationToken) =>
        Update(id, t => t with { Status = final ? JobStatus.Failed : JobStatus.Queued, Error = error, FinishedAt = final ? DateTimeOffset.UtcNow : null });

    private Task<bool> Update(Guid id, Func<FetchTask, FetchTask?> change)
    {
        lock (_lock)
        {
            var row = _rows.FirstOrDefault(r => r.Task.Id == id);
            var updated = row == null ? null : change(row.Task);
            if (updated == null) return Task.FromResult(false);
            row!.Task = updated;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<OutboxMessage>> PendingOutboxAsync(int max, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<OutboxMessage>>(_outbox.Where(o => !o.Sent).Take(max).Select(o => o.Message).ToList());
    }

    public Task MarkSentAsync(Guid outboxId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var i = _outbox.FindIndex(o => o.Message.Id == outboxId);
            if (i >= 0) _outbox[i] = (_outbox[i].Message, true);
        }
        return Task.CompletedTask;
    }

    public Task MarkSyntheticAsync(string securityType, string market, string ticker, CancellationToken cancellationToken)
    {
        lock (_lock) _synthetic.Add($"{securityType}/{market}/{ticker}".ToLowerInvariant());
        return Task.CompletedTask;
    }

    public Task<bool> IsSyntheticAsync(string securityType, string market, string ticker, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_synthetic.Contains($"{securityType}/{market}/{ticker}".ToLowerInvariant()));
    }
}
