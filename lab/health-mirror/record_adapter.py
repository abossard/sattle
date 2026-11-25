"""Convert native Azure and model capture shapes into the engine's typed record envelope."""
from datetime import datetime, timezone
import ipaddress
import json

from mirror_program import ONTOLOGY_ROW, ROW


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def record(at, row_type, key, values):
    return {"v": 1, "source": "records:" + row_type, "capturedAt": at, "key": compact(key), "payload": {"key": compact(key), "values": values}}


def normalize(envelope):
    """Yield zero or more keyed assertions. Classification and evaluation belong to GKQL."""
    source, at, raw = envelope["source"], envelope["capturedAt"], envelope["payload"]
    if source.startswith("records:"):
        yield envelope
    elif source == "azure-cloud-health":
        values = {f["name"]: raw.get(f["name"], "" if f["type"] == "Text" else None) for f in ROW}
        values["configuration"] = compact(raw["configuration"])
        values["name"] = raw.get("name", raw["key"])
        yield record(at, "CloudHealthRow", [raw["kind"], raw["key"]], values)
    elif source == "workload-ontology":
        values = {f["name"]: raw.get(f["name"], "") for f in ONTOLOGY_ROW}
        values["configuration"] = compact(raw["configuration"])
        yield record(at, "WorkloadOntologyRow", [raw["kind"], raw["key"]], values)
    elif source == "app-dependencies":
        target = raw.get("targetResourceId", "")
        scope, role = raw["scopeId"], raw["sourceComponent"]
        host = raw["destination"].lower()
        name = host.rstrip(".")
        try:
            loopback = ipaddress.ip_address(name).is_loopback
        except ValueError:
            loopback = name == "localhost" or name.endswith(".localhost")
        result = {True: "success", False: "failure", None: "unknown"}[raw["success"]]
        series = compact([role, host, raw["dependencyType"], result])
        values = {k: raw[k] for k in ["scopeId", "sourceComponent", "dependencyType", "windowStart", "windowEnd", "calls", "spanCount"]}
        values.update(scopeKey=scope.lower(), result=result, series=series, destination=host, targetResourceId=target,
                      targetScopeKey=target.lower(), endpointScope=compact([scope.lower(), role]) if loopback else "")
        key = [scope.lower(), series, raw["windowStart"], raw["windowEnd"]]
        yield record(at, "AppDependencyRow", key, values)
    elif source == "vnet-flow-log":
        vnet, observer = raw["targetResourceID"], raw.get("macAddress", "")
        for flow in raw["flowRecords"]["flows"]:
            acl = flow.get("aclID", "")
            for group in flow.get("flowGroups", []):
                rule = group.get("rule", "")
                series = compact([observer, acl, rule])
                for item in group["flowTuples"]:
                    t, src, dst, sport, dport, proto, direction, state, encryption, _, _, _, _ = item.split(",")
                    secs, millis = divmod(int(t), 1000)
                    utc = datetime.fromtimestamp(secs, timezone.utc).replace(microsecond=millis * 1000)
                    values = dict(vnetId=vnet, observer=observer, acl=acl, rule=rule, series=series, tuple=item,
                                  time=utc.isoformat().replace("+00:00", "Z"), srcIp=src, dstIp=dst, srcPort=int(sport),
                                  dstPort=int(dport), protocol=int(proto), direction=direction, flowState=state, encryption=encryption)
                    yield record(at, "VnetEvidenceRow", [vnet, series, item], values)
    else:
        raise ValueError(f"Unrecognized capture source: {source}")
