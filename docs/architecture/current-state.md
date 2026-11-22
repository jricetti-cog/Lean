# LEAN monolith: current state, seams and shared state

Baseline for the "monolith to Docker microservices" migration. It describes `master` as of `de116c0de`
(".NET 10 LEAN fork") and records what any split has to respect. Nothing here changes behaviour; the default
`Launcher/config.json` stays the reference configuration.

## 1. Solution and project references

`QuantConnect.Lean.sln` holds 23 projects. Five are executables (`OutputType Exe`); the rest are libraries.
Paths are relative to the repo root; references are `ProjectReference`s from each `.csproj`.

| Project | Path | Kind | References |
|---|---|---|---|
| QuantConnect.Logging | `Logging/` | lib | — |
| QuantConnect.Configuration | `Configuration/` | lib | Logging |
| QuantConnect.Compression | `Compression/` | lib | Logging |
| QuantConnect (Common) | `Common/` | lib | Compression, Configuration, Logging |
| QuantConnect.Indicators | `Indicators/` | lib | Common, Logging |
| QuantConnect.Api | `Api/` | lib | Common, Configuration, Logging |
| QuantConnect.Messaging | `Messaging/` | lib | Common, Configuration, Logging |
| QuantConnect.Brokerages | `Brokerages/` | lib | Api, Common, Compression, Configuration, Logging |
| QuantConnect.Queues | `Queues/` | lib | Brokerages, Common, Configuration, Logging |
| QuantConnect.Algorithm | `Algorithm/` | lib | Common, Configuration, Indicators, Logging |
| QuantConnect.Algorithm.Framework | `Algorithm.Framework/` | lib | Algorithm, Common, Indicators |
| QuantConnect.Algorithm.CSharp | `Algorithm.CSharp/` | lib | Algorithm, Algorithm.Framework, Common, Indicators |
| QuantConnect.Algorithm.Python | `Algorithm.Python/` | lib (copies `.py` files) | Algorithm, Common, Indicators |
| QuantConnect.AlgorithmFactory | `AlgorithmFactory/` | lib | Algorithm, Common, Configuration, Logging |
| QuantConnect.Lean.Engine | `Engine/` | lib | Algorithm, Algorithm.CSharp, AlgorithmFactory, Api, Brokerages, Common, Compression, Configuration, Indicators, Logging |
| QuantConnect.Optimizer | `Optimizer/` | lib | Common, Configuration, Logging |
| QuantConnect.Research | `Research/` | lib (+ notebooks) | Algorithm, Algorithm.Framework, Api, Common, Configuration, Engine, Indicators, Logging, Queues |
| **QuantConnect.Lean.Launcher** | `Launcher/` | **exe** | Algorithm, Algorithm.CSharp, AlgorithmFactory, Api, Brokerages, Common, Compression, Configuration, DownloaderDataProvider.Launcher, Engine, Indicators, Logging, Messaging, Optimizer.Launcher, Queues, Research, ToolBox |
| **QuantConnect.Optimizer.Launcher** | `Optimizer.Launcher/` | **exe** | Common, Configuration, Logging, Optimizer |
| **QuantConnect.Report** | `Report/` | **exe** | Algorithm, Api, Brokerages, Common, Configuration, Engine, Logging, Messaging, ToolBox |
| **QuantConnect.DownloaderDataProvider.Launcher** | `DownloaderDataProvider/` | **exe** | Common, Configuration, Engine, Logging |
| **QuantConnect.ToolBox** | `ToolBox/` | **exe** | Engine |
| QuantConnect.Tests | `Tests/` | NUnit | every project above except Algorithm.Python |

Layering, bottom to top:

```
Logging ─► Configuration / Compression ─► Common ─► Indicators / Api / Messaging
        ─► Brokerages ─► Queues
        ─► Algorithm ─► Algorithm.Framework ─► Algorithm.CSharp ; AlgorithmFactory
        ─► Engine  (pulls in Algorithm.CSharp and Brokerages)
        ─► Research / Report / ToolBox / DownloaderDataProvider
        ─► Launcher (references every other executable so their dlls land in Launcher/bin)
```

Observations that matter for a split:

* `Engine` references `Algorithm.CSharp` and `Brokerages` directly, so every process that needs any engine type
  (Report, ToolBox, DownloaderDataProvider, Research) also loads the regression algorithms and brokerage models.
* `Launcher` references the other executables only to co-locate their dlls in `Launcher/bin/<cfg>`. `Composer`
  discovers implementations by scanning that folder (section 3), so **a service image only sees the handlers
  whose dlls are in its folder**.
* `Messaging`, `Queues` and `Api` sit below `Engine` and are not referenced by it; the engine sees them only
  through interfaces in `Common/Interfaces` resolved by name at runtime. That is the cleanest seam in the
  codebase.
* `Common` already defines every handler interface except the engine-internal ones (`ISetupHandler`,
  `IResultHandler`, `IDataFeed`, `IRealTimeHandler`, `ITransactionHandler`, which live in `Engine/`).

## 2. Handlers resolved by name

`Launcher/config.json` is a layered file: top-level keys, then the block under `environments.<environment>`
(`"environment": "backtesting"` by default) overrides them. Handlers are type names resolved through
`Composer.Instance.GetExportedValueByTypeName<T>(name)` (full or short name). Resolution happens in:

* `Engine/LeanEngineSystemHandlers.cs` `FromConfiguration` — job queue, api, messaging, lean manager.
* `Engine/LeanEngineAlgorithmHandlers.cs` `FromConfiguration` — everything per-algorithm.
* `Engine/HistoricalData/HistoryProviderManager.cs` — `history-provider` list (copied onto the job packet by `Queues/JobQueue.cs`).
* `Engine/DataFeeds/DataQueueHandlerManager.cs` — `data-queue-handler` list (live only, via the job packet).

### 2.1 The ten handlers in scope

| Config key | Interface (file) | Default in `config.json` (backtesting) | Live default | Implementations in repo |
|---|---|---|---|---|
| `job-queue-handler` | `IJobQueueHandler` (`Common/Interfaces/IJobQueueHandler.cs`) | `QuantConnect.Queues.JobQueue` | same | `JobQueue` (`Queues/JobQueue.cs`) — builds a `BacktestNodePacket`/`LiveNodePacket` from `Config`, reads the algorithm file with `File.ReadAllBytes(algorithm-location)` |
| `messaging-handler` | `IMessagingHandler` (`Common/Interfaces/IMessagingHandler.cs`) | `QuantConnect.Messaging.Messaging` | same | `Messaging` (`Messaging/Messaging.cs`, logs packets + sends notifications), `StreamingMessageHandler` (`Messaging/StreamingMessageHandler.cs`, NetMQ push), `EventMessagingHandler` (`Messaging/EventMessagingHandler.cs`, in-process events) |
| `api-handler` | `IApi` (`Common/Interfaces/IApi.cs`) | `QuantConnect.Api.Api` | same | `Api` (`Api/Api.cs`, QuantConnect cloud REST client) |
| `data-provider` | `IDataProvider` (`Common/Interfaces/IDataProvider.cs`) | `QuantConnect.Lean.Engine.DataFeeds.DefaultDataProvider` | same | `DefaultDataProvider` (local disk), `ApiDataProvider` (download from QC then disk), `DownloaderDataProvider` (vendor download then disk), `ProcessedDataProvider`, `CompositeDataProvider` — all `Engine/DataFeeds/` (`BackupUniverseFileDataProvider` is test-only) |
| `setup-handler` | `ISetupHandler` (`Engine/Setup/ISetupHandler.cs`) | `…Setup.BacktestingSetupHandler` | `…Setup.BrokerageSetupHandler` | `BacktestingSetupHandler`, `BrokerageSetupHandler` (+ `BaseSetupHandler` helpers) |
| `result-handler` | `IResultHandler` (`Engine/Results/IResultHandler.cs`) | `…Results.BacktestingResultHandler` | `…Results.LiveTradingResultHandler` | `BacktestingResultHandler`, `LiveTradingResultHandler` (both on `BaseResultsHandler`), `RegressionResultHandler` (tests) |
| `data-feed-handler` | `IDataFeed` (`Engine/DataFeeds/IDataFeed.cs`) | `…DataFeeds.FileSystemDataFeed` | `…DataFeeds.LiveTradingDataFeed` | `FileSystemDataFeed`, `LiveTradingDataFeed` (subclass), `NullDataFeed` (`MockDataFeed` is test-only) |
| `real-time-handler` | `IRealTimeHandler` (`Engine/RealTime/IRealTimeHandler.cs`) | `…RealTime.BacktestingRealTimeHandler` | `…RealTime.LiveTradingRealTimeHandler` | `BacktestingRealTimeHandler` (on `BaseRealTimeHandler`), `LiveTradingRealTimeHandler` (subclass) |
| `history-provider` (list) | `IHistoryProvider` (`Common/Interfaces/IHistoryProvider.cs`) | `[SubscriptionDataReaderHistoryProvider]` | `[BrokerageHistoryProvider, SubscriptionDataReaderHistoryProvider]` | `HistoryProviderManager` (composite, the one the engine actually holds), `SubscriptionDataReaderHistoryProvider`, `BrokerageHistoryProvider` (both on `SynchronizingHistoryProvider`), plus brokerage plugins (e.g. IQFeed) |
| `transaction-handler` | `ITransactionHandler` (`Engine/TransactionHandlers/ITransactionHandler.cs`) | `…TransactionHandlers.BacktestingTransactionHandler` | `…BrokerageTransactionHandler` (`live-paper` keeps `BacktestingTransactionHandler`) | `BrokerageTransactionHandler`, `BacktestingTransactionHandler` (subclass) |

### 2.2 Other name-resolved plug points

Not in the ticket list but resolved the same way, and each one is a potential seam or trap:

| Config key | Interface | Default | Notes |
|---|---|---|---|
| `lean-manager-type` | `ILeanManager` | `LocalLeanManager` (`Engine/Server/LocalLeanManager.cs`) | Called every loop iteration (`leanManager.Update()`) |
| `map-file-provider` | `IMapFileProvider` | `LocalDiskMapFileProvider` | `LocalZipMapFileProvider` goes through `IDataProvider` (section 4.2) |
| `factor-file-provider` | `IFactorFileProvider` | `LocalDiskFactorFileProvider` | `LocalZipFactorFileProvider` goes through `IDataProvider` |
| `object-store` | `IObjectStore` | `LocalObjectStore` (`Engine/Storage/`) | Root from static `LocalObjectStore.DefaultObjectStore` = `object-store-root` (`./storage`) |
| `data-permission-manager` | `IDataPermissionManager` | `DataPermissionManager` | |
| `data-monitor` | `IDataMonitor` | `QuantConnect.Data.DataMonitor` | Strips `Globals.DataFolder` from paths |
| `data-channel-provider` | `IDataChannelProvider` | `DataChannelProvider` | |
| `data-aggregator` | `IDataAggregator` | `AggregationManager` | Live consolidation |
| `data-queue-handler` (list) | `IDataQueueHandler` | `LiveDataQueue` (live-paper) or the brokerage | Wrapped by `DataQueueHandlerManager` |
| `live-mode-brokerage` | `IBrokerageFactory` / `IBrokerage` | `PaperBrokerage` (live-paper) | Brokerage plugins are separate repos/dlls |
| `log-handler` | `ILogHandler` | `CompositeLogHandler` | Assigned to static `Log.LogHandler` |
| `optimization-launcher`, `optimization-strategy` | `LeanOptimizer`, `IOptimizationStrategy` | `ConsoleLeanOptimizer`, `GridSearchOptimizationStrategy` | Optimizer.Launcher only |

### 2.3 Runtime wiring (one process, one algorithm)

`Launcher/Program.cs` → `Initializer.Start()` (logging) → `GetSystemHandlers()` → `JobQueue.NextJob()` →
`GetAlgorithmHandlers()` → `new Engine.Engine(...).Run(job, algorithmManager, …)`. Inside
`Engine/Engine.cs` `Run`:

1. `Results.Initialize` (gets messaging, api, transactions), `Setup.CreateAlgorithmInstance` (AlgorithmFactory
   `Loader`, Python via pythonnet), `ObjectStore` / `DataPermissionsManager.Initialize`.
2. Builds `DataManager`, `UniverseSelection`, `Synchronizer`/`LiveSynchronizer`; `DataFeed.Initialize(algorithm,
   job, results, mapFileProvider, factorFileProvider, dataProvider, dataManager, synchronizer, permissions)`;
   `HistoryProviderManager.Initialize(...)`.
3. `algorithm.Initialize()`; `Setup.Setup(...)` (brokerage connect, cash/holdings); `Transactions.Initialize`,
   `RealTime.Setup`.
4. `Isolator.ExecuteWithTimeLimit(...)` runs `AlgorithmManager.Run` on the `WorkerThread`, enforcing RAM and
   time limits; `Results.Exit()`, handlers disposed.

The result handler and the transaction handler each run their own thread; the data feed runs worker tasks on
`WeightedWorkScheduler` (static singleton). Everything shares the single `IAlgorithm` object.

## 3. Entry points

| Entry point | Main | What it does | Handlers / state it uses |
|---|---|---|---|
| `Launcher` | `Launcher/Program.cs` | Backtest or live, one algorithm per process. `--config`, `--key value` args merge into `Config`. | All handlers above; `PythonInitializer`; `Isolator` |
| `Optimizer.Launcher` | `Optimizer.Launcher/Program.cs`, `ConsoleLeanOptimizer.cs` | Parameter search. **Already multi-process**: spawns one `dotnet QuantConnect.Lean.Launcher.dll --results-destination-folder … --parameters …` per backtest (`lean-binaries-location`) and reads results back from disk. | `Config`, `Composer` (strategy), filesystem results |
| `Report` | `Report/Program.cs` | Reads backtest/live result JSON from disk, renders HTML/PDF. | Calls `LeanEngineAlgorithmHandlers.FromConfiguration` (map/factor/data providers), `PythonInitializer.Initialize()` (charts via matplotlib), `Globals.DataFolder` for `PortfolioLooper` |
| `DownloaderDataProvider` | `DownloaderDataProvider/Program.cs` | Download vendor data with an `IDataDownloader` and write LEAN zips with `LeanDataWriter` into `Globals.DataFolder`. | `data-provider` hard-coded to `DefaultDataProvider`, map/factor providers, `Composer` |
| `ToolBox` | `ToolBox/Program.cs` | Batch converters/generators (`--app=RandomDataGenerator`, `CoarseUniverseGenerator`, `FactorFileGenerator`, …). Writes into `Globals.DataFolder`. | `data-provider`, map/factor providers from `Config` |
| `Research` | `Research/QuantBook.cs`, `Research/start.py`, `DockerfileJupyter` | Jupyter `QuantBook` inside a Python kernel; `start.py` loads CoreCLR with `QuantConnect.Lean.Launcher.runtimeconfig.json`. | `QuantBook` calls `Composer.Instance.Reset()` + `Config.Reset()` then builds system and algorithm handlers with `researchMode: true` |

Container images today: `Dockerfile` (`FROM quantconnect/lean:foundation`, entrypoint `QuantConnect.Lean.Launcher.dll`),
`DockerfileJupyter` (`FROM quantconnect/lean:$LEAN_TAG`), `DockerfileLeanFoundation(ARM)` (builds the foundation
image itself: .NET, Python 3.11, pythonnet, the Python scientific stack).

## 4. Constraints any split must respect

### 4.1 Static, process-wide state

| State | Where | Set from | Consequence |
|---|---|---|---|
| `Config` | `Configuration/Config.cs` — `static class`, `Lazy<JObject> Settings`, `Set`, `Reset`, `SetConfigurationFile`, `MergeCommandLineArgumentsWithConfiguration` | `config.json` + CLI | One configuration per process. Two jobs cannot share a process unless they share every key. Tests leak `Config.Set` between fixtures. A service host must apply its settings before anything touches `Config` (lazy) or call `Config.Reset()`. |
| `Globals` | `Common/Globals.cs` — `DataFolder`, `CacheDataFolder`, `ResultsDestinationFolder`, `LiveMode`, `UserId`, `UserToken`, `ProjectId`, `OrganizationID`, `Version` | `Globals.Reset()` in the static ctor, reading `Config` | Captured once at type init. `data-folder` default `../../../Data/` is **relative to the working directory**; any process not started in `Launcher/bin/<cfg>` needs an absolute `data-folder`. Changing `Config` later needs `Globals.Reset()`. |
| `Composer.Instance` | `Common/Util/Composer.cs` — `Lazy<Composer>` | `composer-dll-directory`, `plugin-directory` | Scans `*.dll` in the base directory once and caches exported parts (MEF). Handler implementations must be in the scanned folder; resolved instances are cached singletons unless `Reset()`. Research resets it per `QuantBook`. |
| `Log.LogHandler`, `Log.DebuggingEnabled`, `Log.FilePath` | `Logging/Log.cs` | `log-handler` | One log sink per process. |
| `PythonInitializer` | `Common/Python/PythonInitializer.cs` | `python-venv`, algorithm location | One Python runtime (and one GIL) per process; `Shutdown()` cannot be followed by a re-`Initialize()`. |
| Lazy databases | `MarketHoursDatabase.FromDataFolder()`, `SymbolPropertiesDatabase.FromDataFolder()`; static fields in `DividendYieldProvider`, `FuturesExpiryUtilityFunctions`, `OptionStrategies`, `RangeConsolidator`, `CurrencyPairUtil`, `Collective2SignalExport` | `Globals.DataFolder` at first touch | Loaded once from disk and kept for the process lifetime (refreshed on `AlgorithmSettings.DatabasesRefreshPeriod`). Some are captured in static initializers, i.e. at first type use, before a host might have set `data-folder`. |
| Caches | `SecurityIdentifier` SID cache, `SymbolCache`, `OptionUniverse`/`FutureUniverse` symbol caches, `FundamentalInstanceProvider`, `FutureMarginModel` margin cache, `TextSubscriptionDataSourceReader` cache | — | Process-wide, unbounded or global; fine for one algorithm per process, a leak/correctness risk for a long-lived multi-job host. |
| `WeightedWorkScheduler.Instance` | `Engine/DataFeeds/WorkScheduling/` | — | One data-loading thread pool per process. |
| `LocalObjectStore.DefaultObjectStore` | `Engine/Storage/LocalObjectStore.cs` | `object-store-root` | Static path. |
| `JsonConvert.DefaultSettings` | overwritten by `Api.Initialize` | — | Global serializer settings change when an `Api` is constructed. |

**Rule for the migration:** one algorithm per OS process. A worker service that wants to run several jobs
must spawn a `Launcher` process (or container) per job, exactly as `ConsoleLeanOptimizer` already does.
In-process multi-tenancy would require removing every row of the table above.

### 4.2 Files read straight from disk instead of `IDataProvider`

`IDataProvider.Fetch(key)` is the only abstraction for data access, and the keys are absolute paths under
`Globals.DataFolder`. Price data (`TradeBar`/`QuoteBar`/`Tick`/`OpenInterest.GetSource` →
`SubscriptionDataReader` → `ZipDataCacheProvider` → `IDataProvider`) goes through it. These do **not**:

| Data | Code | How it is read |
|---|---|---|
| Market hours | `Common/Securities/MarketHoursDatabase.cs` `FromDataFolder` | `File.ReadAllText(Globals.GetDataFolderPath("market-hours")/market-hours-database.json)` |
| Symbol properties | `Common/Securities/SymbolPropertiesDatabase.cs` `FromDataFolder` | `File.Exists`/read of `symbol-properties/symbol-properties-database.csv` |
| Security definitions | `Common/Securities/SecurityDefinitionSymbolResolver.cs` | default key `symbol-properties/security-database.csv` |
| Map files (default provider) | `Common/Data/Auxiliary/LocalDiskMapFileProvider.cs` | `Directory.Exists` + enumerate `<market>/map_files/` on disk; only the per-file lines go through `IDataProvider` |
| Factor files (default provider) | `Common/Data/Auxiliary/LocalDiskFactorFileProvider.cs` | path from `Globals.GetDataFolderPath`, depends on the disk map file resolver |
| Future margins | `Common/Securities/Future/FutureMarginModel.cs` | `Globals.DataFolder/future/<market>/margins/*.csv` |
| Interest rates | `Common/Data/InterestRateProvider.cs` | `alternative/interest-rate/usa/` |
| Shortable lists | `Common/Data/Shortable/LocalDiskShortableProvider.cs` | `<type>/<market>/shortable/` |
| Coarse fundamentals | `Common/Data/UniverseSelection/CoarseFundamentalDataProvider.cs` | `equity/usa/fundamental/coarse/` |

`LocalZipMapFileProvider` / `LocalZipFactorFileProvider` do go through `IDataProvider.Fetch` and are the
remote-friendly alternatives. Writers (`DownloaderDataProvider`, `ToolBox`, `LeanDataWriter`,
`ApiDataProvider`) always write into `Globals.DataFolder` on local disk.

**Consequence:** an engine process needs a real filesystem at `data-folder` holding at least `market-hours/`,
`symbol-properties/`, `*/map_files/` and `*/factor_files/`, whatever `data-provider` says. The plan's shared
read-only `lean-data` volume satisfies this; a pure network data provider does not on its own.

### 4.3 pythonnet needs the foundation image

* Python algorithms, `Report` (charts), `Research` and any code path that calls `PythonInitializer.Initialize()`
  need the CPython + pythonnet + scientific stack baked into `quantconnect/lean:foundation`
  (`DockerfileLeanFoundation`).
* On a plain host, `dotnet test` and the Report entry point crash in pythonnet ("GIL must always be released"),
  even for C#-only filters. Tests must run as
  `docker run --rm -v $PWD:$PWD -w $PWD/Tests/bin/Release quantconnect/lean:foundation dotnet test QuantConnect.Tests.dll …`
  with the repo mounted at the path it was built at.
* Any service image that loads `Engine`, `Report` or `Research` (directly or through `Composer` scanning) should
  derive from the foundation image. Services that only reference `Common`/`Api`/contracts can use a slim
  ASP.NET image, but must not pull `Engine` transitively.
* One GIL per process: Python-heavy work (report charts, Python algorithms) does not parallelise inside a
  process; scale by replicas.

### 4.4 What the per-tick loop touches

`AlgorithmManager.Run` (`Engine/AlgorithmManager.cs`) iterates `synchronizer.StreamData()` time slices. Per
slice it synchronously calls, in order:

| Step | Touches |
|---|---|
| `leanManager.Update()` | `ILeanManager` |
| `realtime.ScanPastEvents(time)`, `algorithm.SetDateTime(time)` | `IRealTimeHandler` (scheduled events), algorithm clock |
| security changes | `leanManager`, `realtime`, `results`.`OnSecuritiesChanged`, `UniverseManager` |
| `security.Update(...)` per data point | `SecurityManager`, `Security` cache, fill-forward flags |
| settlement scan, `cash.Update()`, `Portfolio.InvalidateTotalPortfolioValue()` | `SecurityPortfolioManager`, `CashBook`, currency conversions |
| `transactions.GetOpenOrderTickets`, `transactions.ProcessSynchronousEvents()` | `ITransactionHandler` (backtest fills happen here; in-memory order book) |
| `realtime.SetTime` | `IRealTimeHandler` |
| margin calls | `MarginCallModel`, transactions |
| `HandleDividends` / `HandleSplits` / delistings | Portfolio, open orders, factor-adjusted prices |
| consolidators `Update` | `SubscriptionManager` consolidators, indicators |
| `algorithm.OnData(slice)`, `OnFrameworkData` | user code (C# or Python under the GIL) |
| `transactions.ProcessSynchronousEvents()`, `results.ProcessSynchronousEvents()`, `algorithm.OnEndOfTimeStep()` | transactions, `IResultHandler` (sampling, charts, statistics) |

Upstream of the loop, the data feed reads files through `IDataProvider`/`ZipDataCacheProvider` and the static
market-hours/symbol-properties/map/factor data. Results are flushed by the result handler thread via
`IMessagingHandler.Send(packet)` and written with `File.WriteAllText` into `Globals.ResultsDestinationFolder`.

**Consequence:** every per-tick dependency is an in-memory object call on shared mutable state
(`IAlgorithm`, `Portfolio`, `Securities`, `Transactions`). The loop, data feed, transaction handler, real-time
handler and result handler sampling must stay in one process. The safe out-of-process seams are the ones the
loop only talks to asynchronously or at start/end:

* **job intake** — `IJobQueueHandler` (start of process);
* **result/log/status egress** — `IMessagingHandler.Send` (already off the loop, on the result thread) and the
  result files written at the end;
* **raw file fetch** — `IDataProvider.Fetch`, *if* cached locally (it is called on data-feed worker threads,
  not per tick, but a cold remote fetch stalls the feed);
* **live market data in** — `IDataQueueHandler` (already asynchronous);
* **API / object store** — `IApi`, `IObjectStore` (rare, user-initiated);
* **peripheral executables** — Optimizer, Report, ToolBox, DownloaderDataProvider, Research are already
  separate processes and share only the data folder and result files.

## 5. Earlier unmerged branches

Both branches are single-PR attempts based on `de116c0de` (current `master`), authored 2026-11-12. They both add
a top-level `Services/` tree, a root `docker-compose.yml`, edit `QuantConnect.Lean.sln`, `Tests/QuantConnect.Tests.csproj`
and `readme.md`, so **they conflict with each other** and cannot both be merged as-is.

### 5.1 `origin/devin/1794435663-phase1-microservices` (12 commits, +5.7k lines)

Adds a NetMQ message bus (`Messaging/Bus/*`), versioned contracts (`Common/Services/*`: `MessageEnvelope`,
`marketdata.v1`, `jobs.v1`), `RemoteDataProvider`/`RemoteDataQueueHandler` (`Engine/DataFeeds/Remote`),
`BusMessagingHandler`, and four services: `MessageBroker`, `MarketData`, `ApiGateway`, `BacktestWorker`;
docs in `Documentation/microservices-phase1.md`.

Worth salvaging:

* The boundary analysis: loop stays in one process, worker spawns one `Launcher` process per job with a
  generated `config.json` (`BacktestWorker.WriteEngineConfiguration` overriding `data-provider`,
  `messaging-handler`, `data-folder`, `results-destination-folder`, `close-automatically`).
* `RemoteDataProvider` as a **read-through cache** into a local data folder, and its
  `DefaultReferenceData` list (`market-hours/`, `symbol-properties/`, `*/map_files/`, `*/factor_files/`) that is
  pre-synced because of section 4.2. Key validation that rejects paths outside the data root.
* `BusMessagingHandler` pattern: subclass `Messaging` and publish packets as job events; requires
  `Messaging.Dispose` to become `virtual` (the only edit to existing code besides project files).
* The envelope fields (`id`, `correlationId`, `contractVersion`, headers, binary payload frame) and the
  versioning rule (additive in `v1`, breaking → `v2` topics side by side).
* `RemoteBacktestParityTests`: run regression algorithms with an empty data folder filled only through the
  service and assert identical statistics including `OrderListHash`. This is the parity harness shape to keep.
* Cancellation/process-exit edge cases fixed in later commits (cancel before start, ignore cancel after exit).

Avoid:

* NetMQ XSUB/XPUB broker and a home-grown request/reply layer — the approved plan uses **RabbitMQ**. Keep the
  contracts, replace the transport.
* `Engine` → `Messaging` project reference added for the remote handlers. Put bus-backed handlers in their own
  project (e.g. `Services/*` or an `Engine.Remote` library) so `Engine` keeps its current dependency set.
* In-memory job queue and lease without reassignment in the gateway — the plan uses PostgreSQL.
* Gateway re-implementing QuantConnect API auth (`Basic userId:sha256(token:timestamp)`) and serving a static
  `wwwroot/index.html` dashboard — the plan has a React+Vite console and an OpenAPI gateway.
* Test project referencing service executables (`Tests` → `Services/*`), which pulls ASP.NET hosts into the
  39k-test assembly; prefer per-service test projects.
* compose that bind-mounts the whole repo into `quantconnect/lean:foundation` and runs host-built `bin/Release`
  dlls instead of building images.

### 5.2 `origin/devin/1794438819-phase1-peripheral-services` (1 commit, +2.5k lines)

Adds `Services/Shared` (`LeanServiceHost`, `LeanServiceConfiguration`), `Services/Api` (REST façade over the
`QuantConnect.Api` cloud client), `Services/Report` (HTTP wrapper around `Report.Report`), `Services/ToolBox/lean-job.sh`,
a multi-stage `Services/Dockerfile` (targets `api`, `report`, `toolbox`, `engine`), `Services/k8s/*` kustomize
manifests, `DockerfileJupyter` `LEAN_BASE` build arg, and `Documentation/deployment-topology.md`.

Worth salvaging:

* The phase framing in `deployment-topology.md`: peripherals first, then messaging, then data, then workers;
  never split the `AlgorithmManager` loop.
* `LeanServiceConfiguration`: `LEAN_*` environment variables → `Config` keys (`LEAN_DATA_FOLDER` →
  `data-folder`), plus `LEAN_CONFIG_FILE`. Simple and container-friendly; must run before first `Config`
  access (section 4.1).
* `Services/Report` reusing `Report.Report`/`Compile` unchanged, with one compile at a time because of the GIL
  and scale-by-replicas.
* Multi-stage Dockerfile on `quantconnect/lean:foundation` with an `engine` target that keeps the root
  `Dockerfile` layout, and `DockerfileJupyter` `LEAN_BASE` (default unchanged) so Research runs the same build.
* ToolBox/Downloader as run-to-completion jobs writing to a shared `lean-data` volume; the warning that
  `RandomDataGenerator --tickers=SPY` overwrites real data (use synthetic tickers).
* `.dockerignore` additions (`**/bin/`, `**/obj/`).

Avoid:

* An API service that is mostly a proxy to `api.quantconnect.com` (`/v1/cloud/*`); it is not part of the
  target platform and couples the gateway to QC cloud credentials.
* Reports stored on a local volume per replica — the plan uses MinIO.
* Raw kustomize manifests — the plan uses Helm.
* Building the engine in `Debug` inside the image while the repo standard is `-c Release`.
* Again, `Tests` referencing service projects and a checked-in 584-line result fixture inside the main test
  assembly.

### 5.3 Summary

| Idea | Source | Keep? |
|---|---|---|
| One `Launcher` process per job, generated config | 5.1 | Yes |
| Read-through cached remote `IDataProvider` + reference-data pre-sync | 5.1 | Yes (with shared read-only volume as primary) |
| Bus-publishing `IMessagingHandler` subclass | 5.1 | Yes, on RabbitMQ, outside `Engine` |
| Versioned envelope/contracts | 5.1 | Yes, rewritten as OpenAPI + RabbitMQ message schemas |
| Parity test with `OrderListHash` | 5.1 | Yes |
| `LEAN_*` env → `Config` | 5.2 | Yes |
| Report service wrapping `Report.Report` | 5.2 | Yes, store in MinIO |
| Foundation-based multi-stage images, `LEAN_BASE` for Jupyter | 5.2 | Yes |
| NetMQ broker, in-memory queue, custom auth, static dashboards | 5.1 | No |
| QC cloud proxy API, kustomize, local report storage | 5.2 | No |
| `Engine` → `Messaging` reference, `Tests` → `Services` references | both | No |
