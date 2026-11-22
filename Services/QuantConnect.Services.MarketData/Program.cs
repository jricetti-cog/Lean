using Microsoft.AspNetCore.Builder;
using QuantConnect.Services.Defaults;

namespace QuantConnect.Services.MarketData;

/// <summary>market-data host (a named class: a top-level <c>Program</c> would shadow LEAN's launcher <c>Program</c> types for referencing projects).</summary>
public static class MarketDataHost
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddLeanServiceDefaults(options => options.HostsLeanCode = true);
        builder.AddMarketData();

        var app = builder.Build();
        app.MapMarketData();
        app.Run();
    }
}
