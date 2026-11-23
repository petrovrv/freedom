# SPDX-License-Identifier: AGPL-3.0-or-later
# Apps made on the fly (Freedom F14c, SPEC §3.6a item 6): the template of `freedom_modules.py new --app` passes check and
# the review before install, `envs` rules, the own-key level of verify, the review's findings stop install. No network.
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import freedom_modules as fm  # noqa: E402
import freedom_release as fr  # noqa: E402
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402

TREE = pathlib.Path(__file__).resolve().parents[2]


class AppTemplateTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.parent = pathlib.Path(self.tmp.name)
        self.cfg = {"members": [{"env": "olga", "role": "owner"}, {"env": "kid"}], "modules": {}}
        get = lambda k, d=None: {"members": self.cfg["members"], "modules.dirs": [str(self.parent)],  # noqa: E731
                                 "modules.trustedKeys": self.cfg["modules"].get("trustedKeys", []),
                                 "modules.ownKey": None}.get(k, d)
        self.patch = mock.patch.object(fm.aiwb_install, "get", side_effect=get)
        self.patch.start()
        self.locals = mock.patch.object(fm, "local_modules", return_value={})
        self.locals.start()

    def tearDown(self):
        self.locals.stop()
        self.patch.stop()
        self.tmp.cleanup()

    def make(self, **kw):
        return fm.new_app(kw.get("mid", "shop-list"), kw.get("parent"), kw.get("title", "Покупки"), kw.get("icon", "🛒"),
                          kw.get("env", ""), lang=kw.get("lang", "ru"))

    def test_template_is_clean(self):
        root = self.make()
        self.assertEqual(root, self.parent / "shop-list")
        self.assertEqual(fm.check(root), [])
        r = fm.review(root)
        self.assertEqual(r["errors"], [], r)
        m = fm.load_manifest(root)
        self.assertEqual(m["envs"], ["olga"])                   # the owner's env by default
        self.assertEqual(m["permissions"], ["data.env"])         # the least it needs
        self.assertNotIn("server", m)
        ui = m["provides"]["ui"][0]
        self.assertTrue(ui["app"])
        self.assertEqual((ui["icon"], ui["label"], ui["slot"]), ("🛒", "Покупки", "ui.page"))
        self.assertFalse(m["defaults"]["enabled"])
        js = (root / "ui" / "main.js").read_text(encoding="utf-8")
        self.assertIn("coExt.data.get", js)
        self.assertIn('coExt.title("Покупки")', js)
        html = (root / "ui" / "main.html").read_text(encoding="utf-8")
        self.assertIn('src="/ext/ext1.js"', html)
        self.assertIn('lang="ru"', html)
        # tests stay at home: signed with the module, never uploaded
        self.assertNotIn("tests/test_app.py", fm.module_files(root, with_server=False))
        self.assertIn("ui/main.js", fm.module_files(root, with_server=False))

    def test_template_test_runs(self):
        root = self.make(env="kid")
        r = subprocess.run([sys.executable, str(root / "tests" / "test_app.py")], capture_output=True, text=True,
                           env={**__import__("os").environ, "AIWB_FREEDOM": str(TREE)})
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertTrue(r.stdout.startswith("ok shop-list 0.1.0"))

    def test_title_is_escaped(self):
        root = self.make(title='<b>"x"</b>', mid="esc")
        html = (root / "ui" / "main.html").read_text(encoding="utf-8")
        self.assertNotIn("<b>", html)
        self.assertEqual(fm.check(root), [])

    def test_new_refuses(self):
        self.make()
        with self.assertRaises(fm.Fail):
            self.make()                                         # the folder exists
        with self.assertRaises(fm.Fail):
            self.make(mid="Bad_Id")
        with self.assertRaises(fm.Fail):
            self.make(mid="x2", env="Not An Env")
        self.cfg["members"] = []
        with self.assertRaises(fm.Fail):
            self.make(mid="x3")                                 # nobody to make it for

    def test_envs_rules(self):
        root = self.make()
        m = fm.load_manifest(root)
        for bad in ([], "olga", ["Olga!"]):
            m["envs"] = bad
            (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
            self.assertIn("envs: a non-empty list of environment ids", fm.check(root))
        m["envs"] = ["olga"]
        m["provides"]["tools"] = [{"name": "x"}]
        m["provides"]["context"] = [{"file": "context/a.md"}]
        (root / "module.json").write_text(json.dumps(m), encoding="utf-8")
        p = " | ".join(fm.check(root))
        self.assertIn("provides.tools: not for a module of some environments", p)
        self.assertIn("provides.context: not for a module of some environments", p)

    def test_review_finds(self):
        root = self.make()
        (root / "ui" / "main.js").write_text(
            "fetch('/x');\nconst s = document.createElement('script'); eval(s);\nlocation='https://evil.example.org/';\n"
            "localStorage.x = 1;\n", encoding="utf-8")
        (root / "ui" / "main.html").write_text('<script src="https://cdn.example.net/a.js"></script>', encoding="utf-8")
        r = fm.review(root)
        rules = {e.split(" ")[0] for e in r["errors"]}
        self.assertTrue({"network", "dynamic-code", "external-url", "storage-escape", "remote-code"} <= rules, r)

    def test_own_level_and_install_stops_on_findings(self):
        root = self.make()
        own, other = Ed25519PrivateKey.generate(), Ed25519PrivateKey.generate()
        self.cfg["modules"]["trustedKeys"] = [{"name": "owner", "key": fr.public_hex(own)},
                                              {"name": "friend", "key": fr.public_hex(other)}]
        trust = self.parent / "maintainers.json"
        trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "m1", "key": fr.public_hex(Ed25519PrivateKey.generate())}]}))

        def sign(key, name):
            man = fm.signed_files(root, fr.build(root, "0.1.0"))
            man["module"] = "shop-list"
            fr.write_json(root / fr.MANIFEST_NAME, fr.sign(man, name, key))

        sign(own, "owner")
        v = fm.verify(root, trust)
        self.assertEqual((v["trusted"], v["standard"], v["level"], v["publisher"]), (True, False, "own", "owner"))
        sign(other, "friend")
        self.assertEqual(fm.verify(root, trust)["level"], "trusted")
        (root / fr.MANIFEST_NAME).unlink()
        self.assertEqual(fm.verify(root, trust)["level"], "untrusted")
        # a finding of the review stops install before anything reaches the server
        sign(own, "owner")
        (root / "ui" / "main.js").write_text("fetch('https://evil.example.org/')\n", encoding="utf-8")
        sign(own, "owner")
        with mock.patch.object(fm, "host_call", side_effect=AssertionError("no server call")):
            rc = fm.main(["install", str(root), "--trust", str(trust)])
        self.assertEqual(rc, 1)

    def test_sign_own_uses_the_owner_key(self):
        root = self.make()
        key = Ed25519PrivateKey.generate()
        self.cfg["modules"]["trustedKeys"] = [{"name": "owner", "key": fr.public_hex(key)}]
        with mock.patch.object(fr, "keyring_key", return_value=key) as kk:
            self.assertEqual(fm.main(["sign", str(root), "--own"]), 0)
        kk.assert_called_once_with("owner", create=True)
        sig = json.loads((root / fr.MANIFEST_NAME).read_text(encoding="utf-8"))["signatures"]
        self.assertEqual([s["name"] for s in sig], ["owner"])
        self.assertEqual(fm.verify(root)["level"], "own")


if __name__ == "__main__":
    unittest.main()
