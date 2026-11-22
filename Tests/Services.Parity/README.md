# Platform parity regression harness

Runs LEAN's backtest regression suite through the **gateway of a running stack** and holds every result to the
algorithm's own `ExpectedStatistics` (including `OrderListHash`), the same source of truth as
`Tests/RegressionTests.cs`. A mismatch is a platform bug: fix the services, never the expectation.

## The set

`ParityCatalog` discovers cases exactly like `RegressionTests.GetRegressionTestParameters`: every non-abstract
`IRegressionAlgorithmDefinition` in `QuantConnect.Algorithm.CSharp` with a default constructor, once per language in
`Languages` (`PARITY_LANGUAGES`, default `CSharp,Python`). Definitions with `CanRunLocally = false` need data that is
not in the repo's `Data/` folder; they stay in the table as **skipped with the reason** (so does a Python case whose
`Algorithm.Python/<Name>.py` is missing).

* C# cases are submitted as built-in references (`{"source":"builtin","typeName":"<Name>"}`): the worker runs the class
  from its own `QuantConnect.Algorithm.CSharp.dll`.
* Python cases are uploaded as a project zip: the algorithm as `main.py` plus every `Algorithm.Python` module it
  imports (transitively), as they sit in one folder in the monolith.

Each job must end in the status LEAN's suite expects (`Completed`/`Running` -> `completed`, `RuntimeError` -> `failed`
with `runtime-error`) and every expected statistic must be equal (`-0` = `0`, as in `AlgorithmRunner.AssertAlgorithmState`).

## Running

Everything (build, monolith suite in `quantconnect/lean:foundation`, compose stack with scaled workers, platform suite,
table):

```bash
Tests/Services.Parity/run-parity.sh            # WORKERS=6 by default
SKIP_BUILD=1 SKIP_MONOLITH=1 PARITY_ONLY=UpdateOrderRegressionAlgorithm,Python/BasicTemplateAlgorithm Tests/Services.Parity/run-parity.sh
```

By hand against any stack:

```bash
dotnet build Tests/Services.Parity -c Release
PARITY_GATEWAY_URL=http://127.0.0.1:5080 PARITY_CONCURRENCY=6 \
PARITY_MONOLITH_TRX=$PWD/Tests/bin/Release/monolith-regression.trx \
dotnet test Tests/Services.Parity/bin/Release/net10.0/QuantConnect.Tests.Services.Parity.dll --filter TestCategory=ServicesParity
```

| variable | default | |
|---|---|---|
| `PARITY_GATEWAY_URL` | `http://127.0.0.1:5080` | when unset and unreachable the gateway cases are ignored (the catalog tests still run) |
| `PARITY_API_KEY` | `GATEWAY_API_KEY` from the environment, `.env`, `.env.example` | sent as `X-Api-Key` |
| `PARITY_CONCURRENCY` | 8 | jobs in flight; about the number of backtest workers |
| `PARITY_ONLY` | all | comma-separated `Name` or `Language/Name` |
| `PARITY_MONOLITH_TRX` | none | TRX of `RegressionTests.AlgorithmStatisticsRegression`: the monolith column |
| `PARITY_MAX_RUNTIME_SECONDS` | 3600 | per-job `maxRuntimeSeconds` |
| `PARITY_OUTPUT_DIR` | `Tests/Services.Parity/parity-results` | |

The gateway's per-key rate limit (`GATEWAY_RATE_LIMIT`, 120/min by default) is too low for hundreds of polled jobs;
`run-parity.sh` raises it for the run, and the client also honours `429 Retry-After`.

## Output

`parity-results/parity.{md,csv,json}`: one row per case with algorithm, language, monolith match, platform match,
`OrderListHash` match, wall time both ways and the mismatch or skip reason. Monolith time is the NUnit duration of the
case in LEAN's suite (one process, sequential); platform time is the job's `startedAt` to `finishedAt` (worker
process start to final result). The summary also gives the suite wall time both ways.
