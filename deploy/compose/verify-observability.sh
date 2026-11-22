#!/usr/bin/env bash
# Plan step s5.3 verification: the observability profile (OpenTelemetry Collector, Prometheus, Grafana, Tempo, Loki,
# postgres-exporter) next to the whole platform. Submits one backtest through api-gateway and checks that a single
# trace spans gateway -> orchestrator -> worker -> results, that metrics and logs of every service reach Prometheus
# and Loki, and that every panel query of the provisioned Grafana dashboards returns data.
# Run from the repo root: deploy/compose/verify-observability.sh   (KEEP=1 leaves the stack running)
set -euo pipefail
[ -f .env ] && { set -a; . ./.env; set +a; }
DC="docker compose --profile infra --profile orchestrator --profile market-data --profile results --profile report --profile workers --profile gateway --profile observability"
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}"; KEY="${GATEWAY_API_KEY:-lean-dev-api-key}"
PROM="http://127.0.0.1:${PROMETHEUS_PORT:-9090}"; TEMPO="http://127.0.0.1:${TEMPO_PORT:-3200}"
LOKI="http://127.0.0.1:${LOKI_PORT:-3100}"; GRAFANA="http://127.0.0.1:${GRAFANA_PORT:-3000}"
GF_AUTH="${GRAFANA_ADMIN_USER:-admin}:${GRAFANA_ADMIN_PASSWORD:-lean-dev-grafana}"
SERVICES="api-gateway job-orchestrator backtest-worker results"
pass() { echo "  PASS $*"; }
fail() { echo "  FAIL $*"; exit 1; }
prom() { curl -fsS "$PROM/api/v1/query" --data-urlencode "query=$1" | jq -r '.data.result | length'; }
retry() { local n=$1; shift; for _ in $(seq 1 "$n"); do "$@" && return 0; sleep 2; done; return 1; }

echo "== compose up (all platform profiles + observability)"
$DC up -d --wait --scale backtest-worker=2 2>&1 | grep -E 'Healthy|Started|Running' | sort -u | tail -n 40
$DC ps --format 'table {{.Service}}\t{{.Status}}' otel-collector prometheus grafana tempo loki postgres-exporter

echo "== backends ready, provisioned from deploy/compose/observability"
curl -fsS "$PROM/-/ready" >/dev/null && pass "prometheus ready"
retry 30 curl -fsS -o /dev/null "$TEMPO/ready" && pass "tempo ready"
retry 30 curl -fsS -o /dev/null "$LOKI/ready" && pass "loki ready"
[ "$(curl -fsS "$GRAFANA/api/health" | jq -r .database)" = ok ] && pass "grafana healthy"
for ds in prometheus loki; do
  [ "$(curl -fsS -u "$GF_AUTH" "$GRAFANA/api/datasources/uid/$ds/health" | jq -r .status)" = OK ] && pass "grafana datasource $ds -> OK" || fail "datasource $ds"
done
curl -fsS -o /dev/null -u "$GF_AUTH" "$GRAFANA/api/datasources/proxy/uid/tempo/ready" && pass "grafana datasource tempo -> proxied /ready OK" || fail "datasource tempo"
dashboards=$(curl -fsS -u "$GF_AUTH" "$GRAFANA/api/search?type=dash-db" | jq -r '[.[].uid] | sort | join(" ")')
[ "$dashboards" = "lean-platform-overview lean-postgresql lean-rabbitmq lean-service-red" ] && pass "dashboards: $dashboards" || fail "dashboards: $dashboards"

echo "== submit a backtest through the gateway"
printf 'from AlgorithmImports import *\nclass ObservabilitySmoke(QCAlgorithm):\n    def initialize(self):\n        self.set_start_date(2013, 10, 7)\n        self.set_end_date(2013, 10, 11)\n        self.add_equity("SPY")\n    def on_data(self, data):\n        if not self.portfolio.invested:\n            self.set_holdings("SPY", 1)\n' > /tmp/obs-main.py
code=$(curl -sS -o /tmp/obs.body -D /tmp/obs.headers -w '%{http_code}' -X POST -H "X-Api-Key: $KEY" \
  -F file=@/tmp/obs-main.py -F name=observability-smoke -F startDate=2013-10-07 -F endDate=2013-10-11 "$GW/api/v1/backtests")
[ "$code" = 201 ] || fail "submit returned $code $(cat /tmp/obs.body)"
job=$(jq -r .id /tmp/obs.body)
trace=$(grep -i '^x-request-id:' /tmp/obs.headers | tr -d '\r' | cut -d' ' -f2)
pass "POST /api/v1/backtests -> 201 job $job, X-Request-ID (trace id) $trace"
status=""
for _ in $(seq 1 180); do
  status=$(curl -fsS -H "X-Api-Key: $KEY" "$GW/api/v1/jobs/$job" | jq -r .status)
  case "$status" in completed|failed|cancelled) break;; esac; sleep 2
done
[ "$status" = completed ] && pass "job $job -> completed" || fail "job ended $status"
curl -fsS -o /dev/null -H "X-Api-Key: $KEY" "$GW/api/v1/jobs/$job/result" && pass "GET /api/v1/jobs/$job/result -> 200"

echo "== one trace in Tempo: gateway -> orchestrator -> worker -> results"
tree() {
  curl -fsS "$TEMPO/api/traces/$trace" > /tmp/obs-trace.json 2>/dev/null || return 1
  jq -e --arg s "$SERVICES" '[.batches[] | .resource.attributes[] | select(.key=="service.name") | .value.stringValue] | unique as $have
    | ($s | split(" ")) - $have | length == 0' /tmp/obs-trace.json >/dev/null &&
  jq -e '[.batches[] | select(any(.resource.attributes[]; .value.stringValue=="results")) | .scopeSpans[].spans[].name] | any(test("results"))' /tmp/obs-trace.json >/dev/null
}
retry 45 tree || fail "trace $trace incomplete: $(jq -c '[.batches[]?.resource.attributes[]|select(.key=="service.name")|.value.stringValue]|unique' /tmp/obs-trace.json 2>/dev/null)"
jq -r '[.batches[] | (.resource.attributes[] | select(.key=="service.name") | .value.stringValue) as $svc | .scopeSpans[].spans[] | {svc:$svc, name, start:(.startTimeUnixNano|tonumber), kind}]
  | ((length|tostring) + " spans: " + ([.[].svc] | group_by(.) | map(.[0] + "=" + (length|tostring)) | join(" "))),
    (group_by(.svc + .name) | map(min_by(.start)) | sort_by(.start) | .[] | "    " + .svc + ": " + .name + " (" + .kind + ")")' /tmp/obs-trace.json | head -n 40
ids=$(jq -r '[.batches[].scopeSpans[].spans[] | .traceId] | unique | length' /tmp/obs-trace.json)
[ "$ids" = 1 ] && pass "all spans share trace id $trace across $(echo $SERVICES | tr ' ' ',')"

echo "== metrics in Prometheus"
retry 30 sh -c "[ \"\$(curl -fsS '$PROM/api/v1/query' --data-urlencode 'query=sum by (job) (rate(lean_worker_job_duration_seconds_count[5m])) > 0' | jq '.data.result|length')\" -gt 0 ]" || true
curl -fsS "$PROM/api/v1/targets" | jq -r '.data.activeTargets[] | "    target \(.labels.job) \(.scrapeUrl) \(.health)"'
[ "$(curl -fsS "$PROM/api/v1/targets" | jq '[.data.activeTargets[] | select(.health!="up")] | length')" = 0 ] && pass "every scrape target up"
for svc in api-gateway job-orchestrator market-data backtest-worker results report; do
  [ "$(prom "target_info{job=\"$svc\"}")" -gt 0 ] && pass "OTLP metrics from $svc" || fail "no OTLP metrics from $svc"
done
for q in 'lean_jobs' 'lean_queue_ready' 'sum(lean_worker_busy)' 'lean_worker_job_duration_seconds_count' \
         'http_server_request_duration_seconds_count{job="api-gateway"}' 'lean_messaging_process_duration_seconds_count{job="results"}' \
         'rabbitmq_identity_info' 'pg_up == 1'; do
  n=$(prom "$q"); [ "$n" -gt 0 ] && pass "$q -> $n series" || fail "$q empty"
done

echo "== logs in Loki"
since=$(( $(date +%s) - 3600 ))
for svc in $SERVICES; do
  n=$(curl -fsS -G "$LOKI/loki/api/v1/query_range" --data-urlencode "query={service_name=\"$svc\"}" --data-urlencode "start=${since}000000000" --data-urlencode limit=1000 | jq '[.data.result[].values[]] | length')
  [ "$n" -gt 0 ] && pass "loki {service_name=\"$svc\"} -> $n lines" || fail "no logs from $svc"
done
n=$(curl -fsS -G "$LOKI/loki/api/v1/query_range" --data-urlencode "query={service_name=~\".+\"} | trace_id=\"$trace\"" --data-urlencode "start=${since}000000000" --data-urlencode limit=1000 | jq '[.data.result[].values[]] | length')
[ "$n" -gt 0 ] && pass "loki log lines correlated to trace $trace -> $n" || fail "no logs carry trace $trace"

echo "== every Grafana panel query returns data"
empty=0
for f in deploy/compose/observability/grafana/dashboards/*.json; do
  total=0; ok=0
  while IFS= read -r expr; do
    total=$((total+1))
    q=$(printf '%s' "$expr" | sed -e 's/\$__rate_interval/2m/g' -e 's/\$__range/1h/g' -e 's/\$service/.+/g')
    if [ "$(prom "$q")" -gt 0 ]; then ok=$((ok+1)); else echo "    no data: $q"; empty=$((empty+1)); fi
  done < <(jq -r '.panels[] | select(.type!="row") | .targets[]?.expr | select(. != null)' "$f")
  echo "    $(basename "$f"): $ok/$total queries with data"
done
[ "$empty" = 0 ] && pass "all dashboard queries return data" || fail "$empty dashboard queries without data"

echo "== monolith unaffected"
git diff --quiet origin/master -- Launcher/config.json && pass "Launcher/config.json unchanged"
[ "${KEEP:-0}" = 1 ] || $DC down >/dev/null 2>&1
echo "ALL CHECKS PASSED"
