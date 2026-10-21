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

namespace QuantConnect.Util
{
    /// <summary>
    /// Json Converter for ChartPoint which handles special reading
    /// </summary>
    public class ChartPointJsonConverter : JsonConverter<ChartPoint>
    {
        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override ChartPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var jObject = JsonNode.Parse(ref reader).AsObject();
                var x = jObject["x"];
                if (!jObject.ContainsKey("y"))
                {
                    return new ChartPoint(x.Value<long>(), 0);
                }

                var y = jObject["y"];
                if (y.IsNull())
                {
                    return new ChartPoint(x.Value<long>(), null);
                }
                if (y.Kind() == JsonValueKind.Number)
                {
                    return new ChartPoint(x.Value<long>(), y.SafeDecimalValue());
                }
                return null;
            }

            var jArray = JsonNode.Parse(ref reader).AsArray();
            var value = jArray[1];
            return new ChartPoint(jArray[0].Value<long>(), value.IsNull() ? null : value.SafeDecimalValue());
        }

        /// <summary>
        /// Writes a JSON object from a Chart Point
        /// </summary>
        public override void Write(Utf8JsonWriter writer, ChartPoint chartPoint, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(chartPoint.X);
            JsonSerializer.Serialize(writer, chartPoint.Y, options);
            writer.WriteEndArray();
        }
    }
}
