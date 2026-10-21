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
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Util
{
    /// <summary>
    /// Reads json and always produces a List, even if the input has just an object
    /// </summary>
    public class SingleValueListConverter<T> : JsonConverter<List<T>>
    {
        /// <summary>
        /// Writes the list to json
        /// </summary>
        public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }

        /// <summary>
        /// Reads the JSON representation of the object, a single value or an array of values
        /// </summary>
        public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                case JsonTokenType.StartObject:
                    return new List<T> { JsonSerializer.Deserialize<T>(ref reader, options) };
                case JsonTokenType.StartArray:
                    return JsonSerializer.Deserialize<List<T>>(ref reader, options);
                default:
                    throw new ArgumentException("The JsonReader is expected to point at a JsonToken.StartObject or JsonToken.StartArray.");
            }
        }
    }
}
