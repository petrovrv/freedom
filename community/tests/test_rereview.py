# SPDX-License-Identifier: AGPL-3.0-or-later
"""Regression tests for the re-review of d781742 (BLOCK tag names, should-fix 1-7, nits)."""
import asyncio
import json
import os
import tempfile
import time
import unittest
from pathlib import Path

from community import agent, archive, cards, gate, keys, nostr, outfilter, relay, schemas
from community.tests.helpers import MemTransport, sample_card, tmpdir, workbench
from community.tests.test_review_fixes import FakeRG

JOB = {"AIWB_JOB_ID": "JOB-1"}


class TagNameTest(unittest.TestCase):
    """BLOCK: tag names are free text; only the names this package writes are allowed."""

    def test_unknown_tag_names_refused(self):
        wb = workbench("tn")
        for tags in ([["Anna_Kowalski_0471234567", "x"]], [["subject", "x"]], [["r", "wss://x"]], [["T", "freedom"]]):
            with self.assertRaises(agent.DraftError, msg=str(tags)):
                wb.draft(nostr.KIND_DISCUSSION, {"form": "comment", "text": "x"}, extra_tags=tags)
        errs, _ = schemas.check_tags(nostr.KIND_DISCUSSION, [["Anna_Kowalski_0471234567", "x"]])
        self.assertTrue(errs and "not allowed" in errs[0])

    def test_tampered_tag_name_refused_at_approve(self):
        wb = workbench("tn2")
        r = wb.draft(nostr.KIND_DISCUSSION, {"form": "comment", "text": "x"})
        d = wb.archive.draft(r["id"])
        wb.archive.db.execute("UPDATE drafts SET tags=? WHERE id=?",
                              (json.dumps(d["tags"] + [["Kowalski_0471234567"]]), r["id"]))
        with self.assertRaises(agent.DraftError):
            wb.approve(r["id"])


class ApproveGateTest(unittest.TestCase):
    """should-fix 1: inside a job every manual approve needs the owner, except kinds in the auto-publish list."""

    def test_manual_approve_gated_in_job(self):
        rg = FakeRG(True)
        wb = workbench("ag")
        wb.owner_gate = lambda action: gate.require_owner(action, environ=JOB, rg=rg)
        c = wb.draft(nostr.KIND_CARD, sample_card())
        with self.assertRaises(gate.OwnerConfirmRequired):
            wb.approve(c["id"])
        wb.settings["autoPublish"] = ["card"]
        self.assertEqual(wb.approve(c["id"])["status"], "published")
        wb.owner_gate = lambda action: gate.require_owner(action, environ={}, rg=FakeRG(True))   # owner at terminal
        d = wb.draft(nostr.KIND_REVIEW, {"target": "a" * 64, "verdict": "accept", "checks": "c"})
        self.assertEqual(wb.approve(d["id"])["status"], "published")


class DossierFailClosedTest(unittest.TestCase):
    """should-fix 2: an enabled but unreadable dossier blocks publication; an empty valid dossier does not."""

    def stub(self, code):
        tools = Path(tempfile.mkdtemp())
        (tools / "aiwb-dossier").mkdir()
        (tools / "aiwb-dossier" / "aiwb-dossier.py").write_text(code, encoding="utf-8")
        return tools

    def test_dossier_names_errors(self):
        for code in ("import sys; sys.exit(3)\n", "print('not json')\n", "print('{\"error\": \"auth\"}')\n"):
            with self.assertRaises(outfilter.DossierUnavailable, msg=code):
                outfilter.dossier_names(self.stub(code))
        self.assertEqual(outfilter.dossier_names(self.stub("print('{\"rows\": []}')\n")), [])

    def test_unavailable_dossier_holds_publication(self):
        def broken():
            raise outfilter.DossierUnavailable("test outage")
        f = outfilter.from_settings({"dossier": True}, install={}, names_source=broken)
        self.assertEqual(f.check_event_parts({"text": "harmless"}, []).rules(), ["dossier-unavailable"])
        off = outfilter.from_settings({"dossier": False}, install={}, names_source=broken)
        self.assertTrue(off.check_event_parts({"text": "harmless"}, []).ok)
        wb = workbench("df")
        wb.filter = f
        r = wb.draft(nostr.KIND_DISCUSSION, {"form": "comment", "text": "harmless"})
        self.assertEqual(r["status"], "pending")                     # held, not blocked for good
        self.assertEqual(wb.approve(r["id"])["status"], "held")
        wb.settings["autoPublish"] = ["discussion"]
        self.assertEqual(wb.draft(nostr.KIND_DISCUSSION, {"form": "comment", "text": "x"})["status"], "pending")
        wb.filter = off
        self.assertEqual(wb.approve(r["id"])["status"], "published")


class BoundsTest(unittest.TestCase):
    """should-fix 3 and 4: out-of-range numbers, lone surrogates, bad subscription ids."""

    def setUp(self):
        self.k = keys.generate()

    def test_verify_event_bounds(self):
        for created, kind in ((2**63, 1), (-1, 1), (1, 70000), (1, -5)):
            e = nostr.sign_event(self.k.seckey, kind if kind >= 0 else 1, "x", [], created if created >= 0 else 0)
            e["kind"], e["created_at"] = kind, created
            e["id"] = nostr.event_id(e)
            self.assertFalse(nostr.verify_event(e), (created, kind))
        lone = nostr.sign_event(self.k.seckey, 1, "ok", [])
        self.assertFalse(nostr.verify_event(dict(lone, content="\ud800")))
        self.assertFalse(nostr.verify_event(dict(lone, created_at=True)))

    def test_fetch_skips_bad_events_and_advances_cursor(self):
        class RawTransport(MemTransport):
            """A relay that hands over everything it has, junk included (the agent must cope)."""
            def query(self, urls, filters, k):
                return {u: list(self.events) for u in urls}
        t = RawTransport()
        b = workbench("bb", transport=t)
        now = int(time.time())
        bad = nostr.sign_event(self.k.seckey, nostr.KIND_DISCUSSION, "{}", [], now)
        bad["created_at"] = 2**63
        good = nostr.sign_event(self.k.seckey, nostr.KIND_DISCUSSION, json.dumps({"form": "comment", "text": "g"}),
                                [["t", "freedom"]], now)
        t.events += [bad, {"id": 5, "created_at": "x"}, good]
        rep = b.tick()
        self.assertFalse(rep["skipped"])
        self.assertEqual(rep["new"], 1)
        self.assertGreaterEqual(rep["skippedInvalid"], 2)
        self.assertEqual(b.archive.cursor("mem://r1"), now)
        self.assertFalse(b.tick()["skipped"])                         # and the next tick runs too

    def test_filter_bounds_and_surrogates(self):
        for f in ({"since": 2**64}, {"limit": 2**63}, {"#t": ["\ud800"]}, {"\ud800": [1]}):
            self.assertTrue(nostr.filter_problem(f), f)
        self.assertEqual(nostr.filter_problem({"since": 2**63 - 1}), "")

    def test_relay_answers_closed(self):
        r = relay.RelayThread(os.path.join(tmpdir(), "b.sqlite"), allowed=[self.k.pubkey]).start()
        try:
            async def run():
                from websockets.asyncio.client import connect
                async with connect(r.url) as ws:
                    await ws.recv()
                    out = []
                    for msg in (["CLOSE", 5], ["CLOSE", ["x"]], ["REQ", {"a": 1}, {"kinds": [1]}],
                                ["REQ", "s", {"since": 2**64}], ["REQ", "s", {"#t": ["\ud800"]}], ["REQ", "s"]):
                        await ws.send(json.dumps(msg))
                        out.append(json.loads(await asyncio.wait_for(ws.recv(), 5)))
                    await ws.send(json.dumps(["REQ", "ok", {"kinds": [1]}]))
                    out.append(json.loads(await asyncio.wait_for(ws.recv(), 5)))
                    return out
            out = asyncio.run(run())
        finally:
            r.stop()
        for m in out[:-1]:
            self.assertEqual(m[0], "CLOSED", m)
            self.assertTrue(m[2].startswith("invalid"), m)
        self.assertEqual(out[-1][0], "EOSE")                          # the connection still works


class GateFailClosedTest(unittest.TestCase):
    """should-fix 5 + README item: read-guard failing open is refused; ancestor agent process counts as a job."""

    def test_no_decision_without_grant_is_refused(self):
        rg = FakeRG(True)
        rg.guard_check_action = lambda summary, **kw: None            # what read-guard returns on internal errors
        with self.assertRaises(gate.OwnerConfirmRequired) as cm:
            gate.require_owner("x", environ=JOB, rg=rg)
        self.assertIn("no decision", str(cm.exception))

    def test_exception_is_refused(self):
        rg = FakeRG(True)

        def boom(key):
            raise OSError("state dir not writable")
        rg.grant_active = boom
        with self.assertRaises(gate.OwnerConfirmRequired):
            gate.require_owner("x", environ=JOB, rg=rg)

    def test_grant_opens(self):
        gate.require_owner("x", environ=JOB, rg=FakeRG(False))

    def test_ancestor_agent_is_a_job(self):
        self.assertTrue(gate.in_agent_job({}, FakeRG(True, ancestor=True)))
        self.assertFalse(gate.in_agent_job({}, FakeRG(True, ancestor=False)))
        with self.assertRaises(gate.OwnerConfirmRequired):
            gate.require_owner("x", environ={}, rg=FakeRG(True, ancestor=True))
        broken = FakeRG(True)
        broken._ancestor_agent_pid = lambda: 1 / 0
        self.assertFalse(gate.in_agent_job({}, broken))               # best effort; job variables stay the signal


class ClaimFirstSeenTest(unittest.TestCase):
    """should-fix 6: a claim first seen long after its date is flagged in the inbox."""

    def test_late_claim_warning(self):
        t = MemTransport()
        a, b = workbench("ca", transport=t), workbench("cb", transport=t)
        u, now = schemas.new_uuid(), int(time.time())
        a.approve(a.draft(nostr.KIND_PROPOSAL, {"project": u, "title": "P", "goal": "g", "deadline": now + 9 * 86400,
                                                "plan": [{"piece": "p1", "title": "x", "tokens": 1, "days": 1}],
                                                "budget": {"tokens": 1, "days": 1}})["id"])
        b.fetch()
        p = b.archive.events(kinds=[nostr.KIND_PROPOSAL])[0]
        b.approve(b.draft(nostr.KIND_PLEDGE, {"project": u, "sharePercent": 1, "ownerConfirmed": True}, root=p)["id"])
        b.approve(b.draft(nostr.KIND_CLAIM, {"project": u, "piece": "p1", "due": now + 86400}, root=p)["id"])
        a.fetch()
        claim = a.archive.events(kinds=[nostr.KIND_CLAIM])[0]
        self.assertNotIn("warning", a.inbox([claim])[0])
        a.archive.db.execute("UPDATE events SET received_at=? WHERE id=?",
                             (claim["created_at"] + 2 * agent.LATE_GRACE, claim["id"]))
        self.assertIn("backdated", a.inbox([claim])[0]["warning"])


class NitsTest(unittest.TestCase):
    def test_accents_and_surnames(self):
        f = outfilter.OutboundFilter(names=["José Müller", "Anna Kowalski"])
        for text in ("Jose Muller", "jose müller", "MULLER", "kowalski said", "Kowalski's"):
            self.assertIn("name", f.check(text).rules(), text)
        for text in ("anna-karenina", "the mueller report"):
            self.assertTrue(f.check(text).ok, text)
        self.assertEqual(len(outfilter.skeleton("Müller é й")), len("Müller é й"))

    def test_bare_uuid_ambiguity(self):
        a, b = keys.generate(), keys.generate()
        card = cards.sign_card(sample_card(), a, "wb-a")
        arch = archive.Archive(":memory:")
        arch.add_event(cards.to_event(card, a))
        self.assertEqual(arch.card_authors(card["id"]), [a.pubkey])
        arch.add_event(cards.to_event(cards.sign_card(dict(sample_card(), id=card["id"]), b, "wb-b"), b))
        self.assertEqual(len(arch.card_authors(card["id"])), 2)

    def test_fetch_pages(self):
        class PagedTransport(MemTransport):
            """Honours limit/since/until like a relay (newest first)."""
            def query(self, urls, filters, k):
                f = filters[0]
                evs = sorted((e for e in self.events if nostr.match_filter(e, f)),
                             key=lambda e: (-e["created_at"], e["id"]))
                return {u: evs[:f.get("limit", 10**9)] for u in urls}
        t = PagedTransport()
        a = workbench("pg", transport=t)
        k = keys.generate()
        now = int(time.time())
        t.events += [nostr.sign_event(k.seckey, nostr.KIND_DISCUSSION, json.dumps({"form": "comment", "text": str(i)}),
                                      [["t", "freedom"]], now - 1200 + i // 2) for i in range(1100)]
        self.assertEqual(len(a.fetch()), 1100)


if __name__ == "__main__":
    unittest.main()
