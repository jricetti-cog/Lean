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
using System.Text;
using NetMQ;
using NetMQ.Sockets;
using QuantConnect.Logging;
using QuantConnect.Services;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// TCP message bus using NetMQ publish/subscribe sockets connected to a <see cref="MessageBroker"/>.
    /// Wire format is a three frame message: topic, envelope metadata (json), binary payload.
    /// </summary>
    public class NetMqMessageBus : MessageBusBase
    {
        private readonly PublisherSocket _publisher;
        private readonly SubscriberSocket _subscriber;
        private readonly NetMQQueue<Action> _actions;
        private readonly NetMQPoller _poller;

        /// <summary>
        /// Address publishers connect to (the broker frontend)
        /// </summary>
        public string PublishAddress { get; }

        /// <summary>
        /// Address subscribers connect to (the broker backend)
        /// </summary>
        public string SubscribeAddress { get; }

        /// <summary>
        /// Creates a new bus connection
        /// </summary>
        public NetMqMessageBus(string publishAddress, string subscribeAddress, string instanceId = null) : base(instanceId)
        {
            PublishAddress = publishAddress;
            SubscribeAddress = subscribeAddress;

            _publisher = new PublisherSocket();
            _publisher.Options.SendHighWatermark = 100000;
            _publisher.Connect(publishAddress);

            _subscriber = new SubscriberSocket();
            _subscriber.Options.ReceiveHighWatermark = 100000;
            _subscriber.Connect(subscribeAddress);
            _subscriber.ReceiveReady += OnReceiveReady;

            _actions = new NetMQQueue<Action>();
            _actions.ReceiveReady += (_, args) =>
            {
                while (args.Queue.TryDequeue(out var action, TimeSpan.Zero))
                {
                    action();
                }
            };

            _poller = new NetMQPoller { _subscriber, _actions };
            _poller.RunAsync("NetMqMessageBus");
            SubscribeInbox();
        }

        /// <summary>
        /// Sends the message to the broker
        /// </summary>
        protected override void TransportPublish(MessageEnvelope envelope)
        {
            var topic = Encoding.UTF8.GetBytes(envelope.Topic);
            var metadata = envelope.SerializeMetadata();
            var payload = envelope.Payload ?? Array.Empty<byte>();
            _actions.Enqueue(() => _publisher.SendMoreFrame(topic).SendMoreFrame(metadata).SendFrame(payload));
        }

        /// <summary>
        /// Subscribes the socket to a prefix
        /// </summary>
        protected override void TransportSubscribe(string topicPrefix)
        {
            _actions.Enqueue(() => _subscriber.Subscribe(topicPrefix));
        }

        /// <summary>
        /// Unsubscribes the socket from a prefix
        /// </summary>
        protected override void TransportUnsubscribe(string topicPrefix)
        {
            _actions.Enqueue(() => _subscriber.Unsubscribe(topicPrefix));
        }

        private void OnReceiveReady(object sender, NetMQSocketEventArgs args)
        {
            var message = new NetMQMessage();
            while (args.Socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref message, 3))
            {
                try
                {
                    if (message.FrameCount < 2)
                    {
                        continue;
                    }
                    var payload = message.FrameCount > 2 ? message[2].ToByteArray() : null;
                    OnMessageReceived(MessageEnvelope.Deserialize(message[1].ToByteArray(), payload));
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "NetMqMessageBus.OnReceiveReady(): failed to read message");
                }
                message = new NetMQMessage();
            }
        }

        /// <summary>
        /// Closes the sockets
        /// </summary>
        public override void Dispose()
        {
            base.Dispose();
            _poller.Stop();
            _poller.Dispose();
            _actions.Dispose();
            _publisher.Dispose();
            _subscriber.Dispose();
        }
    }
}
