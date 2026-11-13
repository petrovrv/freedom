# SPDX-License-Identifier: AGPL-3.0-or-later
"""CLI end to end in a temporary data folder; the key goes to keyring service aiwb/nostr-test and is deleted.

The CLI runs against a temporary tool library (copies of read-guard and the toolbox scanner, a stub aiwb-dossier),
a temporary LOCALAPPDATA (read-guard state) and without the job variables or the guard event file, so nothing
reaches the real read-guard state or the owner's Waiting-for-you. When the tests themselves run under a claude/codex
agent, the CLI sees that ancestor and treats itself as "in a job": owner-only commands must then be held (exit 4).
"""
import json
import os
import shutil
import subprocess
import sys
import unittest
from pathlib import Path

from community import config, gate, guard, volunteer
from community.tests.helpers import INSTALL, sample_card, tmpdir

SERVICE = "aiwb/nostr-test"
DOSSIER_OK = "import json; print(json.dumps({'rows': []}))\n"
DOSSIER_BROKEN = "import sys; sys.exit(2)\n"


def stub_tools(root: Path, dossier_code: str) -> Path:
    real = config.tools_dir()
    (root / "toolbox").mkdir(parents=True)
    shutil.copy(real / "toolbox" / "security.py", root / "toolbox" / "security.py")
    (root / "read-guard").mkdir()
    shutil.copy(real / "read-guard" / "read_guard.py", root / "read-guard" / "read_guard.py")
    (root / "aiwb-dossier").mkdir()
    (root / "aiwb-dossier" / "aiwb-dossier.py").write_text(dossier_code, encoding="utf-8")
    return root


class CliTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import keyring
            keyring.get_password(SERVICE, "probe")
        except Exception as e:  # noqa: BLE001
            raise unittest.SkipTest(f"no keyring: {e}")
        if guard._RG is None:
            raise unittest.SkipTest("read-guard not installed")
        cls.dir = Path(tmpdir())
        (cls.dir / "install.json").write_text(json.dumps(INSTALL), encoding="utf-8")
        tools = stub_tools(cls.dir / "tools", DOSSIER_OK)
        base = {k: v for k, v in os.environ.items()
                if k not in ("AIWB_JOB_ID", "AIWB_CHAT_ID", "AIWB_GUARD_EVENTS", "CLAUDE_PROJECT_DIR")}
        cls.env = dict(base, AIWB_COMMUNITY_DIR=str(cls.dir / "data"), AIWB_INSTALL=str(cls.dir / "install.json"),
                       LOCALAPPDATA=str(cls.dir / "localappdata"), AIWB_TOOLS=str(tools), PYTHONIOENCODING="utf-8")
        cls.job_env = dict(cls.env, AIWB_JOB_ID="JOB-TEST", AIWB_CHAT_ID="CHAT-20260101000000-TEST")
        cls.under_agent = gate._ancestor_agent(guard._RG)     # the CLI subprocess inherits this ancestry
        cls.owner_code = 4 if cls.under_agent else 0
        cls.wid = None

    @classmethod
    def tearDownClass(cls):
        import keyring
        from keyring.errors import PasswordDeleteError
        if cls.wid:
            try:
                keyring.delete_password(SERVICE, cls.wid)
            except PasswordDeleteError:
                pass
            assert keyring.get_password(SERVICE, cls.wid) is None

    def run_cli(self, *args, stdin=None, code=0, env=None, stderr=False):
        p = subprocess.run([sys.executable, "-m", "community", "--key-service", SERVICE, *args],
                           input=stdin, capture_output=True, text=True, encoding="utf-8", env=env or self.env,
                           cwd=str(config.REPO), timeout=120)
        self.assertEqual(p.returncode, code, p.stderr + p.stdout)
        return p.stderr if stderr else p.stdout

    def test_flow(self):
        out = json.loads(self.run_cli("init"))
        type(self).wid = out["workbenchId"]
        self.assertTrue(out["npub"].startswith("npub1"))
        self.assertTrue(out["created"])
        self.assertFalse(json.loads(self.run_cli("init"))["created"])
        who = json.loads(self.run_cli("whoami"))
        self.assertEqual(who["pubkey"], out["pubkey"])

        self.assertIn("clean", self.run_cli("filter", "-", stdin="RRF over FTS and vectors; MRR 0.66 -> 0.87"))
        blocked = self.run_cli("filter", "-", "--json", stdin="ask Anna at anna@family-mail.be", code=3)
        self.assertEqual({h["rule"] for h in json.loads(blocked)["hits"]}, {"name", "known-email"})

        card = sample_card()
        self.assertTrue(json.loads(self.run_cli("card", "validate", "-", "--unsigned", stdin=json.dumps(card)))["ok"])
        d = json.loads(self.run_cli("card", "draft", "-", stdin=json.dumps(card)))
        self.assertEqual(d["status"], "pending")
        bad = dict(card, pitfalls="Kowalski hit this")
        self.run_cli("card", "draft", "-", stdin=json.dumps(bad), code=3)
        drafts = json.loads(self.run_cli("drafts"))
        self.assertEqual([x["status"] for x in drafts], ["pending", "blocked"])
        self.run_cli("reject", str(drafts[0]["id"]))

        # owner-only commands: run at a terminal, held under an agent
        self.run_cli("policy", "--auto-publish", "card", "report", code=self.owner_code)
        self.run_cli("policy", "--auto-publish", "pledge", code=1)          # never allowed, before any gate
        self.run_cli("settings", "--add-relay", "ws://127.0.0.1:9", code=self.owner_code)
        expected_policy = [] if self.under_agent else ["card", "report"]
        self.assertEqual(json.loads(self.run_cli("policy"))["autoPublish"], expected_policy)

        # inside an agent job they are always held (exit 4) until the owner's read-guard grant
        held = self.run_cli("policy", "--auto-publish", "card", env=self.job_env, code=4, stderr=True)
        self.assertIn("owner", held)
        self.assertRegex(held, r"\d{4} да")
        self.run_cli("settings", "--set", 'filterAllow=["Anna"]', env=self.job_env, code=4)
        disc = json.loads(self.run_cli("draft", "discussion", "-", stdin=json.dumps({"form": "comment", "text": "hi"})))
        self.run_cli("approve", str(disc["id"]), env=self.job_env, code=4)   # any manual approve in a job
        self.run_cli("decide", "0" * 64 + ":" + card["id"], "skip", env=self.job_env, code=4)
        self.run_cli("--no-dossier", "drafts", env=self.job_env, code=4)
        self.assertEqual(json.loads(self.run_cli("policy"))["autoPublish"], expected_policy)   # unchanged
        self.assertEqual(json.loads(self.run_cli("settings"))["filterAllow"], [])
        self.assertEqual(json.loads(self.run_cli("drafts", "--status", "pending"))[0]["id"], disc["id"])

        # a broken dossier never fails open
        broken = dict(self.env, AIWB_TOOLS=str(stub_tools(self.dir / "tools-broken", DOSSIER_BROKEN)))
        res = json.loads(self.run_cli("filter", "-", "--json", stdin="harmless text", env=broken, code=3))
        self.assertEqual([h["rule"] for h in res["hits"]], ["dossier-unavailable"])

        ident = json.loads(self.run_cli("identity"))
        self.assertTrue(ident["gistText"].startswith("Freedom workbench: npub1"))
        self.assertEqual(json.loads(self.run_cli("rep", "--now", "1800000000"))["scores"], {})
        self.assertEqual(json.loads(self.run_cli("project")), [])
        self.assertIn("нет", self.run_cli("digest"))

        vol = self.dir / "vol"
        self.run_cli("volunteer", "init", str(vol))
        # a volunteer job starts without secrets in its environment
        clean = {k: v for k, v in self.env.items() if not volunteer.secret_env_name(k)}
        self.assertTrue(json.loads(self.run_cli("volunteer", "check", str(vol), env=clean))["ok"])
        dirty = dict(clean, GITHUB_TOKEN="x")
        self.assertIn("GITHUB_TOKEN", self.run_cli("volunteer", "check", str(vol), env=dirty, code=3))
        (vol / "memory.md").write_text("x", encoding="utf-8")
        self.run_cli("volunteer", "check", str(vol), env=clean, code=3)


if __name__ == "__main__":
    unittest.main()
