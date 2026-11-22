using System.Text.Json;

namespace QuantConnect.Services.Messaging;

/// <summary>
/// The RabbitMQ definitions export (<c>docs/architecture/contracts/topology/definitions.json</c>, embedded):
/// the single source for broker declarations and the in-memory fake.
/// </summary>
public sealed class TopologyDefinition
{
    public sealed record Exchange(string Name, string Type, bool Durable = true, bool AutoDelete = false);
    public sealed record Queue(string Name, IReadOnlyDictionary<string, object?> Arguments, bool Durable = true, bool AutoDelete = false)
    {
        public long? DeliveryLimit => Arguments.TryGetValue("x-delivery-limit", out var v) && v is long l ? l : null;
        public string? DeadLetterExchange => Arguments.TryGetValue("x-dead-letter-exchange", out var v) ? v as string : null;
        public string? DeadLetterRoutingKey => Arguments.TryGetValue("x-dead-letter-routing-key", out var v) ? v as string : null;
    }
    public sealed record Binding(string Source, string Destination, string RoutingKey);

    public required IReadOnlyList<Exchange> Exchanges { get; init; }
    public required IReadOnlyList<Queue> Queues { get; init; }
    public required IReadOnlyList<Binding> Bindings { get; init; }

    private static readonly Lazy<TopologyDefinition> _v1 = new(() =>
    {
        using var stream = typeof(TopologyDefinition).Assembly.GetManifestResourceStream("QuantConnect.Services.Messaging.definitions.json")
            ?? throw new InvalidOperationException("definitions.json resource missing");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The v1 topology shipped with the contracts.</summary>
    public static TopologyDefinition V1 => _v1.Value;

    public static TopologyDefinition Parse(string definitionsJson)
    {
        using var document = JsonDocument.Parse(definitionsJson);
        var root = document.RootElement;
        IEnumerable<JsonElement> Items(string name) => root.TryGetProperty(name, out var array) ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();
        return new TopologyDefinition
        {
            Exchanges = Items("exchanges").Select(e => new Exchange(e.GetProperty("name").GetString()!, e.GetProperty("type").GetString()!, Flag(e, "durable", true), Flag(e, "auto_delete", false))).ToList(),
            Queues = Items("queues").Select(q => new Queue(q.GetProperty("name").GetString()!, Arguments(q), Flag(q, "durable", true), Flag(q, "auto_delete", false))).ToList(),
            Bindings = Items("bindings")
                .Where(b => b.GetProperty("destination_type").GetString() == "queue")
                .Select(b => new Binding(b.GetProperty("source").GetString()!, b.GetProperty("destination").GetString()!, b.GetProperty("routing_key").GetString()!))
                .ToList(),
        };
    }

    private static bool Flag(JsonElement element, string name, bool fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static IReadOnlyDictionary<string, object?> Arguments(JsonElement queue)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (queue.TryGetProperty("arguments", out var args))
        {
            foreach (var arg in args.EnumerateObject())
            {
                arguments[arg.Name] = arg.Value.ValueKind switch
                {
                    JsonValueKind.Number => arg.Value.GetInt64(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => arg.Value.GetString(),
                };
            }
        }
        return arguments;
    }
}
