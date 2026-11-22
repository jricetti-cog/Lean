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
using System.Diagnostics;
using System.Diagnostics.Metrics;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.LiveWorker;

/// <summary>
/// The span around the deployment this container runs (the engine stamps its messages with it through
/// <c>bus-traceparent</c>, so live results land in the same trace) and the platform metrics: running and restarts.
/// </summary>
public static class LiveWorkerTelemetry
{
    public const string Name = "QuantConnect.Services.LiveWorker";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static int _running;
    private static readonly ObservableGauge<int> Running = Meter.CreateObservableGauge("lean.live.running", () => Volatile.Read(ref _running), "{deployment}",
        "1 while this live-worker runs its engine");
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("lean.live.runs", "{run}", "Engine runs ended by this live-worker, by outcome");

    public static Activity? StartRun(JobSubmitted job, string workerId, long leaseEpoch)
    {
        Interlocked.Exchange(ref _running, 1);
        var activity = Source.StartActivity("live run", ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("lean.job.id", job.JobId.ToString("D"));
            activity.SetTag("lean.job.attempt", job.Attempt);
            activity.SetTag("lean.job.kind", job.Kind.ToString());
            activity.SetTag("lean.worker.id", workerId);
            activity.SetTag("lean.lease.epoch", leaseEpoch);
        }
        return activity;
    }

    /// <summary><paramref name="exitCode"/> null: the run was abandoned (container stopping).</summary>
    public static void EndRun(Activity? activity, int? exitCode)
    {
        Interlocked.Exchange(ref _running, 0);
        var outcome = exitCode switch { LiveJobRunner.ExitFinished => "finished", LiveJobRunner.ExitRestart => "restart", _ => "abandoned" };
        if (activity != null)
        {
            activity.SetTag("lean.live.outcome", outcome);
            if (outcome == "restart") activity.SetStatus(ActivityStatusCode.Error, "engine exited without a final result");
        }
        Runs.Add(1, new KeyValuePair<string, object?>("lean.live.outcome", outcome));
    }
}
