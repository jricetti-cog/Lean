var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var root = Path.GetFullPath(Environment.GetEnvironmentVariable("LEAN_DATA_ROOT") ?? "/lean-data") + Path.DirectorySeparatorChar;
long requests = 0, notFound = 0, bytes = 0;

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/data/{**path}", (string path) =>
{
    Interlocked.Increment(ref requests);
    var full = Path.GetFullPath(Path.Combine(root, path));
    if (!full.StartsWith(root, StringComparison.Ordinal))
    {
        return Results.BadRequest();
    }
    var file = new FileInfo(full);
    if (!file.Exists)
    {
        Interlocked.Increment(ref notFound);
        return Results.NotFound();
    }
    Interlocked.Add(ref bytes, file.Length);
    return Results.File(full, "application/octet-stream");
});

app.MapGet("/stats", () => Results.Ok(new { requests = Interlocked.Read(ref requests), notFound = Interlocked.Read(ref notFound), bytes = Interlocked.Read(ref bytes) }));
app.MapPost("/stats/reset", () =>
{
    Interlocked.Exchange(ref requests, 0);
    Interlocked.Exchange(ref notFound, 0);
    Interlocked.Exchange(ref bytes, 0);
    return Results.NoContent();
});

app.Run();
