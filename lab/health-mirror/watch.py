#!/usr/bin/env python3
"""Poll a deployment, deliver pending native records, and resume after an interrupted send."""
import argparse
import fcntl
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time

from container_inputs import arm_records, inventory_events

HERE = Path(__file__).resolve().parent


def run_python(script, *args):
    subprocess.run([sys.executable, "-B", str(HERE / script), *map(str, args)], check=True)


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
    history = state / "inventory.ndjson"

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
                run_python("collect-inventory.py", args.scope, "--output", history, "--delta", pending)
                if pending.exists() and pending.stat().st_size:
                    normalized = state / "inventory-records.ndjson"
                    keys = {event["key"] for event in inventory_events(pending)}
                    rows = arm_records(inventory_events(history), pending_keys=keys)
                    with normalized.open("w") as output:
                        for row in rows:
                            output.write(json.dumps(row, separators=(",", ":")) + "\n")
                    send(normalized)
                pending.unlink(missing_ok=True)
            except (subprocess.CalledProcessError, OSError, ValueError) as exc:
                if args.once:
                    raise
                print(f"Refresh failed: {exc}. Retrying in {args.interval}s.", file=sys.stderr, flush=True)
            if args.once:
                return
            time.sleep(args.interval)


if __name__ == "__main__":
    main()
