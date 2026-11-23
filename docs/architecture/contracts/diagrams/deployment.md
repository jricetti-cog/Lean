# Deployment diagram (as built)

The same images run on Docker Compose (`docker-compose.yml`, `make up`) and on Kubernetes (Helm chart
`deploy/helm/lean-platform`). Images: `lean/<service>:dev` built locally (`Services/Dockerfile`, `docker buildx bake -f
Services/docker-bake.hcl`) or `ghcr.io/jricetti-cog/lean/<service>:<tag>`.

```mermaid
flowchart TB
  subgraph compose[Docker Compose: project lean, network lean, ports on 127.0.0.1]
    direction TB
    subgraph cinfra[Infrastructure]
      crmq[rabbitmq :5672 / :15672<br/>v1 topology loaded at boot]
      cpg[postgres :5432<br/>initdb: roles + databases]
      cminio[minio :9000 / :9001]
      cboot[minio-bootstrap, lean-data-seed<br/>one-shot]
      cready[infra-ready]
    end
    subgraph capps[Platform, default profile]
      cgw[api-gateway :5080]
      ccon[console :5090]
      corch[job-orchestrator :5081]
      cmd[market-data :5082]
      cres[results :5083]
      crep[report :5084]
      copt[optimizer :5085]
      cbw[backtest-worker xN<br/>BACKTEST_WORKER_REPLICAS]
      cproxy[docker-socket-proxy<br/>internal docker-api network]
    end
    clive[lean-live-jobId containers<br/>created at run time]
    cobs[observability profile:<br/>otel-collector, prometheus + alerts,<br/>grafana :3000, tempo, loki, postgres-exporter]
    copt2[research profile: research :8888<br/>toolbox profile: toolbox one-shot]
    cvol[(volumes: rabbitmq-data, postgres-data,<br/>minio-data, lean-data, ...)]
  end
  corch -->|DOCKER_HOST tcp://docker-socket-proxy:2375| cproxy
  cproxy -->|/var/run/docker.sock| clive

  subgraph k8s[Kubernetes: Helm release lean in namespace lean]
    direction TB
    kinfra[infra subchart: RabbitMQ, PostgreSQL, MinIO + init Jobs<br/>or infra.enabled=false + endpoints.*]
    kdep[Deployments + Services: job-orchestrator, market-data,<br/>results, report, optimizer, api-gateway, console<br/>probes /healthz /readyz, init containers wait for infra]
    kbw[backtest-worker Deployment]
    kscale[KEDA ScaledObject or HPA<br/>on jobs.backtest ready + unacked]
    klive[lean-live-jobId Deployments<br/>1 replica, Recreate, label lean.role=live-worker]
    krbac[ServiceAccount + Role<br/>apps/deployments in the namespace]
    kpvc[(lean-data PVC, seeded by a Job<br/>RWO on kind, RWX multi-node)]
  end
  kscale --> kbw
  kdep -->|job-orchestrator, Mode=kubernetes| krbac
  krbac --> klive
  kpvc -.->|rw: market-data; ro: workers, report, live| kdep
```
