# Compose infrastructure (plan step s2.3)

The root [`docker-compose.yml`](../../docker-compose.yml) runs the infrastructure every LEAN service needs
(ADR 0002–0004). From the repository root:

```bash
cp .env.example .env                              # optional: every setting has a local-development default
docker compose --profile infra up -d --wait       # blocks until every container is healthy (or completed)
sh deploy/compose/verify-infra.sh                 # smoke test: health, ports, credentials, isolation
docker compose --profile infra down               # keep data; add -v to delete the named volumes
```

End-to-end smoke test (plan step s2.4, also run by `.github/workflows/services.yml`): with the infra profile up and
the images built (`docker buildx bake -f Services/docker-bake.hcl`), `sh deploy/compose/smoke-backtest.sh` submits
`BasicTemplateAlgorithm` as a `jobs.v1.BacktestRequested` to the stock Launcher in the `backtest-worker` image (bus
handlers selected by config) and asserts that it completes with its expected statistics, publishes its events and final
`PacketRef` on `lean.events`, and stores its result file in `lean-results`. `SMOKE_IMAGE` / `REGISTRY` + `TAG` pick
the image.

Job orchestrator (plan step s4.1): `docker compose --profile infra --profile orchestrator up -d --wait --build` adds
`job-orchestrator` on 127.0.0.1:5081 (`ORCHESTRATOR_PORT`). `sh deploy/compose/verify-orchestrator.sh` plays the worker
side over HTTP and the RabbitMQ management API: upload to MinIO, validation, idempotency, queued → dispatched → running →
completed, cancellation, heartbeat-timeout retries up to max attempts, and queue depth. Start the stack with
`ORCHESTRATOR_MAX_ATTEMPTS=2 ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS=10` (and pass the same to the script) to keep it short.

Market data (plan step s3.1): `docker compose --profile infra --profile market-data up -d --wait --build` adds
`market-data` on 127.0.0.1:5082 (`MARKET_DATA_PORT`), the only service mounting `lean-data` read-write. It serves the
catalog, coverage, history and fetch-task API, runs fetch tasks from `marketdata.fetch`, and publishes LEAN's simulated
`FakeDataQueue` feed for `MARKET_DATA_LIVE_SYMBOLS` on `lean.marketdata.live`. `bash deploy/compose/verify-market-data.sh`
checks all of it, including that synthetic data is refused for real tickers.

Results (plan step s3.2): `docker compose --profile infra --profile results up -d --wait --build` adds `results` on
127.0.0.1:5083 (`RESULTS_PORT`). It consumes `results.packets` into the PostgreSQL `results` database, reads final result
JSON from MinIO `lean-results` and serves the results API (`/internal/v1/results/...`); see
[`Services/QuantConnect.Services.Results/README.md`](../../Services/QuantConnect.Services.Results/README.md).

Report (plan step s3.3): `docker compose --profile infra --profile report up -d --wait --build` adds `report` on
127.0.0.1:5084 (`REPORT_PORT`). It consumes `jobs.report` one render at a time, verifies the backtest (and optional live)
result JSON from `lean-results` against its `ObjectRef`, runs LEAN's `QuantConnect.Report.dll` (HTML, PDF via
`xvfb-run wkhtmltopdf`) with the absolute `data-folder` `/Lean/Data/` (read-only `lean-data`), writes
`lean-reports/<jobId>/<attempt>/report.{html,pdf,log}`, publishes `job.completed` / `job.failed` and serves
`/internal/v1/reports`. `sh deploy/compose/verify-report.sh` renders the stored fixture
`Tests/TestData/test_report_data.json` through the bus (no backtest) and checks a tampered `ObjectRef` fails.

Research and ToolBox (plan step s3.4), both opt-in profiles so the default stack stays small:

- `docker compose --profile infra --profile market-data --profile results --profile research up -d --wait` adds `research`: JupyterLab +
  QuantBook (`Services/Dockerfile` target `research`, from `DockerfileJupyter`) on http://127.0.0.1:8888/lab
  (`RESEARCH_PORT`), `lean-data` mounted read-only, the services' base URLs in `LEAN_MARKET_DATA_URL`,
  `LEAN_RESULTS_URL`, `LEAN_ORCHESTRATOR_URL`, `LEAN_REPORT_URL`. `ServicesQuantBookTemplate.ipynb` compares QuantBook
  history with the market-data history API and loads a finished backtest from the results API. Notebooks saved under
  `work/` persist in the `research-notebooks` volume.
- `docker compose --profile toolbox run --rm toolbox <app> [--option=value ...]` runs one ToolBox converter or generator
  (`rdg`, `cug`, `kdc`, `asfc`; `help` lists them) as a one-shot job on the market-data volume. `Services/toolbox/lean-toolbox.sh`
  runs the app against a staging copy (`.toolbox-staging/`, `TOOLBOX_SEED`) and renames the new/changed files into place,
  so readers never see partial files; generators may only add files (a run that would overwrite data, e.g. `rdg
  --tickers=SPY`, publishes nothing and exits 3) and `TOOLBOX_DRY_RUN=1` only lists them. Converter inputs come from
  `TOOLBOX_INPUT_DIR` (default `deploy/compose/toolbox-input/`), mounted read-only at `/input`. Example:
  `docker compose --profile toolbox run --rm toolbox rdg --tickers=SYNA --start=20240102 --end=20240131 --resolution=Daily --include-coarse=false`.
- `bash deploy/compose/verify-research-toolbox.sh` checks both (it needs the `results` profile and a finished backtest from `smoke-backtest.sh`).

| Service | What | Host port (127.0.0.1 only) | Files |
|---|---|---|---|
| `rabbitmq` | RabbitMQ 3.13 + management UI, v1 topology from `docs/architecture/contracts/topology/` | 5672, UI 15672 | `rabbitmq/entrypoint.sh` adds the compose user to the topology definitions |
| `postgres` | PostgreSQL 17, databases `jobs`, `results`, `gateway`, `marketdata`, each owned by its own login role | 5432 | `postgres/initdb/` (first start only); services run their own migrations |
| `minio` | S3-compatible object storage (community build `pgsty/minio`; upstream images are no longer published) | API 9000, console 9001 | |
| `minio-bootstrap` | one-shot: buckets `lean-algorithms`, `lean-results`, `lean-reports` and the `lean-services` account limited to them | | `minio/minio-bootstrap.sh` |
| `lean-data-seed` | one-shot: copies `Data/` into the `lean-data` volume once (marker `.lean-data-seeded`), owner uid 1654 | | `lean-data/seed-lean-data.sh` |
| `infra-ready` | healthy once all of the above are healthy/completed: the single dependency for later services | | |

Adding a service (later phases): give it a profile, `environment: *lean-service-env` (RabbitMQ URI, S3 settings,
`LEAN_DATA_FOLDER`) plus its own `ConnectionStrings__<Database>`, `depends_on: infra-ready: condition: service_healthy`,
and mount `lean-data:/Lean/Data:ro` (read-write only for market-data and its one-shot `toolbox` jobs). Source mounts and debug ports go in
`docker-compose.override.yml`. Reseed market data with `docker volume rm lean_lean-data`.

## API gateway (plan step s4.5)

`api-gateway` (profile `gateway`) is the only public entry point, on `127.0.0.1:${GATEWAY_PORT:-5080}`. It authenticates
with the API key from `GATEWAY_API_KEY` (`X-Api-Key: <key>` or `Authorization: Bearer <key>`), or with a JWT once
`GATEWAY_JWT_AUTHORITY` / `GATEWAY_JWT_SIGNING_KEY` is set, rate-limits per key (`GATEWAY_RATE_LIMIT` requests per
`GATEWAY_RATE_WINDOW_SECONDS`), echoes and forwards `X-Request-ID`, and proxies `/api/v1/...` to the internal services.
An upstream that is not running answers `503 application/problem+json`. The merged OpenAPI document
is at `/openapi`. See `Services/QuantConnect.Services.Gateway/README.md` for the routes.

```bash
docker compose --profile infra --profile orchestrator --profile results --profile report --profile gateway up -d --build --wait
deploy/compose/verify-gateway.sh   # backtest through the gateway -> orchestrator, result <- results, report <- report
```

## Optimizer (plan step s4.6)

`optimizer` (profile `optimizer`) consumes `jobs.optimization` and runs LEAN's `LeanOptimizer` strategies (grid, Euler) in
`BusLeanOptimizer`: every parameter set becomes a child backtest job (`parentJobId` = the optimization) submitted to
job-orchestrator and run by the `backtest-worker` pool; child `job.completed` events (queue `optimizer.children`) are folded
against the target/constraints, and progress plus the best parameter set are published as `job.progress` / the terminal
event, with `optimization-result.json` in `lean-results`. Its API (`/internal/v1/optimizations/...`) is the gateway's
`optimizer` upstream, on `127.0.0.1:${OPTIMIZER_PORT:-5085}`. In-flight children are capped by the spec's
`maximumConcurrentBacktests` and `OPTIMIZER_MAX_CONCURRENT_BACKTESTS`; past that, the worker pool sets the pace, so scale
`backtest-worker` to speed optimizations up. Run one optimizer replica.

```bash
docker compose --profile infra --profile orchestrator --profile workers --profile optimizer --profile gateway build
deploy/compose/verify-optimizer.sh   # 3x3 grid through the gateway on 3 workers, then on 1 for comparison
```

## Observability (plan step s5.3)

Profile `observability` adds an OpenTelemetry Collector, Prometheus, Grafana, Tempo, Loki and a PostgreSQL exporter, all
configured from `deploy/compose/observability/`. Every service built on `QuantConnect.Services.Defaults` exports traces,
metrics and logs over OTLP to `otel-collector:4317` (`OTEL_EXPORTER_OTLP_ENDPOINT`; set it to empty to turn export off).
The collector forwards traces to Tempo, logs to Loki and metrics to a Prometheus exporter. Prometheus also scrapes the
orchestrator `/metrics` (queue depth, jobs per state), RabbitMQ `/metrics/per-object` and postgres-exporter.

The RabbitMQ envelope carries W3C `traceparent`/`tracestate` headers, so one backtest is a single trace:
gateway request -> orchestrator dispatch -> worker `backtest run` -> engine packets -> results.
`X-Request-ID` on the gateway response is the trace id (paste it into Grafana Explore > Tempo).

| UI | Address |
|---|---|
| Grafana (anonymous viewer, admin `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD`) | `http://127.0.0.1:${GRAFANA_PORT:-3000}` |
| Prometheus | `http://127.0.0.1:${PROMETHEUS_PORT:-9090}` |
| Tempo / Loki APIs | `http://127.0.0.1:${TEMPO_PORT:-3200}` / `http://127.0.0.1:${LOKI_PORT:-3100}` |

The provisioned dashboards (folder LEAN) are Platform overview (jobs per state, queue depth, worker count and utilisation,
job duration p50/p95), Service RED, RabbitMQ health and PostgreSQL health.

```bash
docker compose --profile infra --profile orchestrator --profile market-data --profile results --profile report \
  --profile workers --profile gateway --profile observability up -d --build --wait
deploy/compose/verify-observability.sh   # one backtest; checks the trace, metrics, logs and every dashboard query
```
