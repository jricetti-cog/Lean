using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Messaging;

/// <summary>Maps <see cref="MessageHeaders"/> to and from AMQP header tables (strings arrive as UTF-8 bytes).</summary>
public static class AmqpHeaders
{
    public static Dictionary<string, object?> ToAmqp(MessageHeaders headers)
    {
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(headers, ContractJson.Options).EnumerateObject())
        {
            table[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out var integer) ? integer : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.Value.GetRawText(),
            };
        }
        return table;
    }

    /// <summary>Scalar headers only; broker tables such as <c>x-death</c> are ignored.</summary>
    public static MessageHeaders FromAmqp(IDictionary<string, object?>? table)
    {
        var json = new JsonObject();
        foreach (var (key, value) in table ?? new Dictionary<string, object?>())
        {
            JsonNode? node = value switch
            {
                byte[] bytes => JsonValue.Create(Encoding.UTF8.GetString(bytes)),
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value)),
                float or double or decimal => JsonValue.Create(Convert.ToDouble(value)),
                _ => null,
            };
            if (node != null)
            {
                json[key] = node;
            }
        }
        return json.Deserialize<MessageHeaders>(ContractJson.Options) ?? throw new JsonException("Missing headers");
    }
}
