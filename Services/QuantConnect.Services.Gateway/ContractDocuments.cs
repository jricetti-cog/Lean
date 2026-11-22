using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace QuantConnect.Services.Gateway;

/// <summary>
/// The service contracts under <c>docs/architecture/contracts/</c>, embedded in the gateway. <c>/openapi</c> uses a
/// service's contract when its live document cannot be fetched. Cross-file <c>$ref</c>s are rewritten to local
/// components so the merged document is self-contained.
/// </summary>
public static class ContractDocuments
{
    private const string Prefix = "contracts/";
    private const string SchemaSuffix = ".schema.json";
    private static readonly Regex WholeFileRef = new(@"^(?:[^#]*/)?(?<name>[^/#]+)\.schema\.json$", RegexOptions.Compiled);
    private static readonly Regex FileRef = new(@"^(?:[^#]*/)?(?<file>[^/#]+)#/(?<path>.+)$", RegexOptions.Compiled);
    private static readonly Lazy<(Dictionary<string, JsonObject> Documents, JsonObject Components)> Loaded = new(Load);

    public static readonly IReadOnlyDictionary<string, string> ClusterContracts = new Dictionary<string, string>
    {
        [GatewayProxy.Results] = "results.openapi.yaml",
        [GatewayProxy.MarketData] = "market-data.openapi.yaml",
        [GatewayProxy.Report] = "report.openapi.yaml",
        [GatewayProxy.Optimizer] = "optimizer.openapi.yaml",
    };

    /// <summary>The contract document behind <paramref name="cluster"/>, with refs rewritten, or null.</summary>
    public static JsonObject? For(string cluster) =>
        ClusterContracts.TryGetValue(cluster, out var file) && Loaded.Value.Documents.TryGetValue(file, out var document) ? document : null;

    /// <summary>Components of every contract file (plus the message <c>$defs</c> as schemas) that rewritten refs point at.</summary>
    public static JsonObject Components => Loaded.Value.Components;

    private static (Dictionary<string, JsonObject>, JsonObject) Load()
    {
        var assembly = typeof(ContractDocuments).Assembly;
        var documents = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var components = new JsonObject();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            JsonNode? node;
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                node = JsonNode.Parse(text);
            }
            else
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(text));
                node = ToJson(yaml.Documents[0].RootNode);
            }
            if (node is not JsonObject document) continue;
            Rewrite(document);
            documents[Path.GetFileName(name)] = document;
            if (document["$defs"] is JsonObject defs) Merge(components, "schemas", defs);
            if (name.EndsWith(SchemaSuffix, StringComparison.Ordinal) && !name.EndsWith("/defs" + SchemaSuffix, StringComparison.Ordinal))
            {
                var schema = (JsonObject)document.DeepClone();
                schema.Remove("$schema");
                schema.Remove("$id");
                schema.Remove("$defs");
                Merge(components, "schemas", new JsonObject { [Path.GetFileName(name)[..^SchemaSuffix.Length]] = schema });
            }
            if (document["components"] is JsonObject parts)
            {
                foreach (var (kind, entries) in parts)
                {
                    if (entries is JsonObject entryObject) Merge(components, kind, entryObject);
                }
            }
        }
        return (documents, components);
    }

    private static void Merge(JsonObject components, string kind, JsonObject entries)
    {
        if (components[kind] is not JsonObject target) components[kind] = target = new JsonObject();
        foreach (var (name, value) in entries)
        {
            if (!target.ContainsKey(name)) target[name] = value?.DeepClone();
        }
    }

    /// <summary><c>dir/name.schema.json</c> becomes <c>#/components/schemas/name</c>; <c>file#/components/x/y</c> becomes <c>#/components/x/y</c>; <c>#/$defs/y</c> and <c>file#/$defs/y</c> become <c>#/components/schemas/y</c>.</summary>
    private static void Rewrite(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference)) obj["$ref"] = RewriteRef(reference);
                foreach (var (_, child) in obj.ToList()) Rewrite(child);
                break;
            case JsonArray array:
                foreach (var child in array) Rewrite(child);
                break;
        }
    }

    public static string RewriteRef(string reference)
    {
        if (WholeFileRef.Match(reference) is { Success: true } whole) return $"#/components/schemas/{whole.Groups["name"].Value}";
        var path = reference.StartsWith("#/", StringComparison.Ordinal)
            ? reference[2..]
            : FileRef.Match(reference) is { Success: true } match ? match.Groups["path"].Value : null;
        if (path == null) return reference;
        return path.StartsWith("$defs/", StringComparison.Ordinal) ? $"#/components/schemas/{path["$defs/".Length..]}" : $"#/{path}";
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, ToJson(c.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlScalarNode { Style: ScalarStyle.Plain } scalar => Plain(scalar.Value),
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => null,
    };

    private static JsonNode? Plain(string? value) => value switch
    {
        null or "" or "~" or "null" => null,
        "true" => JsonValue.Create(true),
        "false" => JsonValue.Create(false),
        _ when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
        _ when Regex.IsMatch(value, @"^-?\d+\.\d+(e[-+]?\d+)?$", RegexOptions.IgnoreCase)
               && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        _ => JsonValue.Create(value),
    };
}
