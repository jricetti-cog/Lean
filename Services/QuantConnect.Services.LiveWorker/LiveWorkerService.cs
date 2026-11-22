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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantConnect.Services.Defaults;

namespace QuantConnect.Services.LiveWorker;

/// <summary>Runs the container's deployment once, then stops the host with <see cref="LiveJobRunner"/>'s exit code.</summary>
public sealed class LiveWorkerService : BackgroundService
{
    private readonly LiveJobRunner _runner;
    private readonly LiveWorkerOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ReadinessGate _readiness;
    private readonly ILogger<LiveWorkerService> _logger;

    public LiveWorkerService(LiveJobRunner runner, IOptions<LiveWorkerOptions> options, IHostApplicationLifetime lifetime, ReadinessGate readiness,
        ILogger<LiveWorkerService> logger)
    {
        _runner = runner;
        _options = options.Value;
        _lifetime = lifetime;
        _readiness = readiness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int exitCode;
        try
        {
            exitCode = await _runner.RunAsync(LiveJobRunner.ParseDispatch(_options.Dispatch), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "live-worker {WorkerId} failed", _runner.WorkerId);
            exitCode = LiveJobRunner.ExitRestart;
        }
        Environment.ExitCode = exitCode;
        _readiness.BeginDrain();
        _lifetime.StopApplication();
    }
}
