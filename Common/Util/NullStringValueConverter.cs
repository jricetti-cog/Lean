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
using System.Text.Json.Serialization;

namespace QuantConnect.Util
{
    /// <summary>
    /// Converts the string "null" into a new instance of T.
    /// This converter only handles deserialization of JSON into an object of type T.
    /// </summary>
    public class NullStringValueConverter<T> : JsonConverter<T>
        where T : new()
    {
        /// <summary>
        /// Null tokens also produce a new instance
        /// </summary>
        public override bool HandleNull => true;

        /// <summary>
        /// Writing is not supported
        /// </summary>
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Converts "null" (string) or null tokens into a new instance of T, deserializing T otherwise
        /// </summary>
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null || (reader.TokenType == JsonTokenType.String && reader.GetString() == "null"))
            {
                return new T();
            }
            return JsonSerializer.Deserialize<T>(ref reader, options);
        }
    }
}
