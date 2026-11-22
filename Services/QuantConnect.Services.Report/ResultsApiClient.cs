using System.Net;
using System.Net.Http.Json;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.Report;

/// <summary>Results API (<c>results.openapi.yaml</c>): resolves a job's final result JSON reference.</summary>
public sealed class ResultsApiClient(HttpClient http)
{
    /// <summary>The <c>result</c> reference of <paramref name="jobId"/> (latest attempt when null), or null if there is none.</summary>
    public async Task<ObjectRef?> GetResultRefAsync(Guid jobId, int? attempt, CancellationToken cancellationToken)
    {
        var uri = $"internal/v1/results/{jobId:D}" + (attempt is { } a ? $"?attempt={a}" : string.Empty);
        using var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<ResultSummary>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
        return summary?.Result;
    }
}
