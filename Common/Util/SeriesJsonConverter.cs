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
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using QuantConnect.Util.Json;

namespace QuantConnect.Util
{
    /// <summary>
    /// Json Converter for Series which handles special Pie Series serialization case
    /// </summary>
    public class SeriesJsonConverter : JsonConverter<BaseSeries>
    {
        private static readonly ColorJsonConverter ColorJsonConverter = new();

        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        public override bool CanConvert(Type objectType)
        {
            return typeof(BaseSeries).IsAssignableFrom(objectType);
        }

        /// <summary>
        /// Write Series to Json
        /// </summary>
        public override void Write(Utf8JsonWriter writer, BaseSeries baseSeries, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", baseSeries.Name);
            writer.WriteString("unit", baseSeries.Unit);
            writer.WriteNumber("index", baseSeries.Index);
            writer.WriteNumber("seriesType", (int)baseSeries.SeriesType);

            if (baseSeries.ZIndex.HasValue)
            {
                writer.WriteNumber("zIndex", baseSeries.ZIndex.Value);
            }
            if (baseSeries.IndexName != null)
            {
                writer.WriteString("indexName", baseSeries.IndexName);
            }
            if (baseSeries.Tooltip != null)
            {
                writer.WriteString("tooltip", baseSeries.Tooltip);
            }

            switch (baseSeries)
            {
                case Series series:
                    var values = series.Values;
                    if (series.SeriesType == SeriesType.Pie)
                    {
                        values = new List<ISeriesPoint>();
                        var dataPoint = series.ConsolidateChartPoints();
                        if (dataPoint != null)
                        {
                            values.Add(dataPoint);
                        }
                    }

                    writer.WritePropertyName("values");
                    WriteValues(writer, values, options);
                    writer.WritePropertyName("color");
                    ColorJsonConverter.Write(writer, series.Color, options);
                    writer.WritePropertyName("scatterMarkerSymbol");
                    JsonSerializer.Serialize(writer, series.ScatterMarkerSymbol, options);
                    break;

                default:
                    writer.WritePropertyName("values");
                    WriteValues(writer, baseSeries.Values, options);
                    break;
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads series from Json
        /// </summary>
        public override BaseSeries Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jObject = JsonNode.Parse(ref reader).AsObject();

            var name = (jObject["Name"] ?? jObject["name"]).Value<string>();
            var unit = (jObject["Unit"] ?? jObject["unit"]).Value<string>();
            var index = (jObject["Index"] ?? jObject["index"]).Value<int>();
            var seriesType = (SeriesType)(jObject["SeriesType"] ?? jObject["seriesType"]).Value<int>();
            var values = (jObject["Values"] ?? jObject["values"]) as JsonArray;

            var zindex = jObject["ZIndex"]?.Value<int?>() ?? jObject["zIndex"]?.Value<int?>();
            var indexName = jObject["IndexName"]?.Value<string>() ?? jObject["indexName"]?.Value<string>();
            var tooltip = jObject["Tooltip"]?.Value<string>() ?? jObject["tooltip"]?.Value<string>();
            if (seriesType == SeriesType.Candle)
            {
                return new CandlestickSeries()
                {
                    Name = name,
                    Unit = unit,
                    Index = index,
                    ZIndex = zindex,
                    Tooltip = tooltip,
                    IndexName = indexName,
                    SeriesType = seriesType,
                    Values = values.Deserialize<List<Candlestick>>(options).Where(x => x != null).Cast<ISeriesPoint>().ToList()
                };
            }

            var color = jObject["Color"] ?? jObject["color"];
            var scatterMarkerSymbol = jObject["ScatterMarkerSymbol"] ?? jObject["scatterMarkerSymbol"];
            var result = new Series()
            {
                Name = name,
                Unit = unit,
                Index = index,
                ZIndex = zindex,
                Tooltip = tooltip,
                IndexName = indexName,
                SeriesType = seriesType,
                Color = color.IsNull() ? Color.Empty : ColorJsonConverter.FromString(color.Value<string>()),
                ScatterMarkerSymbol = scatterMarkerSymbol.IsNull() ? ScatterMarkerSymbol.None : scatterMarkerSymbol.Deserialize<ScatterMarkerSymbol>(options)
            };

            if (seriesType == SeriesType.Scatter)
            {
                result.Values = values.Deserialize<List<ScatterChartPoint>>(options).Where(x => x != null).Cast<ISeriesPoint>().ToList();
            }
            else
            {
                result.Values = values.Deserialize<List<ChartPoint>>(options).Where(x => x != null).Cast<ISeriesPoint>().ToList();
            }
            return result;
        }

        private static void WriteValues(Utf8JsonWriter writer, List<ISeriesPoint> values, JsonSerializerOptions options)
        {
            if (values == null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartArray();
            foreach (var value in values)
            {
                JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), options);
            }
            writer.WriteEndArray();
        }
    }
}
