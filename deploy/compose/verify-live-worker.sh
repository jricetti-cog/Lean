#!/usr/bin/env bash
# Plan step s4.4 verification: a live deployment through the gateway's existing POST /api/v1/live runs in its own
# live-worker container (created by job-orchestrator through the Docker API), trades on PaperBrokerage with
# market-data's simulated feed, survives a container restart and an engine crash by restoring holdings from results,
# and obeys liquidate and stop (POST /api/v1/live/{id}/stop) from the API. Run from the repository root:
#   docker compose --profile infra --profile orchestrator --profile market-data --profile results --profile gateway --profile workers \
#     build job-orchestrator live-worker market-data results api-gateway
# Requires curl and jq.
set -euo pipefail
[ -f .env ] && set -a && . ./.env && set +a
export DOCKER_SOCKET_GID="${DOCKER_SOCKET_GID:-$(stat -c %g /var/run/docker.sock)}"
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}"
ORCH="http://127.0.0.1:${ORCHESTRATOR_PORT:-5081}/internal/v1"
KEY="${GATEWAY_API_KEY:-lean-dev-api-key}"
PREFIX="${COMPOSE_PROJECT_NAME:-lean}-live-"
TIMEOUT="${TIMEOUT:-300}"
msgs() { jq -Rr 'fromjson? | .Message // empty'; }
compose() { docker compose --profile infra --profile orchestrator --profile market-data --profile results --profile gateway "$@"; }
pass() { echo "PASS: $*"; }
fail() { echo "FAIL: $*"; [ -n "${container:-}" ] && docker logs --tail 40 "$container" 2>&1 | sed 's/^/   | /'; exit 1; }
gw() { curl -fsS -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' "$@"; }
job() { curl -fsS "$ORCH/jobs/$id"; }
state() { curl -sS -H "X-Api-Key: $KEY" "$GW/api/v1/live/$id/state"; }
btc() { state | jq -r '[.holdings[]? | select((.symbol.value // "") == "BTCUSD") | .q] | add // 0'; }
rmq() { compose exec -T rabbitmq rabbitmqadmin -u "${RABBITMQ_USER:-lean}" -p "${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}" -V "${RABBITMQ_VHOST:-lean}" "$@"; }
until_true() { # until_true DESCRIPTION COMMAND...
    local what=$1 deadline=$((SECONDS + TIMEOUT)); shift
    until "$@" >/dev/null 2>&1; do [ "$SECONDS" -lt "$deadline" ] || fail "timed out: $what"; sleep 3; done
}
engine_log() { docker exec "$container" sh -c "cat /work/$id/live/engine.log" 2>/dev/null; }
restarts() { docker inspect -f '{{.RestartCount}}' "$container"; }
has_holdings() { [ "$(btc)" != 0 ]; }
container_running() { [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null)" = true ]; }
algo_started_times() { engine_log | grep -c "LiveWorkerDemo: started with" || true; }

echo "== docker compose up (infra, job-orchestrator with the Docker socket, market-data feed, results, api-gateway)"
compose up -d --wait
compose ps --format 'table {{.Service}}\t{{.Status}}'
docker inspect -f '{{range .Mounts}}{{.Source}}->{{.Destination}} {{end}}' "$(compose ps -q job-orchestrator)" | grep -q docker.sock || fail "socket not mounted"
pass "job-orchestrator has the Docker socket; live deployment mode $(docker exec "$(compose ps -q job-orchestrator)" printenv Orchestrator__LiveDeployment__Mode)"

echo "== deploy through the gateway: POST /api/v1/live (PaperBrokerage, bus data queue)"
spec='{"name":"live-smoke","algorithm":{"source":"builtin","typeName":"LiveWorkerDemoAlgorithm"},"brokerage":"PaperBrokerage","dataQueueHandlers":["bus"]}'
id=$(gw -X POST -d "$spec" "$GW/api/v1/live" | jq -r .id)
echo "   job $id"
container="${PREFIX}${id}"
until_true "container $container running" container_running
docker ps --filter "name=$container" --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Labels}}'
pass "orchestrator started $container ($(docker inspect -f '{{.Config.Image}}, restart {{.HostConfig.RestartPolicy.Name}}:{{.HostConfig.RestartPolicy.MaximumRetryCount}}, network {{.HostConfig.NetworkMode}}' "$container"))"
until_true "job running" sh -c "curl -fsS $ORCH/jobs/$id | jq -e '.status == \"running\"'"
job | jq -c '{status, kind, attempt, leaseEpoch, workerId, lastHeartbeatAt}'
epoch=$(job | jq -r .leaseEpoch)
pass "job running, lease epoch $epoch held by $(job | jq -r .workerId)"

code=$(curl -sS -o /tmp/lease.body -w '%{http_code}' -H 'Content-Type: application/json' -d '{"workerId":"intruder"}' "$ORCH/jobs/$id/lease")
[ "$code" = 409 ] || fail "a second worker got a lease ($code)"
pass "second lease refused: $code $(jq -r '.detail // .title' /tmp/lease.body)"

echo "== live trading on the simulated feed"
until_true "BTCUSD holdings in results live-state" has_holdings
before=$(btc)
state | jq -c '{attempt, updatedAt, holdings: [.holdings[] | {symbol: .symbol.id, q, a, p}], cash}'
engine_log | grep -E "LiveWorkerDemo|PaperBrokerage|BusDataQueueHandler.Subscribe" | head -5 | sed 's/^/   /'
pass "algorithm bought $before BTCUSD on PaperBrokerage; results stores the live portfolio"
rmq delete queue name=verify.live.heartbeats >/dev/null 2>&1 || true
rmq declare queue name=verify.live.heartbeats durable=false >/dev/null
rmq declare binding source=lean.events destination=verify.live.heartbeats routing_key="job.live.$id.heartbeat.v1" >/dev/null
sleep 25
rmq get queue=verify.live.heartbeats ackmode=ack_requeue_false count=1000 --format=raw_json > /tmp/live-heartbeats.json
rmq delete queue name=verify.live.heartbeats >/dev/null
python3 - "$epoch" /tmp/live-heartbeats.json <<'PY' || fail "missing lease-stamped heartbeats"
import collections, json, sys
epoch, messages = int(sys.argv[1]), json.load(open(sys.argv[2]))
by = collections.Counter((m["properties"].get("app_id"), json.loads(m["payload"]).get("leaseEpoch")) for m in messages)
print("   heartbeats in 25 s by (app, leaseEpoch):", dict(by))
sys.exit(0 if by[("live-worker", epoch)] >= 2 and by[("lean-engine", epoch)] >= 2 else 1)
PY
pass "live-worker and engine heartbeats every ~10 s, stamped with lease epoch $epoch"

echo "== container restart (docker restart): the deployment comes back with its portfolio"
docker restart "$container" >/dev/null
until_true "container back" container_running
until_true "engine restarted" sh -c "docker exec $container sh -c 'grep -q \"LiveWorkerDemo: started with\" /work/$id/live/engine.log'"
docker logs "$container" 2>&1 | msgs | grep -E "restoring|fresh portfolio" | tail -2 | sed 's/^/   /'
engine_log | grep "LiveWorkerDemo: started with" | sed 's/^/   /'
engine_log | grep -q "LiveWorkerDemo: started with $before BTCUSD" || fail "restarted algorithm did not start with $before BTCUSD"
[ "$(job | jq -r .leaseEpoch)" = "$epoch" ] || fail "lease epoch changed on a same-worker restart"
pass "restored $before BTCUSD after the restart, same lease epoch $epoch, no second buy"

echo "== engine crash (SIGKILL the Launcher): Docker restarts the container, which restores again"
docker exec "$container" pkill -9 -f QuantConnect.Lean.Launcher.dll
until_true "container restarted by Docker" sh -c "[ \$(docker inspect -f '{{.RestartCount}}' $container) -ge 1 ]"
until_true "container back" container_running
until_true "engine restarted" sh -c "docker exec $container sh -c 'grep -q \"LiveWorkerDemo: started with\" /work/$id/live/engine.log'"
engine_log | grep "LiveWorkerDemo: started with" | sed 's/^/   /'
engine_log | grep -q "LiveWorkerDemo: started with $before BTCUSD" || fail "crashed algorithm did not restart with $before BTCUSD"
docker logs "$container" 2>&1 | msgs | grep -E "without a final result|restoring" | tail -2 | sed 's/^/   /'
pass "RestartCount $(restarts); restored $before BTCUSD after the crash"
sleep 15
[ "$(btc)" = "$before" ] || fail "holdings changed across restarts: $(btc)"
pass "results still report $before BTCUSD (no duplicate order)"

echo "== liquidate from the API: POST /api/v1/live/{id}/liquidate"
gw -X POST "$GW/api/v1/live/$id/liquidate" | jq -c '{id, status}'
until_true "holdings liquidated" sh -c "[ \"\$(curl -sS -H 'X-Api-Key: $KEY' $GW/api/v1/live/$id/state | jq -r '[.holdings[]? | .q] | add // 0')\" = 0 ]"
state | jq -c '{updatedAt, holdings, cash}'
container_running || fail "liquidation stopped the container"
pass "portfolio flat, deployment still running"

echo "== stop from the API: POST /api/v1/live/{id}/stop"
gw -X POST -d '{"reason":"verify-live-worker done","liquidate":true}' "$GW/api/v1/live/$id/stop" | jq -c '{id, status}'
until_true "job cancelled" sh -c "curl -fsS $ORCH/jobs/$id | jq -e '.status == \"cancelled\"'"
job | jq -c '{status, cancelReason, finishedAt, attempt}'
pass "job cancelled by the live-worker"
until_true "container exited 0" sh -c "! docker inspect $container >/dev/null 2>&1 || [ \"\$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' $container)\" = 'exited 0' ]"
docker logs "$container" 2>&1 | msgs | grep -E "stop requested|stopped \(engine exit" | tail -2 | sed 's/^/   /' || true
until_true "finished container removed" sh -c "! docker inspect $container >/dev/null 2>&1"
pass "container exited cleanly and was removed by the orchestrator sweep"
compose logs --no-log-prefix job-orchestrator 2>/dev/null | msgs | grep -E "Live deploy|live-worker container|Live deployer" | sed 's/^/   /' | tail -6
echo "ALL CHECKS PASSED"
