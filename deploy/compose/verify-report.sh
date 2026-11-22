#!/bin/sh
# End-to-end check of the report service (plan step s3.3) against the compose stack, from a stored result fixture
# (no backtest runs). Run from the repository root:
#
#   docker compose --profile infra --profile report up -d --wait --build
#   deploy/compose/verify-report.sh
#
# Uploads Tests/TestData/test_report_data.json to lean-results, publishes jobs.v1.ReportRequested to lean.jobs over
# the RabbitMQ management API, and checks job.completed, the report API and the HTML/PDF in lean-reports.
# Requires curl, jq, sha256sum and docker.
set -eu
[ -f .env ] && set -a && . ./.env && set +a

RABBITMQ_USER="${RABBITMQ_USER:-lean}"; RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}"
RABBITMQ_VHOST="${RABBITMQ_VHOST:-lean}"; RABBITMQ_MANAGEMENT_PORT="${RABBITMQ_MANAGEMENT_PORT:-15672}"
API="http://127.0.0.1:${REPORT_PORT:-5084}"
MQ="http://127.0.0.1:${RABBITMQ_MANAGEMENT_PORT}/api"
NETWORK="${COMPOSE_PROJECT_NAME:-lean}_lean"
MINIO_IMAGE="${MINIO_IMAGE:-pgsty/minio:RELEASE.2026-08-04T00-00-00Z}"
RESULTS="${MINIO_BUCKET_RESULTS:-lean-results}"; REPORTS="${MINIO_BUCKET_REPORTS:-lean-reports}"
FIXTURE="${FIXTURE:-Tests/TestData/test_report_data.json}"
OUT="${OUT:-/tmp/lean-report-verify}"
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; exit 1; }
mq() { curl -fsS -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" -H 'content-type: application/json' "$@"; }
mc() { # mc COMMAND... with $OUT mounted at /out and the fixture at /fixture.json
    docker run --rm --network "$NETWORK" --entrypoint /bin/sh -v "$PWD/$FIXTURE:/fixture.json:ro" -v "$OUT:/out" \
        -e AK="${MINIO_SERVICE_ACCESS_KEY:-lean-services}" -e SK="${MINIO_SERVICE_SECRET_KEY:-lean-dev-minio-services}" \
        "$MINIO_IMAGE" -c "mc alias set svc http://minio:9000 \"\$AK\" \"\$SK\" >/dev/null && $*"
}
uuid() { cat /proc/sys/kernel/random/uuid; }
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
request() { # request JOB_ID SHA256 -> publishes jobs.v1.ReportRequested for the uploaded fixture
    body=$(jq -nc --arg j "$1" --arg s "$source" --arg t "$(now)" --arg b "$RESULTS" --arg k "$key" --argjson n "$size" --arg h "$2" \
        '{jobId:$j, kind:"report", attempt:1, submittedAt:$t, submittedBy:"verify-report",
          report:{sourceJobId:$s, strategyName:"Stored fixture", strategyDescription:"verify-report.sh", formats:["html","pdf"]},
          backtestResult:{bucket:$b, key:$k, size:$n, sha256:$h, contentType:"application/json"}}')
    props=$(jq -nc --arg j "$1" --arg m "$(uuid)" --argjson ts "$(date +%s)" \
        '{message_id:$m, correlation_id:$j, type:"jobs.v1.ReportRequested", content_type:"application/json", delivery_mode:2,
          timestamp:$ts, app_id:"job-orchestrator", headers:{"contract-version":"1.0", "job-id":$j, attempt:1, "job-kind":"report"}}')
    routed=$(mq -X POST "$MQ/exchanges/$RABBITMQ_VHOST/lean.jobs/publish" \
        -d "$(jq -nc --argjson p "$props" --arg b "$body" '{properties:$p, routing_key:"jobs.report.v1", payload:$b, payload_encoding:"string"}')" | jq -r .routed)
    [ "$routed" = true ] || fail "report.requested for $1 was not routed"
}
watch_events() { # watch_events JOB_ID: a temporary queue bound to the job's events on lean.events
    mq -X PUT "$MQ/queues/$RABBITMQ_VHOST/verify-report.$1" -d '{"durable":false,"auto_delete":false}' >/dev/null
    mq -X POST "$MQ/bindings/$RABBITMQ_VHOST/e/lean.events/q/verify-report.$1" -d "{\"routing_key\":\"job.report.$1.#\"}" >/dev/null
}
terminal_event() { # terminal_event JOB_ID -> first job.completed / job.failed message (type + payload)
    for _ in $(seq 1 180); do
        msg=$(mq -X POST "$MQ/queues/$RABBITMQ_VHOST/verify-report.$1/get" -d '{"count":50,"ackmode":"ack_requeue_false","encoding":"auto"}' \
            | jq -c '[.[] | select(.properties.type == "events.v1.JobCompleted" or .properties.type == "events.v1.JobFailed")][0] // empty | {type:.properties.type, payload:(.payload|fromjson)}')
        [ -n "$msg" ] && { mq -X DELETE "$MQ/queues/$RABBITMQ_VHOST/verify-report.$1" >/dev/null; echo "$msg"; return 0; }
        sleep 1
    done
    fail "no terminal event for report $1"
}

echo "== health"
curl -fsS "$API/healthz" | jq -c . | sed 's/^/      /'
curl -fsS "$API/readyz" >/dev/null && pass "GET /healthz and /readyz on $API"

echo "== stored result fixture"
mkdir -p "$OUT"; rm -f "$OUT"/report.*
source=$(uuid); key="$source/1/result.json"
size=$(wc -c < "$FIXTURE" | tr -d ' '); sha=$(sha256sum "$FIXTURE" | cut -d' ' -f1)
mc "mc cp -q /fixture.json svc/$RESULTS/$key >/dev/null"
pass "uploaded $FIXTURE to $RESULTS/$key ($size bytes, sha256 $sha)"

echo "== report.requested -> job.completed"
job=$(uuid)
watch_events "$job"
request "$job" "$sha"
pass "published jobs.v1.ReportRequested $job to lean.jobs (jobs.report.v1)"
event=$(terminal_event "$job")
[ "$(printf "%s" "$event" | jq -r .type)" = events.v1.JobCompleted ] || fail "expected job.completed, got $event"
printf "%s" "$event" | jq -c '.payload | {result, artifacts}' | sed 's/^/      /'
html=$(printf "%s" "$event" | jq -r .payload.result.key)
[ "$html" = "$job/1/report.html" ] || fail "result key $html"
pass "job.completed -> $REPORTS/$html"

echo "== report API"
record=$(curl -fsS "$API/internal/v1/reports/$job")
printf "%s" "$record" | jq -c '{jobId, status, attempt, sourceJobId: .spec.sourceJobId, html: .html.key, pdf: .pdf.key, completedAt}' | sed 's/^/      /'
[ "$(printf "%s" "$record" | jq -r .status)" = completed ] || fail "report status $(printf "%s" "$record" | jq -r .status)"
pass "GET /internal/v1/reports/$job -> completed"
[ "$(curl -fsS "$API/internal/v1/reports?sourceJobId=$source" | jq -r '.items[0].jobId')" = "$job" ] && pass "GET /internal/v1/reports?sourceJobId=$source lists it"

echo "== artifacts in $REPORTS"
mc "mc cp -q svc/$REPORTS/$job/1/report.html /out/report.html >/dev/null && (mc cp -q svc/$REPORTS/$job/1/report.pdf /out/report.pdf >/dev/null || true)"
[ "$(sha256sum "$OUT/report.html" | cut -d' ' -f1)" = "$(printf "%s" "$event" | jq -r .payload.result.sha256)" ] || fail "report.html sha256 mismatch"
grep -q 'Stored fixture' "$OUT/report.html" && grep -q 'data:image/png;base64' "$OUT/report.html" || fail "report.html lacks the strategy name or charts"
pass "report.html $(wc -c < "$OUT/report.html" | tr -d ' ') bytes with $(grep -o 'data:image/png;base64' "$OUT/report.html" | wc -l | tr -d ' ') rendered charts (saved to $OUT/report.html)"
if [ -s "$OUT/report.pdf" ]; then pass "report.pdf $(wc -c < "$OUT/report.pdf" | tr -d ' ') bytes ($(head -c 5 "$OUT/report.pdf"))"; fi

echo "== tampered source -> job.failed"
bad=$(uuid)
watch_events "$bad"
request "$bad" "$(printf '0%.0s' $(seq 1 64))"
event=$(terminal_event "$bad")
[ "$(printf "%s" "$event" | jq -r .type)" = events.v1.JobFailed ] || fail "expected job.failed, got $event"
pass "sha256 mismatch -> job.failed $(printf "%s" "$event" | jq -c '.payload | {error: .error.code, final}')"
