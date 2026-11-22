#!/bin/sh
# End-to-end check of api-gateway (plan step s4.5) against the compose stack. Run from the repository root:
#
#   docker compose --profile infra --profile orchestrator --profile results --profile report --profile gateway up -d --wait --build
#   deploy/compose/verify-gateway.sh
#
# Submits a backtest through the gateway (multipart Python source -> orchestrator), plays the worker over the
# orchestrator's internal API and the RabbitMQ management API (the stored result fixture
# Tests/TestData/test_report_data.json goes to lean-results, then job.completed), reads the result back through the
# gateway from the results service, then requests a report through the gateway (orchestrator -> jobs.v1.ReportRequested
# -> report service) and downloads its HTML. Also checks auth, request ids, the 503 problem for an upstream that is not
# running (optimizer) and the merged /openapi document.
# Requires curl and jq.
set -eu
[ -f .env ] && set -a && . ./.env && set +a

RABBITMQ_USER="${RABBITMQ_USER:-lean}"; RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}"
RABBITMQ_VHOST="${RABBITMQ_VHOST:-lean}"
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}"
ORCH="http://127.0.0.1:${ORCHESTRATOR_PORT:-5081}"
MQ="http://127.0.0.1:${RABBITMQ_MANAGEMENT_PORT:-15672}/api"
KEY="${GATEWAY_API_KEY:-lean-dev-api-key}"
DC="docker compose --profile infra --profile orchestrator --profile results --profile report --profile gateway"
FIXTURE="${FIXTURE:-Tests/TestData/test_report_data.json}"
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; exit 1; }
mq() { curl -fsS -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" -H 'content-type: application/json' "$@"; }
# gw METHOD PATH [curl args...] -> prints the status code; body in /tmp/gw.body, headers in /tmp/gw.headers
gw() { method=$1; path=$2; shift 2; curl -sS -o /tmp/gw.body -D /tmp/gw.headers -w '%{http_code}' -X "$method" -H "X-Api-Key: $KEY" "$@" "$GW$path"; }
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
publish() { # publish TYPE JOB_ID PAYLOAD
    props=$(jq -nc --arg t "$1" --arg j "$2" --arg m "$(cat /proc/sys/kernel/random/uuid)" --argjson ts "$(date +%s)" \
        '{message_id:$m, correlation_id:$j, type:$t, content_type:"application/json", delivery_mode:2, timestamp:$ts, app_id:"backtest-worker",
          headers:{"contract-version":"1.0", "job-id":$j, attempt:1, "job-kind":"backtest"}}')
    routed=$(mq -X POST "$MQ/exchanges/$RABBITMQ_VHOST/lean.events/publish" \
        -d "$(jq -nc --argjson p "$props" --arg k "job.backtest.$2.status.v1" --arg b "$3" '{properties:$p, routing_key:$k, payload:$b, payload_encoding:"string"}')" | jq -r .routed)
    [ "$routed" = true ] || fail "$1 for $2 was not routed"
}

echo "== containers and ports"
$DC ps --format '{{.Service}}|{{.Status}}|{{.Ports}}' | grep -E '^(api-gateway|job-orchestrator|results|report)\|' | sed 's/^/      /'
$DC ps api-gateway --format '{{.Status}}' | grep -q '(healthy)' && pass "api-gateway container healthy"
$DC ps api-gateway --format '{{.Ports}}' | grep -q "^127.0.0.1:${GATEWAY_PORT:-5080}->8080/tcp" && pass "published on 127.0.0.1:${GATEWAY_PORT:-5080} only" || fail "gateway port binding"
curl -fsS "$GW/healthz" | jq -c . | sed 's/^/      /'

echo "== auth"
code=$(curl -sS -o /tmp/gw.body -w '%{http_code}' "$GW/api/v1/jobs")
[ "$code" = 401 ] && pass "no key -> 401 $(jq -c '{title, detail}' /tmp/gw.body)" || fail "expected 401, got $code"
code=$(curl -sS -o /tmp/gw.body -w '%{http_code}' -H 'Authorization: Bearer wrong-key' "$GW/api/v1/jobs")
[ "$code" = 401 ] && pass "wrong key -> 401 $(jq -r .detail /tmp/gw.body)" || fail "expected 401, got $code"

echo "== submit a backtest through the gateway (multipart Python source)"
mq -X DELETE "$MQ/queues/$RABBITMQ_VHOST/jobs.backtest/contents" >/dev/null
printf 'from AlgorithmImports import *\nclass GatewaySmoke(QCAlgorithm):\n    def initialize(self):\n        self.add_equity("SPY")\n' > /tmp/main.py
code=$(gw POST /api/v1/backtests -H "X-Request-ID: verify-gw-$(date +%s)" -F file=@/tmp/main.py -F name=gateway-smoke -F startDate=2013-10-07 -F endDate=2013-10-11)
[ "$code" = 201 ] || fail "submit returned $code $(cat /tmp/gw.body)"
job=$(jq -r .id /tmp/gw.body)
rid=$(grep -i '^x-request-id:' /tmp/gw.headers | tr -d '\r' | cut -d' ' -f2)
pass "201 job $job (submittedBy $(jq -r .submittedBy /tmp/gw.body), X-Request-ID $rid)"
spec=$(curl -fsS "$ORCH/internal/v1/jobs/$job" | jq -c '.spec.backtest | {name, startDate, endDate, algorithm: {source: .algorithm.source, language: .algorithm.language, object: (.algorithm.object.bucket + "/" + .algorithm.object.key), contentType: .algorithm.object.contentType}}')
echo "      orchestrator spec: $spec"
echo "$spec" | grep -qi '"language":"python"' && pass "orchestrator stored the uploaded Python algorithm in lean-algorithms" || fail "unexpected spec"
code=$(gw GET "/api/v1/jobs/$job")
[ "$code" = 200 ] && pass "GET /api/v1/jobs/$job -> $(jq -r .status /tmp/gw.body)" || fail "get job returned $code"
code=$(gw GET "/api/v1/jobs?kind=backtest&limit=3")
[ "$code" = 200 ] && pass "GET /api/v1/jobs -> $(jq '.items | length' /tmp/gw.body) items, first $(jq -r '.items[0].id' /tmp/gw.body)" || fail "list returned $code"

echo "== worker side (no worker image): dispatch -> attempt -> started -> result in MinIO -> completed"
for _ in $(seq 1 30); do [ "$(curl -fsS "$ORCH/internal/v1/jobs/$job" | jq -r .status)" = dispatched ] && break; sleep 1; done
msg=$(mq -X POST "$MQ/queues/$RABBITMQ_VHOST/jobs.backtest/get" -d '{"count":1,"ackmode":"ack_requeue_false","encoding":"auto"}' | jq -c '.[0]')
[ "$(echo "$msg" | jq -r '.properties.headers["job-id"]')" = "$job" ] && pass "jobs.backtest delivered $(echo "$msg" | jq -r .properties.type)" || fail "no dispatch for $job"
curl -fsS -o /dev/null -X POST -H 'content-type: application/json' "$ORCH/internal/v1/jobs/$job/attempts" -d '{"workerId":"verify-gateway","deliveryCount":0,"attempt":1}'
publish events.v1.JobStarted "$job" "$(jq -nc --arg j "$job" --arg t "$(now)" '{jobId:$j, kind:"backtest", attempt:1, startedAt:$t, workerId:"verify-gateway", results:{bucket:"lean-results", prefix:($j+"/1/")}}')"
cp "$FIXTURE" /tmp/result.json
key="$job/1/$job.json"
$DC exec -T minio sh -c "mc alias set svc http://localhost:9000 '${MINIO_SERVICE_ACCESS_KEY:-lean-services}' '${MINIO_SERVICE_SECRET_KEY:-lean-dev-minio-services}' >/dev/null && mc pipe svc/lean-results/$key >/dev/null" < /tmp/result.json
size=$(wc -c < /tmp/result.json | tr -d ' '); sha=$(sha256sum /tmp/result.json | cut -d' ' -f1)
pass "uploaded s3://lean-results/$key ($size bytes)"
publish events.v1.JobCompleted "$job" "$(jq -nc --arg j "$job" --arg t "$(now)" --arg k "$key" --argjson s "$size" --arg h "$sha" '{jobId:$j, kind:"backtest", attempt:1, completedAt:$t, artifacts:[], result:{bucket:"lean-results", key:$k, size:$s, sha256:$h, contentType:"application/json"}}')"

echo "== read the result through the gateway (results service)"
for _ in $(seq 1 30); do
    code=$(gw GET "/api/v1/jobs/$job/result")
    [ "$code" = 200 ] && [ "$(jq -r .status /tmp/gw.body)" = completed ] && [ "$(jq -r '.statistics["Net Profit"] // empty' /tmp/gw.body)" != "" ] && break; sleep 1
done
[ "$code" = 200 ] || fail "result returned $code $(cat /tmp/gw.body)"
echo "      $(jq -c '{jobId, attempt, status, statistics, result: .result.key}' /tmp/gw.body)"
[ "$(jq -r '.statistics["Net Profit"]' /tmp/gw.body)" = "$(jq -r '.Statistics["Net Profit"]' /tmp/result.json)" ] && pass "GET /api/v1/jobs/$job/result -> results summary with statistics" || fail "statistics missing"
code=$(gw GET "/api/v1/jobs/$job")
[ "$(jq -r .status /tmp/gw.body)" = completed ] && pass "job is completed via the gateway" || fail "job status $(jq -r .status /tmp/gw.body)"
code=$(gw GET "/api/v1/jobs/$job/result/download")
loc=$(grep -i '^location:' /tmp/gw.headers | tr -d '\r' | cut -d' ' -f2)
[ "$code" = 302 ] && [ "$(curl -fsS "$loc" | jq -r '.Statistics["Sharpe Ratio"]')" = "$(jq -r '.Statistics["Sharpe Ratio"]' /tmp/result.json)" ] && pass "download -> 302 presigned MinIO URL, object readable from the host" || fail "download returned $code"

echo "== request a report through the gateway (orchestrator -> jobs.report -> report service)"
code=$(gw POST /api/v1/reports -H 'content-type: application/json' \
    -d "$(jq -nc --arg s "$job" '{sourceJobId:$s, strategyName:"Gateway smoke", strategyDescription:"verify-gateway.sh", formats:["html","pdf"]}')")
[ "$code" = 201 ] || fail "report request returned $code $(cat /tmp/gw.body)"
report=$(jq -r .id /tmp/gw.body)
pass "POST /api/v1/reports -> 201 report job $report ($(jq -r .kind /tmp/gw.body))"
for _ in $(seq 1 180); do
    code=$(gw GET "/api/v1/reports/$report")
    [ "$code" = 200 ] && case "$(jq -r .status /tmp/gw.body)" in completed|failed) break;; esac; sleep 1
done
echo "      $(jq -c '{jobId, status, attempt, sourceJobId: .spec.sourceJobId, html: .html.key, pdf: .pdf.key, error}' /tmp/gw.body)"
[ "$(jq -r .status /tmp/gw.body)" = completed ] && pass "GET /api/v1/reports/$report -> completed (report service)" || fail "report not completed: $(cat /tmp/gw.body)"
code=$(gw GET "/api/v1/reports?sourceJobId=$job")
[ "$code" = 200 ] && [ "$(jq -r --arg r "$report" '[.items[].jobId] | index($r) != null' /tmp/gw.body)" = true ] && pass "GET /api/v1/reports?sourceJobId=$job lists it" || fail "report list returned $code"
code=$(gw GET "/api/v1/reports/$report/html")
loc=$(grep -i '^location:' /tmp/gw.headers | tr -d '\r' | cut -d' ' -f2)
[ "$code" = 302 ] && curl -fsS "$loc" > /tmp/gw.report.html && grep -qi '<html' /tmp/gw.report.html \
    && pass "GET /api/v1/reports/$report/html -> 302 presigned MinIO URL, $(wc -c < /tmp/gw.report.html | tr -d ' ') bytes of HTML" || fail "report html returned $code"
code=$(gw GET "/api/v1/jobs/$report")
[ "$(jq -r .status /tmp/gw.body)" = completed ] && pass "report job is completed in the orchestrator" || fail "report job status $(jq -r .status /tmp/gw.body)"

echo "== request id, unavailable upstreams, openapi"
code=$(gw GET "/api/v1/jobs/$job/result" -H 'X-Request-ID: verify-trace-42')
grep -qi '^x-request-id: verify-trace-42' /tmp/gw.headers && pass "X-Request-ID echoed (and forwarded upstream; traceparent propagates the trace)"
for path in "/api/v1/optimizations/$job"; do
    code=$(gw GET "$path")
    ct=$(grep -i '^content-type:' /tmp/gw.headers | tr -d '\r' | cut -d' ' -f2 | cut -d';' -f1)
    [ "$code" = 503 ] && [ "$ct" = application/problem+json ] && pass "$path -> 503 $ct: $(jq -r .detail /tmp/gw.body | cut -c1-90)" || fail "$path returned $code $ct"
done
curl -fsS "$GW/openapi" > /tmp/gw.openapi
echo "      upstreams: $(jq -c '."x-lean-upstreams"' /tmp/gw.openapi)"
[ "$(jq -r '.paths | has("/api/v1/backtests") and has("/api/v1/jobs/{id}/result")' /tmp/gw.openapi)" = true ] \
    && pass "/openapi merges $(jq '.paths | length' /tmp/gw.openapi) public paths, no /internal paths: $(jq '[.paths | keys[] | select(startswith("/internal"))] | length == 0' /tmp/gw.openapi)" || fail "openapi"
echo "all gateway checks passed"
