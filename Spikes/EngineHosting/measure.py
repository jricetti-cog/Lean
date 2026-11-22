"""Run a command and print its wall time and peak RSS (ru_maxrss of the child) as one JSON line."""
import json, os, subprocess, sys, time

start = time.monotonic()
child = subprocess.Popen(sys.argv[1:])
_, status, usage = os.wait4(child.pid, 0)
print("SPIKE_MEASURE " + json.dumps({
    "wall_s": round(time.monotonic() - start, 3),
    "max_rss_mb": round(usage.ru_maxrss / 1024, 1),
    "cpu_s": round(usage.ru_utime + usage.ru_stime, 2),
    "exit": os.waitstatus_to_exitcode(status),
}), flush=True)
sys.exit(os.waitstatus_to_exitcode(status))
