"""Tests of aiwb_secret 0.4.0: the owner's jobs work as before, a household member's job is refused (own_device_first).
Never touches the real keyring: an in-memory backend, and the refusing backend in a child process.

    python test_aiwb_secret.py
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import keyring  # noqa: E402
from keyring.backend import KeyringBackend  # noqa: E402

import aiwb_secret  # noqa: E402


class MemoryKeyring(KeyringBackend):
    priority = 1

    def __init__(self):
        super().__init__()
        self.d = {}

    def get_password(self, service, username):
        return self.d.get((service, username))

    def set_password(self, service, username, password):
        self.d[(service, username)] = password

    def delete_password(self, service, username):
        self.d.pop((service, username), None)


CLEAN = {k: v for k, v in os.environ.items() if not k.startswith(("AIWB_", "PYTHON_KEYRING"))}


class Role(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        (root / "install.json").write_text(json.dumps({"members": [{"env": "anna", "role": "owner"}, {"env": "kid"}]}),
                                           encoding="utf-8")
        for env in ("anna", "kid"):
            chat = root / "projects" / f"P-{env}" / "chats" / f"CHAT-20261113000000-{env.upper()[:3]}X"
            chat.mkdir(parents=True)
            (root / "projects" / f"P-{env}" / "project.json").write_text(json.dumps({"environmentId": env}), encoding="utf-8")
        self.root = root
        self.env = dict(CLEAN, AIWB_INSTALL=str(root / "install.json"), AIWB_PROJECTS_DIR=str(root / "projects"))
        self.cwd = os.getcwd()
        self.kr = MemoryKeyring()
        keyring.set_keyring(self.kr)

    def tearDown(self):
        os.chdir(self.cwd)
        self.tmp.cleanup()

    def run_in(self, env_extra, cwd=None):
        os.chdir(cwd or self.root)
        with mock.patch.dict(os.environ, dict(self.env, **env_extra), clear=True):
            return aiwb_secret.job_role()

    def test_roles(self):
        self.assertEqual(self.run_in({}), "owner")                                   # no job: owner's own script
        self.assertEqual(self.run_in({"AIWB_JOB_ROLE": "owner"}), "owner")
        self.assertEqual(self.run_in({"AIWB_JOB_ROLE": "member"}), "member")
        self.assertEqual(self.run_in({"AIWB_JOB_ENV": "kid"}), "member")
        self.assertEqual(self.run_in({"AIWB_JOB_ENV": "anna"}), "owner")
        job = {"AIWB_JOB_ID": "JOB-1"}
        self.assertEqual(self.run_in(job, self.root / "projects" / "P-kid" / "chats"), "member")   # project.json
        self.assertEqual(self.run_in(job, self.root / "projects" / "P-anna" / "chats"), "owner")
        self.assertEqual(self.run_in({}, self.root / "projects" / "P-kid" / "chats"), "owner")     # not a job: no guess
        self.assertEqual(self.run_in({"AIWB_CHAT_ID": "CHAT-20261113000000-KIDX"}), "member")      # chat lookup

    def test_owner_reads_and_writes(self):
        os.chdir(self.root)
        with mock.patch.dict(os.environ, dict(self.env, AIWB_JOB_ROLE="owner"), clear=True):
            long = "x" * 2500
            aiwb_secret.set_secret("aiwb/t", "a", long)
            self.assertEqual(aiwb_secret.get_secret("aiwb/t", "a"), long)
            aiwb_secret.set_secret("aiwb/t", "b", "short")
            self.assertEqual(aiwb_secret.get_secret("aiwb/t", "b"), "short")

    def test_member_refused(self):
        self.kr.set_password("aiwb/t", "a", "owner-secret")
        os.chdir(self.root)
        with mock.patch.dict(os.environ, dict(self.env, AIWB_JOB_ROLE="member", AIWB_JOB_ENV="kid"), clear=True):
            with self.assertRaises(aiwb_secret.MemberRefused) as cm:
                aiwb_secret.get_secret("aiwb/t", "a")
            self.assertIn("own_device_first", str(cm.exception))
            self.assertIn("secret_prompt", str(cm.exception))
            with self.assertRaises(aiwb_secret.MemberRefused):
                aiwb_secret.set_secret("aiwb/t", "a", "kid-value")
        self.assertEqual(self.kr.get_password("aiwb/t", "a"), "owner-secret")

    def test_secret_prompt_refuses_member(self):
        env = dict(self.env, AIWB_JOB_ROLE="member", AIWB_JOB_ENV="kid")
        r = subprocess.run([sys.executable, str(HERE / "secret-prompt.py"), "--service", "aiwb/none", "--check"],
                           env=env, capture_output=True, text=True, timeout=60)
        self.assertEqual(r.returncode, 5, r.stderr)
        self.assertIn("own_device_first", r.stderr)

    def test_backend_refuses_direct_keyring(self):
        """What Bridge does for a member's job: every keyring call of any tool is refused."""
        env = dict(self.env, AIWB_JOB_ROLE="member", AIWB_JOB_ENV="kid",
                   PYTHON_KEYRING_BACKEND="aiwb_secret.MemberRefusedKeyring", PYTHONPATH=str(HERE))
        code = ("import keyring\n"
                "for f in (lambda: keyring.get_password('aiwb/gmail','x'), lambda: keyring.set_password('aiwb/x','d','v'),\n"
                "          lambda: keyring.delete_password('aiwb/x','d'), lambda: keyring.get_credential('aiwb/x', None)):\n"
                "    try:\n        f(); print('LEAK')\n"
                "    except keyring.errors.KeyringError as e:\n        print('refused' if 'own_device_first' in str(e) else 'other')\n")
        r = subprocess.run([sys.executable, "-c", code], env=env, capture_output=True, text=True, timeout=60, cwd=self.tmp.name)
        self.assertEqual(r.stdout.split(), ["refused"] * 4, r.stdout + r.stderr)

    def test_owner_default_keyring_untouched(self):
        """Importing aiwb_secret must not change the owner's default backend (no chainer with the refusing one)."""
        code = ("import keyring, aiwb_secret\n"
                "k = keyring.get_keyring()\n"
                "names = [type(b).__name__ for b in getattr(k, 'backends', [k])]\n"
                "print('BAD' if 'MemberRefusedKeyring' in names else 'OK', type(k).__name__)\n")
        env = dict(CLEAN, PYTHONPATH=str(HERE), AIWB_JOB_ROLE="owner")
        r = subprocess.run([sys.executable, "-c", code], env=env, capture_output=True, text=True, timeout=60, cwd=self.tmp.name)
        self.assertTrue(r.stdout.startswith("OK"), r.stdout + r.stderr)

    def test_member_site_without_backend_variable(self):
        """Codex drops *KEY* variables from its shell: member_site/sitecustomize.py selects the backend again."""
        env = dict(self.env, AIWB_JOB_ROLE="member", PYTHONPATH=os.pathsep.join([str(HERE), str(HERE / "member_site")]))
        env.pop("PYTHON_KEYRING_BACKEND", None)
        code = ("import keyring\ntry:\n    keyring.get_password('aiwb/x','y'); print('LEAK')\n"
                "except keyring.errors.KeyringError as e:\n    print('refused' if 'own_device_first' in str(e) else 'other')\n")
        r = subprocess.run([sys.executable, "-c", code], env=env, capture_output=True, text=True, timeout=60, cwd=self.tmp.name)
        self.assertEqual(r.stdout.strip(), "refused", r.stdout + r.stderr)
        env["AIWB_JOB_ROLE"] = "owner"
        code2 = "import os; print(os.environ.get('PYTHON_KEYRING_BACKEND'))"
        r = subprocess.run([sys.executable, "-c", code2], env=env, capture_output=True, text=True, timeout=60, cwd=self.tmp.name)
        self.assertEqual(r.stdout.strip(), "None", r.stdout + r.stderr)

if __name__ == "__main__":
    unittest.main(verbosity=2)
