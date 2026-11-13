# SPDX-License-Identifier: AGPL-3.0-or-later
"""Codex round 3 (freedom-community-20261112-105842) and the self-audit it asked for:
(a) file writes atomic and never clobbering on read errors, (b) credential formats in the outbound filter,
(c) the shared-project state machine as a transition table."""
import json
import os
import unittest
from pathlib import Path
from unittest import mock

from community import agent, archive, cards, config, keys, nostr, outfilter, projects, schemas, volunteer
from community.tests.helpers import sample_card, tmpdir, workbench

DAY = 86400
T0 = 1_780_000_000


# ---------------------------------------------------------------- P1 settings / (a) writes

class SettingsTest(unittest.TestCase):
    def setUp(self):
        self.d = Path(tmpdir())
        self.p = self.d / "community.json"

    def test_corrupt_file_is_never_replaced(self):
        s = config.load_settings(self.d)
        wid = s["workbenchId"]
        good = self.p.read_bytes()
        self.p.write_text('{"workbenchId": "' + wid + '", "relays": [', encoding="utf-8")   # truncated
        broken = self.p.read_bytes()
        with self.assertRaises(config.SettingsError):
            config.load_settings(self.d)
        self.assertEqual(self.p.read_bytes(), broken)                    # left as is, no new id written
        for bad in ("[]", "{}", '{"workbenchId": ""}', '{"workbenchId": 5}'):
            self.p.write_text(bad, encoding="utf-8")
            with self.assertRaises(config.SettingsError, msg=bad):
                config.load_settings(self.d)
            self.assertEqual(self.p.read_text(encoding="utf-8"), bad)
        self.p.write_bytes(good)
        self.assertEqual(config.load_settings(self.d)["workbenchId"], wid)

    def test_atomic_save_keeps_backup(self):
        s = config.load_settings(self.d)
        s["relays"] = ["wss://r.example.org"]
        config.save_settings(s, self.d)
        bak = json.loads((self.d / "community.json.bak").read_text(encoding="utf-8"))
        self.assertEqual(bak["relays"], [])
        self.assertEqual(json.loads(self.p.read_text(encoding="utf-8"))["relays"], ["wss://r.example.org"])
        self.assertEqual([f.name for f in self.d.iterdir() if f.name.endswith(".tmp")], [])
        with self.assertRaises(config.SettingsError):
            config.save_settings({"relays": []}, self.d)

    def test_crash_during_write_leaves_old_file(self):
        config.load_settings(self.d)
        before = self.p.read_bytes()
        with mock.patch("community.config.os.replace", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                config.save_settings({"workbenchId": "wb-x", "relays": ["x"]}, self.d)
        self.assertEqual(self.p.read_bytes(), before)

    def test_volunteer_profile_not_overwritten(self):
        folder = self.d / "vol"
        volunteer.init_profile(folder)
        with self.assertRaises(FileExistsError):
            volunteer.init_profile(folder, share=5)
        volunteer.init_profile(folder, share=5, overwrite=True)
        self.assertTrue((folder / "volunteer.json.bak").exists())

    def test_archive_add_event_is_one_transaction(self):
        k = keys.generate()
        a = archive.Archive(os.path.join(tmpdir(), "a.sqlite"))
        c = cards.sign_card(sample_card(), k, "wb")
        v1 = cards.to_event(c, k, created_at=T0)
        a.add_event(v1)
        v2 = cards.to_event(cards.sign_card(dict(c, result="new"), k, "wb"), k, created_at=T0 + 10)
        with mock.patch("community.archive.cards.from_event", side_effect=RuntimeError("boom")):
            with self.assertRaises(RuntimeError):
                a.add_event(v2)
        self.assertEqual([e["id"] for e in a.events(kinds=[nostr.KIND_CARD])], [v1["id"]])   # old version intact
        a.add_event(v2)
        self.assertEqual([e["id"] for e in a.events(kinds=[nostr.KIND_CARD])], [v2["id"]])

    def test_corrupt_key_is_not_replaced(self):
        store = keys.MemoryKeyStore()
        store.set("wb-x", "garbage")
        with self.assertRaises(ValueError):
            keys.load_or_create(store, "wb-x")
        self.assertEqual(store.get("wb-x"), "garbage")

    def test_corrupt_archive_fails_loudly(self):
        p = Path(tmpdir()) / "bad.sqlite"
        p.write_bytes(b"this is not a database" * 100)
        with self.assertRaises(Exception):
            archive.Archive(p)
        self.assertEqual(p.read_bytes(), b"this is not a database" * 100)


# ---------------------------------------------------------------- P1 / (b) credential formats

SECRET_TEXTS = [
    '{"password":"Zk3pQ9rT7vW2xY5a"}',
    '{"password": "hunter22"}',
    "{'client_secret': 'abcDEF123456'}",
    "password: s3cr3t-Value",
    "db:\n  password: correcthorse",
    "DB_PASSWORD=correcthorse",
    "export GITHUB_TOKEN=Ab12Cd34Ef56Gh78",
    "api_key='Ab12Cd34Ef56'",
    "https://api.example.org/v1?token=Ab12Cd34Ef56Gh78&x=1",
    "Authorization: Bearer abc123def456ghi789",
    "authorization: Basic dXNlcjpwYXNzd29yZA==",
    "Cookie: session=abcdef123456; theme=dark",
    "postgres://app:p4ssw0rd@10.0.0.1/db",
    "https://admin:Sup3rSecret@db.example.org/x",
    "-----BEGIN RSA " + "PRIVATE KEY-----\nMIIE",
    "eyJhbGciOiJIUzI1NiJ9" + ".eyJzdWIiOiIxMjM0NTY3ODkwIn0" + ".SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV",
    "nsec1" + "q" * 58,
    "my key is Q2hlY2tUaGlzVG9rZW5JdElzTG9uZ0VOb3VnaDEyMw== ok",
    '{"refresh_token": "1//0abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN"}',
]
CLEAN_TEXTS = [
    "token budget: 3 % of the week",
    "password: <your password>",
    "password: ${DB_PASSWORD}",
    '"token": "<token>"',
    "api_key: none",
    "Authorization: Bearer <token>",
    "auth: OAuth device flow",
    "session: daily",
    "keyring service: aiwb/nostr",
    "passwords live only in the OS keyring",
    "event " + "ab" * 32 + " and pubkey " + "cd" * 32,
    "uuid 6f1c2a52-6b0e-4d5e-9a57-0f6d8c1f3b10, sha 8953fbe",
    "https://github.com/org/repo and https://user@example.org/path",
    "2 400 000 tokens, tokens per day, secret sharing scheme",
    "potion-multilingual-128M static model, 256 dims",
]


class CredentialTest(unittest.TestCase):
    def setUp(self):
        self.f = outfilter.generic_filter()

    def test_credentials_blocked(self):
        for t in SECRET_TEXTS:
            self.assertIn("secret", self.f.check(t).rules(), t)

    def test_no_false_positives(self):
        for t in CLEAN_TEXTS:
            r = self.f.check(t)
            self.assertTrue(r.ok, f"{t!r}: {r.explain()}")

    def test_help_request_log_with_json_credential_is_blocked_even_on_auto_publish(self):
        wb = workbench("hc", settings={"autoPublish": ["help"]})
        r = wb.draft(nostr.KIND_HELP, {"topic": "install", "summary": "setup fails", "want": "analysis",
                                       "log": 'config {"password":"Zk3pQ9rT7vW2xY5a"} rejected'})
        self.assertEqual(r["status"], "blocked")
        self.assertEqual(wb.transport.events, [])


# ---------------------------------------------------------------- P2 / (c) project state machine as a table

class ProjectTable(unittest.TestCase):
    """Each row: steps, the time `now`, and the expected (status of piece p1, holder, number of missed claims)."""

    def setUp(self):
        self.k = {n: keys.generate() for n in "ABCD"}
        self.uuid = schemas.new_uuid()

    def run_steps(self, steps, now, ident=None):
        evs = []
        prop = nostr.sign_event(self.k["A"].seckey, nostr.KIND_PROPOSAL, json.dumps({
            "project": self.uuid, "title": "T", "goal": "g", "deadline": T0 + 30 * DAY,
            "plan": [{"piece": "p1", "title": "x", "tokens": 1, "days": 1}], "budget": {"tokens": 1, "days": 1}}),
            [["t", "freedom"]], T0)
        evs.append(prop)
        last = {}

        def add(who, kind, c, t, parent=None):
            e = nostr.sign_event(self.k[who].seckey, kind, json.dumps(c), nostr.reply_tags(prop, parent), T0 + t)
            evs.append(e)
            return e
        for st in steps:
            op, who, t = st[0], st[1], st[2]
            if op == "pledge":
                add(who, nostr.KIND_PLEDGE, {"project": self.uuid, "sharePercent": 1, "ownerConfirmed": True}, t)
            elif op == "claim":
                last[who + "claim"] = add(who, nostr.KIND_CLAIM, {"project": self.uuid, "piece": "p1", "due": T0 + st[3]}, t)
            elif op == "deliver":
                last[who + "dev"] = add(who, nostr.KIND_DELIVERY, {
                    "project": self.uuid, "piece": "p1", "claim": last[who + "claim"]["id"], "summary": "s",
                    "artifact": {"type": "git", "ref": "https://github.com/o/r"}}, t)
            elif op == "review":
                target = last[st[4] + "dev"]
                add(who, nostr.KIND_REVIEW, {"target": target["id"], "verdict": st[3], "checks": "c"}, t, target)
        st = projects.project_state(evs, prop["id"], T0 + now, ident)
        p = st["pieces"][0]
        holder = next((n for n, kk in self.k.items() if kk.pubkey == p["holder"]), None)
        return p["status"], holder, len(st["missed"]), st

    D = 10 * DAY   # due offset used by most rows

    def test_transitions(self):
        D = self.D
        rows = [
            ("nothing", [], 100, ("open", None, 0)),
            ("claimed", [("pledge", "B", 1), ("claim", "B", 2, D)], 100, ("claimed", "B", 0)),
            ("claim without pledge", [("claim", "B", 2, D)], 100, ("open", None, 0)),
            ("missed: no delivery by due", [("pledge", "B", 1), ("claim", "B", 2, D)], D + 1, ("open", None, 1)),
            ("delivered in time, review pending, after due",
             [("pledge", "B", 1), ("claim", "B", 2, D), ("deliver", "B", 50)], D + 1, ("delivered", "B", 0)),
            ("rejected after due",
             [("pledge", "B", 1), ("claim", "B", 2, D), ("deliver", "B", 50), ("review", "A", D + 5, "reject", "B")],
             D + 10, ("open", None, 1)),
            ("rejected before due: may re-deliver",
             [("pledge", "B", 1), ("claim", "B", 2, D), ("deliver", "B", 50), ("review", "A", 60, "reject", "B")],
             100, ("delivered", "B", 0)),
            ("accepted",
             [("pledge", "B", 1), ("claim", "B", 2, D), ("deliver", "B", 50), ("review", "A", 60, "accept", "B")],
             100, ("accepted", "B", 0)),
            ("P2: pending delivery keeps the piece against a later claim",
             [("pledge", "B", 1), ("pledge", "C", 1), ("claim", "B", 2, D), ("deliver", "B", 50),
              ("claim", "C", D + 100, D + 5 * DAY)], D + 200, ("delivered", "B", 0)),
            ("P2: pending, competing claim, then accepted",
             [("pledge", "B", 1), ("pledge", "C", 1), ("claim", "B", 2, D), ("deliver", "B", 50),
              ("claim", "C", D + 100, D + 5 * DAY), ("review", "A", D + 300, "accept", "B")], D + 400,
             ("accepted", "B", 0)),
            ("rejected, then a competing claim takes it",
             [("pledge", "B", 1), ("pledge", "C", 1), ("claim", "B", 2, D), ("deliver", "B", 50),
              ("review", "A", D + 50, "reject", "B"), ("claim", "C", D + 100, D + 5 * DAY)], D + 200,
             ("claimed", "C", 1)),
            ("late delivery does not count",
             [("pledge", "B", 1), ("claim", "B", 2, D), ("deliver", "B", D + 10), ("review", "A", D + 20, "accept", "B")],
             D + 30, ("open", None, 1)),
            ("self review does not count",
             [("pledge", "B", 1), ("claim", "B", 2, D), ("deliver", "B", 50), ("review", "B", 60, "accept", "B")],
             100, ("delivered", "B", 0)),
            ("missed, re-claimed by another, accepted",
             [("pledge", "B", 1), ("pledge", "C", 1), ("claim", "B", 2, D), ("claim", "C", D + 10, D + 5 * DAY),
              ("deliver", "C", D + 20), ("review", "A", D + 30, "accept", "C")], D + 40, ("accepted", "C", 1)),
            ("competing claim while the first is active is refused",
             [("pledge", "B", 1), ("pledge", "C", 1), ("claim", "B", 2, D), ("claim", "C", 3, D)], 100,
             ("claimed", "B", 0)),
            ("latest verdict per reviewer counts",
             [("pledge", "B", 1), ("claim", "B", 2, D), ("deliver", "B", 50), ("review", "A", 60, "reject", "B"),
              ("review", "A", 70, "accept", "B")], 100, ("accepted", "B", 0)),
        ]
        for name, steps, now, expected in rows:
            with self.subTest(name):
                got = self.run_steps(steps, now)[:3]
                self.assertEqual(got, expected, name)

    def test_same_identity_review_does_not_count(self):
        ident = {self.k["B"].pubkey: "bob", self.k["D"].pubkey: "bob"}
        steps = [("pledge", "B", 1), ("claim", "B", 2, self.D), ("deliver", "B", 50), ("review", "D", 60, "accept", "B")]
        self.assertEqual(self.run_steps(steps, 100, ident)[:3], ("delivered", "B", 0))
        self.assertEqual(self.run_steps(steps, 100)[:3], ("accepted", "B", 0))

    def test_deadline_expires_project_and_pledge(self):
        status, holder, missed, st = self.run_steps([("pledge", "C", 1)], 31 * DAY)
        self.assertEqual(st["status"], "expired")
        self.assertEqual(st["pledges"][0]["result"], "missed")

    def test_invariants_over_all_rows(self):
        """No piece has two holders, missed claims are never the current holder, accepted pieces have a reviewer
        different from the holder."""
        D = self.D
        steps = [("pledge", "B", 1), ("pledge", "C", 1), ("claim", "B", 2, D), ("deliver", "B", 50),
                 ("claim", "C", D + 100, D + 5 * DAY), ("review", "A", D + 300, "accept", "B")]
        for now in (10, 60, D - 1, D + 1, D + 150, D + 350, 31 * DAY):
            _, _, _, st = self.run_steps(steps, now)
            p = st["pieces"][0]
            valid = [c for c in st["claims"] if c["valid"]]
            self.assertLessEqual(len({c["pubkey"] for c in valid if c["pubkey"] == p["holder"]}), 1)
            self.assertNotIn(p["holder"], [m["pubkey"] for m in st["missed"]] if p["holder"] else [])
            if p["status"] == "accepted":
                self.assertNotEqual(p["reviewer"], p["holder"])


if __name__ == "__main__":
    unittest.main()
