# SPDX-License-Identifier: AGPL-3.0-or-later
"""Idea card (SPEC §5.1): fields exactly id, title, problem, approach, interface, pitfalls, result, cost, code, author.

`author` = {workbench, pubkey, sig}: sig is a BIP340 signature of the canonical card JSON without author.sig, so a
card is verifiable on its own (archive export, git, mail) and not only inside its Nostr event.
"""
from __future__ import annotations

import hashlib
import json

from . import nostr, schemas, schnorr

FIELDS = ["id", "title", "problem", "approach", "interface", "pitfalls", "result", "cost", "code", "author"]
REQUIRED_TEXT = ["title", "problem", "approach", "interface", "pitfalls", "result", "cost"]
LIMITS = {"title": 120, "problem": 1000, "approach": 3000, "interface": 1000, "pitfalls": 2000, "result": 1000,
          "cost": 300}


def new_card(title, problem, approach, interface, pitfalls, result, cost, code=None, card_id=None) -> dict:
    c = {"id": card_id or schemas.new_uuid(), "title": title, "problem": problem, "approach": approach,
         "interface": interface, "pitfalls": pitfalls, "result": result, "cost": cost}
    if code:
        c["code"] = code
    return c


def canonical(card: dict) -> bytes:
    body = {k: v for k, v in card.items() if k != "author"}
    a = card.get("author") or {}
    body["author"] = {"workbench": a.get("workbench", ""), "pubkey": a.get("pubkey", "")}
    return json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def sign_card(card: dict, keys, workbench_id: str) -> dict:
    c = {k: v for k, v in card.items() if k != "author"}
    c["author"] = {"workbench": workbench_id, "pubkey": keys.pubkey}
    digest = hashlib.sha256(canonical(c)).digest()
    c["author"]["sig"] = schnorr.sign(digest, bytes.fromhex(keys.seckey)).hex()
    return c


def verify_card(card: dict) -> bool:
    a = card.get("author") or {}
    try:
        return schnorr.verify(hashlib.sha256(canonical(card)).digest(), bytes.fromhex(a["pubkey"]),
                              bytes.fromhex(a["sig"]))
    except (KeyError, ValueError, TypeError):
        return False


def validate(card, require_signature: bool = True) -> list[str]:
    if not isinstance(card, dict):
        return ["card must be an object"]
    errs = [f"unknown field {k}" for k in card if k not in FIELDS]
    if not schemas._is_uuid(card.get("id")):
        errs.append("id: uuid expected")
    for f in REQUIRED_TEXT:
        v = card.get(f)
        if not isinstance(v, str) or not v.strip():
            errs.append(f"{f}: required text")
        elif len(v) > LIMITS[f]:
            errs.append(f"{f}: longer than {LIMITS[f]} characters")
    if "code" in card and not schemas._is_url(card["code"]):
        errs.append("code: https URL expected (optional field)")
    a = card.get("author")
    if require_signature:
        if not isinstance(a, dict) or set(a) != {"workbench", "pubkey", "sig"}:
            errs.append("author: {workbench, pubkey, sig} expected")
        elif not verify_card(card):
            errs.append("author.sig: signature does not verify")
    elif a is not None and (not isinstance(a, dict) or set(a) - {"workbench", "pubkey", "sig"}):
        errs.append("author: {workbench, pubkey, sig} expected")
    return errs


def to_event(card: dict, keys, created_at: int | None = None) -> dict:
    """Addressable event: d = card uuid, so a corrected card replaces the old one at the relays."""
    if card.get("author", {}).get("pubkey") != keys.pubkey:
        raise ValueError("card is signed by another key")
    tags = [["d", card["id"]], ["t", nostr.FREEDOM_TAG], ["title", card["title"]], ["alt", "Freedom idea card"]]
    return nostr.sign_event(keys.seckey, nostr.KIND_CARD, json.dumps(card, ensure_ascii=False), tags, created_at)


def from_event(ev: dict) -> tuple[dict | None, list[str]]:
    if ev.get("kind") != nostr.KIND_CARD:
        return None, ["not a card event"]
    try:
        card = json.loads(ev["content"])
    except (ValueError, TypeError, RecursionError):
        return None, ["content is not JSON"]
    errs = validate(card, require_signature=True)
    if not errs and card["author"]["pubkey"] != ev["pubkey"]:
        errs.append("card author differs from the event author")
    if not errs and nostr.d_tag(ev) != card["id"]:
        errs.append("d-tag differs from the card id")
    return (card if not errs else None), errs


def report_target(ev: dict, content: dict) -> tuple[str, str] | None:
    """(card author pubkey, card uuid) a report is about: its "a" coordinate 30470:<author>:<uuid>, whose uuid must
    equal content.card. A uuid alone is not enough - anyone can publish a card under someone else's uuid."""
    for t in ev.get("tags", []):
        if len(t) >= 2 and t[0] == "a":
            parts = t[1].split(":")
            if len(parts) == 3 and parts[0] == str(nostr.KIND_CARD) and parts[2] == content.get("card"):
                return parts[1], parts[2]
    return None
