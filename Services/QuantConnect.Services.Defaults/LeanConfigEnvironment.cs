using System.Collections;
using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;

namespace QuantConnect.Services.Defaults;

/// <summary>
/// 12-factor bridge onto LEAN's <see cref="Config"/>: <c>LEAN_&lt;KEY&gt;</c> sets config key <c>&lt;key&gt;</c>
/// lower-cased with <c>_</c> → <c>-</c> (e.g. <c>LEAN_DATA_FOLDER=/Lean/Data/</c> → <c>data-folder</c>,
/// <c>LEAN_JOB_QUEUE_HANDLER</c> → <c>job-queue-handler</c>). JSON array/object values are parsed. Keys not
/// set in the environment keep their <c>config.json</c> value, so a host without <c>LEAN_*</c> variables
/// behaves exactly like the monolith.
/// </summary>
public static class LeanConfigEnvironment
{
    public const string Prefix = "LEAN_";

    /// <summary>The config key for an environment variable name, or null if it is not a <c>LEAN_*</c> variable.</summary>
    public static string? ToConfigKey(string variable) =>
        variable.Length > Prefix.Length && variable.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? variable[Prefix.Length..].ToLowerInvariant().Replace('_', '-')
            : null;

    /// <summary>Applies the process environment; returns the config keys set.</summary>
    public static IReadOnlyList<string> Apply() => Apply(Environment.GetEnvironmentVariables());

    public static IReadOnlyList<string> Apply(IDictionary variables)
    {
        var applied = new List<string>();
        foreach (DictionaryEntry entry in variables)
        {
            if (entry.Key is not string name || ToConfigKey(name) is not { } key || entry.Value is not string value)
            {
                continue;
            }
            var trimmed = value.Trim();
            if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
            {
                Config.Set(key, JToken.Parse(trimmed));
            }
            else
            {
                Config.Set(key, value);
            }
            applied.Add(key);
        }
        applied.Sort(StringComparer.Ordinal);
        return applied;
    }
}
