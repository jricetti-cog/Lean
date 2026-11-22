/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.BacktestWorker;

public enum AttemptOutcome
{
    /// <summary>The worker owns this attempt.</summary>
    Granted,
    /// <summary>Unknown, terminal or superseded job: drop the delivery.</summary>
    Gone,
    /// <summary>job-orchestrator rejected the request itself: the delivery can never be run.</summary>
    Rejected,
    /// <summary>The redelivery exceeded the job's maximum attempts and the job was failed: dead-letter the poison dispatch.</summary>
    Exhausted,
}

public sealed record AttemptGrant(AttemptOutcome Outcome, JobAttempt? Attempt = null, string? Detail = null);

/// <summary><c>POST /internal/v1/jobs/{id}/attempts</c>: claims (or, on redelivery, renews) the attempt a dispatch runs as.</summary>
public interface IAttemptClient
{
    Task<AttemptGrant> StartAttemptAsync(Guid jobId, string workerId, long deliveryCount, int? attempt, CancellationToken cancellationToken);
}

public sealed class OrchestratorAttemptClient : IAttemptClient
{
    public const string HttpClientName = "job-orchestrator";
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<OrchestratorAttemptClient> _logger;

    public OrchestratorAttemptClient(IHttpClientFactory http, ILogger<OrchestratorAttemptClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<AttemptGrant> StartAttemptAsync(Guid jobId, string workerId, long deliveryCount, int? attempt, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                using var response = await _http.CreateClient(HttpClientName).PostAsJsonAsync($"internal/v1/jobs/{jobId:D}/attempts",
                    new { workerId, deliveryCount, attempt }, ContractJson.Options, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var granted = await response.Content.ReadFromJsonAsync<JobAttempt>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
                    return new AttemptGrant(AttemptOutcome.Granted, granted ?? throw new InvalidOperationException("job-orchestrator returned an empty attempt"));
                }
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Gone)
                {
                    return new AttemptGrant(AttemptOutcome.Exhausted, Detail: detail);
                }
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                {
                    return new AttemptGrant(AttemptOutcome.Gone, Detail: detail);
                }
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    return new AttemptGrant(AttemptOutcome.Rejected, Detail: detail);
                }
                if (retry >= Backoff.Length)
                {
                    throw new HttpRequestException($"job-orchestrator answered {(int)response.StatusCode} for job {jobId:D}: {detail}");
                }
            }
            catch (HttpRequestException e) when (retry < Backoff.Length)
            {
                _logger.LogWarning(e, "job-orchestrator unreachable claiming job {JobId}; retrying", jobId);
            }
            await Task.Delay(Backoff[retry], cancellationToken).ConfigureAwait(false);
        }
    }
}
