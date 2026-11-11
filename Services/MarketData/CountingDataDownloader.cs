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

using System.Collections.Concurrent;
using System.Collections.Generic;
using QuantConnect.Data;

namespace QuantConnect.Services.MarketData
{
    /// <summary>
    /// Decorates a downloader and counts the data points it returns per symbol
    /// </summary>
    public class CountingDataDownloader : IDataDownloader
    {
        private readonly IDataDownloader _downloader;

        /// <summary>
        /// Data points returned per symbol
        /// </summary>
        public ConcurrentDictionary<string, long> Counts { get; } = new();

        /// <summary>
        /// Creates a new instance
        /// </summary>
        public CountingDataDownloader(IDataDownloader downloader)
        {
            _downloader = downloader;
        }

        /// <summary>
        /// Downloads and counts
        /// </summary>
        public IEnumerable<BaseData> Get(DataDownloaderGetParameters dataDownloaderGetParameters)
        {
            var data = _downloader.Get(dataDownloaderGetParameters);
            if (data == null)
            {
                return null;
            }
            return Count(dataDownloaderGetParameters.Symbol.Value, data);
        }

        private IEnumerable<BaseData> Count(string symbol, IEnumerable<BaseData> data)
        {
            foreach (var point in data)
            {
                Counts.AddOrUpdate(symbol, 1, (_, count) => count + 1);
                yield return point;
            }
        }
    }
}
