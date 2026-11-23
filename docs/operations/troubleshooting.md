# Troubleshooting

Start with `make ps` (`docker compose ps -a`): a service that is `unhealthy` or `exited` names the failure, and
`docker compose logs --tail 200 <service>` shows its JSON logs. Optional profiles need the same flag for `logs`
(`docker compose --profile observability logs prometheus`). With the observability profile, the `X-Request-ID` of a
gateway response is the trace id in Grafana Explore > Tempo, and Loki has every service's logs (`{service_name="results"}`).

## Broker (RabbitMQ)

| Symptom | Cause / check | Fix |
|---|---|---|
| `rabbitmq` exits at boot with `.erlang.cookie: eacces` | Some Docker setups cannot write the cookie file | Compose passes the cookie on the command line (`RABBITMQ_ERLANG_COOKIE`); keep that, and use `rabbitmqctl --erlang-cookie <cookie>` inside the container |
| `ACCESS_REFUSED` for `guest` or another user | The v1 topology (`definitions.json`) defines no users; `deploy/compose/rabbitmq/entrypoint.sh` adds only `RABBITMQ_USER` on vhost `RABBITMQ_VHOST` | Use `lean` / `lean-dev-rabbitmq` on vhost `lean`, or `rabbitmqctl add_user` + `set_permissions -p lean` |
| Jobs stay `queued` | `rabbitmqctl -p lean list_queues name messages_ready messages_unacknowledged consumers`: `jobs.backtest` with 0 consumers → no worker; growing ready → workers busy | Start or scale `backtest-worker`; [LeanJobsWaitingNoWorker](runbooks.md#leanjobswaitingnoworker) |
| Messages in `jobs.<kind>.dead` | Delivered 3 times without an ack (worker crashes, poison message) | [LeanDeadLetters](runbooks.md#leandeadletters) |
| Publishers hang, `blocked` connections in the UI | Memory or disk alarm | [RabbitMQMemoryHigh](runbooks.md#rabbitmqmemoryhigh), [RabbitMQDiskLow](runbooks.md#rabbitmqdisklow) |
| Queue counts in the management API lag | The stats interval is ~5 s | Poll instead of reading once after a publish |
| A test steals or loses messages | `Tests/Services/Messaging` RabbitMQ tests use the real `jobs.backtest` / results queues | Stop compose workers/orchestrator/results before running them |

Services reconnect with backoff after a broker restart; consumers re-declare nothing (the topology is loaded by the
broker from `docs/architecture/contracts/topology/`).

## Database (PostgreSQL)

| Symptom | Cause / check | Fix |
|---|---|---|
| job-orchestrator, results or market-data unhealthy at start, `password authentication failed` | The roles are created by `deploy/compose/postgres/initdb/` **only on the first start** of the `postgres-data` volume; changing `POSTGRES_*_PASSWORD` later does not update them | `ALTER ROLE <svc> PASSWORD '...'` as `postgres`, or `make clean` to recreate (deletes all jobs/results) |
| `database "<x>" does not exist` | Same first-start rule, e.g. a volume from an older checkout | Create it as `postgres` or recreate the volume |
| Services restart in a loop while postgres starts | Normal: each service retries its migrations until postgres is ready | Wait for `infra-ready`; check `docker compose logs postgres` if it does not get healthy |
| Tests against the compose database clobber data | Opt-in tests (`LEAN_TEST_POSTGRES`) create tables | Point them at a separate database, e.g. `jobs_test` |
| Too many connections | [PostgresConnectionsHigh](runbooks.md#postgresconnectionshigh) | |

Connect with `docker compose exec postgres psql -U postgres -d jobs` (`\dt`, `select status, count(*) from jobs group
by 1;`). The `gateway` database is created but unused (the gateway keeps API keys in configuration, ADR 0006).

## Object storage (MinIO)

| Symptom | Cause / check | Fix |
|---|---|---|
| Backtest completes in the logs but the job fails or has no result; worker logs `AccessDenied` / `InvalidAccessKeyId` | Wrong `S3__*` settings; services must use the `lean-services` account (`MINIO_SERVICE_*`), not the root account | Fix `.env`, `docker compose up -d` to recreate the services |
| Presigned download URL fails from the browser | `Gateway__PublicS3Url` (`http://127.0.0.1:${MINIO_API_PORT}`) must be reachable from the browser | Set it to the externally visible MinIO URL |
| `minio` image does not pull | Upstream MinIO images are no longer published; compose pins the community build `pgsty/minio` | Keep `MINIO_IMAGE`, or point S3 settings at another S3-compatible store |
| Buckets missing | `minio-bootstrap` exited non-zero | `docker compose logs minio-bootstrap`, then `docker compose up -d minio-bootstrap` |

## Data volume (`lean-data`)

| Symptom | Cause / check | Fix |
|---|---|---|
| Backtest trades nothing, or `market-hours-database.json` / map files not found | The engine needs an absolute `data-folder`: workers set it to `/Lean/Data/` (`LEAN_DATA_FOLDER`); a process started elsewhere with a relative path reads the wrong folder | Use an absolute `data-folder`; check `docker compose exec backtest-worker ls /Lean/Data/market-hours` |
| New files in `Data/` not visible | `lean-data-seed` copies `Data/` **once** (marker `.lean-data-seeded`) | `docker compose down && docker volume rm lean_lean-data && make up` reseeds |
| `Read-only file system` from a job | `lean-data` is mounted read-only everywhere except market-data and toolbox, on purpose (ADR 0004) | Write data through market-data (`POST /api/v1/data/fetches`) or a toolbox job |
| Real symbol data (e.g. SPY) overwritten by synthetic data | `RandomDataGenerator --tickers=SPY` on the real folder | The platform refuses it (market-data and `lean-toolbox` exit 3); restore with a reseed. Use synthetic tickers |
| Partial or corrupt zips | Something wrote in place on the shared volume | Only market-data and `lean-toolbox` write, via staging + rename; market-data deletes `/Lean/Data/.staging/` on start, so other writers must stage elsewhere (`.toolbox-staging/`) |
| Helm: pods `Pending` on the `lean-data` PVC | Multi-node clusters need a `ReadWriteMany` storage class | Set `data.storageClass` and `data.accessModes` |

## pythonnet

| Symptom | Cause / check | Fix |
|---|---|---|
| `dotnet test` or the Report service crashes on the host with `GIL must always be released` | pythonnet needs the Python stack of `quantconnect/lean:foundation` | Run tests and engine-hosting code inside the foundation image (`docker run --rm -v $PWD:$PWD -w $PWD/Tests/bin/Release quantconnect/lean:foundation dotnet test ...`) |
| Python backtest exits with code −6 (`GILState.Finalize`) after printing statistics | Pre-existing LEAN shutdown crash, also in the monolith (ADR 0005 finding 5) | Not an error: workers treat "final result published + acked" as success, not the exit code |
| `No module named AlgorithmImports` / a framework model module | `PYTHONPATH` must include `/Lean/Launcher/bin/Release/` (and the framework model folders for models importing siblings) | The worker images set it; set it yourself when running the Launcher by hand |
| Python job slower to start than C# | pythonnet + Python runtime start in every engine process (~2 s more than C#) | Expected, see [scaling-workers.md](scaling-workers.md) |
| One report render at a time | One Python runtime per process (GIL) | Scale `report` replicas |

## Workers and jobs

| Symptom | Cause / check | Fix |
|---|---|---|
| Job `failed` with `compile-error` | Uploaded C# did not compile (Roslyn in the worker) | The diagnostics are in the job logs (`GET /api/v1/jobs/{id}/logs`) |
| Job `failed` with `runtime-error` "exceeded its N MB RAM limit", or `timeout` | Engine RSS above `maxRamMb` + `Worker__MemoryHeadroomMb`, or runtime above `maxRuntimeSeconds` | Raise the job's limits or `Worker__DefaultRamMb` / `BACKTEST_WORKER_MEMORY` |
| Job retried and runs again | Heartbeats stopped for `ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS` (worker crash, host pause) | Expected up to `ORCHESTRATOR_MAX_ATTEMPTS`; each attempt writes `lean-results/<job>/<attempt>/` |
| Job `completed` by `smoke-backtest.sh` stays `running` in results | That script drives the worker directly and never publishes `job.completed` through the orchestrator | Use the gateway (`deploy/smoke/smoke.sh`) for a full lifecycle |
| Live deployment not started (Compose) | job-orchestrator creates `lean-live-<jobId>` through `docker-socket-proxy` (`DOCKER_HOST=tcp://docker-socket-proxy:2375`) | `docker compose logs job-orchestrator docker-socket-proxy`; `docker ps -a --filter name=lean-live-` |
| Live container not restarted after `docker kill` | Docker counts `kill` as a manual stop for `on-failure` policies | Expected; a real crash restarts it (up to 5 times) and the portfolio is restored from results |
| Engine log of a job | Kept in the job's result folder | `lean-results/<job>/<attempt>/` in MinIO, or `GET /api/v1/jobs/{id}/logs` |

## Gateway and console

| Symptom | Cause / check | Fix |
|---|---|---|
| `401` | Missing or wrong `X-Api-Key` / `Authorization: Bearer` | Default key `lean-dev-api-key` (`GATEWAY_API_KEY`) |
| `429` with `Retry-After` | Per-key rate limit (`GATEWAY_RATE_LIMIT` per `GATEWAY_RATE_WINDOW_SECONDS`) | Back off, or raise the limit for scripted runs |
| `503 application/problem+json` naming an upstream | That service is down or not ready | `make ps`; start it |
| Console shows an old UI after a rebuild | The browser keeps the previous SPA bundle | Hard reload |
| SSE stream (`/api/v1/jobs/{id}/events`) ends right away | The job is already terminal; the gateway sends the snapshot and closes | Expected |
