# SPDX-License-Identifier: AGPL-3.0-or-later
import asyncio
import json
import os
import time
import unittest

from community import client, identity, keys, nostr, relay, schnorr
from community.tests.helpers import tmpdir


class RelayTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.a, cls.b, cls.c = keys.generate(), keys.generate(), keys.generate()
        cls.dir = tmpdir()
        cls.allow = os.path.join(cls.dir, "allow.json")
        with open(cls.allow, "w", encoding="utf-8") as f:
            json.dump({"pubkeys": [cls.a.pubkey, schnorr.npub(cls.b.pubkey)]}, f)
        cls.r = relay.RelayThread(os.path.join(cls.dir, "relay.sqlite"), allowlist_path=cls.allow).start()

    @classmethod
    def tearDownClass(cls):
        cls.r.stop()

    def ev(self, k, kind=nostr.KIND_DISCUSSION, tags=None, created_at=None, content=None):
        return nostr.sign_event(k.seckey, kind, content or json.dumps({"form": "comment", "text": "hi"}),
                                [["t", "freedom"]] + (tags or []), created_at)

    def test_write_allowed_and_read_open(self):
        e = self.ev(self.a)
        self.assertEqual(client.publish([self.r.url], e, self.a)[self.r.url], [True, ""])
        got = client.query([self.r.url], [{"ids": [e["id"]]}])            # no key: reading is open
        self.assertEqual([x["id"] for x in got], [e["id"]])
        dup = client.publish([self.r.url], e, self.a)[self.r.url]
        self.assertTrue(dup[0])
        self.assertTrue(dup[1].startswith("duplicate"))

    def test_unauthenticated_write_rejected(self):
        res = client.publish([self.r.url], self.ev(self.a), None)[self.r.url]
        self.assertFalse(res[0])
        self.assertTrue(res[1].startswith("auth-required"))

    def test_unauthorized_pubkey_rejected(self):
        e = self.ev(self.c)
        res = client.publish([self.r.url], e, self.c)[self.r.url]
        self.assertEqual(res[0], False)
        self.assertTrue(res[1].startswith("restricted"))
        self.assertEqual(client.query([self.r.url], [{"ids": [e["id"]]}]), [])

    def test_allowed_connection_cannot_push_foreign_author(self):
        e = self.ev(self.c)
        res = client.publish([self.r.url], e, self.a)[self.r.url]
        self.assertFalse(res[0])
        self.assertIn("author", res[1])

    def test_rebroadcast_of_allowed_author(self):
        e = self.ev(self.b)
        self.assertTrue(client.publish([self.r.url], e, self.a)[self.r.url][0])

    def test_bad_signature_rejected(self):
        e = dict(self.ev(self.a), content="tampered")
        res = client.publish([self.r.url], e, self.a)[self.r.url]
        self.assertFalse(res[0])
        self.assertTrue(res[1].startswith("invalid"))

    def test_addressable_replacement_and_tag_filter(self):
        old = self.ev(self.a, nostr.KIND_CARD, [["d", "card-1"]], created_at=int(time.time()) - 100, content="{}")
        new = self.ev(self.a, nostr.KIND_CARD, [["d", "card-1"]], created_at=int(time.time()), content='{"v":2}')
        client.publish([self.r.url], old, self.a)
        client.publish([self.r.url], new, self.a)
        res = client.publish([self.r.url], old, self.a)[self.r.url]       # older version refused
        self.assertIn("replaced-newer", res[1])
        got = client.query([self.r.url], [{"kinds": [nostr.KIND_CARD], "#d": ["card-1"]}])
        self.assertEqual([g["id"] for g in got], [new["id"]])

    def test_auth_with_wrong_challenge(self):
        async def run():
            from websockets.asyncio.client import connect
            async with connect(self.r.url) as ws:
                json.loads(await ws.recv())
                ev = nostr.auth_event(self.a.seckey, self.r.url, "wrong")
                await ws.send(json.dumps(["AUTH", ev]))
                ok = json.loads(await ws.recv())
                await ws.send(json.dumps(["EVENT", self.ev(self.a)]))
                return ok, json.loads(await ws.recv())
        ok, after = asyncio.run(run())
        self.assertFalse(ok[2])
        self.assertFalse(after[2])

    def test_live_subscription(self):
        async def run():
            from websockets.asyncio.client import connect
            async with connect(self.r.url) as ws:
                await ws.recv()
                await ws.send(json.dumps(["REQ", "s1", {"kinds": [nostr.KIND_REVIEW]}]))
                self.assertEqual(json.loads(await ws.recv())[0], "EOSE")
                e = self.ev(self.a, nostr.KIND_REVIEW)
                await client.publish_one(self.r.url, e, self.a)
                m = json.loads(await asyncio.wait_for(ws.recv(), 5))
                return m, e
        m, e = asyncio.run(run())
        self.assertEqual(m[:2], ["EVENT", "s1"])
        self.assertEqual(m[2]["id"], e["id"])

    def test_publish_to_several_relays_one_dead(self):
        e = self.ev(self.a)
        res = client.publish([self.r.url, "ws://127.0.0.1:1"], e, self.a, timeout=3)
        self.assertTrue(res[self.r.url][0])
        self.assertFalse(res["ws://127.0.0.1:1"][0])

    def test_allowlist_reload(self):
        d = keys.generate()
        e = self.ev(d)
        self.assertFalse(client.publish([self.r.url], e, d)[self.r.url][0])
        with open(self.allow, encoding="utf-8") as f:
            al = json.load(f)
        al["pubkeys"].append(d.pubkey)
        time.sleep(0.05)
        with open(self.allow, "w", encoding="utf-8") as f:
            json.dump(al, f)
        os.utime(self.allow, (time.time() + 5, time.time() + 5))
        self.assertTrue(client.publish([self.r.url], e, d)[self.r.url][0])


class IdentityTest(unittest.TestCase):
    def setUp(self):
        self.ks = [keys.generate() for _ in range(5)]
        self.gists = {}

        def fetch(gid):
            if gid not in self.gists:
                raise FileNotFoundError("404")
            return self.gists[gid]
        self.fetch = fetch

    def gist(self, gid, owner, k, public=True):
        self.gists[gid] = {"owner": owner, "public": public, "text": "my proof\n" + identity.proof_text(k.pubkey)}

    def test_build_allowlist_limits(self):
        claims = []
        for i, k in enumerate(self.ks[:4]):
            self.gist(f"aa{i:06d}", "alice", k)
            claims.append({"npub": k.npub, "github": "Alice", "gist": f"aa{i:06d}", "created_at": i})
        self.gist("bb000001", "bob", self.ks[4])
        claims.append({"pubkey": self.ks[4].pubkey, "github": "bob", "gist": "bb000001", "created_at": 9})
        al = identity.build_allowlist(claims, self.fetch, max_per_identity=3)
        self.assertEqual(al["pubkeys"], [k.pubkey for k in self.ks[:3]] + [self.ks[4].pubkey])
        self.assertEqual(al["identities"][self.ks[0].pubkey], "alice")
        self.assertEqual(len(al["rejected"]), 1)
        self.assertIn("limit", al["rejected"][0]["reason"])

    def test_bad_proofs(self):
        k, other = self.ks[0], self.ks[1]
        self.gist("cc000001", "mallory", k)
        self.assertIn("another account", identity.verify_claim({"pubkey": k.pubkey, "github": "alice", "gist": "cc000001"},
                                                               self.fetch))
        self.gist("cc000002", "alice", other)
        self.assertIn("does not contain", identity.verify_claim({"pubkey": k.pubkey, "github": "alice",
                                                                 "gist": "cc000002"}, self.fetch))
        self.gist("cc000003", "alice", k, public=False)
        self.assertIn("secret", identity.verify_claim({"pubkey": k.pubkey, "github": "alice", "gist": "cc000003"},
                                                      self.fetch))
        self.assertIn("not readable", identity.verify_claim({"pubkey": k.pubkey, "github": "alice", "gist": "zz"},
                                                            self.fetch))
        self.assertEqual("bad GitHub login", identity.verify_claim({"pubkey": k.pubkey, "github": "a b", "gist": "x"},
                                                                   self.fetch))

    def test_metadata_event_and_exports(self):
        k = self.ks[0]
        ev = identity.metadata_event(k, "Alice", "dd000001")
        self.assertEqual(identity.claim_from_metadata(ev)["github"], "alice")
        al = {"pubkeys": [k.pubkey]}
        toml = identity.nostr_rs_relay_toml(al)
        self.assertIn("pubkey_whitelist", toml)
        self.assertIn(k.pubkey, toml)
        self.assertIn("nip42_auth = true", toml)
        plug = identity.strfry_plugin("/srv/allow.json")
        compile(plug, "plugin.py", "exec")
        path = os.path.join(tmpdir(), "al.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(f"# list\n{k.npub}\n")
        self.assertEqual(identity.load_allowlist(path)["pubkeys"], [k.pubkey])

    def test_strfry_plugin_runs(self):
        import subprocess
        import sys
        d = tmpdir()
        alp = os.path.join(d, "allow.json")
        with open(alp, "w", encoding="utf-8") as f:
            json.dump({"pubkeys": [self.ks[0].pubkey]}, f)
        plug = os.path.join(d, "plugin.py")
        with open(plug, "w", encoding="utf-8") as f:
            f.write(identity.strfry_plugin(alp))
        lines = "\n".join(json.dumps({"type": "new", "event": {"id": str(i), "pubkey": k.pubkey}})
                          for i, k in enumerate(self.ks[:2])) + "\n"
        out = subprocess.run([sys.executable, plug], input=lines, capture_output=True, text=True, timeout=30).stdout
        acts = [json.loads(x)["action"] for x in out.splitlines()]
        self.assertEqual(acts, ["accept", "reject"])


if __name__ == "__main__":
    unittest.main()
