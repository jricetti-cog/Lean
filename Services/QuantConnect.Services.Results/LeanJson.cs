using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantConnect.Services.Results;

/// <summary>
/// Reads LEAN JSON without referencing the engine: inline packets are LEAN's default (PascalCase) Newtonsoft JSON,
/// result files are camelCase. Lookups are case-insensitive and stored objects are normalized to the result-JSON
/// (camelCase) form the results API serves.
/// </summary>
public static class LeanJson
{
    public static JsonNode? Get(this JsonObject? node, string name)
    {
        if (node == null)
        {
            return null;
        }
        if (node.TryGetPropertyValue(name, out var exact))
        {
            return exact;
        }
        foreach (var (key, value) in node)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    public static JsonObject? GetObject(this JsonObject? node, string name) => node.Get(name) as JsonObject;

    public static JsonArray? GetArray(this JsonObject? node, string name) => node.Get(name) as JsonArray;

    public static string? GetString(this JsonObject? node, string name) => node.Get(name) switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    public static double? GetDouble(this JsonObject? node, string name) => AsDouble(node.Get(name));

    public static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }
        if (value.GetValueKind() == JsonValueKind.Number)
        {
            // Nodes built in memory hold their CLR type (int, long, decimal...): read them through their JSON text.
            return double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        return value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    public static int? GetInt(this JsonObject? node, string name)
    {
        var number = node.GetDouble(name);
        return number is { } n && n == Math.Floor(n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
    }

    /// <summary>String dictionary (statistics, runtime statistics, state).</summary>
    public static Dictionary<string, string> GetStringMap(this JsonObject? node, string name)
    {
        var map = new Dictionary<string, string>();
        if (node.GetObject(name) is { } values)
        {
            foreach (var (key, value) in values)
            {
                if (value is JsonValue scalar)
                {
                    map[key] = scalar.TryGetValue<string>(out var text) ? text : scalar.ToJsonString();
                }
            }
        }
        return map;
    }

    /// <summary>Recursively camelCases property names like Newtonsoft's <c>CamelCaseNamingStrategy</c>.</summary>
    public static JsonNode? ToCamelCase(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(CamelCase(p.Key), ToCamelCase(p.Value)))),
        JsonArray array => new JsonArray(array.Select(ToCamelCase).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    /// <summary>A LEAN <c>Chart</c> in result-JSON form: chart fields camelCased, series names kept as they are.</summary>
    public static JsonObject NormalizeChart(string name, JsonObject chart)
    {
        var normalized = new JsonObject();
        foreach (var (key, value) in chart)
        {
            var camel = CamelCase(key);
            if (camel == "series")
            {
                continue;
            }
            normalized[camel] = value?.DeepClone();
        }
        normalized["name"] = normalized["name"]?.GetValue<string>() is { Length: > 0 } chartName ? chartName : name;
        var series = new JsonObject();
        if (chart.GetObject("series") is { } source)
        {
            foreach (var (seriesName, value) in source)
            {
                if (value is JsonObject seriesObject)
                {
                    var copy = new JsonObject();
                    foreach (var (key, field) in seriesObject)
                    {
                        copy[CamelCase(key)] = field?.DeepClone();
                    }
                    copy["values"] ??= new JsonArray();
                    series[seriesName] = copy;
                }
            }
        }
        normalized["series"] = series;
        return normalized;
    }

    /// <summary>Port of Newtonsoft.Json's <c>StringUtils.ToCamelCase</c>.</summary>
    public static string CamelCase(string s)
    {
        if (string.IsNullOrEmpty(s) || !char.IsUpper(s[0]))
        {
            return s;
        }
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }
            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                if (char.IsSeparator(chars[i + 1]))
                {
                    chars[i] = char.ToLowerInvariant(chars[i]);
                }
                break;
            }
            chars[i] = char.ToLowerInvariant(chars[i]);
        }
        return new string(chars);
    }

    /// <summary>LEAN serializes <c>DateTime</c> as ISO 8601 without offset in UTC; series points use unix seconds.</summary>
    public static DateTimeOffset? AsTime(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<string>(out var text))
        {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
        }
        return AsDouble(value) is { } seconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null;
    }

    public static JsonObject ParseObject(ReadOnlySpan<byte> utf8) =>
        JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
        ?? throw new JsonException("Expected a JSON object");
}
