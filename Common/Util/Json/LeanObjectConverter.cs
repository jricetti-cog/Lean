/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Converter for values declared as <see cref="object"/>. Mirrors Newtonsoft.Json: integers are read as <see cref="long"/>,
    /// floating point numbers as <see cref="double"/> (or <see cref="decimal"/>), ISO dates as <see cref="DateTime"/>,
    /// objects as <see cref="JsonObject"/> and arrays as <see cref="JsonArray"/>. Values are written using their runtime type.
    /// </summary>
    public sealed class LeanObjectConverter : JsonConverter<object>
    {
        private readonly bool _floatAsDecimal;

        /// <summary>
        /// Creates a new instance
        /// </summary>
        /// <param name="floatAsDecimal">True to read floating point numbers as decimals</param>
        public LeanObjectConverter(bool floatAsDecimal = false)
        {
            _floatAsDecimal = floatAsDecimal;
        }

        /// <summary>Reads an untyped value</summary>
        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var integer))
                    {
                        return integer;
                    }
                    var raw = NumberFormatting.GetRawText(ref reader);
                    if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
                    {
                        return System.Numerics.BigInteger.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (_floatAsDecimal && reader.TryGetDecimal(out var number))
                    {
                        return number;
                    }
                    return reader.GetDouble();
                case JsonTokenType.String:
                    if (LooksLikeIsoDate(ref reader) && reader.TryGetDateTime(out var date))
                    {
                        return date;
                    }
                    return reader.GetString();
                case JsonTokenType.StartObject:
                    return JsonNode.Parse(ref reader, new JsonNodeOptions { PropertyNameCaseInsensitive = false }) as JsonObject;
                case JsonTokenType.StartArray:
                    return JsonNode.Parse(ref reader) as JsonArray;
                default:
                    throw new JsonException($"Unexpected token {reader.TokenType}.");
            }
        }

        /// <summary>Writes an untyped value using its runtime type</summary>
        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            if (type == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }
            JsonSerializer.Serialize(writer, value, type, options);
        }

        private static bool LooksLikeIsoDate(ref Utf8JsonReader reader)
        {
            var span = reader.HasValueSequence ? reader.ValueSequence.FirstSpan : reader.ValueSpan;
            return span.Length >= 19 && span[4] == '-' && span[7] == '-' && span[10] == 'T' && span[13] == ':' && span[16] == ':';
        }
    }
}
