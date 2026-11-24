"""Replay the saved project telemetry, withhold CloudHealth, and edit a GKQL threshold."""
from hashlib import sha256
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from verify_workloads import Graph

HERE = Path(__file__).resolve().parent
FIXTURE = HERE / "fixture"
CLI = HERE.parents[1] / "engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll"


def lines(path):
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def capture_hashes(capture):
    return {f.name: sha256(f.read_bytes()).hexdigest() for f in capture.glob("*.ndjson")}


@unittest.skipUnless((FIXTURE / "diagnostic-events").is_dir(), "capture real Azure diagnostic deliveries first")
class IndependentHealthTests(unittest.TestCase):
    def test_threshold_and_missing_metric_propagate(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp) / "space"
            source = Path(tmp) / "source"
            source.mkdir()
            for name in ("flow-ontology.json", "request-observations.ndjson", "request-dependencies.ndjson"):
                shutil.copyfile(FIXTURE / name, source / name)
            shutil.copytree(FIXTURE / "diagnostic-events", source / "diagnostic-events")
            inventory = []
            for row in lines(FIXTURE / "container-apps.ndjson"):
                resource = row["payload"]
                resource_id = resource.get("id") or resource["properties"]["targetResourceId"]
                if "/providers/microsoft.cloudhealth/" not in resource_id.casefold():
                    inventory.append(row)
            (source / "container-apps.ndjson").write_text("".join(json.dumps(r) + "\n" for r in inventory))

            subprocess.run([sys.executable, "-B", str(HERE / "build-space.py"), "--fixture", str(source),
                            "--out", str(work)], check=True, stdout=subprocess.DEVNULL)
            capture = work / "capture"
            defs = work / "definitions"
            sources = {r["source"] for path in capture.glob("*.ndjson") for r in lines(path)}
            self.assertNotIn("records:CloudHealthRow", sources)
            self.assertIn("records:WorkloadOntologyRow", sources)
            metrics = capture / "platform-metrics.ndjson"
            self.assertTrue(metrics.stat().st_size, "Capture native CPU and memory diagnostics for a deployed app")

            def replay(label):
                out = work / label
                subprocess.run(["dotnet", str(CLI), "replay", str(capture), "--defs", str(defs), "--out", str(out),
                                "--space", "healthMirror", "--lateness", "0s"], check=True, stdout=subprocess.DEVNULL)
                for name in ("signals", "health"):
                    self.assertFalse(Graph(out, name).nodes)
                return Graph(out, "projectHealth")

            baseline = replay("default")
            rows = baseline.typed("PlatformMetric")
            self.assertEqual({r["source"] for r in rows}, {"AzureDiagnosticSettings"})
            # Use the captured value at the inclusive threshold boundary.
            candidates = [r for r in rows if r["name"] == "MemoryPercentage" and r["state"] == "Healthy"
                          and 0 < float(r["value"]) < float(r["warningAt"])]
            self.assertTrue(candidates, "The captured project needs a healthy, positive memory reading")
            selected = max(candidates, key=lambda r: float(r["value"]))
            original_apps = {r["armKey"]: r["state"] for r in baseline.typed("ContainerApp")}
            self.assertEqual(original_apps[selected["armKey"]], "Healthy")
            original_inputs = capture_hashes(capture)
            policy = defs / "mirror.projectHealth.gkql.json"
            doc = json.loads(policy.read_text())
            warning = next(p for p in doc["parameters"] if p["name"] == "memoryWarningPercent")
            warning["default"] = float(selected["value"])
            policy.write_text(json.dumps(doc))
            changed = replay("edited")
            self.assertEqual(original_inputs, capture_hashes(capture))
            by_arm = {r["armKey"]: r["state"] for r in changed.typed("ContainerApp")}
            self.assertEqual(by_arm[selected["armKey"]], "Degraded")
            self.assertEqual(changed.typed("ResourceGroup")[0]["state"], "Degraded")
            for edge, (_, step), (_, app) in changed.edges:
                if edge == "RunsOnApp" and app["armKey"] == selected["armKey"]:
                    self.assertEqual(step["platformState"], "Degraded")
                    self.assertEqual(step["state"], "Degraded")
            self.assertTrue(all(f["state"] == "Unknown" for f in changed.typed("UserFlow")))

            # Withhold actual memory observations, keeping the CPU checks and request outcomes.
            retained = [r for r in lines(metrics) if r["payload"]["values"]["name"] != "MemoryPercentage"]
            metrics.write_text("".join(json.dumps(r) + "\n" for r in retained))
            missing = replay("missing-memory")
            self.assertTrue(missing.typed("ContainerApp"))
            self.assertTrue(all(app["state"] == "Unknown" for app in missing.typed("ContainerApp")))
            self.assertTrue(all(step["state"] == "Unknown" for step in missing.typed("SystemComponent")))
            self.assertTrue(all(f["inferredState"] == "Unknown" for f in missing.typed("UserFlow")))
            self.assertEqual(missing.typed("ResourceGroup")[0]["state"], "Unknown")


if __name__ == "__main__":
    unittest.main()
