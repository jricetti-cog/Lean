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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.DownloaderDataProvider.Launcher.Models;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Services.MarketData
{
    /// <summary>
    /// Market Data Service: serves the <c>marketdata.v1.*</c> contracts on the message bus.
    /// It owns the data store (fetch, list, store), runs data downloads using the existing LEAN downloader
    /// pipeline and streams live data from an upstream <see cref="IDataQueueHandler"/>.
    /// </summary>
    public class MarketDataService : IDisposable
    {
        private readonly object _downloadLock = new();
        private readonly List<IDisposable> _registrations = new();
        private readonly ConcurrentDictionary<string, LiveStream> _streams = new();
        private readonly IMessageBus _bus;
        private readonly Func<IDataDownloader> _downloaderFactory;
        private readonly Lazy<IDataQueueHandler> _dataQueueHandler;
        private readonly Lazy<MarketHoursDatabase> _marketHours = new(MarketHoursDatabase.FromDataFolder);
        private readonly string _version = typeof(MarketDataService).Assembly.GetName().Version?.ToString();

        /// <summary>
        /// The data store
        /// </summary>
        public MarketDataStore Store { get; }

        /// <summary>
        /// Number of active live streams
        /// </summary>
        public int ActiveStreams => _streams.Count;

        /// <summary>
        /// Creates a new service
        /// </summary>
        /// <param name="bus">The message bus</param>
        /// <param name="store">The data store</param>
        /// <param name="downloaderFactory">Creates the downloader used for download requests, null disables downloads</param>
        /// <param name="dataQueueHandlerFactory">Creates the upstream live data source, null disables streaming</param>
        public MarketDataService(IMessageBus bus, MarketDataStore store, Func<IDataDownloader> downloaderFactory = null,
            Func<IDataQueueHandler> dataQueueHandlerFactory = null)
        {
            _bus = bus;
            Store = store;
            _downloaderFactory = downloaderFactory;
            _dataQueueHandler = dataQueueHandlerFactory == null ? null : new Lazy<IDataQueueHandler>(dataQueueHandlerFactory);
        }

        /// <summary>
        /// Registers the request handlers
        /// </summary>
        public MarketDataService Start()
        {
            _registrations.Add(_bus.Respond(Topics.MarketDataFetch, Fetch));
            _registrations.Add(_bus.Respond(Topics.MarketDataStore, StoreData));
            _registrations.Add(_bus.Respond<DataListRequest, DataListResponse>(Topics.MarketDataList,
                (request, _) => Store.List(request?.Prefix, request?.MaxResults ?? 10000, request?.Contains)));
            _registrations.Add(_bus.Respond<DataDownloadRequest, DataDownloadResponse>(Topics.MarketDataDownload, (request, _) => Download(request)));
            _registrations.Add(_bus.Respond<DataSubscriptionRequest, DataSubscriptionResponse>(Topics.MarketDataSubscribe, (request, _) => Subscribe(request)));
            _registrations.Add(_bus.Respond<DataUnsubscribeRequest, DataSubscriptionResponse>(Topics.MarketDataUnsubscribe, (request, _) => Unsubscribe(request.SubscriptionId)));
            _registrations.Add(_bus.Respond<object, ServiceHealth>(Topics.MarketDataHealth, (_, _) => Health()));
            Log.Trace($"MarketDataService.Start(): serving {Store.Root}");
            return this;
        }

        /// <summary>
        /// Current health
        /// </summary>
        public ServiceHealth Health()
        {
            return new ServiceHealth
            {
                Service = "market-data",
                InstanceId = _bus.InstanceId,
                Version = _version,
                Contracts = new List<string> { ContractVersions.MarketDataV1 },
                Details = new Dictionary<string, string>
                {
                    ["dataRoot"] = Store.Root,
                    ["downloads"] = (_downloaderFactory != null).ToString(),
                    ["streaming"] = (_dataQueueHandler != null).ToString(),
                    ["activeStreams"] = ActiveStreams.ToStringInvariant()
                }
            };
        }

        private MessageEnvelope Fetch(MessageEnvelope request)
        {
            var key = request.GetBody<DataFetchRequest>().Key;
            var content = Store.Read(key);
            var response = new DataFetchResponse { Key = key, Found = content != null, Size = content?.Length ?? 0 };
            return request.CreateReply(response, content);
        }

        private MessageEnvelope StoreData(MessageEnvelope request)
        {
            var body = request.GetBody<DataStoreRequest>();
            var content = request.Payload ?? Array.Empty<byte>();
            if (!Store.Store(body.Key, content, body.Overwrite))
            {
                return request.CreateErrorReply($"'{body.Key}' already exists");
            }
            return request.CreateReply(new DataStoreResponse { Key = body.Key, Size = content.Length });
        }

        /// <summary>
        /// Downloads data into the store using the LEAN downloader pipeline (<c>DownloaderDataProvider</c>)
        /// </summary>
        public DataDownloadResponse Download(DataDownloadRequest request)
        {
            if (_downloaderFactory == null)
            {
                throw new InvalidOperationException("No data downloader is configured, set 'data-downloader'");
            }
            if (request.Symbols == null || request.Symbols.Count == 0)
            {
                throw new ArgumentException("At least one symbol is required");
            }
            var symbols = request.Symbols.Select(s => s.ToSymbol()).ToList();
            var first = symbols[0];
            var config = new DataDownloadConfig(request.TickType, first.SecurityType, request.Resolution, request.Start, request.End, first.ID.Market, symbols);

            var downloader = new CountingDataDownloader(_downloaderFactory());
            lock (_downloadLock)
            {
                using var cacheProvider = new DiskDataCacheProvider();
                DownloaderDataProvider.Launcher.Program.RunDownload(downloader, config, Store.Root, cacheProvider);
            }

            var response = new DataDownloadResponse();
            foreach (var symbol in symbols)
            {
                var count = downloader.Counts.TryGetValue(symbol.Value, out var value) ? value : 0;
                response.DataPointsBySymbol[symbol.Value] = count;
                response.DataPoints += count;
                if (count == 0)
                {
                    response.Warnings.Add($"No data returned for {symbol.Value}");
                }
            }
            return response;
        }

        /// <summary>
        /// Starts streaming live data for a subscription
        /// </summary>
        public DataSubscriptionResponse Subscribe(DataSubscriptionRequest request)
        {
            var response = new DataSubscriptionResponse { SubscriptionId = request.SubscriptionId, StreamTopic = Topics.MarketDataStream(request.SubscriptionId) };
            if (_dataQueueHandler == null)
            {
                throw new InvalidOperationException("Live streaming is not configured, set 'data-queue-handler'");
            }

            var symbol = request.Symbol.ToSymbol();
            var type = MarketDataPointSerializer.ResolveType(request.DataType);
            var entry = _marketHours.Value.GetEntry(symbol.ID.Market, symbol, symbol.SecurityType);
            var config = new SubscriptionDataConfig(type, symbol, request.Resolution, entry.DataTimeZone, entry.ExchangeHours.TimeZone,
                false, request.ExtendedMarketHours, false, tickType: request.TickType);

            var stream = new LiveStream { Config = config, Topic = response.StreamTopic };
            var enumerator = _dataQueueHandler.Value.Subscribe(config, (_, _) => stream.Signal.Set());
            if (enumerator == null)
            {
                return response;
            }
            stream.Enumerator = enumerator;
            stream.Thread = new Thread(() => Pump(request.SubscriptionId, stream)) { IsBackground = true, Name = $"MarketDataStream {symbol.Value}" };
            _streams[request.SubscriptionId] = stream;
            stream.Thread.Start();
            response.Active = true;
            Log.Trace($"MarketDataService.Subscribe(): {config} -> {response.StreamTopic}");
            return response;
        }

        /// <summary>
        /// Stops a live stream
        /// </summary>
        public DataSubscriptionResponse Unsubscribe(string subscriptionId)
        {
            if (_streams.TryRemove(subscriptionId, out var stream))
            {
                stream.Cancellation.Cancel();
                stream.Signal.Set();
                _dataQueueHandler.Value.Unsubscribe(stream.Config);
                if (stream.Thread.Join(TimeSpan.FromSeconds(5)))
                {
                    stream.Enumerator.DisposeSafely();
                    stream.Signal.Dispose();
                    stream.Cancellation.Dispose();
                }
                Log.Trace($"MarketDataService.Unsubscribe(): {stream.Config}");
            }
            return new DataSubscriptionResponse { SubscriptionId = subscriptionId, Active = false };
        }

        private void Pump(string subscriptionId, LiveStream stream)
        {
            var batch = new MarketDataBatch { SubscriptionId = subscriptionId };
            void Flush()
            {
                if (batch.Points.Count > 0)
                {
                    _bus.Publish(stream.Topic, batch, ContractVersions.MarketDataV1);
                    batch = new MarketDataBatch { SubscriptionId = subscriptionId };
                }
            }
            try
            {
                while (!stream.Cancellation.IsCancellationRequested)
                {
                    var ended = false;
                    while (true)
                    {
                        if (!stream.Enumerator.MoveNext())
                        {
                            ended = true;
                            break;
                        }
                        if (stream.Enumerator.Current == null)
                        {
                            break;
                        }
                        batch.Points.Add(MarketDataPointSerializer.ToContract(stream.Enumerator.Current));
                        if (batch.Points.Count >= 500)
                        {
                            Flush();
                        }
                    }
                    Flush();
                    if (ended)
                    {
                        break;
                    }
                    stream.Signal.Wait(250, stream.Cancellation.Token);
                    stream.Signal.Reset();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"MarketDataService.Pump({stream.Config})");
            }
        }

        /// <summary>
        /// Stops every stream and unregisters the handlers
        /// </summary>
        public void Dispose()
        {
            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }
            foreach (var id in _streams.Keys)
            {
                Unsubscribe(id);
            }
            if (_dataQueueHandler?.IsValueCreated == true)
            {
                _dataQueueHandler.Value.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        private sealed class LiveStream
        {
            public SubscriptionDataConfig Config { get; set; }
            public string Topic { get; set; }
            public IEnumerator<BaseData> Enumerator { get; set; }
            public Thread Thread { get; set; }
            public ManualResetEventSlim Signal { get; } = new();
            public CancellationTokenSource Cancellation { get; } = new();
        }
    }
}
