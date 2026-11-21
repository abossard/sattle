"""Run with python3 -B lab/health-mirror/test_space.py after building the Release CLI."""
from pathlib import Path
import importlib.util
import json
import subprocess
import tempfile
import unittest


HERE = Path(__file__).resolve().parent
CLI = HERE.parents[1] / "engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll"
spec = importlib.util.spec_from_file_location("build_space", HERE / "build-space.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


def declaration():
    """A second model with opaque identifiers, a shared dependency and a reusable definition."""
    definition = {"signalKind": "AzureResourceMetric", "evaluationRules": {
        "degradedRule": {"operator": "GreaterThanOrEqual", "threshold": 5},
        "unhealthyRule": {"operator": "GreaterThan", "threshold": 7}}}
    model = {"schemaVersion": 1, "model": {"name": "test-model"}, "entities": [],
             "signalDefinitions": [{"name": "latency", "properties": definition}],
             "relationships": [], "bindings": []}
    for key, impact in [("parent", "Standard"), ("other-parent", "Standard"), ("cache", "Limited"), ("backup", "Suppressed")]:
        cfg = {"impact": impact}
        if key in ("cache", "backup"):
            cfg["signalGroups"] = {"azureResource": {"signals": [{"name": "reading", "signalDefinitionName": "latency", **definition}]}}
            model["bindings"].append({"id": key, "entityId": key, "group": "azureResource", "signalName": "reading", "definitionId": "latency"})
        model["entities"].append({"name": key, "properties": cfg})
    for parent, child in [("parent", "backup"), ("parent", "cache"), ("other-parent", "cache")]:
        model["relationships"].append({"name": parent + "-" + child, "properties": {"parentEntityName": parent, "childEntityName": child}})
    return model


class MirrorTests(unittest.TestCase):
    def test_unsupported_declarations_fail(self):
        model = declaration()
        model["entities"][0]["properties"]["signalGroups"] = {"dependencies": {"aggregationType": "MaxIssues"}}
        with self.assertRaisesRegex(ValueError, "unsupported dependency aggregation"):
            builder.build_model(model)
        model = declaration()
        model["relationships"].append({"name": "back", "properties": {"parentEntityName": "cache", "childEntityName": "parent"}})
        with self.assertRaisesRegex(ValueError, "cycle"):
            builder.build_model(model)
        model = declaration()
        model["entities"][0]["properties"]["healthState"] = "Healthy"
        with self.assertRaisesRegex(ValueError, "reference file"):
            builder.build_model(model)

    def test_shared_child_thresholds_recovery_and_network(self):
        with tempfile.TemporaryDirectory() as directory:
            tmp = Path(directory)
            model = declaration()
            model["entities"][0]["properties"]["signalGroups"] = {"azureResource": {"azureResourceId": "/resources/instrumentation"}}
            model["entities"][1]["properties"]["signalGroups"] = {"azureResource": {"azureResourceId": "/resources/storage"}}
            (tmp / "model.json").write_text(json.dumps(model))
            readings = []
            # Blank counters are valid on a flow-start tuple. The second source record is a redelivery.
            tuple_text = "1794306078045,192.0.2.1,192.0.2.2,40000,443,6,O,B,NX,,,,"
            sample = {"source": "vnet-flow-log", "capturedAt": "2026-11-10T12:32:29Z",
                      "payload": {"targetResourceID": "vnet-test", "flowRecords": {"flows": [
                          {"flowGroups": [{"rule": "test-rule", "flowTuples": [tuple_text, tuple_text]}]}]}}}
            (tmp / "network.ndjson").write_text(json.dumps(sample) + "\n")
            call = {"source": "AppDependencies", "scopeId": "/RESOURCES/instrumentation", "sourceComponent": "worker",
                    "destination": "service.example", "targetResourceId": "/resources/storage", "dependencyType": "HTTP",
                    "windowStart": "2026-11-10T11:00:00Z", "windowEnd": "2026-11-10T11:05:00Z", "spanCount": 1}
            groups = [{**call, "success": success, "calls": count} for success, count in [(True, 3), (False, 2), (None, 1)]]
            (tmp / "dependencies.ndjson").write_text("\n".join(map(json.dumps, groups + groups[:1])) + "\n")
            for minute, value, expected_child, expected_parent in [
                (1, 8, "Unhealthy", "Degraded"), (2, 5, "Degraded", "Healthy"),
                (3, 0, "Healthy", "Healthy"), (4, None, "Unknown", "Unknown")]:
                checkpoint = f"2026-11-10T13:0{minute}:00Z"
                for key in ("cache", "backup"):
                    measured = value if key == "cache" else 99
                    reading = {"bindingId": key, "entityId": key, "definitionId": "latency", "checkpoint": checkpoint,
                               "windowStart": "2026-11-10T13:00:00Z", "windowEnd": checkpoint,
                               "value": measured, "status": "queryError" if measured is None else "value"}
                    readings.append(json.dumps(reading))
                (tmp / "observations.ndjson").write_text("\n".join(reversed(readings)) + "\n" + readings[-1] + "\n")
                builder.build(tmp, tmp)
                out = subprocess.run(["dotnet", str(CLI), "replay", str(tmp / "capture"), "--defs", str(tmp / "definitions"),
                                      "--space", "healthMirror", "--lateness", "0s", "--out", str(tmp / "replay")],
                                     text=True, capture_output=True)
                self.assertEqual(out.returncode, 0, out.stderr)
                self.assertIn("rejected records: 0", out.stdout)
                def extent(graph):
                    return json.loads((tmp / "replay" / f"{graph}.final.snapshot.json").read_text())["extent"]
                def props(item):
                    return {v["property"]["value"].split(".")[-1]: v["value"].get("value") for v in item["properties"]}
                entities = {p["key"]: p for p in map(props, extent("health")["entities"])}
                self.assertEqual(entities["cache"]["state"], expected_child)
                self.assertEqual(entities["backup"]["state"], "Unhealthy")
                for parent in ("parent", "other-parent"):
                    self.assertEqual(entities[parent]["state"], expected_parent)
                    self.assertEqual(entities[parent]["ownState"], "Unknown")
                signals = {p["key"]: p for p in map(props, extent("signals")["entities"])}
                self.assertEqual(signals["cache"]["checkpoint"], checkpoint)
                self.assertEqual(signals["cache"]["status"], "queryError" if value is None else "value")
                self.assertEqual(len(extent("health")["relationships"]), 3)
                graph = extent("network")
                def only(items, label):
                    return [props(item) for item in items if item["type"]["value"] == label]
                self.assertEqual({n["address"] for n in only(graph["entities"], "IpAddress")}, {"192.0.2.1", "192.0.2.2"})
                self.assertEqual([int(e["tuples"]) for e in only(graph["relationships"], "ObservedTraffic")], [1])
                [route] = only(graph["relationships"], "CalledEndpoint")
                self.assertEqual(tuple(int(route[k]) for k in ("calls", "failedCalls", "unknownResultCalls", "spanCount")), (6, 2, 1, 3))
                self.assertEqual(len(only(graph["relationships"], "InstrumentedBy")), 1)
                self.assertEqual(len(only(graph["relationships"], "EndpointOf")), 1)
                flows = [props(n) for n in extent("master")["entities"] if n["type"]["value"] == "NetworkFlow"]
                self.assertEqual([f["tuple"] for f in flows], [tuple_text])


if __name__ == "__main__":
    unittest.main()
