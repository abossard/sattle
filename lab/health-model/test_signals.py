"""Signal list tests: python3 -B lab/health-model/test_signals.py

reference/model.json is the deployed health model, exported and scrubbed by lab/health-mirror/export-model.py --scrub.
The gkql program in definitions/ must compute each of its 26 platform metric and 21 log query signals, with the
model's thresholds, and hold its entities, edges and impacts.
"""
import json
from pathlib import Path
import unittest

HERE = Path(__file__).parent
MODEL = json.loads((HERE / "reference" / "model.json").read_text())
PROGRAM = json.loads((HERE / "definitions" / "health.signals.gkql.json").read_text())
VALUES = {v["id"]: v["values"]["rows"] for v in PROGRAM["verbs"] if v["verb"] == "values"}
SIGNALS = {r["signal"]: r for r in VALUES["signals"]}
MEASURES = {"azureResource": {"Average": "avg", "Total": "sum"}, "azureLogAnalytics": ("ratio", "failures", "p95")}


def expected():
    """Every metric and query signal of the model, keyed like the reference bindings: entity/group/signal."""
    out = {}
    for entity in MODEL["entities"]:
        for group in ("azureResource", "azureLogAnalytics"):
            for signal in entity["properties"].get("signalGroups", {}).get(group, {}).get("signals", []):
                out["/".join((entity["name"], group, signal["name"]))] = (group, signal)
    return out


def query_measure(text):
    return "p95" if "percentile(" in text else "ratio" if "round(100.0" in text else "failures"


class SignalListTests(unittest.TestCase):
    def test_the_model_has_26_metric_and_21_query_signals(self):
        groups = [group for group, _ in expected().values()]
        self.assertEqual((groups.count("azureResource"), groups.count("azureLogAnalytics")), (26, 21))

    def test_each_signal_is_computed_with_the_model_thresholds(self):
        for key, (group, signal) in sorted(expected().items()):
            with self.subTest(signal=key):
                self.assertIn(key, SIGNALS, "missing from the program")
                row, rules = SIGNALS[key], signal["evaluationRules"]
                measure = (MEASURES[group][signal["aggregationType"]] if group == "azureResource"
                           else query_measure(signal["queryText"]))
                self.assertEqual(row["readsMeasure"], measure)
                self.assertEqual(row["above"], rules["unhealthyRule"]["operator"] == "GreaterThan")
                self.assertEqual(row["unhealthy"], rules["unhealthyRule"]["threshold"])
                self.assertEqual(row["hasDegraded"], "degradedRule" in rules)
                if "degradedRule" in rules:
                    self.assertEqual(rules["degradedRule"]["operator"], rules["unhealthyRule"]["operator"])
                    self.assertEqual(row["degraded"], rules["degradedRule"]["threshold"])

    def test_the_program_has_no_signal_the_model_lacks(self):
        self.assertEqual(sorted(set(SIGNALS) - set(expected())), [])

    def test_entities_edges_and_impacts_are_the_model(self):
        impact = {e["name"]: e["properties"].get("impact", "Standard") for e in MODEL["entities"]}
        edges = {(r["properties"]["parentEntityName"], r["properties"]["childEntityName"]) for r in MODEL["relationships"]}
        self.assertEqual({r["entity"] for r in VALUES["entities"]}, set(impact))
        self.assertEqual({(r["parent"], r["child"]) for r in VALUES["children"]}, edges)
        for row in VALUES["children"]:
            with self.subTest(edge=(row["parent"], row["child"])):
                self.assertEqual(row["impact"], impact[row["child"]])


if __name__ == "__main__":
    unittest.main()
