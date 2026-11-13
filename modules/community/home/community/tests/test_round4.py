# SPDX-License-Identifier: AGPL-3.0-or-later
"""Codex round 4 (freedom-community-20261112-110836): strong secret keys block any non-placeholder value;
replace-in-place writes are single transactions (relay store, archive, archive migration)."""
import json
import os
import sqlite3
import unittest

from community import archive, cards, keys, nostr, outfilter, relay, schemas
from community.tests.helpers import sample_card, tmpdir

T0 = 1_780_000_000


class StrongKeyTest(unittest.TestCase):
    def setUp(self):
        self.f = outfilter.generic_filter()

    def test_any_shape_blocked(self):
        for t in ['password: "violet meadow silver river"', 'DB_PASSWORD="a/b/cdefghi"', "secret: aiwb/nostr",
                  "pwd=lowercaseonly", "api_key: plainwords", "client_secret: 'two words'", "token: abcd",
                  '{"private_key": "x y z w"}', "passwd: ab/cd", "PASSWORD=Hunter 2 Go", "pwd: abc"]:
            self.assertIn("secret", self.f.check(t).rules(), t)

    def test_explicit_placeholders_pass(self):
        for t in ["password: <your password>", "password: ${DB_PASSWORD}", 'token: "<token>"', "api_key: none",
                  "secret: ***", "password: changeme", "password: %PASSWORD%", "token: {{ token }}", "secret: xxx",
                  "password: redacted", "token: n/a"]:
            r = self.f.check(t)
            self.assertTrue(r.ok, f"{t!r}: {r.explain()}")


class _FailingDB:
    """Proxy for a sqlite3 connection whose executemany fails (after the deletes already ran)."""

    def __init__(self, real):
        self.real = real

    def executemany(self, *a, **k):
        raise sqlite3.OperationalError("disk I/O error")

    def __getattr__(self, name):
        return getattr(self.real, name)


class TransactionTest(unittest.TestCase):
    def setUp(self):
        self.k = keys.generate()
        self.u = schemas.new_uuid()

    def card_ev(self, t, v):
        return nostr.sign_event(self.k.seckey, nostr.KIND_CARD, json.dumps({"v": v}), [["d", self.u], ["t", "freedom"]], t)

    def test_relay_replace_is_atomic(self):
        st = relay.Store(os.path.join(tmpdir(), "r.sqlite"))
        v1, v2 = self.card_ev(T0, 1), self.card_ev(T0 + 10, 2)
        st.save(v1)
        real = st.db
        st.db = _FailingDB(real)
        with self.assertRaises(sqlite3.OperationalError):
            st.save(v2)
        st.db = real
        self.assertEqual([e["id"] for e in st.query({"kinds": [nostr.KIND_CARD]})], [v1["id"]])   # old one intact
        self.assertEqual(real.execute("SELECT COUNT(*) FROM tags WHERE id=?", (v1["id"],)).fetchone()[0], 2)
        self.assertEqual(st.save(v2), "")
        self.assertEqual([e["id"] for e in st.query({"kinds": [nostr.KIND_CARD]})], [v2["id"]])

    def test_archive_replace_is_atomic(self):
        a = archive.Archive(os.path.join(tmpdir(), "a.sqlite"))
        c = cards.sign_card(sample_card(), self.k, "wb")
        v1 = cards.to_event(c, self.k, created_at=T0)
        a.add_event(v1)
        v2 = cards.to_event(cards.sign_card(dict(c, result="r2"), self.k, "wb"), self.k, created_at=T0 + 10)
        real = a.db

        class FailOnCards:
            def __init__(self, r):
                self.r = r

            def execute(self, sql, *args):
                if sql.startswith("INSERT OR REPLACE INTO cards"):
                    raise sqlite3.OperationalError("disk full")
                return self.r.execute(sql, *args)

            def __getattr__(self, n):
                return getattr(self.r, n)
        a.db = FailOnCards(real)
        with self.assertRaises(archive.StorageError):         # sqlite errors surface as StorageError
            a.add_event(v2)
        a.db = real
        self.assertEqual([e["id"] for e in a.events(kinds=[nostr.KIND_CARD])], [v1["id"]])
        self.assertEqual(a.version_ids(nostr.address(v1)), {v1["id"]})        # no half-written version row

    def test_migration_is_redone_after_a_crash(self):
        path = os.path.join(tmpdir(), "m.sqlite")
        a = archive.Archive(path)
        c = cards.sign_card(sample_card(), self.k, "wb")
        ce = cards.to_event(c, self.k)
        a.add_event(ce)
        a.close()
        db = sqlite3.connect(path)       # crash state: coord column there but empty, seeding never committed
        db.executescript("UPDATE events SET coord=NULL; DELETE FROM processed; DELETE FROM meta;")
        db.commit()
        db.close()
        b = archive.Archive(path)
        self.assertEqual(b.db.execute("SELECT coord FROM events WHERE id=?", (ce["id"],)).fetchone()[0],
                         nostr.address(ce))
        self.assertEqual(b.unprocessed(), [])


if __name__ == "__main__":
    unittest.main()
