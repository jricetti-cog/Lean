# Adding a brokerage or a data source

The platform runs LEAN's own plug-in points unchanged (ADR 0001): a brokerage, a data downloader or a live data feed
is the same LEAN type name and assembly the monolith would load through `Composer`, made available in the right image
and selected by configuration. No engine code changes.

## Where `Composer` looks

Engine processes (backtest and live) run `/Lean/Launcher/bin/Release/QuantConnect.Lean.Launcher.dll` with
`composer-dll-directory` = that folder; market-data composes from its own `/app` folder plus the LEAN assemblies it
references. A plug-in assembly is found only if it sits in that folder, so a new brokerage or downloader means a new
image layer:

```dockerfile
# e.g. deploy/docker/live-worker-ib.Dockerfile, or a COPY in the engine-runtime stage of Services/Dockerfile
FROM ghcr.io/jricetti-cog/lean/live-worker:0.1.0
COPY --chown=app:app plugins/QuantConnect.Brokerages.InteractiveBrokers*.dll /Lean/Launcher/bin/Release/
```

Build the plug-in against the same LEAN version (`dotnet build` of its project referencing this repo's `Common`,
`Brokerages`, `Engine`), and point the image at it with `Orchestrator__LiveDeployment__Image` (Compose) or the
chart's live image setting. Python dependencies of a plug-in go into the image with `pip install`.

## A brokerage for live deployments

A live deployment names its brokerage in the request; the live-worker generates the engine config from it
(`LiveWorkspace.ConfigOverrides`):

```sh
curl -s -H 'X-Api-Key: lean-dev-api-key' -H 'Content-Type: application/json' http://127.0.0.1:5080/api/v1/live -d '{
  "algorithm": {"source": "builtin", "typeName": "BasicTemplateAlgorithm"},
  "brokerage": "PaperBrokerage",
  "dataQueueHandlers": ["QuantConnect.Services.EngineHandlers.BusDataQueueHandler"]
}'
```

| Request field | Engine config | Notes |
|---|---|---|
| `brokerage` | `live-mode-brokerage` | LEAN brokerage type name (`IBrokerage` / its `BrokerageFactory`). `PaperBrokerage` fills through the backtesting transaction handler; any other name uses `BrokerageTransactionHandler` |
| `dataQueueHandlers` | `data-queue-handler` | Default `BusDataQueueHandler` (market-data's fan-out on `lean.marketdata.live`). A brokerage that streams its own data lists its `IDataQueueHandler` here, alone or alongside the bus handler |
| `parameters` | algorithm parameters | as in the monolith |
| (always) | `BrokerageSetupHandler`, `LiveTradingResultHandler`, `LiveTradingDataFeed`, `LiveTradingRealTimeHandler`, bus job-queue/messaging handlers | |

Steps for a real brokerage:

1. Put the brokerage assembly (and its dependencies) next to the Launcher in the live-worker image, as above.
2. Provide its settings (account, credentials, gateway host) as the engine config keys the brokerage reads (the same
   keys as in `Launcher/config.json`, e.g. `ib-account`). **Gap today:** the live-worker builds the engine config only
   from the image's `Launcher/config.json` plus the overrides above, and job-orchestrator forwards only the variables in
   `Orchestrator__LiveDeployment__ForwardEnvironment` (broker, S3, OTLP) into the container. Until the live-worker
   also applies the shared `Worker__EngineConfig__<key>` section (the backtest worker does, `JobWorkspace.ConfigOverrides`),
   the options are a derived image whose `config.json` carries the non-secret keys, or that small change plus
   adding the secret variables to `ForwardEnvironment` (Compose) / the chart's secret. Never put credentials in the
   job request: it is stored in the jobs database and on the bus.
3. Market hours, symbol properties and brokerage models come from `lean-data` and LEAN as in the monolith; set the
   algorithm's brokerage model in the algorithm itself.
4. Verify with `deploy/compose/verify-live-worker.sh` adapted to the brokerage (paper/sandbox account first), then
   stop with `POST /api/v1/live/{id}/stop` (`liquidate: true` to flatten first).

Only `PaperBrokerage` is exercised by the platform's checks; live deployments of a real brokerage are untested here.

## A historical data source (market-data downloads)

market-data runs fetch tasks from `marketdata.fetch` (`POST /api/v1/data/fetches`), writes them into
`/Lean/Data/.staging/<taskId>/` with LEAN's `DownloaderDataProvider` path (`LeanDataWriter`, merged with the published
files) and renames them into place. Three modes:

| `mode` | Source | Setting |
|---|---|---|
| `download` | an `IDataDownloader` (a LEAN data vendor plug-in) | request `downloader`, else `MarketData__Fetch__Downloader`; none is connected by default |
| `fetch-files` | a folder with the `Data/` layout (e.g. an NFS export of a vendor's LEAN data) | `MarketData__Fetch__SourceFolder`; request `paths` relative to it |
| `generate-synthetic` | LEAN's random data generator | synthetic tickers only (real symbols are refused); high resolutions limited to `MarketData__Fetch__MaxSyntheticHighResolutionDays` (31) |

Adding a vendor:

1. Put the vendor's `IDataDownloader` assembly in the market-data image next to `QuantConnect.Services.MarketData.dll`
   (`/app`), plus any keys it reads as `LEAN_<KEY>` environment variables (market-data runs with `HostsLeanCode`, so
   `LEAN_POLYGON_API_KEY` sets LEAN config `polygon-api-key`).
2. Set `MarketData__Fetch__Downloader=<DownloaderTypeName>` on market-data (Compose `environment:`, Helm
   `services.market-data.env`), or pass `"downloader"` per request.
3. Request data and check it:

```sh
curl -s -H 'X-Api-Key: lean-dev-api-key' -H 'Content-Type: application/json' http://127.0.0.1:5080/api/v1/data/fetches -d '{"fetch": {
  "mode": "download", "securityType": "Equity", "market": "usa", "tickers": ["AAPL"],
  "resolution": "Daily", "startDate": "2024-01-02", "endDate": "2024-12-31"}}'
curl -s -H 'X-Api-Key: lean-dev-api-key' http://127.0.0.1:5080/api/v1/data/fetches/<id>    # queued -> running -> completed / failed
curl -s -H 'X-Api-Key: lean-dev-api-key' \
  'http://127.0.0.1:5080/api/v1/data/coverage?securityType=Equity&market=usa&ticker=AAPL&resolution=Daily&start=2024-01-02&end=2024-12-31'
```

With no downloader configured the task fails with `invalid-request` ("No data downloader configured"). On a fresh
stack the same request with `"mode": "generate-synthetic"`, a synthetic ticker (e.g. `SYNDOC`) and January 2024 completes
in a few seconds, and coverage for it answers `"ranges": [{"start": "2024-01-02", "end": "2024-01-31"}], "missing": []`.

Backtests read whatever is on the volume, exactly as the monolith reads `Data/`. Missing files stay missing
(`fetchMissingData` is accepted in the backtest spec but no fetch-on-miss data provider is deployed, ADR 0006): fetch
the data before submitting. Bulk conversions of vendor files (ToolBox converters) run as one-shot toolbox jobs:
`docker compose --profile toolbox run --rm toolbox <app> ...` with the input files in `TOOLBOX_INPUT_DIR`.

## A live data feed (market-data fan-out)

market-data holds one upstream `IDataQueueHandler` and republishes on `lean.marketdata.live`
(`md.<securityType>.<market>.<ticker>`); live deployments subscribe through `BusDataQueueHandler`.

| Setting | Default | |
|---|---|---|
| `MarketData__Live__DataQueueHandler` | `FakeDataQueue` | LEAN `IDataQueueHandler` type name; the default is LEAN's simulated feed |
| `MarketData__Live__Symbols` (`MARKET_DATA_LIVE_SYMBOLS`) | `Equity/usa/SPY,Forex/oanda/EURUSD,Crypto/coinbase/BTCUSD` | `<SecurityType>/<market>/<TICKER>` |
| `MarketData__Live__Resolution` (`MARKET_DATA_LIVE_RESOLUTION`) | `Second` | `Tick` publishes ticks, otherwise LEAN-aggregated bars |
| `MarketData__Live__Enabled` | `true` | |

Put the feed's assembly in the market-data image, set `MarketData__Live__DataQueueHandler` and its keys as `LEAN_*`
variables, and list the symbols. A brokerage that streams its own data does not need this: list its handler in the
deployment's `dataQueueHandlers` instead.
