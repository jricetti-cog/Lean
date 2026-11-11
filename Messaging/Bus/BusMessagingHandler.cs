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

using System.Collections.Generic;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Services;

namespace QuantConnect.Messaging.Bus
{
    /// <summary>
    /// Messaging handler used by engine workers: keeps the local console behavior of <see cref="Messaging"/>
    /// and also publishes every packet as a <see cref="JobEvent"/> on <c>jobs.v1.events.{jobId}</c>.
    /// The job id is read from the <c>job-id</c> setting, falling back to the algorithm id.
    /// </summary>
    public class BusMessagingHandler : Messaging
    {
        private IMessageBus _bus;
        private bool _ownsBus;
        private string _jobId;

        /// <summary>
        /// Creates a handler that connects to the configured bus on first use
        /// </summary>
        public BusMessagingHandler()
        {
        }

        /// <summary>
        /// Creates a handler publishing to the given bus
        /// </summary>
        public BusMessagingHandler(IMessageBus bus, string jobId = null)
        {
            _bus = bus;
            _jobId = jobId;
        }

        /// <summary>
        /// Captures the job id
        /// </summary>
        public override void SetAuthentication(AlgorithmNodePacket job)
        {
            base.SetAuthentication(job);
            _jobId ??= Config.Get("job-id", job.AlgorithmId);
        }

        /// <summary>
        /// Handles the packet locally and publishes it to the bus
        /// </summary>
        public override void Send(Packet packet)
        {
            base.Send(packet);
            var jobEvent = ToJobEvent(packet);
            if (jobEvent == null)
            {
                return;
            }
            try
            {
                if (_bus == null)
                {
                    _bus = MessageBusFactory.Create();
                    _ownsBus = true;
                }
                jobEvent.JobId = _jobId ?? Config.Get("job-id");
                _bus.Publish(Topics.JobEvents(jobEvent.JobId), jobEvent, ContractVersions.JobsV1);
            }
            catch (MessageBusException exception)
            {
                Log.Error(exception, "BusMessagingHandler.Send(): failed to publish packet");
            }
        }

        /// <summary>
        /// Converts an engine packet into its wire event, null for packets that are not published
        /// </summary>
        public static JobEvent ToJobEvent(Packet packet)
        {
            var jobEvent = new JobEvent { PacketType = packet.Type.ToString() };
            switch (packet)
            {
                case DebugPacket debug:
                    jobEvent.Message = debug.Message;
                    break;
                case LogPacket log:
                    jobEvent.Message = log.Message;
                    break;
                case RuntimeErrorPacket runtimeError:
                    jobEvent.Message = runtimeError.Message;
                    break;
                case HandledErrorPacket handledError:
                    jobEvent.Message = handledError.Message;
                    break;
                case BacktestResultPacket backtest:
                    jobEvent.Progress = backtest.Progress;
                    jobEvent.Statistics = backtest.Results?.Statistics == null ? null : new Dictionary<string, string>(backtest.Results.Statistics);
                    break;
                case LiveResultPacket live:
                    jobEvent.Statistics = live.Results?.Statistics == null ? null : new Dictionary<string, string>(live.Results.Statistics);
                    break;
                default:
                    return null;
            }
            return jobEvent;
        }

        /// <summary>
        /// Flushes and disconnects the bus if this handler created it
        /// </summary>
        public new void Dispose()
        {
            if (_ownsBus)
            {
                _bus?.Dispose();
            }
            base.Dispose();
        }
    }
}
