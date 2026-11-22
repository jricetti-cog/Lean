#!/usr/bin/env bash
# Verifies a running market-data service (plan step s3.1) against the compose stack:
#   docker compose --profile infra --profile market-data up -d --wait --build
#   bash deploy/compose/verify-market-data.sh
# Catalog / coverage / history over lean-data, a generate-synthetic fetch task end to end (outbox -> marketdata.fetch ->
# runner -> atomic publish -> job.marketdata.* events, manifest in lean-results), the real-ticker guard (the real
# equity/usa/daily/spy.zip stays byte-identical) and the live fan-out on lean.marketdata.live. Requires curl and jq.
set -euo pipefail
[ -f .env ] && set -a && . ./.env && set +a
B="http://127.0.0.1:${MARKET_DATA_PORT:-5082}"
RMQ="http://${RABBITMQ_USER:-lean}:${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}@127.0.0.1:${RABBITMQ_MANAGEMENT_PORT:-15672}/api"
VHOST="${RABBITMQ_VHOST:-lean}"
DC="docker compose --profile infra --profile market-data"
RUN="$(date +%s)"
ok() { echo "  ok    $*"; }
fail() { echo "  FAIL  $*" >&2; exit 1; }
check() { [ "$2" = "$3" ] && ok "$1 ($2)" || fail "$1: expected '$3', got '$2'"; }

echo "health:"
check "readyz" "$(curl -s "$B/readyz" | jq -r .status)" ok

echo "catalog / coverage / history (seeded repo data):"
check "catalog lists SPY daily" "$(curl -s "$B/internal/v1/data/catalog?securityType=Equity&market=usa&resolution=Daily" | jq -r '[.[] | select(.ticker=="SPY")] | length')" 1
COV=$(curl -s "$B/internal/v1/data/coverage?securityType=Equity&market=usa&ticker=SPY&resolution=Minute&start=2013-10-04&end=2013-10-14")
echo "        $COV"
check "SPY minute coverage range" "$(echo "$COV" | jq -r '.ranges[0].start + ".." + .ranges[0].end')" "2013-10-04..2013-10-11"
check "SPY minute missing" "$(echo "$COV" | jq -c .missing)" '["2013-10-14"]'
HIST=$(curl -s "$B/internal/v1/data/history?securityType=Equity&market=usa&ticker=SPY&resolution=Daily&start=2013-10-07&end=2013-10-11")
echo "$HIST" | jq -c '.[]' | sed 's/^/        /'
check "SPY daily bars" "$(echo "$HIST" | jq length)" 5
check "unknown ticker -> 404" "$(curl -s -o /dev/null -w '%{http_code}' "$B/internal/v1/data/history?securityType=Equity&market=usa&ticker=NOSUCHSYN&resolution=Daily&start=2013-10-07&end=2013-10-11")" 404
check "bad request -> 400" "$(curl -s -o /dev/null -w '%{http_code}' "$B/internal/v1/data/coverage?securityType=Stock&market=usa&ticker=SPY&resolution=Daily&start=2013-10-07&end=2013-10-11")" 400

echo "live fan-out (lean.marketdata.live):"
Q="md.verify.$RUN"
curl -sf -o /dev/null -XPUT -H 'content-type: application/json' "$RMQ/queues/$VHOST/$Q" -d '{"auto_delete":false,"durable":false,"arguments":{"x-message-ttl":60000}}'
curl -sf -o /dev/null -XPOST -H 'content-type: application/json' "$RMQ/bindings/$VHOST/e/lean.marketdata.live/q/$Q" -d '{"routing_key":"md.#"}'
sleep 3
LIVE=$(curl -s -XPOST -H 'content-type: application/json' "$RMQ/queues/$VHOST/$Q/get" -d '{"count":4,"ackmode":"ack_requeue_false","encoding":"auto"}')
curl -s -o /dev/null -XDELETE "$RMQ/queues/$VHOST/$Q"
echo "$LIVE" | jq -c '.[] | {routing_key, type: .properties.type, delivery_mode: .properties.delivery_mode, seq: .properties.headers.sequence, first: (.payload|fromjson|.points[0])}' | sed 's/^/        /'
check "live batches received" "$(echo "$LIVE" | jq '[.[] | select(.properties.type=="marketdata.v1.LiveData")] | length > 0')" true

echo "fetch task (generate-synthetic SYNVERIFY):"
wait_task() {
  local id=$1 s
  for _ in $(seq 1 60); do
    s=$(curl -s "$B/internal/v1/data/fetches/$id")
    case "$(echo "$s" | jq -r .status)" in completed|failed) echo "$s"; return;; esac
    sleep 1
  done
  echo "$s"
}
SPEC='{"mode":"generate-synthetic","securityType":"Equity","market":"usa","tickers":["SYNVERIFY"],"resolution":"Daily","startDate":"2013-10-07","endDate":"2013-10-11"}'
T=$(curl -s -XPOST "$B/internal/v1/data/fetches" -H 'content-type: application/json' -H "Idempotency-Key: verify-$RUN" -d "{\"fetch\":$SPEC}")
echo "        POST -> $T"
AGAIN=$(curl -s -XPOST "$B/internal/v1/data/fetches" -H 'content-type: application/json' -H "Idempotency-Key: verify-$RUN" -d "{\"fetch\":$SPEC}" | jq -r .id)
check "Idempotency-Key returns the same task" "$AGAIN" "$(echo "$T" | jq -r .id)"
DONE=$(wait_task "$(echo "$T" | jq -r .id)")
echo "        GET  -> $DONE"
check "task status" "$(echo "$DONE" | jq -r .status)" completed
check "synthetic daily bars via history" "$(curl -s "$B/internal/v1/data/history?securityType=Equity&market=usa&ticker=SYNVERIFY&resolution=Daily&start=2013-10-07&end=2013-10-11" | jq length)" 5
check "manifest in lean-results" "$(docker exec "$($DC ps -q minio)" sh -c "test -d /data/lean-results/$(echo "$T" | jq -r .id)/1/manifest.json && echo yes || echo no")" yes
check "staging empty after publish" "$($DC exec -T market-data sh -c 'ls -A /Lean/Data/.staging | wc -l' | tr -d ' ')" 0

echo "real-ticker guard:"
T=$(curl -s -XPOST "$B/internal/v1/data/fetches" -H 'content-type: application/json' -H "Idempotency-Key: verify-spy-$RUN" \
  -d '{"fetch":{"mode":"generate-synthetic","securityType":"Equity","market":"usa","tickers":["SPY"],"resolution":"Daily","startDate":"2013-10-07","endDate":"2013-10-11"}}')
DONE=$(wait_task "$(echo "$T" | jq -r .id)")
echo "        $(echo "$DONE" | jq -c '{status, error: .error.code, message: .error.message}')"
check "synthetic SPY refused" "$(echo "$DONE" | jq -r .status)/$(echo "$DONE" | jq -r .error.code)" failed/invalid-request
check "spy.zip on lean-data == repo Data/" "$($DC exec -T market-data sha256sum /Lean/Data/equity/usa/daily/spy.zip | cut -c1-64)" "$(sha256sum Data/equity/usa/daily/spy.zip | cut -c1-64)"

echo "marketdata database:"
$DC exec -T postgres psql -U marketdata -d marketdata -c "select status, spec->>'mode' as mode, spec->'tickers' as tickers, spec->>'resolution' as resolution, files_written from fetch_tasks order by created_at" -c "select count(*) as outbox_unsent from outbox where sent_at is null"
echo "all market-data checks passed"
