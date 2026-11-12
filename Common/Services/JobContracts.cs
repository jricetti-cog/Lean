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

namespace QuantConnect.Services
{
    /// <summary>
    /// The kind of engine job
    /// </summary>
    public enum JobType
    {
        /// <summary>Historical backtest</summary>
        Backtest,
        /// <summary>Live (paper) trading deployment</summary>
        Live,
        /// <summary>Research notebook execution</summary>
        Research
    }

    /// <summary>
    /// Job lifecycle status
    /// </summary>
    public enum JobStatus
    {
        /// <summary>Waiting for a worker</summary>
        Queued,
        /// <summary>Leased by a worker, not started yet</summary>
        Assigned,
        /// <summary>Running on a worker</summary>
        Running,
        /// <summary>Finished successfully</summary>
        Completed,
        /// <summary>Finished with an error</summary>
        Failed,
        /// <summary>Cancelled by the user</summary>
        Cancelled
    }

    /// <summary>
    /// A job request submitted through the gateway
    /// </summary>
    public class JobSubmission
    {
        /// <summary>The job type</summary>
        public JobType Type { get; set; } = JobType.Backtest;

        /// <summary>Display name</summary>
        public string Name { get; set; }

        /// <summary>Algorithm class name (backtest/live)</summary>
        public string AlgorithmTypeName { get; set; }

        /// <summary>Algorithm language</summary>
        public Language AlgorithmLanguage { get; set; } = Language.CSharp;

        /// <summary>Algorithm location on the worker, defaults to the bundled algorithm library of the language</summary>
        public string AlgorithmLocation { get; set; }

        /// <summary>Notebook to execute (research), relative to the worker research folder</summary>
        public string Notebook { get; set; }

        /// <summary>Algorithm parameters</summary>
        public Dictionary<string, string> Parameters { get; set; } = new();
    }

    /// <summary>
    /// The state of a job
    /// </summary>
    public class JobRecord
    {
        /// <summary>Job id, also used as the LEAN algorithm id</summary>
        public string JobId { get; set; }

        /// <summary>The user that submitted the job</summary>
        public string UserId { get; set; }

        /// <summary>The submission</summary>
        public JobSubmission Submission { get; set; }

        /// <summary>Current status</summary>
        public JobStatus Status { get; set; }

        /// <summary>The worker running the job</summary>
        public string WorkerId { get; set; }

        /// <summary>Submission time</summary>
        public DateTime SubmittedUtc { get; set; }

        /// <summary>Lease time</summary>
        public DateTime? AssignedUtc { get; set; }

        /// <summary>Start time</summary>
        public DateTime? StartedUtc { get; set; }

        /// <summary>End time</summary>
        public DateTime? CompletedUtc { get; set; }

        /// <summary>Last progress reported by the engine, 0 to 1</summary>
        public decimal Progress { get; set; }

        /// <summary>Final statistics reported by the engine</summary>
        public Dictionary<string, string> Statistics { get; set; }

        /// <summary>Last status message</summary>
        public string Message { get; set; }

        /// <summary>The engine process exit code</summary>
        public int? ExitCode { get; set; }
    }

    /// <summary>
    /// A worker asking for its next job
    /// </summary>
    public class JobLeaseRequest
    {
        /// <summary>The worker id</summary>
        public string WorkerId { get; set; }

        /// <summary>Job types the worker can run</summary>
        public List<JobType> JobTypes { get; set; } = new();
    }

    /// <summary>
    /// Reply to <see cref="JobLeaseRequest"/>
    /// </summary>
    public class JobLeaseResponse
    {
        /// <summary>The leased job, null if the queue is empty</summary>
        public JobRecord Job { get; set; }
    }

    /// <summary>
    /// Job lifecycle update published by a worker
    /// </summary>
    public class JobStatusUpdate
    {
        /// <summary>Job id</summary>
        public string JobId { get; set; }

        /// <summary>Worker id</summary>
        public string WorkerId { get; set; }

        /// <summary>New status</summary>
        public JobStatus Status { get; set; }

        /// <summary>Status message</summary>
        public string Message { get; set; }

        /// <summary>Engine exit code</summary>
        public int? ExitCode { get; set; }
    }

    /// <summary>
    /// Cancel a job
    /// </summary>
    public class JobCancelRequest
    {
        /// <summary>Job id</summary>
        public string JobId { get; set; }
    }

    /// <summary>
    /// An engine packet published while a job runs
    /// </summary>
    public class JobEvent
    {
        /// <summary>Job id</summary>
        public string JobId { get; set; }

        /// <summary>The LEAN packet type</summary>
        public string PacketType { get; set; }

        /// <summary>Human readable message (debug, log, errors)</summary>
        public string Message { get; set; }

        /// <summary>Progress 0 to 1 for result packets</summary>
        public decimal? Progress { get; set; }

        /// <summary>Statistics of result packets</summary>
        public Dictionary<string, string> Statistics { get; set; }

        /// <summary>Event time</summary>
        public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    }
}
