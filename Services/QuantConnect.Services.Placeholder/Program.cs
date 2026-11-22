using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using QuantConnect.Services.Defaults;

// Services/Dockerfile publishes this host under a service's assembly name (QuantConnect.Services.<Service>.dll)
// while that service's own project does not exist yet, so every image target builds, starts and passes its
// HEALTHCHECK (/healthz, /readyz from AddLeanServiceDefaults) from the first platform step on.
var builder = WebApplication.CreateBuilder(args);
builder.AddLeanServiceDefaults();

var app = builder.Build();
var serviceName = app.Configuration["Service:Name"] ?? app.Environment.ApplicationName;

app.MapGet("/", () => Results.Json(new
{
    service = serviceName,
    placeholder = true,
    message = "Placeholder host: this service is not implemented yet.",
}));

app.Run();
