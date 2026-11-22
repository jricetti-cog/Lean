#!/usr/bin/env bash
# Platform parity regression run (plan step s6.1): LEAN's regression suite through the monolith and through the
# gateway of the compose stack, then the parity table in Tests/Services.Parity/parity-results/.
#
#   Tests/Services.Parity/run-parity.sh                 # build, monolith suite, stack up, platform suite
#   SKIP_BUILD=1 SKIP_MONOLITH=1 Tests/Services.Parity/run-parity.sh
#   WORKERS=6 PARITY_ONLY=UpdateOrderRegressionAlgorithm SKIP_MONOLITH=1 Tests/Services.Parity/run-parity.sh
#
# WORKERS backtest-worker replicas (default 6) and jobs in flight; MONOLITH_TRX reuses an earlier monolith run;
# KEEP_STACK=0 takes the stack down afterwards. Needs docker (with compose) and the quantconnect/lean:foundation image.
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT=$PWD
WORKERS=${WORKERS:-6}
OUT=${PARITY_OUTPUT_DIR:-$ROOT/Tests/Services.Parity/parity-results}
MONOLITH_TRX=${MONOLITH_TRX:-$ROOT/Tests/bin/Release/monolith-regression.trx}
FOUNDATION=${LEAN_FOUNDATION:-quantconnect/lean:foundation}
mkdir -p "$OUT"

if [ "${SKIP_BUILD:-0}" != 1 ]; then
    echo "== dotnet build QuantConnect.Lean.sln -c Release"
    dotnet build QuantConnect.Lean.sln -c Release -v q -nologo
fi

if [ "${SKIP_MONOLITH:-0}" != 1 ]; then
    echo "== monolith: RegressionTests.AlgorithmStatisticsRegression in $FOUNDATION"
    docker run --rm -v "$ROOT:$ROOT" -w "$ROOT/Tests/bin/Release" "$FOUNDATION" dotnet test QuantConnect.Tests.dll \
        --filter "FullyQualifiedName~QuantConnect.Tests.RegressionTests.AlgorithmStatisticsRegression" \
        --logger "trx;LogFileName=$MONOLITH_TRX" > "$OUT/monolith.log" 2>&1 || true
    tail -n 5 "$OUT/monolith.log"
fi

echo "== stack: $WORKERS backtest workers"
[ -f .env ] || cp .env.example .env
export DOCKER_SOCKET_GID=${DOCKER_SOCKET_GID:-$(stat -c %g /var/run/docker.sock)}
# The harness polls every in-flight job; lift the per-key rate limit for this run only.
GATEWAY_RATE_LIMIT=${GATEWAY_RATE_LIMIT:-100000} BACKTEST_WORKER_REPLICAS=$WORKERS \
    docker compose --profile infra --profile orchestrator --profile results --profile report --profile workers --profile gateway \
    up -d --build --wait
docker compose ps --format 'table {{.Name}}\t{{.Status}}'

echo "== platform: Tests/Services.Parity through the gateway"
status=0
docker run --rm --network host -v "$ROOT:$ROOT" -w "$ROOT/Tests/Services.Parity/bin/Release/net10.0" \
    -e PARITY_REPO_ROOT="$ROOT" -e PARITY_GATEWAY_URL="${PARITY_GATEWAY_URL:-http://127.0.0.1:${GATEWAY_PORT:-5080}}" \
    -e PARITY_CONCURRENCY="${PARITY_CONCURRENCY:-$WORKERS}" -e PARITY_MONOLITH_TRX="$MONOLITH_TRX" -e PARITY_OUTPUT_DIR="$OUT" \
    -e PARITY_ONLY="${PARITY_ONLY:-}" -e PARITY_LANGUAGES="${PARITY_LANGUAGES:-}" -e PARITY_API_KEY="${PARITY_API_KEY:-}" \
    "$FOUNDATION" dotnet test QuantConnect.Tests.Services.Parity.dll --filter "TestCategory=ServicesParity" \
    --logger "trx;LogFileName=$OUT/platform.trx" --logger "console;verbosity=normal" > "$OUT/platform.log" 2>&1 || status=$?
grep -E "^\s*(Failed|Total|Passed|Skipped)|Test Run" "$OUT/platform.log" | tail -n 8
cat "$OUT/parity.md" 2>/dev/null | sed -n '1,12p'

[ "${KEEP_STACK:-1}" = 1 ] || docker compose --profile '*' down
exit $status
