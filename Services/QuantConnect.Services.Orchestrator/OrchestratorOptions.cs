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

namespace QuantConnect.Services.Orchestrator;

/// <summary>The <c>Orchestrator</c> configuration section.</summary>
public sealed class OrchestratorOptions
{
    public const string Section = "Orchestrator";

    /// <summary>Attempts per job, including the first; a worker death past this fails the job with <c>max-attempts-exceeded</c>.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>A dispatched/running attempt with no heartbeat for this long is considered dead.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 90;

    /// <summary>Heartbeat interval handed to workers (ADR 0002: 15 s).</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    /// <summary>How often the heartbeat/deadline monitor scans.</summary>
    public int MonitorIntervalSeconds { get; set; } = 5;

    /// <summary>How often the outbox relay polls for unsent rows (it is also woken on every write).</summary>
    public int OutboxPollMilliseconds { get; set; } = 1000;

    /// <summary>Default <c>maxRuntimeSeconds</c> of a backtest when the request has none (ADR 0002: 6 h).</summary>
    public int DefaultBacktestMaxRuntimeSeconds { get; set; } = 6 * 3600;

    /// <summary>Jobs (ready + unacked) one worker replica is expected to absorb when suggesting replicas.</summary>
    public int JobsPerReplica { get; set; } = 1;

    public int MinReplicas { get; set; }

    public int MaxReplicas { get; set; } = 20;

    /// <summary>Base URL of the results service, used to resolve the source result of a report job.</summary>
    public string? ResultsUrl { get; set; }

    public TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(HeartbeatTimeoutSeconds);
}
