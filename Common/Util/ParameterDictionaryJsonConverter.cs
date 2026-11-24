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
using QuantConnect.Api;
using System.Text.Json;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QuantConnect.Util
{
    /// <summary>
    /// Parameter dictionary json converter, accepts both a dictionary and a list of parameters
    /// </summary>
    public class ParameterDictionaryJsonConverter : JsonConverter<Dictionary<string, string>>
    {
        /// <summary>
        /// Null tokens are handled by the converter
        /// </summary>
        public override bool HandleNull => true;

        /// <summary>
        /// Reads the JSON representation of the object
        /// </summary>
        public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return Read(ref reader, options);
        }

        /// <summary>
        /// Writes the dictionary using the default serialization
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }
            JsonSerializer.Serialize(writer, value, options);
        }

        internal static Dictionary<string, string> Read(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.StartObject:
                    // {name: value, ...}
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
                case JsonTokenType.StartArray:
                    // [{"key": key, "value": value}, ...], the optimization properties (min, max, step) don't apply here
                    var parameters = new Dictionary<string, string>();
                    foreach (var parameter in JsonSerializer.Deserialize<List<Parameter>>(ref reader, options))
                    {
                        if (!string.IsNullOrEmpty(parameter?.Name))
                        {
                            parameters[parameter.Name] = parameter.Value;
                        }
                    }
                    return parameters;
                default:
                    throw new ArgumentException($"Unexpected Tokentype {reader.TokenType}");
            }
        }
    }
}
