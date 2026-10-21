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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Helpers to read <see cref="JsonNode"/> values leniently, the way Newtonsoft.Json tokens were read
    /// </summary>
    public static class JsonNodeExtensions
    {
        /// <summary>
        /// Converts the node value into the requested type, numbers given as strings and vice versa are accepted.
        /// Returns the default value for missing or null nodes
        /// </summary>
        public static T Value<T>(this JsonNode node)
        {
            if (node == null)
            {
                return default;
            }
            return node.Deserialize<T>(JsonSerialization.Default);
        }

        /// <summary>
        /// Converts the array items into the requested type
        /// </summary>
        public static IEnumerable<T> Values<T>(this JsonNode node)
        {
            if (node is not JsonArray array)
            {
                return Enumerable.Empty<T>();
            }
            return array.Select(x => x.Value<T>());
        }

        /// <summary>
        /// Gets the json value kind of the node, <see cref="JsonValueKind.Null"/> for null nodes
        /// </summary>
        public static JsonValueKind Kind(this JsonNode node)
        {
            return node?.GetValueKind() ?? JsonValueKind.Null;
        }

        /// <summary>
        /// True if the node is missing or a json null
        /// </summary>
        public static bool IsNull(this JsonNode node)
        {
            return node == null || node.GetValueKind() == JsonValueKind.Null;
        }

        /// <summary>
        /// True if the node is a floating point number
        /// </summary>
        public static bool IsFloat(this JsonNode node)
        {
            return node is JsonValue && node.GetValueKind() == JsonValueKind.Number && node.ToJsonString().AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
        }

        /// <summary>
        /// Gets the node at the given dotted path, null if not found
        /// </summary>
        public static JsonNode SelectToken(this JsonNode node, string path)
        {
            foreach (var part in path.Split('.'))
            {
                if (node is not JsonObject jsonObject || !jsonObject.TryGetPropertyValue(part, out node))
                {
                    return null;
                }
            }
            return node;
        }

        /// <summary>
        /// Gets the first property value found with any of the given names
        /// </summary>
        public static JsonNode Get(this JsonNode node, params string[] names)
        {
            if (node is not JsonObject jsonObject)
            {
                return null;
            }
            foreach (var name in names)
            {
                if (jsonObject.TryGetPropertyValue(name, out var value))
                {
                    return value;
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the property value matching the name, an exact match is preferred over one using the given comparison
        /// </summary>
        public static bool TryGetValue(this JsonObject jsonObject, string name, StringComparison comparison, out JsonNode value)
        {
            if (jsonObject.TryGetPropertyValue(name, out value))
            {
                return true;
            }
            foreach (var property in jsonObject)
            {
                if (string.Equals(property.Key, name, comparison))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        /// <summary>
        /// Gets the property value matching the name, null if not found
        /// </summary>
        public static JsonNode GetValue(this JsonObject jsonObject, string name, StringComparison comparison)
        {
            return jsonObject.TryGetValue(name, comparison, out var value) ? value : null;
        }

        /// <summary>
        /// Gets the text of the node like Newtonsoft.Json tokens did: strings are returned as is, null values as an empty string
        /// and objects and arrays as indented json
        /// </summary>
        public static string AsText(this JsonNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }
            return node.GetValueKind() switch
            {
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => string.Empty,
                _ => node.ToString()
            };
        }

        /// <summary>
        /// Reads a decimal value, floating point numbers are read as doubles first like Newtonsoft.Json tokens did
        /// </summary>
        public static decimal SafeDecimalValue(this JsonNode node)
        {
            if (node.IsFloat())
            {
                return node.GetValue<double>().SafeDecimalCast();
            }
            return node.Value<decimal>();
        }
    
        /// <summary>
        /// Reads a nullable decimal value, floating point numbers are read as doubles first like Newtonsoft.Json tokens did
        /// </summary>
        public static decimal? SafeNullableDecimalValue(this JsonNode node)
        {
            return node.IsNull() ? null : node.SafeDecimalValue();
        }
    }
}
