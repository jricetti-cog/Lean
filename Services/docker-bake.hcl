// Builds every LEAN service image from Services/Dockerfile in one BuildKit run, so the shared build stage
// runs once (plan step s2.2). From the repository root:
//
//   docker buildx bake -f Services/docker-bake.hcl                  # all images, loaded into the local engine
//   docker buildx bake -f Services/docker-bake.hcl results report   # a subset
//   TAG=1.2.3 REGISTRY=ghcr.io/org/lean docker buildx bake -f Services/docker-bake.hcl --push

variable "REGISTRY" {
  default = "lean"
}

variable "TAG" {
  default = "dev"
}

group "default" {
  targets = ["api-gateway", "job-orchestrator", "results", "optimizer",
             "backtest-worker", "live-worker", "market-data", "report", "research", "toolbox"]
}

target "service" {
  name       = svc
  matrix     = {
    svc = ["api-gateway", "job-orchestrator", "results", "optimizer",
           "backtest-worker", "live-worker", "market-data", "report", "research", "toolbox"]
  }
  context    = "."
  dockerfile = "Services/Dockerfile"
  target     = svc
  tags       = ["${REGISTRY}/${svc}:${TAG}"]
  labels     = {
    "org.opencontainers.image.title"  = "lean-${svc}"
    "org.opencontainers.image.source" = "https://github.com/jricetti-cog/Lean"
  }
}
