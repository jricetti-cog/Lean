# ADR 0004: Market data access — shared read-only volume + market-data service

* Status: Accepted; as-built differences in [ADR 0006](0006-as-built.md)
* Plan step: `s1.2`
* Decision taken: **shared read-only `lean-data` volume plus the market-data service** (approved plan).

## Context

* `IDataProvider.Fetch(key)` is the only data abstraction, and its keys are absolute paths under
  `Globals.DataFolder`. Price data goes through it, but market hours, symbol properties, security definitions,
  map files (directory enumeration in `LocalDiskMapFileProvider`), factor files, future margins, interest rates,
  shortable lists and coarse fundamentals are read **straight from disk** (§4.2). An engine process therefore
  needs a real filesystem at `data-folder` whatever `data-provider` says.
* Those databases are loaded once per process and some are captured in static initializers (§4.1), so the data
  folder must be correct and absolute before the engine starts. The default `data-folder` (`../../../Data/`) is
  relative to the working directory.
* `IDataProvider.Fetch` is called on data-feed worker threads, not per tick, but a cold remote fetch stalls the
  feed (§4.4). Streaming every bar over the network would add that latency to every file and still not serve
  the direct disk reads.
* Writers are not safe for concurrent readers in other processes: `LeanDataWriter` stores through
  `DiskDataCacheProvider.Store` → `Compression.ZipCreateAppendData`, which opens the existing zip with
  `FileMode.OpenOrCreate` and `ZipArchiveMode.Update` and rewrites it in place; the only lock is an
  in-process key synchronizer. `DownloaderDataProvider`, `ToolBox` and `ApiDataProvider` all write into
  `Globals.DataFolder` this way (§4.2).
* `ToolBox RandomDataGenerator --tickers=SPY` overwrites the real `Data/equity/usa/daily/spy.zip` (§5.2).

## Decision

1. **One shared `lean-data` volume**, mounted at `/Lean/Data/`.
   * **market-data** mounts it read-write and is the **only writer**.
   * backtest-worker, live-worker, report and research mount it **read-only**. Their generated config or
     `LEAN_DATA_FOLDER=/Lean/Data/` sets an absolute `data-folder` before first `Config` access.
   * Workers keep `data-provider = DefaultDataProvider` and the default `LocalDiskMapFileProvider` /
     `LocalDiskFactorFileProvider`, i.e. the engine reads data exactly as in the monolith. A read-only mount also
     makes it impossible for a job to corrupt shared data.
2. **market-data owns all writes and fetching:**
   * Downloads and conversions run as tasks from the `marketdata.fetch` queue (ADR 0002), executing
     `DownloaderDataProvider` / `ToolBox` code paths unchanged, one task per process (static state, §4.1).
   * **Atomic publish:** a task writes into a staging directory on the same volume (`/Lean/Data/.staging/<taskId>/`
     with the normal LEAN layout) and, when finished, `rename`s each file into place (atomic on one filesystem).
     Readers never see a partially rewritten zip. Merges with an existing file copy it into staging first.
   * Synthetic data uses synthetic tickers only; the service rejects generator tasks targeting existing real
     symbols.
   * Keeps the data catalog (`marketdata` database, ADR 0003) and serves `/internal/v1/data/...`: coverage
     queries, fetch requests, and **history queries** for the gateway, console charts and research
     (read through the same LEAN readers over the volume).
3. **Missing data during a job.** Default: missing files are missing, as in the monolith (`DefaultDataProvider`
   returns `null`), which keeps backtests deterministic. Per job, `fetchMissingData: true` selects a
   fetch-on-miss `IDataProvider` (adapter project outside `Engine`): on a miss it requests the file from
   market-data, waits with a bounded timeout for the atomic publish, then reads it from the read-only volume.
   Preferred workflow: the gateway asks market-data for coverage **before** dispatch and queues the fetch
   first.
4. **History inside algorithms** stays in-process (`HistoryProviderManager` →
   `SubscriptionDataReaderHistoryProvider` over the volume, plus `BrokerageHistoryProvider` live). History calls
   are synchronous from user code and must not cross the network for every request.
5. **Live data fan-out.** A live-worker uses its brokerage's `IDataQueueHandler` directly when the brokerage
   provides data. For shared feeds, market-data holds the single upstream `IDataQueueHandler` connection and
   republishes on `lean.marketdata.live` (ADR 0002); the live-worker's generated config selects a bus-backed
   `IDataQueueHandler` that subscribes to those topics. `IDataQueueHandler` is already asynchronous (§4.4), so
   this adds no work to the tick loop.
6. **Kubernetes:** `lean-data` is a `ReadWriteMany` PVC (market-data mounts it RW, everything else with
   `readOnly: true`). Where no RWX storage class exists, the fallback is the read-through caching
   `IDataProvider` from the earlier branch (§5.1): a per-pod local data folder, pre-synced reference data
   (`market-hours/`, `symbol-properties/`, `*/map_files/`, `*/factor_files/`) and files fetched from market-data
   on first use.

## Consequences

* Zero engine changes and the same data-read path as the monolith, so backtest parity is a property of the
  design, not something to re-prove per data type.
* Workers start with all data already visible; no per-job copy.
* The volume is a single shared dependency: market-data throughput bounds how fast new data appears, and the
  volume needs backups. Reads scale with the volume's backing storage, not with market-data replicas.
* Only one market-data instance writes a given path at a time; the service serialises tasks per symbol and
  resolution through its catalog.
* Database refresh: long-lived live workers see new files immediately, but reference databases are cached per
  process and refresh on `AlgorithmSettings.DatabasesRefreshPeriod` (§4.1), as today.

## Alternatives considered

* **Network-only data provider (every bar over HTTP/bus).** Rejected: latency on every file, and the direct disk
  reads in §4.2 would still fail without a local folder.
* **Each worker downloads what it needs into its own disk.** Rejected as the default: duplicated storage and
  cold starts per job; kept as the no-RWX fallback (decision 6).
* **Workers write fetched data themselves into a shared RW volume.** Rejected: in-place zip updates with only
  an in-process lock would race across workers (see Context).
* **Market data in object storage (MinIO) mounted via FUSE.** Rejected: directory enumeration for map files and
  random zip access perform poorly over S3 FUSE, and it adds an infrastructure component.
