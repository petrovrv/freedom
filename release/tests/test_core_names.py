# SPDX-License-Identifier: AGPL-3.0-or-later
# release/check_core_names.py (Freedom F10): the core knows no module by name - module names come from modules/*/module.json,
# legacy wire tokens are masked, and the committed tree is not worse than the baseline.
import pathlib
import re
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import check_core_names as cc  # noqa: E402


class CoreNamesTest(unittest.TestCase):
    def test_gateway_module_names_come_from_the_manifests(self):
        terms = cc.module_terms("HEAD")
        for want in ("corporate", "engie_chat"):
            self.assertIn(want, terms)

    def test_not_worse_than_the_baseline(self):
        self.assertEqual(cc.main([]), 0)

    def test_bridge_and_installer_are_clean(self):
        hits = cc.scan("HEAD")
        # the gateway module's names (the relay tool list of BridgeDevices.cs names library tools: pinned in the baseline)
        dirty = sorted(p for p, h in hits.items() if (p.startswith("bridge/") or p.startswith("install/freedom_modules"))
                       and any(re.search(r"corporate|engie|tractebel", x, re.I) for x in h))
        self.assertEqual(dirty, [], dirty)


if __name__ == "__main__":
    unittest.main()
