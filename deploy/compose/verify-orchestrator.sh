#!/bin/sh
# End-to-end check of job-orchestrator (plan step s4.1) against the compose stack. Run from the repository root:
#
#   ORCHESTRATOR_MAX_ATTEMPTS=2 ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS=10 \
#     docker compose up -d --wait --build job-orchestrator
#   ORCHESTRATOR_MAX_ATTEMPTS=2 ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS=10 deploy/compose/verify-orchestrator.sh
#
# Plays the worker side over HTTP and the RabbitMQ management API (takes dispatches from jobs.backtest, publishes
# job.* events to lean.events), so no worker image is needed. Requires curl and jq.
set -eu
[ -f .env ] && set -a && . ./.env && set +a

RABBITMQ_USER="${RABBITMQ_USER:-lean}"; RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}"
RABBITMQ_VHOST="${RABBITMQ_VHOST:-lean}"; RABBITMQ_MANAGEMENT_PORT="${RABBITMQ_MANAGEMENT_PORT:-15672}"
MAX_ATTEMPTS="${ORCHESTRATOR_MAX_ATTEMPTS:-3}"; TIMEOUT="${ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS:-90}"
API="http://127.0.0.1:${ORCHESTRATOR_PORT:-5081}"
MQ="http://127.0.0.1:${RABBITMQ_MANAGEMENT_PORT}/api"
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; exit 1; }
mq() { curl -fsS -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" -H 'content-type: application/json' "$@"; }
# req METHOD PATH [BODY] [extra curl args...] -> prints "<status> <body>"
req() {
    method=$1; path=$2; shift 2
    if [ $# -gt 0 ]; then body=$1; shift; else body=''; fi
    curl -sS -o /tmp/orch.body -w '%{http_code}' -X "$method" -H "content-type: ${CT:-application/json}" ${body:+--data-binary "$body"} "$@" "$API$path" > /tmp/orch.code
    echo "$(cat /tmp/orch.code) $(cat /tmp/orch.body)"
}
status_of() { curl -fsS "$API/internal/v1/jobs/$1" | jq -r .status; }
wait_status() {
    for _ in $(seq 1 "${3:-30}"); do [ "$(status_of "$1")" = "$2" ] && return 0; sleep 1; done
    fail "job $1 is $(status_of "$1"), expected $2"
}
take_dispatch() {
    mq -X POST "$MQ/queues/$RABBITMQ_VHOST/jobs.backtest/get" -d '{"count":1,"ackmode":"ack_requeue_false","encoding":"auto"}' | jq -c '.[0]'
}
publish() { # publish TYPE JOB_ID ATTEMPT SEGMENT PAYLOAD
    props=$(jq -nc --arg t "$1" --arg j "$2" --argjson a "$3" --arg m "$(cat /proc/sys/kernel/random/uuid)" --argjson ts "$(date +%s)" \
        '{message_id:$m, correlation_id:$j, type:$t, content_type:"application/json", delivery_mode:2, timestamp:$ts, app_id:"backtest-worker",
          headers:{"contract-version":"1.0", "job-id":$j, attempt:$a, "job-kind":"backtest"}}')
    routed=$(mq -X POST "$MQ/exchanges/$RABBITMQ_VHOST/lean.events/publish" \
        -d "$(jq -nc --argjson p "$props" --arg k "job.backtest.$2.$4.v1" --arg b "$5" '{properties:$p, routing_key:$k, payload:$b, payload_encoding:"string"}')" | jq -r .routed)
    [ "$routed" = true ] || fail "$1 for $2 was not routed"
}
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
backtest() { jq -nc --argjson algo "$1" --arg name "$2" '{kind:"backtest", backtest:{name:$name, algorithm:$algo}}'; }

echo "== health"
curl -fsS "$API/healthz" | jq -c . | sed 's/^/      /'
curl -fsS "$API/readyz" >/dev/null && pass "GET /healthz and /readyz (PostgreSQL jobs database reachable)"
docker compose --profile infra --profile orchestrator ps job-orchestrator --format '{{.Service}}|{{.Status}}' | grep -q '(healthy)' \
    && pass "job-orchestrator container healthy"

# This script plays the worker: stop real ones (started by default since the platform has no profiles) so none races it.
docker compose stop backtest-worker optimizer >/dev/null 2>&1 || true
mq -X DELETE "$MQ/queues/$RABBITMQ_VHOST/jobs.backtest/contents" >/dev/null
pass "purged jobs.backtest (no workers run in this check)"

echo "== algorithm upload (MinIO)"
set -- $(CT=text/x-python req POST "/internal/v1/algorithms?fileName=main.py" 'class SmokeAlgorithm(QCAlgorithm): pass')
[ "$1" = 201 ] || fail "upload returned $*"
obj=$(cat /tmp/orch.body)
echo "      $obj"
pass "stored as s3://$(echo "$obj" | jq -r .bucket)/$(echo "$obj" | jq -r .key)"
algo=$(jq -nc --argjson o "$obj" '{source:"upload", language:"python", object:$o}')

echo "== validation"
set -- $(req POST /internal/v1/jobs '{"kind":"backtest","live":{"brokerage":"x","algorithm":{"source":"builtin","typeName":"BasicTemplateAlgorithm"}}}')
[ "$1" = 400 ] && pass "mismatched spec -> 400: $(jq -r .detail /tmp/orch.body) $(jq -c '.errors // empty' /tmp/orch.body)" || fail "expected 400, got $1"
missing=$(jq -nc '{source:"upload", language:"python", object:{bucket:"lean-algorithms", key:"sha256/none/main.py", size:1, sha256:("0"*64)}}')
set -- $(req POST /internal/v1/jobs "$(backtest "$missing" missing)")
[ "$1" = 400 ] && pass "algorithm object missing in MinIO -> 400" || fail "expected 400, got $1"

echo "== happy path: queued -> dispatched -> running -> completed"
key="smoke-$(date +%s)"
set -- $(req POST /internal/v1/jobs "$(backtest "$algo" smoke)" -H "Idempotency-Key: $key" -H 'X-Lean-User: verify')
[ "$1" = 201 ] || fail "create returned $*"
job=$(jq -r .id /tmp/orch.body)
pass "created $job ($(jq -r .status /tmp/orch.body), attempt $(jq -r .attempt /tmp/orch.body))"
set -- $(req POST /internal/v1/jobs "$(backtest "$algo" smoke)" -H "Idempotency-Key: $key" -H 'X-Lean-User: verify')
[ "$(jq -r .id /tmp/orch.body)" = "$job" ] && pass "same Idempotency-Key and body -> same job ($1)" || fail "idempotent replay returned $*"
set -- $(req POST /internal/v1/jobs "$(backtest "$algo" other)" -H "Idempotency-Key: $key" -H 'X-Lean-User: verify')
[ "$1" = 409 ] && pass "same Idempotency-Key, different body -> 409" || fail "expected 409, got $1"
wait_status "$job" dispatched
pass "dispatched (outbox relayed with publisher confirms)"
msg=$(take_dispatch)
[ "$(echo "$msg" | jq -r .properties.type)" = jobs.v1.BacktestRequested ] && [ "$(echo "$msg" | jq -r '.properties.headers["job-id"]')" = "$job" ] \
    || fail "unexpected dispatch $msg"
pass "jobs.backtest delivered $(echo "$msg" | jq -r .properties.type) (routing key $(echo "$msg" | jq -r .routing_key), attempt $(echo "$msg" | jq -r .properties.headers.attempt), submittedBy $(echo "$msg" | jq -r '.payload | fromjson | .submittedBy'))"
set -- $(req POST "/internal/v1/jobs/$job/attempts" '{"workerId":"verify-worker","deliveryCount":0,"attempt":1}')
[ "$1" = 201 ] && pass "attempt taken: $(cat /tmp/orch.body)" || fail "attempts returned $*"
publish events.v1.JobStarted "$job" 1 status "$(jq -nc --arg j "$job" --arg t "$(now)" '{jobId:$j, kind:"backtest", attempt:1, startedAt:$t, workerId:"verify-worker", results:{bucket:"lean-results", prefix:($j+"/1/")}}')"
wait_status "$job" running
pass "job.started -> running"
publish events.v1.JobProgress "$job" 1 progress "$(jq -nc --arg j "$job" --arg t "$(now)" '{jobId:$j, kind:"backtest", attempt:1, at:$t, progress:0.42}')"
for _ in $(seq 1 20); do [ "$(curl -fsS "$API/internal/v1/jobs/$job" | jq -r .progress)" = 0.42 ] && break; sleep 0.5; done
[ "$(curl -fsS "$API/internal/v1/jobs/$job" | jq -r .progress)" = 0.42 ] && pass "job.progress -> progress 0.42" || fail "progress not applied"
publish events.v1.JobCompleted "$job" 1 status "$(jq -nc --arg j "$job" --arg t "$(now)" '{jobId:$j, kind:"backtest", attempt:1, completedAt:$t, artifacts:[], result:{bucket:"lean-results", key:($j+"/1/result.json"), size:2, sha256:("a"*64)}}')"
wait_status "$job" completed
pass "job.completed -> completed: $(curl -fsS "$API/internal/v1/jobs/$job" | jq -c '{status, attempt, progress, startedAt, finishedAt}')"

echo "== cancellation"
set -- $(req POST /internal/v1/jobs "$(backtest "$algo" cancel-queued)")
queued=$(jq -r .id /tmp/orch.body)
wait_status "$queued" dispatched
set -- $(req POST "/internal/v1/jobs/$queued/cancel" '{"reason":"verify"}')
[ "$1" = 202 ] && [ "$(jq -r .status /tmp/orch.body)" = cancelled ] && pass "dispatched job without a worker -> cancelled directly" || fail "cancel returned $*"
take_dispatch >/dev/null
set -- $(req POST "/internal/v1/jobs/$queued/attempts" '{"workerId":"verify-worker","deliveryCount":0,"attempt":1}')
[ "$1" = 409 ] && pass "its dispatch is refused at attempt time -> 409" || fail "expected 409, got $1"
set -- $(req POST /internal/v1/jobs "$(backtest "$algo" cancel-running)")
running=$(jq -r .id /tmp/orch.body)
wait_status "$running" dispatched
take_dispatch >/dev/null
req POST "/internal/v1/jobs/$running/attempts" '{"workerId":"verify-worker","deliveryCount":0,"attempt":1}' >/dev/null
mq -X PUT "$MQ/queues/$RABBITMQ_VHOST/verify.control" -d '{"durable":false,"auto_delete":false}' >/dev/null
mq -X POST "$MQ/bindings/$RABBITMQ_VHOST/e/lean.control/q/verify.control" -d "$(jq -nc --arg k "job.$running.cancel.v1" '{routing_key:$k}')" >/dev/null
set -- $(req POST "/internal/v1/jobs/$running/cancel" '{"reason":"verify"}')
[ "$1" = 202 ] && pass "held job -> 202, $(jq -r .status /tmp/orch.body) until the worker confirms" || fail "cancel returned $*"
for _ in $(seq 1 20); do
    control=$(mq -X POST "$MQ/queues/$RABBITMQ_VHOST/verify.control/get" -d '{"count":1,"ackmode":"ack_requeue_false","encoding":"auto"}' | jq -c '.[0] // empty')
    [ -n "$control" ] && break; sleep 0.5
done
mq -X DELETE "$MQ/queues/$RABBITMQ_VHOST/verify.control" >/dev/null
[ "$(echo "$control" | jq -r .properties.type)" = control.v1.CancelJob ] || fail "no CancelJob on lean.control"
pass "lean.control carried control.v1.CancelJob ($(echo "$control" | jq -r .routing_key))"
publish events.v1.JobCancelled "$running" 1 status "$(jq -nc --arg j "$running" --arg t "$(now)" '{jobId:$j, kind:"backtest", attempt:1, cancelledAt:$t, reason:"verify"}')"
wait_status "$running" cancelled
pass "job.cancelled -> cancelled"

echo "== worker death: heartbeat timeout ${TIMEOUT}s, max attempts ${MAX_ATTEMPTS}"
set -- $(req POST /internal/v1/jobs "$(backtest "$algo" dies)")
dies=$(jq -r .id /tmp/orch.body)
attempt=1
while :; do
    wait_status "$dies" dispatched
    take_dispatch >/dev/null
    req POST "/internal/v1/jobs/$dies/attempts" "{\"workerId\":\"dying-$attempt\",\"deliveryCount\":0,\"attempt\":$attempt}" >/dev/null
    publish events.v1.JobStarted "$dies" "$attempt" status "$(jq -nc --arg j "$dies" --argjson a "$attempt" --arg t "$(now)" '{jobId:$j, kind:"backtest", attempt:$a, startedAt:$t, workerId:"dying", results:{bucket:"lean-results", prefix:($j+"/"+($a|tostring)+"/")}}')"
    wait_status "$dies" running
    echo "      attempt $attempt running, worker goes silent"
    [ "$attempt" -ge "$MAX_ATTEMPTS" ] && break
    wait_status "$dies" dispatched $((TIMEOUT + 30))
    attempt=$(curl -fsS "$API/internal/v1/jobs/$dies" | jq -r .attempt)
    pass "heartbeat timeout -> retried as attempt $attempt"
done
wait_status "$dies" failed $((TIMEOUT + 30))
pass "after $MAX_ATTEMPTS attempts -> failed: $(curl -fsS "$API/internal/v1/jobs/$dies" | jq -c '{status, attempt, error}')"

echo "== queue depth"
for i in 1 2 3; do req POST /internal/v1/jobs "$(backtest "$algo" "depth-$i")" >/dev/null; done
# RabbitMQ refreshes management stats every 5 s and the orchestrator caches hints for 5 s
for _ in $(seq 1 30); do
    hints=$(curl -fsS "$API/internal/v1/queues")
    [ "$(echo "$hints" | jq '.[] | select(.queue=="jobs.backtest") | .ready')" -ge 3 ] && break; sleep 1
done
echo "$hints" | jq -c '.[]' | sed 's/^/      /'
[ "$(echo "$hints" | jq '.[] | select(.queue=="jobs.backtest") | .ready')" -ge 3 ] && pass "GET /internal/v1/queues reports jobs.backtest ready >= 3" || fail "queue depth"
curl -fsS "$API/metrics" | grep -E '^lean_(queue_(ready|suggested_replicas)\{queue="jobs.backtest"|jobs\{kind="backtest"|outbox)' | sed 's/^/      /'
pass "GET /metrics exports queue depth and suggested replicas"
mq -X DELETE "$MQ/queues/$RABBITMQ_VHOST/jobs.backtest/contents" >/dev/null
curl -fsS "$API/internal/v1/jobs?kind=backtest&limit=5" | jq -r '.items[] | "      \(.id) \(.status) attempt \(.attempt)"'
echo "all orchestrator checks passed"
