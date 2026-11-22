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
and mount `lean-data:/Lean/Data:ro` (read-write only for market-data). Source mounts and debug ports go in
`docker-compose.override.yml`. Reseed market data with `docker volume rm lean_lean-data`.
