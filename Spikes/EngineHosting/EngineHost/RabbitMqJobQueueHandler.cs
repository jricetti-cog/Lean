using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Queues;
using RabbitMQ.Client;

namespace QuantConnect.Spikes.EngineHosting
{
    /// <summary>
    /// Pulls one job message from RabbitMQ, applies its config overrides, then delegates to the
    /// stock <see cref="JobQueue"/> so the backtest packet is built exactly as the monolith builds it.
    /// Message: {"jobId": "...", "config": {"algorithm-type-name": "...", ...}}
    /// </summary>
    public class RabbitMqJobQueueHandler : IJobQueueHandler
    {
        private JobQueue _inner;
        private IModel _channel;
        private ulong _deliveryTag;

        public void Initialize(IApi api, IMessagingHandler messagingHandler)
        {
            _channel = SpikeBus.CreateChannel();
            SpikeBus.EnableConfirms();

            var timeout = TimeSpan.FromSeconds(Config.GetInt("job-wait-seconds", 120));
            var stopwatch = Stopwatch.StartNew();
            BasicGetResult message;
            while ((message = _channel.BasicGet(SpikeBus.JobsQueue, autoAck: false)) == null)
            {
                if (stopwatch.Elapsed > timeout)
                {
                    throw new TimeoutException($"No job on {SpikeBus.JobsQueue} after {timeout}");
                }
                Thread.Sleep(20);
            }

            _deliveryTag = message.DeliveryTag;
            var job = JObject.Parse(Encoding.UTF8.GetString(message.Body.Span));
            SpikeBus.JobId = job.Value<string>("jobId");
            foreach (var setting in (JObject)job["config"])
            {
                Config.Set(setting.Key, setting.Value.ToString());
            }
            Log.Trace($"RabbitMqJobQueueHandler.Initialize(): received job {SpikeBus.JobId} after {stopwatch.ElapsedMilliseconds}ms");

            _inner = new JobQueue();
            _inner.Initialize(api, messagingHandler);
        }

        public AlgorithmNodePacket NextJob(out string algorithmPath)
        {
            return _inner.NextJob(out algorithmPath);
        }

        public void AcknowledgeJob(AlgorithmNodePacket job)
        {
            _channel.BasicAck(_deliveryTag, multiple: false);
            _inner.AcknowledgeJob(job);
        }
    }
}
