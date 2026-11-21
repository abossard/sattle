#!/usr/bin/env python3
"""Compare a live listener's recording and graph contents with the local file replay."""
import argparse
from collections import Counter
import json
from pathlib import Path
from urllib.request import urlopen


def load(url):
    with urlopen(url, timeout=10) as response:
        return json.load(response)


def records(paths):
    return Counter(json.dumps(json.loads(line), sort_keys=True) for path in paths
                   for line in path.read_text().splitlines() if line.strip())


def verify(work, port):
    url = f"http://127.0.0.1:{port}"
    space = load(url + "/space")
    stream = space["input"]
    if stream.get("mode") != "live" or stream.get("retrying") or not stream.get("record"):
        raise ValueError("Start the live listener and wait for it to connect.")
    expected = records(sorted((work / "capture").glob("*.ndjson")))
    received = records([Path(stream["record"])])
    matched = expected == received and bool(expected)
    print(f"Event Hubs delivered {sum((expected & received).values())}/{sum(expected.values())} expected envelopes; "
          f"{sum((received - expected).values())} additional envelopes")
    graphs = {}
    for path in sorted((work / "replay").glob("*.final.snapshot.json")):
        graph = path.name.split(".")[0]
        if graph == "catalog":
            continue
        replay = json.loads(path.read_text())["extent"]
        live = load(f"{url}/graphs/{graph}/snapshot")["extent"]
        # Wire order is presentation only. Full IDs, properties, endpoints and references must match.
        def canonical(items):
            return sorted(json.dumps(item, sort_keys=True) for item in items)
        equal = replay.keys() == live.keys() and all(canonical(replay[k]) == canonical(live[k]) for k in replay)
        print(f"{graph}: {len(live['entities'])} nodes, {len(live['relationships'])} edges · {'PASS' if equal else 'FAIL'}")
        graphs[graph] = equal
    passed = matched and bool(graphs) and all(graphs.values())
    report = {"passed": passed, "position": stream["position"], "events": sum(received.values()), "extents": graphs}
    (work / "transport.json").write_text(json.dumps(report, indent=2) + "\n")
    return passed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()
    raise SystemExit(0 if verify(args.work, args.port) else 1)
