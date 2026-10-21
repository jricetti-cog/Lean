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
using QuantConnect.Api;
using QuantConnect.Orders;

namespace QuantConnect.Tests.Common.Util
{
    /// <summary>
    /// The serializer used by <see cref="JsonWireFormatTests"/>
    /// </summary>
    public static class JsonWireFormat
    {
        private static readonly JsonSerializerOptions CamelCaseOptions = JsonSerialization.Create(camelCase: true,
            converters: new System.Text.Json.Serialization.JsonConverter[] { new LiveAlgorithmResultsJsonConverter(), new OrderJsonConverter() });

        /// <summary>
        /// Serializes the value the way LEAN does
        /// </summary>
        public static string Serialize(object value, bool camelCase)
        {
            return JsonSerialization.Serialize(value, camelCase ? CamelCaseOptions : JsonSerialization.Default);
        }

        /// <summary>
        /// Deserializes the json the way LEAN does
        /// </summary>
        public static object Deserialize(string json, Type type)
        {
            return JsonSerialization.Deserialize(json, type);
        }
    }
}
