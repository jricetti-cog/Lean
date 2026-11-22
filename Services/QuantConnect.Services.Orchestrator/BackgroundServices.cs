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
*/

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.Orchestrator;

/// <summary>Publishes unsent outbox rows with publisher confirms, in order; a failed publish stays unsent and is retried.</summary>
public sealed class OutboxRelay : BackgroundService
{
    private readonly JobStore _store;
    private readonly IMessagePublisher _publisher;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<OutboxRelay> _logger;
    private readonly SemaphoreSlim _wake = new(0);

    public OutboxRelay(JobStore store, IMessagePublisher publisher, IOptions<OrchestratorOptions> options, ILogger<OutboxRelay> logger)
    {
        _store = store;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
        _store.OutboxWritten += () => { if (_wake.CurrentCount == 0) _wake.Release(); };
    }

    /// <summary>Publishes everything unsent; returns the number of rows published.</summary>
    public async Task<int> RelayOnceAsync(CancellationToken ct)
    {
        var published = 0;
        while (true)
        {
            var rows = await _store.UnsentAsync(100, ct);
            if (rows.Count == 0) return published;
            foreach (var row in rows)
            {
                try
                {
                    await _publisher.PublishAsync(row.Message, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(e, "Outbox row {Id} ({Type} → {Exchange}/{RoutingKey}) not confirmed; will retry", row.Id, row.Message.Envelope.Type, row.Message.Exchange, row.Message.RoutingKey);
                    await _store.MarkFailedAsync(row, e.Message, ct);
                    return published;
                }
                await _store.MarkSentAsync(row, ct);
                published++;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayOnceAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(e, "Outbox relay pass failed");
            }
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(_options.OutboxPollMilliseconds), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }
}

/// <summary>Consumes <c>orchestrator.status</c> (job status, heartbeat and progress events) and applies them to the jobs database.</summary>
public sealed class StatusConsumer : BackgroundService
{
    private readonly JobStore _store;
    private readonly IMessageConsumer _consumer;
    private readonly ILogger<StatusConsumer> _logger;

    public StatusConsumer(JobStore store, IMessageConsumer consumer, ILogger<StatusConsumer> logger)
    {
        _store = store;
        _consumer = consumer;
        _logger = logger;
    }

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage message, CancellationToken ct)
    {
        try
        {
            await _store.ApplyEventAsync(message.Envelope, message.Body, ct);
            return MessageDisposition.Ack;
        }
        catch (System.Text.Json.JsonException e)
        {
            _logger.LogError(e, "Malformed {Type} {MessageId}; dead-lettering", message.Envelope.Type, message.Envelope.MessageId);
            return MessageDisposition.DeadLetter;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(e, "Applying {Type} {MessageId} failed; requeueing", message.Envelope.Type, message.Envelope.MessageId);
            return MessageDisposition.Retry;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IAsyncDisposable? subscription = null;
        while (subscription == null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                subscription = await _consumer.ConsumeAsync(Queues.OrchestratorStatus, HandleAsync, new ConsumerOptions { Prefetch = 32 }, stoppingToken);
                _logger.LogInformation("Consuming {Queue}", Queues.OrchestratorStatus);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "Cannot consume {Queue} yet; retrying", Queues.OrchestratorStatus);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (subscription != null) await subscription.DisposeAsync();
        }
    }
}

/// <summary>Periodically retries attempts whose worker stopped heartbeating and times out attempts past their deadline.</summary>
public sealed class HeartbeatMonitor : BackgroundService
{
    private readonly JobStore _store;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<HeartbeatMonitor> _logger;

    public HeartbeatMonitor(JobStore store, IOptions<OrchestratorOptions> options, ILogger<HeartbeatMonitor> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.MonitorIntervalSeconds));
        do
        {
            try
            {
                await _store.MonitorAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(e, "Heartbeat monitor pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
