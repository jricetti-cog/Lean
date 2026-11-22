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
using System.Diagnostics.Metrics;
using System.Threading;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.BacktestWorker;

/// <summary>
/// The worker's span around each attempt (the engine child stamps its messages with it, so results land in the same
/// trace) and the platform metrics: busy (utilisation), jobs by outcome and job duration.
/// </summary>
public static class WorkerTelemetry
{
    public const string Name = "QuantConnect.Services.BacktestWorker";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static int _busy;
    private static readonly ObservableGauge<int> Busy = Meter.CreateObservableGauge("lean.worker.busy", () => Volatile.Read(ref _busy), "{job}",
        "1 while this worker runs an engine, 0 when idle");
    private static readonly Counter<long> Jobs = Meter.CreateCounter<long>("lean.worker.jobs", "{job}", "Job attempts finished by this worker");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram("lean.worker.job.duration", "s", "Wall time of one job attempt on the worker",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600, 1200, 1800, 3600, 7200] });

    public static Activity? StartRun(JobSubmitted job, string workerId)
    {
        Interlocked.Exchange(ref _busy, 1);
        var activity = Source.StartActivity("backtest run", ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("lean.job.id", job.JobId.ToString("D"));
            activity.SetTag("lean.job.attempt", job.Attempt);
            activity.SetTag("lean.job.kind", job.Kind.ToString());
            activity.SetTag("lean.worker.id", workerId);
        }
        return activity;
    }

    public static void EndRun(Activity? activity, string terminalType, TimeSpan elapsed)
    {
        Interlocked.Exchange(ref _busy, 0);
        var outcome = terminalType == MessageTypes.JobCompleted ? "completed" : "failed";
        if (activity != null)
        {
            activity.SetTag("lean.job.outcome", outcome);
            if (outcome != "completed") activity.SetStatus(ActivityStatusCode.Error, terminalType);
        }
        var tag = new KeyValuePair<string, object?>("lean.job.outcome", outcome);
        Jobs.Add(1, tag);
        Duration.Record(elapsed.TotalSeconds, tag);
    }

    /// <summary>Clears the busy flag when an attempt is abandoned (worker stopping) without a terminal status.</summary>
    public static void Abandon() => Interlocked.Exchange(ref _busy, 0);
}
