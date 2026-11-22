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

using QuantConnect.Services.BacktestWorker;

namespace QuantConnect.Services.LiveWorker;

/// <summary><c>LiveWorker__*</c> settings; engine paths come from the shared <c>Worker__*</c> section (<see cref="WorkerOptions"/>).</summary>
public sealed class LiveWorkerOptions
{
    public const string Section = "LiveWorker";

    /// <summary>Lease holder id; job-orchestrator's live deployer sets the container name, which survives container restarts.</summary>
    public string? WorkerId { get; set; }
    /// <summary>The <c>JobSubmitted</c> of the deployment this container runs (set by job-orchestrator when it creates the container).</summary>
    public string? Dispatch { get; set; }
    public Uri OrchestratorUri { get; set; } = new("http://localhost:5081/");
    public Uri ResultsUri { get; set; } = new("http://localhost:5082/");
    /// <summary>Worker-side heartbeat interval when the lease does not say otherwise (live default 10 s, ADR 0002).</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;
    /// <summary>Time a liquidation gets to fill before a <c>liquidate: true</c> stop quits the algorithm.</summary>
    public int LiquidateGraceSeconds { get; set; } = 10;
    /// <summary>Time the algorithm gets to stop after the quit command before the engine is killed.</summary>
    public int StopGraceSeconds { get; set; } = 30;
    public int PollIntervalMilliseconds { get; set; } = 500;
    /// <summary>Restore holdings and cash from results' <c>live-state</c> when the engine starts.</summary>
    public bool RestoreState { get; set; } = true;
    public bool KeepWorkDirectory { get; set; }

    public string ResolvedWorkerId => string.IsNullOrWhiteSpace(WorkerId) ? Environment.MachineName : WorkerId!;
}
