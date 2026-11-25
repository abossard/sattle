#!/usr/bin/env python3
"""Export an existing Azure Monitor health model, its bindings and separate reference states."""
import argparse
import fcntl
import hashlib
import hmac
import json
import os
import re
import subprocess
import tempfile
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlencode, urlparse

ARM = "https://management.azure.com"
API = "2026-11-01"
COLLECTIONS = ("entities", "relationships", "signalDefinitions", "authenticationSettings", "discoveryRules")
MODEL_ID = re.compile(r"/subscriptions/[\da-f-]{36}/resourceGroups/[^/?#]+/providers/Microsoft\.CloudHealth/healthModels/[^/?#]+", re.I)
UUID = re.compile(r"(?<![\da-f])[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}(?![\da-f])", re.I)
SENSITIVE = re.compile(r"(?:AccountKey|SharedAccessSignature|SharedAccessKey|client_secret|password|sig)\s*="
                       r"|-----BEGIN (?:\w+ )*PRIVATE KEY-----|\bBearer [A-Za-z0-9_-]{16,}", re.I)
SECRET_FIELD = re.compile(r"(?:apiKey|accountKey|password|clientSecret|secretValue|accessToken|refreshToken|privateKey)", re.I)
# an availability test's name is a deployment name, read from the queries that select it
AVAILABILITY_TEST = re.compile(r'AppAvailabilityResults\b.*?\bName\s*==\s*"([^"]+)"', re.S)


def alias(salt, kind, name):
    """kind-<12 hex>: an HMAC under a local secret salt, so a guessed name cannot be confirmed from the alias."""
    return kind + "-" + hmac.new(salt.encode(), f"{kind}:{name.casefold()}".encode(), hashlib.sha256).hexdigest()[:12]


def read_json(path):
    return json.loads(path.read_text())


def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, delete=False) as handle:
        json.dump(data, handle, ensure_ascii=False, indent=2, sort_keys=True, allow_nan=False)
        handle.write("\n")
    os.replace(handle.name, path)


def capture(model_id, directory):
    if not MODEL_ID.fullmatch(model_id):
        raise ValueError("Pass the full ARM resource ID of one Microsoft.CloudHealth/healthModels resource.")
    directory.mkdir(parents=True, mode=0o700, exist_ok=True)
    if any(directory.iterdir()):
        raise ValueError("Choose an empty private raw capture directory.")
    if directory.stat().st_mode & 0o077:
        raise ValueError("Raw Azure data is private. Use a directory with mode 0700.")
    base = ARM + model_id

    def read(kind):
        path = base if kind == "model" else base + "/" + kind
        url = path + "?" + urlencode({"api-version": API})
        pages, seen = [], set()
        while url:
            if url in seen or urlparse(url)._replace(query="").geturl().lower() != path.lower():
                raise ValueError(f"{kind}: invalid or repeated Azure nextLink")
            seen.add(url)
            proc = subprocess.run(["az", "rest", "--method", "get", "--url", url,
                                   "--output", "json", "--only-show-errors"], check=True, stdout=subprocess.PIPE)
            result = json.loads(proc.stdout)
            if kind == "model":
                save(directory / "model.json", result)
                return
            pages.extend(result["value"])
            url = result.get("nextLink")
        save(directory / (kind + ".json"), pages)
        if kind == "entities":
            (directory / "readAt.txt").write_text(datetime.now(timezone.utc).isoformat())

    with ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(read, ("model", *COLLECTIONS)))


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings(item)


class Names:
    """Keep native names locally. Optional aliases join the same IDs in labels, KQL, and bindings."""
    def __init__(self, raw, salt):
        self.match = None
        if not salt:
            return
        self.salt = salt
        self.names = {}
        model = raw["model"]
        environment = model.get("tags", {}).get("azd-env-name")
        if environment:
            self.names[environment.lower()] = "demo-workload"
        for value in strings(raw):
            if not value.lower().startswith("/subscriptions/"):
                continue
            parts = value.strip("/").split("/")
            if len(parts) < 4:
                continue
            for i in range(1, len(parts), 2):
                parent = parts[i-1].lower()
                item = parts[i]
                if parent == "providers" or item.lower() in ("default",):
                    continue
                if parent in ("entities", "relationships", "signaldefinitions", "authenticationsettings", "discoveryrules"):
                    continue  # children are logical model names; the corresponding collection owns the key
                self.names[item.lower()] = alias(salt, "subscription" if parent == "subscriptions" else
                                                 "group" if parent == "resourcegroups" else "resource", item)
        for value in strings(raw):
            for test in AVAILABILITY_TEST.findall(value):
                self.names[test.lower()] = alias(salt, "test", test)
        self.names[model["name"].lower()] = "model-root"
        for entity in raw["entities"]:
            if entity["properties"].get("discoveredBy"):
                label = entity["properties"].get("displayName", "")
                if label and not re.search(r"\s", label):
                    self.names.setdefault(label.lower(), alias(salt, "resource", label))
        pattern = "|".join(re.escape(v) for v in sorted(self.names, key=len, reverse=True))
        self.match = re.compile(r"(?<![a-z0-9])(?:" + pattern + r")(?![a-z0-9])", re.I)

    def text(self, value):
        if SENSITIVE.search(value):
            raise ValueError("A configuration string contains a credential. Remove it before export.")
        if not self.match:
            return value
        value = self.match.sub(lambda m: self.names[m.group().lower()], value)
        return UUID.sub(lambda m: alias(self.salt, "id", m.group()), value)

    def clean(self, value):
        if isinstance(value, dict):
            if any(SECRET_FIELD.fullmatch(k) and v for k, v in value.items()):
                raise ValueError("A credential field contains a value. Remove it before export.")
            return {k: self.clean(v) for k, v in value.items() if k != "provisioningState"}
        if isinstance(value, list):
            return [self.clean(v) for v in value]
        return self.text(value) if isinstance(value, str) else value


def split_export(raw, timestamp, scrub):
    names = Names(raw, scrub)
    model = raw["model"]
    config = {"schemaVersion": 1, "model": {
        "name": names.text(model["name"]), "sourceId": names.text(model["id"]),
        "location": model["location"], "type": model["type"],
        "properties": names.clean(model.get("properties", {})),
        "identityType": model.get("identity", {}).get("type"), "tags": names.clean(model.get("tags", {})),
    }}
    for kind in COLLECTIONS:
        config[kind] = sorted((
            {"name": names.text(r["name"]), "properties": names.clean(r["properties"])} for r in raw[kind]
        ), key=lambda r: r["name"])
    held_out = {"capturedAt": timestamp, "entities": [], "signals": []}
    bindings, private_map = [], []
    definitions = {r["name"] for r in config["signalDefinitions"]}
    raw_entities = {names.text(r["name"]): r for r in raw["entities"]}

    def binding(entity, group, name, status, raw_name, definition=None):
        key = "/".join((entity, group, name))
        def_id = definition or key
        if definition and definition not in definitions:
            raise ValueError(f"{key}: referenced signal definition {definition} missing")
        row = {"id": key, "entityId": entity, "group": group, "signalName": name, "definitionId": def_id}
        bindings.append(row)
        private_map.append({"entityName": raw_entities[entity]["name"], "group": group,
                            "signalName": raw_name, "bindingId": key, "entityId": entity, "definitionId": def_id})
        reference = {"bindingId": key, "state": status.get("healthState"), "reportedAt": status.get("reportedAt")}
        for field in ("value", "availabilityState"):
            if field in status:
                reference[field] = status[field]
        if status.get("error"):
            message = str(status["error"]).lower()
            if "insufficient access" in message or "authorizationfailed" in message:
                reference["errorCode"] = "AuthorizationFailed"
            elif "no rows" in message:
                reference["errorCode"] = "NoData"
            else:
                reference["errorCode"] = "SourceError"
        held_out["signals"].append(reference)

    for entity in config["entities"]:
        eid, props = entity["name"], entity["properties"]
        held_out["entities"].append({"entityId": eid, "state": props.pop("healthState", None)})
        for group in props.get("signalAggregationGroups", []):
            observed = {k: group.pop(k) for k in ("aggregatedHealthState", "unresolvedMembers") if k in group}
            if observed:
                held_out.setdefault("aggregationGroups", []).append({"entityId": eid, "name": group["name"], **observed})
        for group, data in props.get("signalGroups", {}).items():
            raw_group = raw_entities[eid]["properties"]["signalGroups"][group]
            raw_signals = {names.text(s["name"]): s for s in raw_group.get("signals", [])}
            for signal in data.get("signals", []):
                name = signal["name"]
                binding(eid, group, name, signal.pop("status", {}), raw_signals[name]["name"],
                        signal.get("signalDefinitionName"))
            health = data.get("resourceHealth", {})
            if health:
                status = health.pop("status", {})
                if health.get("enabled") == "Enabled":
                    previous_name = health["signalName"]
                    if scrub:
                        health["signalName"] = "resource-health"
                        for aggregation in props.get("signalAggregationGroups", []):
                            aggregation["members"] = [
                                "resource-health" if member == previous_name else member for member in aggregation["members"]
                            ]
                    binding(eid, group, health["signalName"], status, raw_group["resourceHealth"]["signalName"])
            if data.get("healthState") is not None or "status" in data:
                raise ValueError(f"{eid}/{group}: unhandled runtime state in signal group")
    config["bindings"] = sorted(bindings, key=lambda b: b["id"])
    entities = {e["name"] for e in config["entities"]}
    if not entities or len(entities) != len(raw["entities"]):
        raise ValueError("Empty or ambiguous entity list")
    if len({b["id"] for b in bindings}) != len(bindings):
        raise ValueError("Two Azure bindings have the same local identity")
    for edge in config["relationships"]:
        if not {edge["properties"][end] for end in ("parentEntityName", "childEntityName")} <= entities:
            raise ValueError(f'Unresolved edge {edge["name"]}')
    held_out["signals"].sort(key=lambda s: s["bindingId"])
    return config, held_out, private_map


def export(directory, out, phase="comparison", scrub=False):
    if directory.resolve() == out.resolve() or (scrub and out.resolve() in directory.resolve().parents):
        raise ValueError("Keep private ARM files separate from an exported configuration and outside any scrubbed fixture.")
    raw = {k: read_json(directory / (k + ".json")) for k in ("model", *COLLECTIONS)}
    timestamp = (directory / "readAt.txt").read_text().strip()
    if datetime.fromisoformat(timestamp).tzinfo is None:
        raise ValueError("Capture timestamps must include the UTC offset.")
    config, checkpoint, private_map = split_export(raw, timestamp, scrub)
    checkpoint["phase"] = phase
    out.mkdir(parents=True, mode=0o700, exist_ok=True)
    model, reference = out / "model.json", out / "reference.json"
    lock = os.open(out, os.O_RDONLY)
    try:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if model.exists() and read_json(model) != config:
            raise ValueError("The deployed configuration changed. Use a new output directory for this model version.")
        result = read_json(reference) if reference.exists() else {"schemaVersion": 1, "checkpoints": []}
        other = {s["capturedAt"]: s for s in result["checkpoints"]}
        if timestamp in other and other[timestamp] != checkpoint:
            raise ValueError("Different Azure reference states for the same capture.")
        other[timestamp] = checkpoint
        result["checkpoints"] = sorted(other.values(), key=lambda s: datetime.fromisoformat(s["capturedAt"]))
        save(directory / "bindings.json", private_map)
        save(model, config)
        save(reference, result)
    finally:
        os.close(lock)
    print(f'{len(config["entities"])} entities, {len(config["relationships"])} relationships, '
          f'{len(config["bindings"])} bindings, {len(other)} reference checkpoint(s)')


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    args = parser.add_mutually_exclusive_group(required=True)
    args.add_argument("--model-id", help="Deployed health model ARM ID")
    args.add_argument("--from-raw", type=Path, help="Previously captured private ARM bundle")
    parser.add_argument("--raw-out", type=Path, help="Keep private source IDs for the telemetry collector")
    parser.add_argument("--out", type=Path, required=True, help="Local fixture directory")
    parser.add_argument("--scrub", type=Path, metavar="LOCAL_SALT",
                        help="Replace Azure identifiers and deployment names with aliases salted by this local key file")
    parser.add_argument("--phase", choices=("warmup", "comparison"), default="comparison",
                        help="Label a separately observed deployment warmup in the reference history")
    options = parser.parse_args()
    salt = options.scrub and options.scrub.read_text().strip()
    if options.scrub and not salt:
        parser.error("The scrub key file is empty")
    if options.from_raw:
        if options.raw_out:
            parser.error("--raw-out applies to an Azure capture")
        export(options.from_raw, options.out, options.phase, salt)
    else:
        with tempfile.TemporaryDirectory() as tmp:
            directory = options.raw_out or Path(tmp)
            capture(options.model_id, directory)
            export(directory, options.out, options.phase, salt)
