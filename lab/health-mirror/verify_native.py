"""Trace live app telemetry to native Event Hub deliveries in this project's local receipt store."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
from tempfile import NamedTemporaryFile
from urllib.request import urlopen

from platform_metrics import PROJECT, normalize, resource_group, series
from verify import properties, read, same_number


def verify(fixture, port):
    def get(route):
        with urlopen(f"http://127.0.0.1:{port}{route}", timeout=10) as response:
            return json.load(response)
    live = get("/space")["input"]
    if live["mode"] != "live" or live.get("retrying"):
        raise ValueError("The project is not serving a healthy live input")
    extent = get("/graphs/appTelemetry/snapshot")["extent"]
    nodes = [(e["type"]["value"].split(".")[-1], properties(e)) for e in extent["entities"]]
    arrivals, sources = defaultdict(list), Counter()
    catalog, scope = read(PROJECT / "metrics.json"), resource_group(fixture)
    for path in (fixture / "diagnostic-events").rglob("*.json"):
        receipt = read(path)
        for raw in receipt["records"]:
            sources[raw["resourceId"].lower()] += 1
        for row in normalize(receipt, scope, catalog):
            values = row["payload"]["values"]
            arrivals[(*series(values), values["windowStart"], values["windowEnd"])].append((values, receipt, path))
    matched = []
    for kind, metric in nodes:
        if kind != "PlatformMetric":
            continue
        rows = arrivals[(*series(metric), metric["windowStart"], metric["windowEnd"])]
        hit = next(((r, path) for raw, r, path in rows if raw["source"] == metric["source"] == "AzureDiagnosticSettings"
                    and raw["unit"] == metric["unit"] and raw["status"] == metric["status"]
                    and same_number(raw["value"], metric.get("value"))
                    and ("sequenceNumber" not in metric or str(r["sequenceNumber"]) == str(metric["sequenceNumber"]))), None)
        if not hit:
            raise ValueError(f"Missing native receipt for {metric['name']} at {metric['windowStart']}")
        receipt, path = hit
        matched.append({**metric, "namespace": receipt["namespace"], "hub": receipt["hub"],
                        "partition": receipt["partition"], "sequenceNumber": receipt["sequenceNumber"], "receipt": str(path)})
    if not matched:
        raise ValueError("Waiting for a native diagnostic metric to appear in the project")
    for graph in ("health", "signals"):
        if get(f"/graphs/{graph}/snapshot")["extent"]["entities"]:
            raise ValueError("The standalone graph contains official model inputs")
    return {"matched": matched, "apps": {p["armKey"]: sources[p["armKey"]] for k, p in nodes if k == "ContainerApp"}}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.fixture, args.port)
    with NamedTemporaryFile(mode="w", dir=args.out.parent, delete=False) as output:
        json.dump(result, output, indent=2)
    Path(output.name).replace(args.out)
    for arm, count in result["apps"].items():
        print(f"{arm.split('/')[-1]}: {count} received metric records")
    print(f"{len(result['matched'])} live readings traced to their native Event Hub receipts")
