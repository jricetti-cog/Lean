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
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;
using QuantConnect.Packets;
using QuantConnect.Services;

namespace QuantConnect.Lean.Engine.DataFeeds.Remote
{
    /// <summary>
    /// <see cref="IDataQueueHandler"/> that streams live data from the Market Data Service.
    /// Subscriptions are requested on <c>marketdata.v1.subscribe</c> and data arrives as
    /// <see cref="MarketDataBatch"/> messages on <c>marketdata.v1.stream.{subscriptionId}</c>.
    /// </summary>
    public class RemoteDataQueueHandler : IDataQueueHandler
    {
        private readonly ConcurrentDictionary<SubscriptionDataConfig, RemoteSubscription> _subscriptions = new();
        private readonly Lazy<IMessageBus> _bus;
        private readonly bool _ownsBus;
        private bool _disposed;

        /// <summary>
        /// Returns whether the data provider is connected
        /// </summary>
        public bool IsConnected => !_disposed;

        /// <summary>
        /// Creates a handler connected to the configured message bus
        /// </summary>
        public RemoteDataQueueHandler() : this(null)
        {
        }

        /// <summary>
        /// Creates a handler using the given bus
        /// </summary>
        public RemoteDataQueueHandler(IMessageBus bus)
        {
            _ownsBus = bus == null;
            _bus = bus == null ? new Lazy<IMessageBus>(() => MessageBusFactory.Create()) : new Lazy<IMessageBus>(bus);
        }

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!MarketDataPointSerializer.IsSupported(dataConfig.Type))
            {
                return null;
            }

            var subscription = new RemoteSubscription { Id = Guid.NewGuid().ToString("N"), Enumerator = new EnqueueableEnumerator<BaseData>() };
            subscription.Stream = _bus.Value.Subscribe<MarketDataBatch>(Topics.MarketDataStream(subscription.Id), (batch, _) =>
            {
                foreach (var point in batch.Points)
                {
                    var data = MarketDataPointSerializer.FromContract(point, dataConfig.Symbol);
                    subscription.Enumerator.Enqueue(data);
                    newDataAvailableHandler?.Invoke(this, new NewDataAvailableEventArgs { DataPoint = data });
                }
            });

            DataSubscriptionResponse response;
            try
            {
                response = _bus.Value.Request<DataSubscriptionRequest, DataSubscriptionResponse>(Topics.MarketDataSubscribe, new DataSubscriptionRequest
                {
                    SubscriptionId = subscription.Id,
                    Symbol = SymbolContract.From(dataConfig.Symbol),
                    DataType = dataConfig.Type.FullName,
                    Resolution = dataConfig.Resolution,
                    TickType = dataConfig.TickType,
                    ExtendedMarketHours = dataConfig.ExtendedMarketHours
                }, ContractVersions.MarketDataV1);
            }
            catch
            {
                subscription.Stream.Dispose();
                throw;
            }

            if (response?.Active != true)
            {
                subscription.Stream.Dispose();
                return null;
            }
            _subscriptions[dataConfig] = subscription;
            return subscription.Enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            if (_subscriptions.TryRemove(dataConfig, out var subscription))
            {
                Stop(subscription);
            }
        }

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        public void SetJob(LiveNodePacket job)
        {
        }

        /// <summary>
        /// Stops every subscription
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            foreach (var config in _subscriptions.Keys)
            {
                Unsubscribe(config);
            }
            _disposed = true;
            if (_ownsBus && _bus.IsValueCreated)
            {
                _bus.Value.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        private void Stop(RemoteSubscription subscription)
        {
            try
            {
                _bus.Value.Request(Topics.MarketDataUnsubscribe, new DataUnsubscribeRequest { SubscriptionId = subscription.Id },
                    ContractVersions.MarketDataV1, timeout: TimeSpan.FromSeconds(5));
            }
            catch (MessageBusException exception)
            {
                Log.Error(exception, "RemoteDataQueueHandler.Unsubscribe()");
            }
            subscription.Stream.Dispose();
            subscription.Enumerator.Stop();
        }

        private sealed class RemoteSubscription
        {
            public string Id { get; set; }
            public EnqueueableEnumerator<BaseData> Enumerator { get; set; }
            public IDisposable Stream { get; set; }
        }
    }
}
