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
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Util;

/// <summary>
/// Helper json converter to use the safe decimal cast
/// </summary>
public class DecimalJsonConverter : JsonConverterFactory
{
    /// <summary>
    /// Determines whether this instance can convert the specified object type
    /// </summary>
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(decimal) || typeToConvert == typeof(decimal?);
    }

    /// <summary>
    /// Creates the converter for the given type
    /// </summary>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return typeToConvert == typeof(decimal) ? new Converter() : new NullableConverter();
    }

    private static decimal? ReadValue(ref Utf8JsonReader reader, Type typeToConvert)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                if (typeToConvert == typeof(decimal?))
                {
                    return null;
                }
                throw new JsonException($"Cannot convert null value to {typeToConvert}.");
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer))
                {
                    return integer;
                }
                return reader.GetDouble().SafeDecimalCast();
            case JsonTokenType.String:
                var str = reader.GetString();
                if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var res))
                {
                    return res;
                }
                return Convert.ToDecimal(str, CultureInfo.InvariantCulture);
            case JsonTokenType.True:
                return 1m;
            case JsonTokenType.False:
                return 0m;
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} when converting to {typeToConvert}.");
        }
    }

    private class Converter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return ReadValue(ref reader, typeof(decimal)).Value;
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }

    private class NullableConverter : JsonConverter<decimal?>
    {
        public override bool HandleNull => true;

        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return ReadValue(ref reader, typeof(decimal?));
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
