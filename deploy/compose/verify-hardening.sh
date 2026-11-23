#!/usr/bin/env bash
# Plan step s4.7 verification: fault and load tests for the engine path, against the compose stack with five
# backtest-worker replicas. Every scenario asserts the end state of the jobs through job-orchestrator and RabbitMQ:
#   1. compose limits: every long-running container has a CPU/memory limit and a restart policy (live-workers too)
#   2. worker kill:    `docker kill` the worker running a backtest -> redelivered, a new attempt completes exactly once
#                      (one job.completed event, one result); a second running job is cancelled while the worker is down
#   3. broker restart: RabbitMQ killed for ~20 s with jobs running and jobs submitted/cancelled during the outage ->
#                      the outbox keeps the publishes until the broker confirms them, every job ends as expected
#   4. poison job:     an algorithm that SIGKILLs its backtest-worker -> each delivery crashes a worker (Docker restarts
#                      it), the attempt limit fails the job with max-attempts-exceeded and the dispatch is dead-lettered
#   5. limits:         RSS over maxRamMb and run time over maxRuntimeSeconds -> engine stopped, job failed with the reason
#   6. live-worker kill: SIGKILL a live deployment's main process -> restarted, holdings restored, no second buy
#   7. load:           50 queued backtests across 5 workers, throughput recorded
# Run from the repository root after building the images:
#   docker compose --profile infra --profile orchestrator --profile workers --profile market-data --profile results --profile gateway build
#   deploy/compose/verify-hardening.sh
# Requires curl, jq and python3.
set -euo pipefail
[ -f .env ] && set -a && . ./.env && set +a
export DOCKER_SOCKET_GID="${DOCKER_SOCKET_GID:-$(stat -c %g /var/run/docker.sock)}"
ORCH="http://127.0.0.1:${ORCHESTRATOR_PORT:-5081}/internal/v1"
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}/api/v1"
KEY="${GATEWAY_API_KEY:-lean-dev-api-key}"
WORKERS="${WORKERS:-5}"
LOAD_JOBS="${LOAD_JOBS:-50}"
TIMEOUT="${TIMEOUT:-900}"
SLOW_SLEEP="${SLOW_SLEEP:-0.05}"
# Short heartbeats and timeout so a dead attempt or a dead live-worker is noticed within ~30 s (defaults 30 s / 90 s).
export BACKTEST_WORKER_HEARTBEAT_SECONDS="${BACKTEST_WORKER_HEARTBEAT_SECONDS:-5}"
export ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS="${ORCHESTRATOR_HEARTBEAT_TIMEOUT_SECONDS:-30}"
PROFILES=(--profile infra --profile orchestrator --profile workers --profile market-data --profile results --profile gateway)
compose() { docker compose "${PROFILES[@]}" "$@"; }
rmq() { compose exec -T rabbitmq rabbitmqadmin -u "${RABBITMQ_USER:-lean}" -p "${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}" -V "${RABBITMQ_VHOST:-lean}" "$@"; }
sql() { compose exec -T postgres psql -U "${POSTGRES_USER:-postgres}" -d jobs -tAc "$1"; }
pass() { echo "PASS: $*"; RESULTS+=("PASS  $*"); }
fail() { echo "FAIL: $*"; exit 1; }
RESULTS=()
work=$(mktemp -d)

job() { curl -fsS "${ORCH}/jobs/$1"; }
status() { job "$1" | jq -r .status; }
upload() { curl -fsS -X POST -H 'Content-Type: text/x-python' --data-binary "@$1" "${ORCH}/algorithms?fileName=$(basename "$1")"; }
submit() { curl -fsS -X POST -H 'Content-Type: application/json' -d "$1" "${ORCH}/jobs/" | jq -r .id; }
cancel() { curl -fsS -X POST -H 'Content-Type: application/json' -d "{\"reason\":\"$2\"}" "${ORCH}/jobs/$1/cancel" >/dev/null; }
builtin() { jq -nc --arg n "$1" --arg t "$2" '{kind:"backtest",backtest:{name:$n,algorithm:{source:"builtin",typeName:$t},maxRamMb:2048}}'; }
uploaded() { # uploaded NAME OBJECT [MAX_RAM_MB] [MAX_RUNTIME_SECONDS]
    jq -nc --arg n "$1" --argjson o "$2" --argjson ram "${3:-2048}" --argjson rt "${4:-1800}" \
        '{kind:"backtest",backtest:{name:$n,algorithm:{source:"upload",language:"python",object:$o},maxRamMb:$ram,maxRuntimeSeconds:$rt}}'
}
wait_status() { # wait_status JOB STATUS...: until the job is in one of the statuses
    local id=$1 deadline=$((SECONDS + TIMEOUT)) s; shift
    while :; do
        s=$(status "$id")
        for want in "$@"; do [ "$s" = "$want" ] && return 0; done
        case "$s" in completed|failed|cancelled) fail "job $id is $s, expected $*: $(job "$id" | jq -c '{attempt, error, cancelReason}')" ;; esac
        [ "$SECONDS" -lt "$deadline" ] || fail "job $id still $s after ${TIMEOUT}s (expected $*)"
        sleep 2
    done
}
until_true() { # until_true DESCRIPTION COMMAND...
    local what=$1 deadline=$((SECONDS + TIMEOUT)); shift
    until "$@" >/dev/null 2>&1; do [ "$SECONDS" -lt "$deadline" ] || fail "timed out: $what"; sleep 2; done
}
worker_container() { # the backtest-worker container whose hostname (= WorkerId) is $1
    local c; for c in $(compose ps -a -q backtest-worker); do
        [ "$(docker inspect -f '{{.Config.Hostname}}' "$c")" = "$1" ] && { echo "$c"; return; }
    done
}
active_jobs() { sql "SELECT count(*) FROM jobs WHERE status IN ('queued','dispatched','running','stale') AND kind <> 'live'"; }
idle() { # cancel leftovers of earlier runs so every scenario starts on idle workers
    for id in $(sql "SELECT id FROM jobs WHERE status IN ('queued','dispatched','running','stale') AND kind <> 'live'"); do
        cancel "$id" "verify-hardening: leftover from an earlier run" || true
    done
    until_true "no active backtest jobs" sh -c "[ \"\$(docker compose ${PROFILES[*]} exec -T postgres psql -U ${POSTGRES_USER:-postgres} -d jobs -tAc \"SELECT count(*) FROM jobs WHERE status IN ('queued','dispatched','running','stale') AND kind <> 'live'\")\" = 0 ]"
}
workers_ready() { [ "$(compose ps backtest-worker --status running --format '{{.Health}}' | grep -c '^healthy$')" -eq "$WORKERS" ]; }
worker_restarts() { compose ps -a -q backtest-worker | xargs docker inspect -f '{{.RestartCount}}' | paste -sd+ | bc; }
tap() { # tap QUEUE ROUTING_KEY: temporary queue on lean.events
    rmq delete queue name="$1" >/dev/null 2>&1 || true
    rmq declare queue name="$1" durable=false >/dev/null
    rmq declare binding source=lean.events destination="$1" routing_key="$2" >/dev/null
}
drain() { rmq get queue="$1" ackmode=ack_requeue_false count=100000 --format=raw_json; rmq delete queue name="$1" >/dev/null; }
events_of() { # events_of FILE JOB: "<message type> <attempt> <app>" per tapped message of the job
    python3 - "$1" "$2" <<'PY'
import json, sys
for m in json.load(open(sys.argv[1])):
    b = json.loads(m["payload"])
    if b.get("jobId") == sys.argv[2]:
        print(m["properties"].get("type", "").split(".")[-1], b.get("attempt"), m["properties"].get("app_id"))
PY
}

cat > "${work}/SlowAlgorithm.py" <<PY
from AlgorithmImports import *
import time as wall_clock

class SlowAlgorithm(QCAlgorithm):
    def initialize(self):
        self.set_start_date(2013, 10, 7)
        self.set_end_date(2013, 10, 11)
        self.spy = self.add_equity("SPY", Resolution.MINUTE).symbol

    def on_data(self, data):
        if not self.portfolio.invested:
            self.set_holdings(self.spy, 1)
        wall_clock.sleep(${SLOW_SLEEP})
PY
cat > "${work}/MemoryHogAlgorithm.py" <<'PY'
from AlgorithmImports import *

class MemoryHogAlgorithm(QCAlgorithm):
    def initialize(self):
        self.set_start_date(2013, 10, 7)
        self.set_end_date(2013, 10, 11)
        self.add_equity("SPY", Resolution.MINUTE)
        self.hog = []

    def on_data(self, data):
        self.hog.append(bytearray(b"\x01") * (64 * 1024 * 1024))
PY
cat > "${work}/PoisonAlgorithm.py" <<'PY'
import os, signal
from AlgorithmImports import *

class PoisonAlgorithm(QCAlgorithm):
    """Takes its backtest-worker down: SIGKILLs the worker process that runs this engine."""
    def initialize(self):
        self.set_start_date(2013, 10, 7)
        self.set_end_date(2013, 10, 11)
        self.add_equity("SPY", Resolution.MINUTE)
        for pid in os.listdir("/proc"):
            try:
                if pid.isdigit() and b"QuantConnect.Services.BacktestWorker.dll" in open(f"/proc/{pid}/cmdline", "rb").read():
                    os.kill(int(pid), signal.SIGKILL)
            except OSError:
                pass
PY

echo "== docker compose up (infra, job-orchestrator, ${WORKERS} backtest-workers, market-data, results, api-gateway)"
compose up -d --wait --scale "backtest-worker=${WORKERS}"
compose ps --format 'table {{.Service}}\t{{.Name}}\t{{.Status}}'
until_true "${WORKERS} healthy workers" workers_ready
idle
slow=$(upload "${work}/SlowAlgorithm.py")
hog=$(upload "${work}/MemoryHogAlgorithm.py")
poison=$(upload "${work}/PoisonAlgorithm.py")

echo
echo "== 1. container CPU/memory limits and restart policies"
printf '   %-38s %-6s %-8s %s\n' CONTAINER CPUS MEMORY RESTART
missing=0
for c in $(compose ps -q); do
    read -r name nano mem policy < <(docker inspect -f '{{.Name}} {{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.RestartPolicy.Name}}' "$c")
    printf '   %-38s %-6s %-8s %s\n' "${name#/}" "$(echo "scale=2; $nano / 1000000000" | bc)" "$((mem / 1048576))M" "$policy"
    [ "$nano" -gt 0 ] && [ "$mem" -gt 0 ] && [ "$policy" = unless-stopped ] || missing=$((missing + 1))
done
[ "$missing" -eq 0 ] || fail "$missing running containers without a CPU/memory limit or restart policy"
pass "every compose container has a CPU and memory limit and restart: unless-stopped"

echo
echo "== 2. kill a worker mid-backtest (and cancel another running job while it is down)"
tap verify.hardening.kill "job.backtest.#"
victim=$(submit "$(uploaded hardening-worker-kill "$slow")")
other=$(submit "$(uploaded hardening-cancel-during-kill "$slow")")
wait_status "$victim" running
wait_status "$other" running
sleep 10
[ "$(status "$victim")" = running ] || fail "job $victim finished before the worker kill; the slow algorithm is too short for this host"
holder=$(job "$victim" | jq -r .workerId)
container=$(worker_container "$holder")
[ -n "$container" ] || fail "no container for worker $holder"
echo "   job $victim attempt $(job "$victim" | jq -r .attempt) running on $holder; docker kill $(docker inspect -f '{{.Name}}' "$container")"
docker kill "$container" >/dev/null
cancel "$other" "cancelled during the worker-kill chaos run"
wait_status "$other" cancelled
pass "job $other cancelled while a worker was being killed (attempt $(job "$other" | jq -r .attempt), finished $(job "$other" | jq -r .finishedAt))"
wait_status "$victim" completed
job "$victim" | jq -c '{status, attempt, workerId, result: .result.key}'
attempt=$(job "$victim" | jq -r .attempt)
[ "$attempt" -ge 2 ] || fail "the killed attempt was not retried (attempt $attempt)"
[ "$(job "$victim" | jq -r .workerId)" != "$holder" ] || fail "the retry ran on the killed worker"
drain verify.hardening.kill > "${work}/kill-events.json"
events_of "${work}/kill-events.json" "$victim" | sort | uniq -c | sed 's/^/   events /'
completions=$(events_of "${work}/kill-events.json" "$victim" | grep -c '^JobCompleted ' || true)
[ "$completions" -eq 1 ] || fail "job.completed published $completions times"
events_of "${work}/kill-events.json" "$victim" | grep '^JobCompleted ' | grep -q " $attempt " || fail "job.completed is not from attempt $attempt"
result_keys=$(sql "SELECT count(DISTINCT result) FROM jobs WHERE id = '$victim' AND result IS NOT NULL")
[ "$result_keys" = 1 ] || fail "$result_keys results recorded"
pass "killed attempt retried on another worker; completed once (1 job.completed, from attempt $attempt; 1 result)"
docker start "$container" >/dev/null
until_true "${WORKERS} healthy workers" workers_ready

echo
echo "== 3. kill RabbitMQ for ~20 s: jobs running, submitted and cancelled during the outage"
running=(); for i in 1 2 3; do running+=("$(submit "$(uploaded "hardening-broker-running-$i" "$slow")")"); done
for id in "${running[@]}"; do wait_status "$id" running; done
docker kill "$(compose ps -q rabbitmq)" >/dev/null
echo "   rabbitmq killed with ${#running[@]} jobs running"
during=(); for i in 1 2 3 4; do during+=("$(submit "$(builtin "hardening-broker-outage-$i" BasicTemplateAlgorithm)")"); done
cancel "${running[0]}" "cancelled while the broker was down"
sleep 5
unsent=$(sql "SELECT count(*) FROM outbox WHERE sent_at IS NULL")
failed_publishes=$(sql "SELECT COALESCE(sum(publish_attempts), 0) FROM outbox WHERE sent_at IS NULL")
echo "   during the outage: ${#during[@]} jobs submitted (HTTP 2xx), 1 cancel accepted; outbox holds $unsent unsent messages ($failed_publishes failed publish attempts)"
[ "$unsent" -ge 5 ] || fail "expected the outage's dispatches and cancel to wait in the outbox, $unsent unsent"
sleep 15
compose start rabbitmq >/dev/null
until_true "rabbitmq healthy" sh -c "[ \"\$(docker inspect -f '{{.State.Health.Status}}' $(compose ps -q rabbitmq))\" = healthy ]"
echo "   rabbitmq back after $(( $(date +%s) - $(date -d "$(docker inspect -f '{{.State.FinishedAt}}' "$(compose ps -q rabbitmq)")" +%s 2>/dev/null || date +%s) ))s"
until_true "outbox drained" sh -c "[ \"\$(docker compose ${PROFILES[*]} exec -T postgres psql -U ${POSTGRES_USER:-postgres} -d jobs -tAc 'SELECT count(*) FROM outbox WHERE sent_at IS NULL')\" = 0 ]"
pass "outbox drained after the broker came back: every publish confirmed"
wait_status "${running[0]}" cancelled
for id in "${running[@]:1}" "${during[@]}"; do wait_status "$id" completed; done
printf '   %-38s %-34s %-10s %s\n' JOB NAME STATUS ATTEMPT
for id in "${running[@]}" "${during[@]}"; do job "$id" | jq -r '[.id, .name, .status, .attempt] | @tsv' | awk -F'\t' '{printf "   %-38s %-34s %-10s %s\n", $1, $2, $3, $4}'; done
pass "after the broker restart: ${#during[@]} outage submissions completed, $(( ${#running[@]} - 1 )) running jobs completed, the cancel landed"
consumers=$(rmq list queues name consumers --format=raw_json | jq '[.[] | select(.name == "jobs.backtest") | .consumers] | add // 0')
[ "$consumers" -eq "$WORKERS" ] || fail "only $consumers of $WORKERS workers consume jobs.backtest after the broker restart"
pass "every worker re-subscribed to jobs.backtest after the broker restart ($consumers consumers)"
compose logs --no-log-prefix --since 5m job-orchestrator backtest-worker 2>/dev/null | jq -Rr 'fromjson? | .Message // empty' |
    grep -E "Outbox publish failed|recover|reconnect|Published" | sort | uniq -c | sort -rn | head -6 | sed 's/^/   log /' || true
until_true "${WORKERS} healthy workers" workers_ready

echo
echo "== 4. poison job: an algorithm that kills its worker"
restarts_before=$(worker_restarts)
dead_before=$(rmq list queues name messages --format=raw_json | jq '[.[] | select(.name == "jobs.backtest.dead") | .messages] | add // 0')
poison_id=$(submit "$(uploaded hardening-poison "$poison")")
wait_status "$poison_id" failed
job "$poison_id" | jq -c '{status, attempt, error}'
[ "$(job "$poison_id" | jq -r .error.code)" = max-attempts-exceeded ] || fail "poison job error is not max-attempts-exceeded"
until_true "poison dispatch dead-lettered" sh -c "[ \$(docker compose ${PROFILES[*]} exec -T rabbitmq rabbitmqadmin -u ${RABBITMQ_USER:-lean} -p ${RABBITMQ_PASSWORD:-lean-dev-rabbitmq} -V ${RABBITMQ_VHOST:-lean} list queues name messages --format=raw_json | jq '[.[] | select(.name == \"jobs.backtest.dead\") | .messages] | add // 0') -gt $dead_before ]"
rmq get queue=jobs.backtest.dead ackmode=ack_requeue_true count=100 --format=raw_json |
    jq -c --arg id "$poison_id" '.[] | select((.payload | fromjson | .jobId) == $id) | {routing_key, deaths: .properties.headers["x-death"], firstDeathReason: .properties.headers["x-first-death-reason"], deliveryCount: .properties.headers["x-delivery-count"]}' | sed 's/^/   dlq /'
until_true "${WORKERS} healthy workers" workers_ready
restarts=$(( $(worker_restarts) - restarts_before ))
[ "$restarts" -ge 3 ] || fail "expected Docker to restart the crashed workers ($restarts restarts)"
pass "poison job failed after $(job "$poison_id" | jq -r .attempt) attempts ($(job "$poison_id" | jq -r .error.message)), dispatch in jobs.backtest.dead, Docker restarted the crashed workers $restarts times"

echo
echo "== 5. resource limits: RAM over maxRamMb, run time over maxRuntimeSeconds"
ram_id=$(submit "$(uploaded hardening-ram-limit "$hog" 512)")
time_id=$(submit "$(uploaded hardening-time-limit "$slow" 2048 20)")
wait_status "$ram_id" failed
wait_status "$time_id" failed
job "$ram_id" | jq -c '{name, status, attempt, error}'
job "$time_id" | jq -c '{name, status, attempt, error}'
job "$ram_id" | jq -r .error.message | grep -qiE "ram|memory" || fail "RAM-limit job failed without the reason"
[ "$(job "$time_id" | jq -r .error.code)" = timeout ] || fail "time-limit job did not fail with timeout"
pass "RAM hog stopped: $(job "$ram_id" | jq -r '.error.code + ": " + .error.message')"
pass "time limit enforced: $(job "$time_id" | jq -r '.error.code + ": " + .error.message')"

echo
echo "== 6. kill a live-worker container: redeployed, holdings restored, no double buy"
gwc() { curl -fsS -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' "$@"; }
btc() { curl -sS -H "X-Api-Key: $KEY" "$GW/live/$live/state" | jq -r '[.holdings[]? | select((.symbol.value // "") == "BTCUSD") | .q] | add // 0'; }
live=$(gwc -X POST -d '{"name":"hardening-live","algorithm":{"source":"builtin","typeName":"LiveWorkerDemoAlgorithm"},"brokerage":"PaperBrokerage","dataQueueHandlers":["bus"]}' "$GW/live" | jq -r .id)
lc="${COMPOSE_PROJECT_NAME:-lean}-live-${live}"
until_true "live container running" sh -c "[ \"\$(docker inspect -f '{{.State.Running}}' $lc)\" = true ]"
docker inspect -f '   {{.Name}}: cpus {{.HostConfig.NanoCpus}} memory {{.HostConfig.Memory}} restart {{.HostConfig.RestartPolicy.Name}}' "$lc"
[ "$(docker inspect -f '{{.HostConfig.Memory}}' "$lc")" -gt 0 ] && [ "$(docker inspect -f '{{.HostConfig.NanoCpus}}' "$lc")" -gt 0 ] || fail "live-worker has no CPU/memory limit"
until_true "BTCUSD bought" sh -c "[ \"\$(curl -sS -H 'X-Api-Key: $KEY' $GW/live/$live/state | jq -r '[.holdings[]? | .q] | add // 0')\" != 0 ]"
before=$(btc)
epoch=$(job "$live" | jq -r .leaseEpoch)
echo "   deployment $live holds $before BTCUSD, lease epoch $epoch; SIGKILL the container's main process"
# A crash, not `docker kill`: Docker counts a `docker kill` as a manual stop and skips the on-failure restart policy.
killed_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
docker run --rm --pid host --user 0 --entrypoint sh "$(docker inspect -f '{{.Config.Image}}' "$lc")" -c "kill -9 $(docker inspect -f '{{.State.Pid}}' "$lc")"
until_true "live container running again" sh -c "[ \"\$(docker inspect -f '{{.State.Running}}' $lc 2>/dev/null)\" = true ]"
# The killed run's engine.log survives in the container until the restarted worker recreates the workspace.
until_true "live worker recreated the workspace" sh -c "docker logs --since $killed_at $lc 2>&1 | grep -qE 'restoring|fresh portfolio'"
until_true "algorithm restarted" sh -c "docker exec $lc sh -c 'grep -q \"LiveWorkerDemo: started with\" /work/$live/live/engine.log'"
docker exec "$lc" sh -c "grep 'LiveWorkerDemo: started with' /work/$live/live/engine.log" | sed 's/^/   /'
docker exec "$lc" sh -c "grep -q 'LiveWorkerDemo: started with $before BTCUSD' /work/$live/live/engine.log" || fail "restarted deployment did not restore $before BTCUSD"
job "$live" | jq -c '{status, attempt, leaseEpoch, workerId}'
sleep 20
[ "$(btc)" = "$before" ] || fail "holdings changed after the restore: $(btc) (double buy?)"
pass "live-worker container killed: back on lease epoch $(job "$live" | jq -r .leaseEpoch), restored $before BTCUSD, still $before 20 s later (no double buy)"
gwc -X POST -d '{"reason":"verify-hardening done","liquidate":true}' "$GW/live/$live/stop" >/dev/null
wait_status "$live" cancelled

echo
echo "== 7. load: ${LOAD_JOBS} queued backtests across ${WORKERS} workers"
until_true "${WORKERS} healthy workers" workers_ready
idle
load_ids=()
load_started=$(date +%s.%N)
for i in $(seq 1 "$LOAD_JOBS"); do load_ids+=("$(submit "$(builtin "hardening-load-$i" BasicTemplateAlgorithm)")"); done
echo "   ${#load_ids[@]} jobs submitted in $(printf '%.1f' "$(echo "$(date +%s.%N) - $load_started" | bc)")s; queue: $(curl -fsS "${ORCH%/internal/v1}/internal/v1/queues" | jq -c '[.[]? | select(.queue == "jobs.backtest")] | first // .')"
for id in "${load_ids[@]}"; do wait_status "$id" completed; done
load_elapsed=$(echo "$(date +%s.%N) - $load_started" | bc)
for id in "${load_ids[@]}"; do job "$id"; done | jq -s '.' > "${work}/load.json"
python3 - "${work}/load.json" "$load_elapsed" "$WORKERS" <<'PY' | tee "${work}/throughput.txt"
import collections, json, statistics, sys
from datetime import datetime
jobs, elapsed, workers = json.load(open(sys.argv[1])), float(sys.argv[2]), int(sys.argv[3])
t = lambda s: datetime.fromisoformat(s.replace("Z", "+00:00"))
run = [(t(j["finishedAt"]) - t(j["startedAt"])).total_seconds() for j in jobs]
wait = [(t(j["startedAt"]) - t(j["createdAt"])).total_seconds() for j in jobs]
by = collections.Counter(j["workerId"] for j in jobs)
print(f"   jobs: {len(jobs)} completed, attempts {dict(collections.Counter(j['attempt'] for j in jobs))}")
print(f"   wall time (first submit -> last completed): {elapsed:.1f} s")
print(f"   throughput: {len(jobs) / elapsed * 60:.1f} backtests/min ({len(jobs) / elapsed:.3f}/s) on {workers} workers")
print(f"   run time per job: mean {statistics.mean(run):.1f} s, p50 {statistics.median(run):.1f} s, max {max(run):.1f} s")
print(f"   queue wait: mean {statistics.mean(wait):.1f} s, max {max(wait):.1f} s")
print(f"   per worker: {dict(sorted(by.items()))}")
sys.exit(0 if len(by) == workers else 1)
PY
pass "${LOAD_JOBS}/${LOAD_JOBS} completed on all ${WORKERS} workers ($(grep throughput "${work}/throughput.txt" | sed 's/^ *throughput: //'))"

echo
compose ps backtest-worker --format 'table {{.Name}}\t{{.Status}}'
echo "== summary"
printf '  %s\n' "${RESULTS[@]}"
echo "ALL CHECKS PASSED"
