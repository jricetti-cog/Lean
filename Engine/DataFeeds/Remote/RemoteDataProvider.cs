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
using System.Collections.Concurrent;
using System.IO;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;
using QuantConnect.Services;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds.Remote
{
    /// <summary>
    /// <see cref="IDataProvider"/> that reads data owned by the Market Data Service over the message bus
    /// (<c>marketdata.v1.fetch</c>). Keys under <see cref="Globals.DataFolder"/> are fetched remotely and cached
    /// (by default in the data folder itself, so a worker's data folder becomes a read-through cache).
    /// Keys outside the data folder are read from disk.
    /// </summary>
    public class RemoteDataProvider : IDataProvider, IDisposable
    {
        private readonly ConcurrentDictionary<string, object> _keyLocks = new();
        private readonly ConcurrentDictionary<string, DateTime> _missing = new();
        private readonly TimeSpan _missingTtl = TimeSpan.FromSeconds(Config.GetInt("remote-data-missing-ttl-seconds", 60));
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Config.GetInt("remote-data-timeout-seconds", 60));
        private readonly Lazy<IMessageBus> _bus;
        private readonly bool _ownsBus;
        private readonly string _dataFolder;
        private readonly string _cacheFolder;

        /// <summary>
        /// Event raised each time data fetch is finished (successfully or not)
        /// </summary>
        public event EventHandler<DataProviderNewDataRequestEventArgs> NewDataRequest;

        /// <summary>
        /// Creates a provider connected to the configured message bus
        /// </summary>
        public RemoteDataProvider() : this(null)
        {
        }

        /// <summary>
        /// Creates a provider using the given bus
        /// </summary>
        /// <param name="bus">The bus, null to connect to the configured bus on first use</param>
        /// <param name="cacheFolder">Local cache folder, defaults to the <c>remote-data-cache-folder</c> setting or <see cref="Globals.DataFolder"/></param>
        public RemoteDataProvider(IMessageBus bus, string cacheFolder = null)
        {
            _ownsBus = bus == null;
            _bus = bus == null ? new Lazy<IMessageBus>(() => MessageBusFactory.Create()) : new Lazy<IMessageBus>(bus);
            _dataFolder = Path.GetFullPath(Globals.DataFolder);
            _cacheFolder = Path.GetFullPath(cacheFolder ?? Config.Get("remote-data-cache-folder", Globals.DataFolder));
        }

        /// <summary>
        /// Retrieves data to be used in an algorithm
        /// </summary>
        public Stream Fetch(string key)
        {
            var success = true;
            var errorMessage = string.Empty;
            try
            {
                if (!TryGetRelativeKey(key, out var relativeKey))
                {
                    var path = FileExtension.ToNormalizedPath(key);
                    if (!File.Exists(path))
                    {
                        success = false;
                        return null;
                    }
                    return OpenRead(path);
                }

                var cachePath = Path.Combine(_cacheFolder, relativeKey);
                if (File.Exists(cachePath))
                {
                    return OpenRead(cachePath);
                }

                lock (_keyLocks.GetOrAdd(relativeKey, _ => new object()))
                {
                    if (File.Exists(cachePath))
                    {
                        return OpenRead(cachePath);
                    }
                    if (_missing.TryGetValue(relativeKey, out var missingUntil) && missingUntil > DateTime.UtcNow)
                    {
                        success = false;
                        return null;
                    }

                    var reply = _bus.Value.Request(Topics.MarketDataFetch, new DataFetchRequest { Key = relativeKey },
                        ContractVersions.MarketDataV1, timeout: _timeout);
                    var response = reply.GetBody<DataFetchResponse>();
                    if (response == null || !response.Found)
                    {
                        _missing[relativeKey] = DateTime.UtcNow + _missingTtl;
                        success = false;
                        errorMessage = response?.Error ?? string.Empty;
                        return null;
                    }

                    WriteAtomically(cachePath, reply.Payload ?? Array.Empty<byte>());
                    return OpenRead(cachePath);
                }
            }
            catch (Exception exception)
            {
                success = false;
                errorMessage = exception.Message;
                Log.Error(exception, $"RemoteDataProvider.Fetch({key})");
                throw;
            }
            finally
            {
                NewDataRequest?.Invoke(this, new DataProviderNewDataRequestEventArgs(key, success, errorMessage));
            }
        }

        /// <summary>
        /// Converts a data provider key into a Market Data Service key relative to the data folder
        /// </summary>
        public bool TryGetRelativeKey(string key, out string relativeKey)
        {
            relativeKey = null;
            if (string.IsNullOrEmpty(key) || key.Contains("://", StringComparison.Ordinal))
            {
                return false;
            }
            var fullPath = Path.GetFullPath(FileExtension.ToNormalizedPath(key));
            var root = _dataFolder.EndsWith(Path.DirectorySeparatorChar) ? _dataFolder : _dataFolder + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.Ordinal))
            {
                return false;
            }
            relativeKey = fullPath[root.Length..].Replace(Path.DirectorySeparatorChar, '/');
            return true;
        }

        /// <summary>
        /// Data LEAN reads straight from disk instead of through <see cref="IDataProvider"/>: market hours,
        /// symbol properties and the map and factor file folders enumerated by the local disk providers
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultReferenceData = new[] { "market-hours/", "symbol-properties/", "*/map_files/", "*/factor_files/" };

        /// <summary>
        /// Downloads every file under the given prefixes into the cache, used to bootstrap the reference data
        /// LEAN reads straight from disk. A prefix starting with '*' matches keys containing the rest, e.g. "*/map_files/"
        /// </summary>
        /// <returns>The number of files written</returns>
        public static int SynchronizeFolders(IMessageBus bus, string cacheFolder, params string[] prefixes)
        {
            var written = 0;
            foreach (var prefix in prefixes)
            {
                var request = prefix.StartsWith('*') ? new DataListRequest { Contains = prefix[1..], MaxResults = int.MaxValue } : new DataListRequest { Prefix = prefix, MaxResults = int.MaxValue };
                var listing = bus.Request<DataListRequest, DataListResponse>(Topics.MarketDataList, request, ContractVersions.MarketDataV1);
                foreach (var file in listing.Files)
                {
                    var path = Path.Combine(cacheFolder, file.Key);
                    if (File.Exists(path) && new FileInfo(path).Length == file.Size)
                    {
                        continue;
                    }
                    var reply = bus.Request(Topics.MarketDataFetch, new DataFetchRequest { Key = file.Key }, ContractVersions.MarketDataV1);
                    if (reply.GetBody<DataFetchResponse>()?.Found == true)
                    {
                        WriteAtomically(path, reply.Payload ?? Array.Empty<byte>());
                        written++;
                    }
                }
            }
            return written;
        }

        /// <summary>
        /// Disconnects the bus if this provider created it
        /// </summary>
        public void Dispose()
        {
            if (_ownsBus && _bus.IsValueCreated)
            {
                _bus.Value.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        private static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private static void WriteAtomically(string path, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, true);
        }
    }
}
