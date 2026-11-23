# ADR 0006: As built — amendments to ADRs 0001–0005

* Status: Accepted
* Plan step: `s6.4` (records the platform as merged on `master` after plan steps s2.1–s6.3)
* Amends: [0001](0001-service-decomposition.md), [0002](0002-service-communication.md), [0003](0003-storage.md),
  [0004](0004-market-data-access.md), [0005](0005-engine-hosting-benchmark.md). Those ADRs stay as accepted; where the
  implementation diverged, this record wins.

## Context

ADRs 0001–0005 were written before any service existed. Implementation (Phases 2–6) kept every core decision —
eight services plus research, one engine process per job with the stock Launcher, bus-backed handlers selected by
generated config, RabbitMQ with the v1 topology, PostgreSQL + MinIO, a shared read-only `lean-data` volume with
market-data as the only writer, the monolith's `Launcher/config.json` untouched — and parity proves it: all 1101
runnable regression algorithms produce their `ExpectedStatistics` and `OrderListHash` through the gateway
(`Tests/Services.Parity`). Some details changed on the way; this ADR lists them so the ADRs plus this one describe
the system as it runs.

## Decision: the as-built differences

| # | ADR | Planned | As built | Why |
|---|---|---|---|---|
| 1 | 0001, 0002 | live-worker: a long-running pool consuming `jobs.live`, one process per deployment | **job-orchestrator consumes `jobs.live`** and starts **one live-worker container per deployment** (`lean-live-<jobId>`): Docker Engine API through `docker-socket-proxy` in Compose (`Orchestrator__LiveDeployment__Mode=docker`), one single-replica `Recreate` Deployment per deployment through the Kubernetes API in Helm (`kubernetes`, namespace-scoped Role on `apps/deployments`). `none` leaves `jobs.live` to external workers. The container runs one engine; Docker/Kubernetes restart it on a crash and it restores holdings and cash from results' last `LiveResult` | A deployment lives for weeks and must survive restarts of any worker host; one container per deployment gives it its own limits, restart policy and logs, and the orchestrator already owns the lease |
| 2 | 0001 | backtest concurrency = worker slots × replicas | **one job per backtest-worker** (prefetch 1); concurrency = replicas, scaled by KEDA on `jobs.backtest` depth in Helm | Simpler failure domain and resource limits per container; replicas scale the same way |
| 3 | 0001 | ~seconds of per-job process start | measured **~4–5 s per job for C#, ~7 s for Python** on top of engine time (parity run: suite 433 s in-process vs 1992 s on 6 workers). Engine time itself stays within ~9% of the monolith (ADR 0005). No warm process pool yet | See [`docs/operations/scaling-workers.md`](../../operations/scaling-workers.md) |
| 4 | 0003 | `gateway` database: users, hashed API keys | **API keys come from configuration** (`Gateway__ApiKeys__<n>__Key/User/Admin`), JWT (OIDC authority or HS256 key) optional; the `gateway` database and role are created but unused | No user management was in scope; keys in a secret are enough for the demo and for a cluster |
| 5 | 0003 | optimizer state in PostgreSQL (diagram) | **optimizer keeps optimization state in memory** (`OptimizationCatalog`); children are ordinary jobs in the `jobs` database and the result is `optimization-result.json` in `lean-results`. Run one replica | Child jobs, results and the final artefact are already durable; a restarted optimizer loses only in-flight bookkeeping |
| 6 | 0003 | user algorithms: Python files or prebuilt dlls | Python files or project zips, **and C# sources** (`.cs` or a project `.zip`) **compiled by the backtest-worker with Roslyn** against the image's LEAN assemblies; build errors fail the job as `compile-error`. Built-in C# algorithms run by type name from the image | Users submit source, like the LEAN CLI |
| 7 | 0004 | per-job `fetchMissingData: true` selects a fetch-on-miss `IDataProvider`; gateway checks coverage before dispatch | **Not built.** `fetchMissingData` is accepted in the contract but ignored; missing data stays missing as in the monolith. Coverage, history and fetch tasks (`download`, `fetch-files`, `generate-synthetic`) are served by market-data and the gateway (`/api/v1/data/...`) for clients to call before submitting | Determinism and parity first; no data vendor is connected in this deployment |
| 8 | 0004 | market-data republishes the live feed of a vendor | Fan-out built (`lean.marketdata.live`, `BusDataQueueHandler` in live jobs); the upstream defaults to LEAN's simulated **`FakeDataQueue`** (`MarketData__Live__DataQueueHandler`). Live deployments are exercised with **`PaperBrokerage`** only | No vendor or brokerage credentials in scope |
| 9 | 0002 | every service serves `/openapi/v1.json`; the console client is generated from the gateway's document | The **contracts under `docs/architecture/contracts/openapi/` are the source of truth**. job-orchestrator and optimizer also map the built-in `/openapi/v1.json`; the others do not. The gateway serves one **merged document at `/openapi`** (its routes plus each upstream's live document, else its embedded contract). The console's TypeScript client is generated from the gateway contract YAML (`npm run gen:api`; `check:api` fails on drift) | Contract-first work started before the services; one public document is what clients need |
| 10 | 0005 | final result on a `lean.results` queue | Results travel on **`lean.events`** (`job.<kind>.<id>.result.v1`, consumer queue `results.packets`); the final result is a `PacketRef` to the engine's JSON in MinIO (≤ 256 KiB inline otherwise) | The v1 topology contract (s1.4) unified all job streams on one topic exchange |
| 11 | — | (no observability ADR) | Every service uses `QuantConnect.Services.Defaults`: OpenTelemetry traces/metrics/logs over OTLP, JSON logs, `/healthz` + `/readyz`, graceful drain. The envelope carries W3C `traceparent`, so a backtest is one trace from gateway request to results ingestion. Optional `observability` profile: Collector, Prometheus (+ alert rules, [runbooks](../../operations/runbooks.md)), Grafana, Tempo, Loki | Operating a distributed system needs it; the shared defaults keep every service consistent |
| 12 | — | (deployment details) | Compose with no profile starts the whole platform (`make up`); a Helm chart (`deploy/helm/lean-platform`) with an `infra` subchart or external services, KEDA autoscaling, the `lean-data` PVC seeded by a Job (`ReadWriteOnce` on kind; `ReadWriteMany` for multi-node). Images on GHCR per `master` build and `v*` tag | Plan step s6.3 |

## Consequences

* The architecture overview ([`../README.md`](../README.md)) and the context diagram
  ([`../contracts/diagrams/context.md`](../contracts/diagrams/context.md)) show the as-built system; the sequence
  diagram is unchanged.
* Open items, each needing its own ADR when picked up: a fetch-on-miss data provider (7), real brokerage and data vendor
  credentials for live deployments (8, see [adding a brokerage](../../operations/adding-a-brokerage-or-data-source.md)),
  persistent optimizer state if more than one replica is needed (5), user management in the gateway (4), and a warm
  Launcher process pool to cut the per-job overhead (3).
