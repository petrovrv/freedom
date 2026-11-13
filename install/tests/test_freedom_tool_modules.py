# SPDX-License-Identifier: AGPL-3.0-or-later
# install/freedom_modules.py (Freedom F10): modules of tools and services - provides.tools through toolbox (install,
# owned-by-module state on enable/disable/remove), schedule services, secret names, home/ and tools/ never uploaded,
# sync with the switches of the server. A temporary tool library with the real toolbox; the server is mocked.
import json
import os
import pathlib
import shutil
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import freedom_modules as fm  # noqa: E402
import freedom_release as fr  # noqa: E402
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402

REPO = pathlib.Path(__file__).resolve().parents[2]
TOOLBOX = next((p for p in (pathlib.Path(os.environ.get("AIWB_TOOLS", "")) / "toolbox", pathlib.Path(r"C:\AIWorkbench\tools\toolbox"),
                            REPO / "tools" / "toolbox") if (p / "modstate.py").is_file()), None)


def make_tool(root: pathlib.Path, name: str, version: str = "0.1.0"):
    d = root / name
    d.mkdir(parents=True, exist_ok=True)
    (d / "SKILL.md").write_text(f"---\nname: {name}\ndescription: test tool {name}\nversion: {version}\nentry: {name}.py\n---\n\n# {name}\n",
                                encoding="utf-8")
    (d / f"{name}.py").write_text("print('hi')\n", encoding="utf-8")


class StandardModules(unittest.TestCase):
    def test_modules_of_the_tree_check_clean(self):
        for d in sorted((REPO / "modules").glob("*/module.json")):
            with self.subTest(module=d.parent.name):
                self.assertEqual(fm.check(d.parent), [])

    def test_community_home_code_never_uploaded(self):
        files = fm.module_files(REPO / "modules" / "community")
        self.assertIn("server/ops.php", files)
        self.assertIn("ui/forum.html", files)
        self.assertFalse(any(k.startswith(("home/", "tools/")) for k in files))


class HomeParts(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp())
        self.root = self.tmp / "mod"
        (self.root / "home").mkdir(parents=True)
        self.man = {"schema": 1, "id": "mod", "version": "0.1.0", "ext": ">=1 <2", "provides": {
            "tools": [{"name": "mod-tool", "minVersion": "0.2.0"}, "other-tool"],
            "services": [{"id": "tick", "kind": "schedule", "cron": "0 7 * * *", "cwd": "home", "run": "python -m x"},
                         {"id": "srv", "tool": "mod-tool", "run": "serve", "windowsTask": "\\AIWorkbench\\ModSrv"}],
            "secrets": [{"name": "aiwb/mod"}, "aiwb/mod/token"]}}
        self.write()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self):
        (self.root / "module.json").write_text(json.dumps(self.man), encoding="utf-8")

    def test_clean(self):
        self.assertEqual(fm.check(self.root), [])

    def test_bad_parts(self):
        p = self.man["provides"]
        p["tools"].append({"name": "Bad_Tool"})
        p["services"] += [{"id": "nocron", "kind": "schedule", "cwd": "home", "run": "x"},
                          {"id": "weird", "kind": "cron", "tool": "nope", "run": "x"},
                          {"id": "out", "cwd": "../etc", "run": "x"},
                          {"id": "task", "run": "x", "windowsTask": "\\Microsoft\\Windows\\Defrag"},
                          {"id": "tick", "cwd": "home", "run": "x"}]
        p["secrets"].append({"name": "gmail"})
        (self.root / "tools" / "other-tool").mkdir(parents=True)  # shipped without SKILL.md
        self.write()
        got = " | ".join(fm.check(self.root))
        for want in ("Bad_Tool", "needs cron", "daemon|schedule", "'nope' is not in provides.tools", "outside home/",
                     "windowsTask must be under", "'tick' malformed or twice", "secret 'gmail'", "tools/other-tool has no SKILL.md"):
            self.assertIn(want, got)

    def test_tool_entries(self):
        self.assertEqual(fm.tool_entries(self.man), [{"name": "mod-tool", "minVersion": "0.2.0"},
                                                     {"name": "other-tool", "minVersion": ""}])

    def test_schedule_is_not_started_at_enable(self):
        calls = []
        with mock.patch.object(fm.sys, "platform", "win32"), \
                mock.patch.object(fm.subprocess, "run", side_effect=lambda a, **k: calls.append(a) or
                                  fm.subprocess.CompletedProcess(a, 0, "", "")):
            fm.service({"provides": {"services": [{"id": "s", "kind": "schedule", "windowsTask": "\\AIWorkbench\\S"},
                                                  {"id": "d", "windowsTask": "\\AIWorkbench\\D"}]}}, "start")
        self.assertEqual([c[1] for c in calls], ["/Change", "/Change", "/Run"])
        self.assertEqual(calls[2][-1], "\\AIWorkbench\\D")


@unittest.skipIf(TOOLBOX is None, "toolbox 1.7.0+ not found (set AIWB_TOOLS)")
class ToolLibrary(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp())
        self.lib = self.tmp / "lib"
        (self.lib / ".git").mkdir(parents=True)   # toolbox writes into AIWB_TOOLS only when it is a working copy
        (self.lib / "toolbox").mkdir()
        for f in TOOLBOX.iterdir():
            if f.is_file():
                shutil.copy2(f, self.lib / "toolbox" / f.name)
        make_tool(self.lib, "other-tool", "1.0.0")
        self.root = self.tmp / "mods" / "mod"
        make_tool(self.root / "tools", "mod-tool", "0.2.0")
        self.man = {"schema": 1, "id": "mod", "version": "0.1.0", "ext": ">=1 <2",
                    "provides": {"tools": [{"name": "mod-tool", "minVersion": "0.2.0"}, {"name": "other-tool"}]}}
        (self.root / "module.json").write_text(json.dumps(self.man), encoding="utf-8")
        self.state_file = self.tmp / "tool-modules.json"
        claude = self.tmp / "CLAUDE.md"
        claude.write_text("x\n<!-- aiwb-tools-index:begin -->\n<!-- aiwb-tools-index:end -->\n", encoding="utf-8")
        self.env = mock.patch.dict(os.environ, {"AIWB_TOOLS": str(self.lib), "AIWB_TOOLS_MIRROR": str(self.tmp / "mirror"),
                                                "AIWB_CLAUDE_MD": str(claude), "AIWB_AGENTS_MD": str(self.tmp / "AGENTS.md"),
                                                "AIWB_TOOL_MODULES": str(self.state_file), "AIWB_MODULE_DIRS": str(self.tmp / "mods"),
                                                "PYTHONIOENCODING": "utf-8"})
        self.env.start()
        self.cfg = mock.patch.object(fm.aiwb_install, "get", lambda k, d=None: d)
        self.cfg.start()

    def tearDown(self):
        self.cfg.stop()
        self.env.stop()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def state(self):
        return json.loads(self.state_file.read_text(encoding="utf-8"))["modules"]

    def test_plan_install_and_switches(self):
        plan = {t["name"]: t["action"] for t in fm.tools_plan(self.root, self.man)}
        self.assertEqual(plan, {"mod-tool": "install", "other-tool": "ok"})
        fm.tools_install(self.root, self.man)
        self.assertEqual(fm.tool_version(self.lib, "mod-tool"), "0.2.0")
        self.assertTrue(fm.tools_state(self.man, "disabled")["ok"])
        self.assertEqual(self.state()["mod"]["tools"], ["mod-tool", "other-tool"])
        self.assertFalse(self.state()["mod"]["enabled"])
        fm.tools_state(self.man, "enabled")
        self.assertTrue(self.state()["mod"]["enabled"])
        fm.tools_state(self.man, "removed")
        self.assertNotIn("mod", self.state())
        self.assertTrue((self.lib / "mod-tool" / "SKILL.md").is_file())   # files stay in the library

    def test_missing_tool_stops_before_anything(self):
        self.man["provides"]["tools"].append({"name": "absent-tool"})
        with self.assertRaises(fm.Fail) as e:
            fm.tools_install(self.root, self.man)
        self.assertIn("absent-tool (missing)", str(e.exception))
        self.assertIsNone(fm.tool_version(self.lib, "mod-tool"))

    def test_too_old_tool(self):
        self.man["provides"]["tools"] = [{"name": "other-tool", "minVersion": "2.0"}]
        with self.assertRaises(fm.Fail) as e:
            fm.tools_install(self.root, self.man)
        self.assertIn("other-tool (old, needs 2.0)", str(e.exception))

    def test_shipped_tool_below_min_version_changes_nothing(self):
        make_tool(self.lib, "mod-tool", "0.1.0")
        self.man["provides"]["tools"] = [{"name": "mod-tool", "minVersion": "0.3.0"}]   # shipped 0.2.0 is newer, not enough
        with self.assertRaises(fm.Fail) as e:
            fm.tools_install(self.root, self.man)
        self.assertIn("mod-tool (old, needs 0.3.0)", str(e.exception))
        self.assertEqual(fm.tool_version(self.lib, "mod-tool"), "0.1.0")

    def test_failed_service_is_retried_by_the_next_sync(self):
        fm.tools_install(self.root, self.man)
        fm.tools_state(self.man, "disabled")
        with mock.patch.object(fm, "service", return_value=["\\AIWorkbench\\X: start failed: access denied"]):
            done = fm.sync([{"id": "mod", "enabled": True}])
        self.assertFalse(done[0]["ok"])
        self.assertFalse(self.state()["mod"]["enabled"])           # not recorded: the next sync tries again
        with mock.patch.object(fm, "service", return_value=["\\AIWorkbench\\X: start ok"]):
            done = fm.sync([{"id": "mod", "enabled": True}])
        self.assertTrue(done[0]["ok"])
        self.assertTrue(self.state()["mod"]["enabled"])

    def test_failed_stop_at_remove_is_retried(self):
        fm.tools_install(self.root, self.man)
        fm.tools_state(self.man, "enabled")
        bad = ["\\AIWorkbench\\X: stop failed: access denied"]
        with self._server([{"ok": True, "data": "keep"}]), mock.patch.object(fm, "service", return_value=bad):
            self.assertEqual(fm.main(["remove", "mod", "--data", "keep"]), 1)
        self.assertFalse(self.state()["mod"]["enabled"])            # kept, switched off: sync retries the removal
        with mock.patch.object(fm, "service", return_value=["\\AIWorkbench\\X: stop ok"]):
            done = fm.sync([])
        self.assertTrue(done[0]["ok"])
        self.assertNotIn("mod", self.state())

    def test_update_left_off_stops_services(self):
        key = Ed25519PrivateKey.generate()
        trust = self.tmp / "maintainers.json"
        trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(key)}]}))
        fr.write_json(self.root / fr.MANIFEST_NAME, fr.sign(fr.build(self.root, "0.1.0"), "m1", key))
        live = {"modules": [{"id": "mod", "version": "0.0.9", "enabled": True, "files": {}}]}
        with self._server([live, {"ok": True, "enabled": False, "grew": ["net.listen"]}]), \
                mock.patch.object(fm, "ssh_client"), mock.patch.object(fm, "upload"), \
                mock.patch.object(fm, "service", return_value=["\\AIWorkbench\\X: stop ok"]) as svc:
            self.assertEqual(fm.main(["install", str(self.root), "--trust", str(trust), "--enable"]), 0)
        svc.assert_called_once()
        self.assertEqual(svc.call_args[0][1], "stop")
        self.assertFalse(self.state()["mod"]["enabled"])

    def test_failed_start_at_enable_is_not_recorded(self):
        fm.tools_install(self.root, self.man)
        fm.tools_state(self.man, "disabled")
        with self._server([{"ok": True}]), \
                mock.patch.object(fm, "service", return_value=["\\AIWorkbench\\X: start failed: no such task"]):
            self.assertEqual(fm.main(["enable", "mod"]), 1)
        self.assertFalse(self.state()["mod"]["enabled"])

    def test_planted_bytecode_is_removed_before_verification(self):
        key = Ed25519PrivateKey.generate()
        fr.write_json(self.root / fr.MANIFEST_NAME, fr.sign(fr.build(self.root, "0.1.0"), "m1", key))
        cache = self.root / "tools" / "mod-tool" / "__pycache__"
        cache.mkdir()
        (cache / "mod-tool.cpython-312.pyc").write_bytes(b"evil")
        (self.root / "stray.pyc").write_bytes(b"evil")
        trust = {"keys": {fr.public_hex(key): "m1"}, "threshold": 1}
        self.assertEqual(fr.verify(self.root, json.loads((self.root / fr.MANIFEST_NAME).read_text()), trust, strict=True), [])
        self.assertFalse(cache.exists() or (self.root / "stray.pyc").exists())

    def _server(self, answers):
        calls = iter(answers)
        return mock.patch.object(fm, "host_call", side_effect=lambda p: next(calls))

    def test_install_enable_disable_remove_cycle(self):
        key = Ed25519PrivateKey.generate()
        trust = self.tmp / "maintainers.json"
        trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(key)}]}))
        fr.write_json(self.root / fr.MANIFEST_NAME, fr.sign(fr.build(self.root, "0.1.0"), "m1", key))
        with self._server([{"modules": []}, {"ok": True, "id": "mod", "version": "0.1.0", "enabled": False},
                           {"ok": True}]), mock.patch.object(fm, "ssh_client"), mock.patch.object(fm, "upload") as up:
            self.assertEqual(fm.main(["install", str(self.root), "--trust", str(trust), "--enable"]), 0)
        self.assertEqual(up.call_args[0][2], {})       # tools/ is not uploaded
        self.assertTrue(self.state()["mod"]["enabled"])
        with self._server([{"ok": True}]):
            self.assertEqual(fm.main(["disable", "mod", "--no-service"]), 0)
        self.assertFalse(self.state()["mod"]["enabled"])
        with self._server([{"modules": [{"id": "mod", "version": "0.1.0", "enabled": True}]}]):
            self.assertEqual(fm.main(["sync"]), 0)     # switched on in CO Web: the home PC follows
        self.assertTrue(self.state()["mod"]["enabled"])
        with self._server([{"ok": True, "data": "keep"}]):
            self.assertEqual(fm.main(["remove", "mod", "--data", "keep"]), 0)
        self.assertNotIn("mod", self.state())

    def test_sync_leaves_unchanged_modules_alone(self):
        fm.tools_install(self.root, self.man)
        fm.tools_state(self.man, "enabled")
        with mock.patch.object(fm, "service", side_effect=AssertionError("no service call for an unchanged module")):
            self.assertEqual(fm.sync([{"id": "mod", "enabled": True}]), [])
        done = fm.sync([], apply=False)
        self.assertEqual(done, [{"id": "mod", "removed": True}])


if __name__ == "__main__":
    unittest.main()
