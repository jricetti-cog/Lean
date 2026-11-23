# Deploying the LEAN platform (plan step s6.3)

Two targets run the same images: **Docker Compose** for local and demo use, and the **Helm chart**
`deploy/helm/lean-platform` for Kubernetes (verified on a local `kind` cluster).

## Clean machine: Docker Compose

Needs Docker Engine with Compose v2, `make`, `curl` and `jq`.

```sh
git clone https://github.com/jricetti-cog/Lean.git && cd Lean
docker compose up            # builds the images on first run, starts the whole platform (foreground)
make smoke                   # in another shell, once everything is healthy
```

`make up` is `docker compose up -d --wait` (detached, returns when every service is healthy); `make down` stops it,
`make clean` also deletes the volumes. With no profile Compose starts infrastructure (RabbitMQ, PostgreSQL, MinIO,
lean-data seed), job-orchestrator, backtest-worker, market-data, results, report, optimizer, api-gateway and console.
Optional profiles: `observability`, `research`, `toolbox` (`docker compose --profile observability up -d`).

| URL | Service |
| --- | --- |
| http://127.0.0.1:5090 | console |
| http://127.0.0.1:5080 (`/openapi`) | api-gateway, API key `lean-dev-api-key` |
| http://127.0.0.1:15672 | RabbitMQ management |
| http://127.0.0.1:9001 | MinIO console |

`make smoke` runs `deploy/compose/smoke-backtest.sh` (BasicTemplateAlgorithm over the bus on the backtest-worker
image) and then `deploy/smoke/smoke.sh`, which uses only the public endpoints: gateway health, auth and OpenAPI, the
console and its API proxy, a Python backtest run by a real worker, the result download (presigned MinIO URL), and a
report on it. `verify-events.sh` (timing-sensitive) is not part of the smoke gate.

Live deployments: job-orchestrator starts one `lean-live-<jobId>` container per deployment through the Docker socket.
`make up` passes the socket's group (`DOCKER_SOCKET_GID`); with plain `docker compose up` set it in `.env` when it is
not 999 (`stat -c %g /var/run/docker.sock`).

### Versioned images (GHCR)

`.github/workflows/services.yml` pushes every image to `ghcr.io/jricetti-cog/lean/<service>` on each `master` build
(`<git sha>` and `master`) and on a `v<version>` tag (`<version>` and `latest`), plus the chart as
`oci://ghcr.io/jricetti-cog/lean/charts/lean-platform:<version>`. To run published images instead of building:

```sh
make pull up IMAGE_REGISTRY=ghcr.io/jricetti-cog/lean IMAGE_TAG=0.1.0
```

`make build` / `make push` wrap `docker buildx bake -f Services/docker-bake.hcl` with the same variables.

## Kubernetes: Helm chart

```sh
make build                   # lean/<service>:dev images (or reuse the ones Compose built)
make kind-up                 # kind cluster + local registry + KEDA, push the images, helm install
make kind-smoke              # port-forwards gateway/console/MinIO, runs deploy/smoke/smoke.sh (+ a live deployment)
make kind-down
```

Against any cluster with published images:

```sh
helm install lean deploy/helm/lean-platform -n lean --create-namespace --wait --wait-for-jobs --timeout 30m \
  --set image.tag=0.1.0          # image.registry defaults to ghcr.io/jricetti-cog/lean
```

What the chart deploys (one release per namespace; Services keep the Compose names, so service-to-service URLs match):

- **Platform services**: a Deployment + Service each for job-orchestrator, market-data, results, report, optimizer,
  api-gateway and console, with `/healthz`/`/readyz` probes and init containers that wait for the infrastructure and
  the lean-data seed.
- **Backtest workers**: a Deployment consuming `jobs.backtest` (prefetch 1). The HPA scales it on queue depth:
  `backtestWorker.autoscaling.provider=keda` (default) renders a KEDA `ScaledObject` (RabbitMQ scaler, ready +
  unacked messages); `provider=external` renders a plain `autoscaling/v2` HPA on an External metric from your metrics
  adapter. Defaults: 1–8 replicas, 2 messages per worker (one running, one waiting), scale up by up to 4 pods per 30 s,
  scale down one pod per minute after 5 min: each job pays ~4–5 s of engine child-process start-up, so warm workers
  are kept through bursts (e.g. an optimization's backtests).
- **Live workers**: job-orchestrator runs with `Orchestrator__LiveDeployment__Mode=kubernetes` and creates one
  single-replica `Recreate` Deployment per live algorithm (`lean-live-<jobId>`, label `lean.role=live-worker`) through
  the Kubernetes API, using a ServiceAccount bound to a namespace-scoped Role (`apps/deployments`:
  get/list/watch/create/patch/delete). The Deployment restarts a crashed worker; the orchestrator deletes it once the
  job is terminal (stopped, failed). A Deployment rather than a Job because a live algorithm is long-running and must
  come back after any exit until it is stopped. Live pods get no API token.
- **Market data**: the `lean-data` PVC, filled once by a Job from the `lean-data` image (the repository's `Data/`),
  written by market-data and mounted read-only everywhere else. kind/single node: `ReadWriteOnce`; multi-node clusters
  need a `ReadWriteMany` class (`data.storageClass`, `data.accessModes`).
- **Infrastructure**: the bundled `infra` subchart (RabbitMQ with the v1 topology contract, PostgreSQL with the
  per-service databases, MinIO with the buckets and service user — the same scripts as Compose). For managed services
  set `infra.enabled=false` and `endpoints.*` (RabbitMQ, PostgreSQL, S3, OTLP) plus `global.credentials` or
  `existingSecret`.

The default credentials are the local-development ones from `docker-compose.yml`; override them outside a local cluster.
