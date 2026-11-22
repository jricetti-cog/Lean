using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.Report;

/// <summary>Consumes <c>jobs.report</c> with prefetch 1: one render at a time per replica (ADR 0001).</summary>
public sealed class ReportConsumer(IMessageConsumer consumer, ReportProcessor processor, ILogger<ReportConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await consumer.ConsumeAsync(Queues.Report, processor.HandleAsync,
                    new ConsumerOptions { Prefetch = 1 }, stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Consuming {Queue}", Queues.Report);
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Consumer on {Queue} stopped; resubscribing", Queues.Report);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
