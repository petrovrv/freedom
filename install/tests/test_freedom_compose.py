# SPDX-License-Identifier: AGPL-3.0-or-later
# install/freedom_compose.py (Freedom F10 reverse task): features covered by modules, orphan tools, core leftovers,
# install to-dos, private layer. Module folders, the library, the server and git are stand-ins.
import json
import pathlib
import shutil
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import freedom_compose as fc  # noqa: E402


def tool(lib, name, v="0.1.0"):
    (lib / name).mkdir(parents=True)
    (lib / name / "SKILL.md").write_text(f"---\nname: {name}\nversion: {v}\n---\n", encoding="utf-8")


class ComposeTest(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp())
        self.lib = self.tmp / "lib"
        for t in ("core-tool", "mail-tool", "orphan-tool"):
            tool(self.lib, t)
        self.mods = {"mail": (self.tmp / "mail", {"id": "mail", "version": "0.1.0", "provides": {"tools": [{"name": "mail-tool"}]}}),
                     "geo": (self.tmp / "geo", {"id": "geo", "version": "0.2.0", "provides": {}})}
        self.inv = {"features": [{"feature": "Mail", "modules": ["mail"], "tools": ["mail-tool", "sheet-tool"]},
                                 {"feature": "Geo", "modules": ["geo"]},
                                 {"feature": "Canvas", "modules": ["canvas"]},
                                 {"feature": "Heating", "modules": ["heat"], "future": True}],
                    "coreTools": ["core-tool"],
                    "coreMustNot": [{"what": "lab", "path": "co-web/src/lab.ts"}],
                    "private": [{"what": "host key", "keyring": ["aiwb/co-push", "default"]}]}
        self.patches = [
            mock.patch.object(fc.fm, "tools_root", return_value=self.lib),
            mock.patch.object(fc.fm, "local_modules", return_value=self.mods),
            mock.patch.object(fc.fm, "verify", return_value={"standard": True, "trusted": True}),
            mock.patch.object(fc.fm, "check", return_value=[]),
            mock.patch.object(fc.fm, "toolbox", return_value={"ok": True, "modules": {"mail": {"enabled": True}}}),
            mock.patch.object(fc.fm, "host_call", return_value={"modules": [{"id": "geo", "version": "0.1.0", "enabled": True},
                                                                             {"id": "ghost", "version": "1", "enabled": True}]}),
            mock.patch.object(fc, "core_files", return_value=["co-web/src/lab.ts", "README.md"]),
            mock.patch.object(fc, "keyring_has", return_value=True),
            mock.patch.object(fc, "EXPORT_CONFIG", self.tmp / "none.json"),
        ]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in self.patches:
            p.stop()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_compose(self):
        r = fc.compose(self.inv)
        gaps = "\n".join(r["gaps"])
        self.assertIn("feature 'Canvas': no module folder", gaps)
        self.assertIn("feature 'Mail': tool sheet-tool is not owned", gaps)
        self.assertIn("tool orphan-tool is neither a core tool nor owned", gaps)
        self.assertNotIn("core-tool", gaps)
        self.assertNotIn("Heating", gaps)
        self.assertIn("core still has co-web/src/lab.ts", gaps)
        self.assertIn("module ghost is installed on the server but has no folder", gaps)
        todo = "\n".join(r["todo"])
        self.assertIn("install module mail", todo)
        self.assertIn("module geo: server has 0.1.0, folder 0.2.0", todo)
        self.assertEqual({f["feature"]: f["status"] for f in r["features"]},
                         {"Mail": "gap", "Geo": "covered", "Canvas": "gap", "Heating": "future"})
        self.assertTrue(r["private"][0]["ok"])

    def test_offline_and_exit_code(self):
        self.inv["features"] = [{"feature": "Geo", "modules": ["geo"]}]
        self.inv["coreMustNot"] = []
        self.inv["coreTools"] = ["core-tool", "mail-tool", "orphan-tool"]
        r = fc.compose(self.inv, offline=True)
        self.assertEqual(r["gaps"], [])
        f = self.tmp / "inv.json"
        f.write_text(json.dumps(self.inv), encoding="utf-8")
        self.assertEqual(fc.main(["--offline", "--inventory", str(f), "--json"]), 0)

    def test_freedom_tree_without_release_folder(self):
        # an exported tree: core tools are its tools/ minus module tools; private files must be absent from it
        tree = self.tmp / "tree"
        tool(tree / "tools", "orphan-tool")
        tool(tree / "tools", "mail-tool")
        self.inv["features"] = [{"feature": "Geo", "modules": ["geo"]}]
        self.inv["coreMustNot"], self.inv["coreTools"] = [], ["core-tool"]
        self.inv["private"] = [{"what": "release scripts", "exportExcluded": ["sync.ps1"]}]
        with mock.patch.object(fc, "REPO", tree), mock.patch.object(fc, "export_excluded", return_value=[]):
            r = fc.compose(self.inv, offline=True)
            self.assertEqual(r["gaps"], [])
            self.assertIn("orphan-tool", r["core"]["tools"])
            self.assertNotIn("mail-tool", r["core"]["tools"])
            with mock.patch.object(fc, "core_files", return_value=["sync.ps1"]):
                self.assertIn("private file(s) not excluded from the export: sync.ps1", "\n".join(fc.compose(self.inv, offline=True)["gaps"]))


if __name__ == "__main__":
    unittest.main()
