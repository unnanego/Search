"""Runs the Phase 0 spikes one process at a time and keeps each result in spikes/results/.

    python spikes/run.py                 # everything
    python spikes/run.py startup tabs    # some

Each run gets a WebView2 data folder of its own under %TEMP%\\search-spike, wiped first, so a "cold" start
really is one and nothing touches a real browser's profile.
"""
import json, os, shutil, subprocess, sys, tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
EXE = max((ROOT / "Harness/bin").rglob("Harness.exe"), key=lambda p: p.stat().st_mtime)
OUT = ROOT / "results"

def run(name, scenario, *args, fresh=True, timeout=180, profile=None):
    data = Path(tempfile.gettempdir()) / "search-spike" / (profile or name)
    if fresh: shutil.rmtree(data, ignore_errors=True)
    out = OUT / f"{name}.json"
    out.unlink(missing_ok=True)
    cmd = [str(EXE), "--scenario", scenario, "--out", str(out), "--data", str(data), "--timeout", str(timeout - 20), *args]
    try: subprocess.run(cmd, timeout=timeout, check=False)
    except subprocess.TimeoutExpired: print(f"{name}: killed after {timeout}s")
    # Popups a run never handled outlive it, and keep its WebView2 browser process (and data folder) alive.
    subprocess.run(["powershell", "-NoProfile", "-Command", "Get-CimInstance Win32_Process -Filter \"Name='msedgewebview2.exe'\" | Where-Object CommandLine -like '*search-spike*' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"], check=False)
    result = json.loads(out.read_text()) if out.exists() else {"error": "no result written"}
    print(f"{name}: {'ERROR ' + str(result['error'])[:200] if 'error' in result else 'ok'}")
    return result

PLAN = {
    "startup": lambda: [run("startup-cold", "startup"), *[run(f"startup-warm{i}", "startup", fresh=False, profile="startup-cold") for i in range(1, 4)]],
    "tabs": lambda: [run("tabs-10", "tabs", "--count", "10"), run("tabs-50", "tabs", "--count", "50", timeout=300)],
    "shield": lambda: [run(f"shield-{m}", "shield", "--mode", m, "--count", "6") for m in ("none", "filter", "dnr")],
    "extensions": lambda: [run("extensions", "extensions"), run("extensions-handled", "extensions", "--mode", "handled")],
    "pip": lambda: [run("pip", "pip")],
    "overlay": lambda: [run("overlay", "overlay")],
    "windows": lambda: [run("windows", "windows")],
    "keeper": lambda: [run("keeper", "keeper")],
}

if __name__ == "__main__":
    OUT.mkdir(exist_ok=True)
    for key in sys.argv[1:] or PLAN: PLAN[key]()
