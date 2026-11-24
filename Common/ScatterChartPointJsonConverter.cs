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
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;

namespace QuantConnect
{
    /// <summary>
    /// ScatterChartPoint json converter
    /// </summary>
    public class ScatterChartPointJsonConverter : JsonConverter<ScatterChartPoint>
    {
        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override ScatterChartPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var jObject = JsonNode.Parse(ref reader).AsObject();
                var tooltip = jObject["tooltip"]?.Value<string>();
                return new ScatterChartPoint(jObject["x"].Value<long>(), jObject["y"].SafeNullableDecimalValue(), tooltip);
            }

            var jArray = JsonNode.Parse(ref reader).AsArray();
            return new ScatterChartPoint(jArray[0].Value<long>(), jArray[1].SafeNullableDecimalValue());
        }

        /// <summary>
        /// Writes the point using its default serialization
        /// </summary>
        public override void Write(Utf8JsonWriter writer, ScatterChartPoint value, JsonSerializerOptions options)
        {
            writer.WriteDefault(value, options);
        }
    }
}
