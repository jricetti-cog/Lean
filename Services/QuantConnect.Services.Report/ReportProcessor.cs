using System.Text;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Results;
using ReportRecord = QuantConnect.Services.Contracts.Http.Report;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;

namespace QuantConnect.Services.Report;

/// <summary>
/// Handles one <c>jobs.v1.ReportRequested</c>: downloads the verified backtest (and optional live) result JSON from
/// <c>lean-results</c>, renders it with <see cref="IReportRenderer"/>, uploads <c>report.html</c> (+ <c>report.pdf</c>,
/// <c>report.log</c>) to <c>lean-reports/&lt;jobId&gt;/&lt;attempt&gt;/</c> and publishes <c>job.completed</c>
/// (<c>result</c> = HTML, <c>artifacts</c> = PDF + log) or <c>job.failed</c>. The dispatch is acked only after the
/// terminal event is confirmed (ADR 0002).
/// </summary>
public sealed class ReportProcessor(
    IMessagePublisher bus,
    IResultObjectStore store,
    IReportRenderer renderer,
    ReportCatalog catalog,
    ReportSettings settings,
    ILogger<ReportProcessor> logger,
    ResultsApiClient? results = null)
{
    private readonly string _workerId = $"report-{Environment.MachineName}";

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        if (message.Envelope.Type != MessageTypes.ReportRequested)
        {
            logger.LogWarning("Unexpected {Type} on {Queue}", message.Envelope.Type, message.Queue);
            return MessageDisposition.DeadLetter;
        }
        ReportRequested request;
        try
        {
            request = message.GetBody<ReportRequested>();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Malformed report.requested {MessageId}", message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }

        var jobId = request.JobId;
        var attempt = request.Attempt;
        var prefix = ReportCatalog.AttemptPrefix(jobId, attempt);
        var startedAt = DateTimeOffset.UtcNow;
        await catalog.SaveAsync(new ReportRecord { JobId = jobId, Attempt = attempt, Status = JobStatus.Running, Spec = request.Report }, cancellationToken)
            .ConfigureAwait(false);
        await PublishAsync(MessageTypes.JobStarted, jobId, attempt, new JobStarted
        {
            JobId = jobId, Kind = JobKind.Report, Attempt = attempt, StartedAt = startedAt, WorkerId = _workerId,
            Results = new ResultsPrefix { Bucket = settings.ReportsBucket, Prefix = prefix },
        }, cancellationToken).ConfigureAwait(false);

        var workDirectory = Path.Combine(Path.GetFullPath(settings.WorkDirectory), jobId.ToString("D"), attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
            Directory.CreateDirectory(workDirectory);

            var backtestFile = Path.Combine(workDirectory, "backtest.json");
            await File.WriteAllBytesAsync(backtestFile, await DownloadAsync(request.BacktestResult, "backtest", cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
            string? liveFile = null;
            if (await ResolveLiveAsync(request, cancellationToken).ConfigureAwait(false) is { } liveRef)
            {
                liveFile = Path.Combine(workDirectory, "live.json");
                await File.WriteAllBytesAsync(liveFile, await DownloadAsync(liveRef, "live", cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }

            var formats = request.Report.Formats is { Count: > 0 } f ? f : [ReportFormat.Html];
            var output = await renderer.RenderAsync(new ReportRenderRequest
            {
                JobId = jobId, Attempt = attempt, Spec = request.Report, WorkDirectory = workDirectory,
                BacktestResultFile = backtestFile, LiveResultFile = liveFile, Pdf = formats.Contains(ReportFormat.Pdf),
            }, cancellationToken).ConfigureAwait(false);

            ObjectRef html;
            ObjectRef? pdf = null;
            var artifacts = new List<ObjectRef>();
            try
            {
                html = await store.PutRefAsync(settings.ReportsBucket, $"{prefix}report.html", output.Html, "text/html", cancellationToken).ConfigureAwait(false);
                if (output.Pdf != null)
                {
                    pdf = await store.PutRefAsync(settings.ReportsBucket, $"{prefix}report.pdf", output.Pdf, "application/pdf", cancellationToken).ConfigureAwait(false);
                    artifacts.Add(pdf);
                }
                artifacts.Add(await store.PutRefAsync(settings.ReportsBucket, $"{prefix}report.log", Encoding.UTF8.GetBytes(output.Log), "text/plain", cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ReportFailedException(ErrorCode.UploadFailed, $"Could not upload the report: {e.Message}", retryable: true, e);
            }

            var completedAt = DateTimeOffset.UtcNow;
            await catalog.SaveAsync(new ReportRecord
            {
                JobId = jobId, Attempt = attempt, Status = JobStatus.Completed, Spec = request.Report, Html = html, Pdf = pdf, CompletedAt = completedAt,
            }, cancellationToken).ConfigureAwait(false);
            await PublishAsync(MessageTypes.JobCompleted, jobId, attempt, new JobCompleted
            {
                JobId = jobId, Kind = JobKind.Report, Attempt = attempt, CompletedAt = completedAt, Result = html, Artifacts = artifacts, ExitCode = output.ExitCode,
            }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Report {JobId} attempt {Attempt} rendered to s3://{Bucket}/{Key} (pdf: {Pdf})", jobId, attempt, html.Bucket, html.Key, pdf != null);
            return MessageDisposition.Ack;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MessageDisposition.Retry;
        }
        catch (Exception e)
        {
            var failure = e as ReportFailedException ?? new ReportFailedException(ErrorCode.Internal, e.Message, retryable: true, e);
            var final = !failure.Retryable || attempt >= settings.MaxAttempts;
            var error = new ErrorInfo
            {
                Code = final && failure.Retryable ? ErrorCode.MaxAttemptsExceeded : failure.Code,
                Message = failure.Message,
                StackTrace = (failure.InnerException ?? failure).StackTrace,
                ExitCode = failure.ExitCode,
            };
            logger.LogWarning(e, "Report {JobId} attempt {Attempt} failed (final: {Final})", jobId, attempt, final);
            var failedAt = DateTimeOffset.UtcNow;
            await catalog.SaveAsync(new ReportRecord { JobId = jobId, Attempt = attempt, Status = JobStatus.Failed, Spec = request.Report, Error = error, CompletedAt = failedAt },
                cancellationToken).ConfigureAwait(false);
            await PublishAsync(MessageTypes.JobFailed, jobId, attempt, new JobFailed
            {
                JobId = jobId, Kind = JobKind.Report, Attempt = attempt, FailedAt = failedAt, Error = error, Final = final,
            }, cancellationToken).ConfigureAwait(false);
            return MessageDisposition.Ack;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
            }
            catch (IOException e)
            {
                logger.LogDebug(e, "Could not clean {WorkDirectory}", workDirectory);
            }
        }
    }

    /// <summary>The request's <c>liveResult</c>, or the live job's final result from the results API when only <c>report.liveJobId</c> is set.</summary>
    private async Task<ObjectRef?> ResolveLiveAsync(ReportRequested request, CancellationToken cancellationToken)
    {
        if (request.LiveResult != null || request.Report.LiveJobId is not { } liveJobId)
        {
            return request.LiveResult;
        }
        if (results == null)
        {
            throw new ReportFailedException(ErrorCode.InvalidRequest, $"liveJobId {liveJobId:D} without liveResult and no Report:ResultsApiUrl configured", retryable: false);
        }
        ObjectRef? reference;
        try
        {
            reference = await results.GetResultRefAsync(liveJobId, null, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ReportFailedException(ErrorCode.Internal, $"Results API unavailable: {e.Message}", retryable: true, e);
        }
        return reference ?? throw new ReportFailedException(ErrorCode.InvalidRequest, $"Live job {liveJobId:D} has no stored result", retryable: false);
    }

    private async Task<byte[]> DownloadAsync(ObjectRef reference, string what, CancellationToken cancellationToken)
    {
        byte[]? content;
        try
        {
            content = await store.TryGetAsync(reference.Bucket, reference.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ReportFailedException(ErrorCode.Internal, $"Could not read the {what} result s3://{reference.Bucket}/{reference.Key}: {e.Message}", retryable: true, e);
        }
        if (content == null)
        {
            throw new ReportFailedException(ErrorCode.InvalidRequest, $"The {what} result s3://{reference.Bucket}/{reference.Key} does not exist", retryable: false);
        }
        var sha256 = ResultObjectStoreExtensions.Sha256(content);
        if (content.LongLength != reference.Size || !string.Equals(sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReportFailedException(ErrorCode.InvalidRequest,
                $"The {what} result s3://{reference.Bucket}/{reference.Key} does not match its ObjectRef " +
                $"(size {content.LongLength} vs {reference.Size}, sha256 {sha256} vs {reference.Sha256})", retryable: false);
        }
        return content;
    }

    private Task PublishAsync<T>(string type, Guid jobId, int attempt, T body, CancellationToken cancellationToken) =>
        bus.PublishAsync(OutgoingMessage.Create(BusExchanges.Events, RoutingKeys.EventFor(type, JobKind.Report, jobId),
            MessageEnvelope.Create(type, jobId, attempt, AppIds.Report, JobKind.Report), body), cancellationToken);
}
