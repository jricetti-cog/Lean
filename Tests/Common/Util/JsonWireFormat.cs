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
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using QuantConnect.Api;
using QuantConnect.Orders;
using QuantConnect.Packets;

namespace QuantConnect.Tests.Common.Util
{
    /// <summary>
    /// The serializer used by <see cref="JsonWireFormatTests"/>
    /// </summary>
    public static class JsonWireFormat
    {
        private static readonly JsonSerializerSettings CamelCaseSettings = new()
        {
            Converters = { new LiveAlgorithmResultsJsonConverter(), new OrderJsonConverter() },
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = true
                }
            }
        };

        private static readonly JsonSerializerSettings DefaultSettings = new()
        {
            Converters = { new OrderJsonConverter() }
        };

        public static string Serialize(object value, bool camelCase)
        {
            return JsonConvert.SerializeObject(value, camelCase ? CamelCaseSettings : null);
        }

        public static object Deserialize(string json, Type type)
        {
            return JsonConvert.DeserializeObject(json, type, DefaultSettings);
        }
    }
}
