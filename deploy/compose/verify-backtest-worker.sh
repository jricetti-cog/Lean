#!/usr/bin/env bash
# Plan step s4.3 verification: `docker compose up --scale backtest-worker=3`, 10 concurrent backtests (C# built-ins and
# uploaded Python), all completed by three workers; then a crashing algorithm fails on its own while every worker
# stays up and takes the next job. Run from the repository root after building the images:
#   docker compose --profile infra --profile orchestrator --profile workers build job-orchestrator backtest-worker
set -euo pipefail
[ -f .env ] && set -a && . ./.env && set +a
API="http://127.0.0.1:${ORCHESTRATOR_PORT:-5081}/internal/v1"
WORKERS="${WORKERS:-3}"
TIMEOUT="${TIMEOUT:-900}"
compose() { docker compose --profile infra --profile orchestrator --profile workers "$@"; }
pass() { echo "PASS: $*"; }
fail() { echo "FAIL: $*"; exit 1; }
field() { python3 -c "import json,sys; print(json.load(sys.stdin)$1)"; }

echo "== docker compose up --scale backtest-worker=${WORKERS}"
compose up -d --wait --scale "backtest-worker=${WORKERS}"
compose ps backtest-worker --format 'table {{.Name}}\t{{.Status}}'
running=$(compose ps backtest-worker --status running -q | wc -l)
[ "$running" -eq "$WORKERS" ] || fail "${running}/${WORKERS} workers running"
pass "${WORKERS} backtest-worker replicas running"

upload() { curl -fsS -X POST -H 'Content-Type: text/x-python' --data-binary "@$1" "${API}/algorithms?fileName=$(basename "$1")"; }
submit() { curl -fsS -X POST -H 'Content-Type: application/json' -d "$1" "${API}/jobs/" | field "['id']"; }
builtin() { printf '{"kind":"backtest","backtest":{"name":"%s","algorithm":{"source":"builtin","typeName":"%s"},"maxRamMb":2048}}' "$1" "$1"; }
uploaded() { printf '{"kind":"backtest","backtest":{"name":"%s","algorithm":{"source":"upload","language":"python","object":%s},"maxRamMb":2048}}' "$1" "$2"; }

echo "== upload Python algorithms"
py_basic=$(upload Algorithm.Python/BasicTemplateAlgorithm.py)
py_framework=$(upload Algorithm.Python/BasicTemplateFrameworkAlgorithm.py)
crash_file=$(mktemp -d)/CrashingAlgorithm.py
cat > "$crash_file" <<'PY'
import os, signal
from AlgorithmImports import *

class CrashingAlgorithm(QCAlgorithm):
    def initialize(self):
        self.set_start_date(2013, 10, 7)
        self.set_end_date(2013, 10, 11)
        self.add_equity("SPY", Resolution.MINUTE)

    def on_data(self, data):
        os.kill(os.getpid(), signal.SIGKILL)
PY
py_crash=$(upload "$crash_file")

echo "== submit 10 concurrent jobs"
requests=(
    "$(builtin BasicTemplateAlgorithm)" "$(builtin BasicTemplateAlgorithm)" "$(builtin BasicTemplateAlgorithm)"
    "$(builtin BasicTemplateFrameworkAlgorithm)" "$(builtin BasicTemplateFrameworkAlgorithm)"
    "$(builtin BasicTemplateDailyAlgorithm)" "$(builtin BasicTemplateDailyAlgorithm)"
    "$(uploaded PyBasicTemplateAlgorithm "$py_basic")" "$(uploaded PyBasicTemplateAlgorithm "$py_basic")"
    "$(uploaded PyBasicTemplateFrameworkAlgorithm "$py_framework")"
)
ids_file=$(mktemp)
for request in "${requests[@]}"; do submit "$request" >> "$ids_file" & done
wait
mapfile -t ids < "$ids_file"
[ "${#ids[@]}" -eq 10 ] || fail "submitted ${#ids[@]}/10 jobs"
pass "10 jobs submitted concurrently"

wait_terminal() {
    local deadline=$((SECONDS + TIMEOUT)) id status
    for id in "$@"; do
        while :; do
            status=$(curl -fsS "${API}/jobs/${id}" | field "['status']")
            case "$status" in completed|failed|cancelled) break ;; esac
            [ "$SECONDS" -lt "$deadline" ] || fail "job ${id} still ${status} after ${TIMEOUT}s"
            sleep 5
        done
    done
}
started=$SECONDS
wait_terminal "${ids[@]}"
echo "   all 10 terminal after $((SECONDS - started))s"

printf '%-38s %-34s %-10s %-8s %s\n' JOB NAME STATUS ATTEMPT WORKER
completed=0
declare -A by_worker=()
for id in "${ids[@]}"; do
    row=$(curl -fsS "${API}/jobs/${id}")
    read -r name status attempt worker < <(echo "$row" | python3 -c "import json,sys; j=json.load(sys.stdin); print(j.get('name'), j['status'], j['attempt'], j.get('workerId'))")
    printf '%-38s %-34s %-10s %-8s %s\n' "$id" "$name" "$status" "$attempt" "$worker"
    [ "$status" = completed ] && completed=$((completed + 1)) && by_worker[$worker]=$(( ${by_worker[$worker]:-0} + 1 ))
done
[ "$completed" -eq 10 ] || fail "${completed}/10 jobs completed"
pass "10/10 jobs completed"
for worker in "${!by_worker[@]}"; do echo "   ${worker}: ${by_worker[$worker]} jobs"; done
[ "${#by_worker[@]}" -eq "$WORKERS" ] || fail "jobs ran on ${#by_worker[@]}/${WORKERS} workers"
pass "work spread over all ${WORKERS} workers"

echo "== worker log evidence (prefetch 1, starts, completions)"
compose logs --no-log-prefix backtest-worker 2>/dev/null | grep -E "consuming jobs.backtest|starting on|completed on" | sed 's/^/   /' | head -40

echo "== crash isolation: a Python algorithm that SIGKILLs its own engine"
crash_id=$(submit "$(uploaded CrashingAlgorithm "$py_crash")")
wait_terminal "$crash_id"
crash=$(curl -fsS "${API}/jobs/${crash_id}")
echo "$crash" | python3 -c "import json,sys; j=json.load(sys.stdin); print('   status', j['status'], 'attempts', j['attempt'], 'error', json.dumps(j.get('error')))"
[ "$(echo "$crash" | field "['status']")" = failed ] || fail "crashing job did not fail"
pass "crashing algorithm failed (engine-crash, retried up to the attempt limit)"
running=$(compose ps backtest-worker --status running -q | wc -l)
restarts=$(compose ps backtest-worker -q | xargs docker inspect -f '{{.RestartCount}}' | paste -sd+ | bc)
[ "$running" -eq "$WORKERS" ] && [ "$restarts" -eq 0 ] || fail "workers running ${running}/${WORKERS}, restarts ${restarts}"
pass "all ${WORKERS} workers still running, 0 container restarts"
after_id=$(submit "$(builtin BasicTemplateAlgorithm)")
wait_terminal "$after_id"
[ "$(curl -fsS "${API}/jobs/${after_id}" | field "['status']")" = completed ] || fail "job after the crash did not complete"
pass "next job after the crash completed"
echo "ALL CHECKS PASSED"
