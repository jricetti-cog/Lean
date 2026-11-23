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
using System.Collections.Generic;
using System.IO;

namespace QuantConnect.Services.BacktestWorker;

/// <summary><c>Worker__*</c> settings of backtest-worker.</summary>
public sealed class WorkerOptions
{
    public const string Section = "Worker";

    /// <summary>Reported in <c>JobStarted</c>/<c>JobHeartbeat</c>; defaults to the host name (the container id under Compose).</summary>
    public string? WorkerId { get; set; }
    public Uri OrchestratorUri { get; set; } = new("http://localhost:5081/");
    /// <summary>Folder holding <c>QuantConnect.Lean.Launcher.dll</c>, its <c>config.json</c> and the bus engine handlers.</summary>
    public string LauncherDirectory { get; set; } = "/Lean/Launcher/bin/Release";
    public string DotnetPath { get; set; } = "dotnet";
    /// <summary>The shared <c>lean-data</c> volume, mounted read-only.</summary>
    public string DataFolder { get; set; } = "/Lean/Data/";
    /// <summary>Writable root of the per-attempt work folders (results, logs, object store, materialized algorithms).</summary>
    public string WorkRoot { get; set; } = "/work";
    /// <summary>RAM limit of a job whose <c>backtest.maxRamMb</c> is absent.</summary>
    public int DefaultRamMb { get; set; } = 4096;
    /// <summary>Wall-clock limit of a job whose dispatch has neither <c>maxRuntimeSeconds</c> nor a deadline (job-orchestrator's default).</summary>
    public int DefaultMaxRuntimeSeconds { get; set; } = 6 * 3600;
    /// <summary>Process RSS allowed above the job's RAM limit (runtime, Python, native buffers) before the child is killed.</summary>
    public int MemoryHeadroomMb { get; set; } = 1024;
    /// <summary>Worker-side <c>JobHeartbeat</c> interval; the engine publishes its own heartbeats too.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public bool KeepWorkDirectories { get; set; }
    /// <summary>
    /// Extra Launcher <c>config.json</c> keys for every attempt (<c>Worker__EngineConfig__&lt;key&gt;</c>), e.g. the parity stack's
    /// <c>fundamental-data-provider</c>; the keys the worker sets to wire the bus handlers win over these. Empty by default.
    /// </summary>
    public Dictionary<string, string> EngineConfig { get; set; } = new(StringComparer.Ordinal);

    public string ResolvedWorkerId => string.IsNullOrWhiteSpace(WorkerId) ? Environment.MachineName : WorkerId!;
    public string LauncherPath => Path.Combine(LauncherDirectory, "QuantConnect.Lean.Launcher.dll");
    public string LauncherConfigPath => Path.Combine(LauncherDirectory, "config.json");
    public string BuiltinAlgorithmLocation => Path.Combine(LauncherDirectory, "QuantConnect.Algorithm.CSharp.dll");
}
