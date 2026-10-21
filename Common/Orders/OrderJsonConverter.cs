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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;
using System.Collections.Generic;
using QuantConnect.Brokerages;
using QuantConnect.Securities;

namespace QuantConnect.Orders
{
    /// <summary>
    /// Provides an implementation of <see cref="JsonConverter"/> that can deserialize Orders
    /// </summary>
    public class OrderJsonConverter : JsonConverter<Order>
    {
        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        public override bool CanConvert(Type typeToConvert)
        {
            return typeof(Order).IsAssignableFrom(typeToConvert);
        }

        /// <summary>
        /// Writes the order using the default serialization of its runtime type
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Order value, JsonSerializerOptions options)
        {
            writer.WriteDefault(value, options);
        }

        /// <summary>
        /// Reads the JSON representation of the order
        /// </summary>
        public override Order Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jObject = JsonNode.Parse(ref reader) as JsonObject;
            return CreateOrderFromJObject(jObject);
        }

        /// <summary>
        /// Create an order from a simple JObject
        /// </summary>
        /// <param name="jObject"></param>
        /// <returns>Order Object</returns>
        public static Order CreateOrderFromJObject(JsonObject jObject)
        {
            // create order instance based on order type field
            var orderType = (OrderType)(jObject["Type"]?.Value<int>() ?? jObject["type"].Value<int>());
            var order = CreateOrder(orderType, jObject);

            // populate common order properties
            order.Id = jObject["Id"]?.Value<int>() ?? jObject["id"].Value<int>();

            var jsonStatus = jObject["Status"] ?? jObject["status"];
            var jsonTime = jObject["Time"] ?? jObject["time"];
            if (jsonStatus.Kind() == JsonValueKind.Number)
            {
                order.Status = (OrderStatus)jsonStatus.Value<int>();
            }
            else if (jsonStatus.IsNull())
            {
                order.Status = OrderStatus.Canceled;
            }
            else
            {
                // The `Status` tag can sometimes appear as a string of the enum value in the LiveResultPacket.
                order.Status = (OrderStatus)Enum.Parse(typeof(OrderStatus), jsonStatus.Value<string>(), true);
            }
            if (!jsonTime.IsNull())
            {
                order.Time = jsonTime.Value<DateTime>();
            }
            else
            {
                // `Time` can potentially be null in some LiveResultPacket instances, but
                // `CreatedTime` will always be there if `Time` is absent.
                order.Time = (jObject["CreatedTime"]?.Value<DateTime>() ?? jObject["createdTime"].Value<DateTime>());
            }

            var orderSubmissionData = jObject["OrderSubmissionData"] ?? jObject["orderSubmissionData"];
            if (!orderSubmissionData.IsNull())
            {
                var bidPrice = SafeDecimalValue(orderSubmissionData["BidPrice"] ?? orderSubmissionData["bidPrice"]);
                var askPrice = SafeDecimalValue(orderSubmissionData["AskPrice"] ?? orderSubmissionData["askPrice"]);
                var lastPrice = SafeDecimalValue(orderSubmissionData["LastPrice"] ?? orderSubmissionData["lastPrice"]);
                order.OrderSubmissionData = new OrderSubmissionData(bidPrice, askPrice, lastPrice);
            }

            var priceAdjustmentMode = jObject["PriceAdjustmentMode"] ?? jObject["priceAdjustmentMode"];
            if (!priceAdjustmentMode.IsNull())
            {
                var value = priceAdjustmentMode.Value<int>();
                order.PriceAdjustmentMode = (DataNormalizationMode)value;
            }

            var lastFillTime = jObject["LastFillTime"] ?? jObject["lastFillTime"];
            var lastUpdateTime = jObject["LastUpdateTime"] ?? jObject["lastUpdateTime"];
            var canceledTime = jObject["CanceledTime"] ?? jObject["canceledTime"];

            if (!canceledTime.IsNull())
            {
                order.CanceledTime = canceledTime.Value<DateTime>();
            }
            if (!lastFillTime.IsNull())
            {
                order.LastFillTime = lastFillTime.Value<DateTime>();
            }
            if (!lastUpdateTime.IsNull())
            {
                order.LastUpdateTime = lastUpdateTime.Value<DateTime>();
            }
            var tag = jObject["Tag"] ?? jObject["tag"];
            if (!tag.IsNull())
            {
                order.Tag = tag.Value<string>();
            }
            else
            {
                order.Tag = string.Empty;
            }

            order.Quantity = SafeDecimalValue(jObject["Quantity"] ?? jObject["quantity"]);
            var orderPrice = jObject["Price"] ?? jObject["price"];
            if (!orderPrice.IsNull())
            {
                order.Price = SafeDecimalValue(orderPrice);
            }
            else
            {
                order.Price = default(decimal);
            }

            var priceCurrency = jObject["PriceCurrency"] ?? jObject["priceCurrency"];
            if (!priceCurrency.IsNull())
            {
                order.PriceCurrency = priceCurrency.Value<string>();
            }
            order.BrokerId = jObject["BrokerId"]?.AsArray().Select(x => x.Value<string>()).ToList() ?? jObject["brokerId"].AsArray().Select(x => x.Value<string>()).ToList();
            var jsonContingentId = jObject["ContingentId"] ?? jObject["contingentId"];
            if (!jsonContingentId.IsNull())
            {
                order.ContingentId = jsonContingentId.Value<int>();
            }

            DeserializeContingency(jObject, order);

            var timeInForce = jObject["Properties"]?["TimeInForce"] ?? jObject["TimeInForce"] ?? jObject["Duration"];
            if (timeInForce == null)
            {
                timeInForce = jObject["properties"]?["timeInForce"] ?? jObject["timeInForce"] ?? jObject["duration"];
            }
            order.Properties.TimeInForce = (timeInForce != null)
                ? CreateTimeInForce(timeInForce, jObject)
                : TimeInForce.GoodTilCanceled;

            if (jObject.SelectToken("Symbol.ID") != null)
            {
                var sid = SecurityIdentifier.Parse(jObject.SelectToken("Symbol.ID").Value<string>());
                var ticker = jObject.SelectToken("Symbol.Value").Value<string>();
                order.Symbol = new Symbol(sid, ticker);
            }
            else if (jObject.SelectToken("symbol.id") != null)
            {
                var sid = SecurityIdentifier.Parse(jObject.SelectToken("symbol.id").Value<string>());
                var ticker = jObject.SelectToken("symbol.value").Value<string>();
                order.Symbol = new Symbol(sid, ticker);
            }
            else
            {
                string market = null;

                //does data have market?
                var suppliedMarket = jObject.SelectToken("Symbol.ID.Market");
                if (suppliedMarket != null)
                {
                    market = suppliedMarket.Value<string>();
                }

                // we only get the security type if we need it, because it might not be there in other cases
                var securityType = (SecurityType)(jObject["SecurityType"]?.Value<int>() ?? jObject["securityType"].Value<int>());

                var symbolValueUpperCase = jObject.SelectToken("Symbol.Value");
                var symbolValueCamelCase = jObject.SelectToken("symbol.value");
                string ticker = default;
                if (symbolValueUpperCase != null)
                {
                    // provide for backwards compatibility
                    ticker = symbolValueUpperCase.Value<string>();
                }
                else if (symbolValueCamelCase != null)
                {
                    // provide compatibility for orders in camel case
                    ticker = symbolValueCamelCase.Value<string>();
                }
                else
                {
                    ticker = jObject["Symbol"]?.Value<string>() ?? jObject["symbol"]?.Value<string>();
                }

                if (market == null && !SymbolPropertiesDatabase.FromDataFolder().TryGetMarket(ticker, securityType, out market))
                {
                    market = DefaultBrokerageModel.DefaultMarketMap[securityType];
                }
                order.Symbol = Symbol.Create(ticker, securityType, market);
            }

            return order;
        }

        /// <summary>
        /// Creates an order of the correct type
        /// </summary>
        private static Order CreateOrder(OrderType orderType, JsonObject jObject)
        {
            Order order;
            switch (orderType)
            {
                case OrderType.Market:
                    order = new MarketOrder();
                    break;

                case OrderType.Limit:
                    order = new LimitOrder { LimitPrice = SafeDecimalValueOrDefault(jObject["LimitPrice"] ?? jObject["limitPrice"]) };
                    break;

                case OrderType.StopMarket:
                    order = new StopMarketOrder
                    {
                        StopPrice = SafeDecimalValueOrDefault(jObject["stopPrice"] ?? jObject["StopPrice"])
                    };
                    break;

                case OrderType.StopLimit:
                    order = new StopLimitOrder
                    {
                        LimitPrice = SafeDecimalValueOrDefault(jObject["LimitPrice"] ?? jObject["limitPrice"]),
                        StopPrice = SafeDecimalValueOrDefault(jObject["stopPrice"] ?? jObject["StopPrice"]),
                        StopTriggered = jObject["StopTriggered"]?.Value<bool>() ?? jObject["stopTriggered"]?.Value<bool>() ?? default(bool),
                        StopTriggeredTime = jObject["StopTriggeredTime"]?.Value<DateTime?>() ?? jObject["stopTriggeredTime"]?.Value<DateTime?>()
                    };
                    break;

                case OrderType.TrailingStop:
                    order = new TrailingStopOrder
                    {
                        StopPrice = SafeDecimalValueOrDefault(jObject["StopPrice"] ?? jObject["stopPrice"]),
                        TrailingAmount = SafeDecimalValueOrDefault(jObject["TrailingAmount"] ?? jObject["trailingAmount"]),
                        TrailingAsPercentage = jObject["TrailingAsPercentage"]?.Value<bool>() ?? jObject["trailingAsPercentage"]?.Value<bool>() ?? default(bool)
                    };
                    break;

                case OrderType.LimitIfTouched:
                    order = new LimitIfTouchedOrder
                    {
                        LimitPrice = SafeDecimalValueOrDefault(jObject["LimitPrice"] ?? jObject["limitPrice"]),
                        TriggerPrice = SafeDecimalValueOrDefault(jObject["TriggerPrice"] ?? jObject["triggerPrice"]),
                        TriggerTouched = jObject["TriggerTouched"]?.Value<bool>() ?? jObject["triggerTouched"]?.Value<bool>() ?? default(bool)
                    };
                    break;

                case OrderType.MarketOnOpen:
                    order = new MarketOnOpenOrder();
                    break;

                case OrderType.MarketOnClose:
                    order = new MarketOnCloseOrder();
                    break;

                case OrderType.OptionExercise:
                    order = new OptionExerciseOrder();
                    break;

                case OrderType.ComboMarket:
                    order = new ComboMarketOrder() { GroupOrderManager = DeserializeGroupOrderManager(jObject) };
                    break;

                case OrderType.ComboLimit:
                    order = new ComboLimitOrder() { GroupOrderManager = DeserializeGroupOrderManager(jObject) };
                    break;

                case OrderType.ComboLegLimit:
                    order = new ComboLegLimitOrder
                    {
                        GroupOrderManager = DeserializeGroupOrderManager(jObject),
                        LimitPrice = SafeDecimalValueOrDefault(jObject["LimitPrice"] ?? jObject["limitPrice"])
                    };
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
            return order;
        }

        /// <summary>
        /// Creates a Time In Force of the correct type
        /// </summary>
        private static TimeInForce CreateTimeInForce(JsonNode timeInForce, JsonObject jObject)
        {
            // for backward-compatibility support deserialization of old JSON format
            if (timeInForce is JsonValue)
            {
                var value = timeInForce.Value<int>();

                switch (value)
                {
                    case 0:
                        return TimeInForce.GoodTilCanceled;

                    case 1:
                        return TimeInForce.Day;

                    case 2:
                        var expiry = jObject["DurationValue"].Value<DateTime>();
                        return TimeInForce.GoodTilDate(expiry);

                    default:
                        throw new Exception($"Unknown time in force value: {value}");
                }
            }

            // convert with TimeInForceJsonConverter
            return timeInForce.Value<TimeInForce>();
        }

        /// <summary>
        /// Deserializes the GroupOrderManager from the JSON object
        /// </summary>
        private static GroupOrderManager DeserializeGroupOrderManager(JsonObject jObject)
        {
            var groupOrderManagerJObject = jObject["GroupOrderManager"] ?? jObject["groupOrderManager"];

            // this should never happen
            if (groupOrderManagerJObject == null)
            {
                throw new ArgumentException("OrderJsonConverter.DeserializeGroupOrderManager(): JObject does not have a GroupOrderManager");
            }

            var result = new GroupOrderManager(
                groupOrderManagerJObject["Id"]?.Value<int>() ?? groupOrderManagerJObject["id"].Value<int>(),
                groupOrderManagerJObject["Count"]?.Value<int>() ?? groupOrderManagerJObject["count"].Value<int>(),
                SafeDecimalValue(groupOrderManagerJObject["Quantity"] ?? groupOrderManagerJObject["quantity"]),
                SafeDecimalValue(groupOrderManagerJObject["LimitPrice"] ?? groupOrderManagerJObject["limitPrice"])
            );

            foreach (var orderId in (groupOrderManagerJObject["OrderIds"]?.Values<int>() ?? groupOrderManagerJObject["orderIds"].Values<int>()))
            {
                result.OrderIds.Add(orderId);
            }

            return result;
        }

        /// <summary>
        /// Deserializes the contingency of the order from the JSON object, if any, available for any order type
        /// </summary>
        private static void DeserializeContingency(JsonObject jObject, Order order)
        {
            var contingencyToken = jObject["Contingency"] ?? jObject["contingency"];
            if (contingencyToken is not JsonObject)
            {
                // not a contingent order, or an order serialized before they existed
                return;
            }
            var linksToken = contingencyToken["Links"] ?? contingencyToken["links"];
            var count = (contingencyToken["Count"] ?? contingencyToken["count"])?.Value<int>() ?? 0;
            if (linksToken is not JsonArray || count < 1)
            {
                return;
            }

            var links = new List<ContingencyLink>();
            foreach (var token in linksToken.AsArray())
            {
                if (token is not JsonObject)
                {
                    continue;
                }
                var type = (ContingencyType)((token["Type"] ?? token["type"])?.Value<int>() ?? 0);
                var roleToken = token["Role"] ?? token["role"];
                var role = !roleToken.IsNull() ? (ContingencyRole?)roleToken.Value<int>() : null;
                if (!ContingencyLink.IsValidRole(type, role))
                {
                    continue;
                }
                var triggeredTime = token["TriggeredTime"] ?? token["triggeredTime"];
                links.Add(new ContingencyLink(
                    (token["Id"] ?? token["id"])?.Value<int>() ?? 0,
                    type,
                    role,
                    (token["Triggered"] ?? token["triggered"])?.Value<bool>() ?? false,
                    !triggeredTime.IsNull() ? triggeredTime.Value<DateTime>() : null));
            }

            var contingency = new OrderContingency((contingencyToken["Id"] ?? contingencyToken["id"])?.Value<int>() ?? 0, count, links);
            var orderIds = contingencyToken["OrderIds"] ?? contingencyToken["orderIds"];
            if (orderIds is JsonArray)
            {
                contingency.OrderIds.UnionWith(orderIds.Values<int>());
            }
            order.Contingency = contingency;
        }

        /// <summary>
        /// Gets the decimal value of the given token, clamping it to the decimal range when the token holds
        /// a double too large or too small to be represented as a decimal. Values at the edge of the range,
        /// like a quantity clamped to <see cref="decimal.MaxValue"/> by <see cref="Extensions.SafeDecimalCast"/>,
        /// are parsed back from JSON as doubles that round past the decimal range
        /// </summary>
        private static decimal SafeDecimalValue(JsonNode token)
        {
            return JsonNodeExtensions.SafeDecimalValue(token);
        }

        private static decimal SafeDecimalValueOrDefault(JsonNode token)
        {
            return token == null ? default : SafeDecimalValue(token);
        }
    }
}
