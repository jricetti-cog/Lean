using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.Results;

/// <summary>Consumes <c>results.packets</c> (result/order/log/error/status events of every job) into <see cref="ResultsIngestor"/>.</summary>
public sealed class ResultsConsumer(IMessageConsumer consumer, ResultsIngestor ingestor, ResultsSettings settings, ILogger<ResultsConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await consumer.ConsumeAsync(Queues.ResultsPackets, ingestor.HandleAsync,
                    new ConsumerOptions
                    {
                        Prefetch = settings.Prefetch,
                        Partitions = settings.Partitions,
                        PartitionKey = envelope => envelope.Headers.JobId,
                    }, stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Consuming {Queue}", Queues.ResultsPackets);
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Consumer on {Queue} stopped; resubscribing", Queues.ResultsPackets);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
