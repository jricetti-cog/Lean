# Running the platform locally

## Prerequisites

Docker Engine with Compose v2 (BuildKit), `make`, `curl` and `jq`. The engine-hosting images (backtest-worker,
live-worker, market-data, report, research, toolbox) derive from `quantconnect/lean:foundation` (about 41 GB), so plan
for 60 GB of free disk on the first build. Nothing else is needed on the host: the .NET build runs inside the image
build, and pythonnet only works inside the foundation image anyway (see [troubleshooting](troubleshooting.md#pythonnet)).

## Start, check, stop

```sh
cp .env.example .env     # optional: every setting has a local-development default (configuration.md)
make up                  # = docker compose up -d --wait: builds missing images, returns once every service is healthy
make smoke               # deploy/compose/smoke-backtest.sh + deploy/smoke/smoke.sh
make down                # stop (volumes kept); `make clean` also deletes them
```

`docker compose up` (foreground) works too. With no profile Compose starts the infrastructure (RabbitMQ, PostgreSQL,
MinIO, the one-shot `minio-bootstrap` and `lean-data-seed`, `infra-ready`), `docker-socket-proxy`, job-orchestrator,
one backtest-worker, market-data, results, report, optimizer, api-gateway and console. The first `make up` builds every
image with one shared build stage (tens of minutes); later runs reuse them.

| URL (127.0.0.1 only) | What | Credentials (defaults) |
|---|---|---|
| http://127.0.0.1:5090 | console (React) | none: nginx injects the console API key |
| http://127.0.0.1:5080 | api-gateway, merged OpenAPI at `/openapi` | `X-Api-Key: lean-dev-api-key` |
| http://127.0.0.1:15672 | RabbitMQ management | `lean` / `lean-dev-rabbitmq`, vhost `lean` |
| http://127.0.0.1:9001 | MinIO console | `lean-admin` / `lean-dev-minio-admin` |
| 127.0.0.1:5432 | PostgreSQL (`jobs`, `results`, `marketdata`, `gateway`) | `postgres` / `lean-dev-postgres` |
| http://127.0.0.1:5081-5085 | job-orchestrator, market-data, results, report, optimizer internal APIs | none (private in Kubernetes) |

Submit a backtest through the public API:

```sh
curl -s -H 'X-Api-Key: lean-dev-api-key' -H 'Content-Type: application/json' \
  -d '{"algorithm":{"source":"builtin","typeName":"BasicTemplateAlgorithm"}}' http://127.0.0.1:5080/api/v1/backtests
curl -s -H 'X-Api-Key: lean-dev-api-key' http://127.0.0.1:5080/api/v1/jobs/<id>/result | jq .statistics
```

`make smoke` runs `deploy/compose/smoke-backtest.sh` (BasicTemplateAlgorithm on the backtest-worker image over the
bus) and `deploy/smoke/smoke.sh` (gateway health/auth/OpenAPI, console and its API proxy, a Python backtest on a real
worker, the result download through a presigned MinIO URL, a report on it).

## Optional profiles

| Profile | Adds | Start |
|---|---|---|
| `observability` | OpenTelemetry Collector, Prometheus (with [alerts](runbooks.md)), Grafana, Tempo, Loki, postgres-exporter | `docker compose --profile observability up -d --wait` |
| `research` | JupyterLab + QuantBook on http://127.0.0.1:8888/lab, `lean-data` read-only | `docker compose --profile research up -d --wait` |
| `toolbox` | one-shot ToolBox jobs on the data volume | `docker compose --profile toolbox run --rm toolbox help` |

Grafana is on http://127.0.0.1:3000 (anonymous viewer; admin `admin` / `lean-dev-grafana`), Prometheus on :9090
(alerts at `/alerts`). The `X-Request-ID` returned by the gateway is the trace id to paste into Grafana Explore >
Tempo. Older `--profile infra|orchestrator|workers|...` commands in `deploy/compose/README.md` and the `verify-*.sh`
headers still work; with no profile every platform service already starts.

## Working on one service

```sh
docker compose up -d --build --wait results          # rebuild and restart one service (and its dependencies)
docker compose logs -f results                        # JSON console logs (UTC)
docker compose up -d --scale backtest-worker=4        # more workers (see scaling-workers.md)
docker compose --profile '*' down                     # stop everything, including optional profiles
```

Source mounts and debug ports go in a `docker-compose.override.yml` (Compose merges it automatically). Each service
directory has its own `verify-*.sh` end-to-end check under `deploy/compose/`; their header comments say which
services to start first. Service unit tests live under `Tests/Services/` and run with the rest of the suite inside the
foundation image:

```sh
dotnet build QuantConnect.Lean.sln -c Release
docker run --rm -v $PWD:$PWD -w $PWD/Tests/bin/Release quantconnect/lean:foundation \
  dotnet test QuantConnect.Tests.dll --filter "FullyQualifiedName~QuantConnect.Tests.Services"
```

The platform parity harness (all regression algorithms through the gateway versus their `ExpectedStatistics`) is
`Tests/Services.Parity/run-parity.sh`, see its [README](../../Tests/Services.Parity/README.md).

## Published images

`.github/workflows/services.yml` pushes every image to `ghcr.io/jricetti-cog/lean/<service>` on each `master` build
(`<git sha>`, `master`) and on `v<version>` tags. Run them instead of building:

```sh
make pull up IMAGE_REGISTRY=ghcr.io/jricetti-cog/lean IMAGE_TAG=0.1.0
```

## Kubernetes on a local kind cluster

```sh
make build          # lean/<service>:dev images (or reuse the ones Compose built)
make kind-up        # kind cluster + local registry + KEDA, push the images, helm install
make kind-smoke     # port-forwards gateway (5080), console (5090), MinIO (9000) and runs deploy/smoke/smoke.sh
make kind-down
```

`make kind-smoke` leaves its `kubectl port-forward` processes running; kill them afterwards. Chart details (KEDA
autoscaling, live Deployments, `lean-data` PVC, bundled or external infrastructure) are in
[`deploy/README.md`](../../deploy/README.md).
