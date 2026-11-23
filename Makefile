# LEAN platform wrapper (plan step s6.3). `make up` then `make smoke` on a clean clone; `make kind-up kind-smoke` for
# the Helm chart on a local kind cluster. Every target is a plain command: see deploy/README.md.
SHELL := /bin/bash

# Images: locally built lean/<service>:dev by default; `make pull up IMAGE_REGISTRY=ghcr.io/jricetti-cog/lean IMAGE_TAG=0.1.0`
# runs the published ones.
IMAGE_REGISTRY ?= lean
IMAGE_TAG ?= dev
VERSION := $(shell sed -n 's/^appVersion: *"\{0,1\}\([^"]*\)"\{0,1\}/\1/p' deploy/helm/lean-platform/Chart.yaml)

export LEAN_IMAGE_REGISTRY := $(IMAGE_REGISTRY)
export LEAN_IMAGE_TAG := $(IMAGE_TAG)

COMPOSE ?= docker compose
KIND_CLUSTER ?= lean
NAMESPACE ?= lean
RELEASE ?= lean

.PHONY: help up down clean build pull logs ps smoke images push \
        kind-up kind-load kind-install kind-smoke kind-down helm-lint

help:
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-14s %s\n", $$1, $$2}'

up: ## Build missing images and start the default platform (compose), wait until healthy
	$(COMPOSE) up -d --wait

down: ## Stop the compose platform (keeps volumes)
	$(COMPOSE) down --remove-orphans

clean: ## Stop the compose platform and delete its volumes
	$(COMPOSE) --profile '*' down -v --remove-orphans

build: ## Build every image with BuildKit bake (REGISTRY/TAG from IMAGE_REGISTRY/IMAGE_TAG)
	REGISTRY=$(IMAGE_REGISTRY) TAG=$(IMAGE_TAG) docker buildx bake -f Services/docker-bake.hcl

pull: ## Pull the images for IMAGE_REGISTRY/IMAGE_TAG instead of building them
	$(COMPOSE) pull

push: ## Push every image (needs `docker login ghcr.io`), e.g. make push IMAGE_REGISTRY=ghcr.io/jricetti-cog/lean IMAGE_TAG=$(VERSION)
	REGISTRY=$(IMAGE_REGISTRY) TAG=$(IMAGE_TAG) docker buildx bake -f Services/docker-bake.hcl --push

ps: ## Compose service status
	$(COMPOSE) ps -a

logs: ## Follow compose logs
	$(COMPOSE) logs -f --tail 100

smoke: ## Smoke-test the running compose platform: engine backtest via the bus, then the gateway end to end
	bash deploy/compose/smoke-backtest.sh
	bash deploy/smoke/smoke.sh

helm-lint: ## Lint and render the Helm chart
	helm lint deploy/helm/lean-platform
	helm template $(RELEASE) deploy/helm/lean-platform --namespace $(NAMESPACE) >/dev/null

kind-up: ## Create the kind cluster (+ KEDA), load the images and install the chart
	bash deploy/kind/kind.sh up

kind-load: ## Push the local images to the kind cluster's registry
	bash deploy/kind/kind.sh load

kind-install: ## helm upgrade --install the chart on the kind cluster
	bash deploy/kind/kind.sh install

kind-smoke: ## Run deploy/smoke/smoke.sh against the chart on kind (port-forwards the gateway, console and MinIO)
	bash deploy/kind/kind.sh smoke

kind-down: ## Delete the kind cluster and its registry
	bash deploy/kind/kind.sh down
