"""Read-only Azure Monitor REST requests through the authenticated Azure CLI."""
from datetime import datetime, timezone
from functools import lru_cache
import json
import re
import subprocess
import time
from urllib.parse import urlsplit

ARM = "https://management.azure.com"
LOGS = "https://api.loganalytics.io"


def timestamp(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("checkpoint must include a timezone")
    return parsed.astimezone(timezone.utc)


def iso(value):
    return value.isoformat().replace("+00:00", "Z")


class ReadError(Exception):
    """Only an error code enters the scrubbed fixture."""


def azure(url, subscription, *, query=None, audience=None):
    if urlsplit(url).hostname not in {"management.azure.com", "api.loganalytics.io"}:
        raise ReadError("UnexpectedAzureHost")
    args = ["az", "rest", "--subscription", subscription, "--only-show-errors", "--url", url,
            "--method", "get" if query is None else "post", "-o", "json"]
    if query is not None:
        args.extend(["--body", json.dumps(query)])
    if audience:
        args.extend(["--resource", audience])
    for attempt in range(3):
        response = subprocess.run(args, text=True, capture_output=True, timeout=100)
        if response.returncode == 0:
            try:
                return json.loads(response.stdout)
            except json.JSONDecodeError as exc:
                raise ReadError("InvalidAzureResponse") from exc
        error = response.stderr
        if attempt < 2 and any(s in error for s in ("TooManyRequests", "429", "ServiceUnavailable", "GatewayTimeout")):
            time.sleep(2 ** attempt)
            continue
        if "Detect invalid value:" in error and "for query parameter: 'metricnamespace'" in error:
            raise ReadError("InvalidMetricNamespace")
        match = re.search(r'"code"\s*:\s*"([\w.-]+)"|ERROR: \(([\w.-]+)\)', error)
        code = (match.group(1) or match.group(2)) if match else "AzureReadFailed"
        raise ReadError(code)


def read(path):
    return json.loads(path.read_text())


@lru_cache(maxsize=64)
def workspace_id(resource, subscription):
    doc = azure(ARM + resource + "?api-version=2023-09-01", subscription)
    return doc["properties"]["customerId"]
