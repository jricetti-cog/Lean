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
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantConnect.Services.BacktestWorker;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Messaging;

namespace QuantConnect.Services.LiveWorker;

/// <summary>Environment handed to the child engine (bus and MinIO settings as <c>LEAN_BUS_*</c>, so no secret is written to disk).</summary>
public sealed record LiveEngineEnvironment(IReadOnlyDictionary<string, string> Variables);

/// <summary>Snapshot served on <c>GET /internal/v1/worker</c>.</summary>
public sealed record LiveWorkerStatus
{
    public required string WorkerId { get; init; }
    public Guid? JobId { get; init; }
    public int? Attempt { get; init; }
    public long? LeaseEpoch { get; init; }
    public int? EnginePid { get; init; }
    public bool Restored { get; init; }
    public int RestoredHoldings { get; init; }
    public string Phase { get; init; } = "idle";
    public long LiquidationsSent { get; init; }
}

/// <summary>
/// Runs the one live deployment of this container: takes the orchestrator lease (ADR 0002, so a deployment never runs twice),
/// restores holdings and cash from results' last <c>LiveResult</c> into PaperBrokerage's <c>live-holdings</c> /
/// <c>live-cash-balance</c>, starts the stock Launcher in live mode with the bus handlers and <see cref="LiveWorkspace"/>'s
/// config, publishes <c>JobStarted</c> and lease-stamped heartbeats, and turns <c>control.v1.CancelJob</c> /
/// <c>control.v1.LiquidateJob</c> into LEAN <c>LiquidateCommand</c> / <c>QuitCommand</c> files for the engine's
/// <c>FileCommandHandler</c>. The returned exit code tells the container runtime what to do: 0 = finished (stopped, ended or
/// refused), 1 = the engine died, restart the container (it restores the portfolio again).
/// </summary>
public sealed class LiveJobRunner
{
    public const string LiquidateCommandType = "QuantConnect.Commands.LiquidateCommand, QuantConnect.Common";
    public const string QuitCommandType = "QuantConnect.Commands.QuitCommand, QuantConnect.Common";
    public const int ExitFinished = 0;
    public const int ExitRestart = 1;

    private readonly IMessagePublisher _publisher;
    private readonly ITransientSubscriber _subscriber;
    private readonly ILiveServices _services;
    private readonly IResultStore _store;
    private readonly IEngineLauncher _launcher;
    private readonly LiveEngineEnvironment _environment;
    private readonly WorkerOptions _engine;
    private readonly LiveWorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveJobRunner> _logger;
    private readonly object _gate = new();
    private LiveWorkerStatus _status;
    private int _commandSequence;

    public LiveJobRunner(IMessagePublisher publisher, ITransientSubscriber subscriber, ILiveServices services, IResultStore store, IEngineLauncher launcher,
        LiveEngineEnvironment environment, IOptions<WorkerOptions> engine, IOptions<LiveWorkerOptions> options, TimeProvider time, ILogger<LiveJobRunner> logger)
    {
        _publisher = publisher;
        _subscriber = subscriber;
        _services = services;
        _store = store;
        _launcher = launcher;
        _environment = environment;
        _engine = engine.Value;
        _options = options.Value;
        _time = time;
        _logger = logger;
        WorkerId = _options.ResolvedWorkerId;
        _status = new LiveWorkerStatus { WorkerId = WorkerId };
    }

    public string WorkerId { get; }

    public LiveWorkerStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Parses <c>LiveWorker__Dispatch</c>.</summary>
    public static JobSubmitted ParseDispatch(string? dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch)) throw new InvalidOperationException("LiveWorker__Dispatch (the JobSubmitted of the deployment) is required");
        var job = ContractJson.Deserialize<JobSubmitted>(dispatch);
        if (job is not { Kind: JobKind.Live, Live: not null }) throw new InvalidOperationException($"LiveWorker__Dispatch is not a live job");
        return job;
    }

    public async Task<int> RunAsync(JobSubmitted dispatch, CancellationToken cancellationToken)
    {
        var grant = await _services.AcquireLeaseAsync(dispatch.JobId, WorkerId, cancellationToken).ConfigureAwait(false);
        if (grant.Outcome != LeaseOutcome.Granted)
        {
            _logger.LogWarning("Live deployment {JobId}: no lease for {WorkerId} ({Detail}); nothing to run", dispatch.JobId, WorkerId, grant.Detail);
            return ExitFinished;
        }
        var lease = grant.Lease!;
        var job = dispatch with { Attempt = lease.Attempt, Results = lease.Results ?? ResultsPrefix.For(dispatch.JobId, lease.Attempt) };
        var heartbeat = TimeSpan.FromSeconds(Math.Max(1, lease.HeartbeatIntervalSeconds > 0 ? Math.Min(lease.HeartbeatIntervalSeconds, _options.HeartbeatIntervalSeconds) : _options.HeartbeatIntervalSeconds));

        LiveState? restored = null;
        if (_options.RestoreState)
        {
            restored = await _services.LiveStateAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        }
        var workspace = LiveWorkspace.Create(_engine, job, WorkerId, lease.LeaseEpoch, restored);
        SetStatus(s => s with
        {
            JobId = job.JobId, Attempt = job.Attempt, LeaseEpoch = lease.LeaseEpoch, Phase = "starting",
            Restored = restored != null, RestoredHoldings = restored?.Holdings.Count ?? 0,
        });
        _logger.LogInformation("Live deployment {JobId} attempt {Attempt}, lease epoch {Epoch} on {WorkerId}: {Algorithm} on {Brokerage}, {State}",
            job.JobId, job.Attempt, lease.LeaseEpoch, WorkerId, Describe(job.Live!.Algorithm), job.Live.Brokerage,
            restored == null ? "fresh portfolio" : $"restoring {restored.Holdings.Count} holding(s) and {restored.Cash.Count} cash balance(s) from {restored.UpdatedAt:O}");

        var control = new ControlInbox(lease.LeaseEpoch);
        await using var subscription = await _subscriber.SubscribeTransientAsync(Exchanges.Control,
            new[] { RoutingKeys.Cancel(job.JobId), RoutingKeys.Liquidate(job.JobId) }, (message, _) => Task.FromResult(control.Accept(message, _logger)),
            new TransientQueueOptions { Prefetch = 10, QueueName = $"live-worker.{job.JobId:D}.control" }, cancellationToken).ConfigureAwait(false);

        try
        {
            await _publisher.PublishAsync(Started(job, lease.LeaseEpoch, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            return await SuperviseAsync(job, lease.LeaseEpoch, workspace, control, heartbeat, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SetStatus(s => s with { Phase = "finished", EnginePid = null });
            if (!_options.KeepWorkDirectory) workspace.Delete();
        }
    }

    private async Task<int> SuperviseAsync(JobSubmitted job, long epoch, LiveWorkspace workspace, ControlInbox control, TimeSpan heartbeat, CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Max(10, _options.PollIntervalMilliseconds));
        int exitCode;
        DateTimeOffset? quitAt = null, killAt = null;
        await using (var engine = _launcher.Start(workspace.StartInfo(_engine, _environment.Variables)))
        {
            SetStatus(s => s with { EnginePid = engine.Id, Phase = "running" });
            var nextBeat = _time.GetUtcNow();
            while (!engine.Exited.IsCompleted)
            {
                var now = _time.GetUtcNow();
                if (now >= nextBeat)
                {
                    await TryPublishAsync(Heartbeat(job, epoch, now, engine.RssBytes), cancellationToken).ConfigureAwait(false);
                    nextBeat = now + heartbeat;
                }
                while (control.TakeLiquidation() is { } liquidation)
                {
                    WriteCommand(workspace, LiquidateCommandType, liquidation);
                    SetStatus(s => s with { LiquidationsSent = s.LiquidationsSent + 1 });
                    _logger.LogInformation("Live deployment {JobId}: liquidation requested by {By}", job.JobId, liquidation);
                }
                if (control.Cancel is { } cancel && quitAt == null)
                {
                    if (cancel.Liquidate == true)
                    {
                        WriteCommand(workspace, LiquidateCommandType, cancel.RequestedBy ?? "stop");
                        SetStatus(s => s with { LiquidationsSent = s.LiquidationsSent + 1 });
                    }
                    quitAt = now + (cancel.Liquidate == true ? TimeSpan.FromSeconds(Math.Max(0, _options.LiquidateGraceSeconds)) : TimeSpan.Zero);
                    SetStatus(s => s with { Phase = "stopping" });
                    _logger.LogInformation("Live deployment {JobId}: stop requested ({Reason}{Liquidate})", job.JobId, cancel.Reason, cancel.Liquidate == true ? ", liquidating first" : "");
                }
                if (quitAt is { } q && killAt == null && now >= q)
                {
                    WriteCommand(workspace, QuitCommandType, "stop");
                    killAt = now + TimeSpan.FromSeconds(Math.Max(1, _options.StopGraceSeconds));
                }
                if (killAt is { } k && now >= k)
                {
                    _logger.LogWarning("Live deployment {JobId}: the engine did not stop within {Grace} s; killing it", job.JobId, _options.StopGraceSeconds);
                    engine.Kill();
                    break;
                }
                try
                {
                    await engine.Exited.WaitAsync(poll, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The container is stopping (not a user stop): quit cleanly, keep the deployment for the next container.
                    _logger.LogWarning("live-worker stopping: quitting deployment {JobId}; it resumes when a worker takes the lease again", job.JobId);
                    WriteCommand(workspace, QuitCommandType, "shutdown");
                    try
                    {
                        await engine.Exited.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.StopGraceSeconds)), _time, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        engine.Kill();
                    }
                    throw;
                }
            }
            exitCode = await engine.Exited.ConfigureAwait(false);
        }
        SetStatus(s => s with { EnginePid = null });
        return await TerminalAsync(job, epoch, workspace, control.Cancel, exitCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> TerminalAsync(JobSubmitted job, long epoch, LiveWorkspace workspace, CancelJob? cancel, int exitCode, CancellationToken cancellationToken)
    {
        var artifacts = new List<ObjectRef>();
        var results = job.Results!;
        string Key(string name) => $"{results.Prefix.TrimEnd('/')}/{name}";
        async Task Upload(string path, string name, string contentType)
        {
            if (!File.Exists(path)) return;
            try
            {
                artifacts.Add(await _store.PutRefAsync(results.Bucket, Key(name), await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), contentType,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not store {Name} of live deployment {JobId}", name, job.JobId);
            }
        }
        var state = EngineResultState.Read(workspace.ResultPath);
        await Upload(workspace.ResultPath, $"{job.JobId:D}.json", "application/json").ConfigureAwait(false);
        await Upload(workspace.LogPath, $"engine-{_time.GetUtcNow():yyyyMMddTHHmmssZ}.log", "text/plain").ConfigureAwait(false);

        if (cancel != null)
        {
            await _publisher.PublishAsync(Event(MessageTypes.JobCancelled, job, epoch, new JobCancelled
            {
                JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, ParentJobId = job.ParentJobId, CancelledAt = _time.GetUtcNow(),
                Reason = cancel.Reason, LeaseEpoch = epoch, Artifacts = artifacts.Count > 0 ? artifacts : null,
            }), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Live deployment {JobId} stopped (engine exit {ExitCode})", job.JobId, exitCode);
            return ExitFinished;
        }
        if (state?.RuntimeError != null || string.Equals(state?.Status, "RuntimeError", StringComparison.OrdinalIgnoreCase))
        {
            await _publisher.PublishAsync(Event(MessageTypes.JobFailed, job, epoch, new JobFailed
            {
                JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, ParentJobId = job.ParentJobId, FailedAt = _time.GetUtcNow(), Final = true,
                Error = new ErrorInfo { Code = ErrorCode.RuntimeError, Message = state?.RuntimeError ?? "Algorithm runtime error", StackTrace = state?.StackTrace, ExitCode = exitCode },
                Artifacts = artifacts.Count > 0 ? artifacts : null,
            }), cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Live deployment {JobId} failed: {Error}", job.JobId, state?.RuntimeError);
            return ExitFinished;
        }
        if (state is { Ended: true, Status: var status } && artifacts.FirstOrDefault(a => a.Key.EndsWith($"{job.JobId:D}.json", StringComparison.Ordinal)) is { } result)
        {
            // The algorithm ended itself (Quit(), end date): the deployment is over.
            await _publisher.PublishAsync(Event(MessageTypes.JobCompleted, job, epoch, new JobCompleted
            {
                JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, ParentJobId = job.ParentJobId, CompletedAt = _time.GetUtcNow(), Result = result,
                Artifacts = artifacts.Where(a => a != result).ToList(), AlgorithmId = job.JobId.ToString("D"), ExitCode = exitCode,
            }), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Live deployment {JobId} ended by the algorithm ({Status})", job.JobId, status);
            return ExitFinished;
        }
        _logger.LogError("Live deployment {JobId}: engine exited with code {ExitCode} without a final result; restarting the container", job.JobId, exitCode);
        return ExitRestart;
    }

    private void WriteCommand(LiveWorkspace workspace, string type, string by)
    {
        var id = $"{Interlocked.Increment(ref _commandSequence):D4}-{Guid.NewGuid():N}";
        var body = new JsonObject { ["$type"] = type, ["Id"] = id }.ToJsonString();
        var temp = Path.Combine(workspace.Directory, $"pending-{id}.tmp");
        File.WriteAllText(temp, body);
        // FileCommandHandler picks up command*.json in the engine's working directory; rename so it never reads a partial file.
        File.Move(temp, Path.Combine(workspace.Directory, $"command-{id}.json"));
        _logger.LogDebug("Wrote {Type} ({By})", type, by);
    }

    private void SetStatus(Func<LiveWorkerStatus, LiveWorkerStatus> update)
    {
        lock (_gate) _status = update(_status);
    }

    private async Task TryPublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not publish {Type} for job {JobId}", message.Envelope.Type, message.Envelope.Headers.JobId);
        }
    }

    private static string Describe(AlgorithmRef algorithm) => algorithm switch
    {
        BuiltinAlgorithmRef builtin => builtin.TypeName,
        UploadedAlgorithmRef uploaded => $"{uploaded.Language} s3://{uploaded.Object.Bucket}/{uploaded.Object.Key}",
        _ => algorithm.GetType().Name,
    };

    private static OutgoingMessage Event<T>(string type, JobSubmitted job, long epoch, T body) => OutgoingMessage.Create(Exchanges.Events,
        RoutingKeys.EventFor(type, job.Kind, job.JobId), MessageEnvelope.Create(type, job.JobId, job.Attempt, AppIds.LiveWorker, job.Kind, job.ParentJobId, epoch), body);

    private OutgoingMessage Started(JobSubmitted job, long epoch, DateTimeOffset at) => Event(MessageTypes.JobStarted, job, epoch, new JobStarted
    {
        JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, ParentJobId = job.ParentJobId, StartedAt = at, WorkerId = WorkerId, LeaseEpoch = epoch,
        Results = job.Results!, EngineVersion = typeof(LiveJobRunner).Assembly.GetName().Version?.ToString(),
    });

    private OutgoingMessage Heartbeat(JobSubmitted job, long epoch, DateTimeOffset at, long rss) => Event(MessageTypes.JobHeartbeat, job, epoch, new JobHeartbeat
    {
        JobId = job.JobId, Kind = job.Kind, Attempt = job.Attempt, At = at, WorkerId = WorkerId, LeaseEpoch = epoch, EngineAlive = true, RssBytes = rss,
    });
}

/// <summary>Control messages of the running deployment; anything stamped with another lease epoch is for a different holder and ignored.</summary>
public sealed class ControlInbox(long leaseEpoch)
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _liquidations = new();
    private CancelJob? _cancel;

    public CancelJob? Cancel => Volatile.Read(ref _cancel);

    public string? TakeLiquidation() => _liquidations.TryDequeue(out var by) ? by : null;

    public MessageDisposition Accept(ReceivedMessage message, ILogger logger)
    {
        try
        {
            switch (message.Envelope.Type)
            {
                case MessageTypes.CancelJob:
                    var cancel = ContractJson.Deserialize<CancelJob>(message.Body.Span);
                    if (cancel.LeaseEpoch is { } c && c != leaseEpoch) break;
                    Interlocked.CompareExchange(ref _cancel, cancel, null);
                    break;
                case MessageTypes.LiquidateJob:
                    var liquidate = ContractJson.Deserialize<LiquidateJob>(message.Body.Span);
                    if (liquidate.LeaseEpoch is { } l && l != leaseEpoch) break;
                    _liquidations.Enqueue(liquidate.RequestedBy ?? "api");
                    break;
            }
        }
        catch (JsonException e)
        {
            logger.LogError(e, "Ignoring invalid control message {MessageId}", message.Envelope.MessageId);
        }
        return MessageDisposition.Ack;
    }
}

/// <summary>The <c>state</c> block LEAN writes into its result file (<c>Status</c>, <c>RuntimeError</c>, <c>StackTrace</c>).</summary>
public sealed record EngineResultState(string? Status, string? RuntimeError, string? StackTrace)
{
    private static readonly HashSet<string> EndedStatuses = new(StringComparer.OrdinalIgnoreCase) { "Completed", "Stopped", "Liquidated", "Deleted" };

    /// <summary>The algorithm reached an end state; LiveTradingResultHandler also writes the file while <c>Running</c>.</summary>
    public bool Ended => Status != null && EndedStatuses.Contains(Status);

    public static EngineResultState? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonNode.Parse(File.ReadAllBytes(path))?["state"] as JsonObject ?? JsonNode.Parse(File.ReadAllBytes(path))?["State"] as JsonObject;
            if (state == null) return new EngineResultState(null, null, null);
            static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
            return new EngineResultState(S(state, "Status"), S(state, "RuntimeError"), S(state, "StackTrace"));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }
}
