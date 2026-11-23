# End-to-end demo walkthrough

The script for demonstrating the platform on one machine (plan step s6.5), and the evidence of the recorded run on
`master` 7cc1aee90. Everything goes through the console (http://127.0.0.1:5090) and Grafana (http://127.0.0.1:3000);
the terminal is only used to start the stack and scale the workers.

## Start

```sh
docker compose --profile observability up -d --wait   # `make up` + Grafana, Prometheus, Tempo, Loki, OTel Collector
make smoke                                            # engine over the bus, then the public API end to end
```

## Walkthrough

| # | Where | What to show |
|---|---|---|
| 1 | Console > New backtest | Submit a C# built-in algorithm (e.g. `BasicTemplateAlgorithm`) |
| 2 | Console > New backtest | Submit a Python algorithm (source in the editor) |
| 3 | Console > Jobs > job | Status, progress, equity chart and logs streaming live, then the final statistics |
| 4 | Job page > Report | Generate the report and open the HTML |
| 5 | Console > Jobs | Cancel a running backtest and open its page (see known gap below) |
| 6 | Terminal, then Console > Optimizations | `docker compose up -d --scale backtest-worker=3`, run a grid optimization, child backtests spread over the workers, best parameters |
| 7 | Console > Live | Deploy `LiveWorkerDemoAlgorithm` on `PaperBrokerage` with the simulated BTCUSD feed, watch holdings/equity, stop it |
| 8 | Grafana > Service RED | Request rate, errors and latency per service, including the optimizer |
| 9 | Grafana > Explore > Tempo | Paste a gateway `X-Request-ID` and open the full trace (gateway > orchestrator > worker > results) |

Known gap: a cancelled backtest's page shows "No statistics yet" instead of the last statistics it streamed.

## Evidence

- Parity: [`Tests/Services.Parity/parity-results/parity.md` at 7cc1aee90](https://github.com/jricetti-cog/Lean/blob/7cc1aee90b6f6bd660a909257a5e1de6f748505b/Tests/Services.Parity/parity-results/parity.md),
  1101 / 1101 runnable regression algorithms match `ExpectedStatistics` through the gateway.
- The walkthrough recording is attached to the PR that added this page.

`docker compose --profile observability ps` after `up -d --wait`:

```
SERVICE               STATUS
api-gateway           Up 3 minutes (healthy)
backtest-worker       Up 3 minutes (healthy)
console               Up 3 minutes (healthy)
docker-socket-proxy   Up 3 minutes (healthy)
grafana               Up 3 minutes (healthy)
infra-ready           Up 3 minutes (healthy)
job-orchestrator      Up 3 minutes (healthy)
loki                  Up 3 minutes (healthy)
market-data           Up 3 minutes (healthy)
minio                 Up 3 minutes (healthy)
optimizer             Up 3 minutes (healthy)
otel-collector        Up 3 minutes
postgres              Up 3 minutes (healthy)
postgres-exporter     Up 3 minutes (healthy)
prometheus            Up 3 minutes (healthy)
rabbitmq              Up 3 minutes (healthy)
report                Up 3 minutes (healthy)
results               Up 3 minutes (healthy)
tempo                 Up 3 minutes (healthy)
```

`make smoke`:

```
bash deploy/compose/smoke-backtest.sh
== submit BasicTemplateAlgorithm (job 3b06ccfc-5ae8-42e1-af61-00cfbf64902e, image lean/backtest-worker:dev)
PASS  observer queue smoke.3b06ccfc-5ae8-42e1-af61-00cfbf64902e bound to lean.events job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.#
== run the engine
      20261123 05:26:54.412 TRACE:: BusJobQueueHandler.NextJob(): job 3b06ccfc-5ae8-42e1-af61-00cfbf64902e attempt 1 (Backtest) -> /Lean/Launcher/bin/Release/QuantConnect.Algorithm.CSharp.dll
      20261123 05:26:54.453 TRACE:: BusMessagingHandler: publishing job 3b06ccfc-5ae8-42e1-af61-00cfbf64902e attempt 1 to lean.events
      STATISTICS:: Total Orders 1
      STATISTICS:: Net Profit 1.692%
      STATISTICS:: OrderListHash 3da9fa60bf95b9ed148b95e02e0cfc9e
      20261123 05:26:55.872 TRACE:: BusJobQueueHandler.AcknowledgeJob(): 3b06ccfc-5ae8-42e1-af61-00cfbf64902e done
      20261123 05:26:56.065 TRACE:: BusMessagingHandler.Dispose(): published 9, dropped 0
      20261123 05:26:56.066 TRACE:: Program.Main(): Exiting Lean...
PASS  Launcher exited 0
PASS  statistics match BasicTemplateAlgorithm.ExpectedStatistics (Total Orders 1, OrderListHash 3da9fa60bf95b9ed148b95e02e0cfc9e)
== events on lean.events
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.heartbeat.v1         events.v1.JobHeartbeat
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.result.v1            packets.v1.AlgorithmStatus
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.result.v1            packets.v1.AlgorithmStatus
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.progress.v1          events.v1.JobProgress
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.result.v1            packets.v1.BacktestResult
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.log.v1               packets.v1.Debug
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.log.v1               packets.v1.Debug
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.result.v1            packets.v1.AlgorithmStatus
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.log.v1               packets.v1.Debug
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.log.v1               packets.v1.SystemDebug
      job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.result.v1            packets.v1.PacketRef
PASS  11 events, all on job.backtest.3b06ccfc-5ae8-42e1-af61-00cfbf64902e.*
PASS  heartbeat published
PASS  exactly one final packets.v1.PacketRef
PASS  final result by reference: lean-results/3b06ccfc-5ae8-42e1-af61-00cfbf64902e/1/3b06ccfc-5ae8-42e1-af61-00cfbf64902e.json (42939 bytes)
PASS  PacketRef statistics OrderListHash = 3da9fa60bf95b9ed148b95e02e0cfc9e
== result file in MinIO
PASS  lean-results/3b06ccfc-5ae8-42e1-af61-00cfbf64902e/1/3b06ccfc-5ae8-42e1-af61-00cfbf64902e.json: 42939 bytes, sha256 d53561eadbf8f8d0afaca00774d1407ac65b3c3731368f96e1bec0618dc4ca62
== BasicTemplateAlgorithm completed: smoke test passed
bash deploy/smoke/smoke.sh
== smoke: gateway http://127.0.0.1:5080, console http://127.0.0.1:5090
   PASS gateway /healthz
   PASS gateway /readyz (upstreams reachable)
   PASS no API key -> 401
   PASS wrong API key -> 401
   PASS merged OpenAPI document (30 paths)
== console
   PASS console serves the SPA
   PASS console /api proxy -> gateway 200
== backtest through the gateway (Python source, run by a backtest-worker)
   PASS POST /api/v1/backtests -> 201 job 47c8ebb8-f00e-4005-a720-da573d1ac19f
   PASS backtest completed in 8s on 3b7bb71d48ab
      {"status":"completed","statistics":{"Total Orders":"1","Net Profit":"1.692%","Sharpe Ratio":"8.854","End Equity":"101691.92"}}
   PASS result statistics: 1 order
   PASS result download -> 302 presigned MinIO URL, readable
== report on the backtest (report service)
   PASS report 08233cc7-7b91-43f6-b898-ba86486b31fe completed
   PASS report HTML downloads (presigned MinIO URL)
== smoke passed
EXIT 0
```
