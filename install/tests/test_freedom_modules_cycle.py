# SPDX-License-Identifier: AGPL-3.0-or-later
# install/freedom_modules.py (Freedom F10) with the modules assoc-search, docs-search and dictation: manifest checks,
# signature of the shipped modules, files for the server, and the whole cycle install -> enable -> disable -> update ->
# remove with the server mocked (the server side: co-agent/tests/search-test.php and dictation-test.php). Windows tasks
# of the indexers are switched with enable/disable.
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import freedom_modules as fm  # noqa: E402
import freedom_release as fr  # noqa: E402
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402

MODULES = pathlib.Path(__file__).resolve().parents[2] / "modules"
CASES = {
    # id: (Windows task of the service or None, host ops, user ops, uploads, search providers)
    "assoc-search": ("\\AIWorkbench\\AssocIndex", {"vec_put", "alias_put"}, set(), [], ["vector"]),
    "docs-search": ("\\AIWorkbench\\DocsIndex", {"docs_put", "docs_state"}, {"search_docs"}, [], ["docs"]),
    "dictation": (None, set(), {"dictation_status"}, ["transcribe"], []),
}


class Server:
    """The server side of op modules_host as far as the installer sees it."""

    def __init__(self):
        self.mods = {}
        self.log = []

    def __call__(self, p):
        self.log.append(p["action"])
        a = p["action"]
        if a == "list":
            return {"modules": [dict(v) for v in self.mods.values()]}
        if a == "install":
            m = p["manifest"]
            old = self.mods.get(m["id"])
            self.mods[m["id"]] = {"id": m["id"], "version": m["version"], "files": p["files"], "enabled": bool(old and old["enabled"])}
            return {"ok": True, "id": m["id"], "version": m["version"], "enabled": bool(old and old["enabled"]), "updated": old is not None}
        if a == "set":
            if p["id"] not in self.mods:
                return {"ok": False, "error": "not_found"}
            self.mods[p["id"]]["enabled"] = p["enabled"]
            return {"ok": True}
        if a == "remove":
            return {"ok": self.mods.pop(p["id"], None) is not None, "data": p["data"]}
        raise AssertionError(a)


class CycleTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.maint = Ed25519PrivateKey.generate()
        self.trust = pathlib.Path(self.tmp.name) / "maintainers.json"
        self.trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(self.maint)}]}))

    def tearDown(self):
        self.tmp.cleanup()

    def copy(self, mid):
        root = pathlib.Path(self.tmp.name) / mid
        shutil.copytree(MODULES / mid, root, ignore=shutil.ignore_patterns(fr.MANIFEST_NAME, "desktop.ini"))
        return root

    def sign(self, root):
        m = json.loads((root / "module.json").read_text(encoding="utf-8"))
        man = fr.build(root, m["version"])
        man["module"] = m["id"]
        fr.write_json(root / fr.MANIFEST_NAME, fr.sign(man, "m1", self.maint))

    def test_manifests_clean_and_declared(self):
        for mid, (task, host, user, uploads, providers) in CASES.items():
            with self.subTest(mid):
                self.assertEqual(fm.check(MODULES / mid), [])
                m = json.loads((MODULES / mid / "module.json").read_text(encoding="utf-8"))
                ops = m["server"]["ops"]
                self.assertTrue(host <= set(ops["host"]) and user <= set(ops["user"]))
                self.assertEqual(m["server"].get("uploads", []), uploads)
                self.assertEqual([s["id"] for s in m["provides"].get("search", [])], providers)
                self.assertEqual([s.get("windowsTask") for s in m["provides"]["services"]], [task] if task else [])
                self.assertFalse(m["defaults"]["enabled"])

    def test_shipped_modules_signed_by_maintainers(self):
        for mid in CASES:
            with self.subTest(mid):
                v = fm.verify(MODULES / mid)
                self.assertTrue(v["standard"] and v["trusted"], v["problems"])

    def test_bad_search_provider_and_upload_refused(self):
        root = self.copy("assoc-search")
        m = json.loads((root / "module.json").read_text(encoding="utf-8"))
        m["provides"]["search"] = [{"id": "messages", "hooks": ["chats"]}, {"id": "x", "hooks": ["steal"]}]
        m["server"]["uploads"] = ["../x"]
        (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
        p = " ".join(fm.check(root))
        self.assertIn("search provider", p)
        self.assertIn("hooks from", p)
        self.assertIn("upload", p)

    def test_composer_slot_known(self):
        self.assertIn("ui.composer", fm.SLOTS)

    def test_server_files(self):
        for mid in CASES:
            with self.subTest(mid):
                root = self.copy(mid)
                files = fm.module_files(root)
                self.assertIn("server/ops.php", files)
                self.assertNotIn("README.md", files)
                self.assertNotIn("server/ops.php", fm.module_files(root, with_server=False))

    def test_cycle(self):
        for mid, (task, *_rest) in CASES.items():
            with self.subTest(mid):
                root = self.copy(mid)
                self.sign(root)
                srv = Server()
                ran = []

                def schtasks(args, **kw):
                    ran.append(args[1:])
                    return subprocess.CompletedProcess(args, 0, "", "")
                with mock.patch.object(fm, "host_call", side_effect=srv), mock.patch.object(fm, "ssh_client"), \
                        mock.patch.object(fm, "upload", return_value=mock.Mock()), mock.patch.object(fm.sys, "platform", "win32"), \
                        mock.patch.object(fm.subprocess, "run", side_effect=schtasks), \
                        mock.patch.object(fm, "tools_install", return_value=[]), \
                        mock.patch.object(fm, "toolbox", return_value={"ok": True, "modules": {}}) as tb:
                    self.assertEqual(fm.main(["install", str(root), "--trust", str(self.trust), "--enable"]), 0)
                    self.assertTrue(srv.mods[mid]["enabled"])
                    self.assertEqual(fm.main(["disable", mid]), 0)
                    self.assertFalse(srv.mods[mid]["enabled"])
                    self.assertEqual(fm.main(["enable", mid]), 0)
                    # update: same files again keeps the switch
                    self.assertEqual(fm.main(["install", str(root), "--trust", str(self.trust)]), 0)
                    self.assertTrue(srv.mods[mid]["enabled"])
                    self.assertEqual(fm.main(["remove", mid, "--data", "erase"]), 1)  # erase needs --yes
                    self.assertIn(mid, srv.mods)
                    self.assertEqual(fm.main(["remove", mid, "--data", "keep"]), 0)
                    self.assertNotIn(mid, srv.mods)
                tasks = [r for r in ran if r and r[0] in ("/Change", "/Run", "/End")]
                states = [c.args[c.args.index("--state") + 1] for c in tb.call_args_list if "--state" in c.args]
                if task:
                    # the tool of the indexer follows the switch (owned by the module), a schedule is not run at enable
                    self.assertEqual(states[:3], ["enabled", "disabled", "enabled"])
                    self.assertNotIn(["/Run", "/TN", task], tasks)
                    self.assertIn(["/Change", "/TN", task, "/ENABLE"], tasks)
                    self.assertIn(["/Change", "/TN", task, "/DISABLE"], tasks)
                    self.assertIn(["/End", "/TN", task], tasks)
                else:
                    self.assertEqual(tasks, [])


if __name__ == "__main__":
    unittest.main()
