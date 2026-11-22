using System.Text;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Results;

namespace QuantConnect.Services.Optimizer;

/// <summary>
/// Handles <c>optimizer.children</c> (every <c>job.backtest.*.status.v1</c>): events of children of an optimization this replica
/// runs (by the <c>parent-job-id</c> header) are folded into its <see cref="BusLeanOptimizer"/>; the rest are acked and ignored.
/// </summary>
public sealed class ChildEventsHandler(OptimizationCatalog catalog, IResultObjectStore store, ILogger<ChildEventsHandler> logger)
{
    /// <summary>Reason job-orchestrator gives children it cancels because their optimization was cancelled.</summary>
    public const string ParentCancelledReason = "parent optimization cancelled";

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        var headers = message.Envelope.Headers;
        if (headers.ParentJobId is not { } parentId || !catalog.TryGetRunning(parentId, out var optimizer))
        {
            return MessageDisposition.Ack;
        }
        var childId = headers.JobId;
        try
        {
            switch (message.Envelope.Type)
            {
                case MessageTypes.JobStarted:
                    optimizer.ChildStarted(childId, message.GetBody<JobStarted>().WorkerId);
                    break;
                case MessageTypes.JobCompleted:
                    var completed = message.GetBody<JobCompleted>();
                    if (!optimizer.IsWaitingOn(childId))
                    {
                        break;
                    }
                    byte[] result;
                    try
                    {
                        result = await store.GetVerifiedAsync(completed.Result, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is InvalidDataException or FileNotFoundException)
                    {
                        optimizer.ChildEnded(childId, JobStatus.Failed, $"result s3://{completed.Result.Bucket}/{completed.Result.Key} unusable: {e.Message}");
                        break;
                    }
                    if (optimizer.ChildCompleted(childId, Encoding.UTF8.GetString(result), completed.Statistics))
                    {
                        logger.LogInformation("Optimization {JobId}: child {ChildId} completed", parentId, childId);
                    }
                    break;
                case MessageTypes.JobFailed:
                    var failed = message.GetBody<JobFailed>();
                    // A worker's non-final failure is retried by job-orchestrator; its final verdict comes as a final job.failed.
                    if (failed.Final || message.Envelope.AppId == AppIds.Orchestrator)
                    {
                        optimizer.ChildEnded(childId, JobStatus.Failed, $"{ContractJson.Serialize(failed.Error.Code).Trim('"')}: {failed.Error.Message}");
                    }
                    break;
                case MessageTypes.JobCancelled:
                    var cancelled = message.GetBody<JobCancelled>();
                    if (cancelled.Reason == ParentCancelledReason)
                    {
                        logger.LogInformation("Optimization {JobId} cancelled through job-orchestrator", parentId);
                        optimizer.Cancel(cancelled.Reason);
                    }
                    else
                    {
                        optimizer.ChildEnded(childId, JobStatus.Cancelled, cancelled.Reason);
                    }
                    break;
            }
            return MessageDisposition.Ack;
        }
        catch (System.Text.Json.JsonException e)
        {
            logger.LogError(e, "Malformed {Type} {MessageId}; dead-lettering", message.Envelope.Type, message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }
    }
}
