# SPDX-License-Identifier: AGPL-3.0-or-later
# install/freedom_modules.py (Freedom F10): manifest checks, signatures (standard / trusted author / untrusted), which
# files go to the server, ext ranges, switches. No network: the server side is co-agent/tests/modules-test.php.
import datetime
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

GEO = pathlib.Path(__file__).resolve().parents[2] / "modules" / "geo-presence"
CANVAS = GEO.parent / "canvas"
TASK_XML = """<?xml version="1.0" encoding="UTF-16"?>\r\r
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">\r\r
  <Triggers><LogonTrigger><UserId>PC\\Admin</UserId></LogonTrigger></Triggers>\r\r
  <Actions Context="Author">\r\r
    <Exec>\r\r
      <Command>C:\\Python\\pythonw.exe</Command>\r\r
      <Arguments>"{script}"</Arguments>\r\r
      <WorkingDirectory>{folder}</WorkingDirectory>\r\r
    </Exec>\r\r
  </Actions>\r\r
</Task>\r\r
"""
CORP = pathlib.Path(__file__).resolve().parents[2] / "modules" / "corporate"


class ModulesTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name) / "geo-presence"
        shutil.copytree(GEO, self.root, ignore=shutil.ignore_patterns(fr.MANIFEST_NAME, "desktop.ini"))
        self.maint, self.author, self.other = (Ed25519PrivateKey.generate() for _ in range(3))
        self.trust = pathlib.Path(self.tmp.name) / "maintainers.json"
        self.trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(self.maint)}]}))

    def tearDown(self):
        self.tmp.cleanup()

    def man(self):
        return json.loads((self.root / "module.json").read_text(encoding="utf-8"))

    def write_man(self, m):
        (self.root / "module.json").write_text(json.dumps(m), encoding="utf-8")

    def sign(self, key, name="m1"):
        m = fr.build(self.root, "0.5.0")
        fr.write_json(self.root / fr.MANIFEST_NAME, fr.sign(m, name, key))

    def test_geo_module_checks_clean(self):
        self.assertEqual(fm.check(GEO), [])

    def test_bad_manifest_parts(self):
        m = self.man()
        m["id"] = "Bad_Id"
        m["ext"] = ">=2"
        m["provides"]["ui"][0]["entry"] = "ui/../server/ops.php"
        m["server"]["ops"]["user"].append("Drop-Table")
        m["server"]["ops"]["evil"] = []
        m["personal"]["default"] = "always"
        m["dataPolicy"]["migrations"] = "destructive"
        self.write_man(m)
        p = " | ".join(fm.check(self.root))
        for want in ("id:", "ext", "ui entry", "Drop-Table", "server.ops.evil", "personal.default", "additive"):
            self.assertIn(want, p)

    def test_device_tools(self):
        (self.root / "device").mkdir()
        (self.root / "device" / "hello.py").write_text("print('{}')\n")
        m = self.man()
        m["provides"]["deviceTools"] = [{"name": "geo_hello", "class": "R", "run": "device/hello.py"},
                                        {"name": "geo_native", "class": "X"}]
        m["provides"]["deviceOps"] = ["geo_folder_add"]
        m["device"] = {"default": "off", "warning": "x", "ops": "device/hello.py"}
        self.write_man(m)
        self.assertIn("deviceTools/deviceOps/device need the permission device.tools", fm.check(self.root))
        m["permissions"] = list(m.get("permissions") or []) + ["device.tools"]
        self.write_man(m)
        self.assertEqual(fm.check(self.root), [])
        m["provides"]["deviceTools"] += [{"name": "geo_hello", "class": "R"}, {"name": "Bad", "class": "R"},
                                         {"name": "geo_cls", "class": "Q"}, {"name": "geo_out", "class": "R", "run": "server/ops.php"},
                                         {"name": "geo_gone", "class": "R", "run": "device/none.py"}]
        m["provides"]["deviceOps"].append("Bad Op")
        m["device"]["default"] = "maybe"
        m["device"]["ops"] = "ui/header.html"
        self.write_man(m)
        p = " | ".join(fm.check(self.root))
        for want in ("'geo_hello' twice", "'Bad'", "geo_cls", "geo_out", "geo_gone", "deviceOps 'Bad Op'", "device.default", "device.ops"):
            self.assertIn(want, p)

    def test_missing_server_entry(self):
        (self.root / "server" / "ops.php").unlink()
        self.assertTrue(any("server entry" in x for x in fm.check(self.root)))

    def test_standard_signature(self):
        self.sign(self.maint)
        v = fm.verify(self.root, self.trust)
        self.assertTrue(v["standard"] and v["trusted"] and v["publisher"] == "m1")

    def test_changed_file_after_signing_fails(self):
        self.sign(self.maint)
        (self.root / "ui" / "header.js").write_text("evil()", encoding="utf-8")
        v = fm.verify(self.root, self.trust)
        self.assertFalse(v["trusted"])
        self.assertTrue(any("changed ui/header.js" in x for x in v["problems"]))

    def test_added_file_after_signing_fails(self):
        self.sign(self.maint)
        (self.root / "server" / "extra.php").write_text("<?php", encoding="utf-8")
        self.assertFalse(fm.verify(self.root, self.trust)["trusted"])

    def test_trusted_author_is_not_standard(self):
        self.sign(self.author, "anna")
        with mock.patch.object(fm.aiwb_install, "get", lambda k, d=None: [{"name": "anna", "key": fr.public_hex(self.author)}] if k == "modules.trustedKeys" else d):
            v = fm.verify(self.root, self.trust)
        self.assertEqual((v["standard"], v["trusted"], v["publisher"]), (False, True, "anna"))

    def test_unknown_key_untrusted(self):
        self.sign(self.other, "x")
        with mock.patch.object(fm.aiwb_install, "get", lambda k, d=None: d):
            v = fm.verify(self.root, self.trust)
        self.assertFalse(v["trusted"])

    def test_unsigned(self):
        self.assertFalse(fm.verify(self.root, self.trust)["trusted"])

    def test_files_for_server(self):
        all_ = fm.module_files(GEO)
        self.assertIn("server/ops.php", all_)
        self.assertIn("ui/header.html", all_)
        self.assertNotIn("README.md", all_)
        self.assertNotIn("module.json", all_)
        self.assertNotIn(fr.MANIFEST_NAME, all_)
        self.assertFalse(any(k.startswith("server/") for k in fm.module_files(GEO, with_server=False)))

    def test_ext_ranges(self):
        self.assertTrue(fm.ext_ok(">=1 <2") and fm.ext_ok("1") and fm.ext_ok(""))
        self.assertFalse(fm.ext_ok("<1") or fm.ext_ok(">=2") or fm.ext_ok("one"))
        self.assertTrue(fm.ext_ok(">=1 <3", have=2))

    def test_people(self):
        self.assertEqual(fm.parse_people(["sam=auto", "alex=off"]), {"sam": "auto", "alex": "off"})
        with self.assertRaises(fm.Fail):
            fm.parse_people(["sam=always"])

    def test_erase_needs_yes(self):
        with mock.patch.object(fm, "host_call", side_effect=AssertionError("must not call the server")):
            self.assertEqual(fm.main(["remove", "geo-presence", "--data", "erase"]), 1)

    def test_untrusted_install_refused_before_network(self):
        with mock.patch.object(fm, "host_call", side_effect=AssertionError("must not call the server")), \
                mock.patch.object(fm.aiwb_install, "get", lambda k, d=None: d):
            self.assertEqual(fm.main(["install", str(self.root), "--trust", str(self.trust)]), 1)

    def _install(self, answers):
        """install with the server side mocked: answers = what host_call returns or raises, in order."""
        self.sign(self.maint)
        rollback = mock.Mock()
        calls = iter(answers)

        def host(params):
            r = next(calls)
            if isinstance(r, Exception):
                raise r
            return r
        with mock.patch.object(fm, "host_call", side_effect=host), mock.patch.object(fm, "ssh_client"),                 mock.patch.object(fm, "upload", return_value=rollback):
            code = fm.main(["install", str(self.root), "--trust", str(self.trust)])
        return code, rollback

    def test_unknown_outcome_keeps_new_files(self):
        code, rollback = self._install([{"modules": []}, OSError("timeout"), OSError("network down")])
        self.assertEqual(code, 1)
        rollback.assert_not_called()

    def test_refusal_restores_old_files(self):
        code, rollback = self._install([{"modules": []}, {"ok": False, "error": "file_mismatch"}])
        self.assertEqual(code, 1)
        rollback.assert_called_once()

    def test_install_sends_config(self):
        m = self.man()
        m["installConfig"] = {"publicUrl": "canvas.publicUrl", "missing": "no.such.key"}
        self.write_man(m)
        sent = []

        def host(params):
            sent.append(params)
            return {"modules": []} if params["action"] == "list" else {"ok": True, "enabled": False}
        cfg = {"canvas.publicUrl": "https://pc.example.ts.net"}
        self.sign(self.maint)
        with mock.patch.object(fm, "host_call", side_effect=host), mock.patch.object(fm, "ssh_client"), \
                mock.patch.object(fm, "upload"), mock.patch.object(fm.aiwb_install, "get", lambda k, d=None: cfg.get(k, d)):
            code = fm.main(["install", str(self.root), "--trust", str(self.trust)])
        self.assertEqual(code, 0)
        inst = next(x for x in sent if x["action"] == "install")
        self.assertEqual(inst["config"], {"publicUrl": "https://pc.example.ts.net"})


class DockerVariantTest(unittest.TestCase):
    """Variant B/C (rehearsal 13.11): the server is the Docker stack - db.php with the stack's key, no SSH, no keyring."""

    def setUp(self):
        fm._DOCKER.clear()
        self.calls = []

        class Host:
            def secret(_, name):
                return "stack-key" if name == "push_key" else None

        fake_fd = mock.Mock()
        fake_fd.http_req = lambda s, path, method, body, headers, ca=None, limit=200000: (
            self.calls.append((path, headers, limit)) or (200, {}, '{"ok": true, "modules": []}'))
        fm._DOCKER.update(fd=fake_fd, s={}, h=Host())
        self.p = mock.patch.object(fm.aiwb_install, "get", side_effect=lambda k, d=None: "C" if k == "variant" else d)
        self.p.start()

    def tearDown(self):
        self.p.stop()
        fm._DOCKER.clear()

    def test_host_call_uses_the_stack_key_and_reads_all(self):
        with mock.patch("keyring.get_password", side_effect=AssertionError("keyring of another install")):
            self.assertEqual(fm.host_call({"action": "list"}), {"ok": True, "modules": []})
        path, headers, limit = self.calls[0]
        self.assertEqual((path, headers["X-CO-Push-Key"], limit), ("/agent/db.php", "stack-key", None))

    def test_ssh_client_honours_another_install_json(self):
        with mock.patch.dict(fm.os.environ, {"AIWB_INSTALL": "x.json"}, clear=False), \
                mock.patch.object(fm.aiwb_install, "ssh_connect", return_value="own-ssh") as sc:
            fm.os.environ.pop("AIWB_HZ_DIR", None)
            self.assertEqual(fm.ssh_client(), "own-ssh")
            sc.assert_called_once()


class Ext1ManifestTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name) / "canvas"
        shutil.copytree(CANVAS, self.root, ignore=shutil.ignore_patterns(fr.MANIFEST_NAME, "desktop.ini", "__pycache__"))

    def tearDown(self):
        self.tmp.cleanup()

    def test_canvas_module_checks_clean(self):
        self.assertEqual(fm.check(CANVAS), [])

    def test_bad_ext1_declarations(self):
        m = json.loads((self.root / "module.json").read_text(encoding="utf-8"))
        prov = m["provides"]
        prov["ui"].append({"slot": "ui.header", "id": "h", "entry": "ui/editor.html", "path": "/x"})
        prov["ui"][0]["path"] = "/Bad Path"
        prov["fileActions"][0]["open"]["page"] = "nope"
        prov["fileActions"][1]["exts"] = ["MD!"]
        prov["sideJobs"][0]["key"] = "1bad"
        prov["navigate"] += [{"config": "other"}, "http://plain.example", "javascript:alert(1)"]
        prov["messageViews"] = [{"match": "(unclosed"}]
        prov["modes"] = [{"key": "study", "label": "Study", "rules": "context/none.md"}]
        prov["services"][0]["run"] = "canvas-bridge/missing.py"
        m["installConfig"] = {"publicUrl": 5}
        (self.root / "module.json").write_text(json.dumps(m), encoding="utf-8")
        p = " | ".join(fm.check(self.root))
        for want in ("ui path '/x'", "ui path '/Bad Path'", "open.page", "exts", "sideJob", "installConfig",
                     "{'config': 'publicUrl'}", "http://plain.example", "javascript:", "(unclosed", "rules file", "missing.py"):
            self.assertIn(want, p)


class ServiceTaskTest(unittest.TestCase):
    """service(): a task that runs another copy of a module's script is re-pointed from its own XML and restarted."""

    def setUp(self):
        self.man = json.loads((CANVAS / "module.json").read_text(encoding="utf-8"))
        self.script = fm.service_script(self.man, self.man["provides"]["services"][0])
        self.task = self.man["provides"]["services"][0]["windowsTask"]
        self.calls = []
        self.created = None

    def run_service(self, action, xml, refusal=""):
        def fake(args, **kw):
            self.calls.append(args[1] if args[0] == "schtasks" else args)
            if args[1] == "/Query":
                return subprocess.CompletedProcess(args, 0, xml.encode("ascii"), b"")
            if args[1] == "/Create":
                self.created = pathlib.Path(args[args.index("/XML") + 1]).read_bytes()
            return subprocess.CompletedProcess(args, 0, "", "")
        with mock.patch.object(fm.subprocess, "run", side_effect=fake), mock.patch.object(fm.sys, "platform", "win32"), \
                mock.patch.object(fm, "repoint_refusal", return_value=refusal):
            return fm.service(self.man, action)

    def test_script_is_in_the_repo_module_folder(self):
        self.assertEqual(self.script, (CANVAS / "canvas-bridge" / "canvas_bridge.py").resolve())
        geo = json.loads((GEO / "module.json").read_text(encoding="utf-8"))
        self.assertIsNone(fm.service_script(geo, geo["provides"]["services"][0]))  # a library tool runs it

    def test_old_task_repointed_and_restarted(self):
        old = TASK_XML.format(script=r"C:\AIWorkbench\source\canvas-bridge\canvas_bridge.py", folder=r"C:\AIWorkbench\source\canvas-bridge")
        done = self.run_service("start", old)
        self.assertEqual(self.calls, ["/Query", "/Create", "/Change", "/End", "/Run"])
        self.assertTrue(self.created.startswith(b"\xff\xfe"))  # UTF-16 with BOM, as its declaration says
        xml = self.created.decode("utf-16")
        self.assertIn(f'<Arguments>"{self.script}"</Arguments>', xml)
        self.assertIn(f"<WorkingDirectory>{self.script.parent}</WorkingDirectory>", xml)
        self.assertIn(r"<Command>C:\Python\pythonw.exe</Command>", xml)  # the task's own Python stays
        self.assertIn(r"<LogonTrigger><UserId>PC\Admin</UserId></LogonTrigger>", xml)  # the rest of the task too
        self.assertNotIn("source\\canvas-bridge", xml)
        self.assertNotIn("\r\r", xml)
        self.assertEqual(xml.count("<Exec>"), 1)
        self.assertTrue(any("re-pointed" in d for d in done) and any("restart ok" in d for d in done))

    def test_right_task_untouched(self):
        right = TASK_XML.format(script=str(self.script), folder=str(self.script.parent))
        done = self.run_service("start", right)
        self.assertEqual(self.calls, ["/Query", "/Change", "/Run"])
        self.assertIsNone(self.created)
        self.assertEqual(done, [f"{self.task}: start ok"])

    def test_missing_task_reported(self):
        def fake(args, **kw):
            self.calls.append(args[1])
            err = "ERROR: The system cannot find the file specified."
            return subprocess.CompletedProcess(args, 1, "" if kw.get("text") else b"", err if kw.get("text") else err.encode())
        with mock.patch.object(fm.subprocess, "run", side_effect=fake), mock.patch.object(fm.sys, "platform", "win32"), \
                mock.patch.object(fm, "repoint_refusal", return_value=""):
            done = fm.service(self.man, "start")
        self.assertIn("/Query", self.calls)
        self.assertNotIn("/Create", self.calls)
        self.assertTrue(any("not found" in d for d in done))

    def test_stop_never_repoints(self):
        self.run_service("stop", TASK_XML.format(script=r"C:\old\x.py", folder=r"C:\old"))
        self.assertEqual(self.calls, ["/Change", "/End"])

    def test_refused_repoint_still_starts_the_old_task(self):
        # e.g. the installer runs from a worktree that devtree deletes, or the script is missing
        done = self.run_service("start", TASK_XML.format(script=r"C:\old\x.py", folder=r"C:\old"), refusal="x is in a git worktree")
        self.assertEqual(self.calls, ["/Change", "/Run"])
        self.assertTrue(any("not re-pointed" in d for d in done) and fm.services_ok(done))

    def test_failed_repoint_still_starts_the_old_task(self):
        def fake(args, **kw):
            self.calls.append(args[1])
            if args[1] == "/Query":
                return subprocess.CompletedProcess(args, 0, TASK_XML.format(script=r"C:\old\x.py", folder=r"C:\old").encode("ascii"), b"")
            if args[1] == "/Create":
                return subprocess.CompletedProcess(args, 1, "", "ERROR: needs a password")
            return subprocess.CompletedProcess(args, 0, "", "")
        with mock.patch.object(fm.subprocess, "run", side_effect=fake), mock.patch.object(fm.sys, "platform", "win32"), \
                mock.patch.object(fm, "repoint_refusal", return_value=""):
            done = fm.service(self.man, "start")
        self.assertEqual(self.calls, ["/Query", "/Create", "/Change", "/Run"])
        self.assertTrue(any("old action stays" in d for d in done) and fm.services_ok(done))

    def test_repoint_refusal(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIn("does not exist", fm.repoint_refusal(pathlib.Path(d) / "nope.py"))
        here = pathlib.Path(fm.__file__).resolve()
        g = subprocess.run(["git", "-C", str(here.parent), "rev-parse", "--git-dir", "--git-common-dir"], capture_output=True, text=True).stdout.split()
        in_worktree = len(g) == 2 and pathlib.Path(here.parent, g[0]).resolve() != pathlib.Path(here.parent, g[1]).resolve()
        self.assertEqual(bool(fm.repoint_refusal(here)), in_worktree)


MODULES = pathlib.Path(__file__).resolve().parents[2] / "modules"


class F10ModulesTest(unittest.TestCase):
    """The modules of F10 beyond the probe (chat-import, task-hints, browser-ext) and the manifest parts they added:
    ui.hint (needs composer.read), provides.import (needs import.write), localOnly (signed, not uploaded), generated
    (neither signed nor uploaded); and the lifecycle of each through the CLI with the server mocked."""

    IDS = ("chat-import", "task-hints", "browser-ext")

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.key = Ed25519PrivateKey.generate()
        self.trust = pathlib.Path(self.tmp.name) / "maintainers.json"
        self.trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(self.key)}]}))

    def tearDown(self):
        self.tmp.cleanup()

    def copy(self, mid):
        root = pathlib.Path(self.tmp.name) / mid
        shutil.copytree(MODULES / mid, root, ignore=shutil.ignore_patterns(fr.MANIFEST_NAME, "desktop.ini", "site.js", "dist", "__pycache__"))
        return root

    def sign(self, root):
        m = fm.load_manifest(root)
        man = fm.signed_files(root, fr.build(root, str(m["version"])))
        fr.write_json(root / fr.MANIFEST_NAME, fr.sign(man, "m1", self.key))

    def test_modules_check_clean(self):
        for mid in self.IDS:
            self.assertEqual(fm.check(MODULES / mid), [], mid)

    def test_hint_slot_needs_composer_read(self):
        root = self.copy("task-hints")
        m = fm.load_manifest(root)
        m["permissions"].remove("composer.read")
        (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
        self.assertTrue(any("composer.read" in x for x in fm.check(root)))

    def test_import_needs_import_write_and_a_clean_source(self):
        root = self.copy("chat-import")
        m = fm.load_manifest(root)
        m["permissions"].remove("import.write")
        m["provides"]["import"][0]["source"] = "Bad Source"
        (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
        p = " | ".join(fm.check(root))
        self.assertIn("import.write", p)
        self.assertIn("Bad Source", p)

    def test_local_only_cannot_hide_ui_or_server(self):
        root = self.copy("chat-import")
        m = fm.load_manifest(root)
        m["localOnly"] = ["ui/", "../x"]
        (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
        self.assertEqual(sum("localOnly" in x for x in fm.check(root)), 2)

    def test_generated_cannot_cover_code_the_installer_runs(self):
        root = self.copy("browser-ext")
        m = fm.load_manifest(root)
        m["generated"] = ["home/", "tools/x", "server/a.php", "helper.py", "extension/", "Home/", "extension/site.js", "dist/"]
        (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
        p = [x for x in fm.check(root) if x.startswith("generated")]
        self.assertEqual(len(p), 6, p)  # site.js inside the localOnly extension/ and the dist/ folder are fine
        self.assertFalse(fm.is_generated("extension/background.js", m))  # a folder entry never covers its code
        self.assertTrue(fm.is_generated("extension/site.js", m))
        # a manifest signed with a wider entry still verifies strictly: unsigned code is refused
        (root / "home").mkdir()
        (root / "home" / "run.py").write_text("print(1)", encoding="utf-8")
        self.assertFalse(fm.is_generated("home/run.py", m))
        (root / "dist").mkdir()
        (root / "dist" / "evil.js").write_text("x", encoding="utf-8")
        self.assertFalse(fm.is_generated("dist/evil.js", m))
        self.assertTrue(fm.is_generated("dist/x.zip", m))

    def test_sign_refuses_files_git_ignores(self):
        import subprocess
        root = self.copy("task-hints")
        subprocess.run(["git", "init", "-q", str(root)], check=True)
        (root / ".gitignore").write_text("*.png\n", encoding="utf-8")
        (root / "shot.png").write_bytes(b"png")
        with mock.patch.object(fr, "keyring_key", return_value=self.key):
            self.assertEqual(fm.main(["sign", str(root), "--name", "m1"]), 1)
            (root / "shot.png").unlink()
            (root / ".gitignore").unlink()
            import shutil as sh
            sh.rmtree(root / ".git", ignore_errors=True)
            self.assertEqual(fm.main(["sign", str(root), "--name", "m1"]), 0)

    def test_local_only_signed_not_uploaded(self):
        root = self.copy("chat-import")
        self.sign(root)
        signed = {f["path"] for f in json.loads((root / fr.MANIFEST_NAME).read_text(encoding="utf-8"))["files"]}
        self.assertIn("src/page.ts", signed)
        up = fm.module_files(root)
        self.assertIn("ui/import.js", up)
        self.assertFalse(any(k.startswith(("src/", "tests/")) or k in ("build.mjs", "tsconfig.json") for k in up))

    def test_generated_files_neither_signed_nor_uploaded(self):
        root = self.copy("browser-ext")
        (root / "extension" / "site.js").write_text("var CO_SITE='https://x';", encoding="utf-8")
        self.sign(root)
        signed = {f["path"] for f in json.loads((root / fr.MANIFEST_NAME).read_text(encoding="utf-8"))["files"]}
        self.assertNotIn("extension/site.js", signed)
        self.assertIn("extension/manifest.json", signed)
        # a site.js written after signing (ext-config on another PC) keeps the module verified
        (root / "extension" / "site.js").write_text("var CO_SITE='https://y';", encoding="utf-8")
        (root / "dist").mkdir()
        (root / "dist" / "x.zip").write_bytes(b"zip")
        self.assertTrue(fm.verify(root, self.trust)["standard"])
        self.assertEqual(fm.module_files(root), {})  # nothing of the extension goes to the server
        # any other added file still breaks it
        (root / "extension" / "evil.js").write_text("x", encoding="utf-8")
        self.assertFalse(fm.verify(root, self.trust)["trusted"])

    def test_lifecycle_of_each_module(self):
        """install --enable, disable, enable, update, remove: what the CLI sends to the server (mocked)."""
        for mid in self.IDS:
            root = self.copy(mid)
            self.sign(root)
            sent = []

            def host(params, mid=mid):
                sent.append(params)
                a = params["action"]
                if a == "list":
                    return {"modules": []}
                if a == "install":
                    return {"ok": True, "id": mid, "version": params["manifest"]["version"], "enabled": False}
                return {"ok": True}
            # the server, SSH, Windows tasks and the tool library are mocked: the test never touches the real ones
            with mock.patch.object(fm, "host_call", side_effect=host), mock.patch.object(fm, "ssh_client"), \
                    mock.patch.object(fm, "upload", return_value=mock.Mock()), mock.patch.object(fm, "service", return_value=[]), \
                    mock.patch.object(fm, "toolbox", return_value={"ok": True}), \
                    mock.patch.object(fm.aiwb_install, "get", lambda k, d=None: d):
                self.assertEqual(fm.main(["install", str(root), "--trust", str(self.trust), "--enable"]), 0, mid)
                self.assertEqual(fm.main(["disable", mid]), 0)
                self.assertEqual(fm.main(["enable", mid]), 0)
                m = fm.load_manifest(root)
                m["version"] = "9.9.9"
                (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
                self.sign(root)
                self.assertEqual(fm.main(["install", str(root), "--trust", str(self.trust)]), 0, mid)
                self.assertEqual(fm.main(["remove", mid, "--data", "keep"]), 0)
            acts = [p["action"] for p in sent]
            self.assertEqual(acts, ["list", "install", "set", "set", "set", "list", "install", "remove"], mid)
            inst = [p for p in sent if p["action"] == "install"]
            self.assertTrue(inst[0]["standard"], mid)
            self.assertEqual(inst[1]["manifest"]["version"], "9.9.9")
            self.assertEqual([p["enabled"] for p in sent if p["action"] == "set"], [True, False, True])
            if mid == "browser-ext":
                self.assertEqual(inst[0]["files"], {})


class HomeSideTest(unittest.TestCase):
    """Local folder for Bridge's module loader, services left to the Bridge supervisor, toolbox state; the corporate
    module through install -> enable -> disable -> remove with the server mocked."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.local = pathlib.Path(self.tmp.name) / "local"
        self.env = mock.patch.dict(fm.os.environ, {"AIWB_MODULES_DIR": str(self.local)})
        self.env.start()
        self.corp = pathlib.Path(self.tmp.name) / "corporate"
        shutil.copytree(CORP, self.corp, ignore=shutil.ignore_patterns(fr.MANIFEST_NAME, "desktop.ini"))
        self.maint = Ed25519PrivateKey.generate()
        self.trust = pathlib.Path(self.tmp.name) / "maintainers.json"
        self.trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(self.maint)}]}))
        fr.write_json(self.corp / fr.MANIFEST_NAME, fr.sign(fr.build(self.corp, "0.1.0"), "m1", self.maint))

    def tearDown(self):
        self.env.stop()
        self.tmp.cleanup()

    def test_corporate_module_checks_clean(self):
        self.assertEqual(fm.check(CORP), [])
        m = json.loads((CORP / "module.json").read_text(encoding="utf-8"))
        self.assertFalse(m["defaults"]["enabled"])
        self.assertTrue(m["confidential"] and m["warning"])
        self.assertTrue(all(t["risk"] in ("R", "W", "D") for t in m["provides"]["deviceTools"]))

    def test_new_extension_points_checked(self):
        m = json.loads((self.corp / "module.json").read_text(encoding="utf-8"))
        m["provides"]["deviceTools"][0]["risk"] = "X"
        m["provides"]["deviceTools"][0]["kind"] = "chat"
        m["provides"]["hooks"] = [{"event": "job.middle", "tool": "x", "run": "a; rm -rf"}]
        m["provides"]["context"] = [{"id": "r", "file": "context/missing.md"}]
        m["provides"]["services"] = [{"id": "svc", "tool": "Bad Tool", "run": "serve"}]
        m["provides"]["models"].append({"id": "loose"})
        m["warning"] = ""
        (self.corp / "module.json").write_text(json.dumps(m), encoding="utf-8")
        p = " | ".join(fm.check(self.corp))
        for want in ("job.before|job.after", "plain words", "context file", "gateway: a device tool",
                     "service svc", "model 'loose'", "warning"):
            self.assertIn(want, p)

    def test_local_install_replaces_the_folder(self):
        d = fm.install_local(self.corp, "corporate")
        self.assertTrue((d / "module.json").is_file() and (d / "context" / "rules.md").is_file())
        (d / "stale.txt").write_text("from an older version")
        fm.install_local(self.corp, "corporate")
        self.assertFalse((d / "stale.txt").exists())
        self.assertFalse((self.local / "corporate.new").exists())
        self.assertEqual(json.loads((d / "module.json").read_text(encoding="utf-8"))["id"], "corporate")
        self.assertTrue(fm.remove_local("corporate"))
        self.assertFalse(d.exists())

    def test_services_left_to_bridge_while_it_supervises(self):
        m = {"provides": {"services": [{"id": "receiver", "tool": "geo-presence", "run": "serve", "windowsTask": r"\X\Y"}]}}
        self.local.mkdir(parents=True)
        hb = self.local / ".bridge-supervisor.json"
        hb.write_text(json.dumps({"utc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")}))
        with mock.patch.object(fm.subprocess, "run", side_effect=AssertionError("no schtasks while Bridge supervises")):
            self.assertIn("Bridge supervises", fm.service(m, "start")[0])
        hb.write_text(json.dumps({"utc": "2020-01-01T00:00:00Z"}))   # stale: Bridge is not running its loader
        self.assertFalse(fm.bridge_supervises())

    def test_corporate_cycle(self):
        calls = []

        def host(params):
            calls.append(params.get("action"))
            a = params.get("action")
            if a == "list":
                return {"modules": []}
            if a == "install":
                self.assertFalse(params["manifest"]["defaults"]["enabled"])
                return {"ok": True, "id": "corporate", "version": "0.1.0", "enabled": False}
            if a == "set":
                return {"ok": True, "enabled": params["enabled"]}
            if a == "remove":
                return {"ok": True}
            raise AssertionError(a)
        states = []
        with mock.patch.object(fm, "host_call", side_effect=host), mock.patch.object(fm, "ssh_client"),                 mock.patch.object(fm, "upload", return_value=mock.Mock()),                 mock.patch.object(fm, "tools_state", side_effect=lambda m, st, *x: states.append(st) or {"ok": True}), mock.patch.object(fm, "tools_install", return_value=[]),                 mock.patch.object(fm, "service", return_value=[]):
            self.assertEqual(fm.main(["install", str(self.corp), "--trust", str(self.trust)]), 0)
            self.assertTrue((self.local / "corporate" / "module.json").is_file())   # Bridge reads it; module stays off
            self.assertEqual(fm.main(["enable", "corporate"]), 0)
            self.assertEqual(fm.main(["disable", "corporate"]), 0)
            self.assertEqual(fm.main(["remove", "corporate", "--data", "keep"]), 0)
        self.assertEqual(calls, ["list", "install", "set", "set", "remove"])
        self.assertEqual(states, ["disabled", "enabled", "disabled", "removed"])
        self.assertFalse((self.local / "corporate").exists())


if __name__ == "__main__":
    unittest.main()
