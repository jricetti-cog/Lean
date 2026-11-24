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
using QuantConnect.Util.Json;

namespace QuantConnect
{
    /// <summary>
    /// Helper converter used to restore the default serialization of a type, ignoring any converter inherited from its base type
    /// </summary>
    public class DefaultConverter : JsonConverterFactory
    {
        /// <summary>
        /// Determines whether this instance can convert the specified object type
        /// </summary>
        public override bool CanConvert(Type typeToConvert)
        {
            return true;
        }

        /// <summary>
        /// Creates the default contract converter for the given type
        /// </summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)Activator.CreateInstance(typeof(DefaultContractConverter<>).MakeGenericType(typeToConvert));
        }

        private class DefaultContractConverter<T> : JsonConverter<T>
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return (T)LeanJsonContract.ReadDefault(ref reader, typeToConvert, options);
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                writer.WriteDefault(value, options);
            }
        }
    }
}
