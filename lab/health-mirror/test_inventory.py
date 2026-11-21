from contextlib import redirect_stdout
from importlib.util import module_from_spec, spec_from_file_location
import io
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

spec = spec_from_file_location("inventory", Path(__file__).with_name("collect-inventory.py"))
collector = module_from_spec(spec)
spec.loader.exec_module(collector)

GROUP = "/subscriptions/00000000-0000-4000-a000-000000000001/resourceGroups/test"
MODEL = GROUP + "/providers/Microsoft.CloudHealth/healthModels/example"


def app(name):
    rid = GROUP + "/providers/Microsoft.App/containerApps/" + name
    return {"id": rid, "name": name, "type": "microsoft.app/containerapps", "tags": {},
            "properties": {"provisioningState": "Succeeded", "runningStatus": "Running"}}


class InventoryTests(unittest.TestCase):
    def test_new_app_retry_change_removal_and_failed_poll(self):
        with TemporaryDirectory() as d, redirect_stdout(io.StringIO()):
            history = Path(d) / "history.ndjson"
            delta = Path(d) / "pending.ndjson"
            first, second = app("first"), app("second")
            def poll(*resources):
                snapshot = {r["id"].lower(): r for r in resources}
                with patch.object(collector, "inventory", return_value=snapshot):
                    collector.poll(MODEL, history, delta, [])
            poll(first)
            initial = delta.read_bytes()
            # Recover a failure after the publisher outbox was saved and before the history was installed.
            history.unlink()
            with patch.object(collector, "inventory", side_effect=AssertionError("pending publication must be retried first")):
                collector.poll(GROUP, history, delta, [])
            self.assertEqual(initial, history.read_bytes())
            self.assertEqual(initial, delta.read_bytes())
            delta.unlink()
            poll(first, second)
            self.assertEqual([second], [r["payload"] for r in collector.read_rows(delta)])
            delta.unlink()
            first["properties"]["runningStatus"] = "Stopped"
            poll(first, second)
            self.assertEqual([first], [r["payload"] for r in collector.read_rows(delta)])
            delta.unlink()
            saved = history.read_bytes()
            with patch.object(collector, "inventory", side_effect=ValueError("incomplete")), self.assertRaises(ValueError):
                collector.poll(GROUP, history, delta, [])
            self.assertEqual(saved, history.read_bytes())
            self.assertFalse(delta.exists())
            with patch.object(collector, "confirmed_missing", return_value=False):
                poll(first)
            self.assertEqual(saved, history.read_bytes())
            with patch.object(collector, "confirmed_missing", return_value=True):
                poll(first)
            change, = collector.read_rows(delta)
            self.assertEqual({"changeType": "Delete", "targetResourceId": second["id"]}, change["payload"]["properties"])
            self.assertEqual([first], list(collector.materialize(collector.read_rows(history), GROUP).values()))

    def test_incomplete_page_cannot_become_a_snapshot(self):
        page = {"count": 1, "totalRecords": 2, "resultTruncated": "true", "data": [{}]}
        with patch.object(collector, "azure", return_value=page), self.assertRaisesRegex(ValueError, "partial"):
            collector.pages("subscription", "query")
