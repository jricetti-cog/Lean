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
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.BacktestWorker;

/// <summary>
/// The per-attempt work folder of one child Launcher: <c>job.json</c> (read by <c>BusJobQueueHandler</c>), a
/// <c>config.json</c> derived from the Launcher's own with the bus handlers selected, and the folders the engine writes to.
/// </summary>
public sealed class JobWorkspace
{
    public const string JobQueueHandler = "QuantConnect.Services.EngineHandlers.BusJobQueueHandler";
    public const string MessagingHandler = "QuantConnect.Services.EngineHandlers.BusMessagingHandler";

    private static readonly JsonDocumentOptions ConfigReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions ConfigWriteOptions = new() { WriteIndented = true };

    private JobWorkspace(string directory, Guid jobId)
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
    /// <summary>Where LEAN stores the final result (<c>results-destination-folder/&lt;algorithm-id&gt;.json</c>, algorithm id = job id).</summary>
    public string ResultPath { get; }
    public string StoragePath { get; }
    public string AlgorithmPath { get; }

    public static JobWorkspace Create(WorkerOptions options, JobSubmitted job, string workerId)
    {
        var workspace = new JobWorkspace(Path.Combine(options.WorkRoot, job.JobId.ToString("D"), job.Attempt.ToString()), job.JobId);
        if (System.IO.Directory.Exists(workspace.Directory))
        {
            System.IO.Directory.Delete(workspace.Directory, recursive: true);
        }
        System.IO.Directory.CreateDirectory(workspace.StoragePath);
        System.IO.Directory.CreateDirectory(workspace.AlgorithmPath);
        File.WriteAllText(workspace.JobPath, ContractJson.Serialize(job));
        File.WriteAllText(workspace.ConfigPath, BuildConfig(File.ReadAllText(options.LauncherConfigPath), workspace.ConfigOverrides(options, workerId)));
        return workspace;
    }

    /// <summary>The Launcher's <c>config.json</c> (comments allowed) with <paramref name="overrides"/> set at the top level.</summary>
    public static string BuildConfig(string launcherConfig, IReadOnlyDictionary<string, JsonNode?> overrides)
    {
        var config = JsonNode.Parse(launcherConfig, documentOptions: ConfigReadOptions)?.AsObject()
            ?? throw new InvalidDataException("The Launcher config.json is not a JSON object");
        foreach (var (key, value) in overrides)
        {
            config[key] = value?.DeepClone();
        }
        return config.ToJsonString(ConfigWriteOptions);
    }

    public IReadOnlyDictionary<string, JsonNode?> ConfigOverrides(WorkerOptions options, string workerId) => new Dictionary<string, JsonNode?>
    {
        ["environment"] = "backtesting",
        ["job-queue-handler"] = JobQueueHandler,
        ["messaging-handler"] = MessagingHandler,
        ["close-automatically"] = true,
        ["data-folder"] = options.DataFolder,
        ["composer-dll-directory"] = options.LauncherDirectory,
        ["results-destination-folder"] = Directory,
        ["object-store-root"] = StoragePath,
        ["bus-job-file"] = JobPath,
        ["bus-worker-id"] = workerId,
        ["bus-work-folder"] = AlgorithmPath,
        ["bus-builtin-algorithm-location"] = options.BuiltinAlgorithmLocation,
        ["bus-traceparent"] = System.Diagnostics.Activity.Current?.Id ?? "",
    };

    /// <summary>Points the engine at the worker-compiled assembly of an uploaded C# algorithm (<c>BusJobQueueHandler</c> reads these keys).</summary>
    public void SetCompiledAlgorithm(string assemblyPath, string typeName) =>
        File.WriteAllText(ConfigPath, BuildConfig(File.ReadAllText(ConfigPath), new Dictionary<string, JsonNode?>
        {
            [CompiledAlgorithmLocation] = assemblyPath,
            [CompiledAlgorithmTypeName] = typeName,
        }));

    public const string CompiledAlgorithmLocation = "bus-compiled-algorithm-location";
    public const string CompiledAlgorithmTypeName = "bus-compiled-algorithm-type-name";

    public void Delete()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            var jobFolder = Path.GetDirectoryName(Directory);
            if (jobFolder != null && System.IO.Directory.Exists(jobFolder) && System.IO.Directory.GetFileSystemEntries(jobFolder).Length == 0)
            {
                System.IO.Directory.Delete(jobFolder);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
