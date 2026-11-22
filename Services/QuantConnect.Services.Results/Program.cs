using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using QuantConnect.Services.Results.Storage;

namespace QuantConnect.Services.Results;

// Not top-level statements: those emit a global `Program` that would shadow other `Program` types in the test assembly.
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddResultsService();

        var app = builder.Build();
        await app.Services.GetRequiredService<ResultsDatabase>().MigrateWithRetryAsync(TimeSpan.FromMinutes(1));
        app.MapResultsApi();
        await app.RunAsync();
    }
}
