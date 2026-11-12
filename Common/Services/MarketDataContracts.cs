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
using System.Collections.Generic;

namespace QuantConnect.Services
{
    /// <summary>
    /// Fetch a data file. Keys are relative to the data folder, use '/' separators, e.g. "equity/usa/minute/spy/20131007_trade.zip"
    /// </summary>
    public class DataFetchRequest
    {
        /// <summary>The relative data key</summary>
        public string Key { get; set; }
    }

    /// <summary>
    /// Result of a <see cref="DataFetchRequest"/>, the file bytes are the envelope payload
    /// </summary>
    public class DataFetchResponse
    {
        /// <summary>The relative data key</summary>
        public string Key { get; set; }

        /// <summary>True if the file exists</summary>
        public bool Found { get; set; }

        /// <summary>File size in bytes</summary>
        public long Size { get; set; }

        /// <summary>Error message if the fetch failed for a reason other than the file not existing</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// List data files under a prefix
    /// </summary>
    public class DataListRequest
    {
        /// <summary>Relative folder prefix, e.g. "market-hours" or "equity/usa/map_files"</summary>
        public string Prefix { get; set; }

        /// <summary>Optional substring the key must contain, e.g. "/map_files/"</summary>
        public string Contains { get; set; }

        /// <summary>Maximum number of entries to return</summary>
        public int MaxResults { get; set; } = 10000;
    }

    /// <summary>
    /// A stored data file
    /// </summary>
    public class DataFileInfo
    {
        /// <summary>The relative data key</summary>
        public string Key { get; set; }

        /// <summary>Size in bytes</summary>
        public long Size { get; set; }

        /// <summary>Last write time</summary>
        public DateTime LastModifiedUtc { get; set; }
    }

    /// <summary>
    /// Result of a <see cref="DataListRequest"/>
    /// </summary>
    public class DataListResponse
    {
        /// <summary>The files found</summary>
        public List<DataFileInfo> Files { get; set; } = new();

        /// <summary>True if more files exist than were returned</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Store a data file, the file bytes are the envelope payload
    /// </summary>
    public class DataStoreRequest
    {
        /// <summary>The relative data key</summary>
        public string Key { get; set; }

        /// <summary>Overwrite an existing file</summary>
        public bool Overwrite { get; set; } = true;
    }

    /// <summary>
    /// Result of a <see cref="DataStoreRequest"/>
    /// </summary>
    public class DataStoreResponse
    {
        /// <summary>The relative data key</summary>
        public string Key { get; set; }

        /// <summary>Size written</summary>
        public long Size { get; set; }
    }

    /// <summary>
    /// Identifies a symbol across services. <see cref="SecurityIdentifier"/> is the canonical identity, the
    /// ticker/market/security type triple is used when the identifier is not known by the caller
    /// </summary>
    public class SymbolContract
    {
        /// <summary>The serialized <see cref="SecurityIdentifier"/>, optional</summary>
        public string SecurityIdentifier { get; set; }

        /// <summary>The ticker</summary>
        public string Ticker { get; set; }

        /// <summary>The security type</summary>
        public SecurityType SecurityType { get; set; } = SecurityType.Equity;

        /// <summary>The market</summary>
        public string Market { get; set; } = QuantConnect.Market.USA;

        /// <summary>
        /// Creates a contract from a symbol
        /// </summary>
        public static SymbolContract From(Symbol symbol)
        {
            return new SymbolContract
            {
                SecurityIdentifier = symbol.ID.ToString(),
                Ticker = symbol.Value,
                SecurityType = symbol.SecurityType,
                Market = symbol.ID.Market
            };
        }

        /// <summary>
        /// Resolves the LEAN symbol
        /// </summary>
        public Symbol ToSymbol()
        {
            if (!string.IsNullOrEmpty(SecurityIdentifier) && QuantConnect.SecurityIdentifier.TryParse(SecurityIdentifier, out var sid))
            {
                return new Symbol(sid, string.IsNullOrEmpty(Ticker) ? sid.Symbol : Ticker);
            }
            if (string.IsNullOrEmpty(Ticker))
            {
                throw new ArgumentException("A symbol contract requires a security identifier or a ticker");
            }
            return Symbol.Create(Ticker, SecurityType, Market);
        }
    }

    /// <summary>
    /// Download historical data with the market data service downloader and store it in LEAN format
    /// </summary>
    public class DataDownloadRequest
    {
        /// <summary>The symbols to download</summary>
        public List<SymbolContract> Symbols { get; set; } = new();

        /// <summary>The resolution</summary>
        public Resolution Resolution { get; set; } = Resolution.Minute;

        /// <summary>The tick type</summary>
        public TickType TickType { get; set; } = TickType.Trade;

        /// <summary>Start date (inclusive), exchange time</summary>
        public DateTime Start { get; set; }

        /// <summary>End date (exclusive), exchange time</summary>
        public DateTime End { get; set; }
    }

    /// <summary>
    /// Result of a <see cref="DataDownloadRequest"/>
    /// </summary>
    public class DataDownloadResponse
    {
        /// <summary>Number of data points returned by the downloader, before the pipeline filters them by date range and type</summary>
        public long DataPoints { get; set; }

        /// <summary>Per symbol count of data points returned by the downloader</summary>
        public Dictionary<string, long> DataPointsBySymbol { get; set; } = new();

        /// <summary>Messages for symbols the downloader could not serve</summary>
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Start streaming live data for a subscription
    /// </summary>
    public class DataSubscriptionRequest
    {
        /// <summary>Caller chosen unique subscription id, used to build the stream topic</summary>
        public string SubscriptionId { get; set; }

        /// <summary>The symbol</summary>
        public SymbolContract Symbol { get; set; }

        /// <summary>The data type full name, one of the types supported by <see cref="MarketDataPointSerializer"/></summary>
        public string DataType { get; set; }

        /// <summary>The resolution</summary>
        public Resolution Resolution { get; set; }

        /// <summary>The tick type</summary>
        public TickType TickType { get; set; }

        /// <summary>Extended market hours</summary>
        public bool ExtendedMarketHours { get; set; }
    }

    /// <summary>
    /// Stop streaming live data for a subscription
    /// </summary>
    public class DataUnsubscribeRequest
    {
        /// <summary>The subscription id</summary>
        public string SubscriptionId { get; set; }
    }

    /// <summary>
    /// Result of a subscription request
    /// </summary>
    public class DataSubscriptionResponse
    {
        /// <summary>The subscription id</summary>
        public string SubscriptionId { get; set; }

        /// <summary>The topic data points are published on</summary>
        public string StreamTopic { get; set; }

        /// <summary>True if the subscription is active</summary>
        public bool Active { get; set; }
    }

    /// <summary>
    /// A batch of live data points of a subscription, published on <see cref="Topics.MarketDataStream"/>
    /// </summary>
    public class MarketDataBatch
    {
        /// <summary>The subscription id</summary>
        public string SubscriptionId { get; set; }

        /// <summary>The data points</summary>
        public List<MarketDataPoint> Points { get; set; } = new();
    }
}
