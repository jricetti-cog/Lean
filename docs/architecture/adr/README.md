# Architecture decision records

Decisions for the "LEAN monolith to Docker microservices" migration. Each ADR is short, cites the evidence it
rests on (mostly [`../current-state.md`](../current-state.md), the seams map) and is **Accepted** once merged on
`master`. Later tickets follow these ADRs; changing a decision means a new ADR that supersedes the old one,
not an edit of an accepted one. [ADR 0006](0006-as-built.md) records where the implementation diverged from
0001–0005; read it together with them.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-service-decomposition.md) | Service decomposition | Accepted |
| [0002](0002-service-communication.md) | Communication: REST + OpenAPI and RabbitMQ | Accepted |
| [0003](0003-storage.md) | Storage: PostgreSQL, MinIO, shared data volume | Accepted |
| [0004](0004-market-data-access.md) | Market data access: shared read-only volume + market-data service | Accepted |
| [0005](0005-engine-hosting-benchmark.md) | Engine hosting benchmark (evidence for 0004) | Accepted |
| [0006](0006-as-built.md) | As built: amendments to 0001–0005 | Accepted |

Format: Context → Decision → Consequences → Alternatives considered. Section references like "§4.2" point to
`current-state.md`.

Invariant for every ADR: the monolith keeps working. `Launcher/config.json` defaults (handlers, `data-folder`,
`environment`) stay unchanged; services select their handlers through a generated per-job config or `LEAN_*`
environment variables, never by editing the default file.
