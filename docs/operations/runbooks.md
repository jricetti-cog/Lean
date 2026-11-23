# Alert runbooks

One section per alert in [`deploy/compose/observability/alerts.yml`](../../deploy/compose/observability/alerts.yml)
(loaded by the `observability` profile's Prometheus; each alert's `runbook_url` points here). Firing alerts are listed
on http://127.0.0.1:9090/alerts and can be queried from Grafana. No Alertmanager is bundled: add an `alerting:` block to
`deploy/compose/observability/prometheus.yml` (or load the same rules as a `PrometheusRule` in a cluster running the
Prometheus Operator) to route them. The rules are unit-tested:

```sh
docker run --rm -v $PWD/deploy/compose/observability:/r -w /r --entrypoint promtool prom/prometheus:v3.2.1 test rules alerts.test.yml
```

Useful everywhere: `make ps`, `docker compose logs --tail 200 <service>`, the Grafana dashboards in folder LEAN
(Platform overview, Service RED, RabbitMQ health, PostgreSQL health), RabbitMQ management on :15672 and
[troubleshooting.md](troubleshooting.md).

| Alert | Severity | Fires when |
|---|---|---|
| [LeanScrapeTargetDown](#leanscrapetargetdown) | critical | job-orchestrator, RabbitMQ, postgres-exporter or the collector not scrapable for 2 min |
| [LeanJobsWaitingNoWorker](#leanjobswaitingnoworker) | critical | backtests queued and no worker busy for 5 min |
| [LeanBacktestBacklog](#leanbacktestbacklog) | warning | more than 2 queued backtests per worker for 15 min |
| [LeanDeadLetters](#leandeadletters) | warning | any message in a `jobs.<kind>.dead` queue |
| [LeanOutboxBacklog](#leanoutboxbacklog) | critical | job-orchestrator outbox rows unpublished for 5 min |
| [LeanJobFailureRate](#leanjobfailurerate) | warning | more than 25% of backtest attempts failed over 15 min (at least 4 attempts) |
| [LeanLiveDeploymentStale](#leanlivedeploymentstale) | critical | a live deployment is `stale` |
| [LeanLiveEngineRestarts](#leanliveenginerestarts) | warning | a live engine exited without a final result in the last 15 min |
| [LeanMessagesDeadLettered](#leanmessagesdeadlettered) | warning | a consumer dead-letters deliveries |
| [LeanHttp5xxRate](#leanhttp5xxrate) | warning | a service answers more than 5% 5xx for 10 min |
| [RabbitMQMemoryHigh](#rabbitmqmemoryhigh) | warning | broker above 80% of its memory watermark for 5 min |
| [RabbitMQDiskLow](#rabbitmqdisklow) | critical | broker below 2 GiB free disk for 5 min |
| [PostgresDown](#postgresdown) | critical | postgres-exporter cannot reach PostgreSQL for 1 min |
| [PostgresConnectionsHigh](#postgresconnectionshigh) | warning | connections above 80% of `max_connections` for 5 min |

## LeanScrapeTargetDown

**Impact.** Depends on the target. `lean-platform` = job-orchestrator: no new jobs are accepted or dispatched and
running jobs cannot claim attempts. `rabbitmq`: the bus may be down (see below). `postgres` = postgres-exporter only:
database metrics are blind. `otel-collector`: service metrics, traces and logs stop arriving.

**Diagnose.** `make ps`; `docker compose logs --tail 200 job-orchestrator` (or the target's service); in Prometheus,
Status > Targets shows the scrape error.

**Fix.** Restart the service (`docker compose up -d <service>`). job-orchestrator that keeps failing its health check
usually cannot reach PostgreSQL or RabbitMQ: fix those first. When it is back, the outbox relay publishes anything
committed meanwhile and heartbeat monitoring resumes; jobs whose heartbeats timed out are retried.

## LeanJobsWaitingNoWorker

**Impact.** Backtests (and optimizations, whose children are backtests) do not progress.

**Diagnose.** `docker compose ps backtest-worker`; in RabbitMQ, `jobs.backtest` consumers = 0 means no worker is
connected; consumers > 0 with ready messages and nobody busy means workers are stuck before claiming
(`docker compose logs backtest-worker`: orchestrator unreachable, `POST /internal/v1/jobs/{id}/attempts` refused, broker
reconnect loop). Kubernetes: `kubectl get scaledobject,hpa,pods -l app.kubernetes.io/name=backtest-worker`.

**Fix.** Start or scale workers (`docker compose up -d --scale backtest-worker=N`). A worker restarting in a loop:
check its memory limit (`BACKTEST_WORKER_MEMORY`) and that `lean-data` is mounted. KEDA not scaling up: check the
ScaledObject's RabbitMQ connection and `minReplicas`.

## LeanBacktestBacklog

**Impact.** Jobs wait longer than they run; users see `queued` for minutes.

**Diagnose.** Platform overview dashboard: queue depth, worker utilisation, job duration p95. A burst (an
optimization, a sweep) is expected to queue; a steady backlog means too few workers.

**Fix.** Add workers ([scaling-workers.md](scaling-workers.md)), or let KEDA scale (raise `maxReplicas`). Lower
`OPTIMIZER_MAX_CONCURRENT_BACKTESTS` if one optimization starves other users. Remember each job costs ~4–5 s of
start-up: many tiny jobs saturate workers quickly.

## LeanDeadLetters

**Impact.** At least one job was delivered `ORCHESTRATOR_MAX_ATTEMPTS` (3) times without being acknowledged and has been
moved to `jobs.<kind>.dead`; job-orchestrator marks it `failed` (`max-attempts-exceeded`).

**Diagnose.** RabbitMQ UI > Queues > `jobs.backtest.dead` > Get messages: the `job-id` header names the job.
`GET /api/v1/jobs/{id}` shows its attempts and error; the engine log of each attempt is under
`lean-results/<jobId>/<attempt>/` and in the worker logs (`docker compose logs backtest-worker | grep <jobId>`).
Typical causes: the worker container killed mid-job (OOM above `BACKTEST_WORKER_MEMORY`, host restart), a job that
crashes the engine process every time, a heartbeat timeout shorter than a pause.

**Fix.** Fix the cause, then resubmit the job (new job id). The dead message is only evidence: purge the dead queue
once inspected (`rabbitmqctl -p lean purge_queue jobs.backtest.dead`); the alert clears when the count is 0.

## LeanOutboxBacklog

**Impact.** Jobs are committed in the `jobs` database but not published to RabbitMQ: they stay `queued` forever.

**Diagnose.** `docker compose logs job-orchestrator | grep -i outbox`; usually the broker is unreachable, the
credentials changed, or publisher confirms are refused (RabbitMQ memory/disk alarm). `select count(*) from outbox
where sent_at is null;` in database `jobs` shows the backlog.

**Fix.** Restore the broker (see [RabbitMQMemoryHigh](#rabbitmqmemoryhigh), [RabbitMQDiskLow](#rabbitmqdisklow)); the
relay retries every `Orchestrator__OutboxPollMilliseconds` (1000) and drains the backlog by itself, in order. Restart job-orchestrator only
if it logs no relay activity at all.

## LeanJobFailureRate

**Impact.** Users' backtests fail.

**Diagnose.** Platform overview > outcomes; `GET /api/v1/jobs?status=failed` and each job's `error.code`:
`compile-error` / `runtime-error` / `algorithm-not-found` are usually user errors (one user, one algorithm);
`engine-crash`, `upload-failed`, `timeout`, `lease-lost`, `internal` across many users point at the platform.
`upload-failed` → MinIO; `engine-crash` on every job right after a deploy → the worker image (try a known regression:
`deploy/compose/smoke-backtest.sh`); RAM-limit `runtime-error`s → limits too low.

**Fix.** Roll back the worker image if a deploy caused it (`IMAGE_TAG` / Helm `image.tag`), fix MinIO or data, raise
limits. Run `Tests/Services.Parity/run-parity.sh` (or a `PARITY_ONLY=` subset) to confirm parity after a fix.

## LeanLiveDeploymentStale

**Impact.** A live algorithm stopped sending heartbeats: it may not be trading, or (worse) be running without being
supervised. The orchestrator has published `job.<id>.cancel` for the old lease epoch; it redeploys with a new epoch unless
the deployment was submitted with `redeployOnStale: false`.

**Diagnose.** `docker ps -a --filter name=lean-live-` (Kubernetes: `kubectl get deploy,pods -l lean.role=live-worker`),
the container's logs, `GET /api/v1/jobs/{id}` (lease epoch, last heartbeat) and `GET /api/v1/live/{id}/state` (last
portfolio). Check the brokerage connection and that the container can reach job-orchestrator and RabbitMQ.

**Fix.** Never start a second copy by hand: a live algorithm must not run twice. Either let the redeploy policy act,
or stop it (`POST /api/v1/live/{id}/stop`, `liquidate: true` to flatten) and deploy again; the new deployment restores
holdings and cash from results.

## LeanLiveEngineRestarts

**Impact.** A live engine process exited without a final result and the live-worker restarted it (the container
restart policy allows `Orchestrator__LiveDeployment__MaxRestarts`, 5). Positions are restored from the last
`LiveResult`, but orders in flight at the crash may need checking at the brokerage.

**Diagnose.** The container's logs around the restart (engine log, exit code), `lean.live.runs` by outcome in
Prometheus, the brokerage's order history.

**Fix.** Fix the cause (algorithm exception, brokerage disconnect, memory: `LIVE_WORKER_MEMORY_MB`). After repeated
restarts the deployment fails; stop it and redeploy once fixed.

## LeanMessagesDeadLettered

**Impact.** A consumer rejected deliveries as poison (unparseable, wrong contract version, permanently failing). For
`results.packets` that means missing results/progress; for `orchestrator.status`, missed state changes.

**Diagnose.** Service RED dashboard > deliveries by disposition; the service's logs at the time name the message type
and error; the message itself is in the queue's dead-letter queue (RabbitMQ UI). A contract mismatch after deploying
one service but not another is the usual cause.

**Fix.** Deploy matching service versions (contracts are additive within v1, but a producer newer than a strict
consumer can still trip it), fix the producer, then shovel or purge the dead messages.

## LeanHttp5xxRate

**Impact.** API calls fail for users or between services.

**Diagnose.** Service RED dashboard (requests, errors and latency by route); the gateway answers `503
application/problem+json` naming the upstream that is down; Tempo: open a failing request's trace by its `X-Request-ID`.

**Fix.** Restore the failing upstream (often PostgreSQL or MinIO behind results/report); scale stateless services if
they are saturated (CPU in Service RED).

## RabbitMQMemoryHigh

**Impact.** At 100% of the watermark RabbitMQ blocks every publisher: workers cannot publish results, the orchestrator
cannot dispatch.

**Diagnose.** RabbitMQ health dashboard (memory, queue sizes); `rabbitmq-diagnostics memory_breakdown`. Usually a queue
without consumers is growing (`results.packets` with results down, a forgotten test queue, `sse.*` queues of stuck
clients).

**Fix.** Restore the consumer or delete the stray queue; raise `RABBITMQ_MEMORY` (the container limit, the watermark
follows it) if the steady state needs it.

## RabbitMQDiskLow

**Impact.** Below `disk_free_limit` RabbitMQ blocks publishers, as above.

**Diagnose.** `docker system df`, `df -h` on the host; the broker's data volume `lean_rabbitmq-data`.

**Fix.** Free host disk (old images and build cache: `docker image prune`, `docker builder prune`; foundation-based
images are large), drain or purge large queues.

## PostgresDown

**Impact.** job-orchestrator cannot accept or update jobs, results cannot ingest, market-data cannot record fetches:
effectively a platform outage. Running engines keep running and their messages stay queued in RabbitMQ.

**Diagnose.** `docker compose ps postgres`, `docker compose logs --tail 200 postgres` (disk full, corrupt volume, OOM).
If postgres is healthy, postgres-exporter's own connection is broken (`docker compose logs postgres-exporter`).

**Fix.** Restart postgres; free disk. When it is back, services reconnect, consumers process the queued messages, and
the outbox drains.

## PostgresConnectionsHigh

**Impact.** New connections will be refused at `max_connections`: services fail health checks or requests.

**Diagnose.** PostgreSQL health dashboard: connections by database and state; `select datname, state, count(*) from
pg_stat_activity group by 1, 2;`. Many replicas × Npgsql pool size, or idle-in-transaction sessions.

**Fix.** Reduce replicas or the pool size (`Maximum Pool Size=` in the `ConnectionStrings__*` value), terminate stuck
sessions, or raise `max_connections` on the server.
