"""Case check tests: python3 -B lab/health-policy/test_cases.py"""
import json
from pathlib import Path
import tempfile
import unittest

from cases import P1, differences, t

IMPACT = {"consumer": "checkout", "dependency": "store", "effect": "Degraded"}
TRANSITION = t("store", 1, "Healthy", "Unhealthy", 1, P1)


def snapshot(label, *rows):
    return {"extent": {"entities": [{"type": {"value": "policy." + label},
                                     "properties": [{"property": {"value": "policy." + k}, "value": {"value": v}} for k, v in row.items()]}
                                    for row in rows]}}


class DifferencesTests(unittest.TestCase):
    def test_a_row_differs_from_one_expected_row(self):
        for name, label, want, got, lines in [
            ("one impact", "Impact", IMPACT, [IMPACT], []),
            ("duplicate impact", "Impact", IMPACT, [IMPACT, IMPACT], ["health Impact ['checkout', 'store']: 2 rows, not one"]),
            ("same since", "Transition", TRANSITION, [TRANSITION], []),
            ("other since", "Transition", TRANSITION, [{**TRANSITION, "since": "2026-01-01T00:00:15Z"}],
             [f"health Transition ['store', '{TRANSITION['windowStart']}']: {{'since': ('{TRANSITION['since']}', '2026-01-01T00:00:15Z')}}"]),
        ]:
            with self.subTest(name), tempfile.TemporaryDirectory() as replay:
                (Path(replay) / "health.final.snapshot.json").write_text(json.dumps(snapshot(label, *got)))
                self.assertEqual(differences({"health": {label: [want]}}, Path(replay)), lines)


if __name__ == "__main__":
    unittest.main()
