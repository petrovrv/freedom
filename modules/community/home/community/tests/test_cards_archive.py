# SPDX-License-Identifier: AGPL-3.0-or-later
import json
import os
import unittest

from community import archive, cards, keys, nostr, schemas
from community.tests.helpers import sample_card, tmpdir


class CardTest(unittest.TestCase):
    def setUp(self):
        self.k = keys.generate()

    def test_fields_exactly_as_spec(self):
        self.assertEqual(cards.FIELDS, ["id", "title", "problem", "approach", "interface", "pitfalls", "result", "cost",
                                        "code", "author"])

    def test_sign_validate(self):
        c = cards.sign_card(sample_card(), self.k, "wb-a")
        self.assertEqual(cards.validate(c), [])
        self.assertEqual(set(c["author"]), {"workbench", "pubkey", "sig"})
        bad = dict(c, result="MRR 0.1")
        self.assertIn("author.sig: signature does not verify", cards.validate(bad))
        bad = json.loads(json.dumps(c))
        bad["author"]["workbench"] = "wb-other"
        self.assertTrue(cards.validate(bad))

    def test_validation_errors(self):
        c = sample_card()
        c["extra"] = 1
        c["title"] = ""
        c["code"] = "http://insecure.example.org/x"
        c["id"] = "not-a-uuid"
        errs = cards.validate(c, require_signature=False)
        self.assertTrue(any("unknown field extra" in e for e in errs))
        self.assertTrue(any(e.startswith("title") for e in errs))
        self.assertTrue(any(e.startswith("code") for e in errs))
        self.assertTrue(any(e.startswith("id") for e in errs))
        self.assertIn("author: {workbench, pubkey, sig} expected", cards.validate(sample_card()))
        long = sample_card(title="x" * 121)
        self.assertTrue(cards.validate(long, require_signature=False))

    def test_event_roundtrip(self):
        c = cards.sign_card(sample_card(code="https://github.com/org/repo"), self.k, "wb-a")
        ev = cards.to_event(c, self.k)
        self.assertEqual(ev["kind"], nostr.KIND_CARD)
        self.assertEqual(nostr.d_tag(ev), c["id"])
        back, errs = cards.from_event(ev)
        self.assertEqual(errs, [])
        self.assertEqual(back, c)
        other = keys.generate()
        with self.assertRaises(ValueError):
            cards.to_event(c, other)
        # a card signed by A wrapped in an event of B is rejected
        forged = nostr.sign_event(other.seckey, nostr.KIND_CARD, ev["content"], ev["tags"])
        self.assertIsNone(cards.from_event(forged)[0])


class SchemaTest(unittest.TestCase):
    def test_kinds(self):
        u = schemas.new_uuid()
        ok = {
            nostr.KIND_REPORT: {"card": u, "outcome": "worked", "measure": "recall 0.9"},
            nostr.KIND_HELP: {"topic": "install", "summary": "step google fails", "want": "analysis"},
            nostr.KIND_DISCUSSION: {"form": "pitfall", "text": "watch the alias noise"},
            nostr.KIND_PROPOSAL: {"project": u, "title": "t", "goal": "g", "deadline": 10,
                                  "plan": [{"piece": "p1", "title": "x", "tokens": 1, "days": 1}],
                                  "budget": {"tokens": 1, "days": 1}},
            nostr.KIND_PLEDGE: {"project": u, "sharePercent": 2, "ownerConfirmed": True},
            nostr.KIND_CLAIM: {"project": u, "piece": "p1", "due": 5},
            nostr.KIND_DELIVERY: {"project": u, "piece": "p1", "claim": "a" * 64, "summary": "s",
                                  "artifact": {"type": "git", "ref": "https://github.com/o/r", "sha": "abc1234"}},
            nostr.KIND_REVIEW: {"target": "a" * 64, "verdict": "accept", "checks": "ran tests"},
            nostr.KIND_FLAG: {"target": "a" * 64, "rules": ["email"]},
        }
        for k, c in ok.items():
            self.assertEqual(schemas.validate(k, c), [], nostr.KIND_NAMES[k])

    def test_rejections(self):
        u = schemas.new_uuid()
        self.assertTrue(schemas.validate(nostr.KIND_HELP, {"topic": "install", "summary": "s", "want": "do-it"}))
        self.assertTrue(schemas.validate(nostr.KIND_PLEDGE, {"project": u, "sharePercent": 2, "ownerConfirmed": False}))
        self.assertTrue(schemas.validate(nostr.KIND_DELIVERY, {"project": u, "piece": "p", "claim": "a" * 64, "summary": "s",
                                                               "artifact": {"type": "git", "ref": "file:///tmp/x"}}))
        self.assertTrue(schemas.validate(nostr.KIND_DISCUSSION, {"form": "x", "text": "t"}))
        self.assertTrue(schemas.validate(nostr.KIND_DISCUSSION, {"form": "comment", "text": "t", "secret": 1}))
        self.assertTrue(schemas.validate(nostr.KIND_PROPOSAL, {"project": u, "title": "t", "goal": "g", "deadline": 1,
                                                               "budget": {"tokens": 1, "days": 1},
                                                               "plan": [{"piece": "A B", "title": "x"}]}))
        self.assertEqual(schemas.validate(99999, {}), ["unknown kind 99999"])


class ArchiveTest(unittest.TestCase):
    def setUp(self):
        self.k = keys.generate()
        self.a = archive.Archive(os.path.join(tmpdir(), "a.sqlite"))

    def test_events_cards_replace(self):
        c = cards.sign_card(sample_card(), self.k, "wb-a")
        ev1 = cards.to_event(c, self.k, created_at=100)
        self.assertTrue(self.a.add_event(ev1, own=True))
        self.assertFalse(self.a.add_event(ev1))
        c2 = cards.sign_card(dict(c, result="MRR 0.66 -> 0.90"), self.k, "wb-a")
        ev2 = cards.to_event(c2, self.k, created_at=200)
        self.assertTrue(self.a.add_event(ev2))
        self.assertFalse(self.a.add_event(ev1))           # older version does not come back
        self.assertEqual(len(self.a.events(kinds=[nostr.KIND_CARD])), 1)
        self.assertEqual(self.a.cards()[0]["result"], "MRR 0.66 -> 0.90")
        bad = dict(ev2, content="{}")
        self.assertFalse(self.a.add_event(bad))

    def test_drafts_cursors_ledger_decisions(self):
        did = self.a.add_draft(nostr.KIND_DISCUSSION, {"form": "comment", "text": "x"}, [["t", "freedom"]], "pending",
                               {"ok": True, "hits": []})
        self.assertEqual(self.a.draft(did)["status"], "pending")
        self.a.update_draft(did, status="published", relays={"r": [True, ""]})
        self.assertEqual(self.a.draft(did)["relays"], {"r": [True, ""]})
        self.assertEqual(len(self.a.drafts("published")), 1)
        self.a.set_cursor("r", 5)
        self.assertEqual(self.a.cursor("r"), 5)
        self.a.spend("social", 1000, 0.3)
        self.a.spend("social", 1000, 0.3)
        self.a.spend("social", 2000, 1.0)
        self.assertAlmostEqual(self.a.spent("social", 1000), 0.6)
        self.a.decide("u1", "skip")
        self.assertEqual(self.a.decisions(), {"u1": "skip"})
        self.a.add_digest(["u2"], "md")
        self.assertEqual(self.a.digested(), {"u2"})


if __name__ == "__main__":
    unittest.main()
