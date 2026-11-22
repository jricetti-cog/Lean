using System.Globalization;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Results;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;

namespace QuantConnect.Services.Optimizer;

/// <summary>
/// Runs one <c>optimization.requested</c> delivery: claims the attempt from job-orchestrator, publishes <c>job.started</c>, runs a
/// <see cref="BusLeanOptimizer"/> until LEAN ends it (heartbeats and reconciliation meanwhile), uploads
/// <c>optimization-result.json</c> and publishes the terminal event. The delivery is acked only after that event is confirmed.
/// </summary>
public sealed class OptimizationRunner(
    IMessagePublisher bus,
    IOrchestratorJobs jobs,
    IResultObjectStore store,
    OptimizationCatalog catalog,
    OptimizerSettings settings,
    ILoggerFactory loggers)
{
    public const string ResultFile = "optimization-result.json";
    private readonly ILogger _logger = loggers.CreateLogger<OptimizationRunner>();

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        if (message.Envelope.Type != MessageTypes.OptimizationRequested)
        {
            _logger.LogWarning("Unexpected {Type} on {Queue}", message.Envelope.Type, message.Queue);
            return MessageDisposition.DeadLetter;
        }
        OptimizationRequested request;
        try
        {
            request = message.GetBody<OptimizationRequested>();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Malformed optimization.requested {MessageId}", message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }

        AttemptGrant grant;
        try
        {
            grant = await jobs.StartAttemptAsync(request.JobId, settings.WorkerId, message.DeliveryCount, request.Attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(e, "Could not claim optimization {JobId}; requeueing", request.JobId);
            return MessageDisposition.Retry;
        }
        catch (OperationCanceledException)
        {
            return MessageDisposition.Retry;
        }
        if (grant.Outcome == AttemptOutcome.Gone)
        {
            _logger.LogInformation("Optimization {JobId} dispatch dropped: {Detail}", request.JobId, grant.Detail);
            return MessageDisposition.Ack;
        }
        return await RunAsync(request, grant.Attempt!.Attempt, grant.Attempt.Results, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MessageDisposition> RunAsync(OptimizationRequested request, int attempt, ResultsPrefix results, CancellationToken cancellationToken)
    {
        var jobId = request.JobId;
        await PublishAsync(MessageTypes.JobStarted, jobId, attempt, new JobStarted
        {
            JobId = jobId, Kind = JobKind.Optimization, Attempt = attempt, StartedAt = DateTimeOffset.UtcNow, WorkerId = settings.WorkerId, Results = results,
        }, cancellationToken).ConfigureAwait(false);

        BusLeanOptimizer optimizer;
        try
        {
            var packet = OptimizationPacketFactory.Create(jobId, request.Optimization, settings, request.SubmittedAt);
            var context = new OptimizationContext(jobId, attempt, request.Optimization.Name, request.Optimization.Backtest);
            optimizer = new BusLeanOptimizer(packet, context, jobs, bus, loggers.CreateLogger<BusLeanOptimizer>(), cancellationToken);
        }
        catch (Exception e) when (e is InvalidOptimizationException or ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(e, "Optimization {JobId} is invalid", jobId);
            return await FailAsync(jobId, attempt, new ErrorInfo { Code = ErrorCode.InvalidRequest, Message = e.Message }, true, null, cancellationToken)
                .ConfigureAwait(false);
        }

        catalog.Started(optimizer);
        using var loops = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var background = Task.WhenAll(HeartbeatLoopAsync(jobId, attempt, loops.Token), ReconcileLoopAsync(optimizer, loops.Token));
        try
        {
            try
            {
                await Task.Run(optimizer.Start, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException e)
            {
                // LeanOptimizer.Start: not a single parameter set could be submitted.
                var final = attempt >= settings.MaxAttempts;
                return await FailAsync(jobId, attempt, new ErrorInfo { Code = final ? ErrorCode.MaxAttemptsExceeded : ErrorCode.Internal, Message = e.Message },
                    final, optimizer, cancellationToken).ConfigureAwait(false);
            }
            _logger.LogInformation("Optimization {JobId} attempt {Attempt} running: {Total} backtests, at most {Concurrent} at once",
                jobId, attempt, optimizer.GetCurrentEstimate(), optimizer.Progress().Optimization!.Running);

            await optimizer.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            await loops.CancelAsync().ConfigureAwait(false);
            await background.ConfigureAwait(false);
            return await FinishAsync(optimizer, results, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Optimization {JobId} attempt {Attempt} interrupted by shutdown; requeueing", jobId, attempt);
            catalog.Released(jobId);
            return MessageDisposition.Retry;
        }
        finally
        {
            await loops.CancelAsync().ConfigureAwait(false);
            optimizer.Dispose();
        }
    }

    private async Task<MessageDisposition> FinishAsync(BusLeanOptimizer optimizer, ResultsPrefix results, CancellationToken cancellationToken)
    {
        var jobId = optimizer.JobId;
        var attempt = optimizer.Attempt;
        var status = optimizer.CancelReason != null ? JobStatus.Cancelled : optimizer.CompletedCount > 0 ? JobStatus.Completed : JobStatus.Failed;
        ObjectRef document;
        try
        {
            document = await store.PutRefAsync(results.Bucket, $"{results.Prefix}{ResultFile}", optimizer.ResultDocument(status), "application/json", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var final = attempt >= settings.MaxAttempts;
            return await FailAsync(jobId, attempt, new ErrorInfo { Code = ErrorCode.UploadFailed, Message = $"Could not upload {ResultFile}: {e.Message}" },
                final, optimizer, cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        switch (status)
        {
            case JobStatus.Cancelled:
                await PublishAsync(MessageTypes.JobCancelled, jobId, attempt, new JobCancelled
                {
                    JobId = jobId, Kind = JobKind.Optimization, Attempt = attempt, CancelledAt = now, Reason = optimizer.CancelReason!, Artifacts = [document],
                }, cancellationToken).ConfigureAwait(false);
                break;
            case JobStatus.Failed:
                return await FailAsync(jobId, attempt, new ErrorInfo
                {
                    Code = ErrorCode.RuntimeError, Message = $"none of the {optimizer.FailedCount} child backtests completed",
                }, true, optimizer, cancellationToken, [document]).ConfigureAwait(false);
            default:
                var progress = optimizer.Progress();
                // Final counters for the gateway's progress stream, then the terminal event.
                await PublishAsync(MessageTypes.JobProgress, jobId, attempt, progress, cancellationToken).ConfigureAwait(false);
                await PublishAsync(MessageTypes.JobCompleted, jobId, attempt, new JobCompleted
                {
                    JobId = jobId, Kind = JobKind.Optimization, Attempt = attempt, CompletedAt = now, Result = document, Artifacts = [],
                    Statistics = Summary(optimizer), AlgorithmId = jobId.ToString("D"),
                }, cancellationToken).ConfigureAwait(false);
                break;
        }
        catalog.Finished(jobId, optimizer.State(status, document), optimizer.Children.Select(c => c.ToContract()));
        var best = optimizer.BestChild();
        _logger.LogInformation("Optimization {JobId} attempt {Attempt} {Status}: best {Best} ({Parameters}) {Target} = {Objective}; result s3://{Bucket}/{Key}",
            jobId, attempt, status, best?.JobId, best?.ParameterSet, optimizer.Criterion.Target, best?.Objective, document.Bucket, document.Key);
        return MessageDisposition.Ack;
    }

    /// <summary>Headline numbers for <c>job.completed.statistics</c>: counters, the best child and its parameter set.</summary>
    public static Dictionary<string, string> Summary(BusLeanOptimizer optimizer)
    {
        var progress = optimizer.Progress().Optimization!;
        var summary = new Dictionary<string, string>
        {
            ["Total Backtests"] = progress.Total.ToString(CultureInfo.InvariantCulture),
            ["Completed Backtests"] = progress.Completed.ToString(CultureInfo.InvariantCulture),
            ["Failed Backtests"] = progress.Failed.ToString(CultureInfo.InvariantCulture),
            ["Target"] = optimizer.Criterion.Target,
        };
        if (optimizer.BestChild() is { } best)
        {
            summary["Best Backtest"] = best.JobId.ToString("D");
            summary["Best Objective"] = best.Objective?.ToString(CultureInfo.InvariantCulture) ?? "";
            foreach (var (name, value) in best.ParameterSet.Value)
            {
                summary[$"Best Parameter {name}"] = value;
            }
        }
        return summary;
    }

    private async Task<MessageDisposition> FailAsync(Guid jobId, int attempt, ErrorInfo error, bool final, BusLeanOptimizer? optimizer,
        CancellationToken cancellationToken, List<ObjectRef>? artifacts = null)
    {
        _logger.LogWarning("Optimization {JobId} attempt {Attempt} failed (final: {Final}): {Code} {Message}", jobId, attempt, final, error.Code, error.Message);
        await PublishAsync(MessageTypes.JobFailed, jobId, attempt, new JobFailed
        {
            JobId = jobId, Kind = JobKind.Optimization, Attempt = attempt, FailedAt = DateTimeOffset.UtcNow, Error = error, Final = final, Artifacts = artifacts,
        }, cancellationToken).ConfigureAwait(false);
        if (optimizer != null)
        {
            catalog.Finished(jobId, optimizer.State(JobStatus.Failed, artifacts?.FirstOrDefault()), optimizer.Children.Select(c => c.ToContract()));
        }
        return MessageDisposition.Ack;
    }

    private async Task HeartbeatLoopAsync(Guid jobId, int attempt, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(settings.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await PublishAsync(MessageTypes.JobHeartbeat, jobId, attempt, new JobHeartbeat
                    {
                        JobId = jobId, Kind = JobKind.Optimization, Attempt = attempt, At = DateTimeOffset.UtcNow, WorkerId = settings.WorkerId,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning(e, "Heartbeat for optimization {JobId} not published", jobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Catches child outcomes that publish no final event the optimizer acts on (e.g. job-orchestrator failing a child after its
    /// last worker attempt failed non-finally): children LEAN still waits on that the orchestrator lists as failed/cancelled end.
    /// </summary>
    private async Task ReconcileLoopAsync(BusLeanOptimizer optimizer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(settings.ReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ReconcileOnceAsync(optimizer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<int> ReconcileOnceAsync(BusLeanOptimizer optimizer, CancellationToken cancellationToken)
    {
        if (optimizer.RunningChildIds.Count == 0)
        {
            return 0;
        }
        var ended = 0;
        foreach (var status in new[] { JobStatus.Failed, JobStatus.Cancelled })
        {
            List<Contracts.Http.Job> children;
            try
            {
                children = await jobs.ListChildrenAsync(optimizer.JobId, status, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogDebug(e, "Reconciling optimization {JobId} failed", optimizer.JobId);
                return ended;
            }
            foreach (var child in children)
            {
                if (optimizer.ChildEnded(child.Id, status, child.Error?.Message ?? $"{status} in job-orchestrator"))
                {
                    ended++;
                }
            }
        }
        return ended;
    }

    private Task PublishAsync<T>(string type, Guid jobId, int attempt, T body, CancellationToken cancellationToken) =>
        bus.PublishAsync(OutgoingMessage.Create(BusExchanges.Events, RoutingKeys.EventFor(type, JobKind.Optimization, jobId),
            MessageEnvelope.Create(type, jobId, attempt, AppIds.Optimizer, JobKind.Optimization), body), cancellationToken);
}
