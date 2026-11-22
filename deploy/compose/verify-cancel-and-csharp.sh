#!/usr/bin/env bash
# Plan step s4.8 verification through the gateway: a running backtest is cancelled mid-run (terminal `cancelled` within
# ~10 s, worker kept up with 0 restarts, next job completes), an edited C# algorithm uploaded as .cs source and as a
# project zip is compiled by the worker and completes with statistics, and C# with a syntax error fails with
# `compile-error` and its compiler diagnostics in the job logs. Run from the repository root:
#   docker compose --profile infra --profile orchestrator --profile results --profile workers --profile gateway up -d --build --wait
#   deploy/compose/verify-cancel-and-csharp.sh
set -euo pipefail
[ -f .env ] && set -a && . ./.env && set +a
GW="http://127.0.0.1:${GATEWAY_PORT:-5080}/api/v1"
KEY="${GATEWAY_API_KEY:-lean-dev-api-key}"
TIMEOUT="${TIMEOUT:-600}"
CANCEL_WITHIN="${CANCEL_WITHIN:-10}"
compose() { docker compose --profile infra --profile orchestrator --profile results --profile workers --profile gateway "$@"; }
pass() { echo "PASS: $*"; }
fail() { echo "FAIL: $*"; exit 1; }
api() { curl -fsS -H "X-Api-Key: ${KEY}" "$@"; }
field() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }
work=$(mktemp -d)

upload() { api -X POST -F "file=@$1;filename=$(basename "$1")" -F language=CSharp "${GW}/algorithms"; }
submit() { api -X POST -H 'Content-Type: application/json' -d "{\"name\":\"$1\",\"algorithm\":$2,\"fetchMissingData\":false,\"maxRuntimeSeconds\":900}" "${GW}/backtests" | field "d['id']"; }
status() { api "${GW}/jobs/$1" | field "d['status']"; }
wait_status() {
    local deadline=$((SECONDS + TIMEOUT)) s
    while :; do
        s=$(status "$1")
        case "$s" in completed|failed|cancelled|stale) echo "$s"; return ;; esac
        [ "$SECONDS" -lt "$deadline" ] || fail "job $1 still $s after ${TIMEOUT}s"
        sleep 2
    done
}
restarts() { compose ps backtest-worker -q | xargs docker inspect -f '{{.RestartCount}}' | paste -sd+ | bc; }
statistics() { api "${GW}/jobs/$1/result" | field "', '.join(f'{k}={d[\"statistics\"][k]}' for k in ['Net Profit','Sharpe Ratio','Total Orders'] if k in d['statistics'])"; }

echo "== backtest-worker replicas"
compose ps backtest-worker --format 'table {{.Name}}\t{{.Status}}'
restarts_before=$(restarts)

# Edited BasicTemplateAlgorithm: a different class name and position size, so no built-in class can serve it.
sed -e 's/class BasicTemplateAlgorithm/class EditedTemplateAlgorithm/' -e 's/SetHoldings(_spy, 1)/SetHoldings(_spy, 0.5)/' \
    Algorithm.CSharp/BasicTemplateAlgorithm.cs > "${work}/Main.cs"
grep -q 'class EditedTemplateAlgorithm' "${work}/Main.cs" || fail "could not edit the sample"

echo "== cancel a running backtest"
cat > "${work}/SlowAlgorithm.cs" <<'CS'
using System.Threading;
using QuantConnect.Data;
namespace QuantConnect.Algorithm.CSharp
{
    public class SlowAlgorithm : QCAlgorithm
    {
        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            AddEquity("SPY");
        }

        public override void OnData(Slice slice)
        {
            Thread.Sleep(500);
        }
    }
}
CS
slow=$(upload "${work}/SlowAlgorithm.cs")
slow_id=$(submit "verify-cancel SlowAlgorithm" "$slow")
deadline=$((SECONDS + TIMEOUT))
until [ "$(status "$slow_id")" = running ]; do [ "$SECONDS" -lt "$deadline" ] || fail "slow job never ran"; sleep 1; done
sleep 15
[ "$(status "$slow_id")" = running ] || fail "slow job is no longer running before the cancel: $(status "$slow_id")"
echo "   job ${slow_id} running for 15 s; POST /api/v1/jobs/${slow_id}/cancel"
code=$(curl -sS -o "${work}/cancel.json" -w '%{http_code}' -X POST -H "X-Api-Key: ${KEY}" "${GW}/jobs/${slow_id}/cancel")
[ "$code" = 202 ] || fail "cancel returned ${code}: $(cat "${work}/cancel.json")"
cancel_started=$(date +%s.%N)
until [ "$(status "$slow_id")" = cancelled ]; do
    elapsed=$(echo "$(date +%s.%N) - ${cancel_started}" | bc)
    (( $(echo "${elapsed} < 60" | bc) )) || fail "job still $(status "$slow_id") 60 s after the cancel"
    sleep 0.5
done
elapsed=$(printf '%.1f' "$(echo "$(date +%s.%N) - ${cancel_started}" | bc)")
api "${GW}/jobs/${slow_id}" | field "f\"   status {d['status']}, attempt {d['attempt']}, worker {d.get('workerId')}\""
(( $(echo "${elapsed} <= ${CANCEL_WITHIN}" | bc) )) || fail "cancelled after ${elapsed}s (> ${CANCEL_WITHIN}s)"
pass "running backtest cancelled ${elapsed}s after POST /cancel (202)"
compose logs --no-log-prefix backtest-worker 2>/dev/null | grep -E "${slow_id}.*(Cancel requested|cancelled|killing engine|after cancellation)" | sed 's/^/   /' | tail -5
compose logs --no-log-prefix backtest-worker 2>/dev/null | grep -q "Job ${slow_id} attempt 1 completed" && fail "worker reported job.completed for the cancelled job"
pass "worker published job.cancelled (no job.completed) for ${slow_id}"
sleep 5
[ "$(status "$slow_id")" = cancelled ] || fail "cancelled job changed to $(status "$slow_id")"
running=$(compose ps backtest-worker --status running -q | wc -l)
[ "$running" -ge 1 ] && [ "$(restarts)" -eq "$restarts_before" ] || fail "workers running ${running}, restarts $(restarts) (before ${restarts_before})"
pass "backtest-worker still running (${running} replicas), 0 new container restarts"

echo "== edited C# uploaded as .cs source (next job after the cancel)"
edited=$(upload "${work}/Main.cs")
echo "   $edited"
edited_id=$(submit "verify-csharp EditedTemplateAlgorithm" "$edited")
[ "$(wait_status "$edited_id")" = completed ] || fail "edited C# job: $(api "${GW}/jobs/${edited_id}")"
pass "edited C# ${edited_id} completed: $(statistics "$edited_id")"
compose logs --no-log-prefix backtest-worker 2>/dev/null | grep -E "Compiled uploaded C# of job ${edited_id}" | sed 's/^/   /' | tail -1

echo "== edited C# uploaded as a project zip"
mkdir -p "${work}/project/obj"
cp "${work}/Main.cs" "${work}/project/Main.cs"
echo '<Project Sdk="Microsoft.NET.Sdk" />' > "${work}/project/Project.csproj"
echo 'not C#' > "${work}/project/obj/Generated.cs"
(cd "${work}/project" && python3 -c "import zipfile,os; z=zipfile.ZipFile('../project.zip','w'); [z.write(os.path.join(r,f), os.path.join(r,f)) for r,_,fs in os.walk('.') for f in fs]")
zip_ref=$(upload "${work}/project.zip")
zip_id=$(submit "verify-csharp project.zip" "$zip_ref")
[ "$(wait_status "$zip_id")" = completed ] || fail "zip C# job: $(api "${GW}/jobs/${zip_id}")"
pass "C# project zip ${zip_id} completed: $(statistics "$zip_id")"

echo "== C# with a syntax error"
sed 's/SetCash(100000);/SetCash(100000)/' "${work}/Main.cs" > "${work}/Broken.cs"
broken=$(upload "${work}/Broken.cs")
broken_id=$(submit "verify-csharp syntax error" "$broken")
[ "$(wait_status "$broken_id")" = failed ] || fail "broken C# job did not fail: $(api "${GW}/jobs/${broken_id}")"
api "${GW}/jobs/${broken_id}" > "${work}/broken.json"
python3 - "${work}/broken.json" <<'PY' || fail "broken job error is not a final compile-error"
import json, sys
j = json.load(open(sys.argv[1]))
e = j.get("error") or {}
print(f"   status {j['status']}, attempt {j['attempt']}, error.code {e.get('code')}")
print("   error.message:", e.get("message", "").replace("\n", "\n      "))
sys.exit(0 if e.get("code") == "compile-error" and j["attempt"] == 1 else 1)
PY
pass "syntax error -> failed with compile-error on the first attempt (not retried)"
deadline=$((SECONDS + 30))
until api "${GW}/jobs/${broken_id}/logs?limit=100" > "${work}/logs.json" && grep -q CS1002 "${work}/logs.json"; do
    [ "$SECONDS" -lt "$deadline" ] || fail "no CS1002 diagnostic in the job logs: $(cat "${work}/logs.json")"
    sleep 2
done
field "'\n'.join('   log: ' + i['message'] for i in d['items'])" < "${work}/logs.json"
pass "compiler diagnostics in GET /api/v1/jobs/${broken_id}/logs"

[ "$(restarts)" -eq "$restarts_before" ] || fail "worker restarted during the run"
pass "0 backtest-worker restarts over the whole run"
echo "ALL CHECKS PASSED"
