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
 *
*/

using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;

namespace QuantConnect
{
    /// <summary>
    /// Defines a <see cref="JsonConverter"/> to be used when deserializing to
    /// the <see cref="Symbol"/> class.
    /// </summary>
    public class SymbolJsonConverter : JsonConverter<Symbol>
    {
        /// <summary>
        /// Writes the JSON representation of the object.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Symbol symbol, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("value", symbol.Value);
            writer.WriteString("id", symbol.ID.ToString());
            writer.WriteString("permtick", symbol.Value);
            if (symbol.HasUnderlying)
            {
                writer.WritePropertyName("underlying");
                Write(writer, symbol.Underlying, options);
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override Symbol Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jobject = JsonNode.Parse(ref reader) as JsonObject;
            if (jobject == null)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} when reading a Symbol.");
            }

            if (jobject.TryGetValue("type", StringComparison.InvariantCultureIgnoreCase, out _))
            {
                return BuildSymbolFromUserFriendlyValue(jobject);
            }
            return ReadSymbolFromJson(jobject);
        }

        /// <summary>
        /// Reads a symbol used as a dictionary key
        /// </summary>
        public override Symbol ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
#pragma warning disable CS0618
            return reader.GetString();
#pragma warning restore CS0618
        }

        /// <summary>
        /// Writes a symbol used as a dictionary key
        /// </summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, Symbol value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString());
        }

        private Symbol ReadSymbolFromJson(JsonObject jObject)
        {
            JsonNode symbolId;
            JsonNode value;

            if ((jObject.TryGetValue("ID", StringComparison.InvariantCultureIgnoreCase, out symbolId)
                && jObject.TryGetValue("Value", StringComparison.InvariantCultureIgnoreCase, out value))
                || (jObject.TryGetValue("id", StringComparison.InvariantCultureIgnoreCase, out symbolId)
                && jObject.TryGetValue("value", StringComparison.InvariantCultureIgnoreCase, out value)))
            {
                Symbol underlyingSymbol = null;
                JsonNode underlying;
                if (jObject.TryGetValue("Underlying", StringComparison.InvariantCultureIgnoreCase, out underlying)
                    || jObject.TryGetValue("underlying", StringComparison.InvariantCultureIgnoreCase, out underlying))
                {
                    underlyingSymbol = underlying is JsonObject underlyingObject ? ReadSymbolFromJson(underlyingObject) : null;
                }

                return new Symbol(SecurityIdentifier.Parse(symbolId.AsText()), value.AsText(), underlyingSymbol);
            }
            return null;
        }

        private static Symbol BuildSymbolFromUserFriendlyValue(JsonObject jObject)
        {
            if (jObject.TryGetValue("value", StringComparison.InvariantCultureIgnoreCase, out var value)
                && jObject.TryGetValue("type", StringComparison.InvariantCultureIgnoreCase, out var securityTypeToken)
                && securityTypeToken.AsText().TryParseSecurityType(out var securityType))
            {
                var market = jObject.TryGetValue("market", StringComparison.InvariantCultureIgnoreCase, out var marketToken)
                    ? marketToken.AsText()
                    : Market.USA;
                return SymbolRepresentation.ParseTickerFromUserInput(value.AsText(), securityType, market);
            }
            return null;
        }
    }
}
