# Adding a new service on the shared defaults

Every platform service is an ASP.NET Core host under `Services/QuantConnect.Services.<Name>/` that calls
`AddLeanServiceDefaults()` from `Services/QuantConnect.Services.Defaults`. Follow the ownership rules of ADR 0001:
the service owns its data, talks to other services only through their REST API or bus messages, and a service that
does not run the engine must not reference `Engine`.

## 1. Project

```
Services/QuantConnect.Services.<Name>/QuantConnect.Services.<Name>.csproj   # Microsoft.NET.Sdk.Web, net10.0
Services/QuantConnect.Services.<Name>/Program.cs
```

`Services/Directory.Build.props` already sets the common properties. Reference what you need:

* `QuantConnect.Services.Defaults` (always): configuration, health, telemetry, logging, graceful shutdown.
* `QuantConnect.Services.Contracts`: message bodies, envelope, `MessageTypes`, HTTP DTOs (`ContractJson` for camelCase).
* `QuantConnect.Services.Messaging`: RabbitMQ publisher/consumers over the v1 topology (`AddLeanMessaging`), with an
  in-memory bus for tests.
* LEAN projects only if the service needs them; an engine-hosting service also sets `HostsLeanCode`.

```csharp
namespace QuantConnect.Services.Example;

public static class Program   // not top-level statements: Tests reference service assemblies and a global Program clashes
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddLeanServiceDefaults(o => o.HostsLeanCode = false);   // true copies LEAN_* env onto LEAN Config first
        // builder.Services.AddLeanMessaging(builder.Configuration);    // RabbitMQ__Uri
        var app = builder.Build();
        app.MapGet("/internal/v1/example", () => Results.Ok());
        app.Run();
    }
}
```

What `AddLeanServiceDefaults` gives you, with no further code:

| Feature | Detail | Settings |
|---|---|---|
| Health | `GET /healthz` (liveness), `GET /readyz` (checks tagged `HealthTags.Ready`), `Health` contract body | add your own checks with `tags: [HealthTags.Ready]` |
| Telemetry | OpenTelemetry traces, metrics and logs: ASP.NET Core, HttpClient, runtime, every `ActivitySource`/`Meter` named `QuantConnect.Services*`, every `ILogger` | `OTEL_EXPORTER_OTLP_ENDPOINT` (empty = off), `OTEL_SERVICE_NAME` or `Service__Name` |
| Logging | JSON console, UTC timestamps, scopes | standard `Logging__*` |
| Shutdown | readiness goes down first, optional drain, then hosted services stop | `Service__DrainSeconds` (0), `Service__ShutdownTimeoutSeconds` (30) |
| LEAN config | `LEAN_<KEY>` → LEAN `Config` key `<key>` (only with `HostsLeanCode`) | e.g. `LEAN_DATA_FOLDER=/Lean/Data/` |

Name your meter/activity source `QuantConnect.Services.<Name>` so they are exported; Prometheus then sees them as
`lean_*` series with `job="<service name>"`.

## 2. Contracts

New HTTP API: add `docs/architecture/contracts/openapi/<name>.openapi.yaml` (paths under `/internal/v1/...`, shared
components from `common.yaml`). New messages: a JSON schema under `messages/v1/`, the envelope `type` enum, the
`validate.mjs` catalogue, `MessageTypes` and an example; a new queue goes in `topology/definitions.json` and
`Topology.cs`. Run `(cd docs/architecture/contracts && npm install && npm run lint)`. Contract changes inside v1 are
additive only. If the public API exposes it, add the route to the gateway (`GatewayEndpoints.cs`, an upstream in
`Gateway__Upstreams__<Name>`) and to `gateway.openapi.yaml`.

## 3. Image

Add the service to the image map in `Services/Dockerfile` (the `for entry in ... <image>:<Name>` loop of the build
stage), a runtime target (`FROM aspnet-runtime AS <image>` for services that never load `Engine`, `FROM engine-runtime`
otherwise) with `ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.<Name>.dll"]`, and the image name to both lists in
`Services/docker-bake.hcl` and to the push loop in `.github/workflows/services.yml`. Until the project exists, the
Dockerfile packages `QuantConnect.Services.Placeholder` under its assembly name, so the image already builds and passes
its `HEALTHCHECK`. Images run as the non-root `app` user (uid 1654); write only under a volume or `/tmp`.

## 4. Compose

```yaml
  example:
    image: ${LEAN_IMAGE_REGISTRY:-lean}/example:${LEAN_IMAGE_TAG:-dev}
    build: { context: ., dockerfile: Services/Dockerfile, target: example }
    restart: unless-stopped
    deploy: { resources: { limits: { cpus: "${EXAMPLE_CPUS:-1}", memory: "${EXAMPLE_MEMORY:-512m}" } } }
    environment:
      <<: *lean-service-env            # RabbitMQ__Uri, S3__*, LEAN_DATA_FOLDER, OTEL_*
      Service__Name: example
      ConnectionStrings__Example: Host=postgres;Database=example;Username=example;Password=${POSTGRES_EXAMPLE_PASSWORD:-lean-dev-example}
    depends_on: { infra-ready: { condition: service_healthy } }
    # volumes: [lean-data:/Lean/Data:ro]   # read-only unless the service is market-data
    ports: ["127.0.0.1:${EXAMPLE_PORT:-5086}:8080"]
    healthcheck: { <<: *healthcheck, test: ["CMD", "lean-healthcheck", "8080", "/healthz"] }
    networks: [lean]
```

A database of its own: a login role and database in `deploy/compose/postgres/initdb/` (first start only) and in the
chart's infra subchart, and migrations the service applies at startup. Buckets: `deploy/compose/minio/minio-bootstrap.sh`.
Put new variables in `.env.example` with a comment and regenerate the reference:
`python3 docs/operations/gen_config_reference.py`. If Prometheus should scrape a `/metrics` endpoint directly, add it
to `deploy/compose/observability/prometheus.yml`; OTLP metrics need nothing.

## 5. Helm

Add the service under `services:` in `deploy/helm/lean-platform/values.yaml` (`replicas`, `resources`, `env`, and
`data: ro` if it mounts `lean-data`);
`templates/services.yaml` renders a Deployment + Service with `/healthz`/`/readyz` probes and the init containers that
wait for the infrastructure. Run `make helm-lint`.

## 6. Tests and verification

Unit/integration tests go in `Tests/Services/<Name>/` (the `QuantConnect.Tests` project references the service; use
the in-memory bus, and `LEAN_TEST_RABBITMQ_URI` / `LEAN_TEST_POSTGRES` for the opt-in real-infrastructure tests). Add a
`deploy/compose/verify-<name>.sh` that drives the running service end to end, and list it in
`deploy/compose/README.md`.
