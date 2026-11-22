using QuantConnect.Configuration;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>
/// Config keys read by the bus handlers. Each key falls back to its <c>LEAN_&lt;KEY&gt;</c> environment variable
/// (same mapping as <c>LeanConfigEnvironment</c>), so the worker can pass secrets without writing them to the
/// generated <c>config.json</c>.
/// </summary>
public static class BusSettings
{
    /// <summary>Path of the dispatched <c>JobSubmitted</c> JSON written by the worker host.</summary>
    public const string JobFile = "bus-job-file";
    /// <summary>Inline dispatched <c>JobSubmitted</c> JSON (alternative to <see cref="JobFile"/>).</summary>
    public const string Job = "bus-job";
    public const string WorkerId = "bus-worker-id";
    public const string LeaseEpoch = "bus-lease-epoch";
    public const string WorkFolder = "bus-work-folder";
    public const string BuiltinAlgorithmLocation = "bus-builtin-algorithm-location";
    public const string HeartbeatIntervalSeconds = "bus-heartbeat-interval-seconds";
    public const string OutboxLimit = "bus-outbox-limit";
    public const string ShutdownTimeoutSeconds = "bus-shutdown-timeout-seconds";
    public const string RabbitMqUri = "bus-rabbitmq-uri";
    /// <summary><c>s3</c> (MinIO, default) or <c>filesystem</c>.</summary>
    public const string ArtifactStore = "bus-artifact-store";
    public const string ArtifactRoot = "bus-artifact-root";
    public const string S3Endpoint = "bus-s3-endpoint";
    public const string S3AccessKey = "bus-s3-access-key";
    public const string S3SecretKey = "bus-s3-secret-key";
    public const string S3Region = "bus-s3-region";

    public static string? Get(string key)
    {
        var value = Config.Get(key);
        if (string.IsNullOrEmpty(value))
        {
            value = Environment.GetEnvironmentVariable("LEAN_" + key.ToUpperInvariant().Replace('-', '_'));
        }
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException($"Config '{key}' (or LEAN_{key.ToUpperInvariant().Replace('-', '_')}) is required by the bus engine handlers");

    public static int GetInt(string key, int defaultValue) =>
        int.TryParse(Get(key), out var value) ? value : defaultValue;
}
