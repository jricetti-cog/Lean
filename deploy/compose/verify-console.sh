#!/usr/bin/env sh
# Verifies the web console (plan step s5.1) through its nginx container only, the way a browser uses it:
#   docker compose --profile infra --profile orchestrator --profile results --profile report --profile workers \
#     --profile optimizer --profile gateway --profile console up -d --build --wait
#   deploy/compose/verify-console.sh
# No API key is sent: nginx must add it for /api, while the gateway itself still rejects anonymous calls.
set -eu
[ -f .env ] && set -a && . ./.env && set +a

UI="http://127.0.0.1:${CONSOLE_PORT:-5090}"
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}"
KEY="${CONSOLE_API_KEY:-${GATEWAY_API_KEY:-lean-dev-api-key}}"
SAMPLE="${SAMPLE:-BasicTemplateAlgorithm}"
TIMEOUT="${TIMEOUT:-600}"
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; exit 1; }
ui() { method=$1; path=$2; shift 2; curl -sS -o /tmp/ui.body -D /tmp/ui.headers -w '%{http_code}' -X "$method" "$@" "$UI$path"; }
js() { python3 -c "import json,sys; d=json.load(open('/tmp/ui.body')); print($1)"; }

[ "$(ui GET /healthz)" = 200 ] || fail "console /healthz"
pass "console nginx healthy"

[ "$(ui GET /)" = 200 ] && grep -q '<div id="root">' /tmp/ui.body || fail "GET / does not serve the SPA"
[ "$(ui GET /jobs/00000000-0000-0000-0000-000000000000)" = 200 ] && grep -q '<div id="root">' /tmp/ui.body || fail "deep link fallback"
pass "/ serves the SPA, client routes fall back to index.html"

for asset in $(grep -o '/assets/[^"]*\.js' /tmp/ui.body); do
  curl -fsS "$UI$asset" -o /tmp/ui.asset || fail "asset $asset"
  if grep -q -- "$KEY" /tmp/ui.asset; then fail "API key found in bundle $asset"; fi
done
pass "bundle assets load and do not contain the API key"

[ "$(ui GET /samples/index.json)" = 200 ] || fail "samples index"
counts=$(js "'%d C#, %d Python' % (sum(s['language']=='CSharp' for s in d), sum(s['language']=='Python' for s in d))")
js "next(s for s in d if s['language']=='Python' and s['name']=='$SAMPLE')['file']" > /tmp/ui.sample || fail "no Python $SAMPLE sample"
pass "samples from Algorithm.CSharp / Algorithm.Python: $counts"

[ "$(curl -sS -o /dev/null -w '%{http_code}' "$GW/api/v1/jobs")" = 401 ] || fail "gateway accepted an anonymous call"
[ "$(ui GET '/api/v1/jobs?limit=1')" = 200 ] || fail "console /api proxy: $(cat /tmp/ui.body)"
pass "anonymous gateway call -> 401, same call through console /api -> 200 (key injected by nginx)"

[ "$(ui GET /openapi)" = 200 ] || fail "/openapi through console"
js "'/api/v1/live/{id}/stop' in d['paths'] and '/api/v1/backtests' in d['paths']" | grep -q True || fail "openapi lacks console routes"
pass "/openapi through console lists the routes the typed client uses"

curl -fsS "$UI/samples/$(cat /tmp/ui.sample)" -o /tmp/ui.main.py || fail "sample source"
[ "$(ui POST /api/v1/algorithms -F "file=@/tmp/ui.main.py;filename=main.py" -F language=Python)" = 201 ] || fail "upload: $(cat /tmp/ui.body)"
algorithm=$(cat /tmp/ui.body)
ui POST /api/v1/backtests -H 'content-type: application/json' --data "{\"name\":\"console-verify $SAMPLE\",\"algorithm\":$algorithm,\"fetchMissingData\":false,\"maxRuntimeSeconds\":600}" | grep -q "^20[12]$" \
  || fail "submit: $(cat /tmp/ui.body)"
JOB=$(js "d['id']")
pass "Python $SAMPLE uploaded and submitted as backtest $JOB"

deadline=$(( $(date +%s) + TIMEOUT ))
while :; do
  ui GET "/api/v1/jobs/$JOB" >/dev/null
  status=$(js "d['status']")
  case "$status" in completed) break ;; failed|cancelled|stale) fail "backtest $status: $(cat /tmp/ui.body)" ;; esac
  [ "$(date +%s)" -lt "$deadline" ] || fail "backtest still $status after ${TIMEOUT}s"
  sleep 3
done
pass "backtest $JOB completed"

[ "$(ui GET "/api/v1/jobs/$JOB/result")" = 200 ] || fail "result summary"
pass "statistics: $(js "', '.join(f'{k}={d[\"statistics\"][k]}' for k in ['Net Profit','Sharpe Ratio','Drawdown','Total Orders'] if k in d['statistics'])")"
for chart in "Strategy%20Equity" "Drawdown"; do
  [ "$(ui GET "/api/v1/jobs/$JOB/charts/$chart")" = 200 ] || fail "chart $chart"
  pass "chart $chart: $(js "', '.join(f'{n}({len(s.get(\"values\") or [])})' for n,s in d['series'].items())")"
done
[ "$(ui GET "/api/v1/jobs/$JOB/orders?limit=500")" = 200 ] || fail "orders"
pass "orders: $(js "len(d['items'])")"
[ "$(ui GET "/api/v1/jobs/$JOB/logs?limit=1000")" = 200 ] || fail "logs"
pass "logs: $(js "len(d['items'])") lines"

ui POST /api/v1/reports -H 'content-type: application/json' --data "{\"sourceJobId\":\"$JOB\",\"formats\":[\"html\"]}" | grep -q "^20[12]$" || fail "report: $(cat /tmp/ui.body)"
REPORT=$(js "d['id']")
while :; do
  ui GET "/api/v1/reports/$REPORT" >/dev/null
  status=$(js "d['status']")
  case "$status" in completed) break ;; failed|cancelled|stale) fail "report $status: $(cat /tmp/ui.body)" ;; esac
  [ "$(date +%s)" -lt "$deadline" ] || fail "report still $status"
  sleep 3
done
[ "$(ui GET "/api/v1/reports/$REPORT/html")" = 302 ] || fail "report html redirect"
curl -fsS -o /tmp/ui.report.html "$(sed -n 's/^[Ll]ocation: //p' /tmp/ui.headers | tr -d '\r')" && grep -qi '<html' /tmp/ui.report.html || fail "report html download"
pass "report $REPORT rendered; /api/v1/reports/$REPORT/html redirects to the presigned HTML"

OPT_SPEC='{"name":"console-verify EMA grid","backtest":{"algorithm":{"source":"builtin","language":"CSharp","typeName":"ParameterizedAlgorithm"},"fetchMissingData":false},
  "strategy":"QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy",
  "criterion":{"target":"TotalPerformance.PortfolioStatistics.TotalNetProfit","extremum":"max"},
  "parameters":[{"name":"ema-fast","min":10,"max":20,"step":10},{"name":"ema-slow","min":100,"max":200,"step":100}],
  "maximumConcurrentBacktests":4}'
[ "$(ui POST /api/v1/optimizations/estimate -H 'content-type: application/json' --data "$OPT_SPEC")" = 200 ] || fail "optimization estimate: $(cat /tmp/ui.body)"
[ "$(js "d['totalBacktests']")" = 4 ] || fail "estimate is $(js "d['totalBacktests']"), expected 4"
pass "optimization estimate through console: 4 backtests"
ui POST /api/v1/optimizations -H 'content-type: application/json' --data "$OPT_SPEC" | grep -q "^20[12]$" || fail "optimization submit: $(cat /tmp/ui.body)"
OPT=$(js "d['id']")
deadline=$(( $(date +%s) + TIMEOUT ))
while :; do
  ui GET "/api/v1/jobs/$OPT" >/dev/null
  status=$(js "d['status']")
  case "$status" in completed) break ;; failed|cancelled|stale) fail "optimization $status: $(cat /tmp/ui.body)" ;; esac
  [ "$(date +%s)" -lt "$deadline" ] || fail "optimization still $status after ${TIMEOUT}s"
  sleep 3
done
[ "$(ui GET "/api/v1/optimizations/$OPT")" = 200 ] || fail "optimization state"
pass "optimization $OPT completed: $(js "f\"{d['completed']}/{d['totalBacktests']} completed, best {d['best']['parameters']} objective {d['best']['objective']}\"")"
[ "$(ui GET "/api/v1/optimizations/$OPT/children?limit=100")" = 200 ] || fail "optimization children"
[ "$(js "len(d['items'])")" = 4 ] || fail "children: $(cat /tmp/ui.body)"
pass "results grid rows: $(js "'; '.join(f\"{c['parameters']} {c['status']} objective={c.get('objective')} sharpe={(c.get('statistics') or {}).get('Sharpe Ratio')}\" for c in d['items'])")"

[ "$(ui POST "/api/v1/live/00000000-0000-0000-0000-000000000000/stop")" = 404 ] || fail "live stop route: $(cat /tmp/ui.body)"
pass "live stop route reachable through console (unknown id -> 404)"

echo "console verification passed ($UI)"
