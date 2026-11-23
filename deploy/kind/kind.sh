#!/usr/bin/env bash
# Local Kubernetes for the Helm chart (plan step s6.3): a kind cluster with a local registry (images are pushed once,
# shared layers deduplicated) and KEDA for the queue-depth autoscaler, then `helm upgrade --install`.
#   bash deploy/kind/kind.sh up        # cluster + registry + KEDA, push the local images, install the chart
#   bash deploy/kind/kind.sh smoke     # port-forward the gateway/console/MinIO and run deploy/smoke/smoke.sh
#   bash deploy/kind/kind.sh down      # delete the cluster and the registry
# (`cluster`, `load` and `install` run the steps of `up` separately.)
# Needs docker, kind, kubectl and helm. Images: lean/<service>:dev from `make build` (or `docker compose build`).
set -euo pipefail

CLUSTER="${KIND_CLUSTER:-lean}"
NAMESPACE="${NAMESPACE:-lean}"
RELEASE="${RELEASE:-lean}"
REG_NAME="${KIND_REGISTRY_NAME:-kind-registry}"
REG_PORT="${KIND_REGISTRY_PORT:-5001}"
SRC_REGISTRY="${IMAGE_REGISTRY:-lean}"
TAG="${IMAGE_TAG:-dev}"
KEDA_VERSION="${KEDA_VERSION:-2.18.1}"
IMAGES=(api-gateway job-orchestrator results optimizer backtest-worker live-worker market-data report console lean-data)
CHART="$(cd "$(dirname "$0")/../helm/lean-platform" && pwd)"

registry() {
    if [ "$(docker inspect -f '{{.State.Running}}' "$REG_NAME" 2>/dev/null)" != true ]; then
        docker run -d --restart=always -p "127.0.0.1:${REG_PORT}:5000" --network bridge --name "$REG_NAME" registry:2
    fi
}

cluster() {
    if ! kind get clusters | grep -qx "$CLUSTER"; then
        kind create cluster --name "$CLUSTER" --config "$(dirname "$0")/kind-config.yaml" --wait 120s
    fi
    # containerd on the node resolves localhost:${REG_PORT} to the registry container (kind's local-registry recipe).
    for node in $(kind get nodes --name "$CLUSTER"); do
        docker exec "$node" mkdir -p "/etc/containerd/certs.d/localhost:${REG_PORT}"
        printf '[host."http://%s:5000"]\n' "$REG_NAME" | docker exec -i "$node" cp /dev/stdin "/etc/containerd/certs.d/localhost:${REG_PORT}/hosts.toml"
    done
    docker network connect kind "$REG_NAME" 2>/dev/null || true
    kubectl config use-context "kind-${CLUSTER}" >/dev/null
}

keda() {
    helm repo add kedacore https://kedacore.github.io/charts >/dev/null 2>&1 || true
    helm repo update kedacore >/dev/null
    helm upgrade --install keda kedacore/keda --version "$KEDA_VERSION" --namespace keda --create-namespace --wait --timeout 10m
}

load() {
    for svc in "${IMAGES[@]}"; do
        docker tag "${SRC_REGISTRY}/${svc}:${TAG}" "localhost:${REG_PORT}/lean/${svc}:${TAG}"
        docker push -q "localhost:${REG_PORT}/lean/${svc}:${TAG}"
    done
}

install() {
    helm upgrade --install "$RELEASE" "$CHART" --namespace "$NAMESPACE" --create-namespace \
        --set image.registry="localhost:${REG_PORT}/lean" --set image.tag="$TAG" \
        --wait --wait-for-jobs --timeout "${HELM_TIMEOUT:-30m}" "$@"
    kubectl -n "$NAMESPACE" get pods,svc,pvc,hpa,scaledobject
}

smoke() {
    local pids=()
    trap 'kill "${pids[@]}" 2>/dev/null || true' EXIT
    for fwd in "api-gateway ${GATEWAY_PORT:-5080}:8080" "console ${CONSOLE_PORT:-5090}:8080" "minio ${MINIO_API_PORT:-9000}:9000"; do
        set -- $fwd
        kubectl -n "$NAMESPACE" port-forward "svc/$1" "$2" >/dev/null &
        pids+=($!)
    done
    for _ in $(seq 1 30); do curl -fsS -o /dev/null "http://127.0.0.1:${GATEWAY_PORT:-5080}/healthz" && break; sleep 1; done
    # The live step shows the per-deployment Deployment the orchestrator created through the Kubernetes API.
    SMOKE_LIVE="${SMOKE_LIVE:-1}" \
    SMOKE_LIVE_HOOK="echo \"      kubectl get deploy (\$1):\"; kubectl -n $NAMESPACE get deploy -l lean.role=live-worker -L lean.job-id | sed 's/^/        /'" \
        bash "$(dirname "$0")/../smoke/smoke.sh"
    if [ "${SMOKE_LIVE:-1}" = 1 ]; then
        for _ in $(seq 1 60); do
            [ -z "$(kubectl -n "$NAMESPACE" get deploy -l lean.role=live-worker -o name)" ] && break; sleep 2
        done
        [ -z "$(kubectl -n "$NAMESPACE" get deploy -l lean.role=live-worker -o name)" ] \
            && echo "   PASS stopped live deployment removed by the orchestrator sweep" || { echo "   FAIL live Deployment not removed" >&2; exit 1; }
    fi
}

down() {
    kind delete cluster --name "$CLUSTER"
    docker rm -f "$REG_NAME" >/dev/null 2>&1 || true
}

case "${1:-}" in
    up) registry; cluster; keda; load; shift; install "$@" ;;
    cluster) registry; cluster; keda ;;
    load) registry; load ;;
    install) shift; install "$@" ;;
    smoke) smoke ;;
    down) down ;;
    *) echo "usage: $0 up|cluster|load|install|smoke|down" >&2; exit 2 ;;
esac
