using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace QuantConnect.Services.Defaults;

public sealed class LeanServiceDefaultsOptions
{
    /// <summary>
    /// The host runs LEAN code: copy <c>LEAN_*</c> environment variables onto LEAN's <c>Config</c> before first use
    /// (<see cref="LeanConfigEnvironment"/>).
    /// </summary>
    public bool HostsLeanCode { get; set; }

    /// <summary>Extra tracing setup (sources, exporters) after the defaults.</summary>
    public Action<TracerProviderBuilder>? ConfigureTracing { get; set; }

    /// <summary>Extra metrics setup (meters, exporters) after the defaults.</summary>
    public Action<MeterProviderBuilder>? ConfigureMetrics { get; set; }
}
