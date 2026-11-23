# Architecture (as built)

LEAN runs two ways from this repository:

* **Monolith** — unchanged: `dotnet QuantConnect.Lean.Launcher.dll` with the default `Launcher/config.json` (one
  process, one algorithm, files on disk). See the root [`readme.md`](../../readme.md).
* **Platform** — the same engine split into Docker services under `Services/`, run with Docker Compose or Helm. The
  engine source is not modified: every job runs the stock Launcher in its own process, with bus-backed handlers
  selected by a generated per-job `config.json`. Parity: all 1101 runnable regression algorithms (C# and Python)
  produce their `ExpectedStatistics` and `OrderListHash` through the gateway
  ([`Tests/Services.Parity`](../../Tests/Services.Parity/README.md)).

How to run and operate it: [`../operations/`](../operations/README.md).

## Documents

| Document | What |
|---|---|
| [`current-state.md`](current-state.md) | The monolith's seams, static state and constraints any split must respect (input to the ADRs) |
| [`adr/`](adr/README.md) | Decisions 0001–0005 and [0006](adr/0006-as-built.md), which records where the implementation diverged |
| [`contracts/`](contracts/README.md) | The v1 contracts: OpenAPI per service, JSON Schema messages + examples, RabbitMQ topology (`npm run lint`) |
| [`contracts/diagrams/context.md`](contracts/diagrams/context.md) | Context diagram, as built |
| [`contracts/diagrams/submit-backtest-to-report.md`](contracts/diagrams/submit-backtest-to-report.md) | Sequence: submit a backtest, run it, store results, render a report |
| [`contracts/diagrams/deployment.md`](contracts/diagrams/deployment.md) | Compose and Kubernetes deployment |

## Services

| Service | Image base | Owns | Scales |
|---|---|---|---|
| api-gateway | aspnet | Public `/api/v1` (API key or JWT, rate limit, `X-Request-ID`), SSE job events, merged `/openapi`, algorithm uploads, presigned downloads | replicas |
| console | nginx | React + Vite + TS SPA; nginx proxies `/api` to the gateway with the console key | replicas |
| job-orchestrator | aspnet | `jobs` database: lifecycle `queued → dispatched → running → completed / failed / cancelled` (`stale` for live), attempts, leases, heartbeats, outbox to `lean.jobs`, cancellation, live deployer, `/metrics` | 1 |
| backtest-worker | foundation | Runs one backtest at a time: claims the attempt, materialises the algorithm (Roslyn for C# sources), starts one Launcher process, uploads the result folder to `lean-results/<job>/<attempt>/` | replicas (KEDA) |
| live-worker | foundation | One container per live deployment (`lean-live-<jobId>`), created by job-orchestrator; lease epochs, restore from the last `LiveResult` | one per deployment |
| market-data | foundation | Only writer of `lean-data`: catalog, coverage, history, fetch tasks (staging + rename), live fan-out | 1 |
| results | aspnet | `results` database: packets from `lean.events` (statistics, orders, order events, logs, charts index), final JSON from MinIO | replicas |
| report | foundation | `Report.Report` unchanged on stored results → `lean-reports` (HTML, PDF) | replicas (1 render each) |
| optimizer | aspnet | LEAN `LeanOptimizer` strategies; each parameter set is a child backtest job | 1 (state in memory) |
| research | foundation | JupyterLab + QuantBook on `lean-data` (read-only), service URLs injected | optional profile |
| toolbox | foundation | ToolBox converters/generators as one-shot jobs writing `lean-data` through staging | optional profile |

Shared code: `QuantConnect.Services.Contracts` (DTOs, envelope, message types), `QuantConnect.Services.Messaging`
(RabbitMQ over the v1 topology, in-memory bus for tests), `QuantConnect.Services.Defaults` (configuration, health,
OpenTelemetry, logging, shutdown, `LEAN_*` → LEAN `Config`), `QuantConnect.Services.EngineHandlers` (the bus
job-queue, messaging, data-queue handlers and heartbeats loaded into the Launcher process).

## Key mechanisms

* **One engine process per job** (ADR 0001): LEAN's static state means a process runs one algorithm. Workers
  supervise Launcher processes; this costs ~4–5 s per job (C#) and is why the platform pays off for long or
  concurrent jobs ([scaling-workers.md](../operations/scaling-workers.md)).
* **Bus** (ADR 0002, `contracts/topology/`): work on `lean.jobs` (quorum queues, competing consumers, dead letters
  after 3 deliveries), streams on `lean.events`, commands on `lean.control`, live data on `lean.marketdata.live`.
  Backtest deliveries are acked after the result is uploaded and the terminal event confirmed; live deliveries are
  acked when the lease is granted, and a live algorithm never runs twice (lease epochs).
* **Storage** (ADR 0003): one PostgreSQL database per owner, MinIO buckets for algorithms, results and reports, and
  final artefacts are the files the engine already writes, uploaded unchanged.
* **Market data** (ADR 0004/0005): the shared `lean-data` volume read-only in every engine process through the stock
  `DefaultDataProvider`; market-data is the only writer and publishes atomically.
* **Observability**: OTLP from every service, W3C trace context in every message envelope, Prometheus alert rules
  with [runbooks](../operations/runbooks.md).
