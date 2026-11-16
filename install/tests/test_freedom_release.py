# SPDX-License-Identifier: AGPL-3.0-or-later
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import freedom_release as fr  # noqa: E402
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402


class ReleaseTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name) / "rel"
        (self.root / "assets").mkdir(parents=True)
        (self.root / "index.html").write_text("<html>ok</html>", encoding="utf-8")
        (self.root / "assets" / "app.js").write_bytes(b"console.log(1)")
        self.k1, self.k2, self.k3 = (Ed25519PrivateKey.generate() for _ in range(3))
        self.trust = {"keys": {fr.public_hex(self.k1): "anna", fr.public_hex(self.k2): "boris"}, "threshold": 2}

    def tearDown(self):
        self.tmp.cleanup()

    def signed(self, *keys):
        m = fr.build(self.root, "0.1.0")
        for i, k in enumerate(keys):
            fr.sign(m, f"m{i}", k)
        return json.loads(json.dumps(m))  # as read back from a mirror

    def test_threshold_met(self):
        self.assertEqual(fr.verify(self.root, self.signed(self.k1, self.k2), self.trust), [])

    def test_threshold_not_met_and_untrusted_key_ignored(self):
        p = fr.verify(self.root, self.signed(self.k1, self.k3), self.trust)
        self.assertEqual(p, ["1 valid trusted signature(s), 2 needed"])

    def test_same_key_twice_counts_once(self):
        m = self.signed(self.k1)
        m["signatures"].append(dict(m["signatures"][0], name="again"))
        self.assertIn("1 valid trusted signature(s), 2 needed", fr.verify(self.root, m, self.trust))

    def test_changed_file_rejected(self):
        m = self.signed(self.k1, self.k2)
        (self.root / "assets" / "app.js").write_bytes(b"steal()")
        self.assertEqual(fr.verify(self.root, m, self.trust), ["changed assets/app.js"])

    def test_tampered_manifest_breaks_signatures(self):
        m = self.signed(self.k1, self.k2)
        (self.root / "assets" / "app.js").write_bytes(b"steal()")
        m["files"][0]["sha256"] = fr.sha256_file(self.root / "assets" / "app.js")
        p = fr.verify(self.root, m, self.trust)
        self.assertIn("bad signature of anna", p)
        self.assertIn("0 valid trusted signature(s), 2 needed", p)

    def test_partial_skips_missing_but_not_changed(self):
        m = self.signed(self.k1, self.k2)
        (self.root / "index.html").unlink()
        self.assertEqual(fr.verify(self.root, m, self.trust), ["missing index.html"])
        self.assertEqual(fr.verify(self.root, m, self.trust, partial=True), [])
        (self.root / "assets" / "app.js").write_bytes(b"steal()")
        self.assertEqual(fr.verify(self.root, m, self.trust, partial=True), ["changed assets/app.js"])
        (self.root / "assets" / "app.js").unlink()
        self.assertEqual(fr.verify(self.root, m, self.trust, partial=True), ["none of the listed files is here"])

    def test_extra_file_only_in_strict(self):
        m = self.signed(self.k1, self.k2)
        (self.root / "evil.js").write_text("x", encoding="utf-8")
        self.assertEqual(fr.verify(self.root, m, self.trust), [])
        self.assertEqual(fr.verify(self.root, m, self.trust, strict=True), ["not in manifest evil.js"])

    def test_unsafe_path_and_missing_file(self):
        m = fr.build(self.root, "0.1.0")
        m["files"].append({"path": "../outside.txt", "size": 1, "sha256": "0" * 64})
        m["files"].append({"path": "gone.txt", "size": 1, "sha256": "0" * 64})
        fr.sign(m, "a", self.k1); fr.sign(m, "b", self.k2)
        self.assertEqual(fr.verify(self.root, m, self.trust), ["unsafe path '../outside.txt'", "missing gone.txt"])

    def test_cli_build_and_verify_with_trust_file(self):
        trust = pathlib.Path(self.tmp.name) / "trust.json"
        trust.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "anna", "key": fr.public_hex(self.k1)}]}))
        self.assertEqual(fr.main(["build", str(self.root), "--version", "0.1.0"]), 0)
        mp = self.root / fr.MANIFEST_NAME
        m = json.loads(mp.read_text(encoding="utf-8"))
        fr.write_json(mp, fr.sign(m, "anna", self.k1))
        self.assertEqual(fr.main(["verify", str(self.root), "--trust", str(trust), "--strict"]), 0)
        self.assertEqual(fr.main(["verify", str(self.root)]), 2)  # shipped trust list is empty

    def test_revoked_key_not_trusted(self):
        t = pathlib.Path(self.tmp.name) / "t.json"
        t.write_text(json.dumps({"threshold": 1, "maintainers": [{"name": "a", "key": fr.public_hex(self.k1), "revoked": True}]}))
        self.assertEqual(fr.load_trust(t)["keys"], {})


if __name__ == "__main__":
    unittest.main()
