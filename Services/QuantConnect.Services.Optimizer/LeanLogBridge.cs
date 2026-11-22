using Microsoft.Extensions.Logging;
using QuantConnect.Logging;

namespace QuantConnect.Services.Optimizer;

/// <summary>Routes LEAN's static <see cref="Log"/> (used by <c>LeanOptimizer</c> and the strategies) into the host's JSON logging.</summary>
public sealed class LeanLogBridge(ILogger logger) : ILogHandler
{
    public void Error(string text) => logger.LogError("{Message}", text);
    public void Debug(string text) => logger.LogDebug("{Message}", text);
    public void Trace(string text) => logger.LogInformation("{Message}", text);
    public void Report(string text) => logger.LogInformation("{Message}", text);
    public void Dispose() { }
}
