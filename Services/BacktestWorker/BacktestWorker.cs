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
 *
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using QuantConnect.Lean.Engine.DataFeeds.Remote;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;

namespace QuantConnect.Services.BacktestWorker
{
    /// <summary>
    /// Settings of a <see cref="BacktestWorker"/>
    /// </summary>
    public class BacktestWorkerSettings
    {
        /// <summary>Worker id</summary>
        public string WorkerId { get; set; } = "worker-" + Environment.MachineName;
        /// <summary>Folder containing QuantConnect.Lean.Launcher.dll</summary>
        public string LauncherDirectory { get; set; }
        /// <summary>LEAN configuration used as template for every job</summary>
        public string LeanConfigTemplate { get; set; }
        /// <summary>Local data cache, used as the LEAN data folder</summary>
        public string CacheFolder { get; set; }
        /// <summary>Folder receiving one sub folder per job (config, log, results)</summary>
        public string ResultsFolder { get; set; }
        /// <summary>Job types this worker accepts</summary>
        public List<JobType> JobTypes { get; set; } = new() { JobType.Backtest, JobType.Live, JobType.Research };
        /// <summary>Data folders copied from the Market Data Service before running jobs, read by LEAN straight from disk</summary>
        public List<string> ReferenceDataPrefixes { get; set; } = RemoteDataProvider.DefaultReferenceData.ToList();
        /// <summary>Folder notebooks are resolved from</summary>
        public string ResearchFolder { get; set; }
        /// <summary>Executable used to run notebooks</summary>
        public string ResearchCommand { get; set; } = "jupyter";
        /// <summary>Settings copied into every LEAN job configuration (message bus addresses)</summary>
        public Dictionary<string, string> EngineSettings { get; set; } = new();
        /// <summary>Delay between lease requests when the queue is empty</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Leases jobs from the gateway over the message bus and runs each one in its own LEAN process
    /// (<c>QuantConnect.Lean.Launcher</c>). The whole backtest loop runs inside that process; only raw data
    /// file reads (<see cref="RemoteDataProvider"/>), live data (<see cref="RemoteDataQueueHandler"/>) and
    /// result packets (<see cref="BusMessagingHandler"/>) cross the bus.
    /// </summary>
    public class BacktestWorker
    {
        private readonly IMessageBus _bus;
        private readonly BacktestWorkerSettings _settings;
        private readonly object _processLock = new();
        private string _currentJobId;
        private Process _currentProcess;
        private bool _cancelRequested;
        private bool _referenceDataSynchronized;

        /// <summary>
        /// Creates a new worker
        /// </summary>
        public BacktestWorker(IMessageBus bus, BacktestWorkerSettings settings)
        {
            _bus = bus;
            _settings = settings;
        }

        /// <summary>
        /// Leases and runs jobs until cancelled
        /// </summary>
        public void Run(CancellationToken cancellationToken)
        {
            using var cancelSubscription = _bus.Subscribe<JobCancelRequest>(Topics.JobsCancel, (request, _) => Cancel(request.JobId));
            Log.Trace($"BacktestWorker.Run(): {_settings.WorkerId} accepting {string.Join(", ", _settings.JobTypes)}");
            while (!cancellationToken.IsCancellationRequested)
            {
                JobRecord job = null;
                try
                {
                    job = _bus.Request<JobLeaseRequest, JobLeaseResponse>(Topics.JobsNext,
                        new JobLeaseRequest { WorkerId = _settings.WorkerId, JobTypes = _settings.JobTypes },
                        ContractVersions.JobsV1, TimeSpan.FromSeconds(5), cancellationToken)?.Job;
                }
                catch (MessageBusException exception)
                {
                    Log.Debug($"BacktestWorker.Run(): lease failed: {exception.Message}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (job == null)
                {
                    cancellationToken.WaitHandle.WaitOne(_settings.PollInterval);
                    continue;
                }
                Execute(job);
            }
        }

        /// <summary>
        /// Runs a job to completion and reports its status
        /// </summary>
        public JobStatus Execute(JobRecord job)
        {
            var jobFolder = Path.GetFullPath(Path.Combine(_settings.ResultsFolder, job.JobId));
            Directory.CreateDirectory(jobFolder);
            Publish(job, JobStatus.Running, $"running on {_settings.WorkerId}");
            try
            {
                if (!_referenceDataSynchronized)
                {
                    var files = RemoteDataProvider.SynchronizeFolders(_bus, _settings.CacheFolder, _settings.ReferenceDataPrefixes.ToArray());
                    Log.Trace($"BacktestWorker.Execute(): synchronized {files} reference data files into {_settings.CacheFolder}");
                    _referenceDataSynchronized = true;
                }

                var startInfo = job.Submission.Type == JobType.Research ? CreateResearchProcess(job, jobFolder) : CreateEngineProcess(job, jobFolder);
                var exitCode = RunProcess(job.JobId, startInfo, Path.Combine(jobFolder, "log.txt"));
                var status = _cancelRequested ? JobStatus.Cancelled : exitCode == 0 ? JobStatus.Completed : JobStatus.Failed;
                Publish(job, status, $"exit code {exitCode}", exitCode);
                return status;
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"BacktestWorker.Execute({job.JobId})");
                Publish(job, JobStatus.Failed, exception.Message);
                return JobStatus.Failed;
            }
        }

        /// <summary>
        /// Writes the LEAN configuration of a job: the template plus the job settings and the remote service handlers
        /// </summary>
        public string WriteEngineConfiguration(JobRecord job, string jobFolder)
        {
            var config = JObject.Parse(File.ReadAllText(_settings.LeanConfigTemplate));
            var submission = job.Submission;
            var live = submission.Type == JobType.Live;

            config["environment"] = live ? "live-paper" : "backtesting";
            config["algorithm-type-name"] = submission.AlgorithmTypeName;
            config["algorithm-language"] = submission.AlgorithmLanguage.ToString();
            config["algorithm-location"] = submission.AlgorithmLocation ?? (submission.AlgorithmLanguage == Language.Python
                ? Path.Combine("..", "..", "..", "Algorithm.Python", submission.AlgorithmTypeName + ".py")
                : "QuantConnect.Algorithm.CSharp.dll");
            config["algorithm-id"] = job.JobId;
            config["job-id"] = job.JobId;
            config["backtest-name"] = submission.Name ?? job.JobId;
            config["parameters"] = JObject.FromObject(submission.Parameters ?? new Dictionary<string, string>());
            config["close-automatically"] = true;
            config["data-folder"] = Path.GetFullPath(_settings.CacheFolder) + Path.DirectorySeparatorChar;
            config["results-destination-folder"] = jobFolder;
            config["object-store-root"] = Path.Combine(jobFolder, "storage");
            config["messaging-handler"] = typeof(BusMessagingHandler).FullName;
            config["data-provider"] = typeof(RemoteDataProvider).FullName;
            if (live)
            {
                config["environments"]["live-paper"]["data-queue-handler"] = new JArray(typeof(RemoteDataQueueHandler).FullName);
            }
            foreach (var setting in _settings.EngineSettings)
            {
                config[setting.Key] = setting.Value;
            }

            var path = Path.Combine(jobFolder, "config.json");
            File.WriteAllText(path, config.ToString());
            return path;
        }

        private ProcessStartInfo CreateEngineProcess(JobRecord job, string jobFolder)
        {
            var configPath = WriteEngineConfiguration(job, jobFolder);
            var startInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = _settings.LauncherDirectory };
            startInfo.ArgumentList.Add("QuantConnect.Lean.Launcher.dll");
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(configPath);
            return startInfo;
        }

        private ProcessStartInfo CreateResearchProcess(JobRecord job, string jobFolder)
        {
            var startInfo = new ProcessStartInfo(_settings.ResearchCommand) { WorkingDirectory = _settings.ResearchFolder };
            foreach (var argument in new[] { "nbconvert", "--to", "notebook", "--execute", job.Submission.Notebook, "--output-dir", jobFolder })
            {
                startInfo.ArgumentList.Add(argument);
            }
            foreach (var parameter in job.Submission.Parameters ?? new Dictionary<string, string>())
            {
                startInfo.Environment["QC_PARAMETER_" + parameter.Key.ToUpperInvariant()] = parameter.Value;
            }
            return startInfo;
        }

        private int RunProcess(string jobId, ProcessStartInfo startInfo, string logPath)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            using var log = new StreamWriter(logPath) { AutoFlush = true };
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, args) => { if (args.Data != null) { lock (log) { log.WriteLine(args.Data); } } };
            process.ErrorDataReceived += (_, args) => { if (args.Data != null) { lock (log) { log.WriteLine(args.Data); } } };
            lock (_processLock)
            {
                _cancelRequested = false;
                _currentJobId = jobId;
                _currentProcess = process;
                process.Start();
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            lock (_processLock)
            {
                _currentJobId = null;
                _currentProcess = null;
            }
            return process.ExitCode;
        }

        private void Cancel(string jobId)
        {
            lock (_processLock)
            {
                if (_currentJobId == jobId && _currentProcess != null)
                {
                    Log.Trace($"BacktestWorker.Cancel(): stopping {jobId}");
                    _cancelRequested = true;
                    _currentProcess.Kill(true);
                }
            }
        }

        private void Publish(JobRecord job, JobStatus status, string message, int? exitCode = null)
        {
            _bus.Publish(Topics.JobsStatus, new JobStatusUpdate { JobId = job.JobId, WorkerId = _settings.WorkerId, Status = status, Message = message, ExitCode = exitCode },
                ContractVersions.JobsV1);
        }
    }
}
