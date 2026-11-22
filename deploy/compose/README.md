# Compose infrastructure (plan step s2.3)

The root [`docker-compose.yml`](../../docker-compose.yml) runs the infrastructure every LEAN service needs
(ADR 0002–0004). From the repository root:

```bash
cp .env.example .env                              # optional: every setting has a local-development default
docker compose --profile infra up -d --wait       # blocks until every container is healthy (or completed)
sh deploy/compose/verify-infra.sh                 # smoke test: health, ports, credentials, isolation
docker compose --profile infra down               # keep data; add -v to delete the named volumes
```

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
