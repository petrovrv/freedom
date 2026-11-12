# SPDX-License-Identifier: AGPL-3.0-or-later
"""Reputation (SPEC §8), a pure function of signed events and a reference time: any workbench holding the same
events computes the same numbers.

Points only for independently verified facts (the verifier is another workbench, and another GitHub identity when the
identity map of the allowlist is known):
  card_confirmed    +3  another workbench reports "worked" for the author's card (once per reporter and card)
  report_accepted   +1  another workbench reviews a report with "accept"
  delivery_accepted +5  a project piece delivered in time and accepted by another workbench's review
  pledge_fulfilled  +2  a pledger delivered at least one accepted piece of the project
  claim_missed      -4  a claimed piece had no accepted delivery by its due time (piece back to the pool)
  pledge_missed     -2  a pledger had no accepted piece by the project deadline
  flagged           -3  another workbench flagged a published post and the generic filter confirms the leak
No transfers exist; points decay with a half-life of 6 months (182.625 days) from the time of the verifying event.
"""
from __future__ import annotations

import json

from . import cards, nostr, outfilter, projects, schemas

POINTS = {"card_confirmed": 3.0, "report_accepted": 1.0, "delivery_accepted": 5.0, "pledge_fulfilled": 2.0,
          "claim_missed": -4.0, "pledge_missed": -2.0, "flagged": -3.0}
HALF_LIFE = 182.625 * 86400


def decay(points: float, t: int, now: int) -> float:
    return points * 0.5 ** (max(0, now - t) / HALF_LIFE)


def _valid(events, now):
    seen, out = set(), []
    for e in sorted(events, key=lambda e: (e["created_at"], e["id"])):
        if e["id"] in seen or e["created_at"] > now or not nostr.verify_event(e):
            continue
        seen.add(e["id"])
        out.append(e)
    return out


def _content(ev):
    try:
        c = json.loads(ev["content"])
    except (ValueError, TypeError, RecursionError):
        return None
    return c if isinstance(c, dict) and not schemas.validate(ev["kind"], c) else None


def compute(events: list[dict], now: int, identities: dict | None = None) -> dict:
    """{pubkey: {"score": float, "items": [{"rule", "points", "at", "ref"}]}}; deterministic for a given input."""
    evs = _valid(events, now)
    items: dict[str, list] = {}

    def add(pub, rule, at, ref):
        items.setdefault(pub, []).append({"rule": rule, "points": POINTS[rule], "at": at, "ref": ref})

    # cards are keyed by (author, uuid): a report points at the a-coordinate (review item 4)
    card_keys = {(e["pubkey"], nostr.d_tag(e)) for e in evs if e["kind"] == nostr.KIND_CARD}

    reviews = [e for e in evs if e["kind"] == nostr.KIND_REVIEW]
    confirmed = set()
    for e in evs:
        if e["kind"] != nostr.KIND_REPORT:
            continue
        c = _content(e)
        if not c:
            continue
        tgt = cards.report_target(e, c)
        author = tgt[0] if tgt and tgt in card_keys else None
        if c["outcome"] == "worked" and author and not projects.same_identity(author, e["pubkey"], identities):
            key = (e["pubkey"], tgt)
            if key not in confirmed:
                confirmed.add(key)
                add(author, "card_confirmed", e["created_at"], e["id"])
        ok, at, reviewer, _ = projects.review_outcome(e, reviews, identities, now)
        if ok:
            add(e["pubkey"], "report_accepted", at, e["id"])

    for proj in projects.proposals(evs):
        st = projects.project_state(evs, proj, now, identities)
        for a in st["accepted"]:
            add(a["pubkey"], "delivery_accepted", a["at"], a["delivery"])
        for m in st["missed"]:
            add(m["pubkey"], "claim_missed", m["due"], m["claim"])
        for p in st["pledges"]:
            if p["result"] == "fulfilled":
                add(p["pubkey"], "pledge_fulfilled", p["at"], p["pledge"])
            elif p["result"] == "missed":
                add(p["pubkey"], "pledge_missed", p["at"], p["pledge"])

    by_id = {e["id"]: e for e in evs}
    gen = outfilter.generic_filter()
    flagged = set()
    for e in evs:
        if e["kind"] != nostr.KIND_FLAG:
            continue
        c = _content(e)
        target = by_id.get(c["target"]) if c else None
        if not target or target["id"] in flagged or projects.same_identity(target["pubkey"], e["pubkey"], identities):
            continue
        found = set(gen.check(target["content"]).rules())
        if found & set(c["rules"]):
            flagged.add(target["id"])
            add(target["pubkey"], "flagged", e["created_at"], target["id"])

    out = {}
    for pub, its in items.items():
        its.sort(key=lambda i: (i["at"], i["rule"], i["ref"]))
        out[pub] = {"score": round(sum(decay(i["points"], i["at"], now) for i in its), 4), "items": its}
    return dict(sorted(out.items()))


def score(rep: dict, pubkey: str) -> float:
    return rep.get(pubkey, {}).get("score", 0.0)
