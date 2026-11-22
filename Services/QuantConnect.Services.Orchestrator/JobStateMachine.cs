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
using System.Text.Json;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.Orchestrator;

/// <summary>Transition rules of the job state machine <c>queued → dispatched → running → completed | failed | cancelled</c>.</summary>
public static class JobStateMachine
{
    public static bool IsTerminal(JobStatus status) => status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>Whether <paramref name="from"/> may move to <paramref name="to"/>. Retries move an active job back to <c>queued</c>.</summary>
    public static bool CanTransition(JobStatus from, JobStatus to) => from switch
    {
        JobStatus.Queued => to is JobStatus.Dispatched or JobStatus.Running or JobStatus.Failed or JobStatus.Cancelled,
        JobStatus.Dispatched => to is JobStatus.Running or JobStatus.Queued or JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled,
        JobStatus.Running => to is JobStatus.Queued or JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Stale,
        JobStatus.Stale => to is JobStatus.Running or JobStatus.Queued or JobStatus.Failed or JobStatus.Cancelled,
        _ => false,
    };

    public static string ToWire(JobStatus status) => JsonSerializer.Serialize(status, ContractJson.Options).Trim('"');

    public static JobStatus ParseStatus(string value) => JsonSerializer.Deserialize<JobStatus>($"\"{value}\"", ContractJson.Options);

    public static string ToWire(JobKind kind) => JsonSerializer.Serialize(kind, ContractJson.Options).Trim('"');

    public static JobKind ParseKind(string value) => JsonSerializer.Deserialize<JobKind>($"\"{value}\"", ContractJson.Options);

    /// <summary>Validates a job request; returns field errors (empty when valid).</summary>
    public static Dictionary<string, List<string>> Validate(JobRequest request, string algorithmsBucket)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = new List<string>();
            list.Add(message);
        }

        var specs = new (JobKind Kind, object? Spec, string Field)[]
        {
            (JobKind.Backtest, request.Backtest, "backtest"), (JobKind.Live, request.Live, "live"),
            (JobKind.Optimization, request.Optimization, "optimization"), (JobKind.Report, request.Report, "report"),
        };
        if (request.Kind == JobKind.MarketData)
        {
            Add("kind", "market-data jobs are owned by the market-data service");
        }
        foreach (var (kind, spec, field) in specs)
        {
            if (kind == request.Kind && spec == null) Add(field, $"required when kind is {ToWire(kind)}");
            if (kind != request.Kind && spec != null) Add(field, $"must be absent when kind is {ToWire(request.Kind)}");
        }
        if (request.ParentJobId != null && request.Kind != JobKind.Backtest)
        {
            Add("parentJobId", "only optimization child backtests have a parent");
        }

        void CheckBacktest(BacktestSpec? spec, string field)
        {
            if (spec == null) return;
            CheckAlgorithm(spec.Algorithm, field + ".algorithm");
            if (spec.StartDate != null && spec.EndDate != null && spec.StartDate > spec.EndDate) Add(field + ".endDate", "must not be before startDate");
            if (spec.Cash is <= 0) Add(field + ".cash", "must be positive");
            if (spec.MaxRuntimeSeconds is <= 0) Add(field + ".maxRuntimeSeconds", "must be positive");
        }
        void CheckAlgorithm(AlgorithmRef? algorithm, string field)
        {
            switch (algorithm)
            {
                case null: Add(field, "required"); break;
                case BuiltinAlgorithmRef builtin when string.IsNullOrWhiteSpace(builtin.TypeName): Add(field + ".typeName", "required"); break;
                case UploadedAlgorithmRef uploaded when uploaded.Object.Bucket != algorithmsBucket:
                    Add(field + ".object.bucket", $"algorithms are read from {algorithmsBucket}"); break;
            }
        }
        CheckBacktest(request.Backtest, "backtest");
        CheckBacktest(request.Optimization?.Backtest, "optimization.backtest");
        if (request.Live != null)
        {
            CheckAlgorithm(request.Live.Algorithm, "live.algorithm");
            if (string.IsNullOrWhiteSpace(request.Live.Brokerage)) Add("live.brokerage", "required");
        }
        if (request.Optimization is { Parameters.Count: 0 }) Add("optimization.parameters", "at least one parameter");
        return errors;
    }

    /// <summary>The dispatch message announcing attempt <paramref name="attempt"/> of a job on <c>lean.jobs</c>.</summary>
    public static OutgoingMessage Dispatch(Guid jobId, JobRequest spec, int attempt, string submittedBy, DateTimeOffset now, JsonElement? inputs)
    {
        var results = ResultsPrefix.For(jobId, attempt);
        var exchange = Exchanges.Jobs;
        var key = RoutingKeys.Dispatch(spec.Kind);
        switch (spec.Kind)
        {
            case JobKind.Backtest:
            case JobKind.Live:
                var type = spec.Kind == JobKind.Live ? MessageTypes.LiveRequested : MessageTypes.BacktestRequested;
                return OutgoingMessage.Create(exchange, key,
                    MessageEnvelope.Create(type, jobId, attempt, AppIds.Orchestrator, spec.Kind, spec.ParentJobId, now: now),
                    new JobSubmitted
                    {
                        JobId = jobId, Kind = spec.Kind, Attempt = attempt, SubmittedAt = now, SubmittedBy = submittedBy,
                        ParentJobId = spec.ParentJobId, Results = results, Backtest = spec.Backtest, Live = spec.Live,
                    });
            case JobKind.Optimization:
                return OutgoingMessage.Create(exchange, key,
                    MessageEnvelope.Create(MessageTypes.OptimizationRequested, jobId, attempt, AppIds.Orchestrator, spec.Kind, now: now),
                    new OptimizationRequested
                    {
                        JobId = jobId, Attempt = attempt, SubmittedAt = now, SubmittedBy = submittedBy, Results = results, Optimization = spec.Optimization!,
                    });
            case JobKind.Report:
                var sources = inputs?.Deserialize<ReportInputs>(ContractJson.Options) ?? throw new InvalidOperationException("report job without resolved inputs");
                return OutgoingMessage.Create(exchange, key,
                    MessageEnvelope.Create(MessageTypes.ReportRequested, jobId, attempt, AppIds.Orchestrator, spec.Kind, now: now),
                    new ReportRequested
                    {
                        JobId = jobId, Attempt = attempt, SubmittedAt = now, SubmittedBy = submittedBy, Report = spec.Report!,
                        BacktestResult = sources.BacktestResult, LiveResult = sources.LiveResult,
                    });
            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.Kind, "not dispatched by the orchestrator");
        }
    }

    public static OutgoingMessage Cancel(Guid jobId, JobKind kind, int attempt, long? leaseEpoch, string reason, string? requestedBy, DateTimeOffset now) =>
        OutgoingMessage.Create(Exchanges.Control, RoutingKeys.Cancel(jobId),
            MessageEnvelope.Create(MessageTypes.CancelJob, jobId, attempt, AppIds.Orchestrator, kind, leaseEpoch: leaseEpoch, now: now),
            new CancelJob { JobId = jobId, Attempt = attempt, RequestedAt = now, RequestedBy = requestedBy, Reason = reason, LeaseEpoch = leaseEpoch })
        with { Mandatory = false };

    public static OutgoingMessage Failed(Guid jobId, JobKind kind, int attempt, Guid? parentJobId, ErrorInfo error, DateTimeOffset now) =>
        OutgoingMessage.Create(Exchanges.Events, RoutingKeys.EventFor(MessageTypes.JobFailed, kind, jobId),
            MessageEnvelope.Create(MessageTypes.JobFailed, jobId, attempt, AppIds.Orchestrator, kind, parentJobId, now: now),
            new JobFailed { JobId = jobId, Kind = kind, Attempt = attempt, ParentJobId = parentJobId, FailedAt = now, Error = error, Final = true })
        with { Mandatory = false };

    public static OutgoingMessage Cancelled(Guid jobId, JobKind kind, int attempt, Guid? parentJobId, string reason, DateTimeOffset now) =>
        OutgoingMessage.Create(Exchanges.Events, RoutingKeys.EventFor(MessageTypes.JobCancelled, kind, jobId),
            MessageEnvelope.Create(MessageTypes.JobCancelled, jobId, attempt, AppIds.Orchestrator, kind, parentJobId, now: now),
            new JobCancelled { JobId = jobId, Kind = kind, Attempt = attempt, ParentJobId = parentJobId, CancelledAt = now, Reason = reason })
        with { Mandatory = false };

    public static bool IsDispatch(string type) => type is MessageTypes.BacktestRequested or MessageTypes.LiveRequested
        or MessageTypes.OptimizationRequested or MessageTypes.ReportRequested;
}

/// <summary>Resolved inputs of a report job, stored with the job so every attempt dispatches the same sources.</summary>
public sealed record ReportInputs
{
    public required ObjectRef BacktestResult { get; init; }
    public ObjectRef? LiveResult { get; init; }
}
