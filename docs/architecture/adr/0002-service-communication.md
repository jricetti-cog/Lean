# ADR 0002: Communication — REST + OpenAPI and RabbitMQ

* Status: Accepted; as-built differences in [ADR 0006](0006-as-built.md)
* Plan step: `s1.2`
* Decision taken: **RabbitMQ** as the message broker (approved plan).

## Context

Two kinds of traffic exist after the split (ADR 0001):

* **Request/response** — clients (React console, CLI, research notebooks) submit jobs and query jobs, results,
  reports and data; the gateway asks the owning service.
* **Asynchronous work and streams** — job dispatch to a pool of workers where each job must be processed by
  exactly one worker and survive crashes; and a high-volume stream of progress, logs, order events and
  results per running job (`IMessagingHandler.Send(packet)` is already called off the tick loop, on the result
  handler thread, §4.4). Engine packets already have a type discriminator (`Packet.Type`, `PacketType`) and a
  JSON form.

Job lengths range from seconds (small backtests) to weeks (live). RabbitMQ closes a channel whose delivery
stays unacknowledged longer than `consumer_timeout` (30 minutes by default), which matters for ack timing.

## Decision

### Synchronous: REST + OpenAPI

* ASP.NET Core **minimal APIs on .NET 10**; every service publishes its OpenAPI document with the built-in
  `Microsoft.AspNetCore.OpenApi` at `/openapi/v1.json`. The gateway's document is the public contract; the
  console's TypeScript client is generated from it.
* Public routes live only on **api-gateway** under `/api/v1/...` (jobs, backtests, live deployments,
  optimizations, results, reports, data). Internal services expose `/internal/v1/...` on the private network
  only; the gateway authenticates the caller and forwards to the owner (orchestrator, results, market-data,
  report). No service-to-service calls bypass ownership (ADR 0001).
* Live progress reaches the browser as **Server-Sent Events** from the gateway
  (`GET /api/v1/jobs/{id}/events`), backed by a server-named, exclusive, auto-delete queue bound to that job's
  event topics. The browser never talks to RabbitMQ.
* Errors use RFC 9457 problem details; ids are UUIDs; timestamps UTC ISO-8601.

### Asynchronous: RabbitMQ

| Exchange | Type | Purpose | Routing keys / queues |
|---|---|---|---|
| `lean.jobs` | direct, durable | Work dispatch (competing consumers) | queues `jobs.backtest`, `jobs.live`, `jobs.report`, `jobs.optimization`, `marketdata.fetch` (quorum queues, persistent messages) |
| `lean.jobs.dlx` | direct, durable | Dead letters after max attempts | `jobs.<kind>.dead` |
| `lean.events` | topic, durable | Progress, status and result streaming | `job.<kind>.<jobId>.<event>`, `<event>` ∈ `status`, `heartbeat`, `progress`, `result`, `log`, `order`, `error` |
| `lean.control` | topic, durable | Commands to a running job | `job.<jobId>.cancel` (worker binds one queue per running job) |
| `lean.marketdata.live` | topic, non-durable | Live data fan-out (ADR 0004) | `md.<securityType>.<market>.<ticker>`; transient queues with message TTL |

Consumers of `lean.events`: **results** (`job.*.*.result`, `job.*.*.order`, `job.*.*.log`,
`job.*.*.error`), **job-orchestrator** (`job.*.*.status`, `job.*.*.heartbeat`), **optimizer** (status of its child
backtests), and the gateway's per-request SSE queues (`job.*.<jobId>.#`).

**Message format.** Body is JSON. For engine packets the body is the packet's existing JSON serialization, so
the results service deserializes the same `BacktestResultPacket`/`LiveResultPacket`/`LogPacket`/
`OrderEventPacket` types the monolith produces. AMQP properties: `message_id` (UUID), `correlation_id` (job id),
`type` (`jobs.v1.BacktestRequested`, `packets.v1.BacktestResult`, …), `content_type: application/json`, headers
`contract-version`, `job-id`, `attempt`. Contracts are additive within `v1`; a breaking change adds `v2`
types/queues side by side (§5.1 versioning rule).

**Publishing.** The orchestrator writes the job row and an outbox row in one PostgreSQL transaction and a relay
publishes outbox rows with **publisher confirms**, so a committed job is never lost and a published job always
exists in the database. Workers publish with confirms as well.

**Acks and retries — backtest, report, optimization, fetch jobs:**

* Prefetch = free slots on the worker. The worker **acks after** the child process exited, artifacts are
  uploaded (ADR 0003) and the terminal `status` event is confirmed. A crash before that → redelivery →
  orchestrator bumps `attempt`; work is idempotent by `(jobId, attempt)` because every attempt writes to its own
  artifact prefix.
* After the configured max attempts the message is rejected to `lean.jobs.dlx` and the job becomes `failed`.
* Broker `consumer_timeout` is set above the maximum job runtime allowed by the orchestrator, which enforces
  its own deadline via heartbeats and cancels overdue jobs.

**Acks — live jobs (different on purpose):** a live algorithm must never run twice (double orders). The
live-worker acks the dispatch **as soon as the orchestrator grants it a lease** (`running`, lease epoch N).
Liveness is then tracked by `heartbeat` events, not by an unacked delivery. If heartbeats stop, the orchestrator
marks the deployment `stale`, publishes `job.<id>.cancel` with epoch N, and only redeploys (epoch N+1) when the
policy allows it; a worker holding an older epoch stops its process.

**Cancellation.** Gateway → orchestrator (REST) → `lean.control` `job.<jobId>.cancel`. The worker running the
job terminates its child process, uploads what exists and reports `cancelled`.

**Bus adapters in LEAN.** The job's child `Launcher` publishes through a `Messaging` subclass selected by
`messaging-handler` in the generated config (ADR 0001). It keeps the base logging (so container logs match the
monolith) and publishes every packet to `lean.events`. It lives outside `Engine`; the only change to existing
code it may need is making `Messaging.Dispose` virtual (§5.1). Default `config.json` keeps
`QuantConnect.Messaging.Messaging`.

## Consequences

* RabbitMQ's management UI shows queues, rates and dead letters, which makes the flow visible in the demo.
* Workers scale on `jobs.<kind>` depth, the orchestrator's scaling hint.
* At-least-once delivery everywhere: results and orchestrator consumers are idempotent on `message_id`.
* Ordering is only guaranteed per job per queue; the results service orders by packet time/progress, not
  arrival.
* The engine never blocks on the broker: publishing happens on the result-handler thread; if the broker is
  down the adapter buffers in memory up to a bound, then drops progress (never the final result, which is also
  written to disk and uploaded by the worker).

## Alternatives considered

* **NATS JetStream** — lighter, but less familiar to the team and weaker tooling for the demo.
* **Kafka** — durable log and replay we do not need; partition-based consumers fit job dispatch poorly; overkill
  for this job volume.
* **NetMQ broker from the earlier branch (§5.1)** — home-grown request/reply and no durable queues; keep the
  envelope ideas, drop the transport.
* **gRPC between services** — not needed: internal calls are low-rate queries; REST + OpenAPI gives one
  contract style for the console and internals.
* **Ack on receipt for backtests** — simpler, but loses the job on a worker crash; the orchestrator would have
  to re-implement redelivery.
