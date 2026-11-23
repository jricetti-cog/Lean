# Context diagram (contract v1, as built)

Services and their contracts as merged on `master` (ADR 0006 lists where this differs from ADRs 0001–0005). Solid
arrows: HTTP (OpenAPI under `../openapi/`); dashed: RabbitMQ (exchange, routing key, schema under `../messages/v1/`);
dotted: storage (ADR 0003/0004) and container control.

```mermaid
flowchart LR
  user([User / browser])
  research([research<br/>JupyterLab + QuantBook])

  subgraph svc[Services]
    console[console<br/>React + nginx /api proxy]
    gw[api-gateway<br/>/api/v1 + SSE, /openapi]
    orch[job-orchestrator<br/>/internal/v1/jobs, outbox, leases,<br/>live deployer]
    md[market-data<br/>/internal/v1/data, fetch tasks,<br/>live fan-out]
    res[results<br/>/internal/v1/results]
    opt[optimizer<br/>/internal/v1/optimizations<br/>state in memory]
    rep[report<br/>/internal/v1/reports]
    bw[backtest-worker x N<br/>1 job each, 1 Launcher process per attempt]
    lw[lean-live-jobId<br/>1 live-worker container per deployment]
  end

  subgraph mq[RabbitMQ vhost lean]
    xjobs{{lean.jobs<br/>direct}}
    xdlx{{lean.jobs.dlx<br/>direct}}
    xev{{lean.events<br/>topic}}
    xctl{{lean.control<br/>topic}}
    xmd{{lean.marketdata.live<br/>topic}}
  end

  pg[(PostgreSQL<br/>jobs / results / marketdata)]
  minio[(MinIO<br/>lean-algorithms / lean-results / lean-reports)]
  vol[(lean-data volume)]
  rt[[Docker API via docker-socket-proxy<br/>or Kubernetes API]]
  otel[(OTel Collector → Prometheus,<br/>Tempo, Loki, Grafana)]

  user -->|HTTP| console
  console -->|/api proxy, API key| gw
  user -->|REST, SSE| gw
  research -->|REST| md
  research -->|REST| res
  gw -->|jobs, cancel, live| orch
  gw -->|results, charts, orders, logs, live state| res
  gw -->|coverage, history, fetches| md
  gw -->|optimizations| opt
  gw -->|reports| rep
  opt -->|child backtests POST /internal/v1/jobs| orch
  bw -->|attempts| orch
  lw -->|lease, heartbeats| orch
  lw -->|last LiveResult on restart| res
  rep -->|result ObjectRef| res
  orch -.->|create / delete lean-live-jobId| rt
  rt -.-> lw

  orch -.->|outbox: job.submitted on jobs.backtest / jobs.live,<br/>optimization.requested, report.requested| xjobs
  md -.->|marketdata.fetch task| xjobs
  xjobs -.->|jobs.backtest| bw
  xjobs -.->|jobs.live| orch
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
  md -.->|md.securityType.market.ticker| xmd
  xmd -.->|BusDataQueueHandler| lw

  orch -.- pg
  res -.- pg
  md -.- pg
  gw -.-|upload, presign| minio
  bw -.-|upload results| minio
  lw -.- minio
  res -.-|read result JSON| minio
  rep -.-|read results, write reports| minio
  md -.-|read-write| vol
  bw -.-|read-only| vol
  lw -.-|read-only| vol
  rep -.-|read-only| vol
  research -.-|read-only| vol
  svc -.->|OTLP traces, metrics, logs| otel
```

Not drawn: every service's `/healthz` and `/readyz`; the toolbox one-shot jobs, which write `lean-data` through a
staging copy like market-data.
