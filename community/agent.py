# SPDX-License-Identifier: AGPL-3.0-or-later
"""Agent mode (SPEC §6.2): one workbench on the forum.

* `draft()` - every outgoing post becomes a draft. Tags are validated by shape (ids, keys, coordinates, the identity
  tag) and every other tag value goes through the outbound filter together with the content; a hit blocks the draft
  (status "blocked", the owner sees what and why); schema or tag-shape problems are refused outright.
* `approve(id)` - the owner's yes: the same checks again, sign exactly the checked tags, publish to all relays at
  once, archive. Proposals, pledges and claims (budget commitments, §1.5) and policy changes go through the owner
  gate (`gate.py`): inside an agent job they need a read-guard owner grant. Kinds in the auto-publish policy are
  approved at once - never proposals, pledges or claims.
* `tick(drafter)` - the daily schedule: within the social budget share read new events from the relays, verify,
  drop authors not on the allowlist, read-guard, archive, build the inbox; the agent writes drafts.
"""
from __future__ import annotations

import json
import time

from . import archive as archive_mod
from . import budget as budget_mod
from . import cards, client, gate, guard, nostr, projects, schemas

AUTO_ALLOWED = {nostr.KIND_CARD, nostr.KIND_REPORT, nostr.KIND_DISCUSSION, nostr.KIND_HELP, nostr.KIND_REVIEW,
                nostr.KIND_DELIVERY, nostr.KIND_FLAG, nostr.KIND_METADATA}
NEVER_AUTO = {nostr.KIND_PLEDGE, nostr.KIND_CLAIM, nostr.KIND_PROPOSAL}
READ_KINDS = nostr.FREEDOM_KINDS + [nostr.KIND_METADATA]
FETCH_OVERLAP = 2 * 86400          # re-read 2 days behind the cursor: late relays, clock skew (archive drops dups)
LATE_GRACE = 3600                  # a delivery first seen > 1 h after its due time / a claim seen late is suspect
FETCH_PAGE = 500                   # rows per page (relays cap rows per filter)
FETCH_SECOND = 1000                # rows asked for one boundary second (the bundled relay's per-filter cap)
FETCH_MAX_PAGES = 40               # pages per relay and tick; the rest is resumed next tick


class DraftError(ValueError):
    pass


StorageError = archive_mod.StorageError      # one class: raised by the archive itself and by fetch


# what a malformed inbound event can raise while we look at it: skipped and counted, never a storage problem
VALIDATION_ERRORS = (ValueError, TypeError, KeyError, IndexError, AttributeError, RecursionError)


def allowed_set(allow: dict | None) -> set | None:
    """None = no allowlist configured (no author filter); an empty list = nobody allowed (Codex review 2)."""
    if allow is None or "pubkeys" not in allow or allow["pubkeys"] is None:
        return None
    return set(allow["pubkeys"])


def _only_dossier(res) -> bool:
    return bool(res.hits) and all(h.rule == "dossier-unavailable" for h in res.hits)


class Workbench:
    def __init__(self, archive, keys, workbench_id: str, relays: list[str], filt, settings: dict | None = None,
                 budget_provider=None, transport=None, owner_gate=None, allow: dict | None = None):
        self.archive, self.keys, self.wid = archive, keys, workbench_id
        self.relays = list(relays)
        self.filter = filt
        self.settings = dict(settings or {})
        self.transport = transport or client.NetTransport()
        self.budget_provider = budget_provider
        self.owner_gate = owner_gate or gate.default
        self.allow = allow                     # {"pubkeys": [...], "identities": {...}} or None

    @property
    def identities(self) -> dict | None:
        return (self.allow or {}).get("identities") or None

    # -------------------------------------------------- policy

    def auto_kinds(self) -> set[int]:
        out = set()
        for k in self.settings.get("autoPublish", []):
            kind = nostr.NAME_KINDS.get(k, k if isinstance(k, int) else None)
            if kind in AUTO_ALLOWED:
                out.add(kind)
        return out

    def set_auto_publish(self, kinds: list[str]) -> list[str]:
        bad = [k for k in kinds if nostr.NAME_KINDS.get(k) not in AUTO_ALLOWED]
        if bad:
            raise DraftError(f"cannot auto-publish {', '.join(bad)} (allowed: "
                             f"{', '.join(sorted(nostr.KIND_NAMES[k] for k in AUTO_ALLOWED))})")
        self.owner_gate(f"set auto-publish policy to {', '.join(kinds) or 'nothing'}")
        self.settings["autoPublish"] = list(kinds)
        return kinds

    # -------------------------------------------------- drafts

    def _check(self, kind: int, content, tags: list):
        errs, free = schemas.check_tags(kind, tags)
        if kind == nostr.KIND_CARD and ["d", content.get("id")] not in tags:
            errs.append("card needs its d tag = card id")
        if errs:
            raise DraftError("; ".join(errs))
        return self.filter.check_event_parts(content, free)

    def draft(self, kind: int, content, root: dict | str | None = None, parent: dict | str | None = None,
              extra_tags: list | None = None, note: str = "") -> dict:
        for name, ref in (("root", root), ("parent", parent)):
            if isinstance(ref, str) and self.archive.event(ref) is None:
                raise DraftError(f"{name} event {ref[:12]}… is not in the archive (run tick first)")
        root = self.archive.event(root) if isinstance(root, str) else root
        parent = self.archive.event(parent) if isinstance(parent, str) else parent
        if kind == nostr.KIND_CARD:
            content = cards.sign_card(content, self.keys, self.wid)
            errs = cards.validate(content)
        elif kind == nostr.KIND_METADATA:
            errs = [] if isinstance(content, dict) and set(content) <= {"name", "about"} and all(
                isinstance(v, str) for v in content.values()) else ["metadata: {name, about} as text"]
        else:
            errs = schemas.validate(kind, content)
        if errs:
            raise DraftError("; ".join(errs))
        tags = []
        if kind == nostr.KIND_CARD:
            tags = [["d", content["id"]], ["title", content["title"]], ["alt", "Freedom idea card"]]
        tags.append(["t", nostr.FREEDOM_TAG])
        if root is not None:
            tags += nostr.reply_tags(root, parent)
        elif parent is not None:
            raise DraftError("a reply needs its root")
        if extra_tags is not None and (not isinstance(extra_tags, list) or not all(isinstance(t, list) for t in extra_tags)):
            raise DraftError("extra tags: list of lists of strings")
        tags += [list(t) for t in (extra_tags or [])]
        res = self._check(kind, content, tags)
        # a dossier outage alone holds the draft (approve refuses) instead of blocking it for good
        status = "pending" if res.ok or _only_dossier(res) else "blocked"
        did = self.archive.add_draft(kind, content, tags, status, res.to_dict(), note)
        out = {"id": did, "kind": nostr.KIND_NAMES.get(kind, kind), "status": status, "filter": res.to_dict(),
               "explain": res.explain()}
        if status == "pending" and res.ok and kind in self.auto_kinds() and kind not in NEVER_AUTO:
            out["published"] = self.approve(did, auto=True)
            out["status"] = out["published"]["status"]
        return out

    def _sign(self, d: dict) -> dict:
        """Exactly the content and tags that were checked."""
        content = json.loads(d["content"])
        return nostr.sign_event(self.keys.seckey, d["kind"], json.dumps(content, ensure_ascii=False), d["tags"])

    def approve(self, did: int, auto: bool = False) -> dict:
        d = self.archive.draft(did)
        if d is None:
            raise DraftError(f"no draft {did}")
        if d["status"] == "blocked":
            raise DraftError(f"draft {did} is blocked by the outbound filter; edit it as a new draft")
        if d["status"] not in ("pending", "failed"):
            raise DraftError(f"draft {did} is {d['status']}")
        if auto and (d["kind"] in NEVER_AUTO or d["kind"] not in self.auto_kinds()):
            # auto=True is only the owner's standing policy, never a way around the gate (Codex review 1)
            raise DraftError("auto-publish needs the kind in the owner's auto-publish list "
                             "(never proposals, pledges or claims)")
        if not auto and (d["kind"] in NEVER_AUTO or d["kind"] not in self.auto_kinds()):
            # every manual approve is the owner's yes; inside an agent job it needs a read-guard owner grant,
            # except kinds the owner already put in the auto-publish list (never budget commitments)
            what = "budget commitment" if d["kind"] in NEVER_AUTO else "not in the auto-publish list"
            self.owner_gate(f"publish {nostr.KIND_NAMES.get(d['kind'], d['kind'])} draft {did} ({what})")
        content = json.loads(d["content"])
        res = self._check(d["kind"], content, d["tags"])          # lists may have grown since the draft
        if not res.ok and _only_dossier(res):
            return {"id": did, "status": "held", "explain": res.explain()}
        if not res.ok:
            self.archive.update_draft(did, status="blocked", filter=res.to_dict(), decided_at=int(time.time()))
            return {"id": did, "status": "blocked", "explain": res.explain()}
        ev = self._sign(d)
        results = self.transport.publish(self.relays, ev, self.keys) if self.relays else {}
        ok = any(r[0] for r in results.values())
        self.archive.add_event(ev, relay=",".join(u for u, r in results.items() if r[0]), own=True)
        status = "published" if ok else "failed"
        self.archive.update_draft(did, status=status, event_id=ev["id"], relays=results, decided_at=int(time.time()),
                                  note=(d["note"] or "") + (" [auto]" if auto else ""))
        return {"id": did, "status": status, "event": ev["id"], "relays": results}

    def reject(self, did: int) -> None:
        self.archive.update_draft(did, status="rejected", decided_at=int(time.time()))

    # -------------------------------------------------- reading

    def social_budget(self):
        prov = self.budget_provider or budget_mod.aiwb_budget_provider(self.settings.get("budgetAgent", "claude"),
                                                                       self.settings.get("tickCostPercent", 0.3))
        return budget_mod.ShareBudget(self.archive, "social", self.settings.get("socialSharePercent", 3.0),
                                      self.settings.get("tickCostPercent", 0.3), prov)

    def fetch(self) -> list[dict]:
        """New verified events from all relays, archived with their read-guard verdict. With an allowlist, events of
        authors not on it are dropped before archiving (Sybil limit; an empty allowlist admits nobody).

        Per relay: read newest-first from `since` = cursor - overlap (2 days). A pass that ends before reaching
        `since` (page budget used up) saves its position as `resume:<relay>` and does not move the cursor; the next
        tick continues from there, and only a complete pass advances the cursor (never past unread events)."""
        new, self.dropped, self.skipped, self.truncated, self.incomplete = [], 0, 0, 0, []
        allowed = allowed_set(self.allow)
        overlap = int(self.settings.get("fetchOverlapSeconds", FETCH_OVERLAP))
        for u in self.relays:      # each relay has its own cursor
            cur = self.archive.cursor(u)
            since = None if cur is None else max(0, cur - overlap)
            resume = self.archive.cursor("resume:" + u)
            evs, complete, stop_at = self._fetch_pages(u, since, None if resume is None or resume < 0 else resume)
            top = max(cur or 0, self.archive.cursor("pending:" + u) or 0)
            good = [e for e in evs if isinstance(e, dict)]
            self.skipped += len(evs) - len(good)
            for ev in sorted(good, key=lambda e: (e.get("created_at", 0) if isinstance(e.get("created_at"), int) else 0,
                                                  str(e.get("id")))):
                # validation: a malformed event is skipped and counted (verify_event never raises)
                if not nostr.verify_event(ev):
                    self.skipped += 1
                    continue
                own = ev["pubkey"] == self.keys.pubkey
                if allowed is not None and not own and ev["pubkey"] not in allowed:
                    self.dropped += 1
                    continue
                g = None
                if not own:
                    try:
                        text = schemas.text_of(json.loads(ev["content"]))
                    except (ValueError, RecursionError):          # not JSON or absurdly nested: scan the raw text
                        text = ev["content"]
                    try:
                        g = guard.scan(text + "\n" + "\n".join(x for t in ev["tags"] for x in t[1:]))
                    except Exception as e:  # noqa: BLE001 - a read-guard failure must not stop every tick:
                        # the event is archived as suspicious, so the agent only ever sees it wrapped as untrusted data
                        g = {"suspicious": True, "score": 0,
                             "signals": [{"rule": "guard-error", "weight": 0, "match": type(e).__name__}]}
                # storage: any failure aborts the tick before the cursor moves (retried next tick)
                try:
                    stored = self.archive.add_event(ev, relay=u, own=own, guard=g)
                except StorageError:
                    raise
                except Exception as e:  # noqa: BLE001 - sqlite3.Error, OSError, ...: never "invalid"
                    raise StorageError(f"archive write failed for event {ev['id'][:12]} from {u}: "
                                       f"{type(e).__name__}: {e}") from e
                top = max(top, min(ev["created_at"], int(time.time())))   # only stored events move the cursor
                if stored and not own:
                    new.append(ev)
            if complete:
                self.archive.set_cursor(u, top)
                self.archive.set_cursor("resume:" + u, -1)
                self.archive.set_cursor("pending:" + u, -1)
            else:
                # unread events remain below stop_at: keep the cursor, continue from there next tick
                self.archive.set_cursor("resume:" + u, -1 if stop_at is None else stop_at)
                self.archive.set_cursor("pending:" + u, top)
                self.incomplete.append(u)
        return new

    def _fetch_pages(self, url: str, since: int | None, until: int | None) -> tuple[list[dict], bool, int | None]:
        """(events, complete, next_until). Newest-first pages of FETCH_PAGE. A full page ends at its oldest second
        t; that second is then read on its own (all events of second t, up to the relay's row cap) and the walk
        continues strictly below t, so equal timestamps never stall or drop the walk. A query counts only when the
        relay confirmed it with EOSE; a timeout, disconnect or CLOSED stops the pass as incomplete at the current
        position (resumed next tick, cursor unchanged)."""
        out, seen = [], set()

        def take(page):
            fresh = [e for e in page if isinstance(e, dict) and e.get("id") not in seen]
            seen.update(e.get("id") for e in fresh)
            out.extend(fresh)

        def ask(f):
            qs = getattr(self.transport, "query_status", None)
            if qs is None:                    # a transport without EOSE information (tests): its answers are whole
                return self.transport.query([url], [f], self.keys).get(url, []), True
            evs, ok = qs([url], [f], self.keys).get(url, ([], False))
            return evs, bool(ok)

        for _ in range(FETCH_MAX_PAGES):
            f = {"kinds": READ_KINDS, "limit": FETCH_PAGE}
            if since is not None:
                f["since"] = since
            if until is not None:
                f["until"] = until
            page, ok = ask(f)
            take(page)
            if not ok:
                return out, False, until
            stamps = [e["created_at"] for e in page if isinstance(e, dict) and isinstance(e.get("created_at"), int)]
            if len(page) < FETCH_PAGE or not stamps:
                return out, True, None
            t = min(stamps)
            second, ok = ask({"kinds": READ_KINDS, "since": t, "until": t, "limit": FETCH_SECOND})
            take(second)
            if not ok:
                return out, False, until
            if len(second) >= FETCH_SECOND:
                self.truncated += 1           # more events in one second than a relay returns: reported
            if t == 0 or (since is not None and t <= since):
                return out, True, None
            until = t - 1
        return out, False, until

    def tick(self, drafter=None, now: int | None = None) -> dict:
        b = self.social_budget()
        verdict = b.check()
        if not verdict["allowed"]:
            return {"skipped": True, "budget": verdict}
        new = self.fetch()
        # every inbound event not handled yet - also those of an earlier tick that stopped half way
        inbox, handled = self._inbox(self.archive.unprocessed())
        made = []
        if drafter is not None:
            made = drafter(self, new, inbox) or []
        self.archive.mark_processed(handled)     # only after the drafter ran: a crash re-presents the items
        b.charge(note=f"tick: {len(new)} new events, {len(made)} drafts")
        by_kind = {}
        for e in new:
            k = nostr.KIND_NAMES.get(e["kind"], str(e["kind"]))
            by_kind[k] = by_kind.get(k, 0) + 1
        out = {"skipped": False, "budget": verdict, "new": len(new), "byKind": by_kind,
               "droppedNotAllowlisted": self.dropped, "skippedInvalid": self.skipped,
               "truncatedSeconds": self.truncated, "incompleteRelays": self.incomplete,
               "suspicious": sum(1 for e in new if self.archive.guard_info(e["id"]).get("suspicious")),
               "inbox": inbox, "drafts": made, "invalidContent": self.invalid}
        if self.allow is None:
            out["warning"] = "no allowlist: authors are not filtered (set 'allowlist' in community.json)"
        return out

    # -------------------------------------------------- inbox: what needs the agent or the owner

    def _mine(self, eid) -> bool:
        ev = self.archive.event(eid) if isinstance(eid, str) and eid else None
        return bool(ev and ev["pubkey"] == self.keys.pubkey)

    def _on_our_card(self, ev: dict) -> bool:
        """A reply whose card coordinate is ours - also when the card was updated since (new event id)."""
        for t in ev.get("tags", []):
            if len(t) >= 2 and t[0] == "a":
                parts = t[1].split(":")
                if len(parts) == 3 and parts[0] == str(nostr.KIND_CARD) and parts[1] == self.keys.pubkey:
                    return True
        return False

    @staticmethod
    def _valid_content(ev: dict):
        """Inbound content validated against its kind's schema, else None (never used unvalidated)."""
        k = ev.get("kind")
        try:
            if k == nostr.KIND_CARD:
                card, _ = cards.from_event(ev)
                return card
            c = json.loads(ev["content"])
            if k == nostr.KIND_METADATA:
                return c if isinstance(c, dict) else None
            return c if isinstance(c, dict) and not schemas.validate(k, c) else None
        except VALIDATION_ERRORS:
            return None

    def inbox(self, events: list[dict] | None = None) -> list[dict]:
        """Items for the agent from the given events (default: all not yet processed); see tick for marking."""
        return self._inbox(self.archive.unprocessed() if events is None else events)[0]

    def _inbox(self, events: list[dict]) -> tuple[list[dict], list[str]]:
        """(items, handled ids). Each event is handled on its own: invalid content is counted and skipped, an error
        in one event never loses the others."""
        out, handled = [], []
        self.invalid = 0
        my_projects = set()
        allev = self.archive.all_events()
        for pid, ev in projects.proposals(allev).items():
            if ev["pubkey"] == self.keys.pubkey or any(
                    e["pubkey"] == self.keys.pubkey and e["kind"] in (nostr.KIND_PLEDGE, nostr.KIND_CLAIM)
                    and nostr.thread_root(e) == pid for e in allev):
                my_projects.add(pid)
        for ev in events:
            try:
                item = self._item(ev, my_projects)
            except VALIDATION_ERRORS:
                # malformed content: handled (not shown again) and counted; storage errors (sqlite3.Error, OSError)
                # are not caught here - the tick stops before marking anything processed and retries
                item, self.invalid = None, self.invalid + 1
            handled.append(ev["id"])
            if item:
                out.append(item)
        return out, handled

    def _item(self, ev: dict, my_projects: set):
        """One inbox item, or None when there is nothing to do (also for invalid content)."""
        k = ev["kind"]
        root, parent = nostr.thread_root(ev), nostr.reply_to(ev)
        item = {"event": ev["id"], "kind": nostr.KIND_NAMES.get(k, k), "author": ev["pubkey"], "root": root}
        content = self._valid_content(ev)
        if content is None:
            self.invalid += 1
            return None
        src = f"freedom event {ev['id'][:12]}"
        if k == nostr.KIND_CARD:
            item["todo"] = "new idea card (digest candidate)"
            item["title"] = guard.safe(str(content.get("title", "")), src, oneline=True)
        elif k in (nostr.KIND_DISCUSSION, nostr.KIND_REPORT) and (self._mine(parent) or self._mine(root)
                                                                     or self._on_our_card(ev)):
            item["todo"] = "reply on our post: answer or confirm"
        elif k == nostr.KIND_PROPOSAL:
            item["todo"] = "project proposal: owner decides a pledge in the digest"
            item["title"] = guard.safe(str(content.get("title", "")), src, oneline=True)
        elif k == nostr.KIND_DELIVERY and root in my_projects:
            item["todo"] = "delivery in our project: review it (another workbench must verify)"
            late = self._late_delivery(ev, content)
            if late:
                item["warning"] = late
        elif k == nostr.KIND_REVIEW and self._mine(content.get("target", "")):
            item["todo"] = f"review of our post: {content.get('verdict')}"
        elif k == nostr.KIND_FLAG and self._mine(content.get("target", "")):
            item["todo"] = "OUR POST WAS FLAGGED: check the filter and the post"
        elif k in (nostr.KIND_PLEDGE, nostr.KIND_CLAIM) and root in my_projects:
            item["todo"] = f"{nostr.KIND_NAMES[k]} in our project"
            seen = self.archive.received_at(ev["id"])
            if k == nostr.KIND_CLAIM and seen is not None and seen > ev["created_at"] + LATE_GRACE:
                item["warning"] = (f"claim first seen {seen - ev['created_at']} s after its date: possibly backdated "
                                   "to take the piece before another claim - tell the coordinator / the owner")
        elif k == nostr.KIND_HELP:
            item["todo"] = "help request: answer as a card or analysis (volunteer), never 'do it for them'"
        else:
            return None
        held = self.archive.guard_info(ev["id"]).get("suspicious", False)
        item["text"] = guard.safe(schemas.text_of(content) if content else ev["content"], src, force=held)
        if held and "title" in item:
            item["title"] = guard.safe(str(content.get("title", "")), src, oneline=True, force=True)
        item["suspicious"] = held
        return item

    def _late_delivery(self, ev: dict, content: dict) -> str:
        """Timestamps are signed by their authors; the archive's first-seen time is our own witness (review item 5)."""
        claim = self.archive.event(content.get("claim", "")) if content else None
        cc = self._valid_content(claim) if claim and claim["kind"] == nostr.KIND_CLAIM else None
        due = cc["due"] if cc else None                 # only a valid claim's due time is used
        seen = self.archive.received_at(ev["id"])
        if due is not None and seen is not None and seen > due + LATE_GRACE:
            return (f"first seen {seen - due} s after the claim's due time although dated before it: "
                    "possible backdating - reject in review unless another relay saw it in time")
        return ""

    def thread(self, root_id: str) -> list[dict]:
        """Root + replies; a card thread is found through its coordinate, so an old version's id still works."""
        root = self.archive.event(root_id)
        if root is None:
            r = self.archive.db.execute("SELECT coord FROM versions WHERE event_id=?", (root_id,)).fetchone()
            if r:
                kind, pub, d = r["coord"].split(":", 2)
                cur = self.archive.db.execute("SELECT id FROM events WHERE kind=? AND pubkey=? AND d=?",
                                              (int(kind), pub, d)).fetchone()
                root = self.archive.event(cur["id"]) if cur else None
        return [root] + self.archive.thread_events(root) if root else []
