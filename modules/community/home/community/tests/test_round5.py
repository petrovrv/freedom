# SPDX-License-Identifier: AGPL-3.0-or-later
"""Codex round 5 (freedom-community-20261112-111510): strong keys judged by the whole rest of the line (block
scalars, indented values); storage errors abort the tick instead of being counted as invalid events."""
import json
import sqlite3
import time
import unittest
from unittest import mock

from community import agent, keys, nostr, outfilter
from community.tests.helpers import MemTransport, sample_card, workbench


class LineRuleTest(unittest.TestCase):
    def setUp(self):
        self.f = outfilter.generic_filter()

    def test_blocked(self):
        for t in ["password: my long passphrase",
                  "db:\n  password: |\n    violet meadow silver river\n",
                  "password: >-\n  violet meadow\n",
                  "password: |2+\n  x y z\n",
                  "auth:\n  password:\n    violet meadow silver river\n",
                  '"password" => "violet meadow"',
                  "password = 'a b c'",
                  "PASSWORD=violet meadow silver river",
                  '{"token": "<t>", "password": "real one"}',
                  "client_secret: two words here",
                  "github_token: abc",
                  "secrets: [aiwb/co-push]",                    # documented false positive: placeholders only
                  "Password: required for login",
                  "password: <your password>  # set me"]:   # a comment never makes a value clean            # prose after a strong key blocks too (by design)
            self.assertIn("secret", self.f.check(t).rules(), repr(t))

    def test_whole_line_placeholders_pass(self):
        for t in ["password: <your password>",
                  'token: "${TOKEN}",',
                  "api_key: none",
                  "password: ***",
                  "password:",                                  # empty value, nothing indented below
                  "password:\nuser: anna-free",                 # next line not indented: not a value
                  "tokens: 200000",                             # plural / other words are not strong keys
                  "tokenizer: wordpiece",
                  "bypass: the cache",
                  "compass: north"]:
            r = self.f.check(t)
            self.assertTrue(r.ok, f"{t!r}: {r.explain()}")

    def test_strong_key_names(self):
        for k in ("password", "DB_PASSWORD", "passphrase", "client_secret", "private_key", "privkey", "api-key",
                  "apikey", "access_key", "nsec", "credentials", "bearer", "token", "github_token", "token_url",
                  "pwd", "db.pass"):
            self.assertTrue(outfilter.strong_key_name(k), k)
        for k in ("tokens", "tokenizer", "bypass", "compass", "passage", "session", "user"):
            self.assertFalse(outfilter.strong_key_name(k), k)


class StorageErrorTest(unittest.TestCase):
    def setUp(self):
        self.t = MemTransport()
        self.a = workbench("sa", transport=self.t)
        self.b = workbench("sb", transport=self.t)
        self.a.approve(self.a.draft(nostr.KIND_CARD, sample_card())["id"])

    def test_archive_failure_aborts_fetch_and_keeps_cursor(self):
        before = (self.b.archive.cursor("mem://r1"), self.b.archive.cursor("resume:mem://r1"))
        with mock.patch.object(self.b.archive, "add_event", side_effect=sqlite3.OperationalError("database is locked")):
            with self.assertRaises(agent.StorageError):
                self.b.tick()
        self.assertEqual((self.b.archive.cursor("mem://r1"), self.b.archive.cursor("resume:mem://r1")), before)
        rep = self.b.tick()                                          # storage back: the event is read now
        self.assertEqual(rep["new"], 1)
        self.assertEqual(rep["skippedInvalid"], 0)

    def test_storage_failure_in_inbox_marks_nothing_processed(self):
        self.b.fetch()
        with mock.patch.object(self.b.archive, "guard_info", side_effect=sqlite3.OperationalError("disk I/O error")):
            with self.assertRaises(sqlite3.OperationalError):
                self.b.tick()
        self.assertEqual(len(self.b.archive.unprocessed()), 1)      # nothing was marked processed
        rep = self.b.tick()
        self.assertEqual(len(rep["inbox"]), 1)
        self.assertEqual(rep["invalidContent"], 0)

    def test_malformed_events_are_still_skipped_not_fatal(self):
        k = keys.generate()
        deep = nostr.sign_event(k.seckey, nostr.KIND_DISCUSSION, "[" * 50000 + "]" * 50000, [["t", "freedom"]])
        bad_target = nostr.sign_event(k.seckey, nostr.KIND_REVIEW, json.dumps({"target": ["x"], "verdict": "accept",
                                                                               "checks": "c"}), [["t", "freedom"]])
        self.t.events += [deep, bad_target]
        rep = self.b.tick()                                          # no exception: validation, not storage
        self.assertEqual(rep["invalidContent"], 2)
        self.assertEqual(self.b.tick()["inbox"], [])
        self.assertIsNotNone(self.b.archive.event(deep["id"]))      # stored (valid signature), never shown


if __name__ == "__main__":
    unittest.main()
