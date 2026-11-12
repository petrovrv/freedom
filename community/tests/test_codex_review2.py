# SPDX-License-Identifier: AGPL-3.0-or-later
"""Regression tests for the second Codex review (freedom-community-20261112-104953)."""
import asyncio
import json
import os
import sqlite3
import time
import unittest

from community import archive, cards, client, keys, nostr, relay, schemas
from community.tests.helpers import MemTransport, sample_card, tmpdir, workbench


def note(k, t, text="x", tags=None):
    return nostr.sign_event(k.seckey, nostr.KIND_DISCUSSION, json.dumps({"form": "comment", "text": text}),
                            [["t", "freedom"]] + (tags or []), t)


class FlakyTransport(MemTransport):
    """query_status like NetTransport; while `broken`, answers are partial and unconfirmed (no EOSE)."""

    def __init__(self):
        super().__init__()
        self.broken = False

    def query_status(self, urls, filters, k):
        f = filters[0]
        evs = sorted((e for e in self.events if nostr.match_filter(e, f)), key=lambda e: (-e["created_at"], e["id"]))
        evs = evs[:f.get("limit", 10**9)]
        return {u: ((evs[:3], False) if self.broken else (evs, True)) for u in urls}


class EoseTest(unittest.TestCase):
    """1: only EOSE makes a page complete; on a broken read the cursor stays and the pass resumes."""

    def test_partial_read_keeps_cursor(self):
        t = FlakyTransport()
        wb = workbench("eo", transport=t, settings={"fetchOverlapSeconds": 0})
        k, now = keys.generate(), int(time.time())
        wb.archive.set_cursor("mem://r1", now - 1000)
        t.events += [note(k, now - 900 + i, str(i)) for i in range(10)]
        t.broken = True
        got = wb.fetch()
        self.assertEqual(len(got), 3)                                  # what arrived is kept ...
        self.assertEqual(wb.incomplete, ["mem://r1"])
        self.assertEqual(wb.archive.cursor("mem://r1"), now - 1000)     # ... but the cursor does not move
        t.broken = False
        got += wb.fetch()
        self.assertEqual(len(got), 10)
        self.assertEqual(wb.incomplete, [])
        self.assertEqual(wb.archive.cursor("mem://r1"), now - 900 + 9)

    def test_client_reports_eose(self):
        k = keys.generate()
        r = relay.RelayThread(os.path.join(tmpdir(), "e.sqlite"), allowed=[k.pubkey]).start()
        try:
            client.publish([r.url], note(k, int(time.time())), k)
            res = client.NetTransport(timeout=15).query_status([r.url, "ws://127.0.0.1:1"], [{"kinds": [4472]}], None)
        finally:
            r.stop()
        self.assertEqual((len(res[r.url][0]), res[r.url][1]), (1, True))
        self.assertEqual(res["ws://127.0.0.1:1"], ([], False))

    def test_disconnect_before_eose_is_incomplete(self):
        k = keys.generate()
        ev = note(k, int(time.time()))

        async def run():
            from websockets.asyncio.server import serve

            async def rude(ws):                    # sends one event, then hangs up without EOSE
                msg = json.loads(await ws.recv())
                await ws.send(json.dumps(["EVENT", msg[1], ev]))
                await ws.close()
            async with serve(rude, "127.0.0.1", 0) as srv:
                port = srv.sockets[0].getsockname()[1]
                return await client.query_one_status(f"ws://127.0.0.1:{port}", [{"kinds": [4472]}], None, 3)
        evs, ok = asyncio.run(run())
        self.assertEqual(len(evs), 1)
        self.assertFalse(ok)


class InboundValidationTest(unittest.TestCase):
    """2: malformed signed content is validated away per event; the inbox is idempotent."""

    def test_bad_review_target_does_not_abort_inbox(self):
        t = MemTransport()
        a, b = workbench("va", transport=t), workbench("vb", transport=t)
        a.approve(a.draft(nostr.KIND_CARD, sample_card())["id"])
        k = b.keys
        bad = nostr.sign_event(k.seckey, nostr.KIND_REVIEW, json.dumps({"target": ["bad"], "verdict": "accept",
                                                                        "checks": "c"}), [["t", "freedom"]])
        junk = nostr.sign_event(k.seckey, nostr.KIND_FLAG, "not json", [["t", "freedom"]])
        other = keys.generate()
        t.events += [bad, junk]
        a.allow = None
        # a valid reply on A's card in the same batch must still reach the inbox
        ce = a.archive.events(kinds=[nostr.KIND_CARD])[0]
        t.events.append(nostr.sign_event(other.seckey, nostr.KIND_DISCUSSION,
                                         json.dumps({"form": "question", "text": "how?"}), nostr.reply_tags(ce)))
        rep = a.tick()
        self.assertEqual(rep["invalidContent"], 2)
        self.assertEqual([i["todo"] for i in rep["inbox"]], ["reply on our post: answer or confirm"])
        self.assertEqual(a.tick()["inbox"], [])                           # handled once

    def test_drafter_crash_re_presents_items(self):
        t = MemTransport()
        a, b = workbench("ia", transport=t), workbench("ib", transport=t)
        a.approve(a.draft(nostr.KIND_CARD, sample_card())["id"])

        def crash(wb, new, inbox):
            raise RuntimeError("agent died")
        with self.assertRaises(RuntimeError):
            b.tick(drafter=crash)
        rep = b.tick()                                                    # nothing new from the relay ...
        self.assertEqual(rep["new"], 0)
        self.assertEqual(len(rep["inbox"]), 1)                            # ... but the card is shown again
        self.assertEqual(b.tick()["inbox"], [])

    def test_self_audit_claim_and_volunteer_shapes(self):
        from community import volunteer
        wb = workbench("sa")
        k = keys.generate()
        fake_claim = nostr.sign_event(k.seckey, nostr.KIND_CLAIM, json.dumps({"due": "soon"}), [])   # invalid claim
        wb.archive.add_event(fake_claim)
        dev = {"claim": fake_claim["id"]}
        self.assertEqual(wb._late_delivery(fake_claim, dev), "")       # no TypeError, no warning from junk
        lst = nostr.sign_event(k.seckey, nostr.KIND_HELP, json.dumps(["not", "a", "dict"]), [])
        task = volunteer.formulate_task(lst, "analysis", "Explain the setup wizard error codes in a public card")
        self.assertEqual(task["type"], "analysis")

    def test_archive_rejects_untyped_ids(self):
        a = archive.Archive(":memory:")
        for bad in (["bad"], {"x": 1}, 5, None):
            self.assertIsNone(a.event(bad))
            self.assertIsNone(a.received_at(bad))


class CardThreadTest(unittest.TestCase):
    """3: updating a card keeps its discussion (threads by coordinate and all known version ids)."""

    def test_update_keeps_thread(self):
        t = MemTransport()
        a, b = workbench("ta", transport=t), workbench("tb", transport=t)
        card = sample_card()
        a.approve(a.draft(nostr.KIND_CARD, card)["id"])
        v1 = a.archive.events(kinds=[nostr.KIND_CARD])[0]
        b.fetch()
        b.approve(b.draft(nostr.KIND_DISCUSSION, {"form": "question", "text": "q on v1"},
                          root=b.archive.events(kinds=[nostr.KIND_CARD])[0])["id"])
        time.sleep(1.1)                                                   # newer created_at for the update
        a.approve(a.draft(nostr.KIND_CARD, dict(card, result="MRR 0.66 -> 0.90"))["id"])
        v2 = a.archive.events(kinds=[nostr.KIND_CARD])[0]
        self.assertNotEqual(v1["id"], v2["id"])
        self.assertIsNone(a.archive.event(v1["id"]))                     # old version replaced
        rep = a.tick()
        self.assertEqual([i["todo"] for i in rep["inbox"]], ["reply on our post: answer or confirm"])
        for wb_id in (v2["id"], v1["id"]):                               # by the new id and by the old one
            th = a.thread(wb_id)
            self.assertEqual(th[0]["id"], v2["id"])
            self.assertEqual([json.loads(e["content"])["text"] for e in th[1:]], ["q on v1"])
        b.fetch()                                                         # B sees the update too
        self.assertEqual(len(b.archive.thread_events(b.archive.events(kinds=[nostr.KIND_CARD])[0])), 1)

    def test_old_archive_is_migrated(self):
        path = os.path.join(tmpdir(), "old.sqlite")
        k = keys.generate()
        a = archive.Archive(path)
        c = cards.sign_card(sample_card(), k, "wb")
        ce = cards.to_event(c, k)
        a.add_event(ce)
        a.add_event(nostr.sign_event(keys.generate().seckey, nostr.KIND_DISCUSSION,
                                     json.dumps({"form": "comment", "text": "r"}), nostr.reply_tags(ce)))
        a.close()
        db = sqlite3.connect(path)                                        # make it look like a 0.1.3 archive
        db.executescript("DROP INDEX events_coord; ALTER TABLE events DROP COLUMN coord; DROP TABLE processed; "
                         "DROP TABLE meta;")
        db.commit()
        db.close()
        b = archive.Archive(path)
        self.assertEqual(len(b.thread_events(ce)), 1)                     # coord back-filled
        self.assertEqual(b.unprocessed(), [])                             # already-seen events are not re-shown


if __name__ == "__main__":
    unittest.main()
