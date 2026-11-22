#!/usr/bin/env bash
# Plan step s4.6 verification: a 3x3 grid optimization of ParameterizedAlgorithm (ema-fast x ema-slow) submitted through the
# api-gateway runs as 9 child backtests on 3 scaled backtest-workers and completes with the best parameter set. Then the
# same optimization on 1 worker, to show that scaling the workers speeds optimizations up (COMPARE=0 skips it).
# Run from the repository root after building the images:
#   docker compose --profile infra --profile orchestrator --profile workers --profile optimizer --profile gateway build
set -euo pipefail
[ -f .env ] && set -a && . ./.env && set +a
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}/api/v1"
ORCH="http://127.0.0.1:${ORCHESTRATOR_PORT:-5081}/internal/v1"
OPT="http://127.0.0.1:${OPTIMIZER_PORT:-5085}/internal/v1"
KEY="${GATEWAY_API_KEY:-lean-dev-api-key}"
WORKERS="${WORKERS:-3}"
TIMEOUT="${TIMEOUT:-900}"
COMPARE="${COMPARE:-1}"
compose() { docker compose --profile infra --profile orchestrator --profile workers --profile optimizer --profile gateway "$@"; }
pass() { echo "PASS: $*"; }
fail() { echo "FAIL: $*"; exit 1; }
gw() { curl -fsS -H "X-Api-Key: ${KEY}" "$@"; }

spec='{"name":"EMA 3x3 grid","backtest":{"algorithm":{"source":"builtin","language":"CSharp","typeName":"ParameterizedAlgorithm"},"maxRamMb":2048},
  "strategy":"QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy",
  "criterion":{"target":"TotalPerformance.PortfolioStatistics.TotalNetProfit","extremum":"max"},
  "constraints":[{"target":"TotalPerformance.PortfolioStatistics.Drawdown","operator":"LessOrEqual","targetValue":0.5}],
  "parameters":[{"name":"ema-fast","min":10,"max":30,"step":10},{"name":"ema-slow","min":100,"max":200,"step":50}],
  "maximumConcurrentBacktests":9}'

run_optimization() { # run_optimization WORKERS -> prints "<optimization id> <seconds>"
    local workers=$1 id status started deadline
    echo "== docker compose up --scale backtest-worker=${workers}" >&2
    compose up -d --wait --quiet-pull --scale "backtest-worker=${workers}" >&2
    local running; running=$(compose ps backtest-worker --status running -q | wc -l)
    [ "$running" -eq "$workers" ] || fail "${running}/${workers} workers running"
    pass "${workers} backtest-worker replicas, optimizer and api-gateway running" >&2

    total=$(gw -X POST -H 'Content-Type: application/json' -d "$spec" "${GW}/optimizations/estimate" | jq -r .totalBacktests)
    [ "$total" = 9 ] || fail "estimate through the gateway is ${total}, expected 9"
    pass "POST /api/v1/optimizations/estimate through the gateway: ${total} backtests" >&2

    id=$(gw -X POST -H 'Content-Type: application/json' -d "$spec" "${GW}/optimizations" | jq -r .id)
    echo "   optimization ${id} submitted through POST /api/v1/optimizations" >&2
    started=$SECONDS; deadline=$((SECONDS + TIMEOUT))
    while :; do
        status=$(gw "${GW}/jobs/${id}" | jq -r .status)
        case "$status" in completed|failed|cancelled) break ;; esac
        [ "$SECONDS" -lt "$deadline" ] || fail "optimization ${id} still ${status} after ${TIMEOUT}s"
        if state=$(gw "${GW}/optimizations/${id}" 2>/dev/null); then
            echo "   $(date -u +%H:%M:%S) ${status}: $(echo "$state" | jq -c '{totalBacktests,completed,failed,running,queued}')" >&2
        fi
        sleep 5
    done
    local taken=$((SECONDS - started))
    [ "$status" = completed ] || fail "optimization ${id} ${status}: $(gw "${GW}/jobs/${id}" | jq -c .error)"
    pass "optimization ${id} completed on ${workers} worker(s) in ${taken}s" >&2
    echo "$id $taken"
}

read -r id scaled < <(run_optimization "$WORKERS")

echo "== optimization state through the gateway (GET /api/v1/optimizations/${id})"
state=$(gw "${GW}/optimizations/${id}")
echo "$state" | jq .
[ "$(echo "$state" | jq -r '.completed')" = 9 ] || fail "not 9 completed children"
[ "$(echo "$state" | jq -r '.best.parameters["ema-fast"] != null')" = true ] || fail "no best parameter set"
pass "9/9 children completed, best parameter set $(echo "$state" | jq -c '.best.parameters') objective $(echo "$state" | jq -r .best.objective)"

echo "== children through the gateway (GET /api/v1/optimizations/${id}/children, best first)"
gw "${GW}/optimizations/${id}/children?limit=9" | jq -r '.items[] | "   \(.jobId) \(.status) ema-fast=\(.parameters["ema-fast"]) ema-slow=\(.parameters["ema-slow"]) net-profit=\(.objective)"'

echo "== child backtest jobs in job-orchestrator (parentJobId=${id})"
children=$(curl -fsS "${ORCH}/jobs/?parentJobId=${id}&limit=100")
echo "$children" | jq -r '.items[] | "   \(.id) \(.status) attempt \(.attempt) \(.workerId) \(.name)"'
[ "$(echo "$children" | jq '[.items[] | select(.status == "completed")] | length')" = 9 ] || fail "orchestrator does not list 9 completed children"
workers_used=$(echo "$children" | jq '[.items[].workerId] | unique | length')
echo "$children" | jq -r '[.items[].workerId] | group_by(.) | .[] | "   \(.[0]): \(length) backtests"'
[ "$workers_used" -eq "$WORKERS" ] || fail "children ran on ${workers_used}/${WORKERS} workers"
pass "the 9 children ran across all ${WORKERS} backtest-workers"

echo "== terminal job (GET /api/v1/jobs/${id}) and optimization-result.json"
gw "${GW}/jobs/${id}" | jq -c '{id, kind, status, attempt, workerId, progress}'
compose logs --no-log-prefix optimizer 2>/dev/null | grep -E '"Optimization .* (running|completed)' | tail -2 | jq -r '.State.Message // .Message' | sed 's/^/   /' || true
compose exec -T minio sh -c 'ls /data/lean-results/'"${id}"'/*/ 2>/dev/null' | sed 's/^/   /' || true

if [ "$COMPARE" = 1 ]; then
    echo "== same optimization on 1 worker"
    read -r single_id single < <(run_optimization 1)
    echo "   1 worker: ${single}s, ${WORKERS} workers: ${scaled}s"
    [ "$scaled" -lt "$single" ] || fail "scaling to ${WORKERS} workers did not speed the optimization up"
    pass "scaling backtest-worker 1 -> ${WORKERS} cut the optimization from ${single}s to ${scaled}s"
    compose up -d --wait --scale "backtest-worker=${WORKERS}" >/dev/null 2>&1 || true
fi
echo "ALL CHECKS PASSED"
