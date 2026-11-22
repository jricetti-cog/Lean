using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;
using LeanOptimizationResult = QuantConnect.Optimizer.OptimizationResult;

namespace QuantConnect.Services.Optimizer;

/// <summary>What a <see cref="BusLeanOptimizer"/> needs to know about the optimization job it runs.</summary>
public sealed record OptimizationContext(Guid JobId, int Attempt, string? Name, BacktestSpec Backtest);

/// <summary>A child backtest as the optimizer tracks it.</summary>
public sealed class ChildRecord(Guid jobId, ParameterSet parameterSet, Dictionary<string, string> parameters)
{
    public Guid JobId { get; } = jobId;
    public ParameterSet ParameterSet { get; } = parameterSet;
    public Dictionary<string, string> Parameters { get; } = parameters;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? WorkerId { get; set; }
    public decimal? Objective { get; set; }
    public Dictionary<string, string>? Statistics { get; set; }
    public string? Error { get; set; }

    public ChildBacktest ToContract() => new()
    {
        JobId = JobId, Parameters = Parameters, Status = Status, Statistics = Statistics,
        Objective = Objective is { } objective ? (double)objective : null,
    };
}

/// <summary>
/// LEAN's <see cref="LeanOptimizer"/> over the job bus (ADR 0001): <see cref="RunLean"/> submits each parameter set as a child
/// backtest job to job-orchestrator instead of spawning a Launcher, <see cref="AbortLean"/> cancels it, <see cref="SendUpdate"/>
/// publishes <c>job.progress</c>, and child <c>job.completed</c>/<c>job.failed</c> events come back through
/// <see cref="ChildCompleted"/>/<see cref="ChildEnded"/>. The strategies and the base bookkeeping (concurrency limit, pending queue,
/// target, constraints, analysis) are LEAN's, unchanged.
/// </summary>
public sealed class BusLeanOptimizer : LeanOptimizer
{
    public const string AbortReason = "optimization-finished";

    private readonly OptimizationContext _context;
    private readonly IOrchestratorJobs _jobs;
    private readonly IMessagePublisher _bus;
    private readonly ILogger _logger;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<Guid, ChildRecord> _children = new();
    private readonly TaskCompletionSource<LeanOptimizationResult?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _cancelReason;

    public BusLeanOptimizer(OptimizationNodePacket packet, OptimizationContext context, IOrchestratorJobs jobs, IMessagePublisher bus, ILogger logger,
        CancellationToken stopping = default) : base(packet)
    {
        _context = context;
        _jobs = jobs;
        _bus = bus;
        _logger = logger;
        _stopping = stopping;
        Ended += (_, result) => _ended.TrySetResult(result);
    }

    public Guid JobId => _context.JobId;
    public int Attempt => _context.Attempt;

    /// <summary>Completes when LEAN ends the optimization (all parameter sets done or target reached) or it is cancelled.</summary>
    public Task<LeanOptimizationResult?> Completion => _ended.Task;

    /// <summary>Set once the optimization was cancelled (parent cancelled through job-orchestrator).</summary>
    public string? CancelReason => _cancelReason;

    public Target Criterion => OptimizationTarget;
    public LeanOptimizationResult? Best => Strategy.Solution;
    public IReadOnlyCollection<ChildRecord> Children => _children.Values.ToList();
    public int CompletedCount => _children.Values.Count(c => c.Status == JobStatus.Completed);
    public int FailedCount => int.Parse(GetRuntimeStatistics()["Failed"], CultureInfo.InvariantCulture);

    /// <summary>Child job ids LEAN is still waiting on.</summary>
    public IReadOnlyCollection<Guid> RunningChildIds => RunningParameterSetForBacktest.Keys.Select(Guid.Parse).ToList();

    public bool IsWaitingOn(Guid childId) => RunningParameterSetForBacktest.ContainsKey(childId.ToString("D"));

    protected override string RunLean(ParameterSet parameterSet, string backtestName)
    {
        var parameters = new Dictionary<string, string>(_context.Backtest.Parameters ?? new Dictionary<string, string>());
        foreach (var (name, value) in parameterSet.Value)
        {
            parameters[name] = value;
        }
        var request = new JobRequest
        {
            Kind = JobKind.Backtest,
            ParentJobId = _context.JobId,
            Backtest = _context.Backtest with { Name = backtestName, Parameters = parameters },
        };
        try
        {
            var childId = _jobs.SubmitAsync(request, $"{_context.JobId:D}-{_context.Attempt}-{parameterSet.Id}", _stopping).GetAwaiter().GetResult();
            _children[childId] = new ChildRecord(childId, parameterSet, parameters);
            _logger.LogInformation("Optimization {JobId}: submitted child {ChildId} ({Parameters})", _context.JobId, childId, parameterSet);
            return childId.ToString("D");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Optimization {JobId}: could not submit parameter set {Parameters}", _context.JobId, parameterSet);
            return null!;
        }
    }

    protected override string GetBacktestName(ParameterSet parameterSet)
    {
        var name = $"{(string.IsNullOrWhiteSpace(_context.Name) ? "Optimization" : _context.Name)} [{parameterSet}]";
        return name.Length <= 200 ? name : name[..200];
    }

    protected override void AbortLean(string backtestId)
    {
        var childId = Guid.Parse(backtestId);
        if (_children.TryGetValue(childId, out var child) && !IsTerminal(child.Status))
        {
            child.Status = JobStatus.Cancelled;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            _jobs.CancelAsync(childId, AbortReason, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Optimization {JobId}: could not cancel child {ChildId}", _context.JobId, childId);
        }
    }

    protected override void SendUpdate()
    {
        var progress = Progress();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        _bus.PublishAsync(OutgoingMessage.Create(BusExchanges.Events, RoutingKeys.EventFor(MessageTypes.JobProgress, JobKind.Optimization, _context.JobId),
            MessageEnvelope.Create(MessageTypes.JobProgress, _context.JobId, _context.Attempt, AppIds.Optimizer, JobKind.Optimization), progress), timeout.Token)
            .GetAwaiter().GetResult();
    }

    /// <summary>A child started on a worker.</summary>
    public void ChildStarted(Guid childId, string workerId)
    {
        if (_children.TryGetValue(childId, out var child) && child.Status is JobStatus.Queued or JobStatus.Dispatched)
        {
            child.Status = JobStatus.Running;
            child.WorkerId = workerId;
        }
    }

    /// <summary>A child completed: folds its result JSON into the strategy. False when LEAN was not waiting on it (duplicate, aborted).</summary>
    public bool ChildCompleted(Guid childId, string resultJson, Dictionary<string, string>? statistics)
    {
        lock (RunningParameterSetForBacktest)
        {
            if (!IsWaitingOn(childId))
            {
                return false;
            }
            if (_children.TryGetValue(childId, out var child))
            {
                child.Status = JobStatus.Completed;
                child.Statistics = statistics;
                child.Objective = ObjectiveOf(resultJson);
            }
            NewResult(resultJson, childId.ToString("D"));
            return true;
        }
    }

    /// <summary>A child failed or was cancelled for good: LEAN counts it as a failed backtest.</summary>
    public bool ChildEnded(Guid childId, JobStatus status, string? error)
    {
        lock (RunningParameterSetForBacktest)
        {
            if (!IsWaitingOn(childId))
            {
                return false;
            }
            if (_children.TryGetValue(childId, out var child))
            {
                child.Status = status;
                child.Error = error;
            }
            _logger.LogWarning("Optimization {JobId}: child {ChildId} {Status}: {Error}", _context.JobId, childId, status, error);
            NewResult(null!, childId.ToString("D"));
            return true;
        }
    }

    /// <summary>Stops the optimization: pending parameter sets are dropped and running children cancelled.</summary>
    public void Cancel(string reason)
    {
        if (_ended.Task.IsCompleted)
        {
            return;
        }
        _cancelReason = reason;
        SetOptimizationStatus(OptimizationStatus.Aborted);
        Dispose();
        _ended.TrySetResult(null);
    }

    public JobProgress Progress()
    {
        var statistics = GetRuntimeStatistics();
        var total = Math.Max(GetCurrentEstimate(), 0);
        var completed = int.Parse(statistics["Completed"], CultureInfo.InvariantCulture);
        var failed = int.Parse(statistics["Failed"], CultureInfo.InvariantCulture);
        var best = Best;
        return new JobProgress
        {
            JobId = _context.JobId,
            Kind = JobKind.Optimization,
            Attempt = _context.Attempt,
            At = DateTimeOffset.UtcNow,
            Progress = _ended.Task.IsCompleted ? 1 : total > 0 ? Math.Min(1d, (completed + failed) / (double)total) : 0,
            RuntimeStatistics = statistics,
            Optimization = new OptimizationProgress
            {
                Total = total,
                Completed = completed,
                Failed = failed,
                Running = RunningParameterSetForBacktest.Count,
                BestJobId = best?.BacktestId is { } id && Guid.TryParse(id, out var bestId) ? bestId : null,
                BestTargetValue = OptimizationTarget.Current is { } current ? (double)current : null,
            },
        };
    }

    public OptimizationState State(JobStatus status, ObjectRef? result = null)
    {
        var statistics = GetRuntimeStatistics();
        var children = _children.Values.ToList();
        return new OptimizationState
        {
            JobId = _context.JobId,
            Status = status,
            TotalBacktests = Math.Max(GetCurrentEstimate(), 0),
            Completed = int.Parse(statistics["Completed"], CultureInfo.InvariantCulture),
            Failed = int.Parse(statistics["Failed"], CultureInfo.InvariantCulture),
            Running = children.Count(c => c.Status is JobStatus.Running or JobStatus.Stale),
            Queued = PendingParameterSet.Count + children.Count(c => c.Status is JobStatus.Queued or JobStatus.Dispatched),
            Best = BestChild()?.ToContract(),
            Result = result,
        };
    }

    public ChildRecord? BestChild() =>
        Best?.BacktestId is { } id && Guid.TryParse(id, out var bestId) && _children.TryGetValue(bestId, out var child) ? child : null;

    /// <summary><c>optimization-result.json</c>: the best parameter set, LEAN's analysis and every child, without the children's result JSON.</summary>
    public byte[] ResultDocument(JobStatus status)
    {
        var best = Best;
        var bestChild = BestChild();
        var document = new
        {
            OptimizationId = _context.JobId,
            _context.Attempt,
            _context.Name,
            Status = ContractJson.Serialize(status).Trim('"'),
            Strategy = NodePacket.OptimizationStrategy,
            Criterion = new { OptimizationTarget.Target, Extremum = Criterion.Extremum is Maximization ? "max" : "min", OptimizationTarget.TargetValue },
            Constraints = NodePacket.Constraints?.Select(c => new { c.Target, Operator = c.Operator.ToString(), c.TargetValue }),
            Statistics = GetRuntimeStatistics(),
            Best = best?.ParameterSet == null ? null : new
            {
                BacktestId = best.BacktestId,
                Parameters = best.ParameterSet.Value,
                Objective = OptimizationTarget.Current,
                bestChild?.Statistics,
            },
            best?.Analysis,
            CancelReason = _cancelReason,
            Children = _children.Values.OrderBy(c => c.ParameterSet.Id).Select(c => new
            {
                c.JobId, ParameterSetId = c.ParameterSet.Id, c.Parameters, Status = ContractJson.Serialize(c.Status).Trim('"'), c.WorkerId, c.Objective, c.Statistics, c.Error,
            }),
        };
        return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(document, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
            NullValueHandling = NullValueHandling.Ignore,
        }));
    }

    private decimal? ObjectiveOf(string resultJson)
    {
        try
        {
            return Target.GetTokenInJsonBacktest(resultJson, OptimizationTarget.Target)?.ToString() is { } text
                && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTerminal(JobStatus status) => status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
}
