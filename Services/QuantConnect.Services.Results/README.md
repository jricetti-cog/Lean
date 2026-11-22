# Results service (plan step s3.2)

Consumes `results.packets` (bindings `job.*.*.{result,order,log,error,status}.v1` on `lean.events`), owns the
PostgreSQL `results` database (ADR 0003) and serves `docs/architecture/contracts/openapi/results.openapi.yaml`.

| Message | Effect |
|---|---|
| `packets.v1.BacktestResult` / `LiveResult` (partial) | statistics/runtime statistics (newer sequence only), orders upserted by id, order events, chart deltas merged and downsampled |
| `packets.v1.PacketRef` `final: true`, `lean-result-file` | result file read from MinIO (size + SHA-256 checked) and indexed: final orders, all order events, charts, statistics + the packet's statistics, so `OrderListHash` (added by `Messaging.Send` to the Progress == 1 packet, not in the file) is kept in `results.order_list_hash` and `statistics.OrderListHash` |
| `packets.v1.PacketRef` `lean-packet` | the referenced packet is applied like the inline one |
| inline final `BacktestResult` (Progress == 1) | as above, and the full `BacktestResultPacket` JSON is written to `lean-results/<jobId>/<attempt>/<algorithmId>-final-packet.json` |
| `OrderEvent`, `Log`, `Debug`, `SystemDebug`, `RuntimeError`, `HandledError`, `AlgorithmStatus` | order events, capped logs (`Results:MaxLogsPerAttempt`, default 10000), algorithm status in `state` |
| `events.v1.JobStarted` / `JobCompleted` / `JobFailed` / `JobCancelled` | attempt status; `job.completed` indexes the result file itself if the final `PacketRef` was missed |

**Idempotency.** Each message is applied in one transaction together with a `processed_messages` row keyed by
`(job_id, attempt, "seq:<sequence header>")` (or `"msg:<message id>"` for status events without a sequence); the
delivery is acked only after commit. A redelivery or a republish of the same packet is a no-op, orders are keyed by
order id and only replaced by an equal-or-newer sequence (the final result always wins), order events by
`(order id, event id)`.

**Charts.** PostgreSQL keeps each chart with every series downsampled to `Results:ChartMaxPoints` (default 2000,
largest-triangle-three-buckets for line series). `GET .../charts/{chart}?maxPoints=N` downsamples that copy; asking
for more points than it holds (or omitting `maxPoints`) reads the full-resolution chart from the result JSON in MinIO.

Settings: `ConnectionStrings__Results`, `RabbitMQ__Uri`, `S3__ServiceUrl` / `S3__AccessKey` / `S3__SecretKey` /
`S3__ResultsBucket`, `Results__ChartMaxPoints`, `Results__MaxLogsPerAttempt`, `Results__Prefetch` (16),
`Results__Consume` (true). Migrations (`Storage/Migrations/*.sql`) run at startup under an advisory lock.

```bash
docker compose --profile infra --profile results up -d --wait --build
curl -s localhost:5083/healthz; curl -s localhost:5083/readyz
curl -s "localhost:5083/internal/v1/results/<jobId>"
```
