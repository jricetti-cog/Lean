using Microsoft.AspNetCore.Builder;

namespace QuantConnect.Services.Report;

// Not top-level statements: those emit a global `Program` that would shadow other `Program` types in the test assembly.
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddReportService();

        var app = builder.Build();
        app.MapReportApi();
        await app.RunAsync();
    }
}
