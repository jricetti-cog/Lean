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
using System.Text.Json.Nodes;
using QuantConnect.Services.BacktestWorker;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.LiveWorker;

/// <summary>
/// Work folder of the live engine: <c>job.json</c>, a <c>config.json</c> derived from the Launcher's own with live mode,
/// <c>BrokerageSetupHandler</c>, <c>LiveTradingResultHandler</c>, the live data feed / real-time handlers and the bus handlers
/// selected at the top level (the <c>environment</c> names no section, so no <c>live-*</c> block overrides them), and the
/// restored portfolio as PaperBrokerage's <c>live-holdings</c> / <c>live-cash-balance</c>. It is also the engine's working
/// directory, where <c>FileCommandHandler</c> reads the command files.
/// </summary>
public sealed class LiveWorkspace
{
    public const string Environment = "live-worker";
    public const string PaperBrokerage = "PaperBrokerage";

    private LiveWorkspace(string directory, Guid jobId)
    {
        Directory = directory;
        JobPath = Path.Combine(directory, "job.json");
        ConfigPath = Path.Combine(directory, "config.json");
        LogPath = Path.Combine(directory, "engine.log");
        ResultPath = Path.Combine(directory, $"{jobId:D}.json");
        StoragePath = Path.Combine(directory, "storage");
        AlgorithmPath = Path.Combine(directory, "algorithm");
    }

    public string Directory { get; }
    public string JobPath { get; }
    public string ConfigPath { get; }
    public string LogPath { get; }
    public string ResultPath { get; }
    public string StoragePath { get; }
    public string AlgorithmPath { get; }

    public static LiveWorkspace Create(WorkerOptions options, JobSubmitted job, string workerId, long leaseEpoch, LiveState? restored)
    {
        var workspace = new LiveWorkspace(Path.Combine(options.WorkRoot, job.JobId.ToString("D"), "live"), job.JobId);
        if (System.IO.Directory.Exists(workspace.Directory))
        {
            System.IO.Directory.Delete(workspace.Directory, recursive: true);
        }
        System.IO.Directory.CreateDirectory(workspace.StoragePath);
        System.IO.Directory.CreateDirectory(workspace.AlgorithmPath);
        File.WriteAllText(workspace.JobPath, ContractJson.Serialize(job));
        File.WriteAllText(workspace.ConfigPath, JobWorkspace.BuildConfig(File.ReadAllText(options.LauncherConfigPath),
            workspace.ConfigOverrides(options, job, workerId, leaseEpoch, restored)));
        return workspace;
    }

    public IReadOnlyDictionary<string, JsonNode?> ConfigOverrides(WorkerOptions options, JobSubmitted job, string workerId, long leaseEpoch, LiveState? restored)
    {
        var live = job.Live ?? throw new InvalidOperationException("not a live job");
        var paper = string.Equals(live.Brokerage, PaperBrokerage, StringComparison.OrdinalIgnoreCase);
        var queues = new JsonArray();
        foreach (var handler in live.DataQueueHandlers is { Count: > 0 } handlers ? handlers : new List<string> { "QuantConnect.Services.EngineHandlers.BusDataQueueHandler" })
        {
            queues.Add(handler);
        }
        return new Dictionary<string, JsonNode?>
        {
            ["environment"] = Environment,
            ["live-mode"] = true,
            ["live-mode-brokerage"] = live.Brokerage,
            ["setup-handler"] = "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler",
            ["result-handler"] = "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler",
            ["data-feed-handler"] = "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed",
            ["real-time-handler"] = "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler",
            // PaperBrokerage fills through the backtesting transaction handler; real brokerages need the brokerage one.
            ["transaction-handler"] = paper
                ? "QuantConnect.Lean.Engine.TransactionHandlers.BacktestingTransactionHandler"
                : "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler",
            ["data-queue-handler"] = queues,
            ["job-queue-handler"] = JobWorkspace.JobQueueHandler,
            ["messaging-handler"] = JobWorkspace.MessagingHandler,
            ["close-automatically"] = true,
            ["data-folder"] = options.DataFolder,
            ["composer-dll-directory"] = options.LauncherDirectory,
            ["results-destination-folder"] = Directory,
            ["object-store-root"] = StoragePath,
            ["bus-job-file"] = JobPath,
            ["bus-worker-id"] = workerId,
            ["bus-work-folder"] = AlgorithmPath,
            ["bus-builtin-algorithm-location"] = options.BuiltinAlgorithmLocation,
            ["bus-lease-epoch"] = leaseEpoch.ToString(),
            ["bus-traceparent"] = System.Diagnostics.Activity.Current?.Id ?? "",
            ["live-holdings"] = HoldingsConfig(restored),
            ["live-cash-balance"] = CashConfig(restored),
        };
    }

    /// <summary>LEAN <c>List&lt;Holding&gt;</c> JSON (the holdings of the last <c>LiveResult</c> as LEAN serialized them).</summary>
    public static string HoldingsConfig(LiveState? state)
    {
        var holdings = new JsonArray();
        foreach (var holding in state?.Holdings ?? new List<JsonObject>())
        {
            holdings.Add(holding.DeepClone());
        }
        return holdings.ToJsonString();
    }

    /// <summary>LEAN <c>List&lt;CashAmount&gt;</c> JSON; empty when there is nothing to restore so the algorithm's own cash applies.</summary>
    public static string CashConfig(LiveState? state)
    {
        if (state is not { Cash.Count: > 0 }) return string.Empty;
        var cash = new JsonArray();
        foreach (var amount in state.Cash)
        {
            cash.Add(new JsonObject { ["Amount"] = amount.Amount, ["Currency"] = amount.Currency });
        }
        return cash.ToJsonString();
    }

    public EngineStartInfo StartInfo(WorkerOptions options, IReadOnlyDictionary<string, string> environment) => new()
    {
        FileName = options.DotnetPath,
        Arguments = new[] { options.LauncherPath, "--config", ConfigPath },
        WorkingDirectory = Directory,
        LogPath = LogPath,
        Environment = environment,
    };

    public void Delete()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
