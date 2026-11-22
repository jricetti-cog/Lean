#!/usr/bin/env python3
"""Engine-hosting benchmark spike (plan step s1.3).

Runs the same regression backtests three ways and records wall time, peak RSS and statistics:
  1. baseline  - stock launcher process, repo checkout mounted (the monolith as run today)
  2. broker    - engine container, job in / results out over RabbitMQ, data from the read-only lean-data volume
  3. http-data - as 2, but every IDataProvider read goes to the market-data HTTP service

Usage (from the repo root, after `dotnet build QuantConnect.Lean.sln -c Release`):
  pip install pika && python3 Spikes/EngineHosting/bench.py --reps 3
"""
import argparse, json, os, platform, re, shutil, statistics, subprocess, sys, time, urllib.request, uuid, warnings

import pika

warnings.filterwarnings("ignore", category=DeprecationWarning)

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(HERE, "out")
FOUNDATION = "quantconnect/lean:foundation"
ENGINE_IMAGE = "lean-engine-spike:s1.3"
NETWORK = "lean-spike_default"
SPIKE_NS = "QuantConnect.Spikes.EngineHosting."

ALGORITHMS = [
    ("BasicTemplateAlgorithm", "CSharp", "QuantConnect.Algorithm.CSharp.dll"),
    ("BasicTemplateFrameworkAlgorithm", "CSharp", "QuantConnect.Algorithm.CSharp.dll"),
    ("BasicTemplateOptionsAlgorithm", "CSharp", "QuantConnect.Algorithm.CSharp.dll"),
    ("BasicTemplateFuturesAlgorithm", "CSharp", "QuantConnect.Algorithm.CSharp.dll"),
    ("BasicTemplateAlgorithm", "Python", "../../../Algorithm.Python/BasicTemplateAlgorithm.py"),
]
# Files LEAN reads straight from Globals.DataFolder, bypassing IDataProvider (see current-state.md).
META_GLOBS = ["market-hours", "symbol-properties", "*/*/map_files", "*/*/factor_files"]


def sh(cmd, **kw):
    return subprocess.run(cmd, check=True, text=True, **kw)


def setup():
    ctx = os.path.join(OUT, "image-context")
    shutil.rmtree(ctx, ignore_errors=True)
    shutil.copytree(os.path.join(REPO, "Launcher/bin/Release"), os.path.join(ctx, "Launcher/bin/Release"))
    shutil.copytree(os.path.join(REPO, "Algorithm.Python"), os.path.join(ctx, "Algorithm.Python"),
                    ignore=shutil.ignore_patterns("__pycache__"))
    shutil.copytree(os.path.join(OUT, "engine"), os.path.join(ctx, "engine"))
    shutil.copy(os.path.join(HERE, "measure.py"), ctx)
    sh(["docker", "compose", "-f", os.path.join(HERE, "docker-compose.yml"), "down"])
    sh(["docker", "build", "-q", "-t", ENGINE_IMAGE, "-f", os.path.join(HERE, "Dockerfile.engine"), ctx])

    for volume, script in [
        ("lean-data", "cp -a /src/. /dst/"),
        ("lean-meta", "cd /src && for d in " + " ".join(META_GLOBS) + "; do mkdir -p /dst/$(dirname $d) && cp -a $d /dst/$d; done"),
    ]:
        sh(["docker", "volume", "rm", "-f", volume], stdout=subprocess.DEVNULL)
        sh(["docker", "volume", "create", volume], stdout=subprocess.DEVNULL)
        sh(["docker", "run", "--rm", "-v", f"{REPO}/Data:/src:ro", "-v", f"{volume}:/dst", FOUNDATION, "bash", "-c", script])

    sh(["docker", "compose", "-f", os.path.join(HERE, "docker-compose.yml"), "up", "-d", "--wait"])


def parse_log(output):
    measure = json.loads(re.search(r"^SPIKE_MEASURE (.*)$", output, re.M).group(1))
    stats = sorted(re.findall(r"STATISTICS:: (.*)$", output, re.M))
    return measure, stats


def run_baseline(name, language, location):
    cmd = ["docker", "run", "--rm", "-v", f"{REPO}:{REPO}", "-w", f"{REPO}/Launcher/bin/Release", FOUNDATION,
           "python3", f"{HERE}/measure.py", "dotnet", "QuantConnect.Lean.Launcher.dll",
           "--close-automatically", "true", "--algorithm-type-name", name,
           "--algorithm-language", language, "--algorithm-location", location]
    start = time.monotonic()
    proc = subprocess.run(cmd, capture_output=True, text=True)
    wall = time.monotonic() - start
    measure, stats = parse_log(proc.stdout)
    return {"e2e_s": round(wall, 3), **measure, "stats": stats, "packet_stats": None}


def http_json(method, path):
    req = urllib.request.Request("http://localhost:8080" + path, method=method)
    with urllib.request.urlopen(req) as resp:
        body = resp.read()
        return json.loads(body) if body else None


def run_engine(name, language, location, http_data):
    connection = pika.BlockingConnection(pika.URLParameters("amqp://guest:guest@localhost:5672/"))
    channel = connection.channel()
    for queue in ("lean.jobs", "lean.results"):
        channel.queue_declare(queue, durable=True)
        channel.queue_purge(queue)

    job_id = uuid.uuid4().hex
    job = {"jobId": job_id, "config": {"algorithm-type-name": name, "algorithm-language": language,
                                       "algorithm-location": location}}
    cmd = ["docker", "run", "--rm", "--network", NETWORK, "-e", "RABBITMQ_URL=amqp://guest:guest@rabbitmq:5672/"]
    args = ["--close-automatically", "true", "--data-folder", "/Lean/Data/",
            "--job-queue-handler", SPIKE_NS + "RabbitMqJobQueueHandler",
            "--messaging-handler", SPIKE_NS + "RabbitMqMessagingHandler"]
    if http_data:
        http_json("POST", "/stats/reset")
        cmd += ["-e", "MARKET_DATA_URL=http://market-data:8080/", "-v", "lean-meta:/Lean/Data:ro"]
        args += ["--data-provider", SPIKE_NS + "HttpDataProvider"]
    else:
        cmd += ["-v", "lean-data:/Lean/Data:ro"]

    start = time.monotonic()
    channel.basic_publish("", "lean.jobs", json.dumps(job).encode(),
                          pika.BasicProperties(content_type="application/json", delivery_mode=2))
    proc = subprocess.Popen(cmd + [ENGINE_IMAGE] + args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    result = None
    for method, properties, body in channel.consume("lean.results", inactivity_timeout=1):
        if method is None:
            if proc.poll() is not None:
                break
            continue
        channel.basic_ack(method.delivery_tag)
        if properties.correlation_id == job_id:
            result = (properties.type, json.loads(body), len(body))
            break
    e2e = time.monotonic() - start
    output = proc.communicate()[0]
    channel.cancel()
    connection.close()

    measure, stats = parse_log(output)
    row = {"e2e_s": round(e2e, 3), **measure, "stats": stats}
    if result is None or result[0] != "backtest-result":
        row["packet_stats"] = None
        row["error"] = "no backtest-result message: " + (json.dumps(result[1])[:500] if result else output[-2000:])
        return row
    packet_stats = (result[1].get("Results") or result[1].get("results"))
    packet_stats = packet_stats.get("Statistics") or packet_stats.get("statistics")
    row["packet_stats"] = sorted(f"{k} {v}" for k, v in packet_stats.items())
    row["result_message_bytes"] = result[2]
    if http_data:
        row["http"] = http_json("GET", "/stats")
    return row


def environment():
    def out(cmd):
        return subprocess.run(cmd, capture_output=True, text=True).stdout.strip()
    cpu = next((l.split(":", 1)[1].strip() for l in open("/proc/cpuinfo") if l.startswith("model name")), "?")
    return {
        "commit": out(["git", "-C", REPO, "rev-parse", "HEAD"]),
        "host": f"{platform.system()} {platform.release()}, {os.cpu_count()} vCPU {cpu}",
        "docker": out(["docker", "version", "--format", "{{.Server.Version}}"]),
        "foundation_image": out(["docker", "image", "inspect", FOUNDATION, "--format", "{{.Id}}"]),
        "rabbitmq_image": "rabbitmq:3.13-management",
    }


def summarize(runs):
    summary = []
    for algo in ALGORITHMS:
        def rows_for(mode):
            return [r for r in runs if r["algorithm"] == label(algo) and r["mode"] == mode and r["rep"] >= 0]
        base = rows_for("baseline")
        base_stats = base[0]["stats"]
        base_e2e = statistics.median(r["e2e_s"] for r in base)
        base_eng = statistics.median(r["wall_s"] for r in base)
        for mode in MODES:
            rows = rows_for(mode)
            e2e = statistics.median(r["e2e_s"] for r in rows)
            eng = statistics.median(r["wall_s"] for r in rows)
            summary.append({
                "algorithm": label(algo), "mode": mode, "runs": len(rows),
                "e2e_median_s": e2e, "e2e_range_s": [min(r["e2e_s"] for r in rows), max(r["e2e_s"] for r in rows)],
                "e2e_vs_baseline_pct": round(100 * (e2e / base_e2e - 1), 1),
                "engine_median_s": eng, "engine_vs_baseline_pct": round(100 * (eng / base_eng - 1), 1),
                "rss_median_mb": statistics.median(r["max_rss_mb"] for r in rows),
                "statistics_count": len(base_stats),
                "statistics_identical": all(r["stats"] == base_stats for r in rows),
                "packet_statistics_identical": None if mode == "baseline" else all(r["packet_stats"] == base_stats for r in rows),
                "exit_codes": sorted({r["exit"] for r in rows}),
                "order_list_hash": next(s for s in base_stats if s.startswith("OrderListHash")).split()[-1],
                "total_orders": next(s for s in base_stats if s.startswith("Total Orders")).split()[-1],
                "http": rows[-1].get("http"), "result_message_bytes": rows[-1].get("result_message_bytes"),
            })
    return summary


MODES = {
    "baseline": lambda a: run_baseline(*a),
    "broker": lambda a: run_engine(*a, http_data=False),
    "http-data": lambda a: run_engine(*a, http_data=True),
}


def label(algo):
    return f"{algo[0]} ({'C#' if algo[1] == 'CSharp' else 'Py'})"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reps", type=int, default=3)
    parser.add_argument("--skip-setup", action="store_true")
    parser.add_argument("--out", default=os.path.join(OUT, "results.json"))
    opts = parser.parse_args()

    if not opts.skip_setup:
        setup()

    runs = []
    # One discarded warm-up per mode (page cache, image layers), then interleaved repetitions.
    for rep in range(-1, opts.reps):
        for algo in ALGORITHMS:
            modes = list(MODES)
            modes = modes[rep % 3:] + modes[:rep % 3]
            for mode in modes:
                row = MODES[mode](algo)
                row.update(algorithm=label(algo), mode=mode, rep=rep)
                runs.append(row)
                print(f"rep={rep} {mode:9} {label(algo):40} e2e={row['e2e_s']:7.2f}s engine={row['wall_s']:7.2f}s "
                      f"rss={row['max_rss_mb']:7.1f}MB exit={row['exit']} stats={len(row['stats'])} {row.get('error', '')}",
                      flush=True)
    with open(opts.out, "w") as f:
        json.dump(runs, f, indent=1)

    summary = summarize(runs)
    with open(opts.out.replace(".json", "-summary.json"), "w") as f:
        json.dump({"environment": environment(), "summary": summary}, f, indent=1)
    for s in summary:
        print(json.dumps(s))


if __name__ == "__main__":
    main()
