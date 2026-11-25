"""Source plugins. Each has fetch(stream, window, options), an action that asks the source for one window, and
readings(stream, response), a calculation that turns the response into readings: {key, position, values}.

The position is a time in the source, used to resume: the ingestion time of a Log Analytics row, the sample
time of a Prometheus point, the scrape time of a metrics API reading, the enqueued time of an Event Hubs event, the bucket time of a
platform metric. Event time is a value column of the row.
A reading the source does not have is not made up: a gap stays a gap, a null stays null, and a measured zero
stays zero. Prometheus NaN and infinities have no Decimal, so they become null.
"""
from datetime import datetime, timedelta, timezone
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
from tempfile import TemporaryDirectory
import time
from urllib.parse import urlencode

PROMETHEUS_AUDIENCE = "https://prometheus.monitor.azure.com"
LOGS = "https://api.loganalytics.io/v1/workspaces/"
METRICS_API = "/apis/metrics.k8s.io/v1beta1/"


class ReadError(Exception):
    """A failed source read. Only an error code, never the source's text."""


def now():
    return datetime.now(timezone.utc)


def instant(text):
    """A UTC time from an ISO 8601 string with any number of fraction digits."""
    m = re.fullmatch(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?(Z|[+-]\d\d:\d\d)?", text)
    if not m:
        raise ValueError("not an instant")
    base = datetime.fromisoformat(m.group(1) + "." + (m.group(2) or "0")[:6].ljust(6, "0"))
    zone = m.group(3) or "Z"
    offset = timedelta(0) if zone == "Z" else timedelta(hours=int(zone[1:3]), minutes=int(zone[4:6])) * (
        -1 if zone[0] == "-" else 1)
    return (base - offset).replace(tzinfo=timezone.utc)


def iso(value):
    return value.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def compact(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


def digest(value):
    return hashlib.sha256(compact(value).encode()).hexdigest()[:32]


def az_rest(url, *, body=None, audience=None):
    args = ["az", "rest", "--only-show-errors", "--url", url, "--method", "get" if body is None else "post",
            "-o", "json"]
    if body is not None:
        args += ["--body", compact(body)]
    if audience:
        args += ["--resource", audience]
    return _run(args)


def _run(args):
    for attempt in range(3):
        done = subprocess.run(args, text=True, capture_output=True, timeout=120)
        if done.returncode == 0:
            try:
                return json.loads(done.stdout)
            except json.JSONDecodeError as exc:
                raise ReadError("InvalidResponse") from exc
        if attempt < 2 and re.search(r"TooManyRequests|429|ServiceUnavailable|GatewayTimeout|503|504", done.stderr):
            time.sleep(2 ** attempt)
            continue
        m = re.search(r'"code"\s*:\s*"([\w.-]+)"|ERROR: \(([\w.-]+)\)|(NotFound|Forbidden|Unauthorized)', done.stderr)
        raise ReadError(next((g for g in m.groups() if g), "ReadFailed") if m else "ReadFailed")


# Log Analytics: raw rows of one table, in ingestion order

def log_analytics_query(stream, window):
    start, end = window
    where = f"| where {stream['where']} " if stream.get("where") else ""
    return (f"{stream['table']} | extend _position = ingestion_time() "
            f"| where _position > datetime({iso(start)}) and _position <= datetime({iso(end)}) {where}"
            f"| project _position, {', '.join(stream['columns'])} | order by _position asc")


def log_analytics_fetch(stream, window, options):
    start, end = window
    # timespan bounds TimeGenerated, so the ingestion filter does not scan the whole retention
    span = f"{iso(start - timedelta(days=1))}/{iso(end + timedelta(hours=1))}"
    return az_rest(LOGS + options["workspace"] + "/query", audience="https://api.loganalytics.io",
                   body={"query": log_analytics_query(stream, window), "timespan": span})


def log_analytics_value(kind, value):
    if value is None:
        return None
    if kind == "datetime":
        return iso(instant(value))
    if kind in ("int", "long"):
        return int(value)
    if kind in ("real", "decimal"):
        number = float(value)
        return number if math.isfinite(number) else None
    if kind == "bool":
        return bool(value)
    if kind == "dynamic" and not isinstance(value, str):
        return compact(value)
    return value


def log_analytics_readings(stream, response):
    table = next((t for t in response.get("tables", []) if t.get("name") == "PrimaryResult"), None)
    if table is None:
        return []
    columns = [(c["name"], c["type"]) for c in table["columns"]]
    out = []
    for row in table["rows"]:
        values = {name: log_analytics_value(kind, v) for (name, kind), v in zip(columns, row)}
        position = instant(values.pop("_position"))
        key = "/".join(str(values[k]) for k in stream["key"]) if stream.get("key") else digest(values)
        out.append({"key": key, "position": position, "values": values})
    return out


# Prometheus: points of one range query on a fixed step grid

def prometheus_range(stream, window):
    """The step grid inside the window, ending one step before its end so the last point has settled."""
    step = stream.get("step", 60)
    start = math.ceil(window[0].timestamp() / step) * step
    end = math.floor((window[1].timestamp() - step) / step) * step
    return (start, end, step) if end >= start else None


def prometheus_fetch(stream, window, options):
    grid = prometheus_range(stream, window)
    if grid is None:
        return None
    start, end, step = grid
    query = urlencode({"query": stream["query"], "start": start, "end": end, "step": step})
    return az_rest(options["prometheus"].rstrip("/") + "/api/v1/query_range?" + query, audience=PROMETHEUS_AUDIENCE)


def prometheus_number(text):
    number = float(text)
    return number if math.isfinite(number) else None


def prometheus_readings(stream, response):
    if not response or response.get("status") != "success":
        return []
    out = []
    for series in response["data"]["result"]:
        labels = series["metric"]
        identity = compact(sorted(labels.items()))
        for ts, text in series.get("values", []):
            at = datetime.fromtimestamp(float(ts), timezone.utc)
            values = {"metric": stream["name"], "at": iso(at), "value": prometheus_number(text)}
            values.update({name: labels.get(name) for name in stream.get("labels", [])})
            out.append({"key": f"{identity}/{iso(at)}", "position": at, "values": values})
    return out


# Kubernetes metrics API: the latest scrape of every node or pod container

UNITS = {"n": 1e-9, "u": 1e-6, "m": 1e-3, "": 1, "k": 1e3, "M": 1e6, "G": 1e9, "T": 1e12, "P": 1e15, "E": 1e18,
         "Ki": 2 ** 10, "Mi": 2 ** 20, "Gi": 2 ** 30, "Ti": 2 ** 40, "Pi": 2 ** 50, "Ei": 2 ** 60}
DURATION = {"h": 3600, "m": 60, "s": 1, "ms": 1e-3, "us": 1e-6, "µs": 1e-6, "ns": 1e-9}


def quantity(text):
    """A Kubernetes quantity (cpu in cores, memory in bytes)."""
    if text is None:
        return None
    m = re.fullmatch(r"([+-]?[\d.]+(?:[eE][+-]?\d+)?)(Ki|Mi|Gi|Ti|Pi|Ei|[numkMGTPE]?)", text)
    if not m:
        raise ValueError("not a quantity")
    value = float(m.group(1)) * UNITS[m.group(2)]
    return int(value) if value == int(value) and m.group(2) not in ("n", "u", "m") else value


def duration(text):
    """A Go duration such as 20.05s or 1m0.5s, in seconds."""
    if text is None:
        return None
    parts = re.findall(r"([\d.]+)(h|ms|us|µs|ns|m|s)", text)
    return sum(float(n) * DURATION[u] for n, u in parts)


def kubernetes_metrics_fetch(stream, window, options):
    args = ["kubectl", "get", "--raw", METRICS_API + stream["kind"]]
    if options.get("context"):
        args += ["--context", options["context"]]
    return _run(args)


def kubernetes_metrics_readings(stream, response):
    out = []
    for item in (response or {}).get("items", []):
        meta = item["metadata"]
        kind = "node" if "usage" in item else "pod"
        containers = [{"name": None, "usage": item["usage"]}] if kind == "node" else item.get("containers", [])
        at = instant(item["timestamp"])
        for c in containers:
            usage = c.get("usage") or {}
            values = {"kind": kind, "namespace": meta.get("namespace"), "name": meta["name"],
                      "container": c["name"], "at": iso(at), "window": duration(item.get("window")),
                      "cpu": quantity(usage.get("cpu")), "memory": quantity(usage.get("memory"))}
            key = compact([kind, meta.get("namespace"), meta["name"], c["name"], iso(at)])
            out.append({"key": key, "position": at, "values": values})
    return out


# Azure Monitor metrics through Event Hubs: the AllMetrics records a diagnostic setting sends to a hub

CONSUME = Path(__file__).parent.parent / "capture" / "eh-consume.mjs"
AGGREGATES = ("total", "average", "minimum", "maximum", "count")


def grain(text):
    """An ISO 8601 time grain such as PT1M, in seconds."""
    m = re.fullmatch(r"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?", text)
    if not m:
        raise ValueError("not a time grain")
    h, mi, s = (float(n or 0) for n in m.groups())
    return h * 3600 + mi * 60 + s


def azure_monitor_hub_fetch(stream, window, options):
    """The events enqueued since the window start, read to the end the hub holds now."""
    with TemporaryDirectory() as d:
        out = Path(d) / "events.ndjson"
        done = subprocess.run(["node", str(CONSUME), "--namespace", options["eventhubs"], "--hub", stream["hub"],
                               "--out", str(out), "--from", iso(window[0]), "--seconds", "5"],
                              capture_output=True, text=True, timeout=300)
        if done.returncode != 0:
            raise ReadError("ConsumeFailed")
        return [json.loads(line) for line in out.read_text().splitlines() if line.strip()] if out.exists() else []


def azure_monitor_hub_readings(stream, response):
    out = []
    for event in response or []:
        position = instant(event["meta"]["enqueuedTimeUtc"])
        for r in (event.get("payload") or {}).get("records", []):
            if "metricName" not in r:
                continue
            values = {"resourceId": r["resourceId"].lower(), "metric": r["metricName"], "at": iso(instant(r["time"])),
                      "grain": grain(r["timeGrain"])}
            values.update({a: r[a] if isinstance(r.get(a), (int, float)) and math.isfinite(r[a]) else None
                           for a in AGGREGATES})
            key = compact([values["resourceId"], values["metric"], values["at"], values["grain"]])
            out.append({"key": key, "position": position, "values": values})
    return out


# Azure Monitor platform metrics, polled: one-minute buckets of one resource

METRICS_API_VERSION = "2023-10-01"


def azure_metrics_window(stream, window):
    """Whole buckets that closed at least `settle` seconds ago, so a bucket is read once Azure has filled it."""
    grain_s = grain(stream.get("grain", "PT1M"))
    settle = timedelta(seconds=stream.get("settle", 60))
    start = math.floor(window[0].timestamp() / grain_s) * grain_s
    end = math.floor((window[1] - settle).timestamp() / grain_s) * grain_s
    if end <= start:
        return None
    return datetime.fromtimestamp(start, timezone.utc), datetime.fromtimestamp(end, timezone.utc)


def azure_metrics_fetch(stream, window, options):
    span = azure_metrics_window(stream, window)
    if span is None:
        return None
    query = {"api-version": METRICS_API_VERSION, "metricnamespace": stream["namespace"],
             "metricnames": ",".join(stream["metrics"]), "interval": stream.get("grain", "PT1M"),
             "aggregation": ",".join(stream.get("aggregations", ["Total", "Count"])),
             "timespan": f"{iso(span[0])}/{iso(span[1])}"}
    if stream.get("filter"):
        query["$filter"] = stream["filter"]
    resource = options["resources"][stream["resource"]]
    return az_rest("https://management.azure.com" + resource + "/providers/Microsoft.Insights/metrics?"
                   + urlencode(query))


def azure_metrics_readings(stream, response):
    """A bucket without any aggregate is no reading; a bucket with count 0 is a measured zero."""
    out = []
    for metric in (response or {}).get("value", []):
        resource = metric["id"].lower().split("/providers/microsoft.insights/metrics/")[0]
        for series in metric.get("timeseries", []):
            dims = {d["name"]["value"]: d["value"] for d in series.get("metadatavalues", [])}
            for point in series.get("data", []):
                aggregates = {a: point.get(a) for a in AGGREGATES}
                if all(v is None for v in aggregates.values()):
                    continue
                at = instant(point["timeStamp"])
                values = {"resourceId": resource, "resource": stream["resource"], "metric": metric["name"]["value"], "at": iso(at),
                          "grain": grain(stream.get("grain", "PT1M")), **aggregates}
                key = compact([resource, values["metric"], sorted(dims.items()), values["at"]])
                out.append({"key": key, "position": at, "values": values})
    return out


PLUGINS = {
    "log-analytics": (log_analytics_fetch, log_analytics_readings),
    "prometheus": (prometheus_fetch, prometheus_readings),
    "kubernetes-metrics": (kubernetes_metrics_fetch, kubernetes_metrics_readings),
    "azure-monitor-hub": (azure_monitor_hub_fetch, azure_monitor_hub_readings),
    "azure-metrics": (azure_metrics_fetch, azure_metrics_readings),
}


def fetch(stream, window, options):
    return PLUGINS[stream["plugin"]][0](stream, window, options)


def readings(stream, response):
    return PLUGINS[stream["plugin"]][1](stream, response)
