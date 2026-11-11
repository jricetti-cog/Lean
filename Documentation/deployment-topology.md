# LEAN deployment topology

LEAN is being split into independently deployable services in phases. The rule that drives every phase:
**the `AlgorithmManager` loop (data feed → algorithm → transaction handler → result handler) stays in one
process.** It shares state and does work on every event, so splitting it across services would add a network
hop to each event. LEAN scales by running more engine containers, one algorithm per container. The loop itself
is never split.

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | Peripheral services: API, Report, ToolBox batch jobs, Research image | this document |
| 2 | Message bus for results, logs and notifications (`IMessagingHandler`, `IResultHandler`, `ILeanManager`) | planned |
| 3 | Remote `IDataProvider` / `IHistoryProvider` data service. Live `IDataFeed` stays in-process | planned |
| 4 | Backtest/live worker image + job orchestrator, one algorithm per container | planned |

## Phase 1 topology

```
                      +--------------------------------------+
  clients / browser   |  lean-api  (Services/Api)  :8080     |
  ------------------> |  /v1/cloud/*  -> QuantConnect.Api    | ---> api.quantconnect.com
                      |  /v1/reports* -> report service      |
                      |  /  dashboard, /health               |
                      +------------------+-------------------+
                                         |
                      +------------------v-------------------+
                      |  lean-report (Services/Report) :8080 |   volume: reports
                      |  POST result json -> HTML report     |
                      +--------------------------------------+

  lean-toolbox  (batch job, run to completion)  --->  volume: lean-data  <---  lean-research (JupyterLab :8888)
  lean-engine   (Launcher + Engine, unchanged in-process loop)  --->  volume: lean-data
```

All images come from one multi-stage [`Services/Dockerfile`](../Services/Dockerfile), built on
`quantconnect/lean:foundation`:

| Target | Image | Process | Notes |
|--------|-------|---------|-------|
| `api` | `lean-services/api` | `QuantConnect.Services.Api` | Stateless, scale horizontally |
| `report` | `lean-services/report` | `QuantConnect.Services.Report` | Python.NET for charts; reports stored in `/Lean/Reports` |
| `toolbox` | `lean-services/toolbox` | `lean-job toolbox\|downloader ...` | Exits when the job finishes; Job/CronJob friendly |
| `engine` | `lean-services/engine` | `QuantConnect.Lean.Launcher` | Same layout as the root `Dockerfile` (`/Lean/Launcher/bin/Debug`); base of Research and, in phase 4, the worker |
| (research) | `lean-services/research` | JupyterLab + QuantBook | Built from `DockerfileJupyter` with `LEAN_BASE=lean-engine` |

### Configuration

Every service reads LEAN configuration from `LEAN_*` environment variables
([`LeanServiceConfiguration`](../Services/Shared/LeanServiceConfiguration.cs)). The variable name with the
prefix removed, lower-cased and with `_` replaced by `-`, is the `Config` key, so `LEAN_DATA_FOLDER` sets
`data-folder`. `LEAN_CONFIG_FILE` picks a base `config.json`.

| Variable | Service | Meaning |
|----------|---------|---------|
| `LEAN_REPORT_SERVICE_URL` | api | Upstream for `/v1/reports` |
| `LEAN_JOB_USER_ID`, `LEAN_API_ACCESS_TOKEN`, `LEAN_JOB_ORGANIZATION_ID` | api | QuantConnect credentials for `/v1/cloud` |
| `LEAN_SERVICE_TOKEN` | all | When set, every `/v1` route needs `Authorization: Bearer <token>`. `/health` stays open |
| `LEAN_REPORT_STORAGE_FOLDER` | report | Where generated reports are kept (default `/Lean/Reports/` in the image) |
| `LEAN_DATA_FOLDER` | all | LEAN data folder (default `/Lean/Data/`) |

### API service (`Services/Api`)

`QuantConnect.Api.Api` is a client SDK. The service puts versioned HTTP routes in front of it and in front of
the internal services, so clients only need to reach one origin:

| Route | Backed by |
|-------|-----------|
| `GET /health` | service status |
| `GET /v1/services` | configured upstreams |
| `GET /v1/cloud/authenticated`, `/account`, `/organization`, `/lean-versions` | `Api.Connected`, `ReadAccount`, ... |
| `GET/POST /v1/cloud/projects`, `GET/DELETE /v1/cloud/projects/{id}`, `GET/POST .../files` | project endpoints |
| `POST .../compiles`, `GET .../compiles/{compileId}` | compile endpoints |
| `GET/POST .../backtests`, `GET/DELETE .../backtests/{id}`, `.../orders`, `.../report` | backtest endpoints |
| `GET /v1/cloud/live`, `GET .../live`, `.../live/portfolio`, `POST .../live/stop`, `.../live/liquidate`, `.../live/commands` | live endpoints |
| `GET /v1/cloud/data/prices`, `GET /v1/cloud/object-store?path=` | data and object store |
| `/v1/reports/**` | proxied to the report service |
| `/` | small dashboard: service status, upload a result json, list reports |

When the QuantConnect API fails, the service answers `502` and passes the upstream `RestResponse` through
(`success`, `errors`).

### Report service (`Services/Report`)

Uses the same `Report.Report` / `Compile` code as the `Report` console app.

```bash
# body: the <algorithm>.json backtest result that LEAN writes, a BacktestResultPacket, or {"backtest": ..., "live": ...}
curl -X POST "http://localhost:8080/v1/reports?name=MyStrategy" --data-binary @BasicTemplateAlgorithm.json
# -> 201 {"id": "...", "url": "/v1/reports/<id>", "statistics": {...}}
curl http://localhost:8080/v1/reports/<id>            # HTML report
curl http://localhost:8080/v1/reports/<id>/statistics # report statistics json
curl http://localhost:8080/v1/reports                 # list
```

Add `format=html` to the POST to get the HTML back directly. A body that is not a LEAN result returns `400`.
Compiles run one at a time because the report charts run under the Python GIL. To get more throughput, run more
replicas, each with its own storage.

### ToolBox batch jobs (`Services/ToolBox`)

The `toolbox` image runs one command to completion and exits with that command's status code:

```bash
docker compose run --rm toolbox toolbox --app=RandomDataGenerator --start=20200101 --end=20200201 \
    --security-type=Equity --resolution=Daily --symbol-count=1 --tickers=RNDA
docker compose run --rm toolbox downloader --data-type=Trade --resolution=Daily --security-type=Equity \
    --tickers=SPY --start-date=20240101 --end-date=20240201
```

RandomDataGenerator overwrites any existing data for the tickers it is given, so use synthetic tickers.
Output goes to `/Lean/Data`, which is the shared `lean-data` volume in compose and the `lean-data` PVC in
Kubernetes. The Research image and, later, the workers read from the same volume.

### Research (`DockerfileJupyter`)

`DockerfileJupyter` now takes a `LEAN_BASE` build argument (default `quantconnect/lean:$LEAN_TAG`, so existing
builds don't change). Compose builds it on top of the locally built `engine` image, so notebooks run the same
LEAN build as the services.

## Running it

### docker compose

```bash
docker compose up -d --build                    # api + report, dashboard at http://localhost:8080
docker compose --profile research up -d --build # + JupyterLab at http://localhost:8888
docker compose --profile jobs build toolbox     # batch image, then "docker compose run --rm toolbox ..."
```

QuantConnect credentials are optional: `QC_USER_ID`, `QC_API_TOKEN`, `QC_ORGANIZATION_ID` and
`LEAN_SERVICE_TOKEN` are read from the shell or from a `.env` file. Without credentials, `/v1/cloud/authenticated`
returns `false`.

### Kubernetes

```bash
docker build -f Services/Dockerfile --target api     -t lean-services/api .
docker build -f Services/Dockerfile --target report  -t lean-services/report .
docker build -f Services/Dockerfile --target toolbox -t lean-services/toolbox .
docker build -f Services/Dockerfile --target engine  -t lean-services/engine .
docker build -f DockerfileJupyter --build-arg LEAN_BASE=lean-services/engine -t lean-services/research .

kubectl -n lean create secret generic lean-api --from-literal=user-id=... --from-literal=api-token=... # optional
kubectl apply -k Services/k8s
kubectl create -f Services/k8s/toolbox-jobs.yaml   # data PVC, one-off Job, suspended CronJob template
```

`lean-api` is the only Service exposed outside the cluster (`LoadBalancer`). `lean-report` and `lean-research`
are `ClusterIP`. JupyterLab has no token, so reach it with `kubectl port-forward`.
