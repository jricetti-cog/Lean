# RabbitMQ topology (contract v1)

Implements ADR 0002 ("Asynchronous: RabbitMQ"). Machine-readable form: [`definitions.json`](definitions.json)
(RabbitMQ definitions export, loaded at broker start) and [`rabbitmq.conf`](rabbitmq.conf). Body schemas:
[`../messages/v1/`](../messages/v1/).

All objects live in vhost `lean`. Every routing key ends in the contract major version (`.v1`); a breaking
change adds `.v2` keys, `*.v2` AMQP types and, for dispatch, `v2` queues side by side, and both run until no
`v1` producer is left. Changes within `v1` are additive only (new optional fields, new enum values that
consumers can ignore, new event types).

## Limits

| Limit | Value | Where enforced |
|---|---|---|
| **Maximum inline message body** | **256 KiB (262 144 bytes)** of serialized JSON | Publisher (contract). Anything larger goes to MinIO and is sent as a `PacketRef` / `ObjectRef` |
| Broker hard cap | 1 MiB | `max_message_size` in `rabbitmq.conf` (safety net, a publish above it fails) |
| Final results | never inline, whatever the size | `job.completed.result`, `result.packet` with `final: true` |
| `consumer_timeout` | 7 h | `rabbitmq.conf`; above the orchestrator's maximum backtest runtime (6 h) |
| Dispatch redeliveries | 3 deliveries, then dead-lettered | `x-delivery-limit` on the quorum queues |

Why 256 KiB: ADR 0005 measured 19–43 KB result messages for the small algorithms and 3.69 MB for the futures
algorithm, where publishing the result inline cost +12% engine time. Partial result packets (progress,
runtime statistics, a chart update) and log/order/status messages are far below 256 KiB; the limit keeps them
inline while forcing anything result-sized out of the broker. The `Messaging` adapter measures the serialized
packet before publishing; above the limit it uploads the same JSON to
`lean-results/<jobId>/<attempt>/packets/<sequence>.json` and publishes a `PacketRef` (`format: lean-packet`).

## Exchanges

| Exchange | Type | Durable | Purpose |
|---|---|---|---|
| `lean.jobs` | direct | yes | Work dispatch to competing consumers |
| `lean.jobs.dlx` | direct | yes | Dead letters after the delivery limit (and poison messages of the event consumers) |
| `lean.events` | topic | yes | Status, heartbeat, progress, result, log, order and error streams per job |
| `lean.control` | topic | yes | Commands to a running job (cancel, liquidate) |
| `lean.marketdata.live` | topic | no | Live data fan-out (ADR 0004) |

## Dispatch queues (`lean.jobs`)

All are quorum queues with `x-dead-letter-exchange: lean.jobs.dlx`, `x-dead-letter-strategy: at-least-once`,
`x-overflow: reject-publish` (a full queue nacks the publish, the outbox relay retries) and
`x-delivery-limit: 3`. Messages are persistent (`delivery_mode 2`) and published with confirms.

| Queue | Routing key | AMQP type | Body schema | Publisher | Consumer | Dead-letter queue (routing key) |
|---|---|---|---|---|---|---|
| `jobs.backtest` | `jobs.backtest.v1` | `jobs.v1.BacktestRequested` | `job.submitted` (`kind: backtest`) | job-orchestrator outbox relay | backtest-worker | `jobs.backtest.dead` (`jobs.backtest.dead.v1`) |
| `jobs.live` | `jobs.live.v1` | `jobs.v1.LiveRequested` | `job.submitted` (`kind: live`) | job-orchestrator outbox relay | live-worker | `jobs.live.dead` (`jobs.live.dead.v1`) |
| `jobs.optimization` | `jobs.optimization.v1` | `jobs.v1.OptimizationRequested` | `optimization.requested` | job-orchestrator outbox relay | optimizer | `jobs.optimization.dead` (`jobs.optimization.dead.v1`) |
| `jobs.report` | `jobs.report.v1` | `jobs.v1.ReportRequested` | `report.requested` | job-orchestrator outbox relay | report | `jobs.report.dead` (`jobs.report.dead.v1`) |
| `marketdata.fetch` | `marketdata.fetch.v1` | `jobs.v1.MarketDataFetchRequested` | `marketdata.fetch.requested` | market-data outbox relay | market-data task runner | `marketdata.fetch.dead` (`marketdata.fetch.dead.v1`) |

Dead-letter queues are durable quorum queues without consumers; the orchestrator watches their depth and the
`failed` (final) event, operators inspect/replay them in the management UI.

## Event routing (`lean.events`)

Routing key: `job.<kind>.<jobId>.<event>.v1`, `<kind>` ∈ `backtest | live | optimization | report |
marketdata`, `<event>` ∈ `status | heartbeat | progress | result | log | order | error` (ADR 0002).

| `<event>` | AMQP type(s) | Body schema | Persistent | Publisher |
|---|---|---|---|---|
| `status` | `events.v1.JobStarted`, `events.v1.JobCompleted`, `events.v1.JobFailed`, `events.v1.JobCancelled` | `job.started`, `job.completed`, `job.failed`, `job.cancelled` | yes, confirms | worker host / optimizer / report / market-data |
| `heartbeat` | `events.v1.JobHeartbeat` | `job.heartbeat` | no | worker host (every 10 s live, 30 s other kinds); live carries `leaseEpoch` |
| `progress` | `events.v1.JobProgress` | `job.progress` | no | bus `Messaging` adapter (per partial result packet), optimizer |
| `result` | `packets.v1.BacktestResult`, `packets.v1.LiveResult`, `packets.v1.AlgorithmStatus`, `packets.v1.PacketRef` | `result.packet` | yes, confirms | bus `Messaging` adapter; final `PacketRef` by the worker host after upload |
| `log` | `packets.v1.Log`, `packets.v1.Debug`, `packets.v1.SystemDebug` | `result.packet` | yes | bus `Messaging` adapter |
| `order` | `packets.v1.OrderEvent` | `result.packet` | yes, confirms | bus `Messaging` adapter |
| `error` | `packets.v1.RuntimeError`, `packets.v1.HandledError` | `result.packet` | yes, confirms | bus `Messaging` adapter |

### Event consumer queues

Durable quorum queues, `x-delivery-limit: 5`, dead-lettered to `lean.jobs.dlx` (`<queue>.dead.v1` →
`<queue>.dead`). Consumers are idempotent on `message_id`.

| Queue | Bindings | Consumer |
|---|---|---|
| `results.packets` | `job.*.*.result.v1`, `job.*.*.order.v1`, `job.*.*.log.v1`, `job.*.*.error.v1`, `job.*.*.status.v1` | results |
| `orchestrator.status` | `job.*.*.status.v1`, `job.*.*.heartbeat.v1`, `job.*.*.progress.v1` | job-orchestrator |
| `optimizer.children` | `job.backtest.*.status.v1` (filters on `parent-job-id` header) | optimizer |
| server-named, exclusive, auto-delete (`sse.<gatewayInstance>.<uuid>`) | `job.*.<jobId>.#` | api-gateway, one per open `GET /api/v1/jobs/{id}/events` |

`results.packets` also binds `status` so the results service finalises a result from `job.completed` even
when it missed the final `PacketRef`. The SSE queues are declared by the gateway at request time (not in
`definitions.json`); policy `sse-and-control-transient` caps them at 64 MiB with `drop-head` so a slow
browser drops old progress rather than growing the broker.

## Control (`lean.control`)

| Routing key | AMQP type | Body schema | Queue |
|---|---|---|---|
| `job.<jobId>.cancel.v1` | `control.v1.CancelJob` | `job.cancel` | `control.<workerId>.<jobId>`: classic, exclusive, auto-delete, declared by the worker for each running job |
| `job.<jobId>.liquidate.v1` | `control.v1.LiquidateJob` | `job.liquidate` | same queue (live deployments only) |

## Live market data (`lean.marketdata.live`)

Routing key `md.<securityType>.<market>.<ticker>.v1` (lower-case, e.g. `md.equity.usa.spy.v1`), transient
messages, consumer queues `md.<workerId>.<uuid>` are classic, exclusive, auto-delete with
`x-message-ttl: 5000`. Body: [marketdata.live](../messages/v1/marketdata.live.schema.json), AMQP type
`marketdata.v1.LiveData`, a batch of ticks/bars for one symbol; the envelope `job-id` is the market-data feed id.
Published without `mandatory` (no bound worker = dropped).

## Envelope

AMQP basic properties, schema [`envelope.schema.json`](../messages/v1/envelope.schema.json):

| Property | Value |
|---|---|
| `message_id` | UUID, new per publish (outbox row id for dispatch; replays keep it) |
| `correlation_id` | job id |
| `type` | `<family>.v1.<Name>` (tables above); selects the body schema |
| `content_type` | `application/json` |
| `delivery_mode` | 2 (persistent) for dispatch, status, result, order, error, log; 1 for progress, heartbeat |
| `timestamp` | publish time (Unix seconds) |
| headers | `contract-version: 1`, `job-id`, `attempt` (always); `job-kind`, `parent-job-id`, `lease-epoch` (live), `packet-type` (packets), `sequence` (per-attempt publish counter used for ordering) |

## Publishing and acknowledgements

* **Outbox + confirms.** The orchestrator (and market-data for fetch tasks) writes the job row and an outbox
  row in one PostgreSQL transaction; the relay publishes with publisher confirms and marks the row sent on
  ack, retrying on nack/timeout. Workers publish status, result, order and error with confirms.
* **Backtest, report, optimization, fetch:** prefetch = free slots. The consumer acks the dispatch **after**
  the child process exited, artifacts are uploaded to `lean-results/<jobId>/<attempt>/` and the terminal
  `status` event (`job.completed` / `job.failed` / `job.cancelled`) is confirmed. Success is judged by the
  confirmed final result upload and `job.completed`, not by the exit code (ADR 0005: Python jobs can exit −6
  after publishing complete results). A crash before the ack → redelivery (`x-delivery-count` ≥ 1) → the
  consumer asks the orchestrator for a new attempt (`POST /internal/v1/jobs/{id}/attempts`) and writes to
  the new attempt prefix. After 3 deliveries the queue dead-letters the message to `jobs.<kind>.dead`, the
  orchestrator marks the job `failed` and publishes `job.failed` with `final: true`.
* **Live:** the live-worker acquires a lease (`POST /internal/v1/jobs/{id}/lease`) and acks the dispatch as
  soon as it is granted (`running`, epoch N). Liveness is the `heartbeat` stream. Missing heartbeats →
  `stale`, `job.<id>.cancel.v1` with epoch N, redeploy as epoch N+1 only when `redeployOnStale` allows it. A
  worker with an older epoch stops its process.
* **Event consumers** ack after their database transaction commits; poison messages are rejected without
  requeue after the delivery limit and land in `<queue>.dead`.
* **Ordering** is per job per queue only; results orders packets by `sequence`/progress.

## Message catalogue

| Contract | Schema | AMQP type | Exchange / routing key |
|---|---|---|---|
| `job.submitted` | [job.submitted](../messages/v1/job.submitted.schema.json) | `jobs.v1.BacktestRequested`, `jobs.v1.LiveRequested` | `lean.jobs` / `jobs.backtest.v1`, `jobs.live.v1` |
| `job.started` | [job.started](../messages/v1/job.started.schema.json) | `events.v1.JobStarted` | `lean.events` / `job.<kind>.<jobId>.status.v1` |
| `job.progress` | [job.progress](../messages/v1/job.progress.schema.json) | `events.v1.JobProgress` | `lean.events` / `job.<kind>.<jobId>.progress.v1` |
| `job.completed` | [job.completed](../messages/v1/job.completed.schema.json) | `events.v1.JobCompleted` | `lean.events` / `job.<kind>.<jobId>.status.v1` |
| `job.failed` | [job.failed](../messages/v1/job.failed.schema.json) | `events.v1.JobFailed` | `lean.events` / `job.<kind>.<jobId>.status.v1` |
| `result.packet` | [result.packet](../messages/v1/result.packet.schema.json) | `packets.v1.<PacketType>`, `packets.v1.PacketRef` | `lean.events` / `job.<kind>.<jobId>.{result,log,order,error}.v1` |
| `optimization.requested` | [optimization.requested](../messages/v1/optimization.requested.schema.json) | `jobs.v1.OptimizationRequested` | `lean.jobs` / `jobs.optimization.v1` |
| `report.requested` | [report.requested](../messages/v1/report.requested.schema.json) | `jobs.v1.ReportRequested` | `lean.jobs` / `jobs.report.v1` |
| `job.heartbeat` | [job.heartbeat](../messages/v1/job.heartbeat.schema.json) | `events.v1.JobHeartbeat` | `lean.events` / `job.<kind>.<jobId>.heartbeat.v1` |
| `job.cancelled` | [job.cancelled](../messages/v1/job.cancelled.schema.json) | `events.v1.JobCancelled` | `lean.events` / `job.<kind>.<jobId>.status.v1` |
| `job.cancel` | [job.cancel](../messages/v1/job.cancel.schema.json) | `control.v1.CancelJob` | `lean.control` / `job.<jobId>.cancel.v1` |
| `job.liquidate` | [job.liquidate](../messages/v1/job.liquidate.schema.json) | `control.v1.LiquidateJob` | `lean.control` / `job.<jobId>.liquidate.v1` |
| `marketdata.fetch.requested` | [marketdata.fetch.requested](../messages/v1/marketdata.fetch.requested.schema.json) | `jobs.v1.MarketDataFetchRequested` | `lean.jobs` / `marketdata.fetch.v1` |
| `marketdata.live` | [marketdata.live](../messages/v1/marketdata.live.schema.json) | `marketdata.v1.LiveData` | `lean.marketdata.live` / `md.<securityType>.<market>.<ticker>.v1` |

The last four complete ADR 0002's event set (heartbeat, cancellation, fetch tasks) beyond the eight contracts
named in plan step s1.4.
