# SPDX-License-Identifier: AGPL-3.0-or-later
"""Final review nits in agent mode: a read-guard failure holds the event (suspicious) instead of stopping every
tick; a corrupt archive row and cursor/SQLite errors are StorageError (the tick stops, nothing moves)."""
import sqlite3
import unittest
from unittest import mock

from community import agent, archive, guard, nostr
from community.tests.helpers import MemTransport, sample_card, workbench


class GuardFailureTest(unittest.TestCase):
    def test_scan_failure_holds_event_as_suspicious(self):
        t = MemTransport()
        a, b = workbench("ga", transport=t), workbench("gb", transport=t)
        a.approve(a.draft(nostr.KIND_CARD, sample_card())["id"])
        with mock.patch.object(guard, "scan", side_effect=RuntimeError("read-guard broke")):
            rep = b.tick()                                    # no exception: the tick goes on
        self.assertEqual(rep["new"], 1)
        ev = b.archive.events(kinds=[nostr.KIND_CARD])[0]
        self.assertTrue(b.archive.guard_info(ev["id"])["suspicious"])
        item = rep["inbox"][0]
        self.assertTrue(item["suspicious"])
        self.assertIn("UNTRUSTED", item["text"])              # shown only as untrusted data
        self.assertIn("UNTRUSTED", item["title"])

    def test_present_wraps_when_read_guard_fails(self):
        with mock.patch.object(guard, "scan", side_effect=RuntimeError("x")):
            self.assertIn("UNTRUSTED", guard.present("hello", "src"))
        self.assertIn("UNTRUSTED", guard.present("plain text", "src", force=True))
        self.assertEqual(guard.present("plain text", "src"), "plain text")


class StorageErrorTest(unittest.TestCase):
    def setUp(self):
        self.t = MemTransport()
        self.a = workbench("sa", transport=self.t)
        self.b = workbench("sb", transport=self.t)
        self.a.approve(self.a.draft(nostr.KIND_CARD, sample_card())["id"])

    def test_corrupt_row_is_storage_error(self):
        self.b.tick()
        ev = self.b.archive.events(kinds=[nostr.KIND_CARD])[0]
        self.b.archive.db.execute("UPDATE events SET tags='{broken' WHERE id=?", (ev["id"],))
        self.b.archive.db.commit()
        with self.assertRaises(archive.StorageError):
            self.b.archive.event(ev["id"])
        cur = self.b.archive.cursor("mem://r1")
        with self.assertRaises(agent.StorageError):           # same class in agent and archive
            self.b.tick()
        self.assertEqual(self.b.archive.cursor("mem://r1"), cur)

    def test_cursor_error_is_storage_error(self):
        real = self.b.archive.db

        class FailingCursors:
            def execute(self, sql, *a):
                if "cursors" in sql:
                    raise sqlite3.OperationalError("database is locked")
                return real.execute(sql, *a)

            def __getattr__(self, n):
                return getattr(real, n)
        self.b.archive.db = FailingCursors()
        try:
            with self.assertRaises(agent.StorageError):
                self.b.tick()
        finally:
            self.b.archive.db = real
        self.assertEqual(self.b.tick()["new"], 1)             # storage back: the tick reads the card


if __name__ == "__main__":
    unittest.main()
