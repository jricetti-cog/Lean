#!/bin/sh
# Compose smoke test (plan step s2.4): submits BasicTemplateAlgorithm as a jobs.v1.BacktestRequested and asserts it
# completes. Run from the repository root after `docker compose --profile infra up -d --wait` and building the
# images (`docker buildx bake -f Services/docker-bake.hcl`). Uses the same defaults/.env values as docker-compose.yml.
#
# Until the backtest-worker host exists (s4.3) this script plays its part: it hands the job to the stock Launcher in
# the backtest-worker image with the bus handlers selected by config (bus-job-file, ADR 0001), on the compose
# network, against the compose RabbitMQ, MinIO and lean-data volume. It then asserts that:
#   - the Launcher exits 0 and prints BasicTemplateAlgorithm's expected statistics,
#   - lean.events carries the job's heartbeat and a final packets.v1.PacketRef with the same OrderListHash,
#   - the referenced result file is in lean-results with the advertised size and SHA-256.
set -eu
[ -f .env ] && set -a && . ./.env && set +a

REGISTRY="${REGISTRY:-lean}"; TAG="${TAG:-dev}"
IMAGE="${SMOKE_IMAGE:-${REGISTRY}/backtest-worker:${TAG}}"
RABBITMQ_USER="${RABBITMQ_USER:-lean}"; RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}"
RABBITMQ_VHOST="${RABBITMQ_VHOST:-lean}"; RABBITMQ_MANAGEMENT_PORT="${RABBITMQ_MANAGEMENT_PORT:-15672}"
MINIO_SERVICE_ACCESS_KEY="${MINIO_SERVICE_ACCESS_KEY:-lean-services}"
MINIO_SERVICE_SECRET_KEY="${MINIO_SERVICE_SECRET_KEY:-lean-dev-minio-services}"
MINIO_BUCKET_RESULTS="${MINIO_BUCKET_RESULTS:-lean-results}"
MINIO_IMAGE="${MINIO_IMAGE:-pgsty/minio:RELEASE.2026-08-04T00-00-00Z}"
NETWORK="${COMPOSE_PROJECT_NAME:-lean}_lean"
VOLUME="${COMPOSE_PROJECT_NAME:-lean}_lean-data"
TIMEOUT="${SMOKE_TIMEOUT_SECONDS:-600}"
ALGORITHM=BasicTemplateAlgorithm
EXPECTED_ORDER_LIST_HASH=3da9fa60bf95b9ed148b95e02e0cfc9e   # BasicTemplateAlgorithm.ExpectedStatistics
EXPECTED_TOTAL_ORDERS=1

pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; exit 1; }
api="http://127.0.0.1:${RABBITMQ_MANAGEMENT_PORT}/api"
rmq() { curl -fsS -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" -H 'content-type: application/json' "$@"; }

job_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
observe="smoke.${job_id}"
work=$(mktemp -d)
cleanup() { rmq -X DELETE "${api}/queues/${RABBITMQ_VHOST}/${observe}" >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

echo "== submit ${ALGORITHM} (job ${job_id}, image ${IMAGE})"
docker image inspect "$IMAGE" >/dev/null 2>&1 || fail "image ${IMAGE} not found: build it with docker buildx bake -f Services/docker-bake.hcl"
# Observe every event of this job without taking messages from the real consumers' queues.
rmq -X PUT -d '{"durable":false,"auto_delete":false,"arguments":{"x-expires":3600000}}' "${api}/queues/${RABBITMQ_VHOST}/${observe}" >/dev/null
rmq -X POST -d "{\"routing_key\":\"job.backtest.${job_id}.#\"}" "${api}/bindings/${RABBITMQ_VHOST}/e/lean.events/q/${observe}" >/dev/null
pass "observer queue ${observe} bound to lean.events job.backtest.${job_id}.#"

cat > "${work}/job.json" <<EOF
{
  "jobId": "${job_id}",
  "kind": "backtest",
  "attempt": 1,
  "submittedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "submittedBy": "ci-smoke",
  "results": { "bucket": "${MINIO_BUCKET_RESULTS}", "prefix": "${job_id}/1/" },
  "backtest": {
    "name": "CI smoke ${ALGORITHM}",
    "algorithm": { "source": "builtin", "language": "CSharp", "typeName": "${ALGORITHM}" }
  }
}
EOF
chmod -R a+rX "$work"

echo "== run the engine"
set +e
timeout "$TIMEOUT" docker run --rm --network "$NETWORK" \
    -v "${VOLUME}:/Lean/Data:ro" -v "${work}:/smoke:ro" \
    -e LEAN_BUS_JOB_FILE=/smoke/job.json \
    -e LEAN_BUS_WORKER_ID=ci-smoke \
    -e LEAN_BUS_WORK_FOLDER=/work/job/algorithm \
    -e LEAN_BUS_BUILTIN_ALGORITHM_LOCATION=/Lean/Launcher/bin/Release/QuantConnect.Algorithm.CSharp.dll \
    -e LEAN_BUS_RABBITMQ_URI="amqp://${RABBITMQ_USER}:${RABBITMQ_PASSWORD}@rabbitmq:5672/${RABBITMQ_VHOST}" \
    -e LEAN_BUS_S3_ENDPOINT=http://minio:9000 \
    -e LEAN_BUS_S3_ACCESS_KEY="$MINIO_SERVICE_ACCESS_KEY" -e LEAN_BUS_S3_SECRET_KEY="$MINIO_SERVICE_SECRET_KEY" \
    --entrypoint /bin/bash "$IMAGE" -c '
set -e
mkdir -p /work/job && cd /work/job
exec dotnet /Lean/Launcher/bin/Release/QuantConnect.Lean.Launcher.dll \
    --config /Lean/Launcher/bin/Release/config.json \
    --close-automatically true \
    --data-folder /Lean/Data/ \
    --composer-dll-directory /Lean/Launcher/bin/Release/ \
    --results-destination-folder /work/job \
    --job-queue-handler QuantConnect.Services.EngineHandlers.BusJobQueueHandler \
    --messaging-handler QuantConnect.Services.EngineHandlers.BusMessagingHandler' > "${work}/engine.log" 2>&1
status=$?
set -e
grep -E 'BusJobQueueHandler|BusMessagingHandler|STATISTICS:: (Total Orders|Net Profit|OrderListHash)|ERROR::|Exiting Lean' "${work}/engine.log" | sed 's/^/      /'
[ "$status" -eq 0 ] || { tail -50 "${work}/engine.log"; fail "Launcher exited ${status}"; }
pass "Launcher exited 0"
grep -q "STATISTICS:: OrderListHash ${EXPECTED_ORDER_LIST_HASH}" "${work}/engine.log" || fail "OrderListHash is not ${EXPECTED_ORDER_LIST_HASH}"
grep -q "STATISTICS:: Total Orders ${EXPECTED_TOTAL_ORDERS}\$" "${work}/engine.log" || fail "Total Orders is not ${EXPECTED_TOTAL_ORDERS}"
pass "statistics match ${ALGORITHM}.ExpectedStatistics (Total Orders ${EXPECTED_TOTAL_ORDERS}, OrderListHash ${EXPECTED_ORDER_LIST_HASH})"

echo "== events on lean.events"
# The final PacketRef is the last message published before Dispose drains the outbox; poll briefly for it.
for _ in 1 2 3 4 5 6 7 8 9 10; do
    rmq -X POST -d '{"count":1000,"ackmode":"ack_requeue_true","encoding":"auto"}' "${api}/queues/${RABBITMQ_VHOST}/${observe}/get" > "${work}/events.json"
    grep -Eq 'final\\?": *true' "${work}/events.json" && break
    sleep 1
done
python3 - "${work}/events.json" "$job_id" "$EXPECTED_ORDER_LIST_HASH" "$MINIO_BUCKET_RESULTS" > "${work}/final.env" <<'EOF'
import json, sys
events, job_id, order_list_hash, bucket = json.load(open(sys.argv[1])), sys.argv[2], sys.argv[3], sys.argv[4]
for e in events:
    print(f"      {e['routing_key']:<70} {e['properties'].get('type', '-')}", file=sys.stderr)
types = [e["properties"].get("type") for e in events]
def check(ok, msg):
    if not ok:
        print(f"FAIL  {msg}", file=sys.stderr); sys.exit(1)
    print(f"PASS  {msg}", file=sys.stderr)
check(all(e["routing_key"].startswith(f"job.backtest.{job_id}.") for e in events), f"{len(events)} events, all on job.backtest.{job_id}.*")
check("events.v1.JobHeartbeat" in types, "heartbeat published")
finals = [json.loads(e["payload"]) for e in events if e["properties"].get("type") == "packets.v1.PacketRef"]
finals = [f for f in finals if f.get("final")]
check(len(finals) == 1, "exactly one final packets.v1.PacketRef")
ref = finals[0]
check(ref.get("format") == "lean-result-file" and ref["object"]["bucket"] == bucket, f"final result by reference: {ref['object']['bucket']}/{ref['object']['key']} ({ref['object']['size']} bytes)")
check(ref.get("statistics", {}).get("OrderListHash") == order_list_hash, f"PacketRef statistics OrderListHash = {order_list_hash}")
print(f"KEY={ref['object']['key']}\nSIZE={ref['object']['size']}\nSHA256={ref['object']['sha256']}")
EOF
. "${work}/final.env"

echo "== result file in MinIO"
docker run --rm --network "$NETWORK" --entrypoint /bin/sh \
    -e AK="$MINIO_SERVICE_ACCESS_KEY" -e SK="$MINIO_SERVICE_SECRET_KEY" -e OBJ="${MINIO_BUCKET_RESULTS}/${KEY}" \
    -e SIZE="$SIZE" -e SHA256="$SHA256" "$MINIO_IMAGE" -c '
set -eu
mc alias set svc http://minio:9000 "$AK" "$SK" >/dev/null
mc cat "svc/$OBJ" > /tmp/result.json
[ "$(wc -c < /tmp/result.json | tr -d " ")" = "$SIZE" ] || { echo "FAIL  size of $OBJ is not $SIZE"; exit 1; }
[ "$(sha256sum /tmp/result.json | cut -d" " -f1)" = "$SHA256" ] || { echo "FAIL  sha256 of $OBJ is not $SHA256"; exit 1; }
echo "PASS  $OBJ: $SIZE bytes, sha256 $SHA256"'
echo "== ${ALGORITHM} completed: smoke test passed"
