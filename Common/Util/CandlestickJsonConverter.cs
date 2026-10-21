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
    /// Candlestick Json Converter
    /// </summary>
    public class CandlestickJsonConverter : JsonConverter<Candlestick>
    {
        /// <summary>
        /// Write Series to Json
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Candlestick candlestick, JsonSerializerOptions options)
        {
            // Candlesticks will be written as a single array of 5 values: [time, open, high, low, close]
            writer.WriteStartArray();
            writer.WriteNumberValue(candlestick.LongTime);
            JsonSerializer.Serialize(writer, candlestick.Open, options);
            JsonSerializer.Serialize(writer, candlestick.High, options);
            JsonSerializer.Serialize(writer, candlestick.Low, options);
            JsonSerializer.Serialize(writer, candlestick.Close, options);
            writer.WriteEndArray();
        }

        /// <summary>
        /// Json reader implementation which handles backwards compatiblity for old equity chart points
        /// </summary>
        public override Candlestick Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var chartPoint = JsonSerializer.Deserialize<ChartPoint>(ref reader, options);
                if (chartPoint == null)
                {
                    return null;
                }
                return new Candlestick(chartPoint.X, chartPoint.Y, chartPoint.Y, chartPoint.Y, chartPoint.Y);
            }

            var jArray = JsonNode.Parse(ref reader).AsArray();
            if (jArray.Count <= 2)
            {
                var chartPoint = jArray.Deserialize<ChartPoint>(options);
                if (chartPoint == null)
                {
                    return null;
                }
                return new Candlestick(chartPoint.X, chartPoint.Y, chartPoint.Y, chartPoint.Y, chartPoint.Y);
            }
            return new Candlestick(jArray[0].Value<long>(), jArray[1].SafeNullableDecimalValue(), jArray[2].SafeNullableDecimalValue(),
                jArray[3].SafeNullableDecimalValue(), jArray[4].SafeNullableDecimalValue());
        }
    }
}
