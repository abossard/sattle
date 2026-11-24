"""Check each projected app metric and step observation against the local Azure response."""
import argparse
from decimal import Decimal
from pathlib import Path

from azure_monitor import timestamp
from container_inputs import request_records
from platform_metrics import records, series
from verify import read, same_number
from verify_workloads import Graph, expect, ndjson


def comparable(row):
    # The wire format encodes decimals and integers as strings.
    numeric = {"value", "calls", "spanCount", "failed", "unknown", "attributedCalls", "attributedFailed", "attributedUnknown"}
    return {key: Decimal(str(value)) if key in numeric and value is not None else value for key, value in row.items()}


def verify(fixture, replay):
    graph = Graph(replay, "appTelemetry")
    apps = {app["armKey"] for app in graph.typed("ContainerApp")}
    expected = {}
    capture = replay.parent / "capture" / "platform-metrics.ndjson"
    # More native events can arrive after an offline replay. Compare the exact input to that replay.
    source_values = {(*series(v), v["windowStart"], v["windowEnd"], v["value"]) for r in records(fixture)
                     for v in [r["payload"]["values"]]}
    for record in sorted(ndjson(capture), key=lambda r: timestamp(r["capturedAt"])):
        row = record["payload"]["values"]
        if row["source"] != "AzureDiagnosticSettings" or (*series(row), row["windowStart"], row["windowEnd"], row["value"]) not in source_values:
            raise ValueError(f"Missing diagnostic receipt for {row['name']} at {row['windowStart']}")
        if row["armKey"] in apps:
            expected[series(row)] = comparable(row)
    metrics = {series(m): comparable(m) for m in graph.typed("PlatformMetric")}
    expect("metric values, units and windows", metrics, expected)
    linked = {(app["armKey"], series(metric)) for kind, (_, app), (_, metric) in graph.edges if kind == "HasPlatformMetric"}
    expect("metric resource identity", linked, {(key[0], key) for key in expected})

    checks = {}
    for file, table in [("request-observations.ndjson", "AppRequests"), ("request-dependencies.ndjson", "AppDependencies")]:
        for record in request_records(fixture / file, table):
            row = record["payload"]["values"]
            checks[row["selector"]] = row
    windows = graph.typed("OperationWindow")
    for window in windows:
        expect("Kusto row " + window["selector"], comparable(window), comparable(checks[window["selector"]]))
    observed = {w["selector"] for k, _, (_, w) in graph.edges if k == "ObservedOperation"}
    expect("observations attached to a step", observed, {w["selector"] for w in windows})
    original = Graph(replay, "workloadFlows")
    required = {(step["key"], w["selector"]) for k, (_, step), (_, w) in original.edges if k == "ObservedOperation"}
    visible = {(step["key"], w["selector"]) for k, (_, step), (_, w) in graph.edges if k == "ObservedOperation"}
    expect("preserved step evidence", visible, required)
    return {"apps": len(apps), "metrics": len(metrics), "projectedKustoRows": len(windows), "stepEvidenceLinks": len(visible)}


def compare_official_metrics(fixture, replay):
    """Compare only matching resources, metric definitions, filters, aggregations and UTC windows."""
    model = read(fixture / "model.json")
    references = read(fixture / "reference.json")["checkpoints"]
    windows = {(timestamp(row["checkpoint"]), row["bindingId"]): row for row in ndjson(fixture / "observations.ndjson")}
    graph = Graph(replay, "appTelemetry")
    readings = {(*series(m), m["windowStart"], m["windowEnd"]): m for m in graph.typed("PlatformMetric")}
    definitions = {d["name"]: d["properties"] for d in model["signalDefinitions"]}
    bindings = {}
    for entity in model["entities"]:
        group = entity["properties"].get("signalGroups", {}).get("azureResource", {})
        for signal in group.get("signals", []):
            cfg = {**definitions.get(signal.get("signalDefinitionName"), {}), **signal}
            if cfg.get("metricName") and not cfg.get("dimensionFilter"):
                key = "/".join([entity["name"], "azureResource", signal["name"]])
                bindings[key] = (group["azureResourceId"].lower(), cfg["metricNamespace"], cfg["metricName"], cfg["aggregationType"])
    matches = 0
    for checkpoint in references:
        for signal in checkpoint["signals"]:
            key = signal["bindingId"]
            window = windows.get((timestamp(checkpoint["capturedAt"]), key))
            if key in bindings and window:
                reading = readings.get((*bindings[key], window["windowStart"], window["windowEnd"]))
                if reading:
                    expect(key + " reported value", same_number(reading.get("value"), signal.get("value")), True)
                    matches += 1
    return matches


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--replay", type=Path, required=True)
    args = parser.parse_args()
    for name, count in verify(args.fixture, args.replay).items():
        print(f"{name}: {count}")
    if (args.fixture / "reference.json").exists():
        print("official metric bindings at matching windows:", compare_official_metrics(args.fixture, args.replay))
