# Service contracts (v1)

The contracts every later phase codes against (plan step s1.4). They implement ADR 0001 (decomposition),
0002 (communication: REST + RabbitMQ, envelope, acks, SSE), 0003 (storage), 0004 (market data) and follow
ADR 0005 (final results go through MinIO, not the broker). Docs only: no engine type changes; the monolith's
`Launcher/config.json` is untouched.

| Path | What |
|---|---|
| [`openapi/`](openapi/) | OpenAPI 3.1 per HTTP service: [gateway](openapi/gateway.openapi.yaml) (public `/api/v1`, SSE `GET /api/v1/jobs/{id}/events`), [orchestrator](openapi/orchestrator.openapi.yaml), [market-data](openapi/market-data.openapi.yaml), [results](openapi/results.openapi.yaml), [optimizer](openapi/optimizer.openapi.yaml), [report](openapi/report.openapi.yaml); shared components in [`common.yaml`](openapi/common.yaml) |
| [`messages/v1/`](messages/v1/) | JSON Schema (draft 2020-12) message bodies: `job.submitted`, `job.started`, `job.progress`, `job.completed`, `job.failed`, `result.packet`, `optimization.requested`, `report.requested`, plus `job.heartbeat`, `job.cancelled`, `job.cancel`, `marketdata.fetch.requested`; the AMQP [envelope](messages/v1/envelope.schema.json); shared [`defs`](messages/v1/defs.schema.json) (also used by the OpenAPI documents) |
| [`messages/v1/examples/`](messages/v1/examples/) | One envelope + body per message along the submit → worker → results → report flow; [`engine/`](messages/v1/examples/engine/) holds packets serialized by LEAN's own packet classes |
| [`topology/`](topology/) | [`topology.md`](topology/topology.md): exchanges, queues, routing keys, DLQs, envelope, acks, **maximum inline message size (256 KiB)**; [`definitions.json`](topology/definitions.json) + [`rabbitmq.conf`](topology/rabbitmq.conf) load it into RabbitMQ 3.13 |
| [`diagrams/`](diagrams/) | Mermaid [context diagram](diagrams/context.md) and [sequence diagram](diagrams/submit-backtest-to-report.md) (submit backtest → worker → results → report) |

## Rules

* **Versioning.** Routing keys end in `.v1`, AMQP types are `<family>.v1.<Name>`, the `contract-version`
  header is `1.<minor>`, HTTP paths are `/api/v1` / `/internal/v1`. Within v1 changes are additive only
  (new optional fields, new types, new enum values consumers ignore). A breaking change adds v2 types, routing
  keys and queues (and `/v2` paths) side by side.
* **Engine packets unchanged.** `result.packet` bodies are LEAN's existing Newtonsoft JSON of
  `BacktestResultPacket`, `LiveResultPacket`, `LogPacket`, `DebugPacket`, `SystemDebugPacket`,
  `OrderEventPacket`, `RuntimeErrorPacket`, `HandledErrorPacket` and `AlgorithmStatusPacket`.
* **No large payloads on the bus.** `job.completed.result` and the final `result.packet` (`PacketRef`,
  `final: true`) carry a MinIO `ObjectRef` (`bucket`, `key`, `size`, `sha256`) to the engine's result JSON.
  Only progress-sized packets go inline, up to 256 KiB; larger packets go by reference too.
* **Naming.** Contract bodies are camelCase; inline engine packets keep their PascalCase form. IDs are UUIDs,
  timestamps UTC ISO-8601, errors RFC 9457 problem details.

## Verification

```bash
cd docs/architecture/contracts
npm install            # spectral-cli 6.15.0, ajv 8.20.0, ajv-formats 3.0.1 (devDependencies only)
npm run lint           # spectral (OpenAPI, --fail-severity=warn) + node validate.mjs (ajv, strict)
```

`validate.mjs` compiles every schema (Ajv draft 2020-12, strict mode), validates each example's envelope and
body against the schema chosen by its AMQP type and its routing key against the topology, enforces the
256 KiB inline limit, checks the engine-serialized packets (`examples/engine/`, the final
`BacktestResultPacket` must be rejected inline), runs negative cases and checks `definitions.json`
(exchanges, dispatch queues, DLQs, `.v1` keys). Diagrams render with `npx @mermaid-js/mermaid-cli -i
diagrams/<file>.md -o out.svg`. The topology loads with:

```bash
docker run -d --name lean-rmq -p 15672:15672 \
  -v $PWD/topology/rabbitmq.conf:/etc/rabbitmq/rabbitmq.conf:ro \
  -v $PWD/topology/definitions.json:/etc/rabbitmq/definitions.json:ro rabbitmq:3.13-management
```
