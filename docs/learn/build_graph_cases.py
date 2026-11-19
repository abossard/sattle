"""Rebuild the two shop cases using the checked-in estate and Sattle's replay CLI."""
import json
import shutil
import subprocess
import tempfile
from pathlib import Path

from build_evidence import ESTATE, EXPORT, HERE, ROOT, evidence, records
from replay_change import ENGINE


FLOW_TIME = "2026-01-01T10:07:15.000Z"
CUTOFF = "2026-01-01T10:07:30Z"
COMPONENT_KINDS = {"service", "workload", "resource", "gateway"}


def fields(element):
    return {p["property"]["value"].split(".")[-1]: p["value"].get("value")
            for p in element["properties"]}


def short_type(element):
    return element["type"]["value"].split(".")[-1]


def graph_index(directory, graph):
    extent = json.loads((directory / f"{graph}.final.snapshot.json").read_text())["extent"]
    return {el["id"]["value"]: el for family in ("entities", "relationships") for el in extent[family]}


def components(index):
    selected = (fields(el) for el in index.values() if "source" not in el)
    return {p["name"]: p for p in selected if p.get("kind") in COMPONENT_KINDS}


def health_tree(structure, master, meter):
    # Follow only Affects, in its recorded direction. Keep both the deployment
    # and service named "web", and keep a shared contributor as one identity.
    entities = {id_: fields(el) for id_, el in structure.items() if "source" not in el}
    affects = {}
    for el in structure.values():
        if short_type(el) == "Affects":
            affects.setdefault(el["source"]["value"], set()).add(el["target"]["value"])
    root, = [id_ for id_, p in entities.items() if (p.get("kind"), p.get("name")) == ("service", "web")]
    key = {id_: p["kind"] + ":" + p["name"] for id_, p in entities.items() if "kind" in p}
    nodes, pending = {}, [root]
    for id_ in pending:
        if id_ in nodes:
            continue
        children = sorted(affects.get(id_, ()), key=lambda id_: (entities[id_]["kind"] != "service", key[id_]))
        p = entities[id_]
        nodes[id_] = {"key": key[id_], "name": p["name"], "kind": p["kind"],
                      "children": [key[child] for child in children]}
        pending.extend(children)
    assert len({node["key"] for node in nodes.values()}) == len(nodes)

    service, = [fields(el) for el in master.values() if short_type(el) == "Service"
                and fields(el).get("name") == "database" and fields(el).get("namespace") == "shop"]
    resource_id, = [el["target"]["value"] for el in master.values() if short_type(el) == "MeterOf"
                    and fields(master[el["source"]["value"]]).get("meter") == meter
                    and fields(master[el["target"]["value"]]).get("name") == "pg-shop"]
    resource = fields(master[resource_id])
    assert service["externalName"] == resource["name"] + ".postgres.database.azure.com"
    assert any(short_type(el) == "BackedBy" and nodes.get(el["source"]["value"], {}).get("key") == "service:database"
               and nodes.get(el["target"]["value"], {}).get("key") == "resource:pg-shop" for el in structure.values())
    return {"root": key[root], "nodes": list(nodes.values()),
            "source": {"externalName": service["externalName"], "resourceName": resource["name"],
                       "resourceType": resource["type"], "meter": meter}}


def tree_values(elements, keys):
    typed = {p["kind"] + ":" + p["name"]: p for el in elements.values()
             if "source" not in el and (p := fields(el)).get("kind") and "name" in p}
    states = {key: typed[key]["state"] for key in sorted(keys)}
    own = {key: int(typed[key]["own"]) for key in states if "own" in typed[key]}
    return states, own


def rollup(staging):
    recorded = evidence()
    structure = graph_index(EXPORT, "workloadStructure")
    tree = health_tree(structure, graph_index(EXPORT, "master"), recorded["rule"]["meter"])
    keys = {node["key"] for node in tree["nodes"]}
    current, frames = {}, []
    patches = records(EXPORT / "workload.patches.ndjson")
    for patch in patches:
        for edit in patch["edits"]:
            key = edit["id"]["value"]
            if edit["after"] is None:
                current.pop(key)
            else:
                current[key] = edit["after"]
        revision = int(patch["to"]["revision"]["value"])
        if 15 <= revision <= 18:
            selected = components(current)
            tree_states, tree_own = tree_values(current, keys)
            signal = next(fields(el) for el in current.values() if fields(el).get("rule") == "database-down")
            frames.append({"revision": revision, "states": {n: p["state"] for n, p in selected.items()},
                           "own": {n: int(p["own"]) for n, p in selected.items() if "own" in p},
                           "signal": signal["signalState"], "treeStates": tree_states, "treeOwn": tree_own})
    paths = [["shop", "web", "api", "database", "pg-shop"], ["apim-shop", "api", "database", "pg-shop"]]
    links = {(fields(structure[r["source"]["value"]]).get("name"), fields(structure[r["target"]["value"]]).get("name"))
             for r in structure.values() if short_type(r) == "Affects"}
    for path in paths:
        assert all(pair in links for pair in zip(path, path[1:])), path
    assert [frame["signal"] for frame in frames] == ["off", "on", "on", "off"]
    # An observation minute, its capture time, and the commit that publishes a
    # state have different clocks. Replay the captured prefix for each sample
    # before associating its checkpoint with a frame from the complete run.
    selected_minutes = ["10:10", "10:11", "10:14", "10:15"]
    for frame, minute in zip(frames, selected_minutes, strict=True):
        sample, = [reading for reading in recorded["readings"] if reading["minute"] == minute]
        destination = staging / ("sample-" + minute.replace(":", ""))
        replay(ESTATE, destination, "2026-01-01T" + sample["received"] + "Z")
        snapshot = graph_index(destination, "workload")
        assert tree_values(snapshot, keys) == (frame["treeStates"], frame["treeOwn"])
        signal, = [p for el in snapshot.values() if (p := fields(el)).get("rule") == recorded["rule"]["id"]]
        assert signal["signalState"] == frame["signal"]
        frame.update(minute=sample["minute"], received=sample["received"], value=sample["value"])
    # --no-flush keeps the replay from advancing the clock just because the
    # input was cut short. Bracket each published change by one millisecond
    # and check the entire last patch against the uninterrupted estate replay.
    alarm_times = {"opened": "10:13:00", "cleared": "10:17:00"}
    boundaries = ["10:12:59.999", alarm_times["opened"], "10:16:59.999", alarm_times["cleared"]]
    for frame, at in zip(frames, boundaries, strict=True):
        destination = staging / ("open-" + at.replace(":", ""))
        replay(ESTATE, destination, "2026-01-01T" + at + "Z", flush_at_end=False)
        published = records(destination / "workload.patches.ndjson")[-1]
        reference, = [p for p in patches if int(p["to"]["revision"]["value"]) == frame["revision"]]
        assert published == reference, at
    return {"rule": recorded["rule"], "readings": recorded["readings"], "frames": frames, "paths": paths,
            "tree": tree, "alarmTimes": alarm_times}


def add_flow(capture):
    events = records(ESTATE / "kubernetes/watch.ndjson")
    endpoint = next(e for e in events if e["payload"].get("object", {}).get("metadata", {}).get("name") == "api-slice")
    obj = endpoint["payload"]["object"]
    target = obj["endpoints"][0]
    pod_uid = target["targetRef"]["uid"]
    pod = next(e["payload"]["object"] for e in events
               if e["payload"].get("object", {}).get("metadata", {}).get("uid") == pod_uid)
    slice_info = {"name": obj["metadata"]["name"], "service": obj["metadata"]["labels"]["kubernetes.io/service-name"],
                  "pod": pod["metadata"]["name"], "podUid": pod_uid, "address": target["addresses"][0],
                  "record": endpoint["key"]}

    flows = records(ESTATE / "hubble/flows.ndjson")
    event = next(e for e in flows if e["payload"]["flow"].get("destination_names") == ["shopcache.redis.cache.windows.net"])
    flow = event["payload"]["flow"]
    flow.update(time=FLOW_TIME, uuid="guide-api-to-redis")
    flow["source"]["pod_name"] = slice_info["pod"]
    flow["IP"]["source"] = slice_info["address"]
    event["payload"].update(time=FLOW_TIME, node_name=pod["spec"]["nodeName"])
    event.update(capturedAt=FLOW_TIME, key="hubble/" + flow["uuid"], seq=max(r["seq"] for r in flows) + 1)
    (capture / "hubble/lesson-api-redis.ndjson").write_text(json.dumps(event) + "\n")
    return ({"time": FLOW_TIME[11:19], "capturedAt": FLOW_TIME, "pod": slice_info["pod"],
             "address": flow["IP"]["destination"], "host": flow["destination_names"][0],
             "verdict": flow["verdict"], "record": event["key"]}, slice_info)


def replay(capture, destination, cutoff=CUTOFF, *, flush_at_end=True):
    command = ["dotnet", str(ENGINE), "replay", str(capture), "--cluster", "synth",
               "--until", cutoff, "--out", str(destination)]
    if not flush_at_end:
        command.append("--no-flush")
    subprocess.run(command, cwd=ROOT, stdout=subprocess.DEVNULL, check=True)


def patch_between(original, changed, graph):
    """Confirm the discovery is exactly one real consecutive patch in this graph."""
    baseline = graph_index(original, graph)
    final = graph_index(changed, graph)
    patch = records(changed / f"{graph}.patches.ndjson")[-1]
    edits = []
    for e in patch["edits"]:
        ident = e["id"]["value"]
        assert baseline.get(ident) == e["before"], (graph, ident)
        after = e["after"]
        assert after is not None, "These discovery cases contain additions and updates."
        baseline[ident] = after
        item = {"id": ident, "type": short_type(after), "kind": e["kind"],
                "operation": "added" if e["before"] is None else "changed"}
        if e["kind"] == "relationship":
            item.update(source=after["source"]["value"], target=after["target"]["value"])
        else:
            item.update(label=fields(after).get("name") or fields(after).get("how"))
            if "state" in fields(after):
                item.update(before=fields(e["before"])["state"], after=fields(after)["state"])
        edits.append(item)
    assert baseline == final, graph
    return {"graph": graph, "from": int(patch["from"]["revision"]["value"]),
            "to": int(patch["to"]["revision"]["value"]), "edits": edits,
            "cause": [i["value"] for i in patch["cause"]["inputs"]]}


def lineage(output, indexes, graph, identifier):
    row = next(r for r in records(output / f"{graph}.lineage.ndjson") if r["element"] == identifier)
    sources = []
    for s in row["sources"]:
        source = indexes[s["graph"]][s["element"]]
        p = fields(source)
        sources.append({"graph": s["graph"], "id": s["element"], "role": s["role"],
                        "label": p.get("name") or p.get("address") or short_type(source)})
    return {"id": identifier, "graph": graph, "sink": row["sink"], "rule": row["rule"],
            "inputs": row["inputs"], "sources": sources, "complete": row["complete"]}


def health_dependencies(index):
    """Read the actual DependsOn edges of web and api from one workload health output."""
    nodes, links = {}, []
    for element in index.values():
        if short_type(element) != "DependsOn":
            continue
        source, target = (element[end]["value"] for end in ("source", "target"))
        caller = fields(index[source])
        if caller["kind"] != "service" or caller["name"] not in {"web", "api"}:
            continue
        ends = []
        for identifier in (source, target):
            p = fields(index[identifier])
            key = p["kind"] + ":" + p["name"]
            nodes[key] = {"key": key, "name": p["name"], "kind": p["kind"], "id": identifier}
            ends.append(key)
        links.append({"from": ends[0], "to": ends[1], "id": element["id"]["value"]})
    return [nodes[key] for key in sorted(nodes)], sorted(links, key=lambda link: (link["from"], link["to"]))


def discovery(staging):
    capture, original, changed = (staging / name for name in ("capture", "original", "changed"))
    shutil.copytree(ESTATE, capture)
    flow, endpoint = add_flow(capture)
    replay(ESTATE, original)
    replay(capture, changed)
    graphs = ("master", "workloadStructure", "workload")
    indexes = {g: graph_index(changed, g) for g in graphs}
    patches = {g: patch_between(original, changed, g) for g in graphs}
    master, structure, health = (patches[g] for g in graphs)
    sends, = master["edits"]
    assert sends["type"] == "Sends" and sends["operation"] == "added"
    depends = next(e for e in structure["edits"] if e["type"] == "DependsOn")
    health_depends = next(e for e in health["edits"] if e["type"] == "DependsOn")
    s_index = indexes["workloadStructure"]
    source_name = fields(s_index[depends["source"]])["name"]
    target_name = fields(s_index[depends["target"]])["name"]
    assert (source_name, target_name) == ("api", "shopcache")
    endpoint["relationship"] = next(r["id"]["value"] for r in indexes["master"].values()
                                    if short_type(r) == "Backs" and r["source"]["value"] == sends["source"]
                                    and r["target"]["value"] == depends["source"])
    proofs = []
    for e in structure["edits"]:
        if e["type"] == "DependencyEvidence":
            p = fields(s_index[e["id"]])
            proof = lineage(changed, indexes, "workloadStructure", e["id"])
            proofs.append({"id": e["id"], "how": p["how"], "detail": p["detail"], "sources": proof["sources"]})
    assert {p["how"] for p in proofs} == {"dnsName", "privateEndpoint"}
    proofs.sort(key=lambda p: p["how"] != "privateEndpoint")
    original_health = graph_index(original, "workload")
    before = {n: p["state"] for n, p in components(original_health).items()}
    after = {n: p["state"] for n, p in components(indexes["workload"]).items()}
    assert {n for n in before if before[n] != after[n]} == {"api", "apim-shop"}
    assert before["shopcache"] == after["shopcache"] == after["api"] == after["apim-shop"] == "Degraded"
    assert before["api"] == before["apim-shop"] == "Healthy"
    before_nodes, before_links = health_dependencies(original_health)
    after_nodes, after_links = health_dependencies(indexes["workload"])
    assert before_nodes == after_nodes
    added = [link for link in after_links if link not in before_links]
    assert added == [{"from": "service:api", "to": "resource:shopcache", "id": health_depends["id"]}]
    assert all(link in after_links for link in before_links)
    assert {("service:web", "resource:shopcache"), ("service:api", "service:database")} <= {
        (link["from"], link["to"]) for link in before_links}
    assert graph_index(original, "authored") == graph_index(changed, "authored")
    return {"flow": flow, "cutoff": CUTOFF[11:19], "before": before, "after": after,
            "topology": {"type": "DependsOn", "nodes": before_nodes, "before": before_links, "after": after_links},
            "masterPatch": master, "structurePatch": structure, "healthPatch": health, "proofs": proofs,
            "dependency": {**depends, "sourceLabel": source_name, "targetLabel": target_name},
            "lineage": {"master": lineage(changed, indexes, "master", sends["id"]),
                        "structure": lineage(changed, indexes, "workloadStructure", depends["id"]),
                        "workload": lineage(changed, indexes, "workload", health_depends["id"])},
            "endpointSlice": endpoint}


if __name__ == "__main__":
    if not ENGINE.is_file():
        raise SystemExit("Build the replay CLI first with dotnet build engine/src/Sattle.Cli -c Release")
    with tempfile.TemporaryDirectory(prefix="sattle-graph-cases-") as temp:
        result = {"rollup": rollup(Path(temp)), "discovery": discovery(Path(temp))}
    destination = HERE / "foundry/replay-cases.js"
    destination.write_text("// Generated by ../build_graph_cases.py from Sattle replay evidence.\n"
                           + "const ShopReplayCases = " + json.dumps(result, indent=2) + ";\n")
