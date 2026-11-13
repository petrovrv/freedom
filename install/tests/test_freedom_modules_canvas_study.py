# SPDX-License-Identifier: AGPL-3.0-or-later
# install/freedom_modules.py (Freedom F10) with the modules canvas and study: manifest checks, signature of the shipped
# modules, files for the server, and the whole cycle install -> enable -> disable -> update -> remove with the server
# mocked (the server side of canvas: co-agent/tests/canvas-module-test.php; study has no server part). The Windows task
# of canvas-bridge is switched with enable/disable.
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
from test_freedom_modules_cycle import Server  # noqa: E402

MODULES = pathlib.Path(__file__).resolve().parents[2] / "modules"
CASES = {
    # id: (Windows task of the service or None, host ops, user ops, mode keys, side-job keys, page paths)
    "canvas": ("\\AIWorkbench\\CanvasBridge", {"canvas_job", "canvas_job_get", "canvas_share_put"}, {"canvas_ticket", "canvas_share_draft"},
               [], ["canvas"], ["/canvas", "/canvas-share"]),
    "study": (None, set(), set(), ["study"], [], []),
}


class CanvasStudyCycleTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.maint = Ed25519PrivateKey.generate()
        self.trust = pathlib.Path(self.tmp.name) / "maintainers.json"
        self.trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(self.maint)}]}))

    def tearDown(self):
        self.tmp.cleanup()

    def copy(self, mid):
        root = pathlib.Path(self.tmp.name) / mid
        shutil.copytree(MODULES / mid, root, ignore=shutil.ignore_patterns(fr.MANIFEST_NAME, "desktop.ini", "__pycache__"))
        return root

    def sign(self, root):
        m = json.loads((root / "module.json").read_text(encoding="utf-8"))
        man = fr.build(root, m["version"])
        man["module"] = m["id"]
        fr.write_json(root / fr.MANIFEST_NAME, fr.sign(man, "m1", self.maint))

    def test_manifests_clean_and_declared(self):
        for mid, (task, host, user, modes, sides, paths) in CASES.items():
            with self.subTest(mid):
                self.assertEqual(fm.check(MODULES / mid), [])
                m = json.loads((MODULES / mid / "module.json").read_text(encoding="utf-8"))
                ops = (m.get("server") or {}).get("ops") or {}
                self.assertEqual(set(ops.get("host", [])), host)
                self.assertEqual(set(ops.get("user", [])), user)
                prov = m["provides"]
                self.assertEqual([x["key"] for x in prov.get("modes", [])], modes)
                self.assertEqual([x["key"] for x in prov.get("sideJobs", [])], sides)
                self.assertEqual([u["path"] for u in prov.get("ui", []) if u.get("path")], paths)
                self.assertEqual([s.get("windowsTask") for s in prov.get("services", [])], [task] if task else [])
                self.assertFalse(m["defaults"]["enabled"])

    def test_shipped_modules_signed_by_maintainers(self):
        for mid in CASES:
            with self.subTest(mid):
                v = fm.verify(MODULES / mid)
                self.assertTrue(v["standard"] and v["trusted"], v["problems"])

    def test_study_rules_are_registered_files(self):
        root = self.copy("study")
        files = fm.module_files(root)
        for rel in ("context/rules.md", "context/off.md", "ui/viewer.html", "ui/viewer.js", "ui/viewer.css"):
            self.assertIn(rel, files)

    def test_canvas_server_files(self):
        root = self.copy("canvas")
        self.assertIn("server/ops.php", fm.module_files(root))
        self.assertNotIn("server/ops.php", fm.module_files(root, with_server=False))

    def test_cycle(self):
        for mid, (task, *_rest) in CASES.items():
            with self.subTest(mid):
                root = self.copy(mid)
                self.sign(root)
                srv = Server()
                ran = []
                sent = []

                def schtasks(args, **kw):
                    ran.append(args[1:])
                    if args[1] == "/Query":  # no such task on this machine: nothing to re-point
                        return subprocess.CompletedProcess(args, 1, b"", b"ERROR: not found")
                    return subprocess.CompletedProcess(args, 0, "", "")

                def host(p):
                    sent.append(p)
                    return srv(p)
                with mock.patch.object(fm, "host_call", side_effect=host), mock.patch.object(fm, "ssh_client"), \
                        mock.patch.object(fm, "upload", return_value=mock.Mock()), mock.patch.object(fm.sys, "platform", "win32"), \
                        mock.patch.object(fm.subprocess, "run", side_effect=schtasks), \
                        mock.patch.object(fm.aiwb_install, "get", side_effect=lambda k, d=None: "https://pc.example.net" if k == "canvas.publicUrl" else d):
                    self.assertEqual(fm.main(["install", str(root), "--trust", str(self.trust), "--enable"]), 0)
                    self.assertTrue(srv.mods[mid]["enabled"])
                    self.assertEqual(fm.main(["disable", mid]), 0)
                    self.assertFalse(srv.mods[mid]["enabled"])
                    self.assertEqual(fm.main(["enable", mid]), 0)
                    self.assertEqual(fm.main(["install", str(root), "--trust", str(self.trust)]), 0)
                    self.assertTrue(srv.mods[mid]["enabled"])
                    self.assertEqual(fm.main(["remove", mid, "--data", "keep"]), 0)
                    self.assertNotIn(mid, srv.mods)
                inst = [p for p in sent if p["action"] == "install"]
                self.assertEqual(inst[0].get("config", {}), {"publicUrl": "https://pc.example.net"} if mid == "canvas" else {})
                tasks = [r for r in ran if r and r[0] in ("/Change", "/Run", "/End")]
                if task:
                    self.assertIn(["/Change", "/TN", task, "/ENABLE"], tasks)
                    self.assertIn(["/Change", "/TN", task, "/DISABLE"], tasks)
                    self.assertIn(["/End", "/TN", task], tasks)
                else:
                    self.assertEqual(tasks, [])


if __name__ == "__main__":
    unittest.main()
