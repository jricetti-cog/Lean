# Scaling workers and what each job costs

## How work is spread

* **Backtests.** Each `backtest-worker` container consumes `jobs.backtest` with prefetch 1: it runs **one job at a
  time**, as one `QuantConnect.Lean.Launcher.dll` child process per attempt with a generated `config.json` (ADR 0001,
  0006). Backtest concurrency = number of backtest-worker replicas. Optimizations fan out into ordinary backtest
  jobs, so they use the same pool.
* **Live deployments.** One container (Compose) or one single-replica Deployment (Helm) per deployment, created by
  job-orchestrator; they do not share a pool and are not scaled by count.
* **Reports.** One render at a time per `report` replica (pythonnet GIL); add replicas for parallel renders.
* **market-data** is the only writer of `lean-data`: run one replica. **optimizer** keeps its state in memory: run one.
  api-gateway, results and console are stateless and can run several replicas behind their Service.

## Per-job overhead (measured)

The parity run (PR #36, [`Tests/Services.Parity/parity-results/parity.md`](../../Tests/Services.Parity/parity-results/parity.md))
ran all 1101 runnable regression algorithms both ways:

| | Monolith (LEAN's suite, one process, sequential) | Platform (gateway → worker, 6 workers) |
|---|---|---|
| Suite wall time | **433 s** | **1992 s** |
| Sum of per-job times | 436 s | 8145 worker-seconds |
| Median job, C# (762 jobs) | 0.1 s | 5.1 s (+4.9 s; p10 +4.1 s, p90 +6.9 s) |
| Median job, Python (339 jobs) | 0.1 s | 7.3 s (+7.1 s; p10 +6.3 s, p90 +9.8 s) |
| Median submit → result available | n/a | 7.0 s |

Every platform job pays a fixed **~4–5 s** (C#; ~7 s for Python) on top of its engine time, and the regression
algorithms are tiny (median 0.1 s of engine work), so the overhead dominates the suite. Where it goes:

1. **Child-process start, most of it.** A fresh `dotnet` process, CLR start-up and JIT, `Composer` scanning the
   Launcher folder, LEAN loading its databases (market hours, symbol properties, map files) and, for Python, pythonnet
   and the Python runtime. The monolith suite pays this once for 1101 algorithms; the platform pays it per job,
   by design (static state, ADR 0001).
2. Workspace materialisation (algorithm download or Roslyn compile of uploaded C#), the attempt claim and
   `job.started` to job-orchestrator, and heartbeats.
3. Uploading the result folder to MinIO, the final `PacketRef`/`job.completed` with publisher confirms, and results
   ingesting the final JSON.

The comparison is with LEAN's in-process test suite, which is the cheapest way to run many tiny backtests. A single
backtest through the stock Launcher pays the process start too: the ADR 0005 benchmark measured the same engine in a
container, fed over RabbitMQ and reading the shared volume, at **−5.5% to +8.9% end to end** against the stock Launcher
(engine process +12% at most, from serialising a 3.7 MB result), with identical statistics. Those are 2.6–4.6 s
backtests; the fixed cost's share falls as the engine time grows, so engine time on longer backtests is the same as the
monolith's within that margin.

## When the platform pays off

With a fixed overhead *o* per job (≈5 s), engine time *T* and *N* workers, a batch of jobs takes about
*(T + o) / N* per job on the platform versus *T* in one monolith process. The platform is faster when
**N > 1 + o / T**:

| Engine time per backtest | Overhead share of one job | Workers to beat one sequential monolith process |
|---|---|---|
| 0.1 s (regression algorithms) | ~98% | ~50 (the parity run with 6 was 4.6× slower) |
| 5 s | 50% | 3 |
| 1 min | ~8% | 2 (≈1.8× faster with 2, ≈7.4× with 8) |
| 1 h | <0.2% | any N > 1 is ≈N× faster |

So it pays off for **long backtests** (the overhead disappears in the runtime), for **concurrent work** (optimizations,
parameter sweeps, many users: work spreads over every worker instead of one machine) and for what the monolith cannot
do at all (queued submissions, retries on a crash, cancellation, live deployments that restart, results and reports
queryable from the console). For many sub-second backtests on one machine, LEAN's in-process runner (or the monolith
Launcher in a loop) is faster.

## Knobs

| Knob | Where | Effect |
|---|---|---|
| Worker count | Compose: `BACKTEST_WORKER_REPLICAS` or `docker compose up -d --scale backtest-worker=N`; Helm: `backtestWorker.replicas` / autoscaling | Backtest concurrency. Each worker holds one engine; size the host for N × worker limits |
| Autoscaling | Helm `backtestWorker.autoscaling.*`: `provider` (keda / external), `minReplicas` 1, `maxReplicas` 8, `targetQueueLength` 2, `cooldownPeriod` 300, `behavior` | KEDA scales on `jobs.backtest` ready + unacked. Scale-down is slow on purpose (1 pod/min after 5 min) so bursts reuse warm workers |
| Worker CPU / memory | `BACKTEST_WORKER_CPUS` (2), `BACKTEST_WORKER_MEMORY` (6g); Helm `backtestWorker.resources` | Container limits. A job's engine is killed above its RAM limit + `Worker__MemoryHeadroomMb` (1024) |
| Default job RAM | `BACKTEST_WORKER_DEFAULT_RAM_MB` → `Worker__DefaultRamMb` (4096); per job `backtest.maxRamMb` | Engine RAM limit when the job sets none |
| Job runtime limit | per job `maxRuntimeSeconds`; `Worker__DefaultMaxRuntimeSeconds` / `Orchestrator__DefaultBacktestMaxRuntimeSeconds` (6 h) | Longer jobs need a higher limit; RabbitMQ `consumer_timeout` is 7 h (`topology/rabbitmq.conf`), keep it above the limit |
| Heartbeats | `BACKTEST_WORKER_HEARTBEAT_SECONDS` (30), `ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS` (90), `ORCHESTRATOR_MAX_ATTEMPTS` (3) | How fast a dead worker's job is retried, and how often |
| Suggested replicas | `Orchestrator__JobsPerReplica` (1), `Orchestrator__MinReplicas`, `Orchestrator__MaxReplicas` (20) | `lean_queue_suggested_replicas` on the orchestrator `/metrics`, for an external autoscaler |
| Optimizer fan-out | `OPTIMIZER_MAX_CONCURRENT_BACKTESTS` (16), spec `maximumConcurrentBacktests`, `Optimizer__MaxConcurrentOptimizations` (2) | Child backtests kept in flight; beyond the worker count they just queue |
| Live deployment size | `LIVE_WORKER_MEMORY_MB` (4096), `LIVE_WORKER_CPUS` (2); Helm `liveWorker.memoryMb` / `cpus` | Limits of each `lean-live-<jobId>` container/pod |
| Bulk submission | `GATEWAY_RATE_LIMIT` (120 per `GATEWAY_RATE_WINDOW_SECONDS` 60) | Raise it for scripted sweeps (the parity harness does) |

**Reducing the per-job overhead.** There is no warm process pool today: every attempt starts a new Launcher. The
lever with the largest effect would be a pool of pre-started Launcher processes per worker, each already past CLR
start, `Composer` scan and database loading (and pythonnet initialisation for Python), that takes exactly one job and
then exits (the one-algorithm-per-process rule of ADR 0001 still holds). It would remove most of the ~4–5 s from the
critical path at the cost of one idle process (≈300 MB C#, ≈500 MB Python, ADR 0005) per worker; it needs a new ADR
and a "start, then wait for the job file" mode in the bus job-queue handler. Until then: fewer, longer jobs (e.g.
one backtest over a longer period instead of many short ones), more workers for sweeps, and C# over Python where the
~2 s pythonnet start matters.
