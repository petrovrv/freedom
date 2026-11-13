# SPDX-License-Identifier: AGPL-3.0-or-later
"""Shared projects (SPEC §7.3) computed only from signed events, so every workbench derives the same state.

A project is its proposal event (keyed by the proposal's event id, not by the uuid in it: a second proposal reusing
a uuid is a different project and cannot take over). proposal -> pledges (owner-confirmed, published after the
owner's yes) -> claims (a piece per pledger; a piece held by an unexpired claim cannot be claimed again) -> delivery
before the claim's due time, public artifact -> review by another workbench (accept / reject) -> piece accepted;
all pieces accepted = project accepted. A claim without an accepted delivery whose due time passed returns its piece
to the pool and is "missed".

Time order is enforced: pledge, claim >= proposal; delivery >= its claim; review >= the delivery. Trust model:
created_at is signed by each author, so a delivery could be backdated between its claim and the due time; every
workbench records its own first-seen time (archive.received_at) and the reviewer rejects a delivery first seen after
the due time (Workbench.inbox warns). The verdict itself is then a signed event and counts the same everywhere.
Claims: a claim backdated to before a competing claim takes the piece; the inbox flags claims first seen more than
1 h after their date (coordinator and owner object). First-seen times are local and never change this computation,
so every workbench still derives the same state.
"""
from __future__ import annotations

import json

from . import nostr, schemas


def _content(ev):
    try:
        c = json.loads(ev["content"])
    except (ValueError, TypeError, RecursionError):
        return None
    return c if isinstance(c, dict) and not schemas.validate(ev["kind"], c) else None


def _order(evs):
    return sorted(evs, key=lambda e: (e["created_at"], e["id"]))


def same_identity(a: str, b: str, identities: dict | None) -> bool:
    if a == b:
        return True
    if identities and identities.get(a) and identities.get(a) == identities.get(b):
        return True
    return False


def proposals(events: list[dict]) -> dict:
    """proposal event id -> proposal event (every valid proposal is its own project)."""
    return {ev["id"]: ev for ev in _order(e for e in events if e["kind"] == nostr.KIND_PROPOSAL and nostr.verify_event(e))
            if _content(ev)}


def review_outcome(target: dict, reviews: list[dict], identities=None, now=None):
    """(accepted, accept_time, reviewer, rejections) from reviews of an event: reviews older than the target are
    ignored; the latest verdict of each independent reviewer counts; accepted when one of them is accept."""
    latest = {}
    for ev in _order(reviews):
        if now is not None and ev["created_at"] > now:
            continue
        c = _content(ev)
        if not c or c["target"] != target["id"] or ev["created_at"] < target["created_at"] \
                or same_identity(ev["pubkey"], target["pubkey"], identities):
            continue
        latest[ev["pubkey"]] = (c["verdict"], ev)
    acc = [ev for v, ev in latest.values() if v == "accept"]
    if not acc:
        return False, None, None, [ev for v, ev in latest.values() if v == "reject"]
    first = min(acc, key=lambda e: (e["created_at"], e["id"]))
    return True, first["created_at"], first["pubkey"], []


def project_state(events: list[dict], proposal_id: str, now: int, identities: dict | None = None) -> dict | None:
    events = [e for e in events if e["created_at"] <= now]
    prop = proposals(events).get(proposal_id)
    if prop is None:
        return None
    pc = _content(prop)
    project, t0 = pc["project"], prop["created_at"]
    by_id = {e["id"]: e for e in events}

    def in_project(kind):
        out = []
        for ev in _order(e for e in events if e["kind"] == kind):
            c = _content(ev)
            if c and c.get("project") == project and nostr.thread_root(ev) == proposal_id and ev["created_at"] >= t0:
                out.append((ev, c))
        return out

    pledges = {}
    for ev, c in in_project(nostr.KIND_PLEDGE):
        pledges.setdefault(ev["pubkey"], (ev, c))         # first pledge of a workbench counts
    reviews = [e for e in events if e["kind"] == nostr.KIND_REVIEW]
    deliveries = in_project(nostr.KIND_DELIVERY)

    pieces = {p["piece"]: {"piece": p["piece"], "title": p["title"], "status": "open", "holder": None, "claim": None,
                           "delivery": None, "reviewer": None, "acceptedAt": None, "history": []}
              for p in pc["plan"]}
    claims_log, accepted, missed = [], [], []

    def delivery_for(claim_id, piece):
        """First accepted delivery of the claimer for this claim, made between the claim and its due time:
        (delivery, accepted, accepted_at, reviewer); else the first such delivery not (yet) accepted."""
        claim_ev = by_id[claim_id]
        due = json.loads(claim_ev["content"])["due"]
        best = None
        for dev, dc in deliveries:
            if dev["pubkey"] != claim_ev["pubkey"] or dc["claim"] != claim_id or dc["piece"] != piece:
                continue
            if not claim_ev["created_at"] <= dev["created_at"] <= due:
                continue
            ok, at, reviewer, _ = review_outcome(dev, reviews, identities, now)
            if ok:
                return dev, True, at, reviewer
            best = best or (dev, False, None, None)
        return best or (None, False, None, None)

    def settle(piece, t):
        """Close the current holder of a piece if its due time passed before t."""
        p = pieces[piece]
        if p["status"] in ("claimed", "delivered") and p["claim"]["due"] < t:
            p["history"].append({"claim": p["claim"]["id"], "holder": p["holder"], "result": "missed",
                                 "due": p["claim"]["due"]})
            missed.append({"pubkey": p["holder"], "claim": p["claim"]["id"], "piece": piece, "due": p["claim"]["due"]})
            p.update(status="open", holder=None, claim=None, delivery=None)

    for ev, c in in_project(nostr.KIND_CLAIM):
        piece = c["piece"]
        if piece not in pieces or ev["pubkey"] not in pledges or pledges[ev["pubkey"]][0]["created_at"] > ev["created_at"]:
            claims_log.append({"claim": ev["id"], "pubkey": ev["pubkey"], "piece": piece, "valid": False,
                               "why": "unknown piece or no pledge"})
            continue
        if not ev["created_at"] <= c["due"] <= pc["deadline"]:
            claims_log.append({"claim": ev["id"], "pubkey": ev["pubkey"], "piece": piece, "valid": False,
                               "why": "due before the claim or after the project deadline"})
            continue
        p = pieces[piece]
        if p["status"] in ("claimed", "delivered"):
            dev, ok, at, reviewer = delivery_for(p["claim"]["id"], piece)
            if ok and at <= ev["created_at"]:
                p.update(status="accepted", delivery=dev["id"], reviewer=reviewer, acceptedAt=at)
            elif dev is not None and dev["created_at"] <= ev["created_at"]:
                # delivered in time: the piece stays with its claimer until a review rejects it (round-3 P2);
                # only a rejection standing at the time of the competing claim, after the due time, frees it
                _, _, _, rej = review_outcome(dev, reviews, identities, ev["created_at"])
                if rej:
                    settle(piece, ev["created_at"])
                else:
                    p.update(status="delivered", delivery=dev["id"])
            else:
                settle(piece, ev["created_at"])
        if p["status"] != "open":
            claims_log.append({"claim": ev["id"], "pubkey": ev["pubkey"], "piece": piece, "valid": False,
                               "why": f"piece is {p['status']}"})
            continue
        p.update(status="claimed", holder=ev["pubkey"], claim={"id": ev["id"], "due": c["due"]})
        claims_log.append({"claim": ev["id"], "pubkey": ev["pubkey"], "piece": piece, "valid": True, "why": ""})

    for piece, p in pieces.items():
        if p["status"] in ("claimed", "delivered"):
            dev, ok, at, reviewer = delivery_for(p["claim"]["id"], piece)
            if ok:
                p.update(status="accepted", delivery=dev["id"], reviewer=reviewer, acceptedAt=at)
            elif dev is not None and p["claim"]["due"] >= now:
                p.update(status="delivered", delivery=dev["id"])
            elif dev is not None:
                # delivered in time, verdict pending: keeps the piece; rejected after the due time: missed
                _, _, _, rej = review_outcome(dev, reviews, identities, now)
                if rej:
                    settle(piece, now)
                else:
                    p.update(status="delivered", delivery=dev["id"])
            else:
                settle(piece, now)
        if p["status"] == "accepted":
            accepted.append({"pubkey": p["holder"], "piece": piece, "delivery": p["delivery"], "at": p["acceptedAt"],
                             "reviewer": p["reviewer"]})

    all_ok = all(p["status"] == "accepted" for p in pieces.values())
    status = "accepted" if all_ok else ("expired" if now > pc["deadline"] else "open")
    pledge_out = []
    for pub, (ev, c) in pledges.items():
        done = [a for a in accepted if a["pubkey"] == pub]
        if done:
            res = "fulfilled"
        elif now > pc["deadline"]:
            res = "missed"
        else:
            res = "open"
        pledge_out.append({"pubkey": pub, "pledge": ev["id"], "sharePercent": c["sharePercent"], "result": res,
                           "at": (min(a["at"] for a in done) if done else pc["deadline"])})
    return {"project": project, "proposal": proposal_id, "coordinator": prop["pubkey"], "title": pc["title"],
            "deadline": pc["deadline"], "status": status, "pieces": list(pieces.values()), "claims": claims_log,
            "accepted": accepted, "missed": missed, "pledges": pledge_out}
