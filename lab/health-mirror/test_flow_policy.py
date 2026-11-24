"""Exercise class assignment and operation coverage using the saved project inventory and telemetry."""
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


@unittest.skipUnless((FIXTURE / "request-observations.ndjson").exists(), "capture the project fixture first")
class ProjectPolicyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.fixture = self.base / "source"
        self.fixture.mkdir()
        self.work = self.base / "work"
        for name in ("container-apps.ndjson", "request-observations.ndjson", "request-dependencies.ndjson"):
            shutil.copyfile(FIXTURE / name, self.fixture / name)
        self.policy = json.loads((FIXTURE / "flow-ontology.json").read_text())

    def replay(self):
        (self.fixture / "flow-ontology.json").write_text(json.dumps(self.policy))
        subprocess.run([sys.executable, "-B", str(HERE / "build-space.py"), "--fixture", str(self.fixture),
                        "--out", str(self.work)], check=True, stdout=subprocess.DEVNULL)
        output = self.work / "replay"
        subprocess.run(["dotnet", str(CLI), "replay", str(self.work / "capture"), "--space", "healthMirror",
                        "--defs", str(self.work / "definitions"), "--out", str(output), "--lateness", "0s"],
                       check=True, stdout=subprocess.DEVNULL)
        return Graph(output, "workloadFlows"), Graph(output, "ontologyGaps")

    @staticmethod
    def memberships(graph):
        return sorted((a["key"], b["armId"]) for edge, (_, a), (_, b) in graph.edges if edge == "RunsOnApp")

    def test_print_captured_queries_uses_saved_window_and_rejects_foreign_receipts(self):
        command = [sys.executable, "-B", str(HERE / "curate-flows.py"), "--fixture", str(self.fixture),
                   "--template", str(HERE / "projects/ahm-model-demo/flows.json"), "--print-captured-kql"]
        requests = self.fixture / "request-observations.ndjson"
        rows = [json.loads(line) for line in requests.read_text().splitlines()]
        capture = json.loads(subprocess.check_output([*command, "both"], text=True))
        self.assertEqual(capture["componentId"].lower(), rows[0]["componentId"].lower())
        for key in ("windowStart", "windowEnd"):
            self.assertEqual(capture[key], rows[0][key])
        for kind, query in capture["queries"].items():
            self.assertEqual(query, subprocess.check_output([*command, kind], text=True))
            self.assertNotRegex(query, r"__[A-Z]+__")
            for key in ("componentId", "windowStart", "windowEnd"):
                self.assertIn(capture[key], query)
        self.assertFalse((self.fixture / "flow-ontology.json").exists())
        rows[0]["scopeId"] += "-foreign"
        requests.write_text("".join(json.dumps(row) + "\n" for row in rows))
        rejected = subprocess.run([*command, "both"], capture_output=True, text=True)
        self.assertNotEqual(rejected.returncode, 0)
        self.assertEqual(rejected.stdout, "")
        self.assertIn("Foreign AppRequests measurement", rejected.stderr)

    def test_overlapping_rules_on_real_container_apps(self):
        graph, gaps = self.replay()
        baseline = self.memberships(graph)
        self.assertTrue(baseline)
        self.assertEqual(baseline, sorted(set(baseline)))
        self.assertEqual(gaps.typed("ContainerApp"), [])
        rule = next(r for r in self.policy["nameRules"] if r["class"] == "WebFrontend")
        duplicate = {**rule, "id": rule["id"] + "-review"}
        self.policy["nameRules"].append(duplicate)
        graph, gaps = self.replay()
        self.assertEqual(self.memberships(graph), baseline)
        self.assertEqual(gaps.typed("ContainerApp"), [])
        duplicate["class"] = next(r["class"] for r in self.policy["nameRules"] if r["class"] != rule["class"])
        graph, gaps = self.replay()
        ambiguous = [app for app in gaps.typed("ContainerApp") if app["ontologyStatus"] == "Ambiguous"]
        self.assertEqual(len(ambiguous), 1)
        ambiguous_id = ambiguous[0]["armId"]
        self.assertTrue(any(app == ambiguous_id for _, app in baseline))
        self.assertEqual(self.memberships(graph), [(step, app) for step, app in baseline if app != ambiguous_id])
        self.assertEqual(len(graph.typed("UserFlow")), len(self.policy["flows"]))

    def test_unavailable_receipts_leave_declared_steps_unknown(self):
        graph, _ = self.replay()
        receipts = sum(int(s["measuredChecks"]) for s in graph.typed("SystemComponent"))
        self.assertGreater(receipts, 0)
        clean = [(f["key"], f["expectedChecks"]) for f in graph.typed("UserFlow")]
        for file in ("request-observations.ndjson", "request-dependencies.ndjson"):
            (self.fixture / file).write_text("")
        graph, _ = self.replay()
        self.assertFalse(graph.typed("OperationWindow"))
        self.assertEqual(clean, [(f["key"], f["expectedChecks"]) for f in graph.typed("UserFlow")])
        self.assertTrue(self.memberships(graph))
        self.assertTrue(all(s["state"] == "Unknown" and int(s["measuredChecks"]) == 0
                            for s in graph.typed("SystemComponent")))


if __name__ == "__main__":
    unittest.main()
