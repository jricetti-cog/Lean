using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.Optimizer;

public enum AttemptOutcome
{
    Granted,
    /// <summary>The job is finished, cancelled or the dispatch was superseded: ack and drop it.</summary>
    Gone,
}

public sealed record AttemptGrant(AttemptOutcome Outcome, JobAttempt? Attempt = null, string? Detail = null);

/// <summary>Thrown when job-orchestrator refuses a child (e.g. the parent optimization is already terminal).</summary>
public sealed class ChildRejectedException(string message) : Exception(message);

/// <summary>The parts of job-orchestrator's jobs API (<c>orchestrator.openapi.yaml</c>) the optimizer uses.</summary>
public interface IOrchestratorJobs
{
    Task<AttemptGrant> StartAttemptAsync(Guid jobId, string workerId, long deliveryCount, int? attempt, CancellationToken cancellationToken);
    /// <summary>Submits a child backtest; the idempotency key makes a retried submission return the same job.</summary>
    Task<Guid> SubmitAsync(JobRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task CancelAsync(Guid jobId, string reason, CancellationToken cancellationToken);
    Task<List<Job>> ListChildrenAsync(Guid parentJobId, JobStatus status, CancellationToken cancellationToken);
}

public sealed class OrchestratorJobsClient(HttpClient http, ILogger<OrchestratorJobsClient> logger) : IOrchestratorJobs
{
    public const string SubmittedBy = "optimizer";
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    public async Task<AttemptGrant> StartAttemptAsync(Guid jobId, string workerId, long deliveryCount, int? attempt, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Post($"internal/v1/jobs/{jobId:D}/attempts", new { workerId, deliveryCount, attempt }), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone)
        {
            return new AttemptGrant(AttemptOutcome.Gone, Detail: await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var granted = await response.Content.ReadFromJsonAsync<JobAttempt>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
        return new AttemptGrant(AttemptOutcome.Granted, granted ?? throw new InvalidOperationException("job-orchestrator returned an empty attempt"));
    }

    public async Task<Guid> SubmitAsync(JobRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            var message = Post("internal/v1/jobs/", request);
            message.Headers.Add("Idempotency-Key", idempotencyKey);
            return message;
        }, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            throw new ChildRejectedException($"job-orchestrator refused the child ({(int)response.StatusCode}): " +
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var job = await response.Content.ReadFromJsonAsync<Job>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
        return job?.Id ?? throw new InvalidOperationException("job-orchestrator returned an empty job");
    }

    public async Task CancelAsync(Guid jobId, string reason, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Post($"internal/v1/jobs/{jobId:D}/cancel", new { reason }), cancellationToken).ConfigureAwait(false);
        // 409: already finished, nothing to cancel.
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<List<Job>> ListChildrenAsync(Guid parentJobId, JobStatus status, CancellationToken cancellationToken)
    {
        var jobs = new List<Job>();
        string? cursor = null;
        do
        {
            var url = $"internal/v1/jobs/?parentJobId={parentJobId:D}&kind=backtest&status={Wire(status)}&limit=500" +
                (cursor != null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync<Page<Job>>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
            if (page == null) break;
            jobs.AddRange(page.Items);
            cursor = page.Next;
        }
        while (cursor != null);
        return jobs;
    }

    private static string Wire(JobStatus status) => ContractJson.Serialize(status).Trim('"');

    private static HttpRequestMessage Post<T>(string url, T body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        message.Headers.Add("X-Lean-User", SubmittedBy);
        return message;
    }

    /// <summary>Retries connection failures and 5xx answers; returns any other response.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                var response = await http.SendAsync(request(), cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode < 500 || retry >= Backoff.Length)
                {
                    return response;
                }
                logger.LogWarning("job-orchestrator answered {Status}; retrying", (int)response.StatusCode);
                response.Dispose();
            }
            catch (HttpRequestException e) when (retry < Backoff.Length)
            {
                logger.LogWarning(e, "job-orchestrator unreachable; retrying");
            }
            await Task.Delay(Backoff[retry], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"job-orchestrator answered {(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {detail}",
                null, response.StatusCode);
        }
    }
}
