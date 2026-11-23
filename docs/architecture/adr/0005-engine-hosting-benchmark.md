# ADR 0005: Engine hosting — benchmark evidence for ADR 0004

* Status: Accepted; as-built differences in [ADR 0006](0006-as-built.md)
* Plan step: `s1.3`
* Decision: **keep ADR 0004.** The engine runs in a container, gets its job and publishes its result over
  RabbitMQ (ADR 0002) and reads data from the shared read-only `lean-data` volume through the stock
  `DefaultDataProvider`. A remote data-provider HTTP API for data reads is rejected as the default.

## Context

ADR 0004 picked the shared volume because the engine would see the same data-read path as the monolith. This
spike checks that against numbers before Phase 2. It runs five regression backtests three ways:

| Mode | How the engine runs | Job in / result out | Data reads |
|---|---|---|---|
| `baseline` | stock `QuantConnect.Lean.Launcher` with the repo checkout mounted, default `Launcher/config.json` | `JobQueue` / `Messaging` (console) | `DefaultDataProvider` on `Data/` |
| `broker` | engine image (`quantconnect/lean:foundation` + Release binaries), no repo mount | RabbitMQ `lean.jobs` → `lean.results` | `DefaultDataProvider` on `lean-data` mounted read-only at `/Lean/Data/` |
| `http-data` | as `broker` | as `broker` | `HttpDataProvider` → market-data `GET /data/{path}` on `lean-data`; the engine only mounts `lean-meta` |

`lean-meta` holds only `market-hours/`, `symbol-properties/`, `*/*/map_files/`, `*/*/factor_files/`. LEAN reads
these straight from `Globals.DataFolder` (§4.2), so an HTTP-only provider cannot run without them.

The harness is in `Spikes/EngineHosting/` and is not part of `QuantConnect.Lean.sln`. All LEAN processes run in
`quantconnect/lean:foundation`, because pythonnet crashes on the host.

* `RabbitMqJobQueueHandler` (`IJobQueueHandler`): `BasicGet` one job `{jobId, config}`, apply the config
  overrides, build the packet with the stock `JobQueue`, ack after the engine finishes.
* `RabbitMqMessagingHandler`: the stock `Messaging` plus a publish of the final `BacktestResultPacket`
  (correlation id = job id, publisher confirms).
* `HttpDataProvider` (`IDataProvider`): maps the absolute `Fetch` key under `Globals.DataFolder` to
  `/data/{relative}`. A 404 returns `null`, the same as a missing file.
* `MarketData`: ASP.NET Core minimal API on `lean-data` (read-only), with path-traversal checks and `/stats`
  request counters.
* `measure.py` wraps the launcher and reports wall time, peak RSS (`wait4` `ru_maxrss`) and CPU time.

Each mode had one discarded warm-up run, then 5 interleaved repetitions (`bench.py --reps 5`). The "e2e" time is
from the bench client's view: `docker run` to process exit for `baseline`, and job publish to result message
received for the service modes. In both cases it includes the container start. "engine" time is the launcher
process alone. Statistics are all 28 `STATISTICS::` lines from the console. For the service modes they are also
the `Statistics` of the result packet received over RabbitMQ. Both are compared to the baseline for every run.

Environment: commit `162706671` (the spike code in this PR, before the rebase onto ADRs 0001–0004), Linux 6.8 (AWS), 8 vCPU Xeon, Docker 29.9.2, foundation image
`sha256:6e4fd9b135b8…`, `rabbitmq:3.13-management`. The raw summary is in
[`evidence/0005-engine-hosting-benchmark.json`](evidence/0005-engine-hosting-benchmark.json).

## Results

Medians of 5 runs. Δ is relative to `baseline` for the same algorithm.

| Algorithm | Mode | e2e s (range) | Δ e2e | engine s | Δ engine | peak RSS MB | stats = baseline | orders / `OrderListHash` | HTTP reqs (404) / MB |
|---|---|---|---|---|---|---|---|---|---|
| BasicTemplateAlgorithm (C#) | baseline | 2.625 (2.53–2.66) | — | 2.352 | — | 275 | 28/28 | 1 / `3da9fa60…` | |
| | broker | 2.687 (2.54–2.78) | **+2.4%** | 2.539 | +8.0% | 294 | ✅ console + packet | 1 / `3da9fa60…` | |
| | http-data | 3.249 (3.18–3.43) | +23.8% | 3.100 | +31.8% | 298 | ✅ console + packet | 1 / `3da9fa60…` | 41 (0) / 0.87 |
| BasicTemplateFrameworkAlgorithm (C#) | baseline | 2.660 (2.53–3.00) | — | 2.405 | — | 285 | 28/28 | 3 / `f209ed42…` | |
| | broker | 2.705 (2.55–3.07) | **+1.7%** | 2.555 | +6.2% | 303 | ✅ console + packet | 3 / `f209ed42…` | |
| | http-data | 3.130 (2.98–3.41) | +17.7% | 2.972 | +23.6% | 308 | ✅ console + packet | 3 / `f209ed42…` | 41 (0) / 0.87 |
| BasicTemplateOptionsAlgorithm (C#) | baseline | 2.776 (2.61–4.25) | — | 2.491 | — | 299 | 28/28 | 2 / `19ba1220…` | |
| | broker | 2.623 (2.52–2.85) | **−5.5%** | 2.467 | −1.0% | 296 | ✅ console + packet | 2 / `19ba1220…` | |
| | http-data | 3.245 (3.06–4.52) | +16.9% | 3.105 | +24.6% | 326 | ✅ console + packet | 2 / `19ba1220…` | 42 (1) / 6.96 |
| BasicTemplateFuturesAlgorithm (C#) | baseline | 3.312 (3.30–3.77) | — | 3.046 | — | 344 | 28/28 | 2700 / `6e0f767a…` | |
| | broker | 3.608 (3.50–4.66) | **+8.9%** | 3.413 | +12.0% | 358 | ✅ console + packet | 2700 / `6e0f767a…` | |
| | http-data | 4.254 (3.99–4.48) | +28.4% | 4.067 | +33.5% | 352 | ✅ console + packet | 2700 / `6e0f767a…` | 106 (13) / 4.61 |
| BasicTemplateAlgorithm (Py) | baseline | 4.627 (4.53–4.85) | — | 4.378 | — | 524 | 28/28 | 1 / `3da9fa60…` | |
| | broker | 4.611 (4.53–5.06) | **−0.3%** | 4.584 | +4.7% | 516 | ✅ console + packet | 1 / `3da9fa60…` | |
| | http-data | 4.942 (4.93–5.49) | +6.8% | 4.913 | +12.2% | 524 | ✅ console + packet | 1 / `3da9fa60…` | 41 (0) / 0.87 |

Result message size over RabbitMQ: 19–43 KB for the small algorithms and 3.69 MB for the futures algorithm
(2700 orders).

## Findings

1. **`broker` (shared volume) meets the target.** End-to-end it is within 10% of the baseline for all five
   algorithms (−5.5% to +8.9%). Statistics are identical in every run, both on the console and in the result
   packet, and order counts and `OrderListHash` match. Peak RSS is within +7% (the RabbitMQ client and result
   serialisation).
2. **Engine-process time for futures is +12%.** That is above 10% on the 3.0 s engine-only number, but the
   end-to-end time the user sees is +8.9%. The extra ~0.37 s matches serialising and publishing the 3.69 MB
   result with publisher confirms (the C# algorithms with small results cost +6–8%, about 0.15–0.19 s, which
   is a fixed per-process cost for the client and connection). Phase 2 should keep large result payloads
   (orders, charts) in MinIO and send only a reference on the bus (ADR 0003). The run is short, so this fixed
   cost is a large share of it, and its share shrinks as backtests get longer.
3. **`http-data` costs +7% to +28% end-to-end** (+12% to +34% engine) with identical statistics. Every
   `Fetch` blocks a data-feed worker on a request (§4.4), and the cost grows with the number of files (106
   requests for futures). It also still needs the `lean-meta` volume for the direct disk reads, so it does
   not remove the shared filesystem. This confirms the rejection in ADR 0004.
4. **Future margins do go through `IDataProvider`.** In `http-data`, `FutureMarginModel` fetched its margin
   files from the HTTP API and the futures statistics still matched. §4.2 / ADR 0004 list future margins
   among the direct disk reads; per `Common/Securities/Future/FutureMarginModel.cs` they use
   `Composer.Instance.GetPart<IDataProvider>()`. This does not change the decision. The 404s are
   requests for files that are not in `lean-data` either; `null` is what `DefaultDataProvider` returns for them.
5. **Python exit code.** All Python runs, the **baseline included**, print the full statistics, post the
   results, log `Packet removed from queue` and then abort (exit −6) in pythonnet's `GILState.Finalize`
   (`GIL must always be released …`). In `broker`/`http-data` the result is published and the job is acked
   before the abort, and `lean.jobs` / `lean.results` were empty after the run. The crash already exists in
   the monolith, so the spike did not add it. Phase 2 workers must treat "result received + ack" as success,
   not the process exit code, until the shutdown is fixed.

## Consequences

* ADR 0004 stands, and Phase 2 can start: backtest-worker = engine container + RabbitMQ job/result handlers +
  read-only `lean-data` + stock `DefaultDataProvider`.
* The worker sends large result artefacts to MinIO and publishes only a reference on `lean.results`.
* The worker's success criterion is result published + ack, independent of the process exit code (finding 5).
* The `HttpDataProvider` stays only as a possible fetch-on-miss / no-RWX fallback (ADR 0004 decisions 3 and 6),
  and must be used with a pre-synced metadata folder.

## Reproduce

```bash
dotnet build QuantConnect.Lean.sln -c Release
pip install pika
python3 Spikes/EngineHosting/bench.py --reps 5   # writes Spikes/EngineHosting/out/results{,-summary}.json
docker compose -f Spikes/EngineHosting/docker-compose.yml down
```
