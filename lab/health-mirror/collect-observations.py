#!/usr/bin/env python3
"""Read the deployed bindings from Azure Monitor. Requires an authenticated Azure CLI."""
import argparse
import fcntl
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from datetime import timedelta
from functools import lru_cache
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from urllib.parse import quote, urlencode

from azure_monitor import ARM, LOGS, ReadError, azure, iso, read, timestamp, workspace_id


def duration(value):
    match = re.fullmatch(r"P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?", value)
    if not match:
        raise ValueError(f"unsupported signal time grain {value}")
    days, hours, minutes, seconds = (int(n or 0) for n in match.groups())
    grain = timedelta(days=days, hours=hours, minutes=minutes, seconds=seconds)
    if not grain:
        raise ValueError("signal time grain must be positive")
    return grain


def missing(code):
    return {"status": "noData", "errorCode": code, "value": None}


def numeric(value):
    if value is None:
        return missing("NullValue")
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ReadError("NonNumericValue")
    return {"status": "value", "value": value}


def metric(source, signal, start, end, subscription):
    aggregation = signal["aggregationType"]
    params = {"api-version": "2023-10-01", "metricnamespace": signal["metricNamespace"],
              "metricnames": signal["metricName"], "aggregation": aggregation,
              "timespan": iso(start) + "/" + iso(end), "interval": "FULL"}
    if signal.get("dimensionFilter"):
        params["$filter"] = signal["dimensionFilter"]
    url = ARM + source["azureResourceId"] + "/providers/Microsoft.Insights/metrics?" + urlencode(params)
    answer = azure(url, subscription)
    records = answer["value"]
    if len(records) != 1 or records[0]["name"]["value"].lower() != signal["metricName"].lower():
        raise ReadError("UnexpectedMetricResponse")
    record = records[0]
    if record.get("errorCode", "Success") != "Success":
        raise ReadError(record["errorCode"])
    series = record["timeseries"]
    if len(series) > 1:
        raise ReadError("AmbiguousMetricDimensions")
    points = series[0]["data"] if series else []
    if len(points) > 1:
        raise ReadError("AmbiguousMetricWindow")
    if not points:
        return missing("NoMetricPoints")
    return {**numeric(points[0].get(aggregation.lower())), "sampleAt": points[0]["timeStamp"]}

@lru_cache(maxsize=32)
def blob_child(account, subscription):
    children = azure(ARM + account + "/blobServices?api-version=2023-05-01", subscription)
    matches = [r["id"] for r in children["value"] if r["type"].casefold() == "microsoft.storage/storageaccounts/blobservices"
               and r["id"].casefold().startswith(account.casefold() + "/blobservices/")]
    if children.get("nextLink") or len(matches) != 1:
        raise ReadError("AmbiguousBlobService")
    return matches[0]


def bound_metric(source, signal, start, end, subscription):
    try:
        return metric(source, signal, start, end, subscription)
    except ReadError as exc:
        if str(exc) != "InvalidMetricNamespace" or signal["metricNamespace"].casefold() != "microsoft.storage/storageaccounts/blobservices":
            raise
    # Resolve the metric namespace to the single ARM child that owns it.
    declared = {"status": "queryError", "value": None, "errorCode": "InvalidMetricNamespace",
                "resourceType": "Microsoft.Storage/storageAccounts"}
    try:
        child = blob_child(source["azureResourceId"], subscription)
        result = metric({"azureResourceId": child}, signal, start, end, subscription)
    except ReadError as exc:
        return {"status": "queryError", "value": None, "errorCode": str(exc), "declaredScopeObservation": declared}
    return {**result, "resolvedScopeType": "Microsoft.Storage/storageAccounts/blobServices",
            "declaredScopeObservation": declared}


def logs(source, signal, start, end, subscription):
    workspace = workspace_id(source["logAnalyticsWorkspaceResourceId"], subscription)
    url = f"{LOGS}/v1/workspaces/{quote(workspace, safe='')}/query"
    # Keep the deployed query intact. Fix its now()/ago() clock for historical replay.
    # Absolute timespan independently limits the rows read by the workspace.
    query = signal["queryText"]
    if "{{" in query or "}}" in query:
        raise ReadError("UnsupportedQueryTemplate")
    body = {"query": f"set query_now=datetime({iso(end)});\n{query}", "timespan": iso(start) + "/" + iso(end)}
    answer = azure(url, subscription, audience=LOGS, query=body)
    if answer.get("error"):
        raise ReadError(answer["error"].get("code", "PartialQueryError"))
    table = next((t for t in answer["tables"] if t["name"] == "PrimaryResult"), None)
    if not table:
        raise ReadError("MissingQueryResult")
    columns = [c["name"] for c in table["columns"]]
    column = signal["valueColumnName"]
    if column not in columns:
        raise ReadError("MissingValueColumn")
    if not table["rows"]:
        return missing("NoRows")
    return {**numeric(table["rows"][0][columns.index(column)]), "rowCount": len(table["rows"])}


def resource_health(source, end, subscription):
    url = ARM + source["azureResourceId"] + "/providers/Microsoft.ResourceHealth/availabilityStatuses?api-version=2025-05-01"
    candidates = []
    seen = set()
    while url:
        if url in seen:
            raise ReadError("RepeatedResourceHealthPage")
        seen.add(url)
        answer = azure(url, subscription)
        for entry in answer["value"]:
            state = entry["properties"]
            # Azure spells this property "occuredTime". History records the start of the state.
            occurred = state.get("occuredTime") or state.get("occurredTime")
            if occurred and timestamp(occurred) <= end:
                candidates.append((timestamp(occurred), state))
        url = answer.get("nextLink")
    if not candidates:
        return missing("NoAvailabilityRecord")
    occurred, state = max(candidates, key=lambda item: item[0])
    return {"status": "value", "value": None, "availabilityState": state["availabilityState"],
            "sampleAt": iso(occurred), "sourceReportedAt": state.get("reportedTime")}


def observations(raw, at=None, workers=4):
    model = read(raw / "model.json")
    subscription = model["id"].split("/")[2]
    defs = {d["name"]: d["properties"] for d in read(raw / "signalDefinitions.json")}
    entities = {e["name"]: e["properties"] for e in read(raw / "entities.json")}
    binding_map = read(raw / "bindings.json")
    if not binding_map:
        raise ValueError("no bindings in the private export")
    checkpoint = timestamp(at or (raw / "readAt.txt").read_text().strip())

    def collect(binding):
        entity = entities[binding["entityName"]]
        group = entity["signalGroups"][binding["group"]]
        name = binding["signalName"]
        signal = next((s for s in group.get("signals", []) if s["name"] == name), None)
        if signal is None and group.get("resourceHealth", {}).get("signalName") == name:
            signal = {**group["resourceHealth"], "signalKind": "AzureResourceHealth"}
        if signal is None:
            signal = {"signalKind": "UnsupportedBinding"}
        definition_name = signal.get("signalDefinitionName")
        if definition_name and definition_name not in defs:
            raise ValueError("unresolved signal definition in private export")
        config = {**defs.get(definition_name, {}), **signal}
        # For point-in-time parity use the evaluation CLOCK only. Values and health states
        # are read independently from Monitor; the service status is never a measurement.
        reported = signal.get("status", {}).get("reportedAt")
        end = checkpoint if at or not reported else timestamp(reported)
        kind = config["signalKind"]
        source_kind = {"AzureResourceMetric": "AzureMonitorMetrics", "LogAnalyticsQuery": "LogAnalytics",
                       "AzureResourceHealth": "AzureResourceHealth"}.get(kind, kind)
        base = {k: binding[k] for k in ("bindingId", "entityId", "definitionId")}
        base.update({"checkpoint": iso(checkpoint), "windowStart": None, "windowEnd": iso(end), "source": source_kind,
                     "aggregation": config.get("aggregationType") if kind == "AzureResourceMetric" else
                         config.get("valueColumnName") if kind == "LogAnalyticsQuery" else None})
        if kind == "AzureResourceMetric":
            # Platform metrics have minute time buckets; drop the unfinished minute.
            end = end.replace(second=0, microsecond=0)
        try:
            if kind in ("AzureResourceMetric", "LogAnalyticsQuery"):
                start = end - duration(config["timeGrain"])
                base.update(windowStart=iso(start), windowEnd=iso(end))
                reader = bound_metric if kind == "AzureResourceMetric" else logs
                result = reader(group, config, start, end, subscription)
            elif kind == "AzureResourceHealth":
                result = resource_health(group, end, subscription)
            else:
                result = {"status": "unsupported", "value": None, "errorCode": "UnsupportedSignalKind"}
        except (ReadError, subprocess.TimeoutExpired) as exc:
            result = {"status": "queryError", "errorCode": str(exc) if isinstance(exc, ReadError) else "CollectorTimeout", "value": None}
        return {**base, **result}

    with ThreadPoolExecutor(max_workers=workers) as pool:
        return list(pool.map(collect, binding_map))


def store(rows, output):
    keys = {(row["checkpoint"], row["bindingId"]) for row in rows}
    if len(keys) != len(rows):
        raise ValueError("duplicate binding identity")
    output.parent.mkdir(parents=True, exist_ok=True)
    directory = os.open(output.parent, os.O_RDONLY)
    try:
        fcntl.flock(directory, fcntl.LOCK_EX)
        prior = [json.loads(line) for line in output.read_text().splitlines()] if output.exists() else []
        retained = [r for r in prior if (r["checkpoint"], r["bindingId"]) not in keys]
        with tempfile.NamedTemporaryFile(mode="w", dir=output.parent, delete=False) as out:
            for row in sorted(retained + rows, key=lambda r: (r["checkpoint"], r["bindingId"])):
                out.write(json.dumps(row, separators=(",", ":"), allow_nan=False) + "\n")
        Path(out.name).replace(output)
    finally:
        os.close(directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path, help="private ARM export directory from export-model.py")
    parser.add_argument("--output", type=Path, required=True, help="observation NDJSON; checkpoints accumulate on successive runs")
    parser.add_argument("--at", help="UTC checkpoint override; default reads each binding's evaluation window")
    args = parser.parse_args()
    rows = observations(args.raw, args.at)
    store(rows, args.output)
    summary = Counter(row["status"] for row in rows)
    print(rows[0]["checkpoint"], dict(sorted(summary.items())))
    for row in rows:
        if row["status"] in {"queryError", "unsupported"}:
            print(row["bindingId"], row["errorCode"], file=sys.stderr)
    return int(any(row["status"] in {"unsupported", "queryError"} for row in rows))


if __name__ == "__main__":
    sys.exit(main())
