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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;

namespace QuantConnect.Services.ApiGateway
{
    /// <summary>
    /// Owns the job queue: accepts submissions, leases jobs to workers (<c>jobs.v1.next</c>),
    /// tracks status (<c>jobs.v1.status</c>) and engine events (<c>jobs.v1.events.{jobId}</c>)
    /// </summary>
    public class JobDispatcher : IDisposable
    {
        private const int MaxEventsPerJob = 1000;
        private readonly object _lock = new();
        private readonly List<JobRecord> _queue = new();
        private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();
        private readonly ConcurrentDictionary<string, List<JobEvent>> _events = new();
        private readonly ConcurrentDictionary<string, DateTime> _workers = new();
        private readonly List<IDisposable> _registrations = new();
        private readonly IMessageBus _bus;

        /// <summary>
        /// Raised for every job event and status change
        /// </summary>
        public event Action<JobEvent> JobEventReceived;

        /// <summary>
        /// Workers seen recently, by id
        /// </summary>
        public IReadOnlyDictionary<string, DateTime> Workers => _workers;

        /// <summary>
        /// Creates the dispatcher and registers the bus handlers
        /// </summary>
        public JobDispatcher(IMessageBus bus)
        {
            _bus = bus;
            _registrations.Add(bus.Respond<JobLeaseRequest, JobLeaseResponse>(Topics.JobsNext, (request, _) => Lease(request)));
            _registrations.Add(bus.Subscribe<JobStatusUpdate>(Topics.JobsStatus, (update, _) => OnStatus(update)));
            _registrations.Add(bus.Subscribe<JobEvent>(Topics.JobsEventsPrefix, (jobEvent, _) => OnEvent(jobEvent)));
        }

        /// <summary>
        /// Queues a job
        /// </summary>
        public JobRecord Submit(string userId, JobSubmission submission)
        {
            Validate(submission);
            var job = new JobRecord
            {
                JobId = $"{submission.Type.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                UserId = userId,
                Submission = submission,
                Status = JobStatus.Queued,
                SubmittedUtc = DateTime.UtcNow
            };
            _jobs[job.JobId] = job;
            lock (_lock)
            {
                _queue.Add(job);
            }
            Log.Trace($"JobDispatcher.Submit(): {job.JobId} {submission.Type} {submission.AlgorithmTypeName ?? submission.Notebook}");
            Raise(job, "queued");
            return job;
        }

        /// <summary>
        /// Gets a job
        /// </summary>
        public JobRecord Get(string jobId)
        {
            return _jobs.TryGetValue(jobId, out var job) ? job : null;
        }

        /// <summary>
        /// Lists jobs, newest first
        /// </summary>
        public IEnumerable<JobRecord> List(string userId = null)
        {
            return _jobs.Values.Where(j => userId == null || j.UserId == userId).OrderByDescending(j => j.SubmittedUtc);
        }

        /// <summary>
        /// Events recorded for a job
        /// </summary>
        public IReadOnlyList<JobEvent> Events(string jobId)
        {
            if (!_events.TryGetValue(jobId, out var events))
            {
                return Array.Empty<JobEvent>();
            }
            lock (events)
            {
                return events.ToList();
            }
        }

        /// <summary>
        /// Cancels a job: queued jobs are removed, running jobs are stopped by their worker
        /// </summary>
        public JobRecord Cancel(string jobId)
        {
            var job = Get(jobId);
            if (job == null)
            {
                return null;
            }
            lock (_lock)
            {
                if (_queue.Remove(job))
                {
                    job.Status = JobStatus.Cancelled;
                    job.CompletedUtc = DateTime.UtcNow;
                }
            }
            if (job.Status != JobStatus.Cancelled)
            {
                _bus.Publish(Topics.JobsCancel, new JobCancelRequest { JobId = jobId }, ContractVersions.JobsV1);
            }
            Raise(job, "cancel requested");
            return job;
        }

        private JobLeaseResponse Lease(JobLeaseRequest request)
        {
            _workers[request.WorkerId] = DateTime.UtcNow;
            lock (_lock)
            {
                var job = _queue.FirstOrDefault(j => request.JobTypes == null || request.JobTypes.Count == 0 || request.JobTypes.Contains(j.Submission.Type));
                if (job == null)
                {
                    return new JobLeaseResponse();
                }
                _queue.Remove(job);
                job.Status = JobStatus.Assigned;
                job.WorkerId = request.WorkerId;
                job.AssignedUtc = DateTime.UtcNow;
                Raise(job, $"assigned to {request.WorkerId}");
                return new JobLeaseResponse { Job = job };
            }
        }

        private void OnStatus(JobStatusUpdate update)
        {
            var job = Get(update.JobId);
            if (job == null)
            {
                return;
            }
            job.Status = update.Status;
            job.Message = update.Message ?? job.Message;
            job.ExitCode = update.ExitCode ?? job.ExitCode;
            if (update.Status == JobStatus.Running)
            {
                job.StartedUtc ??= DateTime.UtcNow;
            }
            else if (update.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            {
                job.CompletedUtc = DateTime.UtcNow;
            }
            Raise(job, update.Message);
        }

        private void OnEvent(JobEvent jobEvent)
        {
            var job = Get(jobEvent.JobId);
            if (job == null)
            {
                return;
            }
            if (jobEvent.Progress.HasValue)
            {
                job.Progress = jobEvent.Progress.Value;
            }
            if (jobEvent.Statistics != null && jobEvent.Statistics.Count > 0)
            {
                job.Statistics = jobEvent.Statistics;
            }
            Record(jobEvent);
        }

        private void Raise(JobRecord job, string message)
        {
            Record(new JobEvent { JobId = job.JobId, PacketType = "Status", Message = $"{job.Status}{(string.IsNullOrEmpty(message) ? string.Empty : ": " + message)}" });
        }

        private void Record(JobEvent jobEvent)
        {
            var events = _events.GetOrAdd(jobEvent.JobId, _ => new List<JobEvent>());
            lock (events)
            {
                events.Add(jobEvent);
                if (events.Count > MaxEventsPerJob)
                {
                    events.RemoveAt(0);
                }
            }
            JobEventReceived?.Invoke(jobEvent);
        }

        private static void Validate(JobSubmission submission)
        {
            if (submission == null)
            {
                throw new ArgumentException("A job submission body is required");
            }
            if (submission.Type == JobType.Research)
            {
                if (string.IsNullOrEmpty(submission.Notebook))
                {
                    throw new ArgumentException("Research jobs require 'notebook'");
                }
            }
            else if (string.IsNullOrEmpty(submission.AlgorithmTypeName))
            {
                throw new ArgumentException("Backtest and live jobs require 'algorithmTypeName'");
            }
        }

        /// <summary>
        /// Unregisters the bus handlers
        /// </summary>
        public void Dispose()
        {
            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
