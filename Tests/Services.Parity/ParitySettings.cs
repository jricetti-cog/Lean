namespace QuantConnect.Tests.Services.Parity;

/// <summary>Environment knobs of the harness (all optional; see README.md).</summary>
public static class ParitySettings
{
    /// <summary>Gateway base URL; the harness is ignored when it is unset and the default is not reachable.</summary>
    public static Uri GatewayUrl => new((Env("PARITY_GATEWAY_URL") ?? "http://127.0.0.1:5080").TrimEnd('/') + "/");

    public static bool GatewayExplicit => Env("PARITY_GATEWAY_URL") != null;

    /// <summary><c>PARITY_API_KEY</c>, else <c>GATEWAY_API_KEY</c> from the environment, the repo's <c>.env</c> or <c>.env.example</c>.</summary>
    public static string ApiKey => Env("PARITY_API_KEY") ?? Env("GATEWAY_API_KEY") ?? DotEnv("GATEWAY_API_KEY") ?? "lean-dev-api-key";

    /// <summary>Jobs kept in flight (submitted, not terminal) at once; set it to about the number of backtest workers.</summary>
    public static int Concurrency => int.TryParse(Env("PARITY_CONCURRENCY"), out var value) && value > 0 ? value : 8;

    public static int MaxRuntimeSeconds => int.TryParse(Env("PARITY_MAX_RUNTIME_SECONDS"), out var value) && value > 0 ? value : 3600;

    public static HashSet<Language> Languages => (Env("PARITY_LANGUAGES") ?? "CSharp,Python")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Enum.Parse<Language>).ToHashSet();

    /// <summary>Comma-separated algorithm names (<c>Name</c> or <c>Language/Name</c>) to run a subset by hand.</summary>
    public static HashSet<string> Only => (Env("PARITY_ONLY") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>TRX of LEAN's own <c>RegressionTests.AlgorithmStatisticsRegression</c> run: the monolith column of the table.</summary>
    public static string? MonolithTrx => Env("PARITY_MONOLITH_TRX");

    /// <summary>
    /// The algorithm parameters LEAN's suite runs with: <c>parameters</c> of <c>Launcher/config.json</c>, which the test
    /// binaries load as their <c>config.json</c>. Each job is submitted with them (e.g. <c>GetParameterRegressionAlgorithm</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> MonolithParameters { get; } = ReadLauncherParameters();

    public static string OutputFolder => Path.GetFullPath(Env("PARITY_OUTPUT_DIR") ?? Path.Combine(ParityCatalog.RepoRoot, "Tests", "Services.Parity", "parity-results"));

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string? DotEnv(string name)
    {
        foreach (var file in new[] { ".env", ".env.example" })
        {
            var path = Path.Combine(ParityCatalog.RepoRoot, file);
            if (!File.Exists(path)) continue;
            var line = File.ReadLines(path).Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal));
            if (line != null && line.Length > name.Length + 1) return line[(name.Length + 1)..].Trim('"', '\'');
        }
        return null;
    }

    private static Dictionary<string, string> ReadLauncherParameters()
    {
        var config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(ParityCatalog.RepoRoot, "Launcher", "config.json")));
        return (config["parameters"] as Newtonsoft.Json.Linq.JObject)?.Properties()
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.Ordinal) ?? new Dictionary<string, string>();
    }
}
