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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Optimizer.Parameters
{
    /// <summary>
    /// Override <see cref="OptimizationParameter"/> deserialization method.
    /// Can handle <see cref="OptimizationStepParameter"/> instances
    /// </summary>
    public class OptimizationParameterJsonConverter : JsonConverter<OptimizationParameter>
    {
        /// <summary>
        /// Null tokens are handled by the converter
        /// </summary>
        public override bool HandleNull => true;

        /// <summary>
        /// Writes the JSON representation of the object.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, OptimizationParameter value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartObject();
            foreach (var prop in value.GetType().GetProperties())
            {
                if (prop.CanRead)
                {
                    var attribute = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                    var propVal = prop.GetValue(value, null);
                    if (propVal != null)
                    {
                        writer.WritePropertyName(attribute?.Name ?? prop.Name);
                        JsonSerializer.Serialize(writer, propVal, propVal.GetType(), options);
                    }
                }
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override OptimizationParameter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parameter = JsonSerializer.Deserialize<OptimizationParameterProperties>(ref reader, options);
            var parameterName = parameter?.Name ?? parameter?.Key;
            if (string.IsNullOrEmpty(parameterName))
            {
                throw new ArgumentException(Messages.OptimizationParameterJsonConverter.OptimizationParameterNotSpecified);
            }

            OptimizationParameter optimizationParameter = null;
            if (parameter.Min.HasValue && parameter.Max.HasValue)
            {
                var min = parameter.Min.Value;
                var max = parameter.Max.Value;
                var minStep = parameter.MinStep ?? parameter.MinStepDashed;
                if (parameter.Step.HasValue)
                {
                    if (minStep.HasValue)
                    {
                        optimizationParameter = new OptimizationStepParameter(parameterName, min, max, parameter.Step.Value, minStep.Value);
                    }
                    else
                    {
                        optimizationParameter = new OptimizationStepParameter(parameterName, min, max, parameter.Step.Value);
                    }
                }
                else
                {
                    optimizationParameter = new OptimizationStepParameter(parameterName, min, max);
                }
            }
            else if (parameter.HasValue)
            {
                optimizationParameter = new StaticOptimizationParameter(parameterName, parameter.Value);
            }

            if (optimizationParameter == null)
            {
                throw new ArgumentException(Messages.OptimizationParameterJsonConverter.OptimizationParameterNotSupported);
            }

            return optimizationParameter;
        }

        private class OptimizationParameterProperties
        {
            private string _value;

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("key")]
            public string Key { get; set; }

            [JsonPropertyName("min")]
            public decimal? Min { get; set; }

            [JsonPropertyName("max")]
            public decimal? Max { get; set; }

            [JsonPropertyName("step")]
            public decimal? Step { get; set; }

            [JsonPropertyName("minStep")]
            public decimal? MinStep { get; set; }

            [JsonPropertyName("min-step")]
            public decimal? MinStepDashed { get; set; }

            [JsonPropertyName("value")]
            public string Value
            {
                get { return _value; }
                set
                {
                    _value = value;
                    HasValue = true;
                }
            }

            [JsonIgnore]
            public bool HasValue { get; private set; }
        }
    }
}
