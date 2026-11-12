#!/usr/bin/env python3
"""Submits a backtest through the API Gateway and waits for the result.

Usage: LEAN_API_ACCESS_TOKEN=<token> python3 Services/smoke_test.py [AlgorithmTypeName] [gateway url]
"""
import base64, hashlib, json, os, sys, time, urllib.request

algorithm = sys.argv[1] if len(sys.argv) > 1 else "BasicTemplateAlgorithm"
gateway = sys.argv[2] if len(sys.argv) > 2 else "http://localhost:5000"
user_id = os.environ.get("LEAN_JOB_USER_ID", "0")
token = os.environ["LEAN_API_ACCESS_TOKEN"]

def call(method, path, body=None):
    timestamp = str(int(time.time()))
    digest = hashlib.sha256(f"{token}:{timestamp}".encode()).hexdigest()
    request = urllib.request.Request(gateway + path, method=method, data=json.dumps(body).encode() if body else None, headers={
        "Authorization": "Basic " + base64.b64encode(f"{user_id}:{digest}".encode()).decode(),
        "Timestamp": timestamp, "Content-Type": "application/json"})
    with urllib.request.urlopen(request) as response:
        return json.loads(response.read())

job = call("POST", "/api/v1/jobs/backtest", {"algorithmTypeName": algorithm, "algorithmLanguage": "CSharp"})
print("submitted", job["jobId"])
while job["status"] not in ("Completed", "Failed", "Cancelled"):
    time.sleep(2)
    job = call("GET", f"/api/v1/jobs/{job['jobId']}")
    print(job["status"], f"{float(job.get('progress') or 0):.0%}")
print(json.dumps(job.get("statistics", {}), indent=2, sort_keys=True))
sys.exit(0 if job["status"] == "Completed" else 1)
