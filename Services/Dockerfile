# syntax=docker/dockerfile:1
#
# Shared multi-stage image for every LEAN platform service (plan step s2.2, ADR 0001 "Image base").
# One build stage restores and builds once; each service is a runtime stage picked with --target:
#
#   docker buildx bake -f Services/docker-bake.hcl                 # every service image, one command
#   docker build -f Services/Dockerfile --target results .         # a single service
#
# Engine-hosting images (backtest-worker, live-worker, market-data, report, research) derive from
# quantconnect/lean:foundation and keep pythonnet + the Python stack; API services (api-gateway,
# job-orchestrator, results, optimizer) run on the slim ASP.NET image. All run as the non-root `app`
# user (uid/gid 1654, the same as the ASP.NET images) and carry a HEALTHCHECK on GET /healthz.
#
# A service whose project (Services/QuantConnect.Services.<Name>/) does not exist yet is packaged with
# QuantConnect.Services.Placeholder under the same assembly name, so its image already builds and passes
# its health check; adding the real project replaces the placeholder with no change to this file.

ARG LEAN_FOUNDATION=quantconnect/lean:foundation
ARG ASPNET_BASE=mcr.microsoft.com/dotnet/aspnet:10.0

# ---------------------------------------------------------------------------------------------------
# build: restore + build the engine and every service project once, publish each service to /out
# ---------------------------------------------------------------------------------------------------
FROM ${LEAN_FOUNDATION} AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY . .
RUN --mount=type=cache,id=lean-nuget,target=/root/.nuget/packages <<'EOF'
set -eu
dotnet new sln --name images --output /tmp/images
dotnet sln /tmp/images/images.slnx add \
    Launcher/QuantConnect.Lean.Launcher.csproj \
    Report/QuantConnect.Report.csproj \
    $(ls Services/*/*.csproj | grep -v -i 'tests\.csproj$')
dotnet restore /tmp/images/images.slnx
dotnet build /tmp/images/images.slnx -c Release --no-restore
# service image name -> assembly/project name
for entry in api-gateway:Gateway job-orchestrator:Orchestrator results:Results optimizer:Optimizer \
             backtest-worker:BacktestWorker live-worker:LiveWorker market-data:MarketData report:Report; do
    service="${entry%%:*}"; name="QuantConnect.Services.${entry#*:}"
    project="Services/${name}/${name}.csproj"
    if [ -f "$project" ]; then
        dotnet publish "$project" -c Release --no-build -o "/out/services/${service}"
    else
        echo "${project} not found: packaging the placeholder host as ${name}"
        dotnet publish Services/QuantConnect.Services.Placeholder/QuantConnect.Services.Placeholder.csproj \
            -c Release --no-restore -p:PlaceholderAssemblyName="${name}" -o "/out/services/${service}"
    fi
done
EOF
# Engine-hosting images run the stock Launcher with the bus handlers selected by config only
# (job-queue-handler / messaging-handler, ADR 0001): lay the adapter and its dependencies next to
# QuantConnect.Lean.Launcher.dll. Its newer Microsoft.Extensions.* overwrite the Launcher's (backward compatible).
RUN cp -f Services/QuantConnect.Services.EngineHandlers/bin/Release/*.dll Launcher/bin/Release/

# ---------------------------------------------------------------------------------------------------
# Health probe shared by every runtime image: bash /dev/tcp, so the slim image needs no curl/wget
# ---------------------------------------------------------------------------------------------------
FROM scratch AS healthcheck
COPY --chmod=755 <<'EOF' /lean-healthcheck
#!/bin/bash
# lean-healthcheck [port] [path]: exit 0 when GET http://127.0.0.1:<port><path> answers 2xx.
port="${1:-${HEALTHCHECK_PORT:-8080}}"
path="${2:-${HEALTHCHECK_PATH:-/healthz}}"
exec 3<>"/dev/tcp/127.0.0.1/${port}" || exit 1
printf 'GET %s HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n' "$path" >&3
IFS=' ' read -r -t 5 _ status _ <&3 || exit 1
[[ "$status" == 2* ]]
EOF

# ---------------------------------------------------------------------------------------------------
# engine-runtime: foundation (.NET + pythonnet + Python stack) + the Release engine, non-root
# ---------------------------------------------------------------------------------------------------
FROM ${LEAN_FOUNDATION} AS engine-runtime
RUN groupadd --gid 1654 app \
    && useradd --uid 1654 --gid app --create-home --home-dir /home/app --shell /bin/bash app \
    && mkdir -p /Lean/Data /work \
    && chown app:app /work
COPY --from=healthcheck /lean-healthcheck /usr/local/bin/lean-healthcheck
COPY --from=build /src/Launcher/bin/Release/ /Lean/Launcher/bin/Release/
COPY --from=build /src/Report/bin/Release/ /Lean/Report/bin/Release/
# lean-data is mounted read-only at /Lean/Data/ (ADR 0004); LEAN_* variables map onto LEAN Config.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_HTTP_PORTS=8080 \
    HOME=/home/app \
    PYTHONPATH=/Lean/Launcher/bin/Release/ \
    LEAN_DATA_FOLDER=/Lean/Data/ \
    LEAN_COMPOSER_DLL_DIRECTORY=/Lean/Launcher/bin/Release/
# /Lean is read-only for `app`: launcher processes run with a per-job working directory under /work, which is
# where LEAN writes log.txt, results and the object store (`--config <job>/config.json`, ADR 0001).
WORKDIR /work
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 CMD ["lean-healthcheck"]

# ---------------------------------------------------------------------------------------------------
# aspnet-runtime: slim ASP.NET image for services that never load Engine (ADR 0001 §"Confirmed")
# ---------------------------------------------------------------------------------------------------
FROM ${ASPNET_BASE} AS aspnet-runtime
COPY --from=healthcheck /lean-healthcheck /usr/local/bin/lean-healthcheck
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /app
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["lean-healthcheck"]

# ---------------------------------------------------------------------------------------------------
# Service targets
# ---------------------------------------------------------------------------------------------------
FROM aspnet-runtime AS api-gateway
COPY --from=build /out/services/api-gateway/ /app/
ENV Service__Name=api-gateway OTEL_SERVICE_NAME=api-gateway
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.Gateway.dll"]

FROM aspnet-runtime AS job-orchestrator
COPY --from=build /out/services/job-orchestrator/ /app/
ENV Service__Name=job-orchestrator OTEL_SERVICE_NAME=job-orchestrator
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.Orchestrator.dll"]

FROM aspnet-runtime AS results
COPY --from=build /out/services/results/ /app/
ENV Service__Name=results OTEL_SERVICE_NAME=results
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.Results.dll"]

# Optimizer submits backtests through job-orchestrator and references only Common/Configuration/Logging,
# so it runs on the slim image (ADR 0001).
FROM aspnet-runtime AS optimizer
COPY --from=build /out/services/optimizer/ /app/
ENV Service__Name=optimizer OTEL_SERVICE_NAME=optimizer
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.Optimizer.dll"]

# Workers supervise one QuantConnect.Lean.Launcher.dll process per job from /Lean/Launcher/bin/Release/.
FROM engine-runtime AS backtest-worker
COPY --from=build /out/services/backtest-worker/ /app/
ENV Service__Name=backtest-worker OTEL_SERVICE_NAME=backtest-worker
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.BacktestWorker.dll"]

FROM engine-runtime AS live-worker
COPY --from=build /out/services/live-worker/ /app/
ENV Service__Name=live-worker OTEL_SERVICE_NAME=live-worker
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.LiveWorker.dll"]

# market-data is the only writer to lean-data: it mounts the volume read-write (ADR 0004).
FROM engine-runtime AS market-data
COPY --from=build /out/services/market-data/ /app/
ENV Service__Name=market-data OTEL_SERVICE_NAME=market-data
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.MarketData.dll"]

FROM engine-runtime AS report
COPY --from=build /out/services/report/ /app/
ENV Service__Name=report OTEL_SERVICE_NAME=report
ENTRYPOINT ["dotnet", "/app/QuantConnect.Services.Report.dll"]

# research: JupyterLab + QuantBook on the same engine build (DockerfileJupyter, as the non-root user).
FROM engine-runtime AS research
USER root
RUN if [ "$(uname -m)" = "aarch64" ]; then \
        tini_binary="tini-arm64"; \
        tini_sha256="07952557df20bfd2a95f9bef198b445e006171969499a1d361bd9e6f8e5e0e81"; \
    else \
        tini_binary="tini-amd64"; \
        tini_sha256="93dcc18adc78c65a028a84799ecf8ad40c936fdfc5f2a57b1acda5a8117fa82c"; \
    fi \
    && wget --quiet -O /usr/local/bin/tini "https://github.com/krallin/tini/releases/download/v0.19.0/${tini_binary}" \
    && echo "${tini_sha256} */usr/local/bin/tini" | sha256sum -c - \
    && chmod +x /usr/local/bin/tini \
    && pip install --no-cache-dir clr-loader==0.2.9
COPY --chmod=755 <<'EOF' /usr/local/bin/lean-research
#!/bin/bash
set -e
mkdir -p "${NOTEBOOKS}"
if [ ! -f "${NOTEBOOKS}/config.json" ]; then
    cat > "${NOTEBOOKS}/config.json" <<JSON
{ "data-folder": "${LEAN_DATA_FOLDER}", "composer-dll-directory": "${LEAN_COMPOSER_DLL_DIRECTORY}",
  "algorithm-language": "Python", "messaging-handler": "QuantConnect.Messaging.Messaging",
  "job-queue-handler": "QuantConnect.Queues.JobQueue", "api-handler": "QuantConnect.Api.Api" }
JSON
fi
cd "${NOTEBOOKS}"
exec jupyter lab --ip=0.0.0.0 --port=8888 --no-browser --ServerApp.token='' --notebook-dir="${NOTEBOOKS}" "$@"
EOF
ENV NOTEBOOKS=/home/app/Notebooks \
    HEALTHCHECK_PORT=8888 \
    HEALTHCHECK_PATH=/api \
    Service__Name=research
USER app
RUN mkdir -p /home/app/.ipython/profile_default/startup /home/app/Notebooks \
    && ln -s /Lean/Launcher/bin/Release/start.py /home/app/.ipython/profile_default/startup/start.py
WORKDIR /home/app/Notebooks
EXPOSE 8888
ENTRYPOINT ["/usr/local/bin/tini", "--"]
CMD ["lean-research"]
