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
    /// Versions of the inter-service contracts. A topic carries its contract version so that
    /// services can be upgraded independently; breaking changes get a new version and a new topic.
    /// </summary>
    public static class ContractVersions
    {
        /// <summary>
        /// Market data service contract, version 1
        /// </summary>
        public const string MarketDataV1 = "marketdata.v1";

        /// <summary>
        /// Job submission/execution contract, version 1
        /// </summary>
        public const string JobsV1 = "jobs.v1";

        /// <summary>
        /// All contract versions understood by this build
        /// </summary>
        public static readonly IReadOnlyList<string> Supported = new[] { MarketDataV1, JobsV1 };
    }

    /// <summary>
    /// Well known message bus topics used between the gateway, the market data service and the backtest workers
    /// </summary>
    public static class Topics
    {
        /// <summary>Request: fetch the raw bytes of a data file. Body <see cref="DataFetchRequest"/>, reply <see cref="DataFetchResponse"/> + payload</summary>
        public const string MarketDataFetch = ContractVersions.MarketDataV1 + ".fetch";

        /// <summary>Request: list stored data files under a prefix. Body <see cref="DataListRequest"/>, reply <see cref="DataListResponse"/></summary>
        public const string MarketDataList = ContractVersions.MarketDataV1 + ".list";

        /// <summary>Request: store a data file. Body <see cref="DataStoreRequest"/> + payload, reply <see cref="DataStoreResponse"/></summary>
        public const string MarketDataStore = ContractVersions.MarketDataV1 + ".store";

        /// <summary>Request: download historical data through the configured downloader. Body <see cref="DataDownloadRequest"/>, reply <see cref="DataDownloadResponse"/></summary>
        public const string MarketDataDownload = ContractVersions.MarketDataV1 + ".download";

        /// <summary>Request: start streaming live data. Body <see cref="DataSubscriptionRequest"/>, reply <see cref="DataSubscriptionResponse"/></summary>
        public const string MarketDataSubscribe = ContractVersions.MarketDataV1 + ".subscribe";

        /// <summary>Request: stop streaming live data. Body <see cref="DataUnsubscribeRequest"/>, reply <see cref="DataSubscriptionResponse"/></summary>
        public const string MarketDataUnsubscribe = ContractVersions.MarketDataV1 + ".unsubscribe";

        /// <summary>Request: market data service health. Reply <see cref="ServiceHealth"/></summary>
        public const string MarketDataHealth = ContractVersions.MarketDataV1 + ".health";

        /// <summary>Prefix of the published live data streams, one topic per subscription</summary>
        public const string MarketDataStreamPrefix = ContractVersions.MarketDataV1 + ".stream.";

        /// <summary>Request (worker → gateway): lease the next queued job. Body <see cref="JobLeaseRequest"/>, reply <see cref="JobLeaseResponse"/></summary>
        public const string JobsNext = ContractVersions.JobsV1 + ".next";

        /// <summary>Publish (worker → gateway): job lifecycle updates. Body <see cref="JobStatusUpdate"/></summary>
        public const string JobsStatus = ContractVersions.JobsV1 + ".status";

        /// <summary>Publish (gateway → workers): cancel a job. Body <see cref="JobCancelRequest"/></summary>
        public const string JobsCancel = ContractVersions.JobsV1 + ".cancel";

        /// <summary>Prefix of the published engine packets, one topic per job. Body <see cref="JobEvent"/></summary>
        public const string JobsEventsPrefix = ContractVersions.JobsV1 + ".events.";

        /// <summary>
        /// The live data stream topic of a subscription
        /// </summary>
        public static string MarketDataStream(string subscriptionId) => MarketDataStreamPrefix + subscriptionId;

        /// <summary>
        /// The engine packets topic of a job
        /// </summary>
        public static string JobEvents(string jobId) => JobsEventsPrefix + jobId;
    }

    /// <summary>
    /// Health information returned by a service
    /// </summary>
    public class ServiceHealth
    {
        /// <summary>Service name</summary>
        public string Service { get; set; }

        /// <summary>Unique instance id</summary>
        public string InstanceId { get; set; }

        /// <summary>LEAN version of the service</summary>
        public string Version { get; set; }

        /// <summary>Contract versions the service implements</summary>
        public List<string> Contracts { get; set; } = new();

        /// <summary>Free form details</summary>
        public Dictionary<string, string> Details { get; set; } = new();

        /// <summary>Time the health was produced</summary>
        public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    }
}
