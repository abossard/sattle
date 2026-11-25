#!/usr/bin/env python3
"""Compare the rebuilt health model with Azure at one reference checkpoint.

  prepare RAW REFERENCE OUT APP_ROLE
                              collector output, a reference file (lab/health-mirror/export-model.py --scrub) and
                              the app role name -> OUT/capture/rows.ndjson, OUT/checkpoint.json, OUT/cuts.txt
  compare OUT                 OUT/replays/<cut>/master.final.snapshot.json -> one line per mismatch, exit 1 on any

A record enters the engine when Azure could first see it: a log row at its ingestion time (the collector's position),
a metric minute when it ends. The engine's clock is the capture time, so `replay --until <cut> --advance-every 1s
--lateness 0s --no-flush` stops with the clock at the cut. Azure evaluates each signal at its own time (reportedAt),
so each signal is read from the replay cut at that time, and the entities from the one cut at the capture time.
Names are scrubbed as export-model.py --scrub does, so rows and reference keep their joins.
"""
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import sys

TOLERANCE = 1e-6
ENTRY_DELAY = {"MetricRow": timedelta(minutes=1)}


def digest(name):
    return hashlib.sha256(name.casefold().encode()).hexdigest()[:12]


def instant(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00")).astimezone(timezone.utc)


def iso(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def scrub(values, app_role):
    """The row as the scrubbed reference names it: app role and resource groups by alias, no resource ids."""
    out = {k: v for k, v in values.items() if k != "resourceId"}
    if out.get("AppRoleName") == app_role:
        out["AppRoleName"] = "demo-workload"
    if out.get("ResourceGroup"):
        out["ResourceGroup"] = "group-" + digest(out["ResourceGroup"])
    return out


def entry(envelope):
    """The capture time at which Azure could first see the record."""
    rows = envelope["source"].split(":", 1)[1]
    values = envelope["payload"]["values"]
    if rows in ENTRY_DELAY:
        return instant(values["at"]) + ENTRY_DELAY[rows]
    return instant(envelope["meta"]["position"])


def capture(raw_lines, cuts, app_role):
    """Records at their entry time and a heartbeat at every cut, in capture order."""
    records = []
    for line in raw_lines:
        e = json.loads(line)
        if "values" not in e["payload"]:
            continue
        key = e["source"] + "/" + digest(e["key"])
        records.append((entry(e), {"v": 1, "source": e["source"], "key": key,
                                   "payload": {"key": key, "values": scrub(e["payload"]["values"], app_role)}}))
    sources = sorted({r["source"] for _, r in records}) or ["records:MetricRow"]
    for cut in cuts:
        records.append((cut, {"v": 1, "source": sources[0], "key": "heartbeat/" + iso(cut), "payload": {}}))
    records.sort(key=lambda r: r[0])
    return [{**r, "capturedAt": iso(at), "seq": i} for i, (at, r) in enumerate(records)]


def checkpoint(reference, index=-1):
    """Expected states: signals at their evaluation time (no Resource Health), entities at the capture time."""
    c = reference["checkpoints"][index]
    signals = {s["bindingId"]: {"cut": iso(instant(s["reportedAt"])), "state": s["state"], "value": s.get("value")}
               for s in c["signals"] if "/azureResource/resource-health" not in s["bindingId"]}
    return {"capturedAt": iso(instant(c["capturedAt"])), "signals": signals,
            "entities": {e["entityId"]: e["state"] for e in c["entities"]}}


def nodes(snapshot, label):
    """name -> {property: value} of the snapshot's nodes of one type."""
    out = {}
    for e in snapshot["extent"]["entities"]:
        if e["type"]["value"].split(".")[-1] != label:
            continue
        props = {p["property"]["value"].split(".")[-1]: p["value"].get("value") for p in e["properties"]}
        out[props.get("name")] = props
    return out


def mismatches(expected, snapshots):
    """One line per signal or entity whose state or value differs from the reference."""
    out = []
    for key, want in sorted(expected["signals"].items()):
        got = nodes(snapshots[want["cut"]], "Signal").get(key, {})
        value = float(got["value"]) if got.get("value") is not None else None
        if got.get("state") != want["state"]:
            out.append(f"signal {key} at {want['cut']}: state {got.get('state')} != {want['state']}")
        elif (value is None) != (want["value"] is None) or (value is not None and abs(value - want["value"]) > TOLERANCE * max(1, abs(value))):
            out.append(f"signal {key} at {want['cut']}: value {value} != {want['value']}")
    entities = nodes(snapshots[expected["capturedAt"]], "Entity")
    for key, want in sorted(expected["entities"].items()):
        if entities.get(key, {}).get("state") != want:
            out.append(f"entity {key} at {expected['capturedAt']}: state {entities.get(key, {}).get('state')} != {want}")
    return out


def prepare(raw, reference, out, app_role):
    expected = checkpoint(json.loads(Path(reference).read_text()))
    cuts = sorted({instant(s["cut"]) for s in expected["signals"].values()} | {instant(expected["capturedAt"])})
    out = Path(out)
    (out / "capture").mkdir(parents=True, exist_ok=True)
    lines = Path(raw).read_text().splitlines()
    (out / "capture" / "rows.ndjson").write_text("".join(json.dumps(e) + "\n" for e in capture(lines, cuts, app_role)))
    (out / "checkpoint.json").write_text(json.dumps(expected, indent=1) + "\n")
    (out / "cuts.txt").write_text("".join(iso(c) + "\n" for c in cuts))


def compare(out):
    out = Path(out)
    expected = json.loads((out / "checkpoint.json").read_text())
    snapshots = {cut: json.loads((out / "replays" / cut / "master.final.snapshot.json").read_text())
                 for cut in (out / "cuts.txt").read_text().split()}
    lines = mismatches(expected, snapshots)
    print("\n".join(lines))
    print(f"{len(expected['signals'])} signals, {len(expected['entities'])} entities, {len(lines)} mismatches")
    return 1 if lines else 0


if __name__ == "__main__":
    if sys.argv[1:2] == ["prepare"] and len(sys.argv) == 6:
        prepare(*sys.argv[2:6])
    elif sys.argv[1:2] == ["compare"] and len(sys.argv) == 3:
        sys.exit(compare(sys.argv[2]))
    else:
        sys.exit(__doc__)
