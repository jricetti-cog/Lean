# Operations

How to run, configure, scale, extend and troubleshoot the LEAN platform (the Docker services under `Services/`,
deployed with `docker-compose.yml` or the Helm chart `deploy/helm/lean-platform`). The architecture behind it is in
[`../architecture/`](../architecture/README.md). The monolith (`Launcher/config.json`, `dotnet
QuantConnect.Lean.Launcher.dll`) is unchanged and documented in the root [`readme.md`](../../readme.md).

| Page | What |
|---|---|
| [running-locally.md](running-locally.md) | Start the platform with Docker Compose, URLs, smoke test, profiles, rebuild one service, Kubernetes on kind |
| [configuration.md](configuration.md) | Every setting, generated from `.env.example` and `docker-compose.yml` (`gen_config_reference.py`) |
| [scaling-workers.md](scaling-workers.md) | Per-job overhead measured by the parity run, when the platform pays off, worker count and the other knobs |
| [adding-a-brokerage-or-data-source.md](adding-a-brokerage-or-data-source.md) | Plug in a LEAN brokerage for live deployments, a data downloader or upstream folder for market-data, a live feed |
| [adding-a-service.md](adding-a-service.md) | A new service on the shared defaults: project, image target, compose, Helm, telemetry, tests |
| [troubleshooting.md](troubleshooting.md) | Broker, database, object storage, data volume, pythonnet, workers, gateway |
| [demo-walkthrough.md](demo-walkthrough.md) | End-to-end demo script (backtests, report, optimization, live, observability) and its evidence |
| [runbooks.md](runbooks.md) | One runbook per alert in `deploy/compose/observability/alerts.yml` |

Day-to-day commands (all are plain wrappers, see the `Makefile`):

```sh
make up        # docker compose up -d --wait   (builds missing images)
make smoke     # engine backtest over the bus, then the public API end to end
make ps        # docker compose ps -a
make logs      # docker compose logs -f --tail 100
make down      # stop, keep volumes
make clean     # stop and delete every volume (database, object storage, lean-data copy)
```
