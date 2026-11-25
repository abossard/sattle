"""Network alias tests: python3 -B lab/health-mirror/test_network.py

#176 A4: the aliases of addresses, odd host names and roles come from the lab's one scrub module under a local salt,
so an address cannot be found again by hashing every address."""
import hashlib
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
import unittest

spec = spec_from_file_location("network", Path(__file__).with_name("collect-network.py"))
network = module_from_spec(spec)
spec.loader.exec_module(network)


class AliasTests(unittest.TestCase):
    def test_an_alias_is_salted_and_made_by_the_scrub_module(self):
        cases = [("address", network.endpoint_name, "10.1.2.3", "ip"),
                 ("host name with an odd label", network.endpoint_name, "a_b.example.test", "host"),
                 ("role with a slash", network.role_name, "role/with slash", "role")]
        for name, make, value, kind in cases:
            with self.subTest(name):
                first, second = make(value, "salt-a"), make(value, "salt-b")
                self.assertEqual(first, network.alias("salt-a", kind, value))
                self.assertRegex(first, f"^{kind}-[0-9a-f]{{12}}$")
                self.assertNotEqual(first, second)
                self.assertNotIn(first.split("-", 1)[1], hashlib.sha256(value.lower().encode()).hexdigest())
                self.assertEqual(make(value, "salt-a"), first)

    def test_plain_names_stay_names(self):
        cases = [("host", network.endpoint_name, "api.example.test", "api.example.test"),
                 ("role", network.role_name, "checkout", "checkout"),
                 ("no host", network.endpoint_name, "", "unreported-target"),
                 ("no role", network.role_name, None, "unreported-role")]
        for name, make, value, expected in cases:
            with self.subTest(name):
                self.assertEqual(make(value, "salt-a"), expected)


if __name__ == "__main__":
    unittest.main()
