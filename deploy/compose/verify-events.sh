#!/usr/bin/env sh
# Verifies live job events (plan step s5.2, ADR 0002) end to end on the compose stack:
#   docker compose --profile infra --profile orchestrator --profile results --profile report --profile workers \
#     --profile gateway --profile console up -d --build --wait
#   deploy/compose/verify-events.sh
# Submits a deliberately slow Python backtest (BAR_SLEEP seconds per minute bar) through the console's /api proxy,
# reads GET /api/v1/jobs/{id}/events while it runs and checks the frames the console pages consume. With LIVE=1 (default)
# it then deploys LiveWorkerDemoAlgorithm on the real live worker (needs the market-data profile too and
# DOCKER_SOCKET_GID, see verify-live-worker.sh), streams it for LIVE_SECONDS and stops it through the API.
set -eu
[ -f .env ] && set -a && . ./.env && set +a

UI="http://127.0.0.1:${CONSOLE_PORT:-5090}"
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}"
KEY="${CONSOLE_API_KEY:-${GATEWAY_API_KEY:-lean-dev-api-key}}"
BAR_SLEEP="${BAR_SLEEP:-0.02}"
TIMEOUT="${TIMEOUT:-600}"
OUT="${OUT:-/tmp/lean-events}"
mkdir -p "$OUT"
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; exit 1; }
ui() { method=$1; path=$2; shift 2; curl -sS -o "$OUT/body" -w '%{http_code}' -X "$method" "$@" "$UI$path"; }
js() { python3 -c "import json,sys; d=json.load(open('$OUT/body')); print($1)"; }

cat > "$OUT/main.py" <<PY
from AlgorithmImports import *
import time

class LiveEquityDemo(QCAlgorithm):
    def initialize(self):
        self.set_start_date(2013, 10, 7)
        self.set_end_date(2013, 10, 11)
        self.set_cash(100000)
        self.spy = self.add_equity("SPY", Resolution.MINUTE).symbol
        self.bars = 0

    def on_data(self, data):
        time.sleep($BAR_SLEEP)
        self.bars += 1
        if self.bars % 390 == 1:
            self.set_holdings(self.spy, 1 if self.portfolio[self.spy].quantity <= 0 else -0.5)
            self.debug(f"rebalanced at {self.time}")
PY

[ "$(curl -sS -o /dev/null -w '%{http_code}' "$GW/api/v1/jobs/00000000-0000-0000-0000-000000000000/events")" = 401 ] || fail "anonymous SSE accepted"
[ "$(curl -sS -o /dev/null -w '%{http_code}' -H "X-Api-Key: $KEY" "$GW/api/v1/jobs/00000000-0000-0000-0000-000000000000/events")" = 404 ] || fail "unknown job SSE is not 404"
pass "SSE: anonymous -> 401, unknown job -> 404"

[ "$(ui POST /api/v1/algorithms -F "file=@$OUT/main.py;filename=main.py" -F language=Python)" = 201 ] || fail "upload: $(cat "$OUT/body")"
algorithm=$(cat "$OUT/body")
ui POST /api/v1/backtests -H 'content-type: application/json' --data "{\"name\":\"live equity demo\",\"algorithm\":$algorithm,\"fetchMissingData\":false,\"maxRuntimeSeconds\":$TIMEOUT}" | grep -q "^20[12]$" \
  || fail "submit: $(cat "$OUT/body")"
JOB=$(js "d['id']")
pass "slow Python backtest submitted as $JOB"

# Through nginx exactly like the browser's EventSource (no key; nginx injects it), until the terminal status.
curl -sS -N --max-time "$TIMEOUT" -H 'Accept: text/event-stream' -H 'Last-Event-ID: 1:0' "$UI/api/v1/jobs/$JOB/events" -D "$OUT/sse.headers" > "$OUT/sse.txt" \
  || fail "stream did not end cleanly"
grep -qi '^content-type: text/event-stream' "$OUT/sse.headers" || fail "content type: $(cat "$OUT/sse.headers")"
grep -qi '^cache-control: no-cache' "$OUT/sse.headers" || fail "cache-control"
pass "stream ended by the gateway after the terminal status ($(wc -c < "$OUT/sse.txt") bytes)"

python3 - "$OUT/sse.txt" "$JOB" <<'PY'
import json, sys
frames, cur = [], {}
for line in open(sys.argv[1]).read().split("\n"):
    if not line:
        if "data" in cur: frames.append(cur)
        cur = {}
    elif line.startswith(":"): continue
    else:
        k, _, v = line.partition(": ")
        cur[k] = v
events = [json.loads(f["data"]) for f in frames]
names = [e["event"] for e in events]
def check(ok, msg):
    print(("PASS  " if ok else "FAIL  ") + msg)
    if not ok: sys.exit(1)
check(names[0] == "snapshot" and events[0]["body"]["id"] == sys.argv[2] and events[0].get("lastEventId") == "1:0", "first frame is the snapshot of the job, echoing Last-Event-ID")
check(all(e["jobId"] == sys.argv[2] for e in events), f"{len(events)} frames, all for this job: " + ", ".join(f"{n}={names.count(n)}" for n in dict.fromkeys(names)))
progress = [e["body"]["progress"] for e in events if e["event"] == "progress"]
check(len(progress) >= 2 and progress == sorted(progress), f"progress rises while running: {progress[:3]} ... {progress[-2:]}")
def get(o, k):  # LEAN packets mix PascalCase (packet) and camelCase (series) keys
    return next((v for kk, v in (o or {}).items() if kk.lower() == k.lower()), None) if isinstance(o, dict) else None
points, partials, times = [], 0, set()
for e in events:
    if e["type"] == "packets.v1.BacktestResult":
        partials += 1
        equity = get(get(get(get(get(e["body"], "Results"), "Charts"), "Strategy Equity"), "Series"), "Equity")
        values = get(equity, "values") or []
        if values: points.append(len(values)); times.update(v[0] for v in values)
check(len(points) >= 2, f"{partials} live BacktestResult packets, {len(points)} carried Strategy Equity points ({sum(points)} points, {len(times)} distinct times) before completion")
ids = [f["id"] for f in frames]
check(all(i.count(":") == 1 for i in ids), f"ids are <attempt>:<sequence> (last {ids[-1]})")
check(any(e["event"] == "log" and "rebalanced" in str(get(e["body"], "Message")) for e in events), f"log lines are relayed ({names.count('log')}; order frames: {names.count('order')}, LEAN backtests send order events with the result packets)")
check(names[-1] == "status" and events[-1]["type"] == "events.v1.JobCompleted", "last frame is job.completed")
traced = sum(1 for e in events if e.get("traceparent"))
check(traced > 0, f"{traced} frames keep the publisher's traceparent")
PY

[ "$(ui GET "/api/v1/jobs/$JOB")" = 200 ] && [ "$(js "d['status']")" = completed ] || fail "job not completed"
curl -sS -N --max-time 20 "$UI/api/v1/jobs/$JOB/events" > "$OUT/sse.done.txt"
[ "$(grep -c '^event: ' "$OUT/sse.done.txt")" = 1 ] && grep -q '^event: snapshot' "$OUT/sse.done.txt" || fail "terminal job stream: $(cat "$OUT/sse.done.txt")"
pass "a completed job's stream is its snapshot only"
echo "JOB=$JOB"

# Cancelling a running backtest (POST /api/v1/jobs/{id}/cancel) ends its stream with the terminal job.cancelled.
ui POST /api/v1/backtests -H 'content-type: application/json' --data "{\"name\":\"cancelled stream\",\"algorithm\":$algorithm,\"fetchMissingData\":false,\"maxRuntimeSeconds\":$TIMEOUT}" | grep -q "^20[12]$" \
  || fail "submit: $(cat "$OUT/body")"
CANCEL_JOB=$(js "d['id']")
curl -sS -N --max-time "$TIMEOUT" "$UI/api/v1/jobs/$CANCEL_JOB/events" > "$OUT/cancel.txt" &
stream=$!
deadline=$(( $(date +%s) + TIMEOUT ))
until grep -q '^event: progress' "$OUT/cancel.txt"; do
  [ "$(date +%s)" -lt "$deadline" ] || fail "cancel job never progressed"; sleep 2
done
ui POST "/api/v1/jobs/$CANCEL_JOB/cancel" | grep -q "^20[02]$" || fail "cancel: $(cat "$OUT/body")"
wait "$stream" || fail "cancelled job's stream did not end"
last=$(grep '^data: ' "$OUT/cancel.txt" | tail -1 | python3 -c "import json,sys; e=json.loads(sys.stdin.read()[6:]); print(e['event'], e['type'])")
[ "$last" = "status events.v1.JobCancelled" ] || fail "cancelled stream ended with: $last"
pass "cancelled backtest $CANCEL_JOB: stream ended by the gateway with status events.v1.JobCancelled"

[ "${LIVE:-1}" = 1 ] || exit 0
spec='{"name":"live-events","algorithm":{"source":"builtin","typeName":"LiveWorkerDemoAlgorithm"},"brokerage":"PaperBrokerage","dataQueueHandlers":["bus"]}'
ui POST /api/v1/live -H 'content-type: application/json' --data "$spec" | grep -q "^20[12]$" || fail "live deploy: $(cat "$OUT/body")"
LIVE_JOB=$(js "d['id']")
pass "live deployment $LIVE_JOB submitted (LiveWorkerDemoAlgorithm, PaperBrokerage, bus feed)"
deadline=$(( $(date +%s) + TIMEOUT ))
until [ "$(ui GET "/api/v1/jobs/$LIVE_JOB")" = 200 ] && [ "$(js "d['status']")" = running ]; do
  [ "$(date +%s)" -lt "$deadline" ] || fail "live job not running: $(cat "$OUT/body")"; sleep 3
done
pass "live job running in $(js "d.get('workerId')")"

curl -sS -N --max-time "$TIMEOUT" "$UI/api/v1/jobs/$LIVE_JOB/events" > "$OUT/live.txt" &
stream=$!
sleep "${LIVE_SECONDS:-90}"
ui POST "/api/v1/live/$LIVE_JOB/stop" -H 'content-type: application/json' --data '{"reason":"verify-events done","liquidate":true}' | grep -q "^20[02]$" || fail "stop: $(cat "$OUT/body")"
wait "$stream" || fail "live stream did not end after the stop"
pass "live stream ended by the gateway after the stop ($(wc -c < "$OUT/live.txt") bytes)"

python3 - "$OUT/live.txt" "$LIVE_JOB" <<'PY'
import json, sys
events = [json.loads(l[6:]) for l in open(sys.argv[1]) if l.startswith("data: ")]
names = [e["event"] for e in events]
def get(o, k):
    return next((v for kk, v in (o or {}).items() if kk.lower() == k.lower()), None) if isinstance(o, dict) else None
def check(ok, msg):
    print(("PASS  " if ok else "FAIL  ") + msg)
    if not ok: sys.exit(1)
check(names[0] == "snapshot" and all(e["jobId"] == sys.argv[2] for e in events), f"{len(events)} live frames: " + ", ".join(f"{n}={names.count(n)}" for n in dict.fromkeys(names)))
live = [e for e in events if e["type"] == "packets.v1.LiveResult"]
points = sum(len(get(get(get(get(get(get(e["body"], "Results"), "Charts"), "Strategy Equity"), "Series"), "Equity"), "values") or []) for e in live)
check(len(live) >= 2 and points > 0, f"{len(live)} LiveResult packets carried {points} Strategy Equity points")
check(any(e["event"] == "log" and "LiveWorkerDemo" in str(get(e["body"], "Message")) for e in events), "LiveWorkerDemo log lines are relayed")
orders = [e for e in events if e["event"] == "order"]
check(len(orders) > 0, f"{len(orders)} order frames (packets.v1.OrderEvent) from PaperBrokerage")
check(names[-1] == "status" and events[-1]["type"] in ("events.v1.JobCompleted", "events.v1.JobCancelled", "events.v1.JobFailed"), f"last frame is {events[-1]['type']}")
PY
echo "LIVE_JOB=$LIVE_JOB"
