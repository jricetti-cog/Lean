#!/bin/sh
# Platform smoke test (plan step s6.3), the same for Docker Compose and the Helm chart: drives a running platform only
# through its public endpoints (api-gateway, console, MinIO presigned URLs), with real workers doing the work.
#   1. gateway health, auth (401 without/with a wrong key), merged OpenAPI
#   2. console SPA served and its /api proxy reaches the gateway
#   3. a Python backtest submitted through the gateway runs on a backtest-worker and completes with statistics
#   4. the result downloads through a presigned MinIO URL
#   5. a report on that backtest completes in the report service and its HTML downloads
#   6. (SMOKE_LIVE=1) a PaperBrokerage live deployment runs and stops through the gateway
# `make smoke` runs it against Compose after deploy/compose/smoke-backtest.sh; `make kind-smoke` against kind.
# Requires curl and jq.
set -eu

GW="${LEAN_GATEWAY_URL:-http://127.0.0.1:${GATEWAY_PORT:-5080}}"
CONSOLE="${LEAN_CONSOLE_URL:-http://127.0.0.1:${CONSOLE_PORT:-5090}}"
KEY="${GATEWAY_API_KEY:-lean-dev-api-key}"
TIMEOUT="${SMOKE_TIMEOUT:-300}"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

pass() { echo "   PASS $*"; }
fail() { echo "   FAIL $*" >&2; exit 1; }
# gw METHOD PATH [curl args...] -> prints the status code; body in $tmp/body, headers in $tmp/headers
gw() { method=$1; path=$2; shift 2; curl -sS -o "$tmp/body" -D "$tmp/headers" -w '%{http_code}' -X "$method" -H "X-Api-Key: $KEY" "$@" "$GW$path"; }
location() { grep -i '^location:' "$tmp/headers" | tr -d '\r' | cut -d' ' -f2; }
# wait_status PATH STATUS... -> polls GET PATH until .status is one of the arguments (or the timeout)
wait_status() {
    path=$1; shift
    deadline=$(( $(date +%s) + TIMEOUT ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        if [ "$(gw GET "$path")" = 200 ]; then
            status=$(jq -r .status "$tmp/body")
            for want in "$@"; do [ "$status" = "$want" ] && return 0; done
            case "$status" in failed|cancelled) return 1;; esac
        fi
        sleep 2
    done
    return 1
}

echo "== smoke: gateway $GW, console $CONSOLE"
curl -fsS "$GW/healthz" >/dev/null && pass "gateway /healthz" || fail "gateway not reachable at $GW"
curl -fsS "$GW/readyz" >/dev/null && pass "gateway /readyz (upstreams reachable)" || fail "gateway not ready: $(curl -sS "$GW/readyz")"
code=$(curl -sS -o /dev/null -w '%{http_code}' "$GW/api/v1/jobs"); [ "$code" = 401 ] && pass "no API key -> 401" || fail "expected 401, got $code"
code=$(curl -sS -o /dev/null -w '%{http_code}' -H 'X-Api-Key: wrong' "$GW/api/v1/jobs"); [ "$code" = 401 ] && pass "wrong API key -> 401" || fail "expected 401, got $code"
paths=$(curl -fsS "$GW/openapi" | jq '.paths | length')
[ "$paths" -gt 10 ] && pass "merged OpenAPI document ($paths paths)" || fail "OpenAPI has $paths paths"

echo "== console"
curl -fsS "$CONSOLE/" | grep -qi '<div id="root"' && pass "console serves the SPA" || fail "console index not served at $CONSOLE"
code=$(curl -sS -o /dev/null -w '%{http_code}' "$CONSOLE/api/v1/jobs?limit=1"); [ "$code" = 200 ] && pass "console /api proxy -> gateway 200" || fail "console proxy returned $code"

echo "== backtest through the gateway (Python source, run by a backtest-worker)"
cat > "$tmp/main.py" <<'PY'
from AlgorithmImports import *

class PlatformSmoke(QCAlgorithm):
    def initialize(self):
        self.set_start_date(2013, 10, 7)
        self.set_end_date(2013, 10, 11)
        self.set_cash(100000)
        self.add_equity("SPY", Resolution.MINUTE)

    def on_data(self, data):
        if not self.portfolio.invested:
            self.set_holdings("SPY", 1)
PY
started=$(date +%s)
code=$(gw POST /api/v1/backtests -F file=@"$tmp/main.py" -F name=platform-smoke -F startDate=2013-10-07 -F endDate=2013-10-11)
[ "$code" = 201 ] || fail "submit returned $code $(cat "$tmp/body")"
job=$(jq -r .id "$tmp/body")
pass "POST /api/v1/backtests -> 201 job $job"
wait_status "/api/v1/jobs/$job" completed || fail "backtest did not complete: $(cat "$tmp/body")"
pass "backtest completed in $(( $(date +%s) - started ))s on $(jq -r '.workerId // "a worker"' "$tmp/body")"
code=$(gw GET "/api/v1/jobs/$job/result")
[ "$code" = 200 ] || fail "result returned $code"
echo "      $(jq -c '{status, statistics: (.statistics | {"Total Orders", "Net Profit", "Sharpe Ratio", "End Equity"})}' "$tmp/body")"
[ "$(jq -r '.statistics["Total Orders"]' "$tmp/body")" = 1 ] && pass "result statistics: 1 order" || fail "unexpected statistics $(jq -c .statistics "$tmp/body")"
code=$(gw GET "/api/v1/jobs/$job/result/download")
[ "$code" = 302 ] && curl -fsS "$(location)" | jq -e '(.statistics // .Statistics)["Total Orders"] == "1"' >/dev/null \
    && pass "result download -> 302 presigned MinIO URL, readable" || fail "result download returned $code"

echo "== report on the backtest (report service)"
code=$(gw POST /api/v1/reports -H 'content-type: application/json' \
    -d "$(jq -nc --arg s "$job" '{sourceJobId:$s, strategyName:"Platform smoke", strategyDescription:"deploy/smoke/smoke.sh", formats:["html"]}')")
[ "$code" = 201 ] || fail "report request returned $code $(cat "$tmp/body")"
report=$(jq -r .id "$tmp/body")
wait_status "/api/v1/reports/$report" completed || fail "report did not complete: $(cat "$tmp/body")"
pass "report $report completed"
code=$(gw GET "/api/v1/reports/$report/html")
[ "$code" = 302 ] && curl -fsS "$(location)" | grep -qi '<html' && pass "report HTML downloads (presigned MinIO URL)" || fail "report html returned $code"

if [ "${SMOKE_LIVE:-0}" = 1 ]; then
    echo "== live deployment (PaperBrokerage, bus data queue)"
    code=$(gw POST /api/v1/live -H 'content-type: application/json' \
        -d '{"name":"platform-smoke-live","algorithm":{"source":"builtin","typeName":"LiveWorkerDemoAlgorithm"},"brokerage":"PaperBrokerage","dataQueueHandlers":["bus"]}')
    [ "$code" = 201 ] || fail "live submit returned $code $(cat "$tmp/body")"
    live=$(jq -r .id "$tmp/body")
    wait_status "/api/v1/jobs/$live" running || fail "live deployment not running: $(cat "$tmp/body")"
    pass "live deployment $live running on $(jq -r .workerId "$tmp/body")"
    [ -z "${SMOKE_LIVE_HOOK:-}" ] || sh -c "$SMOKE_LIVE_HOOK running $live"
    code=$(gw POST "/api/v1/live/$live/stop" -H 'content-type: application/json' -d '{"reason":"smoke done","liquidate":true}')
    case "$code" in 200|202) ;; *) fail "stop returned $code";; esac
    wait_status "/api/v1/jobs/$live" cancelled completed || fail "live deployment did not stop: $(cat "$tmp/body")"
    pass "live deployment stopped ($(jq -r .status "$tmp/body"))"
    [ -z "${SMOKE_LIVE_HOOK:-}" ] || sh -c "$SMOKE_LIVE_HOOK stopped $live"
fi

echo "== smoke passed"
