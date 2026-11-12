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
 *
*/

using System;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Services
{
    /// <summary>
    /// Versioned wire representation of a live market data point
    /// </summary>
    public class MarketDataPoint
    {
        /// <summary>Kind: "tick", "tradebar", "quotebar" or "openinterest"</summary>
        public string Kind { get; set; }

        /// <summary>The symbol</summary>
        public SymbolContract Symbol { get; set; }

        /// <summary>Start time, exchange time zone</summary>
        public DateTime Time { get; set; }

        /// <summary>End time, exchange time zone</summary>
        public DateTime EndTime { get; set; }

        /// <summary>Value</summary>
        public decimal Value { get; set; }

        /// <summary>Tick type</summary>
        public TickType? TickType { get; set; }

        /// <summary>Tick quantity</summary>
        public decimal? Quantity { get; set; }

        /// <summary>Tick exchange</summary>
        public string Exchange { get; set; }

        /// <summary>Tick sale condition</summary>
        public string SaleCondition { get; set; }

        /// <summary>Tick suspicious flag</summary>
        public bool? Suspicious { get; set; }

        /// <summary>Bid price (tick)</summary>
        public decimal? BidPrice { get; set; }

        /// <summary>Ask price (tick)</summary>
        public decimal? AskPrice { get; set; }

        /// <summary>Bid size (tick) / last bid size (quote bar)</summary>
        public decimal? BidSize { get; set; }

        /// <summary>Ask size (tick) / last ask size (quote bar)</summary>
        public decimal? AskSize { get; set; }

        /// <summary>Open, high, low, close of a trade bar</summary>
        public decimal[] Ohlc { get; set; }

        /// <summary>Trade bar volume</summary>
        public decimal? Volume { get; set; }

        /// <summary>Quote bar bid OHLC</summary>
        public decimal[] BidOhlc { get; set; }

        /// <summary>Quote bar ask OHLC</summary>
        public decimal[] AskOhlc { get; set; }
    }

    /// <summary>
    /// Converts between <see cref="BaseData"/> and <see cref="MarketDataPoint"/>. Supported types are
    /// <see cref="Tick"/>, <see cref="OpenInterest"/>, <see cref="TradeBar"/> and <see cref="QuoteBar"/>.
    /// </summary>
    public static class MarketDataPointSerializer
    {
        /// <summary>
        /// True if the data type can be streamed between services
        /// </summary>
        public static bool IsSupported(Type type)
        {
            return type == typeof(Tick) || type == typeof(OpenInterest) || type == typeof(TradeBar) || type == typeof(QuoteBar);
        }

        /// <summary>
        /// Resolves a supported data type from its full name
        /// </summary>
        public static Type ResolveType(string fullName)
        {
            foreach (var type in new[] { typeof(Tick), typeof(OpenInterest), typeof(TradeBar), typeof(QuoteBar) })
            {
                if (type.FullName == fullName || type.Name == fullName)
                {
                    return type;
                }
            }
            throw new NotSupportedException($"Data type '{fullName}' can not be streamed between services");
        }

        /// <summary>
        /// Converts a data point to its wire representation
        /// </summary>
        public static MarketDataPoint ToContract(BaseData data)
        {
            var point = new MarketDataPoint
            {
                Symbol = SymbolContract.From(data.Symbol),
                Time = data.Time,
                EndTime = data.EndTime,
                Value = data.Value
            };
            switch (data)
            {
                case OpenInterest openInterest:
                    point.Kind = "openinterest";
                    point.TickType = openInterest.TickType;
                    break;
                case Tick tick:
                    point.Kind = "tick";
                    point.TickType = tick.TickType;
                    point.Quantity = tick.Quantity;
                    point.Exchange = tick.ExchangeCode;
                    point.SaleCondition = tick.SaleCondition;
                    point.Suspicious = tick.Suspicious;
                    point.BidPrice = tick.BidPrice;
                    point.AskPrice = tick.AskPrice;
                    point.BidSize = tick.BidSize;
                    point.AskSize = tick.AskSize;
                    break;
                case TradeBar bar:
                    point.Kind = "tradebar";
                    point.Ohlc = new[] { bar.Open, bar.High, bar.Low, bar.Close };
                    point.Volume = bar.Volume;
                    break;
                case QuoteBar quote:
                    point.Kind = "quotebar";
                    point.BidOhlc = quote.Bid == null ? null : new[] { quote.Bid.Open, quote.Bid.High, quote.Bid.Low, quote.Bid.Close };
                    point.AskOhlc = quote.Ask == null ? null : new[] { quote.Ask.Open, quote.Ask.High, quote.Ask.Low, quote.Ask.Close };
                    point.BidSize = quote.LastBidSize;
                    point.AskSize = quote.LastAskSize;
                    break;
                default:
                    throw new NotSupportedException($"Data type '{data.GetType().FullName}' can not be streamed between services");
            }
            return point;
        }

        /// <summary>
        /// Rebuilds a data point from its wire representation
        /// </summary>
        public static BaseData FromContract(MarketDataPoint point, Symbol symbol = null)
        {
            symbol ??= point.Symbol.ToSymbol();
            var period = point.EndTime - point.Time;
            switch (point.Kind)
            {
                case "openinterest":
                    return new OpenInterest(point.Time, symbol, point.Value);
                case "tick":
                    var tick = new Tick
                    {
                        Symbol = symbol,
                        Time = point.Time,
                        Value = point.Value,
                        TickType = point.TickType ?? QuantConnect.TickType.Trade,
                        Quantity = point.Quantity ?? 0,
                        ExchangeCode = point.Exchange ?? string.Empty,
                        SaleCondition = point.SaleCondition ?? string.Empty,
                        Suspicious = point.Suspicious ?? false,
                        BidPrice = point.BidPrice ?? 0,
                        AskPrice = point.AskPrice ?? 0,
                        BidSize = point.BidSize ?? 0,
                        AskSize = point.AskSize ?? 0
                    };
                    return tick;
                case "tradebar":
                    return new TradeBar(point.Time, symbol, point.Ohlc[0], point.Ohlc[1], point.Ohlc[2], point.Ohlc[3], point.Volume ?? 0, period);
                case "quotebar":
                    var bid = point.BidOhlc == null ? null : new Bar(point.BidOhlc[0], point.BidOhlc[1], point.BidOhlc[2], point.BidOhlc[3]);
                    var ask = point.AskOhlc == null ? null : new Bar(point.AskOhlc[0], point.AskOhlc[1], point.AskOhlc[2], point.AskOhlc[3]);
                    return new QuoteBar(point.Time, symbol, bid, point.BidSize ?? 0, ask, point.AskSize ?? 0, period);
                default:
                    throw new NotSupportedException($"Unknown market data point kind '{point.Kind}'");
            }
        }
    }
}
