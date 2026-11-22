using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace QuantConnect.Services.Contracts;

/// <summary>
/// The one serializer configuration for v1 contract bodies and HTTP payloads: camelCase, nulls omitted,
/// enums as their contract strings, timestamps as UTC ISO-8601 with a trailing <c>Z</c>, dates as <c>yyyy-MM-dd</c>.
/// Inline engine packets are NOT serialized with these options; they keep LEAN's own Newtonsoft form.
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new UtcTimestampConverter());
        options.MakeReadOnly();
        return options;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, Options) ?? throw new JsonException($"Null {typeof(T).Name} body");

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Null {typeof(T).Name} body");

    public static object Deserialize(ReadOnlySpan<byte> utf8Json, Type type) =>
        JsonSerializer.Deserialize(utf8Json, type, Options) ?? throw new JsonException($"Null {type.Name} body");
}

/// <summary><c>defs.schema.json#/$defs/Timestamp</c>: always written in UTC as <c>yyyy-MM-ddTHH:mm:ss[.fffffff]Z</c>.</summary>
public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}
