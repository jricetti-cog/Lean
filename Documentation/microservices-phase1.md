# LEAN microservices: Phase 1 service boundaries and contracts

Phase 1 moves the edges of LEAN into separate deployables. The backtest loop
(data → algorithm → orders → portfolio update) stays in a single engine process.

```
            REST / WebSocket
 client  ───────────────────►  api-gateway  ──┐
                                              │  jobs.v1.*
                                              ▼
                       ┌──────────── broker (NetMQ XSUB/XPUB) ────────────┐
                       ▲                                                   ▲
       marketdata.v1.* │                                                   │ jobs.v1.* / marketdata.v1.*
                       │                                                   │
                 market-data  (owns Data/, downloads, live streams)   backtest-worker
                                                                       └─ one LEAN process per job
                                                                          (full event loop in-process)
```

## Deployables

| Service | Project | Owns | Talks over |
|---|---|---|---|
| Message broker | `Services/MessageBroker` | NetMQ forwarder (`tcp://*:5560` publishers → `tcp://*:5561` subscribers) | — |
| Market Data Service | `Services/MarketData` | `Data/` storage, data download (ToolBox / `DownloaderDataProvider` pipeline), live data queue handlers and stream fan-out | `marketdata.v1.*` |
| API Gateway | `Services/ApiGateway` | External REST + WebSocket surface, LEAN API authentication, job queue, browser dashboard | HTTP, `jobs.v1.*`, `marketdata.v1.*` |
| Backtest worker | `Services/BacktestWorker` | Leases jobs and runs each in its own `QuantConnect.Lean.Launcher` process; research jobs run through `jupyter nbconvert` | `jobs.v1.*`, `marketdata.v1.*` |

Shared libraries (project references, never services):

* `Common/` – contracts in `Common/Services` (`MessageEnvelope`, `ContractVersions`, `Topics`, market data and job DTOs).
* `Messaging/` – the `IMessageBus` abstraction (`Messaging/Bus`), its in-memory and NetMQ transports, the broker and
  `BusMessagingHandler`, which keeps the existing `Messaging` packet handling and publishes engine packets as job events.
* `Engine/` – `RemoteDataProvider` (`IDataProvider`) and `RemoteDataQueueHandler` (`IDataQueueHandler`) in
  `Engine/DataFeeds/Remote`, the engine's client side of the Market Data Service.

## What stays in the backtest worker

`Engine/DataFeeds` holds both the data access layer and the data step of the loop. Only the access layer moved:

| Stays in the engine process | Behind the Market Data Service |
|---|---|
| Subscription readers, enumerators, fill-forward, aggregation, `SubscriptionSynchronizer`, `TimeSliceFactory`, `AlgorithmManager`, transaction and portfolio handling | Raw data file storage and retrieval (`IDataProvider.Fetch`), listing, storing, downloading, live data queue handlers |

The engine reads files through `RemoteDataProvider`: a read-through cache in the worker's data folder. A file is fetched
once over `marketdata.v1.fetch` and read from disk afterwards, so the loop never waits on the network once data is cached.
LEAN reads market hours, symbol properties, map files and factor files straight from disk, so the worker syncs those
folders before its first job (`RemoteDataProvider.DefaultReferenceData`).

## Message bus

`IMessageBus` (in `Messaging/Bus/IMessageBus.cs`) provides:

* `Publish` / `Subscribe(topicPrefix)` – fire-and-forget events, matched by topic prefix, delivered in order per publisher.
* `Respond(topic)` / `RequestAsync` – request/reply. Each bus has a private inbox topic; replies carry the request's
  correlation id. A responder exception comes back as an error reply and is raised as `MessageBusException` for the requester.

Transports (selected by `message-bus`, default `netmq`):

* `netmq` – PUB/SUB sockets through the broker. Wire frames: `[topic][envelope json][binary payload]`.
  Settings: `message-bus-publish-address` (default `tcp://localhost:5560`), `message-bus-subscribe-address` (default `tcp://localhost:5561`).
* `in-memory` – same envelope serialization inside a single process, used by tests.

Every service applies `LEAN_*` environment variables as configuration (`LEAN_MESSAGE_BUS_PUBLISH_ADDRESS` →
`message-bus-publish-address`), then `--key value` arguments.

### Envelope

| Field | Meaning |
|---|---|
| `id`, `correlationId` | Unique id; request id echoed by replies |
| `topic`, `replyTo` | Destination; inbox for replies |
| `contractVersion` | `marketdata.v1` or `jobs.v1` |
| `senderId`, `createdUtc`, `headers` | Diagnostics; error replies carry the error in a header |
| `body` | JSON (camelCase, string enums, UTC dates, no type names) |
| payload frame | Raw bytes (data files) |

## Contracts

### `marketdata.v1` (Market Data Service)

| Topic | Kind | Request → Reply |
|---|---|---|
| `marketdata.v1.fetch` | request | `DataFetchRequest {key}` → `DataFetchResponse {key, found, size, error}` + file bytes as payload |
| `marketdata.v1.list` | request | `DataListRequest {prefix, contains, maxResults}` → `DataListResponse {files[{key,size,lastModifiedUtc}], truncated}` |
| `marketdata.v1.store` | request | `DataStoreRequest {key, overwrite}` + payload → `DataStoreResponse {key, size}` |
| `marketdata.v1.download` | request | `DataDownloadRequest {symbols, resolution, tickType, start, end}` → `DataDownloadResponse {dataPoints, dataPointsBySymbol, warnings}`; uses the configured `data-downloader` and LEAN's `DownloaderDataProvider` writer |
| `marketdata.v1.subscribe` | request | `DataSubscriptionRequest {subscriptionId, symbol, dataType, resolution, tickType, extendedMarketHours}` → `DataSubscriptionResponse {subscriptionId, streamTopic, active}` |
| `marketdata.v1.unsubscribe` | request | `DataUnsubscribeRequest {subscriptionId}` → `DataSubscriptionResponse` |
| `marketdata.v1.stream.{subscriptionId}` | event | `MarketDataBatch {subscriptionId, points[]}`; `MarketDataPoint` carries Tick, TradeBar, QuoteBar and OpenInterest |
| `marketdata.v1.health` | request | → `ServiceHealth` |

Keys are relative to the data root (`equity/usa/minute/spy/20131007_trade.zip`). Keys that resolve outside the root are rejected.

### `jobs.v1` (API Gateway ↔ workers)

| Topic | Kind | Body |
|---|---|---|
| `jobs.v1.next` | request (worker → gateway) | `JobLeaseRequest {workerId, jobTypes}` → `JobLeaseResponse {job}` (null when the queue is empty) |
| `jobs.v1.status` | event (worker) | `JobStatusUpdate {jobId, workerId, status, message, exitCode}` |
| `jobs.v1.events.{jobId}` | event (engine) | `JobEvent {jobId, packetType, message, progress, statistics, timeUtc}` from `BusMessagingHandler` |
| `jobs.v1.cancel` | event (gateway) | `JobCancelRequest {jobId}`; the worker running it kills the engine process |

`JobRecord` statuses: `Queued → Assigned → Running → Completed | Failed | Cancelled`.

### Versioning

Contract versions are part of every topic and envelope. Additive changes (new optional fields) stay in `v1`.
Breaking changes add a `v2` topic set served next to `v1` until all consumers move.

## API Gateway HTTP surface

Authentication is the LEAN API scheme (`Api/ApiConnection.cs`): `Authorization: Basic base64(userId:sha256("{token}:{timestamp}"))`
and a `Timestamp` header (unix seconds, ±5 minutes). WebSocket clients send `?userId=&timestamp=&hash=` instead.
Users come from `gateway-users` (JSON `{"userId":"token"}`) or `job-user-id` / `api-access-token`.

| Method | Path | |
|---|---|---|
| GET | `/` | Dashboard (submit jobs, watch status and events) |
| GET | `/health` | Gateway, Market Data Service and active workers (no auth) |
| POST | `/api/v1/jobs/{backtest\|live\|research}` | Body `JobSubmission {algorithmTypeName, algorithmLanguage, algorithmLocation, notebook, name, parameters}` → `201 JobRecord` |
| GET | `/api/v1/jobs`, `/api/v1/jobs/{id}`, `/api/v1/jobs/{id}/events` | Job list, job, recorded events |
| POST | `/api/v1/jobs/{id}/cancel` | Cancel |
| GET | `/api/v1/data?prefix=` | List data |
| GET / PUT | `/api/v1/data/file?key=` | Fetch / store a data file |
| POST | `/api/v1/data/download` | `DataDownloadRequest` |
| WS | `/ws/jobs/{id}` | Job events (history, then live) |
| WS | `/ws/marketdata?ticker=&securityType=&market=&resolution=` | Live `MarketDataBatch` stream |

## Worker job execution

For each job the worker writes `{results}/{jobId}/config.json` from the launcher's `config.json` with:
`environment` (`backtesting` or `live-paper`), algorithm settings, `job-id`, `parameters`, `close-automatically=true`,
`data-folder` = worker cache, `results-destination-folder` = the job folder, `data-provider` = `RemoteDataProvider`,
`messaging-handler` = `BusMessagingHandler`, live `data-queue-handler` = `RemoteDataQueueHandler`, plus the bus addresses.
It then runs `dotnet QuantConnect.Lean.Launcher.dll --config <file>` and reports the exit code as the job status.
Engine output stays in the job folder (`log.txt`, result json files).

## Behavior preservation

* The default `Launcher/config.json` is unchanged (`DefaultDataProvider`, `Messaging`), so existing runs and tests are unaffected.
* `Tests/Services/RemoteBacktestParityTests` runs regression algorithms with an empty data folder filled only through
  the Market Data Service (in-memory and NetMQ transports) and asserts the exact regression statistics, including `OrderListHash`.

## Not in Phase 1

* Durable job storage: the gateway's queue is in memory, so queued jobs are lost on restart.
* Several workers can share the queue, but a lease is not reassigned if its worker dies.
* Python research notebooks run with the worker image's Jupyter. QuantBook data access still reads the local data folder.
* The gateway does not proxy the QuantConnect cloud API (`Api/` stays the cloud client library).
