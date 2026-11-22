# api-gateway (plan step s4.5)

The only public entry point (ADR 0001/0002). Compose publishes it on `127.0.0.1:${GATEWAY_PORT:-5080}` only.

| Public route | Handled by |
| --- | --- |
| `POST /api/v1/algorithms` (multipart `file`: `.py`, `.cs`, `.dll` or project `.zip`) | gateway -> orchestrator `POST /internal/v1/algorithms` |
| `POST /api/v1/backtests` (JSON `BacktestSpec`, or multipart `file` + `name`, `typeName`, `parameters`, `startDate`, `endDate`, `cash`, `maxRuntimeSeconds`, `fetchMissingData`) | gateway -> orchestrator `POST /internal/v1/jobs` |
| `POST /api/v1/live` (JSON `LiveSpec`, or multipart `file` + `brokerage`, `dataQueueHandlers`), `POST /api/v1/live/{id}/stop` | gateway -> orchestrator |
| `POST /api/v1/optimizations`, `POST /api/v1/reports` (`ReportSpec`; the source job must be the caller's) | gateway -> orchestrator, which dispatches `jobs.v1.ReportRequested` to the report service |
| `GET /api/v1/jobs`, `GET /api/v1/jobs/{id}`, `POST /api/v1/jobs/{id}/cancel` | gateway -> orchestrator (non-admins see only their jobs) |
| `GET /api/v1/jobs/{id}/result`, `/charts[/{chart}]`, `/orders`, `/order-events`, `/logs`, `/artifacts` | YARP -> results `/internal/v1/results/{id}/...` |
| `GET /api/v1/jobs/{id}/events` | Server-Sent Events (ADR 0002): per-request `sse.<instance>.<uuid>` exclusive auto-delete queue bound to `job.*.<jobId>.#` on `lean.events`; `snapshot` first, then every bus message of the job, `: keep-alive` every `Gateway:Events:KeepAliveSeconds` (15), closed after the terminal status. Per client, `progress`/`heartbeat`/`result` are latest-wins and `log`/`order` keep `Gateway:Events:LogBuffer`/`OrderBuffer` (500/1000) frames; frames after a drop carry `dropped` so the console reloads over REST. `?access_token=` is accepted on this route only (EventSource cannot send headers). |
| `GET /api/v1/jobs/{id}/result/download`, `GET /api/v1/reports/{id}/{html,pdf}` | gateway: 302 to a presigned MinIO URL |
| `/api/v1/data/**` | YARP -> market-data `/internal/v1/data/**` |
| `/api/v1/optimizations/**` (estimate, state, children) | YARP -> optimizer `/internal/v1/optimizations/**` |
| `GET /api/v1/reports[?sourceJobId=]`, `GET /api/v1/reports/{id}` | YARP -> report `/internal/v1/reports...` (non-admins must pass a `sourceJobId` they own) |
| `GET /openapi` | merged document: gateway operations + each proxied service's live `/openapi/v1.json` (or, when that is unavailable, its embedded contract from `docs/architecture/contracts/openapi/`) under its public paths; `x-lean-upstreams` names the source |

- **Auth**: `Gateway:ApiKeys` (`Key` or `KeySha256`, `User`, `Admin`) sent as `X-Api-Key` or `Authorization: Bearer`.
  A three-part bearer token goes to JWT validation when `Gateway:Jwt:Authority` (OIDC) or `Gateway:Jwt:SigningKey`
  is set, so an identity provider can replace the keys; `sub` is the user, role `lean-admin` is admin.
- **Rate limiting**: fixed window per API key / JWT subject (`Gateway:RateLimit`), `429` problem with `Retry-After`.
- **Request ids**: `X-Request-ID` (client value if well-formed, else the trace id) is echoed, tagged on the server span
  as `lean.request_id` (and baggage) and forwarded upstream with `traceparent`.
- **Upstreams**: `X-Lean-User` is set from the caller; credentials are stripped. An unreachable upstream answers
  `503 application/problem+json` (connect, including DNS, is bounded to 3 s).

Verify against compose with `deploy/compose/verify-gateway.sh` (see `deploy/compose/README.md`).
