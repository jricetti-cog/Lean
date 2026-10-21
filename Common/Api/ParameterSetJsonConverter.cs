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
using System.Text;
using QuantConnect.Util;
using System.Text.Json;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using QuantConnect.Optimizer.Parameters;

namespace QuantConnect.Api
{
    /// <summary>
    /// Json converter for <see cref="ParameterSet"/> which creates a light weight easy to consume serialized version
    /// </summary>
    public class ParameterSetJsonConverter : JsonConverter<ParameterSet>
    {
        /// <summary>
        /// Writes a JSON object from a Parameter set
        /// </summary>
        public override void Write(Utf8JsonWriter writer, ParameterSet value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value.Value != null)
            {
                writer.WritePropertyName("parameterSet");
                JsonSerializer.Serialize(writer, value.Value, options);
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads the JSON representation of the object
        /// </summary>
        public override ParameterSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                // {"parameterSet": parameters} or {name: value, ...}
                var parameterSet = new Dictionary<string, string>();
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "parameterSet")
                    {
                        parameterSet = ParameterDictionaryJsonConverter.Read(ref reader, options);
                    }
                    else
                    {
                        parameterSet[name] = ReadAsString(ref reader);
                    }
                }
                return new ParameterSet(-1, parameterSet);
            }

            var parameters = ParameterDictionaryJsonConverter.Read(ref reader, options);
            return parameters == null ? null : new ParameterSet(-1, parameters);
        }

        private static string ReadAsString(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                case JsonTokenType.Number:
                    return Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan);
                default:
                    throw new JsonException($"Unexpected token parsing string. Unexpected token: {reader.TokenType}.");
            }
        }
    }
}
