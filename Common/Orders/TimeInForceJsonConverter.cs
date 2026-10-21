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
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Util.Json;

namespace QuantConnect.Orders
{
    /// <summary>
    /// Provides an implementation of <see cref="JsonConverter"/> that can deserialize TimeInForce objects
    /// </summary>
    public class TimeInForceJsonConverter : JsonConverter<TimeInForce>
    {
        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        public override bool CanConvert(Type typeToConvert)
        {
            return typeof(TimeInForce).IsAssignableFrom(typeToConvert);
        }

        /// <summary>
        /// Writes the JSON representation of the object.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, TimeInForce value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            writer.WriteStartObject();
            // don't add if its the default value used by the reader
            if (type != typeof(GoodTilCanceledTimeInForce))
            {
                writer.WriteString("$type", type.FullName);
            }

            foreach (var property in type.GetProperties())
            {
                if (property.CanRead)
                {
                    var propertyValue = property.GetValue(value, null);
                    if (propertyValue != null)
                    {
                        writer.WritePropertyName(property.Name);
                        JsonSerializer.Serialize(writer, propertyValue, propertyValue.GetType(), options);
                    }
                }
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override TimeInForce Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jObject = JsonNode.Parse(ref reader);

            Type type;
            if (jObject is JsonArray array)
            {
                if (array.Count != 0)
                {
                    throw new InvalidOperationException($"Unexpected time in force value: {jObject.ToJsonString()}");
                }
                // default value if not present. for php [] & {} are the same representation of empty object
                type = typeof(GoodTilCanceledTimeInForce);
            }
            else if (jObject["$type"] != null)
            {
                var typeName = jObject["$type"].AsText();
                type = Type.GetType(typeName, throwOnError: false, ignoreCase: true);
                if (type == null || !typeof(TimeInForce).IsAssignableFrom(type))
                {
                    throw new InvalidOperationException($"Unable to find the type: {typeName}");
                }
            }
            else
            {
                // default value if not present
                type = typeof(GoodTilCanceledTimeInForce);
            }

            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[0], null);
            if (constructor == null)
            {
                throw new NotImplementedException($"Unable to find a constructor for type: {type.FullName}");
            }

            var timeInForce = (TimeInForce)constructor.Invoke(null);

            foreach (var property in timeInForce.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = jObject[property.Name];
                if (value != null)
                {
                    property.SetValue(timeInForce, value.Deserialize(property.PropertyType, JsonSerialization.Default));
                }
            }

            return timeInForce;
        }
    }
}
