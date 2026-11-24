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
using QuantConnect.Util.Json;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QuantConnect.Api.Serialization
{
    /// <summary>
    /// Provides an implementation of <see cref="JsonConverter"/> that can deserialize <see cref="Product"/>
    /// </summary>
    public class ProductJsonConverter : JsonConverter<Product>
    {
        private Dictionary<string, ProductType> _productTypeMap = new Dictionary<string, ProductType>()
        {
            {"Professional Seats", ProductType.ProfessionalSeats},
            {"Backtest Node", ProductType.BacktestNode},
            {"Research Node", ProductType.ResearchNode},
            {"Live Trading Node", ProductType.LiveNode},
            {"Support", ProductType.Support},
            {"Data", ProductType.Data},
            {"Modules", ProductType.Modules}
        };

        /// <summary>
        /// Writes the product using its default serialization
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Product value, JsonSerializerOptions options)
        {
            writer.WriteDefault(value, options);
        }

        /// <summary>
        /// Reads the JSON representation of the object
        /// </summary>
        public override Product Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return CreateProductFromJObject(JsonNode.Parse(ref reader) as JsonObject, options);
        }

        /// <summary>
        /// Create a product from a json object
        /// </summary>
        /// <param name="jObject">The json object</param>
        /// <param name="options">The serializer options</param>
        /// <returns>Product Object</returns>
        public Product CreateProductFromJObject(JsonObject jObject, JsonSerializerOptions options = null)
        {
            if (jObject == null)
            {
                return null;
            }

            var productTypeName = jObject["name"].Value<string>();
            if (!_productTypeMap.ContainsKey(productTypeName))
            {
                return null;
            }

            return new Product
            {
                Type = _productTypeMap[productTypeName],
                Items = jObject["items"].ToObject<List<ProductItem>>(options)
            };
        }
    }
}
