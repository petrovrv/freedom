# SPDX-License-Identifier: AGPL-3.0-or-later
# Every standard module in modules/ verifies against install/maintainers.json: a file edited after signing (13.11 the
# geo-presence README) makes `freedom_modules.py install` refuse the module on deploy night. Re-sign after any change:
# python install/freedom_modules.py sign modules/<id>
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import freedom_modules as fm  # noqa: E402

MODULES = pathlib.Path(__file__).resolve().parents[2] / "modules"


class ShippedModulesSigned(unittest.TestCase):
    def test_every_module_verifies(self):
        roots = sorted(p.parent for p in MODULES.glob("*/module.json"))
        self.assertTrue(roots)
        for root in roots:
            with self.subTest(module=root.name):
                v = fm.verify(root)
                self.assertTrue(v["trusted"] and v["standard"], f"{root.name}: {v['problems']} - re-sign it")


if __name__ == "__main__":
    unittest.main()
