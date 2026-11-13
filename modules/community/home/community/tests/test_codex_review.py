# SPDX-License-Identifier: AGPL-3.0-or-later
"""Regression tests for the Codex review of the devtree finish (freedom-community-20261112-104204)."""
import json
import time
import unittest
from unittest import mock

from community import agent, digest, gate, keys, nostr
from community.tests.helpers import MemTransport, sample_card, workbench
from community.tests.test_review_fixes import FakeRG

JOB = {"AIWB_JOB_ID": "JOB-1"}


class PagedTransport(MemTransport):
    """Behaves like a relay: filters, newest first (created_at desc, id asc), at most `limit` rows."""

    def __init__(self, cap=1000):
        super().__init__()
        self.cap, self.queries = cap, 0

    def query(self, urls, filters, k):
        self.queries += 1
        f = filters[0]
        evs = sorted((e for e in self.events if nostr.match_filter(e, f)), key=lambda e: (-e["created_at"], e["id"]))
        return {u: evs[:min(f.get("limit", self.cap), self.cap)] for u in urls}


def note(k, t, i):
    return nostr.sign_event(k.seckey, nostr.KIND_DISCUSSION, json.dumps({"form": "comment", "text": f"n{i}"}),
                            [["t", "freedom"]], t)


class AutoApproveTest(unittest.TestCase):
    """1: approve(auto=True) is the owner's standing policy only - never a way around the gate."""

    def test_auto_requires_policy_membership(self):
        wb = workbench("au")
        wb.owner_gate = lambda action: gate.require_owner(action, environ=JOB, rg=FakeRG(True))   # always refuses
        d = wb.draft(nostr.KIND_DISCUSSION, {"form": "comment", "text": "hi"})
        with self.assertRaises(agent.DraftError):
            wb.approve(d["id"], auto=True)                 # empty policy: no auto publish
        self.assertEqual(wb.transport.events, [])
        p = wb.draft(nostr.KIND_PLEDGE, {"project": "6f1c2a52-6b0e-4d5e-9a57-0f6d8c1f3b10", "sharePercent": 1,
                                         "ownerConfirmed": True})
        wb.settings["autoPublish"] = ["discussion", "pledge"]          # tampered: pledge still never auto
        with self.assertRaises(agent.DraftError):
            wb.approve(p["id"], auto=True)
        self.assertEqual(wb.approve(d["id"], auto=True)["status"], "published")   # listed kind: fine
        self.assertEqual(len(wb.transport.events), 1)


class EmptyAllowlistTest(unittest.TestCase):
    """2: an explicitly empty allowlist admits nobody; only an absent one means "no filter"."""

    def test_allowed_set(self):
        self.assertIsNone(agent.allowed_set(None))
        self.assertIsNone(agent.allowed_set({"identities": {}}))
        self.assertEqual(agent.allowed_set({"pubkeys": []}), set())

    def test_empty_allowlist_drops_everyone(self):
        t = MemTransport()
        a, me = workbench("ea", transport=t), workbench("em", transport=t)
        a.approve(a.draft(nostr.KIND_CARD, sample_card())["id"])
        me.allow = {"pubkeys": [], "identities": {}}
        rep = me.tick()
        self.assertEqual(rep["new"], 0)
        self.assertEqual(rep["droppedNotAllowlisted"], 1)
        self.assertEqual(me.archive.cards(), [])
        # digest: even an archived card of a non-listed author is not shown
        me.allow = None
        me.tick()
        me.archive.set_cursor("mem://r1", 0)
        self.assertEqual(len(digest.build(me.archive, me.keys.pubkey, ours_library=[], allow=None)["items"]), 1)
        self.assertEqual(digest.build(me.archive, me.keys.pubkey, ours_library=[],
                                      allow={"pubkeys": []})["items"], [])


class PaginationTest(unittest.TestCase):
    """3: equal timestamps never stall the walk; an unfinished pass never moves the cursor past unread events."""

    def setUp(self):
        self.k = keys.generate()
        self.now = int(time.time())
        # small pages keep the test fast; the logic is the same as with 500/1000
        self.pages = mock.patch.multiple(agent, FETCH_PAGE=50, FETCH_SECOND=100)
        self.pages.start()
        self.addCleanup(self.pages.stop)

    def test_many_events_in_one_second(self):
        t = PagedTransport()
        wb = workbench("pa", transport=t)
        t.events += [note(self.k, self.now - 100, i) for i in range(52)]        # 52 > page share one created_at
        t.events += [note(self.k, self.now - 200 - i, 1000 + i) for i in range(3)]   # older ones below it
        got = wb.fetch()
        self.assertEqual(len(got), 55)
        self.assertEqual(wb.incomplete, [])
        self.assertEqual(wb.archive.cursor("mem://r1"), self.now - 100)

    def test_page_budget_resumes_and_keeps_cursor(self):
        t = PagedTransport()
        wb = workbench("pb", transport=t, settings={"fetchOverlapSeconds": 0})
        wb.archive.set_cursor("mem://r1", self.now - 5000)                      # since = now - 5000
        t.events += [note(self.k, self.now - 4000 + i, i) for i in range(130)]
        before = wb.archive.cursor("mem://r1")
        with mock.patch.object(agent, "FETCH_MAX_PAGES", 1):
            first = wb.fetch()
            self.assertEqual(wb.incomplete, ["mem://r1"])
            self.assertEqual(wb.archive.cursor("mem://r1"), before)       # not advanced past unread events
            self.assertLess(len(first), 130)
            total = len(first)
            for _ in range(10):
                total += len(wb.fetch())
                if not wb.incomplete:
                    break
        self.assertEqual(total, 130)
        self.assertEqual(wb.incomplete, [])
        self.assertEqual(wb.archive.cursor("mem://r1"), self.now - 4000 + 129)
        self.assertEqual(wb.archive.cursor("resume:mem://r1"), -1)

    def test_new_events_during_backfill_are_not_lost(self):
        t = PagedTransport()
        wb = workbench("pc", transport=t)
        t.events += [note(self.k, self.now - 3000 + i, i) for i in range(110)]
        with mock.patch.object(agent, "FETCH_MAX_PAGES", 1):
            got = len(wb.fetch())
            t.events.append(note(self.k, self.now, 99999))                 # arrives while backfilling
            for _ in range(10):
                got += len(wb.fetch())
                if not wb.incomplete:
                    break
        got += len(wb.fetch())                                             # next normal pass sees the newest
        self.assertEqual(got, 111)


if __name__ == "__main__":
    unittest.main()
