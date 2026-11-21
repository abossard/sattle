#!/usr/bin/env python3
"""Poll a deployment, deliver pending native records, and resume after an interrupted send."""
import argparse
from datetime import datetime, timezone
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent


def run_python(script, *args):
    subprocess.run([sys.executable, "-B", str(HERE / script), *map(str, args)], check=True)


def sample_signals(fixture, pending, scope):
    # A resource group can be watched without a CloudHealth model. For a model,
    # read its bound Azure sources with the current clock and the captured definitions.
    if "/providers/microsoft.cloudhealth/healthmodels/" not in scope.lower():
        return
    model = json.loads((fixture / "model.json").read_text())
    if model["model"]["sourceId"].casefold() != scope.casefold():
        raise ValueError("Capture this model into MIRROR_FIXTURE before watching its signals.")
    checkpoints = [p for p in (fixture / "raw").iterdir() if (p / "bindings.json").is_file()]
    raw = max(checkpoints, key=lambda p: datetime.fromisoformat((p / "readAt.txt").read_text().strip()))
    if not model["bindings"]:
        return
    with tempfile.TemporaryDirectory(dir=pending.parent) as tmp:
        local = Path(tmp)
        shutil.copyfile(fixture / "model.json", local / "model.json")
        at = datetime.now(timezone.utc).isoformat()
        run_python("collect-observations.py", raw, "--at", at, "--output", local / "observations.ndjson")
        run_python("build-space.py", "--fixture", local, "--out", local / "build")
        os.replace(local / "build/capture/observations.ndjson", pending)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scope", help="Azure resource group or captured health model ARM ID")
    parser.add_argument("namespace")
    parser.add_argument("hub")
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--interval", type=int, default=300, help="seconds between successful reads")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    if args.interval < 1:
        parser.error("--interval must be positive")
    key = hashlib.sha256(json.dumps([args.scope.lower(), args.namespace.lower(), args.hub.lower()]).encode()).hexdigest()[:20]
    state = args.work / "watch" / key
    state.mkdir(parents=True, exist_ok=True)
    pending = state / "inventory-pending.ndjson"
    signals = state / "signals-pending.ndjson"

    def send(file):
        if file.exists() and file.stat().st_size:
            subprocess.run(["node", str(HERE / "publish.mjs"), args.namespace, args.hub, str(file)], check=True)
        file.unlink(missing_ok=True)

    with (state / "lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        while True:
            try:
                # The collector reconciles an existing outbox with the local inventory
                # history before it fetches a new snapshot or advances its cursor.
                run_python("collect-inventory.py", args.scope, "--output", state / "inventory.ndjson", "--delta", pending)
                send(pending)
                if not signals.exists():
                    sample_signals(args.fixture, signals, args.scope)
                send(signals)
            except (subprocess.CalledProcessError, OSError, ValueError) as exc:
                if args.once:
                    raise
                print(f"Refresh failed: {exc}. Retrying in {args.interval}s.", file=sys.stderr, flush=True)
            if args.once:
                return
            time.sleep(args.interval)


if __name__ == "__main__":
    main()
