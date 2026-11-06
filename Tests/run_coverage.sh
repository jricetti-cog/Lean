#!/usr/bin/env bash
# Collects line/branch coverage for QuantConnect.Tests with coverlet, inside the CI image.
# Usage: Tests/run_coverage.sh [dotnet test filter]
# Requires a prior `dotnet build QuantConnect.Lean.sln -c Release` and Docker.
# Output: Tests/coverage-results/<guid>/coverage.cobertura.xml (+ HTML/summary if reportgenerator is installed).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FILTER="${1:-TestCategory!=TravisExclude&TestCategory!=ResearchRegressionTests}"
IMAGE="${LEAN_TEST_IMAGE:-quantconnect/lean:foundation}"
COVERLET_VERSION="$(grep -oP 'coverlet.collector" Version="\K[^"]+' "$REPO/Tests/QuantConnect.Tests.csproj")"
ADAPTER="${NUGET_PACKAGES:-$HOME/.nuget/packages}/coverlet.collector/$COVERLET_VERSION/build/netstandard2.0"
RESULTS="$REPO/Tests/coverage-results"

[ -d "$ADAPTER" ] || { echo "coverlet.collector $COVERLET_VERSION not restored at $ADAPTER; build the solution first" >&2; exit 1; }
rm -rf "$RESULTS" 2>/dev/null || docker run --rm -v "$REPO/Tests:/t" "$IMAGE" rm -rf /t/coverage-results

# The repo must be mounted at the same absolute path it was built at: coverlet skips
# assemblies whose PDB source paths do not exist, which yields an empty report.
docker run --rm \
  -v "$REPO:$REPO" -v "$ADAPTER:/coverlet:ro" \
  -e FILTER="$FILTER" -e REPO="$REPO" -e RESULTS="$RESULTS" -e OWNER="$(id -u):$(id -g)" \
  -w "$REPO/Tests/bin/Release" "$IMAGE" \
  bash -c 'dotnet test QuantConnect.Tests.dll --blame-hang-timeout 300seconds \
    --filter "$FILTER" \
    --settings "$REPO/Tests/coverage.runsettings" \
    --collect "XPlat Code Coverage" --test-adapter-path /coverlet \
    --results-directory "$RESULTS"; rc=$?; chown -R "$OWNER" "$RESULTS"; exit $rc'

if command -v reportgenerator >/dev/null 2>&1; then
  reportgenerator -reports:"$RESULTS/*/coverage.cobertura.xml" -targetdir:"$RESULTS/report" \
    -reporttypes:"Html;TextSummary;JsonSummary" >/dev/null
  head -n 20 "$RESULTS/report/Summary.txt"
fi
