# Sequence: submit backtest → worker → results → report (contract v1)

Happy path with the acknowledgement points of ADR 0002 and the MinIO references of ADR 0005. `J` is the
backtest job id, `R` the report job id. Message bodies: `../messages/v1/`; examples with the same ids:
`../messages/v1/examples/`.

```mermaid
sequenceDiagram
  autonumber
  actor U as Browser (console)
  participant GW as api-gateway
  participant MD as market-data
  participant OR as job-orchestrator
  participant PG as PostgreSQL
  participant MQ as RabbitMQ
  participant BW as backtest-worker
  participant L as Launcher child (bus Messaging adapter)
  participant S3 as MinIO
  participant RS as results
  participant RP as report

  U->>GW: POST /api/v1/backtests (BacktestSpec)
  GW->>MD: GET /internal/v1/data/coverage
  MD-->>GW: Coverage (nothing missing)
  GW->>OR: POST /internal/v1/jobs {kind: backtest}
  OR->>PG: one transaction: insert job J (queued, attempt 1) + outbox row
  OR-->>GW: 201 Job J
  GW-->>U: 201 Job J (Location /api/v1/jobs/J)
  U->>GW: GET /api/v1/jobs/J/events (EventSource)
  GW->>MQ: declare sse.* queue (exclusive, auto-delete), bind job.*.J.#
  GW-->>U: event snapshot (Job J queued)

  OR->>MQ: outbox relay: lean.jobs / jobs.backtest.v1 job.submitted (persistent)
  MQ-->>OR: publisher confirm
  OR->>PG: mark outbox row sent
  MQ->>BW: deliver jobs.backtest (prefetch = free slots, stays unacked)
  BW->>OR: POST /internal/v1/jobs/J/attempts {deliveryCount: 0}
  OR-->>BW: attempt 1, prefix lean-results/J/1/
  BW->>MQ: lean.events job.backtest.J.status.v1 job.started (confirm)
  MQ->>OR: orchestrator.status: job.started (J running)
  MQ->>GW: sse queue: job.started
  GW-->>U: event status
  BW->>L: spawn Launcher with generated config.json (messaging-handler = bus adapter)

  loop every partial packet (off the tick loop, result handler thread)
    L->>MQ: job.backtest.J.result.v1 result.packet inline BacktestResult (Progress < 1, < 256 KiB)
    L->>MQ: job.backtest.J.progress.v1 job.progress (transient)
    MQ->>RS: results.packets: partial result (latest runtime statistics)
    MQ->>GW: sse queue: progress
    GW-->>U: event progress
  end
  L->>MQ: job.backtest.J.order.v1 / log.v1 inline OrderEvent, Log packets
  MQ->>RS: results.packets: store order events, logs
  par heartbeats
    BW->>MQ: job.backtest.J.heartbeat.v1 job.heartbeat every 30 s
    MQ->>OR: orchestrator.status: deadline check
  end

  L-->>BW: process exited (exit code 0, or -6 after a complete Python result)
  BW->>S3: upload results folder to lean-results/J/1/ (result JSON 3.69 MB for futures, -log.txt, order events)
  S3-->>BW: ETags, sha256 computed by the worker
  BW->>MQ: job.backtest.J.result.v1 result.packet PacketRef {final: true, object: lean-results/J/1/Algo.json}
  BW->>MQ: job.backtest.J.status.v1 job.completed {result: ObjectRef, artifacts, statistics}
  MQ-->>BW: publisher confirms
  BW->>MQ: ack jobs.backtest delivery
  MQ->>OR: orchestrator.status: job.completed (J completed)
  MQ->>RS: results.packets: PacketRef / job.completed
  RS->>S3: GET lean-results/J/1/Algo.json (verify size + sha256)
  RS->>PG: index statistics, orders, chart names (idempotent on message_id)
  MQ->>GW: sse queue: job.completed
  GW-->>U: event status (completed), stream ends
  U->>GW: GET /api/v1/jobs/J/result
  GW->>RS: GET /internal/v1/results/J
  RS-->>GW: ResultSummary (statistics, result ObjectRef)
  GW-->>U: 200 ResultSummary

  U->>GW: POST /api/v1/reports {sourceJobId: J}
  GW->>OR: POST /internal/v1/jobs {kind: report}
  OR->>RS: GET /internal/v1/results/J (completed? result ObjectRef)
  RS-->>OR: ResultSummary
  OR->>PG: insert job R + outbox row
  OR-->>GW: 201 Job R
  GW-->>U: 201 Job R
  OR->>MQ: lean.jobs / jobs.report.v1 report.requested {backtestResult: ObjectRef}
  MQ->>RP: deliver jobs.report (one render per replica)
  RP->>OR: POST /internal/v1/jobs/R/attempts
  RP->>MQ: job.report.R.status.v1 job.started
  RP->>S3: GET lean-results/J/1/Algo.json
  RP->>RP: Report.Report (unchanged) renders HTML/PDF
  RP->>S3: PUT lean-reports/R/1/report.html, report.pdf
  RP->>MQ: job.report.R.status.v1 job.completed {result: report.html, artifacts: [report.pdf]}
  MQ-->>RP: confirm
  RP->>MQ: ack jobs.report delivery
  MQ->>OR: orchestrator.status: R completed
  U->>GW: GET /api/v1/reports/R/html
  GW->>RP: GET /internal/v1/reports/R
  RP-->>GW: Report {html: ObjectRef}
  GW-->>U: 302 presigned lean-reports URL
```

Failure paths (not drawn): a worker crash before the ack redelivers the dispatch; the next worker calls
`POST /internal/v1/jobs/J/attempts` with `deliveryCount ≥ 1` and writes to `lean-results/J/2/`. After 3
deliveries the message dead-letters to `jobs.backtest.dead` and the orchestrator publishes `job.failed`
(`final: true`). Cancel: `POST /api/v1/jobs/J/cancel` → orchestrator → `lean.control` `job.J.cancel.v1` →
worker stops the child, uploads what exists, publishes `job.cancelled`, acks.
