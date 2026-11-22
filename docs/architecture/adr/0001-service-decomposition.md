# ADR 0001: Service decomposition

* Status: Accepted
* Plan step: `s1.2` (depends on `s1.1`, the seams map)

## Context

The seams map (§4.1, §4.4) shows two hard constraints:

1. **The per-tick loop cannot be split.** `AlgorithmManager.Run` calls the real-time handler, security cache,
   portfolio/cash book, transaction handler, consolidators, user `OnData` and result-handler sampling
   synchronously on shared mutable objects (`IAlgorithm`, `Portfolio`, `Securities`, `Transactions`) for every
   time slice. Any network hop inside that loop multiplies latency by the number of slices.
2. **One algorithm per OS process.** `Config`, `Globals`, `Composer.Instance`, `Log.LogHandler`,
   `PythonInitializer` (one GIL, no re-init after `Shutdown`), lazily loaded databases and process-wide caches
   are static. `ConsoleLeanOptimizer` already respects this by spawning one `QuantConnect.Lean.Launcher.dll`
   process per backtest.

The safe out-of-process seams are the ones the loop touches only at start/end or asynchronously (§4.4): job
intake (`IJobQueueHandler`), result/log/status egress (`IMessagingHandler.Send`, already on the result thread),
file fetch (`IDataProvider.Fetch`, if cached locally), live data in (`IDataQueueHandler`), and the peripheral
executables (Optimizer, Report, ToolBox, DownloaderDataProvider, Research), which already share only the data
folder and result files.

## Decision

Eight services. The proposed split is **confirmed**, with the adjustments marked *Adjusted*.

| Service | Owns | Runs | Image base | LEAN code it loads |
|---|---|---|---|---|
| **api-gateway** | Public REST API (OpenAPI), authentication, job submission, result/report queries, serves the built React console | ASP.NET Core minimal API, .NET 10 | slim `aspnet` | contracts + `Common` packet/statistics types only, **never `Engine`** (§4.3) |
| **job-orchestrator** | Job lifecycle `queued → running → completed / failed / cancelled`, attempts and retries, leases + heartbeats, cancellation, queue-depth scaling hints; **sole owner of the jobs database** | ASP.NET Core minimal API + RabbitMQ consumer | slim `aspnet` | contracts only |
| **backtest-worker** | Executes one backtest job at a time per worker slot | .NET host that spawns **one `Launcher` process per job** with a generated `config.json` | `quantconnect/lean:foundation` | Full engine, unchanged, in the child process |
| **live-worker** | Executes one live/paper deployment per process, long-running | Same host pattern as backtest-worker, live environment (`LiveTradingResultHandler`, `BrokerageTransactionHandler`/paper) | `quantconnect/lean:foundation` | Full engine, unchanged |
| **market-data** | The only writer to the `lean-data` volume: downloads (DownloaderDataProvider/ToolBox as run-to-completion tasks), fetch-on-miss, history queries for the gateway/console/research, live data fan-out | ASP.NET Core minimal API + RabbitMQ consumer | `quantconnect/lean:foundation` (it runs `LeanDataWriter`, map/factor providers, data downloaders) | `Common`, `Engine` data types, `DownloaderDataProvider` |
| **results** | Consumes result/log/status packets from the bus, stores statistics, orders, order events, charts and logs; serves result queries to the gateway | ASP.NET Core minimal API + RabbitMQ consumer | slim `aspnet` | `Common` packets (`BacktestResultPacket`, `LiveResultPacket`, `LogPacket`, …), no `Engine` |
| **optimizer** | Parameter search: `IOptimizationStrategy` (e.g. `GridSearchOptimizationStrategy`) generating parameter sets, fanning each out **as a backtest job through job-orchestrator** and folding results back | .NET host | slim `aspnet` (`Optimizer` references only `Common`/`Configuration`/`Logging`, §1) | `QuantConnect.Optimizer` |
| **report** | Renders HTML/PDF reports from stored results, writes them to object storage | ASP.NET Core host wrapping `Report.Report` unchanged; one render at a time per replica (GIL) | `quantconnect/lean:foundation` | `Report`, `Engine` |
| **research** | Jupyter (`QuantBook`) on the same build, reading `lean-data` read-only and calling the gateway API | `DockerfileJupyter` with `LEAN_BASE` build arg | `quantconnect/lean:foundation` | Full engine in research mode |

### Adjustments to the proposal, with evidence

* *Adjusted* — **workers are process supervisors, not engine hosts.** "The LEAN engine in-process" means the
  engine stays in *one* process per job, not inside the worker host itself. The worker host dequeues a job,
  materialises its inputs into a per-job working directory, writes a per-job `config.json` and starts
  `dotnet QuantConnect.Lean.Launcher.dll --config <job>/config.json`, as `ConsoleLeanOptimizer` does today. Reason:
  all static state in §4.1 (a second job in the same process would inherit `Config`, `Globals`, `Composer`
  caches and a used-up Python runtime). Concurrency = worker slots × replicas; Python jobs scale by replicas.
* *Adjusted* — **"fed by the bus instead of local config" is done through the existing handler seams, not by
  changing the engine.** The child `Launcher` keeps `JobQueue` and reads its generated config; the generated
  config swaps in bus-backed handlers that live in a new project **outside `Engine`** (so `Engine` keeps its
  dependency set, §5.1 "Avoid"), e.g.:
  * `messaging-handler` → a `Messaging` subclass that publishes every packet to RabbitMQ (ADR 0002) and keeps
    the base `Messaging` logging;
  * `data-provider` → default `DefaultDataProvider` over the read-only volume, optionally a fetch-on-miss
    provider (ADR 0004);
  * `results-destination-folder`, `data-folder` (absolute), `object-store-root` → per-job paths;
  * `algorithm-location` → the job's algorithm file downloaded into the working directory (`JobQueue` reads it
    with `File.ReadAllBytes`, §2.1).
  `Composer` only discovers handlers whose dlls are in the scanned folder (§1), so the worker image puts the
  adapter dll next to `QuantConnect.Lean.Launcher.dll`. The default `Launcher/config.json` is untouched.
* *Adjusted* — **the optimizer submits jobs, it does not spawn processes.** A new `LeanOptimizer` subclass
  implements the three abstract members over the orchestrator: `RunLean(parameterSet, backtestName)` → submit a
  child backtest job (with `optimizationId`/`parameters`), `AbortLean(backtestId)` → cancel it,
  `SendUpdate()` → publish optimization status. Results come back through `NewResult(json, backtestId)` from
  the results stream. The strategies and `LeanOptimizer` bookkeeping are reused unchanged;
  `ConsoleLeanOptimizer` stays for the monolith.
* *Adjusted* — **result files stay the source for final artifacts.** The result handler writes final result
  JSON/logs with `File.WriteAllText` into `Globals.ResultsDestinationFolder` at the end of a job (§4.4). The
  worker host uploads that folder to object storage after the child exits and only then reports the job
  terminal; streamed packets feed live progress and the queryable tables in **results**. This keeps parity
  with the monolith output byte for byte and makes the report service read exactly what `Report` reads today.
* *Adjusted* — **market-data runs on the foundation image**, not a slim image: downloaders, `LeanDataWriter`
  and the map/factor file machinery live in `Common`/`Engine`/`DownloaderDataProvider`.
* *Confirmed* — api-gateway, job-orchestrator and results must not reference `Engine` (directly or via
  `Composer` scanning), which lets them use a slim ASP.NET image (§4.3).
* *Confirmed* — research needs no new server code: it is the existing Jupyter image built from the same
  engine build, mounting `lean-data` read-only, and calls the gateway for jobs/results.

### Ownership rules

* Each service owns its data; no service reads another service's tables or buckets directly — it asks the
  owner over REST or listens to its events (ADR 0002, ADR 0003).
* Live and backtest workers are separate deployments because their lifetimes, scaling and failure handling
  differ (minutes vs. weeks; a live job must never run twice, ADR 0002).
* Shared .NET code between services goes in a contracts library (message schemas, DTOs, OpenAPI models) that
  references at most `Common`; service projects and their tests live under a new top-level `Services/` tree
  with **per-service test projects**, not inside `QuantConnect.Tests` (§5.1/§5.2 "Avoid").

## Consequences

* The engine source is not modified for the split; the risk surface is the generated config and the adapter
  handlers, which a parity test covers: run regression algorithms through the worker and assert identical
  statistics including `OrderListHash` (§5.1 harness shape).
* One process per job costs a CLR start and `Composer` scan per job (seconds). Acceptable for backtests; live
  jobs are long-lived.
* Optimizations become ordinary jobs, so they get retries, visibility and scaling for free, and an
  optimization can use the whole worker pool instead of one machine.
* Engine-loading services (workers, market-data, report, research) share one foundation-based multi-stage
  Dockerfile with per-service targets (§5.2), built in `Release`.

## Alternatives considered

* **Split the loop (separate portfolio/transactions/data-feed services).** Rejected: every slice would cross
  the network several times; the loop works on shared mutable objects (§4.4).
* **Multi-tenant worker running several engines in one process.** Rejected: requires removing every row of
  the static-state table (§4.1).
* **One container per job (Docker/K8s Job per backtest) instead of a long-lived worker spawning processes.**
  Viable later as a Helm option; the worker host keeps the same per-job contract either way, so this stays a
  deployment choice, not an architectural one. Rejected as the default because it needs the orchestrator to
  hold container-runtime credentials and slows the Compose demo.
* **Merge results into the orchestrator.** Rejected: result volume (charts, order events, logs) is orders of
  magnitude larger than job metadata and has a different read pattern; separate owners keep the jobs database
  small and the orchestrator's failure domain narrow.
