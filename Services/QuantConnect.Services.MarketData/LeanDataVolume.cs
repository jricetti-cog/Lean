using Microsoft.Extensions.Diagnostics.HealthChecks;
using QuantConnect.Logging;

namespace QuantConnect.Services.MarketData;

/// <summary>
/// The shared <c>lean-data</c> volume (ADR 0004). market-data is its only writer: every task writes into
/// <c>.staging/&lt;taskId&gt;/</c> on the same filesystem and <see cref="Publish"/> renames finished files into place,
/// so readers (workers mount the volume read-only) only ever see complete zips.
/// </summary>
public sealed class LeanDataVolume
{
    public const string StagingDirectoryName = ".staging";

    public LeanDataVolume(string root)
    {
        Root = Path.GetFullPath(root);
        StagingRoot = Path.Combine(Root, StagingDirectoryName);
    }

    public string Root { get; }
    public string StagingRoot { get; }

    public string StagingFor(Guid taskId) => Path.Combine(StagingRoot, taskId.ToString("D"));

    /// <summary>Removes staging directories left by a crashed task (nothing in them was ever visible to readers).</summary>
    public void CleanStaging()
    {
        if (!Directory.Exists(StagingRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
        {
            TryDelete(dir);
        }
    }

    /// <summary>Copies the published file for <paramref name="relativePath"/> into staging so new data merges with it.</summary>
    public bool CopyToStaging(string staging, string relativePath)
    {
        var source = Path.Combine(Root, relativePath);
        if (!File.Exists(source)) return false;
        var target = Path.Combine(staging, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        return true;
    }

    /// <summary>
    /// Atomically renames every file under <paramref name="staging"/> to the same relative path under <see cref="Root"/>
    /// (same filesystem, so each <see cref="File.Move(string,string,bool)"/> is a rename) and deletes the staging directory.
    /// Returns the published relative paths.
    /// </summary>
    public IReadOnlyList<string> Publish(string staging)
    {
        var published = new List<string>();
        if (!Directory.Exists(staging)) return published;
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(staging, file);
            var target = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
            published.Add(relative.Replace('\\', '/'));
        }
        TryDelete(staging);
        return published;
    }

    public static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e)
        {
            Log.Error(e, $"LeanDataVolume: could not delete {dir}");
        }
    }
}

/// <summary>Readiness: the volume is mounted and writable (market-data is its writer).</summary>
public sealed class LeanDataVolumeHealthCheck : IHealthCheck
{
    private readonly LeanDataVolume _volume;

    public LeanDataVolumeHealthCheck(LeanDataVolume volume) => _volume = volume;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_volume.StagingRoot);
            var probe = Path.Combine(_volume.StagingRoot, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception e)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"lean-data volume at {_volume.Root} is not writable", e));
        }
    }
}
