"""Serve the model-free projection and feed it native deliveries from its dedicated metric hub."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import selectors
import shutil
import signal
import subprocess
import sys
import tempfile

from azure_monitor import timestamp
from platform_metrics import PROJECT, normalize, records, resource_group
from record_adapter import compact

HERE = Path(__file__).resolve().parent


def editor_dir(work):
    source = work / "definitions"
    digest = hashlib.sha256()
    for path in sorted(source.glob("*.gkql.json")):
        digest.update(path.name.encode() + b"\0" + path.read_bytes() + b"\0")
    target = work / "editors" / digest.hexdigest()[:24]
    if not target.exists():
        target.parent.mkdir(exist_ok=True)
        shutil.copytree(source, target)
    return target


def seed(capture, fixture, stream):
    # The static capture supplies the vocabulary, inventory and Kusto rows. Native receipts supply every metric.
    inputs = []
    for path in sorted(capture.glob("*.ndjson")):
        if path.name == "platform-metrics.ndjson":
            continue
        for line in path.read_text().splitlines():
            if line:
                inputs.append(json.loads(line))
    inputs.extend(records(fixture))
    for row in sorted(inputs, key=lambda r: timestamp(r["capturedAt"])):
        stream.write(compact(row) + "\n")
    stream.flush()


def listen(args):
    if not (args.work / "definitions").is_dir():
        raise ValueError("Run ./scripts/health-mirror.sh prepare-project first")
    scope = resource_group(args.fixture)
    catalog = json.loads((PROJECT / "metrics.json").read_text())
    runs = args.work / "native-runs"
    runs.mkdir(exist_ok=True)
    run = Path(tempfile.mkdtemp(dir=runs, prefix="run."))
    spool = run / "input.ndjson"
    children = []
    def stop(*_):
        for proc in children:
            if proc.poll() is None:
                proc.terminate()
    stopping = False
    def shutdown(*_):
        nonlocal stopping
        stopping = True
        stop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, shutdown)
    try:
        with spool.open("w") as stream:
            seed(args.work / "capture", args.fixture, stream)
            engine = subprocess.Popen(["dotnet", str(args.dll), "serve", "--live", "--follow", str(spool),
                "--defs", str(editor_dir(args.work)), "--space", "healthMirror", "--port", str(args.port),
                "--write-definitions", "--lateness", "0s", "--live-out", str(run)])
            children.append(engine)
            listener = subprocess.Popen(["node", str(HERE / "listen-diagnostics.mjs"), "--namespace", args.namespace,
                "--hub", args.hub, "--scope", scope, "--fixture", str(args.fixture)], stdout=subprocess.PIPE)
            children.append(listener)
            # Use unbuffered pipe reads: several notifications may arrive in a single event batch.
            pending = b""
            with selectors.DefaultSelector() as selector:
                selector.register(listener.stdout, selectors.EVENT_READ)
                while not stopping and engine.poll() is None:
                    if not selector.select(timeout=1):
                        continue
                    chunk = os.read(listener.stdout.fileno(), 65536)
                    if not chunk:
                        break
                    lines = (pending + chunk).split(b"\n")
                    pending = lines.pop()
                    for line in lines:
                        if not line:
                            continue
                        receipt_file = Path(os.fsdecode(line)).resolve()
                        if not receipt_file.is_relative_to((args.fixture / "diagnostic-events").resolve()):
                            raise ValueError("Unexpected diagnostic receipt path")
                        receipt = json.loads(receipt_file.read_text())
                        for envelope in normalize(receipt, scope, catalog):
                            stream.write(compact(envelope) + "\n")
                        stream.flush()
            if not stopping:
                raise RuntimeError(f"Diagnostic pipeline stopped (engine={engine.poll()}, receiver={listener.poll()})")
    finally:
        stop()
        for proc in children:
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ("fixture", "work", "dll"):
        parser.add_argument("--" + arg, type=Path, required=True)
    parser.add_argument("--port", required=True)
    parser.add_argument("--namespace")
    parser.add_argument("--hub")
    args = parser.parse_args()
    args.fixture = args.fixture.resolve()
    args.work = args.work.resolve()
    if not args.namespace and not args.hub:
        root = args.fixture / "diagnostic-events"
        hubs = {file.parents[1] for file in root.glob("*/*/*/*.json")}
        if len(hubs) != 1:
            parser.error("Pass the namespace and hub when the fixture has no unique recorded destination.")
        hub = hubs.pop()
        args.namespace, args.hub = hub.parent.name, hub.name
    elif not args.namespace or not args.hub:
        parser.error("Pass the namespace and hub together.")
    args.work.mkdir(parents=True, exist_ok=True)
    with (args.work / "native-listener.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            parser.exit(1, "This project already has a diagnostic listener. Run ./scripts/health-mirror.sh verify-diagnostics to inspect its readings.\n")
        listen(args)
