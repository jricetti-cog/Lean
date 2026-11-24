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
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace QuantConnect
{
    /// <summary>
    /// Convert a Chart Series to and from JSON
    /// </summary>
    public class ChartSeriesJsonConverter : JsonConverter<Dictionary<string, BaseSeries>>
    {
        /// <summary>
        /// Writes the series sorted
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Dictionary<string, BaseSeries> series, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            // we sort the series in ascending count so that they are chart nicely, has value for stacked area series so they're continuous
            foreach (var kvp in series.OrderBy(x => x.Value.Index)
                .ThenBy(x => x.Value.Values.Count)
                .ThenBy(x => x.Value.Values.Select(x => (x as ChartPoint)?.Y ?? 0).Sum()))
            {
                writer.WritePropertyName(kvp.Key);
                JsonSerializer.Serialize(writer, kvp.Value, kvp.Value?.GetType() ?? typeof(BaseSeries), JsonSerialization.Default);
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads the series dictionary
        /// </summary>
        public override Dictionary<string, BaseSeries> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} when reading chart series.");
            }
            var result = new Dictionary<string, BaseSeries>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString();
                reader.Read();
                result[key] = JsonSerializer.Deserialize<BaseSeries>(ref reader, options);
            }
            return result;
        }
    }
}
