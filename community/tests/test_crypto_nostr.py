# SPDX-License-Identifier: AGPL-3.0-or-later
import time
import unittest

from community import keys, nostr, schnorr

# BIP340 official test vectors (bitcoin/bips bip-0340/test-vectors.csv, indices 0 and 1)
V0 = ("0000000000000000000000000000000000000000000000000000000000000003",
      "F9308A019258C31049344F85F89D5229B531C845836F99B08601F113BCE036F9",
      "0000000000000000000000000000000000000000000000000000000000000000",
      "0000000000000000000000000000000000000000000000000000000000000000",
      "E907831F80848D1069A5371B402410364BDF1C5F8307B0084C55F1CE2DCA821525F66A4A85EA8B71E482A74F382D2CE5EBEEE8FDB2172F477DF4900D310536C0")
V1 = ("B7E151628AED2A6ABF7158809CF4F3C762E7160F38B4DA56A784D9045190CFEF",
      "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
      "0000000000000000000000000000000000000000000000000000000000000001",
      "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
      "6896BD60EEAE296DB48A229FF71DFE071BDE413E6D43F917DC8DCF8C78DE33418906D11AC976ABCCB20B091292BFF4EA897EFCB639EA871CFA95F6DE339E4B0A")


class SchnorrTest(unittest.TestCase):
    def test_vectors(self):
        for sk, pk, aux, msg, sig in (V0, V1):
            sk_b, msg_b = bytes.fromhex(sk), bytes.fromhex(msg)
            self.assertEqual(schnorr.pubkey_gen(sk_b).hex().upper(), pk)
            self.assertEqual(schnorr.sign(msg_b, sk_b, bytes.fromhex(aux)).hex().upper(), sig)
            self.assertTrue(schnorr.verify(msg_b, bytes.fromhex(pk), bytes.fromhex(sig)))

    def test_tampered_fails(self):
        sk, pk, aux, msg, sig = V1
        bad = bytearray(bytes.fromhex(sig))
        bad[63] ^= 1
        self.assertFalse(schnorr.verify(bytes.fromhex(msg), bytes.fromhex(pk), bytes(bad)))
        self.assertFalse(schnorr.verify(bytes(32), bytes.fromhex(pk), bytes.fromhex(sig)))

    def test_vector_5_public_key_not_on_curve(self):
        # BIP340 vector 5: EEFDEA4C...34 is not a valid X coordinate
        pk = bytes.fromhex("EEFDEA4CDB677750A420FEE807EACF21EB9898AE79B9768766E4FAA04A2D4A34")
        self.assertFalse(schnorr.verify(bytes(32), pk, bytes(64)))

    def test_bech32_roundtrip(self):
        k = keys.generate()
        self.assertTrue(k.npub.startswith("npub1"))
        self.assertEqual(schnorr.decode_key(k.npub, "npub"), k.pubkey)
        self.assertEqual(schnorr.decode_key(schnorr.nsec(k.seckey), "nsec"), k.seckey)
        with self.assertRaises(ValueError):
            schnorr.decode_key(k.npub, "nsec")
        with self.assertRaises(ValueError):
            schnorr.decode_key(k.npub[:-1] + ("q" if k.npub[-1] != "q" else "p"), "npub")

    def test_nip19_known_npub(self):
        # NIP-19 example
        self.assertEqual(schnorr.npub("7e7e9c42a91bfef19fa929e5fda1b72e0ebc1a4c1141673e2794234d86addf4e"),
                         "npub10elfcs4fr0l0r8af98jlmgdh9c8tcxjvz9qkw038js35mp4dma8qzvjptg")


class EventTest(unittest.TestCase):
    def setUp(self):
        self.k = keys.generate()

    def test_sign_verify_and_tamper(self):
        ev = nostr.sign_event(self.k.seckey, 1, "hello «мир»\n\"q\"", [["t", "freedom"]], 1700000000)
        self.assertTrue(nostr.verify_event(ev))
        for field, val in (("content", "x"), ("kind", 2), ("created_at", 1), ("tags", [])):
            bad = dict(ev, **{field: val})
            self.assertFalse(nostr.verify_event(bad), field)
        self.assertFalse(nostr.verify_event(dict(ev, pubkey=keys.generate().pubkey)))
        self.assertFalse(nostr.verify_event({"id": "x"}))

    def test_serialization_matches_nip01(self):
        ev = {"pubkey": "a" * 64, "created_at": 1, "kind": 1, "tags": [["e", "b" * 64]], "content": "x"}
        self.assertEqual(nostr.serialize(ev), '[0,"' + "a" * 64 + '",1,1,[["e","' + "b" * 64 + '"]],"x"]')

    def test_filters(self):
        ev = nostr.sign_event(self.k.seckey, nostr.KIND_DISCUSSION, "{}", [["e", "c" * 64], ["t", "freedom"]], 100)
        self.assertTrue(nostr.match_filter(ev, {"kinds": [nostr.KIND_DISCUSSION], "#t": ["freedom"]}))
        self.assertTrue(nostr.match_filter(ev, {"authors": [self.k.pubkey], "since": 100, "until": 100}))
        self.assertFalse(nostr.match_filter(ev, {"since": 101}))
        self.assertFalse(nostr.match_filter(ev, {"#e": ["d" * 64]}))
        self.assertFalse(nostr.match_filter(ev, {"ids": ["0" * 64]}))

    def test_nip10_reply_tags(self):
        card = nostr.sign_event(self.k.seckey, nostr.KIND_CARD, "{}", [["d", "u1"]])
        other = keys.generate()
        r1 = nostr.sign_event(other.seckey, nostr.KIND_DISCUSSION, "{}", nostr.reply_tags(card))
        self.assertEqual(nostr.thread_root(r1), card["id"])
        self.assertEqual(nostr.reply_to(r1), card["id"])
        self.assertIn(["a", f"{nostr.KIND_CARD}:{self.k.pubkey}:u1", ""], r1["tags"])
        r2 = nostr.sign_event(self.k.seckey, nostr.KIND_DISCUSSION, "{}", nostr.reply_tags(card, r1))
        self.assertEqual(nostr.thread_root(r2), card["id"])
        self.assertEqual(nostr.reply_to(r2), r1["id"])
        self.assertEqual(nostr.tag_values(r2, "p"), [self.k.pubkey, other.pubkey])

    def test_nip42_auth(self):
        url = "ws://127.0.0.1:7447"
        ev = nostr.auth_event(self.k.seckey, url, "abc")
        self.assertEqual(nostr.check_auth_event(ev, url + "/", "abc"), "")
        self.assertEqual(nostr.check_auth_event(ev, url, "xyz"), "wrong challenge")
        self.assertEqual(nostr.check_auth_event(ev, "ws://other:1", "abc"), "wrong relay")
        self.assertEqual(nostr.check_auth_event(ev, url, "abc", now=int(time.time()) + 3600), "stale auth event")
        self.assertEqual(nostr.norm_relay_url("WSS://Relay.example.org:443/"), "wss://relay.example.org")

    def test_kind_classes(self):
        self.assertTrue(nostr.is_addressable(nostr.KIND_CARD))
        for k in nostr.FREEDOM_KINDS:
            if k != nostr.KIND_CARD:
                self.assertTrue(1000 <= k < 10000, k)
        self.assertTrue(nostr.is_ephemeral(nostr.KIND_AUTH))


class KeyStoreTest(unittest.TestCase):
    def test_memory_store(self):
        s = keys.MemoryKeyStore()
        k, created = keys.load_or_create(s, "wb-x")
        self.assertTrue(created)
        k2, created2 = keys.load_or_create(s, "wb-x")
        self.assertFalse(created2)
        self.assertEqual(k, k2)
        self.assertNotIn(k.seckey, repr(k))

    def test_keyring_store_roundtrip(self):
        try:
            s = keys.KeyringStore("aiwb/nostr-test")
            s.get("probe")
        except Exception as e:  # noqa: BLE001
            self.skipTest(f"no keyring: {e}")
        acct = "wb-unittest"
        try:
            k, created = keys.load_or_create(s, acct)
            self.assertTrue(created)
            self.assertEqual(keys.load(s, acct), k)
        finally:
            s.delete(acct)
        self.assertIsNone(s.get(acct))


if __name__ == "__main__":
    unittest.main()
