using System.Buffers.Text;
using System.Text;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Results;
using ReportRecord = QuantConnect.Services.Contracts.Http.Report;

namespace QuantConnect.Services.Report;

/// <summary>
/// Report status records (<c>report.openapi.yaml#/components/schemas/Report</c>) kept in MinIO next to the outputs:
/// <c>lean-reports/&lt;jobId&gt;/&lt;attempt&gt;/report.json</c> per attempt and <c>lean-reports/_index/&lt;jobId&gt;.json</c>
/// for the latest attempt, so the report API needs no database.
/// </summary>
public sealed class ReportCatalog(IResultObjectStore store, ReportSettings settings)
{
    public const string IndexPrefix = "_index/";
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    public static string AttemptPrefix(Guid jobId, int attempt) => $"{jobId:D}/{attempt}/";

    public async Task SaveAsync(ReportRecord report, CancellationToken cancellationToken)
    {
        var json = ContractJson.SerializeToUtf8Bytes(report);
        await store.PutAsync(settings.ReportsBucket, $"{AttemptPrefix(report.JobId, report.Attempt)}report.json", json, "application/json", cancellationToken).ConfigureAwait(false);
        var latest = await GetAsync(report.JobId, null, cancellationToken).ConfigureAwait(false);
        if (latest == null || latest.Attempt <= report.Attempt)
        {
            await store.PutAsync(settings.ReportsBucket, $"{IndexPrefix}{report.JobId:D}.json", json, "application/json", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The record of <paramref name="attempt"/>, or of the latest attempt when null.</summary>
    public async Task<ReportRecord?> GetAsync(Guid jobId, int? attempt, CancellationToken cancellationToken)
    {
        var key = attempt is { } a ? $"{AttemptPrefix(jobId, a)}report.json" : $"{IndexPrefix}{jobId:D}.json";
        var json = await store.TryGetAsync(settings.ReportsBucket, key, cancellationToken).ConfigureAwait(false);
        return json == null ? null : ContractJson.Deserialize<ReportRecord>(json);
    }

    /// <summary>Latest record per report job ordered by job id; <paramref name="cursor"/> is the opaque <c>next</c> of the previous page.</summary>
    public async Task<Contracts.Http.Page<ReportRecord>> ListAsync(Guid? sourceJobId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        Guid? after = cursor == null ? null : Guid.Parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)));
        var keys = (await store.ListAsync(settings.ReportsBucket, IndexPrefix, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Select(o => Guid.TryParse(Path.GetFileNameWithoutExtension(o.Key), out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Where(id => after == null || id.CompareTo(after.Value) > 0)
            .Order();
        var items = new List<ReportRecord>();
        string? next = null;
        foreach (var id in keys)
        {
            if (await GetAsync(id, null, cancellationToken).ConfigureAwait(false) is not { } report) continue;
            if (sourceJobId != null && report.Spec.SourceJobId != sourceJobId) continue;
            if (items.Count == take)
            {
                next = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(items[^1].JobId.ToString("D")));
                break;
            }
            items.Add(report);
        }
        return new Contracts.Http.Page<ReportRecord> { Items = items, Next = next };
    }
}
