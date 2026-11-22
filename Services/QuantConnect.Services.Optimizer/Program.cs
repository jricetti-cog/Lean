using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantConnect.Logging;

namespace QuantConnect.Services.Optimizer;

// Not top-level statements: those emit a global `Program` that would shadow other `Program` types in the test assembly.
public static class OptimizerProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddOptimizerService();

        var app = builder.Build();
        Log.LogHandler = new LeanLogBridge(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("QuantConnect.Optimizer"));
        app.MapOptimizerApi();
        await app.RunAsync();
    }
}
