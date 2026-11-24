"""Translate native Azure Monitor diagnostic deliveries to keyed Sattle metric rows."""
from datetime import timedelta
import json
import math
from pathlib import Path
import re

from azure_monitor import iso, timestamp
from record_adapter import record

PROJECT = Path(__file__).parent / "projects" / "ahm-model-demo"


def series(row):
    return (row["armKey"], row["metricNamespace"], row["name"], row["aggregation"])


def resource_group(fixture):
    inventory = fixture / "container-apps.ndjson"
    scopes = {json.loads(line)["meta"]["scopeId"].rstrip("/").lower() for line in inventory.read_text().splitlines() if line}
    if len(scopes) != 1:
        raise ValueError("Expected the inventory of one resource group")
    scope = scopes.pop()
    if not re.fullmatch(r"/subscriptions/[0-9a-f-]{36}/resourcegroups/[^/]+", scope):
        raise ValueError("Invalid resource group")
    return scope


def time_grain(value):
    grain = re.fullmatch(r"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?", value)
    if not grain:
        raise ValueError(f"Unsupported metric time grain: {value}")
    h, m, s = (float(n or 0) for n in grain.groups())
    delta = timedelta(hours=h, minutes=m, seconds=s)
    if delta <= timedelta():
        raise ValueError("A metric needs a positive time grain")
    return delta


def normalize(receipt, scope, catalog):
    """Use the native event time and grain. Keep a numeric zero distinct from a missing aggregation."""
    metrics = {row["name"].lower(): row for row in catalog["metrics"]}
    namespace = catalog["metricNamespace"]
    prefix = scope + "/providers/" + namespace.lower() + "/"
    delivery = {}
    if receipt.get("namespace") and receipt.get("hub"):
        delivery["eventHub"] = receipt["namespace"] + "/" + receipt["hub"]
    for key, field in (("partition", "partitionId"), ("sequenceNumber", "sequenceNumber"), ("enqueuedAt", "enqueuedAt")):
        if receipt.get(key) is not None:
            delivery[field] = str(receipt[key])
    for metric in receipt["records"]:
        arm = metric["resourceId"].rstrip("/").lower()
        match = metrics.get(metric["metricName"].lower())
        if not match or not arm.startswith(prefix) or "/" in arm[len(prefix):] or not arm[len(prefix):]:
            continue
        start = timestamp(metric["time"])
        end = start + time_grain(metric["timeGrain"])
        aggregate = match["aggregation"]
        value = metric.get(aggregate.lower())
        if value is not None and (type(value) not in (float, int) or not math.isfinite(value)):
            raise ValueError(f"Invalid {aggregate} in native metric {metric['metricName']}")
        row = {"armKey": arm, "metricNamespace": namespace, "name": match["name"], "aggregation": aggregate,
               "unit": match["unit"], "source": "AzureDiagnosticSettings", "windowStart": iso(start),
               "windowEnd": iso(end), "sampleAt": iso(start), "status": "noData" if value is None else "value",
               "value": value, **delivery}
        yield record(row["windowEnd"], "PlatformMetricRow", series(row), row)


def records(fixture):
    base = fixture / "diagnostic-events"
    if not base.exists():
        return
    scope = resource_group(fixture)
    catalog = json.loads((PROJECT / "metrics.json").read_text())
    deliveries = [json.loads(path.read_text()) for path in base.rglob("*.json")]
    for receipt in sorted((r for r in deliveries if "records" in r), key=lambda r: (r["enqueuedAt"], r["partition"], r["sequenceNumber"])):
        yield from normalize(receipt, scope, catalog)
