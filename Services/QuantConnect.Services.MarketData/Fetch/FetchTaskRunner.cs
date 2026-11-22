using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.EngineHandlers;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;
using BusQueues = QuantConnect.Services.Messaging.Queues;

namespace QuantConnect.Services.MarketData.Fetch;

/// <summary>
/// Consumes <c>marketdata.fetch</c> (prefetch 1: one task at a time per process, ADR 0004) and reports the task with
/// <c>job.marketdata.&lt;id&gt;.status.v1</c> events. The completed event's result is a manifest of the published files in
/// <c>lean-results/&lt;id&gt;/&lt;attempt&gt;/manifest.json</c>.
/// </summary>
public sealed class FetchTaskRunner : BackgroundService
{
    public const int MaxAttempts = 3;
    private readonly IMessageBus _bus;
    private readonly IFetchTaskStore _store;
    private readonly FetchExecutor _executor;
    private readonly IArtifactStore _artifacts;
    private readonly ILogger<FetchTaskRunner> _logger;
    private readonly string _workerId = $"market-data-{Environment.MachineName}";

    public FetchTaskRunner(IMessageBus bus, IFetchTaskStore store, FetchExecutor executor, IArtifactStore artifacts, ILogger<FetchTaskRunner> logger)
    {
        _bus = bus;
        _store = store;
        _executor = executor;
        _artifacts = artifacts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await _bus.ConsumeAsync(BusQueues.MarketDataFetch, HandleAsync, new ConsumerOptions { Prefetch = 1 }, stoppingToken)
                    .ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not consume {Queue}; retrying", BusQueues.MarketDataFetch);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        if (message.Envelope.Type != MessageTypes.MarketDataFetchRequested)
        {
            _logger.LogWarning("Unexpected {Type} on {Queue}", message.Envelope.Type, message.Queue);
            return MessageDisposition.DeadLetter;
        }
        var request = message.GetBody<MarketDataFetchRequested>();
        var attempt = (int)message.DeliveryCount + 1;
        var taskId = request.JobId;

        if (!await _store.TryStartAsync(taskId, attempt, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Fetch task {TaskId} is unknown or already finished; dropping the redelivery", taskId);
            return MessageDisposition.Ack;
        }
        await PublishAsync(MessageTypes.JobStarted, taskId, attempt, new JobStarted
        {
            JobId = taskId, Kind = JobKind.MarketData, Attempt = attempt, StartedAt = DateTimeOffset.UtcNow, WorkerId = _workerId,
            Results = ResultsPrefix.For(taskId, attempt),
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var files = await _executor.ExecuteAsync(taskId, request.Fetch, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { taskId, attempt, fetch = request.Fetch, files }, ContractJson.Options);
            ObjectRef result;
            try
            {
                result = await _artifacts.PutObjectAsync(Buckets.Results, $"{ResultsPrefix.For(taskId, attempt).Prefix}manifest.json", manifest,
                    "application/json", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new FetchFailedException(ErrorCode.UploadFailed, $"Could not upload the task manifest: {e.Message}", retryable: true, e);
            }
            await _store.CompleteAsync(taskId, files, cancellationToken).ConfigureAwait(false);
            await PublishAsync(MessageTypes.JobCompleted, taskId, attempt, new JobCompleted
            {
                JobId = taskId, Kind = JobKind.MarketData, Attempt = attempt, CompletedAt = DateTimeOffset.UtcNow, Result = result, Artifacts = new(),
                Statistics = new() { ["Files written"] = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Fetch task {TaskId} attempt {Attempt} published {Files} files", taskId, attempt, files.Count);
            return MessageDisposition.Ack;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _store.FailAsync(taskId, new ErrorInfo { Code = ErrorCode.Internal, Message = "market-data stopped during the task" }, false, CancellationToken.None)
                .ConfigureAwait(false);
            return MessageDisposition.Retry;
        }
        catch (Exception e)
        {
            var failure = e as FetchFailedException ?? new FetchFailedException(ErrorCode.Internal, e.Message, retryable: true, e);
            var final = !failure.Retryable || attempt >= MaxAttempts;
            var error = new ErrorInfo
            {
                Code = final && failure.Retryable ? ErrorCode.MaxAttemptsExceeded : failure.Code,
                Message = failure.Message,
                StackTrace = (failure.InnerException ?? failure).StackTrace,
            };
            _logger.LogWarning(e, "Fetch task {TaskId} attempt {Attempt} failed (final: {Final})", taskId, attempt, final);
            await _store.FailAsync(taskId, error, final, cancellationToken).ConfigureAwait(false);
            await PublishAsync(MessageTypes.JobFailed, taskId, attempt, new JobFailed
            {
                JobId = taskId, Kind = JobKind.MarketData, Attempt = attempt, FailedAt = DateTimeOffset.UtcNow, Error = error, Final = final,
            }, cancellationToken).ConfigureAwait(false);
            return final ? MessageDisposition.Ack : MessageDisposition.Retry;
        }
    }

    private Task PublishAsync<T>(string type, Guid taskId, int attempt, T body, CancellationToken cancellationToken) =>
        _bus.PublishAsync(OutgoingMessage.Create(BusExchanges.Events, RoutingKeys.EventFor(type, JobKind.MarketData, taskId),
            MessageEnvelope.Create(type, taskId, attempt, AppIds.MarketData, JobKind.MarketData), body), cancellationToken);
}

/// <summary>Publishes committed outbox rows (at-least-once; the message id is the outbox id so consumers can de-duplicate).</summary>
public sealed class OutboxRelay : BackgroundService
{
    private readonly IFetchTaskStore _store;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(IFetchTaskStore store, IMessagePublisher publisher, ILogger<OutboxRelay> logger)
    {
        _store = store;
        _publisher = publisher;
        _logger = logger;
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Outbox relay failed; retrying");
            }
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<int> RelayOnceAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var message in await _store.PendingOutboxAsync(100, cancellationToken).ConfigureAwait(false))
        {
            var envelope = MessageEnvelope.Create(message.Type, message.JobId, message.Attempt, AppIds.MarketData, JobKind.MarketData) with { MessageId = message.Id };
            await _publisher.PublishAsync(OutgoingMessage.CreateRaw(message.Exchange, message.RoutingKey, envelope, message.Body), cancellationToken).ConfigureAwait(false);
            await _store.MarkSentAsync(message.Id, cancellationToken).ConfigureAwait(false);
            sent++;
        }
        return sent;
    }
}
