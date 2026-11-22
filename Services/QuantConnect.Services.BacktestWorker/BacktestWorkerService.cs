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
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Defaults;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.BacktestWorker;

/// <summary>Consumes <c>jobs.backtest</c> with prefetch 1: one job per worker at a time, acked by <see cref="BacktestJobRunner"/> on completion.</summary>
public sealed class BacktestWorkerService : BackgroundService
{
    private readonly IMessageConsumer _consumer;
    private readonly BacktestJobRunner _runner;
    private readonly ReadinessGate _readiness;
    private readonly ILogger<BacktestWorkerService> _logger;

    public BacktestWorkerService(IMessageConsumer consumer, BacktestJobRunner runner, ReadinessGate readiness, ILogger<BacktestWorkerService> logger)
    {
        _consumer = consumer;
        _runner = runner;
        _readiness = readiness;
        _logger = logger;
    }

    public static readonly ConsumerOptions Consumer = new() { Prefetch = 1 };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = Queues.Dispatch(JobKind.Backtest);
        IAsyncDisposable? subscription = null;
        while (subscription == null)
        {
            try
            {
                subscription = await _consumer.ConsumeAsync(queue, _runner.HandleAsync, Consumer, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "Cannot consume {Queue} yet; retrying", queue);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        _logger.LogInformation("Worker {WorkerId} consuming {Queue} (prefetch {Prefetch})", _runner.WorkerId, queue, Consumer.Prefetch);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _readiness.BeginDrain();
        await subscription.DisposeAsync().ConfigureAwait(false);
    }
}
