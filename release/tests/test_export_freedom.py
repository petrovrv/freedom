# SPDX-License-Identifier: AGPL-3.0-or-later
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import export_freedom as ef  # noqa: E402


class ScanTest(unittest.TestCase):
    def setUp(self):
        self.cfg = ef.load_config()

    def findings(self, files: dict[str, str], terms=()):
        return ef.scan({p: t.encode() for p, t in files.items()}, self.cfg, list(terms))

    def test_config_is_valid_and_has_rules(self):
        names = {r["name"] for r in self.cfg["patterns"]}
        self.assertIn("IBAN", names)
        self.assertTrue(all(ef.re.compile(r["regex"]) for r in self.cfg["patterns"]))

    def test_secrets_and_paths_found(self):
        f = self.findings({
            "a/x.py": "k = 'sk-ant-oat01-ABCDEFGHIJKLMNOPQRSTUVWX'\np = r'C:\\Users\\Jan\\.ssh'\n",
            "a/y.php": "require '/home/u1234567/co-agent/db.php';\n",
            "a/z.md": "pay to BE68 5390 0754 7034\n-----BEGIN OPENSSH PRIVATE " "KEY-----\n",
        })
        rules = sorted({x["rule"] for x in f})
        self.assertEqual(rules, ["Anthropic/OpenAI key", "IBAN", "Windows user profile path", "hosting account path",
                                 "private key"])

    def test_neutral_text_is_clean(self):
        f = self.findings({
            "a/x.py": "home = r'C:\\Users\\me\\x'; prof = '%USERPROFILE%'; mail = 'anna@example.org'\n",
            "a/y.ts": "const s = d?.corp?.state ?? d?.engie; // legacy wire name\n",
            "a/z.md": "Drive mounts at G:\\My Drive by default.\n",
        })
        self.assertEqual(f, [])

    def test_test_fixtures_are_allowed_for_some_rules_only(self):
        f = self.findings({"x/tests/t.py": "IBAN BE68 5390 0754 7034 C:\\Users\\Jan\nENGIE brand\n"})
        self.assertEqual([x["rule"] for x in f], ["corporate brand outside the connector (lowercase engie* stays as a legacy wire name)"])

    def test_emails_outside_example_domains(self):
        f = self.findings({"a/x.cs": "to = \"someone@realmail.be\"; ok = \"bob@corp.example\";\n"})
        self.assertEqual([x["text"] for x in f], ["someone@realmail.be"])

    def test_private_terms_hidden_in_report(self):
        with tempfile.TemporaryDirectory() as d:
            p = pathlib.Path(d) / "terms.txt"
            p.write_text("# comment\nZorglub\nre:secret-host-\\d+\n", encoding="utf-8")
            terms = ef.load_terms(p)
        f = self.findings({"a/x.py": "by Zorglub on secret-host-42\nZorglubs are fine as a longer word\n"}, terms)
        self.assertEqual(len(f), 2)
        self.assertTrue(all(x["text"] == "(hidden: private term)" for x in f))
        self.assertEqual({x["line"] for x in f}, {1})

    def test_match_any(self):
        self.assertTrue(ef.match_any("release/x.py", ["release/"]))
        self.assertTrue(ef.match_any("docs/TZ-CO-WEB.md", ["docs/TZ-*.md"]))
        self.assertTrue(ef.match_any("co-desktop/tests/A/b.cs", ["*/tests/*"]))
        self.assertFalse(ef.match_any("co-desktop/src/b.cs", ["*/tests/*"]))


@unittest.skipUnless(ef.owner_config_path(), "export needs the owner's part of the config (a maintainer tree)")
class ExportTest(unittest.TestCase):
    def test_rewrites_match_current_sources(self):
        # every export-only rewrite must still find its text, otherwise the config is stale
        tree, dropped = ef.build_tree(ef.load_config(), "HEAD", None)
        # the checks ship (guard runs them in the public repository); the owner's part and the public/ sources do not
        self.assertIn("release/export_freedom.py", tree)
        self.assertIn("release/freedom_guard.py", tree)
        self.assertIn("release/hooks/pre-push", tree)
        self.assertFalse([p for p in tree if p.startswith(("release/owner/", "release/public/"))])
        self.assertNotIn("release/freedom-compose.json", tree)
        self.assertIn("README.md", tree)
        self.assertIn(b"Freedom", tree["README.md"])
        for rule in ef.load_config()["rewrite"]:
            for path in tree:
                if ef.match_any(path, rule["files"]):
                    self.assertIsNone(ef.re.search(rule["regex"], tree[path].decode("utf-8")), path)

    def test_config_json_roundtrip(self):
        json.dumps(ef.load_config())

    def test_core_names_no_module(self):
        tree = {"modules/geo/module.json": b'{"id": "geo-presence"}', "modules/geo/ui/x.js": b"'geo-presence'",
                "co-web/src/a.ts": b"if (m === 'geo-presence') x\nconst p = \"modules/geo-presence/ui\"\nplain geo-presence word",
                "co-web/src/a.selftest.ts": b"'geo-presence'", "README.md": b"'geo-presence'",
                "tools/geo-presence/SKILL.md": b"'geo-presence'", "install/tests/test_x.py": b"'geo-presence'"}
        f = ef.core_module_findings(tree, {})
        self.assertEqual([(x["file"], x["line"]) for x in f], [("co-web/src/a.ts", 1), ("co-web/src/a.ts", 2)])
        self.assertEqual(ef.core_module_findings(tree, {"moduleIdAllow": {"geo-presence": ["co-web/src/a.ts"]}}), [])
        self.assertEqual(ef.core_module_findings({"co-web/src/a.ts": b"'geo-presence'"}, {}), [])   # no such module

    def test_current_core_names_no_module(self):
        tree, _ = ef.build_tree(ef.load_config(), "HEAD", None)
        self.assertEqual(ef.core_module_findings(tree, ef.load_config()), [])


class ModuleSignatureTest(unittest.TestCase):
    def tree(self, data=b"x = 1"):
        import hashlib
        man = {"files": [{"path": "ui/a.js", "sha256": hashlib.sha256(b"x = 1").hexdigest()}]}
        t = {"modules/m/freedom-manifest.json": json.dumps(man).encode(), "modules/m/module.json": b"{}"}
        if data is not None:
            t["modules/m/ui/a.js"] = data
        return t

    def test_unchanged_module_is_clean(self):
        self.assertEqual(ef.module_signature_findings(self.tree()), [])

    def test_rewritten_or_dropped_file_is_a_finding(self):
        self.assertIn("changed", ef.module_signature_findings(self.tree(b"x = 2"))[0]["rule"])
        self.assertIn("missing", ef.module_signature_findings(self.tree(None))[0]["rule"])

    def test_no_rewrite_rule_touches_a_signed_module(self):
        for rule in ef.load_config().get("rewrite", []):
            for g in rule["files"]:
                self.assertFalse(g.startswith("modules/"), g)


if __name__ == "__main__":
    unittest.main()
