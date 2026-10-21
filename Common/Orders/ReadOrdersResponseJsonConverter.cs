/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2024 QuantConnect Corporation.
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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Orders.Serialization;
using QuantConnect.Util.Json;

namespace QuantConnect.Orders
{
    /// <summary>
    /// Api order and order events reponse json converter
    /// </summary>
    public class ReadOrdersResponseJsonConverter : JsonConverter<ApiOrderResponse>
    {
        /// <summary>
        /// Writes the JSON representation of the object.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, ApiOrderResponse orderResponse, JsonSerializerOptions options)
        {
            var jObject = JsonSerialization.FromObject(orderResponse.Order).AsObject();
            jObject["symbol"] = JsonSerialization.FromObject(orderResponse.Symbol);
            jObject["events"] = JsonSerialization.FromObject(orderResponse.Events);
            jObject.WriteTo(writer);
        }

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override ApiOrderResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jObject = JsonNode.Parse(ref reader).AsObject();

            var order = OrderJsonConverter.CreateOrderFromJObject(jObject);

            var events = jObject["Events"] ?? jObject["events"];
            List<SerializedOrderEvent> deserializedEvents = null;
            if (events != null)
            {
                deserializedEvents = events.Value<List<SerializedOrderEvent>>();
            }

            var symbol = jObject["Symbol"] ?? jObject["symbol"];
            Symbol deserializedSymbol = null;
            if (symbol != null)
            {
                deserializedSymbol = symbol.Value<Symbol>();
            }

            return new ApiOrderResponse(order, deserializedEvents ?? new(), deserializedSymbol);
        }
    }
}
