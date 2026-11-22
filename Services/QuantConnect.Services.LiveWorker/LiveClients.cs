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

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.LiveWorker;

public enum LeaseOutcome
{
    Granted,
    /// <summary>Unknown, finished, cancelled job, or another worker holds a live lease: this container has nothing to run.</summary>
    Refused,
}

public sealed record LeaseGrant(LeaseOutcome Outcome, Lease? Lease = null, string? Detail = null);

/// <summary>The two services a live-worker talks to over HTTP: the lease (job-orchestrator) and the last portfolio (results).</summary>
public interface ILiveServices
{
    Task<LeaseGrant> AcquireLeaseAsync(Guid jobId, string workerId, CancellationToken cancellationToken);
    Task<LiveState?> LiveStateAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class LiveServicesClient : ILiveServices
{
    public const string Orchestrator = "job-orchestrator";
    public const string Results = "results";
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<LiveServicesClient> _logger;

    public LiveServicesClient(IHttpClientFactory http, ILogger<LiveServicesClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<LeaseGrant> AcquireLeaseAsync(Guid jobId, string workerId, CancellationToken cancellationToken) => WithRetry(Orchestrator, async client =>
    {
        using var response = await client.PostAsJsonAsync($"internal/v1/jobs/{jobId:D}/lease", new { workerId }, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var lease = await response.Content.ReadFromJsonAsync<Lease>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
            return new LeaseGrant(LeaseOutcome.Granted, lease ?? throw new InvalidOperationException("job-orchestrator returned an empty lease"));
        }
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 400 and < 500) return new LeaseGrant(LeaseOutcome.Refused, Detail: $"{(int)response.StatusCode} {detail}");
        throw new HttpRequestException($"job-orchestrator answered {(int)response.StatusCode} for the lease of {jobId:D}: {detail}");
    }, cancellationToken);

    public Task<LiveState?> LiveStateAsync(Guid jobId, CancellationToken cancellationToken) => WithRetry(Results, async client =>
    {
        using var response = await client.GetAsync($"internal/v1/results/{jobId:D}/live-state", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LiveState>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    private async Task<T> WithRetry<T>(string name, Func<HttpClient, Task<T>> call, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await call(_http.CreateClient(name)).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (retry < Backoff.Length)
            {
                _logger.LogWarning(e, "{Service} unavailable; retrying", name);
            }
            await Task.Delay(Backoff[retry], cancellationToken).ConfigureAwait(false);
        }
    }
}
