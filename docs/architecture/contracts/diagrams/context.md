# Context diagram (contract v1)

Services and their contracts. Solid arrows: HTTP (OpenAPI under `../openapi/`); dashed: RabbitMQ (exchange,
routing key, schema under `../messages/v1/`); dotted: storage (ADR 0003/0004).

```mermaid
flowchart LR
  user([User / browser<br/>React console])
  research([Research<br/>Jupyter QuantBook])

  subgraph svc[Services]
    gw[api-gateway<br/>/api/v1 + SSE]
    orch[job-orchestrator<br/>/internal/v1/jobs]
    md[market-data<br/>/internal/v1/data]
    res[results<br/>/internal/v1/results]
    opt[optimizer<br/>/internal/v1/optimizations]
    rep[report<br/>/internal/v1/reports]
    bw[backtest-worker<br/>1 Launcher process per job]
    lw[live-worker<br/>1 Launcher process per deployment]
  end

  subgraph mq[RabbitMQ vhost lean]
    xjobs{{lean.jobs<br/>direct}}
    xdlx{{lean.jobs.dlx<br/>direct}}
    xev{{lean.events<br/>topic}}
    xctl{{lean.control<br/>topic}}
  end

  pg[(PostgreSQL<br/>per-service DBs)]
  minio[(MinIO<br/>lean-algorithms / lean-results / lean-reports)]
  vol[(lean-data volume)]

  user -->|HTTPS REST, SSE GET /api/v1/jobs/id/events| gw
  research -->|REST| gw
  gw -->|jobs, cancel| orch
  gw -->|results, charts, orders, logs| res
  gw -->|coverage, history, fetches| md
  gw -->|optimization state| opt
  gw -->|report metadata| rep
  opt -->|child backtests POST /internal/v1/jobs| orch
  bw -->|attempts| orch
  lw -->|attempts, lease| orch
  bw -.->|fetch-on-miss POST /internal/v1/data/fetches| md

  orch -.->|outbox: jobs.backtest.v1 / jobs.live.v1 job.submitted<br/>jobs.optimization.v1 optimization.requested<br/>jobs.report.v1 report.requested| xjobs
  md -.->|marketdata.fetch.v1| xjobs
  xjobs -.->|jobs.backtest| bw
  xjobs -.->|jobs.live| lw
  xjobs -.->|jobs.optimization| opt
  xjobs -.->|jobs.report| rep
  xjobs -.->|marketdata.fetch| md
  xjobs -.->|after 3 deliveries| xdlx
  xdlx -.-> dlq[[jobs.kind.dead]]

  bw -.->|job.kind.id.status/heartbeat/progress/result/log/order/error.v1| xev
  lw -.-> xev
  opt -.->|status, progress| xev
  rep -.->|status| xev
  md -.->|status| xev
  xev -.->|results.packets| res
  xev -.->|orchestrator.status| orch
  xev -.->|optimizer.children| opt
  xev -.->|sse.* per request| gw
  orch -.->|job.id.cancel.v1| xctl
  xctl -.-> bw
  xctl -.-> lw

  orch -.- pg
  res -.- pg
  md -.- pg
  opt -.- pg
  gw -.-|upload, presign| minio
  bw -.-|upload results| minio
  lw -.- minio
  res -.-|read result JSON| minio
  rep -.-|read results, write reports| minio
  md -.-|read-write| vol
  bw -.-|read-only| vol
  lw -.-|read-only| vol
  research -.-|read-only| vol
```
