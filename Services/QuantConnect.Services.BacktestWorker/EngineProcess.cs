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
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace QuantConnect.Services.BacktestWorker;

/// <summary>What to run for one job attempt: the stock Launcher with the attempt's <c>config.json</c>.</summary>
public sealed record EngineStartInfo
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string LogPath { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>A running child engine.</summary>
public interface IEngineProcess : IAsyncDisposable
{
    int Id { get; }
    /// <summary>Completes with the exit code once the process has exited and its output is flushed to the log.</summary>
    Task<int> Exited { get; }
    /// <summary>Current resident set size; zero once exited.</summary>
    long RssBytes { get; }
    /// <summary>Kills the process tree (Python included).</summary>
    void Kill();
}

public interface IEngineLauncher
{
    IEngineProcess Start(EngineStartInfo info);
}

/// <summary>Starts each job's engine as a separate OS process, so LEAN's static state dies with it and a crash cannot reach the worker.</summary>
public sealed class ProcessEngineLauncher : IEngineLauncher
{
    private readonly ILogger<ProcessEngineLauncher> _logger;

    public ProcessEngineLauncher(ILogger<ProcessEngineLauncher> logger) => _logger = logger;

    public IEngineProcess Start(EngineStartInfo info)
    {
        var startInfo = new ProcessStartInfo(info.FileName)
        {
            WorkingDirectory = info.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in info.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in info.Environment)
        {
            startInfo.Environment[key] = value;
        }
        return new ChildEngineProcess(startInfo, info.LogPath, _logger);
    }

    private sealed class ChildEngineProcess : IEngineProcess
    {
        private readonly Process _process;
        private readonly StreamWriter _log;
        private readonly object _gate = new();

        public ChildEngineProcess(ProcessStartInfo startInfo, string logPath, ILogger logger)
        {
            _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => Write(e.Data);
            _process.ErrorDataReceived += (_, e) => Write(e.Data);
            _process.Start();
            Id = _process.Id;
            _process.StandardInput.Close();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Exited = WaitAsync();
            logger.LogDebug("Started engine process {Pid}", Id);
        }

        public int Id { get; }
        public Task<int> Exited { get; }

        public long RssBytes
        {
            get
            {
                try
                {
                    if (_process.HasExited) return 0;
                    _process.Refresh();
                    return _process.WorkingSet64;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return 0;
                }
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private async Task<int> WaitAsync()
        {
            // The parameterless WaitForExitAsync also waits for the redirected output to reach EOF.
            await _process.WaitForExitAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _log.Flush();
            }
            return _process.ExitCode;
        }

        private void Write(string? line)
        {
            if (line == null) return;
            lock (_gate)
            {
                _log.WriteLine(line);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!Exited.IsCompleted)
            {
                Kill();
                try
                {
                    await Exited.ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }
            lock (_gate)
            {
                _log.Dispose();
            }
            _process.Dispose();
        }
    }
}
