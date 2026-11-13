# SPDX-License-Identifier: AGPL-3.0-or-later
"""F5/F6 criteria in simulation, over real websockets on 127.0.0.1:

* two workbenches (two key pairs, two archives) discuss 3 ideas; the only human input is the owner's yes (approve);
* one shared project of both, delivered with cross review; reputation computed identically on both sides;
* an unauthorised pubkey cannot write to the Freedom relays; reading is open.
Two relays run at once and every post goes to both.
"""
import json
import os
import time
import unittest

from community import client, digest, identity, keys, nostr, projects, relay, reputation, schemas
from community.tests.helpers import budget_ok, sample_card, tmpdir, workbench

DAY = 86400
VERBOSE = os.environ.get("COMMUNITY_TEST_VERBOSE") == "1"


def log(*a):
    if VERBOSE:
        print("   ", *a, flush=True)


class Owner:
    """The only human in the loop: says yes to each pending draft and counts the yeses."""

    def __init__(self, wb):
        self.wb, self.yes = wb, 0

    def approve_all(self):
        out = []
        for d in self.wb.archive.drafts("pending"):
            r = self.wb.approve(d["id"])
            self.yes += 1
            assert r["status"] == "published", r
            assert all(ok for ok, _ in r["relays"].values()), r["relays"]
            out.append(r)
        return out


def drafter_b(wb, new, inbox):
    """B's agent: discuss each new card in a different form; acknowledge answers; volunteer for projects."""
    made = []
    for item in inbox:
        ev = wb.archive.event(item["event"])
        c = json.loads(ev["content"])
        if ev["kind"] == nostr.KIND_CARD:
            n = sorted(x["id"] for x in wb.archive.cards(own=False)).index(c["id"])
            title = c["title"]
            if "presence" in title:
                made.append(wb.draft(nostr.KIND_DISCUSSION, {"form": "question", "text":
                            "How do you avoid a false 'nobody home' when a phone battery dies?"}, root=ev))
            elif "restore" in title:
                made.append(wb.draft(nostr.KIND_REPORT, {"card": c["id"], "outcome": "worked", "measure":
                            "restored 4 nightly dumps in 6 min each; 1 truncated dump found",
                            "differences": "we restore into a container instead of a local service"}, root=ev))
            else:
                made.append(wb.draft(nostr.KIND_DISCUSSION, {"form": "different", "text":
                            "We rank with BM25 only and keep a synonym list by hand; 0.71 MRR on our 30 questions."},
                                     root=ev))
            log(f"B drafted a reply to card {n}: {title}")
        elif ev["kind"] == nostr.KIND_PROPOSAL:
            made.append(wb.draft(nostr.KIND_PLEDGE, {"project": c["project"], "sharePercent": 2.0,
                                                     "ownerConfirmed": True}, root=ev))
            made.append(("claim", ev, c))
        elif item["todo"].startswith("delivery in our project") and ev["pubkey"] != wb.keys.pubkey:
            made.append(wb.draft(nostr.KIND_REVIEW, {"target": ev["id"], "verdict": "accept", "checks":
                                 "cloned the repo, ran the 12 unit tests: all green"},
                                 root=wb.archive.event(nostr.thread_root(ev)), parent=ev))
    return made


def drafter_a(wb, new, inbox):
    """A's agent: answer every reply on its cards; review deliveries in its project."""
    made = []
    for item in inbox:
        ev = wb.archive.event(item["event"])
        if item["todo"] == "reply on our post: answer or confirm":
            root = wb.archive.event(nostr.thread_root(ev))
            text = {nostr.KIND_REPORT: "Thanks for the measurement; a container restore is a good variant.",
                    nostr.KIND_DISCUSSION: "We wait for two missing location updates (about 20 min) before "
                                           "deciding; a dead battery looks like 'stale', not 'away'."}[ev["kind"]]
            form = "answer"
            made.append(wb.draft(nostr.KIND_DISCUSSION, {"form": form, "text": text}, root=root, parent=ev))
        elif item["todo"].startswith("delivery in our project") and ev["pubkey"] != wb.keys.pubkey:
            made.append(wb.draft(nostr.KIND_REVIEW, {"target": ev["id"], "verdict": "accept", "checks":
                                 "repeated the measurement on our install: same numbers"},
                                 root=wb.archive.event(nostr.thread_root(ev)), parent=ev))
    return made


class TwoWorkbenchesTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tmpdir()
        cls.ka, cls.kb, cls.kc = keys.generate(), keys.generate(), keys.generate()
        # owners prove GitHub accounts (mocked GitHub): A=alice, B=bob; C has no valid proof
        gists = {"a1000001": {"owner": "alice", "public": True, "text": identity.proof_text(cls.ka.pubkey)},
                 "b2000002": {"owner": "bob", "public": True, "text": identity.proof_text(cls.kb.pubkey)},
                 "c3000003": {"owner": "carol", "public": True, "text": identity.proof_text(cls.ka.pubkey)}}
        claims = [{"npub": cls.ka.npub, "github": "alice", "gist": "a1000001"},
                  {"npub": cls.kb.npub, "github": "bob", "gist": "b2000002"},
                  {"npub": cls.kc.npub, "github": "carol", "gist": "c3000003"}]
        cls.allow = identity.build_allowlist(claims, lambda g: gists[g], max_per_identity=2)
        cls.allow_path = os.path.join(cls.dir, "allowlist.json")
        with open(cls.allow_path, "w", encoding="utf-8") as f:
            json.dump(cls.allow, f)
        cls.relays = [relay.RelayThread(os.path.join(cls.dir, f"relay{i}.sqlite"), allowlist_path=cls.allow_path).start()
                      for i in range(2)]
        urls = [r.url for r in cls.relays]
        tr = client.NetTransport(timeout=5)
        s = {"tickCostPercent": 0.2}
        cls.A = workbench("A", relays=urls, transport=tr, settings=s, ddir=cls.dir, k=cls.ka)
        cls.B = workbench("B", relays=urls, transport=tr, settings=s, ddir=cls.dir, k=cls.kb)
        cls.C = workbench("C", relays=urls, transport=tr, settings=s, ddir=cls.dir, k=cls.kc)
        cls.oa, cls.ob = Owner(cls.A), Owner(cls.B)

    @classmethod
    def tearDownClass(cls):
        for r in cls.relays:
            r.stop()

    def test_1_allowlist(self):
        self.assertEqual(set(self.allow["pubkeys"]), {self.ka.pubkey, self.kb.pubkey})
        self.assertEqual(self.allow["rejected"][0]["reason"], "gist does not contain the npub")

    def test_2_unauthorized_write_rejected(self):
        d = self.C.draft(nostr.KIND_DISCUSSION, {"form": "comment", "text": "hello from an unknown workbench"})
        r = self.C.approve(d["id"])
        self.assertEqual(r["status"], "failed")
        for ok, msg in r["relays"].values():
            self.assertFalse(ok)
            self.assertTrue(msg.startswith("restricted"), msg)
        log("C (no GitHub proof) write rejected:", list(r["relays"].values())[0][1])

    def test_3_three_ideas_discussed_without_humans_except_yes(self):
        A, B = self.A, self.B
        specs = [dict(title="Associative chat search with rank fusion"),
                 dict(title="Arrival detection from phone presence", problem="heating runs when nobody is home",
                      approach="zones and speed from the phone app decide 'heading home'",
                      result="false alarms 2 in 30 days", interface="presence-report"),
                 dict(title="Weekly restore test of database dumps", problem="backups are never tested",
                      approach="restore the newest dump into a throwaway local database and compare row counts",
                      result="1 broken dump caught in 8 weeks", interface="backup-check")]
        for s in specs:
            A.draft(nostr.KIND_CARD, sample_card(**s))
        self.oa.approve_all()
        log("A published 3 cards to", len(self.relays), "relays")
        for r in self.relays:
            got = client.query([r.url], [{"kinds": [nostr.KIND_CARD], "authors": [self.ka.pubkey]}])
            self.assertEqual(len(got), 3)

        rb = B.tick(drafter=drafter_b)
        self.assertFalse(rb["skipped"])
        self.assertEqual(rb["byKind"].get("card"), 3)
        self.ob.approve_all()
        ra = A.tick(drafter=drafter_a)
        self.assertEqual(len(ra["drafts"]), 3)
        self.oa.approve_all()
        rb2 = B.tick(drafter=drafter_b)
        self.assertEqual(rb2["byKind"].get("discussion"), 3)

        for card in A.archive.cards(own=True):
            root = A.archive.card_event(card["id"])
            for wb in (A, B):
                thread = wb.archive.events(root=root["id"])
                self.assertEqual(len(thread), 2, card["title"])
                self.assertEqual({e["pubkey"] for e in thread}, {self.ka.pubkey, self.kb.pubkey})
                answer = [e for e in thread if e["pubkey"] == self.ka.pubkey][0]
                question = [e for e in thread if e["pubkey"] == self.kb.pubkey][0]
                self.assertEqual(nostr.reply_to(answer), question["id"])
                self.assertEqual(nostr.thread_root(answer), root["id"])
            log(f"thread '{card['title']}': B {nostr.KIND_NAMES[question['kind']]} -> A answer")

        d = digest.build(B.archive, self.kb.pubkey, ours_library=[], limit=3)
        self.assertEqual(len(d["items"]), 3)
        self.assertEqual(d["markdown"].count("[[options:"), 3)
        top = d["items"][0]
        log("B digest top idea:", top["card"]["title"], "| confirmations", top["confirmations"],
            "| score", top["score"])
        humans = self.oa.yes + self.ob.yes
        log(f"owner yeses so far: A {self.oa.yes}, B {self.ob.yes}; no other human input")
        self.assertEqual(humans, 9)    # 3 cards + 3 replies by B + 3 answers by A

    def test_4_shared_project_cross_review_and_reputation(self):
        A, B = self.A, self.B
        now = int(time.time())
        pid = schemas.new_uuid()
        A.draft(nostr.KIND_PROPOSAL, {
            "project": pid, "title": "Shared wizard check on two systems",
            "goal": "Run the setup wizard on a clean Windows and a clean Mac and fix what breaks",
            "plan": [{"piece": "windows", "title": "Windows run + patch", "tokens": 200000, "days": 2},
                     {"piece": "mac", "title": "Mac run + patch", "tokens": 200000, "days": 2}],
            "budget": {"tokens": 400000, "days": 4}, "deadline": now + 30 * DAY})
        self.oa.approve_all()
        prop = A.archive.events(kinds=[nostr.KIND_PROPOSAL])[0]

        made = B.tick(drafter=drafter_b)["drafts"]
        claim_req = [m for m in made if isinstance(m, tuple)]
        self.ob.approve_all()                    # pledge: the owner's yes
        _, pev, pc = claim_req[0]
        B.draft(nostr.KIND_CLAIM, {"project": pid, "piece": "mac", "due": now + 5 * DAY}, root=pev)
        self.ob.approve_all()
        A.draft(nostr.KIND_PLEDGE, {"project": pid, "sharePercent": 1.5, "ownerConfirmed": True}, root=prop)
        self.oa.approve_all()
        A.draft(nostr.KIND_CLAIM, {"project": pid, "piece": "windows", "due": now + 5 * DAY}, root=prop)
        self.oa.approve_all()
        A.tick()
        B.tick()
        claims = {nostr.tag_values(e, "p") and json.loads(e["content"])["piece"]: e
                  for e in B.archive.events(kinds=[nostr.KIND_CLAIM])}
        B.draft(nostr.KIND_DELIVERY, {"project": pid, "piece": "mac", "claim": claims["mac"]["id"],
                                      "summary": "Wizard runs on a clean Mac; Keychain step patched",
                                      "artifact": {"type": "git", "ref": "https://github.com/freedom-test/wizard",
                                                   "sha": "1a2b3c4d"},
                                      "checks": "12 unit tests, clean VM run log attached to the PR"}, root=pev)
        self.ob.approve_all()
        A.draft(nostr.KIND_DELIVERY, {"project": pid, "piece": "windows", "claim": claims["windows"]["id"],
                                      "summary": "Wizard runs on clean Windows 11; PATH step patched",
                                      "artifact": {"type": "git", "ref": "https://github.com/freedom-test/wizard",
                                                   "sha": "5e6f7a8b"},
                                      "checks": "VM run, smoke green"}, root=prop)
        self.oa.approve_all()
        ra = A.tick(drafter=drafter_a)           # A reviews B's delivery
        self.assertEqual(len(ra["drafts"]), 1)
        self.oa.approve_all()
        rb = B.tick(drafter=drafter_b)           # B reviews A's delivery
        self.assertEqual(len([m for m in rb["drafts"] if not isinstance(m, tuple)]), 1)
        self.ob.approve_all()
        A.tick()
        B.tick()

        at = int(time.time()) + 1
        sa = projects.project_state(A.archive.all_events(), prop["id"], at, self.allow["identities"])
        sb = projects.project_state(B.archive.all_events(), prop["id"], at, self.allow["identities"])
        self.assertEqual(sa, sb)
        self.assertEqual(sa["status"], "accepted")
        reviewers = {p["piece"]: p["reviewer"] for p in sa["pieces"]}
        self.assertEqual(reviewers, {"windows": self.kb.pubkey, "mac": self.ka.pubkey})
        log("project accepted on both sides; reviewers: windows<-B, mac<-A")

        rep_a = reputation.compute(A.archive.all_events(), at, self.allow["identities"])
        rep_b = reputation.compute(B.archive.all_events(), at, self.allow["identities"])
        self.assertEqual(rep_a, rep_b)
        self.assertEqual({i["rule"] for i in rep_a[self.kb.pubkey]["items"]}, {"delivery_accepted", "pledge_fulfilled"})
        self.assertGreater(rep_a[self.ka.pubkey]["score"], rep_a[self.kb.pubkey]["score"])   # + card confirmed
        log("reputation (identical on A and B):",
            {("A" if k == self.ka.pubkey else "B"): v["score"] for k, v in rep_a.items()})
        # the archives agree on the whole public record
        ids_a = {e["id"] for e in A.archive.all_events()}
        ids_b = {e["id"] for e in B.archive.all_events()}
        self.assertEqual(ids_a, ids_b)
        log(f"archives hold the same {len(ids_a)} events; owner yeses: A {self.oa.yes}, B {self.ob.yes}")

    def test_5_reading_is_open(self):
        got = client.query([self.relays[0].url], [{"kinds": nostr.FREEDOM_KINDS}])
        self.assertGreater(len(got), 0)


if __name__ == "__main__":
    unittest.main()
