#!/usr/bin/env bash
# Image size gate for the Services/Dockerfile images (plan step s2.2). Run after
# `docker buildx bake -f Services/docker-bake.hcl`; exits non-zero when an image grows past its budget.
#
# Budgets are the growth over each image's base, so they hold whatever the base images weigh:
#   slim API images    (mcr.microsoft.com/dotnet/aspnet:10.0)  <= ASPNET_BUDGET_MB  (default 50)
#   engine images      (quantconnect/lean:foundation)          <= ENGINE_BUDGET_MB  (default 400)
#   research           (foundation + tini + clr-loader)        <= RESEARCH_BUDGET_MB (default 450)
set -euo pipefail

REGISTRY="${REGISTRY:-lean}"
TAG="${TAG:-dev}"
ASPNET_BASE="${ASPNET_BASE:-mcr.microsoft.com/dotnet/aspnet:10.0}"
LEAN_FOUNDATION="${LEAN_FOUNDATION:-quantconnect/lean:foundation}"
ASPNET_BUDGET_MB="${ASPNET_BUDGET_MB:-50}"
ENGINE_BUDGET_MB="${ENGINE_BUDGET_MB:-400}"
RESEARCH_BUDGET_MB="${RESEARCH_BUDGET_MB:-450}"

size() { docker image inspect --format '{{.Size}}' "$1"; }
mb() { echo $(( $1 / 1000000 )); }

aspnet_base=$(size "$ASPNET_BASE")
foundation_base=$(size "$LEAN_FOUNDATION")
failed=0

printf '%-18s %-10s %12s %12s %10s  %s\n' IMAGE BASE SIZE_MB GROWTH_MB BUDGET_MB RESULT
check() {
    local service="$1" base_name="$2" base_size="$3" budget="$4"
    local image="${REGISTRY}/${service}:${TAG}" image_size growth result=ok
    image_size=$(size "$image")
    growth=$(( image_size - base_size ))
    if (( growth > budget * 1000000 )); then result=FAIL; failed=1; fi
    printf '%-18s %-10s %12s %12s %10s  %s\n' "$service" "$base_name" "$(mb "$image_size")" "$(mb "$growth")" "$budget" "$result"
}

for service in api-gateway job-orchestrator results optimizer; do
    check "$service" aspnet "$aspnet_base" "$ASPNET_BUDGET_MB"
done
for service in backtest-worker live-worker market-data report toolbox; do
    check "$service" foundation "$foundation_base" "$ENGINE_BUDGET_MB"
done
check research foundation "$foundation_base" "$RESEARCH_BUDGET_MB"

exit "$failed"
