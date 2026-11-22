using System;
using RabbitMQ.Client;
using QuantConnect.Configuration;

namespace QuantConnect.Spikes.EngineHosting
{
    /// <summary>
    /// One RabbitMQ connection per engine process, shared by the job queue and messaging handlers.
    /// </summary>
    public static class SpikeBus
    {
        public const string JobsQueue = "lean.jobs";
        public const string ResultsQueue = "lean.results";

        private static readonly Lazy<IConnection> _connection = new(() => new ConnectionFactory
        {
            Uri = new Uri(Environment.GetEnvironmentVariable("RABBITMQ_URL") ?? Config.Get("rabbitmq-url", "amqp://guest:guest@localhost:5672/"))
        }.CreateConnection("lean-engine"));

        private static readonly object _publishLock = new();
        private static IModel _publishChannel;

        /// <summary>
        /// The id of the job this engine process is running, taken from the job message.
        /// </summary>
        public static string JobId { get; set; }

        public static IModel CreateChannel()
        {
            var channel = _connection.Value.CreateModel();
            channel.QueueDeclare(JobsQueue, durable: true, exclusive: false, autoDelete: false);
            channel.QueueDeclare(ResultsQueue, durable: true, exclusive: false, autoDelete: false);
            return channel;
        }

        public static void Publish(string type, byte[] body)
        {
            lock (_publishLock)
            {
                _publishChannel ??= CreateChannel();
                var properties = _publishChannel.CreateBasicProperties();
                properties.CorrelationId = JobId;
                properties.Type = type;
                properties.ContentType = "application/json";
                properties.Persistent = true;
                _publishChannel.BasicPublish("", ResultsQueue, properties, body);
                _publishChannel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(30));
            }
        }

        public static void EnableConfirms()
        {
            lock (_publishLock)
            {
                _publishChannel ??= CreateChannel();
                _publishChannel.ConfirmSelect();
            }
        }
    }
}
