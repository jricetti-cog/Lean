using System.Collections.Concurrent;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Contracts.Http;

namespace QuantConnect.Services.Optimizer;

/// <summary>
/// Optimizations this replica runs or ran (the API's source): running ones answer from their <see cref="BusLeanOptimizer"/>,
/// finished ones keep a final snapshot (the last <see cref="OptimizerSettings.RetainFinished"/>). The durable record of a finished
/// optimization is its <c>optimization-result.json</c> in <c>lean-results</c> and the job row in job-orchestrator.
/// </summary>
public sealed class OptimizationCatalog(OptimizerSettings settings)
{
    private sealed record Entry(BusLeanOptimizer? Running, OptimizationState? State, List<ChildBacktest>? Children, Extremum Extremum);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ConcurrentQueue<Guid> _finished = new();

    public void Started(BusLeanOptimizer optimizer) =>
        _entries[optimizer.JobId] = new Entry(optimizer, null, null, optimizer.Criterion.Extremum is QuantConnect.Optimizer.Objectives.Maximization ? Extremum.Max : Extremum.Min);

    public bool TryGetRunning(Guid jobId, out BusLeanOptimizer optimizer)
    {
        optimizer = null!;
        if (_entries.TryGetValue(jobId, out var entry) && entry.Running is { } running)
        {
            optimizer = running;
            return true;
        }
        return false;
    }

    public void Finished(Guid jobId, OptimizationState state, IEnumerable<ChildBacktest> children, Extremum extremum = Extremum.Max)
    {
        var existing = _entries.TryGetValue(jobId, out var entry) ? entry.Extremum : extremum;
        _entries[jobId] = new Entry(null, state, children.ToList(), existing);
        _finished.Enqueue(jobId);
        while (_finished.Count > settings.RetainFinished && _finished.TryDequeue(out var oldest))
        {
            if (_entries.TryGetValue(oldest, out var old) && old.Running == null)
            {
                _entries.TryRemove(oldest, out _);
            }
        }
    }

    /// <summary>Forgets a running optimization that was handed back to the broker (shutdown).</summary>
    public void Released(Guid jobId)
    {
        if (_entries.TryGetValue(jobId, out var entry) && entry.Running != null)
        {
            _entries.TryRemove(jobId, out _);
        }
    }

    public OptimizationState? Get(Guid jobId) =>
        _entries.TryGetValue(jobId, out var entry) ? entry.Running?.State(JobStatus.Running) ?? entry.State : null;

    /// <summary>Children best-first by the criterion (children without an objective last).</summary>
    public List<ChildBacktest>? Children(Guid jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry))
        {
            return null;
        }
        var children = entry.Running?.Children.Select(c => c.ToContract()).ToList() ?? entry.Children ?? new List<ChildBacktest>();
        var ranked = children.Where(c => c.Objective != null);
        ranked = entry.Extremum == Extremum.Max ? ranked.OrderByDescending(c => c.Objective) : ranked.OrderBy(c => c.Objective);
        return ranked.Concat(children.Where(c => c.Objective == null)).ToList();
    }
}
