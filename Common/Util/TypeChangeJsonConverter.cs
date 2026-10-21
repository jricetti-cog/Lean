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
using System.Text.Json.Serialization;

namespace QuantConnect.Util
{
    /// <summary>
    /// Provides a base class for a <see cref="JsonConverter"/> that serializes a
    /// an input type as some other output type
    /// </summary>
    /// <typeparam name="T">The type to be serialized</typeparam>
    /// <typeparam name="TResult">The output serialized type</typeparam>
    public abstract class TypeChangeJsonConverter<T, TResult> : JsonConverter<T>
    {
        /// <summary>
        /// True will populate TResult object returned by <see cref="Convert(TResult)"/> with json properties
        /// </summary>
        protected virtual bool PopulateProperties => true;

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return Create(typeToConvert, document.RootElement, options);
        }

        /// <summary>
        /// Writes the JSON representation of the object.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var valueToSerialize = Convert(value);
            JsonSerializer.Serialize(writer, valueToSerialize, valueToSerialize?.GetType() ?? typeof(TResult), options);
        }

        /// <summary>
        /// Creates an instance of the un-projected type to be deserialized
        /// </summary>
        /// <param name="type">The input object type, this is the data held in the token</param>
        /// <param name="token">The input data to be converted into a T</param>
        /// <param name="options">The serializer options</param>
        /// <returns>A new instance of T that is to be serialized using default rules</returns>
        protected virtual T Create(Type type, JsonElement token, JsonSerializerOptions options)
        {
            return Convert(token.Deserialize<TResult>(options));
        }

        /// <summary>
        /// Convert the input value to a value to be serialized
        /// </summary>
        /// <param name="value">The input value to be converted before serialziation</param>
        /// <returns>A new instance of TResult that is to be serialzied</returns>
        protected abstract TResult Convert(T value);

        /// <summary>
        /// Converts the input value to be deserialized
        /// </summary>
        /// <param name="value">The deserialized value that needs to be converted to T</param>
        /// <returns>The converted value</returns>
        protected abstract T Convert(TResult value);
    }
}
