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
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;

namespace QuantConnect.Report
{
    /// <summary>
    /// Removes null values in the <see cref="Result"/> object's x,y values so that
    /// deserialization can occur without exceptions.
    /// </summary>
    /// <typeparam name="T">Result type to deserialize into</typeparam>
    public class NullResultValueTypeJsonConverter<T> : JsonConverter<T>
        where T : Result
    {
        private readonly JsonSerializerOptions _settings;

        /// <summary>
        /// Initialize a new instance of <see cref="NullResultValueTypeJsonConverter{T}"/>
        /// </summary>
        public NullResultValueTypeJsonConverter()
        {
            _settings = JsonSerialization.Create(floatAsDecimal: true, converters: new OrderTypeNormalizingJsonConverter());
        }

        /// <summary>
        /// Determine if this converter can convert a given type
        /// </summary>
        public override bool CanConvert(Type objectType)
        {
            return objectType.IsAssignableTo(typeof(T));
        }

        /// <summary>
        /// Read Json and convert
        /// </summary>
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var token = JsonNode.Parse(ref reader);
            if (token == null)
            {
                return null;
            }

            if (GetProperty(token, "Charts") is JsonObject charts)
            {
                foreach (var property in charts)
                {
                    if (GetProperty(property.Value, "Series") is not JsonObject seriesCollection)
                    {
                        continue;
                    }
                    foreach (var seriesProperty in seriesCollection)
                    {
                        var series = seriesProperty.Value;
                        if (GetProperty(series, "Values") is not JsonArray values)
                        {
                            continue;
                        }

                        var newValues = new JsonArray();
                        foreach (var entry in values)
                        {
                            if (entry is JsonObject jobj &&
                                (jobj["x"].IsNull() || jobj["y"].IsNull()))
                            {
                                // null chart point
                                continue;
                            }

                            if (entry is JsonArray jArray && jArray.Any(jToken => jToken.IsNull()))
                            {
                                // null candlestick
                                continue;
                            }

                            newValues.Add(entry?.DeepClone());
                        }

                        if (series["Values"] != null)
                        {
                            series["Values"] = newValues;
                        }
                        else if (series["values"] != null)
                        {
                            series["values"] = newValues;
                        }
                    }
                }
            }

            // Deserialize with OrderTypeNormalizingJsonConverter, otherwise it will fail
            return token.Deserialize<T>(_settings);
        }

        /// <summary>
        /// Write Json; Not implemented
        /// </summary>
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            throw new NotImplementedException();
        }

        private static JsonNode GetProperty(JsonNode jToken, string name)
        {
            if (jToken is not JsonObject jObject)
            {
                return null;
            }
            return jObject[name] ?? jObject[name.ToLowerInvariant()];
        }
    }
}
