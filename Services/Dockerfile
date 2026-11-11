#
#   LEAN services images
#
#   One multi-stage build produces every independently deployable LEAN service. Build a single target with
#       docker build -f Services/Dockerfile --target <target> -t lean-<target> .
#   or let docker-compose.yml build them all. Targets:
#       api       standalone REST service over QuantConnect.Api, fronts the internal services   (port 8080)
#       report    report generation service: backtest result json -> HTML report               (port 8080)
#       toolbox   ToolBox + DownloaderDataProvider batch job image                              (run to completion)
#       engine    Launcher + Engine laid out like the root Dockerfile, base for Research and the worker
#

ARG LEAN_FOUNDATION=quantconnect/lean:foundation

FROM ${LEAN_FOUNDATION} AS build
WORKDIR /src
COPY . .
RUN dotnet build Launcher/QuantConnect.Lean.Launcher.csproj -c Debug -nologo -v q /p:WarningLevel=0 && \
    dotnet build ToolBox/QuantConnect.ToolBox.csproj -c Debug -nologo -v q /p:WarningLevel=0 && \
    dotnet build DownloaderDataProvider/QuantConnect.DownloaderDataProvider.Launcher.csproj -c Debug -nologo -v q /p:WarningLevel=0
RUN dotnet publish Services/Api/QuantConnect.Services.Api.csproj -c Release -nologo -v q -o /out/api && \
    dotnet publish Services/Report/QuantConnect.Services.Report.csproj -c Release -nologo -v q -o /out/report

# Launcher + Engine with the same layout as the root Dockerfile, so DockerfileJupyter and existing tooling work unchanged
FROM ${LEAN_FOUNDATION} AS engine
COPY ./Data/ /Lean/Data/
COPY --from=build /src/Launcher/bin/Debug/ /Lean/Launcher/bin/Debug/
ENV LEAN_DATA_FOLDER=/Lean/Data/
WORKDIR /Lean/Launcher/bin/Debug
ENTRYPOINT [ "dotnet", "QuantConnect.Lean.Launcher.dll" ]

FROM ${LEAN_FOUNDATION} AS api
COPY --from=build /out/api/ /Lean/Services/Api/
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    LEAN_DATA_FOLDER=/Lean/Data/
EXPOSE 8080
WORKDIR /Lean/Services/Api
HEALTHCHECK --interval=15s --timeout=5s --retries=5 CMD curl -sf http://localhost:8080/health || exit 1
ENTRYPOINT [ "dotnet", "QuantConnect.Services.Api.dll" ]

FROM ${LEAN_FOUNDATION} AS report
COPY ./Data/ /Lean/Data/
COPY --from=build /out/report/ /Lean/Services/Report/
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    LEAN_DATA_FOLDER=/Lean/Data/ \
    LEAN_REPORT_STORAGE_FOLDER=/Lean/Reports/
VOLUME /Lean/Reports
EXPOSE 8080
WORKDIR /Lean/Services/Report
HEALTHCHECK --interval=15s --timeout=5s --retries=5 CMD curl -sf http://localhost:8080/health || exit 1
ENTRYPOINT [ "dotnet", "QuantConnect.Services.Report.dll" ]

# Batch jobs: "toolbox --app=RandomDataGenerator ..." or "downloader --data-type=Trade ..." (see lean-job.sh)
FROM ${LEAN_FOUNDATION} AS toolbox
COPY ./Data/ /Lean/Data/
COPY --from=build /src/ToolBox/bin/Debug/ /Lean/ToolBox/bin/Debug/
COPY --from=build /src/DownloaderDataProvider/bin/Debug/ /Lean/DownloaderDataProvider/bin/Debug/
COPY ./Services/ToolBox/lean-job.sh /usr/local/bin/lean-job
RUN chmod +x /usr/local/bin/lean-job
ENV LEAN_DATA_FOLDER=/Lean/Data/
VOLUME /Lean/Data
ENTRYPOINT [ "lean-job" ]
CMD [ "help" ]
