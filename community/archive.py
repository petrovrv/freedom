# SPDX-License-Identifier: AGPL-3.0-or-later
"""Local archive of one workbench (SPEC §6.1: relays may drop events, so each workbench keeps its own copy).

One SQLite file: every verified event seen or published (with the read-guard verdict of inbound text), the cards,
outgoing drafts waiting for the owner's yes, digest decisions, relay cursors and the social/volunteer budget ledger.
"""
from __future__ import annotations

import json
import sqlite3
import time
from pathlib import Path

from . import cards, nostr


class StorageError(RuntimeError):
    """The archive (SQLite) or the disk failed, or a stored row is corrupt. The tick stops; cursor, resume position
    and processed marks stay, the next tick retries. Never counted as an invalid event."""

SCHEMA = """
CREATE TABLE IF NOT EXISTS events (
  id TEXT PRIMARY KEY, pubkey TEXT NOT NULL, kind INTEGER NOT NULL, created_at INTEGER NOT NULL,
  content TEXT NOT NULL, tags TEXT NOT NULL, sig TEXT NOT NULL,
  received_at INTEGER NOT NULL, relay TEXT, own INTEGER NOT NULL DEFAULT 0,
  guard_suspicious INTEGER NOT NULL DEFAULT 0, guard_signals TEXT, root TEXT, d TEXT
);
CREATE INDEX IF NOT EXISTS events_kind ON events(kind, created_at);
CREATE INDEX IF NOT EXISTS events_root ON events(root);
CREATE TABLE IF NOT EXISTS versions (
  coord TEXT NOT NULL, event_id TEXT NOT NULL, PRIMARY KEY (coord, event_id)
);
CREATE TABLE IF NOT EXISTS processed (event_id TEXT PRIMARY KEY, at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS cards (
  author TEXT NOT NULL, uuid TEXT NOT NULL, event_id TEXT NOT NULL, title TEXT NOT NULL,
  json TEXT NOT NULL, created_at INTEGER NOT NULL, own INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (author, uuid)
);
CREATE TABLE IF NOT EXISTS drafts (
  id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, content TEXT NOT NULL, tags TEXT NOT NULL,
  created_at INTEGER NOT NULL, status TEXT NOT NULL, filter TEXT, note TEXT,
  event_id TEXT, relays TEXT, decided_at INTEGER
);
CREATE TABLE IF NOT EXISTS decisions (
  card TEXT PRIMARY KEY, choice TEXT NOT NULL, decided_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS cursors (relay TEXT PRIMARY KEY, since INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS ledger (
  id INTEGER PRIMARY KEY AUTOINCREMENT, category TEXT NOT NULL, week_start INTEGER NOT NULL,
  percent REAL NOT NULL, at INTEGER NOT NULL, note TEXT
);
CREATE TABLE IF NOT EXISTS digests (
  id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, cards TEXT NOT NULL, markdown TEXT NOT NULL
);
"""

DRAFT_STATUSES = ("pending", "blocked", "published", "rejected", "failed")


class Archive:
    def __init__(self, path: str | Path):
        self.path = str(path)
        if self.path != ":memory:":
            Path(self.path).parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(self.path)
        self.db.row_factory = sqlite3.Row
        self.db.executescript(SCHEMA)
        self._migrate()

    def _migrate(self) -> None:
        """Archives from 0.1.0-0.1.3: add events.coord, back-fill it, mark already-seen events processed. One
        transaction per open, idempotent: a crash in the middle is simply redone on the next open (round-4 audit)."""
        try:
            cols = {r["name"] for r in self.db.execute("PRAGMA table_info(events)")}
            if "coord" not in cols:
                self.db.execute("ALTER TABLE events ADD COLUMN coord TEXT")
            for r in self.db.execute("SELECT id, kind, pubkey, d, tags FROM events WHERE coord IS NULL").fetchall():
                c = self._coord_of(r["kind"], r["pubkey"], r["d"], json.loads(r["tags"]))
                if c is not None:
                    self.db.execute("UPDATE events SET coord=? WHERE id=?", (c, r["id"]))
            self.db.execute("CREATE INDEX IF NOT EXISTS events_coord ON events(coord)")
            seeded = self.db.execute("SELECT value FROM meta WHERE key='processed_seeded'").fetchone()
            if not seeded:
                # no committed seeding yet: every event already here predates the idempotent inbox (an older
                # archive, or a crash before this commit) and was shown before; a fresh archive has none
                self.db.execute("INSERT OR IGNORE INTO processed SELECT id, ? FROM events", (int(time.time()),))
                self.db.execute("INSERT INTO meta VALUES ('processed_seeded', '1')")
            self.db.commit()
        except Exception:
            self.db.rollback()
            raise

    @staticmethod
    def _coord_of(kind, pubkey, d, tags) -> str | None:
        """Stable thread key: an addressable event's own kind:pubkey:d, else the first "a" tag it replies to."""
        if nostr.is_addressable(kind):
            return f"{kind}:{pubkey}:{d or ''}"
        for t in tags:
            if isinstance(t, list) and len(t) >= 2 and t[0] == "a" and isinstance(t[1], str):
                return t[1]
        return None

    def close(self):
        self.db.close()

    # -------------------------------------------------- events

    def add_event(self, ev: dict, relay: str = "", own: bool = False, guard: dict | None = None) -> bool:
        """Store a verified event; returns True when it is new. Addressable events keep only the newest version.
        One transaction: on any error nothing of it stays (no half-replaced card)."""
        if not nostr.verify_event(ev):
            return False
        try:
            new = self._add_event(ev, relay, own, guard)
            self.db.commit()
            return new
        except Exception:
            self.db.rollback()
            raise

    def _add_event(self, ev, relay, own, guard) -> bool:
        d = nostr.d_tag(ev) if nostr.is_addressable(ev["kind"]) else None
        coord = self._coord_of(ev["kind"], ev["pubkey"], d, ev["tags"])
        if d is not None:
            old = self.db.execute("SELECT id, created_at FROM events WHERE kind=? AND pubkey=? AND d=?",
                                  (ev["kind"], ev["pubkey"], d)).fetchone()
            # newest wins; equal created_at: the lower id wins (NIP-01), so every archive keeps the same version
            if old and (old["created_at"] > ev["created_at"] or old["id"] == ev["id"] or
                        (old["created_at"] == ev["created_at"] and old["id"] < ev["id"])):
                return False
            if old:
                # the old version goes, but its id stays known: replies to it still belong to the thread
                self.db.execute("INSERT OR IGNORE INTO versions VALUES (?,?)", (coord, old["id"]))
                self.db.execute("DELETE FROM events WHERE id=?", (old["id"],))
        cur = self.db.execute(
            "INSERT OR IGNORE INTO events (id,pubkey,kind,created_at,content,tags,sig,received_at,relay,own,"
            "guard_suspicious,guard_signals,root,d,coord) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            (ev["id"], ev["pubkey"], ev["kind"], ev["created_at"], ev["content"], json.dumps(ev["tags"]), ev["sig"],
             int(time.time()), relay, int(own), int(bool(guard and guard.get("suspicious"))),
             json.dumps(guard.get("signals")) if guard else None, nostr.thread_root(ev) or None, d, coord))
        new = cur.rowcount > 0
        if new and d is not None:
            self.db.execute("INSERT OR IGNORE INTO versions VALUES (?,?)", (coord, ev["id"]))
        if new and ev["kind"] == nostr.KIND_CARD:
            card, errs = cards.from_event(ev)
            if card:
                # keyed by (author, uuid): reusing someone else's uuid cannot take over their card (review item 4)
                self.db.execute("INSERT OR REPLACE INTO cards (uuid,event_id,author,title,json,created_at,own) "
                                "VALUES (?,?,?,?,?,?,?)", (card["id"], ev["id"], ev["pubkey"], card["title"],
                                                           json.dumps(card, ensure_ascii=False), ev["created_at"], int(own)))
        return new

    @staticmethod
    def _ev(row) -> dict:
        try:
            tags = json.loads(row["tags"])
            if not isinstance(tags, list):
                raise ValueError("tags is not a list")
        except (ValueError, TypeError, RecursionError) as e:
            raise StorageError(f"corrupt archive row {row['id'][:12]}: {e}") from None
        return {"id": row["id"], "pubkey": row["pubkey"], "kind": row["kind"], "created_at": row["created_at"],
                "content": row["content"], "tags": tags, "sig": row["sig"]}

    def events(self, kinds=None, root=None, since=None, author=None) -> list[dict]:
        q, a = "SELECT * FROM events WHERE 1=1", []
        if kinds:
            q += f" AND kind IN ({','.join('?' * len(kinds))})"
            a += list(kinds)
        if root:
            q += " AND root=?"
            a.append(root)
        if since is not None:
            q += " AND created_at>=?"
            a.append(since)
        if author:
            q += " AND pubkey=?"
            a.append(author)
        return [self._ev(r) for r in self.db.execute(q + " ORDER BY created_at, id", a)]

    def event(self, eid: str) -> dict | None:
        if not isinstance(eid, str):                  # ids from inbound content must never reach SQL untyped
            return None
        r = self.db.execute("SELECT * FROM events WHERE id=?", (eid,)).fetchone()
        return self._ev(r) if r else None

    def thread_events(self, root: dict) -> list[dict]:
        """Replies of a thread. For an addressable root (a card) by its coordinate and every version id ever seen,
        so updating the card keeps its discussion (Codex review 3)."""
        if not nostr.is_addressable(root["kind"]):
            return self.events(root=root["id"])
        coord = nostr.address(root)
        ids = {r["event_id"] for r in self.db.execute("SELECT event_id FROM versions WHERE coord=?", (coord,))}
        ids.add(root["id"])
        marks = ",".join("?" * len(ids))
        rows = self.db.execute(f"SELECT * FROM events WHERE (root IN ({marks}) OR coord=?) AND id NOT IN ({marks}) "
                               "ORDER BY created_at, id", [*ids, coord, *ids])
        return [self._ev(r) for r in rows]

    def version_ids(self, coord: str) -> set[str]:
        return {r["event_id"] for r in self.db.execute("SELECT event_id FROM versions WHERE coord=?", (coord,))}

    def unprocessed(self) -> list[dict]:
        """Inbound events the inbox has not handled yet (idempotent inbox, Codex review 2)."""
        rows = self.db.execute("SELECT * FROM events WHERE own=0 AND id NOT IN (SELECT event_id FROM processed) "
                               "ORDER BY created_at, id")
        return [self._ev(r) for r in rows]

    def mark_processed(self, ids) -> None:
        now = int(time.time())
        with self.db:                                   # commit, or roll back on error
            self.db.executemany("INSERT OR IGNORE INTO processed VALUES (?,?)", [(i, now) for i in ids])

    def received_at(self, eid: str) -> int | None:
        """When this archive first saw the event (our own witness; authors sign their own created_at)."""
        if not isinstance(eid, str):
            return None
        r = self.db.execute("SELECT received_at FROM events WHERE id=?", (eid,)).fetchone()
        return r["received_at"] if r else None

    def guard_info(self, eid: str) -> dict:
        r = self.db.execute("SELECT guard_suspicious, guard_signals FROM events WHERE id=?", (eid,)).fetchone()
        return {"suspicious": bool(r["guard_suspicious"]), "signals": json.loads(r["guard_signals"] or "null")} if r else {}

    def all_events(self) -> list[dict]:
        return self.events()

    # -------------------------------------------------- cards

    def cards(self, own: bool | None = None) -> list[dict]:
        q = "SELECT json FROM cards" + ("" if own is None else f" WHERE own={int(own)}") + " ORDER BY created_at"
        return [json.loads(r["json"]) for r in self.db.execute(q)]

    def card_authors(self, uuid: str) -> list[str]:
        return [r["author"] for r in self.db.execute("SELECT author FROM cards WHERE uuid=? ORDER BY author", (uuid,))]

    def card_event(self, uuid: str, author: str | None = None) -> dict | None:
        """Card event by uuid and author; without an author: our own card, else the earliest one with that uuid
        (user-facing commands refuse a bare uuid used by several authors, see card_authors)."""
        if author:
            r = self.db.execute("SELECT event_id FROM cards WHERE uuid=? AND author=?", (uuid, author)).fetchone()
        else:
            r = self.db.execute("SELECT event_id FROM cards WHERE uuid=? ORDER BY own DESC, created_at, author",
                                (uuid,)).fetchone()
        return self.event(r["event_id"]) if r else None

    # -------------------------------------------------- drafts

    def add_draft(self, kind: int, content, tags: list, status: str, filt: dict | None, note: str = "") -> int:
        cur = self.db.execute("INSERT INTO drafts (kind,content,tags,created_at,status,filter,note) VALUES (?,?,?,?,?,?,?)",
                              (kind, json.dumps(content, ensure_ascii=False) if not isinstance(content, str) else content,
                               json.dumps(tags), int(time.time()), status, json.dumps(filt) if filt else None, note))
        self.db.commit()
        return cur.lastrowid

    def draft(self, did: int) -> dict | None:
        r = self.db.execute("SELECT * FROM drafts WHERE id=?", (did,)).fetchone()
        if not r:
            return None
        d = dict(r)
        d["tags"] = json.loads(d["tags"])
        d["filter"] = json.loads(d["filter"]) if d["filter"] else None
        d["relays"] = json.loads(d["relays"]) if d["relays"] else None
        return d

    def drafts(self, status: str | None = None) -> list[dict]:
        q = "SELECT id FROM drafts" + (" WHERE status=?" if status else "") + " ORDER BY id"
        return [self.draft(r["id"]) for r in self.db.execute(q, (status,) if status else ())]

    def update_draft(self, did: int, **kw):
        cols = ", ".join(f"{k}=?" for k in kw)
        vals = [json.dumps(v) if k in ("relays", "filter") and v is not None else v for k, v in kw.items()]
        self.db.execute(f"UPDATE drafts SET {cols} WHERE id=?", vals + [did])
        self.db.commit()

    # -------------------------------------------------- decisions, cursors, ledger, digests

    def decide(self, card: str, choice: str):
        self.db.execute("INSERT OR REPLACE INTO decisions VALUES (?,?,?)", (card, choice, int(time.time())))
        self.db.commit()

    def decisions(self) -> dict:
        return {r["card"]: r["choice"] for r in self.db.execute("SELECT * FROM decisions")}

    def cursor(self, relay: str) -> int | None:
        r = self.db.execute("SELECT since FROM cursors WHERE relay=?", (relay,)).fetchone()
        return r["since"] if r else None

    def set_cursor(self, relay: str, since: int):
        self.db.execute("INSERT OR REPLACE INTO cursors VALUES (?,?)", (relay, since))
        self.db.commit()

    def spend(self, category: str, week_start: int, percent: float, note: str = ""):
        self.db.execute("INSERT INTO ledger (category,week_start,percent,at,note) VALUES (?,?,?,?,?)",
                        (category, week_start, percent, int(time.time()), note))
        self.db.commit()

    def spent(self, category: str, week_start: int) -> float:
        r = self.db.execute("SELECT COALESCE(SUM(percent),0) s FROM ledger WHERE category=? AND week_start=?",
                            (category, week_start)).fetchone()
        return float(r["s"])

    def add_digest(self, card_ids: list[str], markdown: str):
        self.db.execute("INSERT INTO digests (at,cards,markdown) VALUES (?,?,?)",
                        (int(time.time()), json.dumps(card_ids), markdown))
        self.db.commit()

    def digested(self) -> set[str]:
        out = set()
        for r in self.db.execute("SELECT cards FROM digests"):
            out.update(json.loads(r["cards"]))
        return out


def _storage_errors(fn):
    """sqlite3 / OS errors of an archive method become StorageError (one error type for the tick to stop on)."""
    import functools

    @functools.wraps(fn)
    def wrapper(*a, **k):
        try:
            return fn(*a, **k)
        except StorageError:
            raise
        except (sqlite3.Error, OSError) as e:
            raise StorageError(f"archive {fn.__name__}: {type(e).__name__}: {e}") from e
    return wrapper


for _name in ("add_event", "events", "event", "thread_events", "version_ids", "unprocessed", "mark_processed",
              "received_at", "guard_info", "all_events", "cards", "card_authors", "card_event", "add_draft", "draft",
              "drafts", "update_draft", "decide", "decisions", "cursor", "set_cursor", "spend", "spent", "add_digest",
              "digested"):
    setattr(Archive, _name, _storage_errors(getattr(Archive, _name)))
