#!/usr/bin/env bash
# Verifies the research and toolbox profiles (plan step s3.4) against the compose stack:
#   docker buildx bake -f Services/docker-bake.hcl research toolbox market-data results backtest-worker
#   docker compose --profile infra --profile market-data --profile results --profile research up -d --wait
#   sh deploy/compose/smoke-backtest.sh            # a finished backtest for the results API
#   bash deploy/compose/verify-research-toolbox.sh
# toolbox: help, a synthetic rdg job published atomically onto lean-data (and served by market-data), the overwrite guard
# (a real ticker leaves equity/usa/daily/spy.zip byte-identical), a failing app publishing nothing. research: read-only
# lean-data, injected base URLs, ServicesQuantBookTemplate.ipynb executed headless (QuantBook vs market-data history,
# a finished backtest from results). Requires curl and jq.
set -euo pipefail
[ -f .env ] && set -a && . ./.env && set +a
MD="http://127.0.0.1:${MARKET_DATA_PORT:-5082}"
RESEARCH="http://127.0.0.1:${RESEARCH_PORT:-8888}"
TB="docker compose --profile toolbox run --rm -T toolbox"
DC="docker compose --profile infra --profile research"
VOLUME="${COMPOSE_PROJECT_NAME:-lean}_lean-data"
SYN="SYNTBX$(date +%s | tail -c 6)"
ok() { echo "  ok    $*"; }
fail() { echo "  FAIL  $*" >&2; exit 1; }
check() { [ "$2" = "$3" ] && ok "$1 ($2)" || fail "$1: expected '$3', got '$2'"; }
on_volume() { docker run --rm -v "${VOLUME}:/d:ro" "${SEED_IMAGE:-alpine:3.22.8}" sh -c "$1"; }

echo "toolbox (one-shot, profile toolbox):"
$TB help > /tmp/toolbox-help.txt 2>&1 || true
grep -q "randomdatagenerator" /tmp/toolbox-help.txt && ok "help lists the apps" || { cat /tmp/toolbox-help.txt; fail "help"; }
set +e; $TB nosuchapp > /dev/null 2>&1; s=$?; set -e
check "unknown app exit code" "$s" 2

SPY_BEFORE=$(on_volume "sha256sum /d/equity/usa/daily/spy.zip | cut -d' ' -f1")
echo "rdg ${SYN} (synthetic ticker, daily):"
$TB rdg --tickers="$SYN" --start=20240102 --end=20240131 --resolution=Daily --security-type=Equity --market=usa \
    --include-coarse=false --random-seed=42 2>&1 | grep -E "^lean-toolbox|^  [+~] " | sed 's/^/        /'
lower=$(echo "$SYN" | tr '[:upper:]' '[:lower:]')
check "published equity/usa/daily/${lower}.zip" "$(on_volume "test -f /d/equity/usa/daily/${lower}.zip && echo yes")" yes
check "toolbox staging cleaned" "$(on_volume 'find /d/.toolbox-staging -mindepth 1 -maxdepth 1 | wc -l' | tr -d ' ')" 0
HIST=$(curl -s "$MD/internal/v1/data/history?securityType=Equity&market=usa&ticker=${SYN}&resolution=Daily&start=2024-01-02&end=2024-01-31")
echo "        market-data history: $(echo "$HIST" | jq -c 'length as $n | {bars: $n, first: .[0]}')"
check "market-data serves the generated bars" "$(echo "$HIST" | jq 'length > 0')" true

echo "overwrite guard (rdg --tickers=SPY over the seeded range):"
set +e
$TB rdg --tickers=SPY --start=20131007 --end=20131011 --resolution=Daily --security-type=Equity --market=usa \
    --include-coarse=false --random-seed=42 > /tmp/toolbox-spy.txt 2>&1; s=$?
set -e
grep -E "^lean-toolbox" /tmp/toolbox-spy.txt | sed 's/^/        /'
check "refused with exit code" "$s" 3
check "spy.zip byte-identical" "$(on_volume "sha256sum /d/equity/usa/daily/spy.zip | cut -d' ' -f1")" "$SPY_BEFORE"

echo "failing app publishes nothing (kdc without --source-dir):"
set +e; $TB kdc --date=20240102 > /dev/null 2>&1; s=$?; set -e
[ "$s" -ne 0 ] && ok "exit code $s" || fail "kdc without inputs exited 0"
check "toolbox staging cleaned" "$(on_volume 'find /d/.toolbox-staging -mindepth 1 -maxdepth 1 | wc -l' | tr -d ' ')" 0

echo "research (profile research):"
check "jupyter /api" "$(curl -s "$RESEARCH/api" | jq -r 'has("version")')" true
set +e; $DC exec -T research touch /Lean/Data/.research-write-probe 2> /tmp/research-ro.txt; s=$?; set -e
[ "$s" -ne 0 ] && grep -q "Read-only" /tmp/research-ro.txt && ok "lean-data mounted read-only ($(cat /tmp/research-ro.txt))" || fail "research can write lean-data"
check "LEAN_MARKET_DATA_URL" "$($DC exec -T research printenv LEAN_MARKET_DATA_URL)" "http://market-data:8080"
check "LEAN_RESULTS_URL" "$($DC exec -T research printenv LEAN_RESULTS_URL)" "http://results:8080"
echo "        executing ServicesQuantBookTemplate.ipynb (nbconvert, headless)"
$DC exec -T research jupyter nbconvert --to notebook --execute --ExecutePreprocessor.timeout=300 \
    --output-dir /tmp --output executed.ipynb ServicesQuantBookTemplate.ipynb 2>&1 | tail -1 | sed 's/^/        /'
OUT=$($DC exec -T research cat /tmp/executed.ipynb)
check "notebook cells without errors" "$(echo "$OUT" | jq '[.cells[] | select(.cell_type=="code") | .outputs[]? | select(.output_type=="error")] | length')" 0
echo "$OUT" | jq -r '.cells[] | select(.cell_type=="code") | .outputs[]? | select(.output_type=="stream") | .text | join("")' | sed 's/^/        /'
echo "$OUT" | jq -r '.cells[] | select(.cell_type=="code" and (.source | join("") | contains("comparison = "))) | .outputs[] | select(.output_type=="execute_result") | .data["text/plain"] | join("")' | sed 's/^/        /'
echo "== research and toolbox verified"
