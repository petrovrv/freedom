# SPDX-License-Identifier: AGPL-3.0-or-later
import json
import random
import unittest

from community import cards, keys, nostr, projects, reputation, schemas
from community.tests.helpers import sample_card

DAY = 86400
T0 = 1_780_000_000


class Builder:
    def __init__(self):
        self.events = []

    def ev(self, k, kind, content, t, root=None, parent=None):
        tags = [["t", "freedom"]] + (nostr.reply_tags(root, parent) if root else [])
        e = nostr.sign_event(k.seckey, kind, json.dumps(content), tags, t)
        self.events.append(e)
        return e


class ProjectTest(unittest.TestCase):
    def setUp(self):
        self.a, self.b, self.c = keys.generate(), keys.generate(), keys.generate()
        self.x = Builder()
        self.pid = schemas.new_uuid()
        self.prop = self.x.ev(self.a, nostr.KIND_PROPOSAL, {
            "project": self.pid, "title": "Shared installer check", "goal": "verify the wizard on two OSes",
            "plan": [{"piece": "win", "title": "Windows run", "tokens": 100, "days": 2},
                     {"piece": "mac", "title": "Mac run", "tokens": 100, "days": 2}],
            "budget": {"tokens": 200, "days": 4}, "deadline": T0 + 30 * DAY}, T0)

    def pledge(self, k, t):
        return self.x.ev(k, nostr.KIND_PLEDGE, {"project": self.pid, "sharePercent": 2, "ownerConfirmed": True}, t,
                         self.prop)

    def claim(self, k, piece, t, due):
        return self.x.ev(k, nostr.KIND_CLAIM, {"project": self.pid, "piece": piece, "due": due}, t, self.prop)

    def deliver(self, k, piece, claim, t):
        return self.x.ev(k, nostr.KIND_DELIVERY, {"project": self.pid, "piece": piece, "claim": claim["id"],
                                                  "summary": "done", "artifact": {"type": "git",
                                                                                  "ref": "https://github.com/o/r"}},
                         t, self.prop)

    def review(self, k, target, verdict, t):
        return self.x.ev(k, nostr.KIND_REVIEW, {"target": target["id"], "verdict": verdict, "checks": "ran tests"},
                         t, self.prop, target)

    def test_full_cycle_cross_review(self):
        self.pledge(self.a, T0 + 1)
        self.pledge(self.b, T0 + 2)
        ca = self.claim(self.a, "mac", T0 + 3, T0 + 5 * DAY)
        cb = self.claim(self.b, "win", T0 + 4, T0 + 5 * DAY)
        da = self.deliver(self.a, "mac", ca, T0 + DAY)
        db = self.deliver(self.b, "win", cb, T0 + DAY)
        self.review(self.a, db, "accept", T0 + 2 * DAY)
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 2 * DAY + 1)
        self.assertEqual(st["status"], "open")
        self.review(self.b, da, "accept", T0 + 3 * DAY)
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 3 * DAY + 1)
        self.assertEqual(st["status"], "accepted")
        self.assertEqual({p["piece"]: p["reviewer"] for p in st["pieces"]}, {"win": self.a.pubkey, "mac": self.b.pubkey})
        self.assertEqual({p["result"] for p in st["pledges"]}, {"fulfilled"})

    def test_self_review_does_not_count(self):
        self.pledge(self.a, T0 + 1)
        ca = self.claim(self.a, "mac", T0 + 2, T0 + 5 * DAY)
        da = self.deliver(self.a, "mac", ca, T0 + DAY)
        self.review(self.a, da, "accept", T0 + DAY + 5)
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 2 * DAY)
        self.assertEqual(st["pieces"][1]["status"], "delivered")
        # same GitHub identity counts as self
        self.review(self.c, da, "accept", T0 + DAY + 6)
        ident = {self.a.pubkey: "alice", self.c.pubkey: "alice"}
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 2 * DAY, ident)
        self.assertEqual(st["pieces"][1]["status"], "delivered")
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 2 * DAY)
        self.assertEqual(st["pieces"][1]["status"], "accepted")

    def test_claim_needs_pledge_and_free_piece(self):
        c1 = self.claim(self.b, "win", T0 + 1, T0 + 5 * DAY)        # no pledge yet
        self.pledge(self.b, T0 + 2)
        self.pledge(self.c, T0 + 2)
        c2 = self.claim(self.b, "win", T0 + 3, T0 + 5 * DAY)
        c3 = self.claim(self.c, "win", T0 + 4, T0 + 5 * DAY)        # piece taken
        c4 = self.claim(self.c, "nope", T0 + 5, T0 + 5 * DAY)
        c5 = self.claim(self.c, "mac", T0 + 6, T0 + 60 * DAY)       # due after the deadline
        st = projects.project_state(self.x.events, self.prop["id"], T0 + DAY)
        valid = {c["claim"]: c["valid"] for c in st["claims"]}
        self.assertEqual(valid, {c1["id"]: False, c2["id"]: True, c3["id"]: False, c4["id"]: False, c5["id"]: False})

    def test_missed_claim_returns_to_pool(self):
        self.pledge(self.b, T0 + 1)
        self.pledge(self.c, T0 + 1)
        cb = self.claim(self.b, "win", T0 + 2, T0 + 2 * DAY)
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 3 * DAY)
        self.assertEqual(st["pieces"][0]["status"], "open")
        self.assertEqual([m["pubkey"] for m in st["missed"]], [self.b.pubkey])
        cc = self.claim(self.c, "win", T0 + 3 * DAY, T0 + 6 * DAY)
        dc = self.deliver(self.c, "win", cc, T0 + 4 * DAY)
        late = self.deliver(self.b, "win", cb, T0 + 4 * DAY)         # after its due: ignored
        self.review(self.a, late, "accept", T0 + 4 * DAY + 10)
        self.review(self.a, dc, "accept", T0 + 4 * DAY + 20)
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 5 * DAY)
        win = st["pieces"][0]
        self.assertEqual((win["status"], win["holder"]), ("accepted", self.c.pubkey))
        self.assertEqual(win["history"][0]["result"], "missed")

    def test_rejected_delivery_after_due_is_missed(self):
        self.pledge(self.b, T0 + 1)
        cb = self.claim(self.b, "win", T0 + 2, T0 + 2 * DAY)
        db = self.deliver(self.b, "win", cb, T0 + DAY)
        self.review(self.a, db, "reject", T0 + DAY + 100)
        st = projects.project_state(self.x.events, self.prop["id"], T0 + DAY + 200)
        self.assertEqual(st["pieces"][0]["status"], "delivered")      # can still re-deliver
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 3 * DAY)
        self.assertEqual(st["pieces"][0]["status"], "open")
        self.assertEqual(len(st["missed"]), 1)

    def test_pledge_missed_after_deadline(self):
        self.pledge(self.c, T0 + 1)
        st = projects.project_state(self.x.events, self.prop["id"], T0 + 31 * DAY)
        self.assertEqual(st["status"], "expired")
        self.assertEqual(st["pledges"][0]["result"], "missed")

    def test_order_independent(self):
        self.test_full_cycle_cross_review()
        evs = list(self.x.events)
        random.Random(1).shuffle(evs)
        self.assertEqual(projects.project_state(evs, self.prop["id"], T0 + 10 * DAY),
                         projects.project_state(self.x.events, self.prop["id"], T0 + 10 * DAY))


class ReputationTest(unittest.TestCase):
    def setUp(self):
        self.a, self.b, self.c = keys.generate(), keys.generate(), keys.generate()
        self.x = Builder()

    def card(self, k, t):
        c = cards.sign_card(sample_card(), k, "wb")
        e = cards.to_event(c, k, created_at=t)
        self.x.events.append(e)
        return c, e

    def report(self, k, card, cev, outcome, t):
        return self.x.ev(k, nostr.KIND_REPORT, {"card": card["id"], "outcome": outcome, "measure": "m"}, t, cev)

    def test_card_confirmation_rules(self):
        c, ce = self.card(self.a, T0)
        self.report(self.b, c, ce, "worked", T0 + 10)
        self.report(self.b, c, ce, "worked", T0 + 20)                # same reporter twice: once
        self.report(self.a, c, ce, "worked", T0 + 30)                # self: nothing
        self.report(self.c, c, ce, "failed", T0 + 40)                # failed: nothing
        r = reputation.compute(self.x.events, T0 + 50)
        self.assertEqual([i["rule"] for i in r[self.a.pubkey]["items"]], ["card_confirmed"])
        self.assertAlmostEqual(r[self.a.pubkey]["score"], 3.0, places=3)

    def test_identity_excludes_sock_puppets(self):
        c, ce = self.card(self.a, T0)
        self.report(self.c, c, ce, "worked", T0 + 10)
        r = reputation.compute(self.x.events, T0 + 50, {self.a.pubkey: "alice", self.c.pubkey: "alice"})
        self.assertNotIn(self.a.pubkey, r)

    def test_report_accepted_by_review(self):
        c, ce = self.card(self.a, T0)
        rep = self.report(self.b, c, ce, "worked", T0 + 10)
        self.x.ev(self.c, nostr.KIND_REVIEW, {"target": rep["id"], "verdict": "accept", "checks": "repeated"},
                  T0 + 20, ce, rep)
        r = reputation.compute(self.x.events, T0 + 30)
        self.assertEqual([i["rule"] for i in r[self.b.pubkey]["items"]], ["report_accepted"])

    def test_decay_half_life(self):
        c, ce = self.card(self.a, T0)
        self.report(self.b, c, ce, "worked", T0)
        r = reputation.compute(self.x.events, T0 + int(reputation.HALF_LIFE))
        self.assertAlmostEqual(r[self.a.pubkey]["score"], 1.5, places=3)
        self.assertEqual(reputation.compute(self.x.events, T0 - 1), {})     # future events ignored

    def test_flag_verified_by_generic_filter(self):
        leak = self.x.ev(self.a, nostr.KIND_DISCUSSION, {"form": "comment", "text": "write me at joe@corp-unknown.com"},
                         T0)
        clean = self.x.ev(self.a, nostr.KIND_DISCUSSION, {"form": "comment", "text": "nothing personal"}, T0 + 1)
        self.x.ev(self.b, nostr.KIND_FLAG, {"target": leak["id"], "rules": ["email"]}, T0 + 10)
        self.x.ev(self.b, nostr.KIND_FLAG, {"target": leak["id"], "rules": ["email"]}, T0 + 11)   # once per post
        self.x.ev(self.b, nostr.KIND_FLAG, {"target": clean["id"], "rules": ["email"]}, T0 + 12)  # false flag
        self.x.ev(self.a, nostr.KIND_FLAG, {"target": leak["id"], "rules": ["email"]}, T0 + 13)   # self flag
        r = reputation.compute(self.x.events, T0 + 20)
        self.assertEqual([i["rule"] for i in r[self.a.pubkey]["items"]], ["flagged"])
        self.assertAlmostEqual(r[self.a.pubkey]["score"], -3.0, places=3)

    def test_invalid_and_duplicate_events_ignored(self):
        c, ce = self.card(self.a, T0)
        rep = self.report(self.b, c, ce, "worked", T0 + 10)
        evs = self.x.events + [rep, dict(rep, id="0" * 64)]
        self.assertEqual(reputation.compute(evs, T0 + 50), reputation.compute(self.x.events, T0 + 50))

    def test_deterministic_any_order(self):
        c, ce = self.card(self.a, T0)
        for i, k in enumerate((self.b, self.c)):
            self.report(k, c, ce, "worked", T0 + i)
        evs = list(self.x.events)
        random.Random(7).shuffle(evs)
        self.assertEqual(reputation.compute(evs, T0 + DAY), reputation.compute(self.x.events, T0 + DAY))


if __name__ == "__main__":
    unittest.main()
