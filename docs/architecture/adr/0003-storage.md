# ADR 0003: Storage — PostgreSQL, MinIO, shared data volume

* Status: Accepted; as-built differences in [ADR 0006](0006-as-built.md)
* Plan step: `s1.2`

## Context

Today everything is files on one machine: the algorithm file (`JobQueue` reads `algorithm-location` with
`File.ReadAllBytes`, §2.1), result JSON and logs written by the result handler into
`Globals.ResultsDestinationFolder` (§4.4), reports written next to them by `Report`, the object store under
`object-store-root` (`LocalObjectStore`, §4.1), and market data under `Globals.DataFolder` (§4.2). After the
split, workers, results, report and the gateway run in different containers and replicas, so per-container
disks cannot hold anything that outlives a job.

Data has three shapes:

* **Small, relational, transactional** — jobs, attempts, leases, optimization → child backtest links, users and
  API keys, result summaries (statistics, runtime statistics, orders, order events).
* **Large immutable blobs** — algorithm packages, full result JSON (with charts), logs, reports.
* **Market data** — tens of thousands of zip files read by path by the engine (ADR 0004).

## Decision

### PostgreSQL — metadata, one database per owner

One PostgreSQL server in Compose (one Helm release), with **one database and one credential per owning
service**; no service connects to another's database.

| Database | Owner | Holds |
|---|---|---|
| `jobs` | job-orchestrator | `jobs` (id, kind, status, attempt, lease owner/epoch/expiry, last heartbeat, parent id for optimizations, parameters `jsonb`, algorithm ref, timestamps, error), append-only `job_events`, `outbox` |
| `results` | results | per job/attempt: `statistics`/`runtime_statistics`/`algorithm_configuration` (`jsonb`), `orders`, `order_events`, capped recent `logs`, chart index (names, last sample), artifact keys |
| `gateway` | api-gateway | users, hashed API keys |
| `marketdata` | market-data | download/fetch tasks and the data catalog (what exists on the volume, coverage per symbol/resolution) |

Each service owns and applies its own schema migrations at startup, accessed with Npgsql. Anything other
services need is exposed through the owner's REST API or events (ADR 0002).

### MinIO — S3-compatible object storage for artifacts

Accessed through the AWS S3 SDK with path-style addressing, so any S3-compatible store can replace MinIO in a
cluster.

| Bucket | Writer | Readers | Keys |
|---|---|---|---|
| `lean-algorithms` | api-gateway (on submit) | workers, research | `sha256/<hash>/<file>` (content addressed, immutable) |
| `lean-results` | backtest/live worker | results, report, gateway (presigned download) | `<jobId>/<attempt>/…` = the job's whole `results-destination-folder` (result JSON, `-log.txt`, order events, …) plus the job's object-store folder |
| `lean-reports` | report | gateway (presigned download) | `<jobId>/<attempt>/report.html`, `report.pdf` |

* Final artifacts are **the files the engine already writes**, uploaded unchanged by the worker after the child
  process exits (ADR 0001). The report service downloads them and runs `Report` on them exactly as today.
* Charts, which dominate result size, are served from the result JSON in `lean-results`; PostgreSQL keeps only
  the index and the latest streamed sample for the live view.
* `IObjectStore` stays `LocalObjectStore` with a per-job `object-store-root`; the folder is uploaded with the
  results. Persisting a project's object store across jobs is a later ADR.
* Built-in C# algorithms run from `QuantConnect.Algorithm.CSharp.dll` inside the worker image (selected by
  `algorithm-type-name`); user uploads are Python files or prebuilt dlls stored in `lean-algorithms`.

### Shared volume — market data only

The `lean-data` volume holds market data and nothing else; it is written only by market-data and mounted
read-only everywhere else (ADR 0004). Results and reports never go on a shared volume (§5.2 "Avoid").

## Consequences

* Workers, results and report are stateless and scale by replicas; losing a container loses nothing committed.
* Per-attempt prefixes make retries idempotent and keep failed attempts inspectable.
* Two stateful dependencies (PostgreSQL, MinIO) plus the volume must be backed up; Compose uses named volumes,
  Helm uses PVCs or external managed services.
* Result queries that need charts read from object storage, which is slower than a table but avoids storing
  millions of chart points relationally.

## Alternatives considered

* **Everything in PostgreSQL (`jsonb`/`bytea` for results and reports).** Rejected: result JSON with charts and
  logs is large and write-once; it bloats the database and its backups.
* **Shared results volume.** Rejected: needs RWX storage, couples every reader to the writer's file layout, and
  reports would be per replica (§5.2).
* **MongoDB for result documents.** Rejected: adds a third store; PostgreSQL `jsonb` covers the queryable part
  and object storage covers the bulk.
* **One shared database for all services.** Rejected: hidden coupling through tables; separate databases on
  one server cost nothing in the demo and keep ownership enforceable.
