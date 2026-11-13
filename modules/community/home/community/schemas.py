# SPDX-License-Identifier: AGPL-3.0-or-later
"""JSON content schemas of the Freedom event kinds (SPEC §5.1, §6.1, §7.3, §8) and their validation.

A schema is {field: (type, required, extra)}; extra is an enum list, a max length or a nested check. Unknown fields
are rejected, so a post cannot smuggle data the filter and the owner did not see in the known fields.
"""
from __future__ import annotations

import re
import uuid

from . import nostr

UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
URL_RE = re.compile(r"^https://[^\s/$.?#][^\s]*$")


def new_uuid() -> str:
    return str(uuid.uuid4())


def _is_uuid(v):
    return isinstance(v, str) and bool(UUID_RE.match(v))


def _is_id(v):
    return isinstance(v, str) and bool(HEX64.match(v))


def _is_url(v):
    return isinstance(v, str) and bool(URL_RE.match(v)) and len(v) <= 300


TEXT = 4000

SCHEMAS = {
    nostr.KIND_REPORT: {
        "card": ("uuid", True, None),
        "outcome": ("enum", True, ["worked", "partial", "failed", "abandoned"]),
        "measure": ("str", True, 1000),        # "MRR 0.66 -> 0.87 on 22 questions"
        "differences": ("str", False, TEXT),   # "ours differs: ..."
        "pitfalls": ("str", False, TEXT),
        "cost": ("str", False, 300),
        "code": ("url", False, None),
    },
    nostr.KIND_HELP: {
        "topic": ("enum", True, ["install", "bug", "howto", "review"]),
        "summary": ("str", True, 1000),
        "step": ("str", False, 200),           # wizard step (SPEC §4.3)
        "log": ("str", False, 8000),           # report without secrets (freedom_setup.py report) - still filtered
        "want": ("enum", True, ["card", "analysis", "patch"]),   # "teach", never "do it for me" (§7.2)
    },
    nostr.KIND_DISCUSSION: {
        "form": ("enum", True, ["pitfall", "different", "question", "answer", "comment"]),
        "text": ("str", True, TEXT),
    },
    nostr.KIND_PROPOSAL: {
        "project": ("uuid", True, None),
        "title": ("str", True, 120),
        "goal": ("str", True, 2000),
        "plan": ("plan", True, None),
        "budget": ("budget", True, None),
        "deadline": ("int", True, None),       # unix time; claims are due no later
        "code": ("url", False, None),
    },
    nostr.KIND_PLEDGE: {
        "project": ("uuid", True, None),
        "sharePercent": ("num", True, None),   # of the pledger's week
        "tokens": ("int", False, None),
        "ownerConfirmed": ("true", True, None),
    },
    nostr.KIND_CLAIM: {
        "project": ("uuid", True, None),
        "piece": ("str", True, 60),
        "due": ("int", True, None),
    },
    nostr.KIND_DELIVERY: {
        "project": ("uuid", True, None),
        "piece": ("str", True, 60),
        "claim": ("id", True, None),
        "artifact": ("artifact", True, None),
        "summary": ("str", True, 2000),
        "checks": ("str", False, 2000),        # how to verify: tests, measurement
    },
    nostr.KIND_REVIEW: {
        "target": ("id", True, None),
        "verdict": ("enum", True, ["accept", "reject"]),
        "checks": ("str", True, 2000),         # what the reviewer ran or repeated
        "notes": ("str", False, TEXT),
    },
    nostr.KIND_FLAG: {
        "target": ("id", True, None),
        "rules": ("rules", True, None),
        "note": ("str", False, 500),
    },
}


def _check_field(name, typ, extra, v) -> str:
    if typ == "str":
        if not isinstance(v, str) or not v.strip():
            return f"{name}: non-empty text expected"
        if extra and len(v) > extra:
            return f"{name}: longer than {extra} characters"
    elif typ == "enum":
        if v not in extra:
            return f"{name}: one of {', '.join(extra)}"
    elif typ == "uuid":
        if not _is_uuid(v):
            return f"{name}: uuid expected"
    elif typ == "id":
        if not _is_id(v):
            return f"{name}: event id (64 hex) expected"
    elif typ == "url":
        if not _is_url(v):
            return f"{name}: https URL expected"
    elif typ == "int":
        if not isinstance(v, int) or isinstance(v, bool) or v < 0:
            return f"{name}: non-negative integer expected"
    elif typ == "num":
        if not isinstance(v, (int, float)) or isinstance(v, bool) or not 0 < v <= 100:
            return f"{name}: number in (0, 100] expected"
    elif typ == "true":
        if v is not True:
            return f"{name}: must be true (the owner confirmed it)"
    elif typ == "plan":
        if not isinstance(v, list) or not 1 <= len(v) <= 30:
            return f"{name}: list of 1-30 pieces expected"
        seen = set()
        for i, p in enumerate(v):
            if not isinstance(p, dict) or set(p) - {"piece", "title", "tokens", "days"}:
                return f"{name}[{i}]: {{piece, title, tokens, days}} expected"
            if not isinstance(p.get("piece"), str) or not re.match(r"^[a-z0-9][a-z0-9-]{0,59}$", p["piece"]):
                return f"{name}[{i}].piece: short slug expected"
            if p["piece"] in seen:
                return f"{name}[{i}].piece: duplicate"
            seen.add(p["piece"])
            if not isinstance(p.get("title"), str) or not p["title"].strip() or len(p["title"]) > 200:
                return f"{name}[{i}].title: text expected"
            if not isinstance(p.get("tokens", 0), int) or not isinstance(p.get("days", 0), (int, float)):
                return f"{name}[{i}]: tokens int, days number"
    elif typ == "budget":
        if not isinstance(v, dict) or set(v) - {"tokens", "days"} or not isinstance(v.get("tokens"), int) \
                or not isinstance(v.get("days"), (int, float)):
            return f"{name}: {{tokens: int, days: number}} expected"
    elif typ == "artifact":
        if not isinstance(v, dict) or set(v) - {"type", "ref", "sha"} or v.get("type") not in ("git", "card", "report"):
            return f"{name}: {{type: git|card|report, ref, sha?}} expected"
        if v["type"] == "git" and not _is_url(v.get("ref")):
            return f"{name}.ref: public https URL of the code expected (results are public, §7.2)"
        if v["type"] == "card" and not _is_uuid(v.get("ref")):
            return f"{name}.ref: card uuid expected"
        if v["type"] == "report" and not _is_id(v.get("ref")):
            return f"{name}.ref: event id expected"
        if "sha" in v and not (isinstance(v["sha"], str) and re.match(r"^[0-9a-f]{7,64}$", v["sha"])):
            return f"{name}.sha: hex expected"
    elif typ == "rules":
        if not isinstance(v, list) or not v or not all(isinstance(x, str) for x in v):
            return f"{name}: list of filter rule ids expected"
    return ""


def validate(kind: int, content) -> list[str]:
    """Problems of an event content (empty list = valid). Cards: see cards.validate."""
    if kind == nostr.KIND_CARD:
        from . import cards
        return cards.validate(content, require_signature=False)
    schema = SCHEMAS.get(kind)
    if schema is None:
        return [f"unknown kind {kind}"]
    if not isinstance(content, dict):
        return ["content must be a JSON object"]
    errs = [f"unknown field {k}" for k in content if k not in schema]
    for name, (typ, req, extra) in schema.items():
        if name not in content:
            if req:
                errs.append(f"{name}: required")
            continue
        e = _check_field(name, typ, extra, content[name])
        if e:
            errs.append(e)
    return errs


def text_of(content) -> str:
    """All human text of a content (for the filter, read-guard and similarity)."""
    out = []

    def walk(o):
        if isinstance(o, str):
            out.append(o)
        elif isinstance(o, dict):
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(content)
    return "\n".join(out)


# ---------------------------------------------------------------- tags (review BLOCK 1)

# Only the tag names this package writes (re-review BLOCK): a tag name is free text too, so unknown names are refused
# instead of trusting them. Values of t/title/alt go through the outbound filter.
TAG_NAMES = {"e", "p", "a", "d", "i", "t", "title", "alt"}
COORD = re.compile(r"^(\d{1,5}):([0-9a-f]{64}):(.*)$")
GITHUB_I = re.compile(r"^github:[a-z0-9](?:[a-z0-9-]{0,38})$")
GIST_ID = re.compile(r"^[0-9a-f]{8,40}$")
E_MARKERS = ("", "root", "reply", "mention")


def check_tags(kind: int, tags) -> tuple[list[str], list[tuple[str, str]]]:
    """Shape of every tag of an outgoing event, and the free-text values the outbound filter must check.

    e/p: 64-hex ids (relay hints are free text); a: kind:pubkey:uuid; d: uuid, only on addressable kinds;
    i: only on kind 0 as github:<login> + gist id (the public identity proof); t/title/alt: every value is free text;
    any other tag name is refused. Returns (errors, [(where, text)])."""
    errs, free = [], []
    if not isinstance(tags, list):
        return ["tags must be a list"], free
    for i, t in enumerate(tags):
        w = f"tags[{i}]"
        if not isinstance(t, list) or not t or len(t) > 6 or not all(isinstance(x, str) for x in t):
            errs.append(f"{w}: list of 1-6 strings expected")
            continue
        name = t[0]
        if name not in TAG_NAMES:
            errs.append(f"{w}: tag name not allowed (allowed: {', '.join(sorted(TAG_NAMES))})")
            continue
        if name == "e":
            if len(t) < 2 or not HEX64.match(t[1]) or len(t) > 5:
                errs.append(f"{w}: e = [e, <64-hex id>, relay?, marker?, pubkey?]")
                continue
            if len(t) > 3 and t[3] not in E_MARKERS:
                errs.append(f"{w}: e marker must be root, reply or mention")
            if len(t) > 4 and not HEX64.match(t[4]):
                errs.append(f"{w}: e pubkey must be 64-hex")
            if len(t) > 2 and t[2]:
                free.append((f"{w}.relay", t[2]))
        elif name == "p":
            if len(t) < 2 or not HEX64.match(t[1]) or len(t) > 3:
                errs.append(f"{w}: p = [p, <64-hex pubkey>, relay?]")
                continue
            if len(t) > 2 and t[2]:
                free.append((f"{w}.relay", t[2]))
        elif name == "a":
            m = COORD.match(t[1]) if len(t) >= 2 else None
            if not m or len(t) > 3 or not UUID_RE.match(m.group(3)):
                errs.append(f"{w}: a = [a, <kind>:<64-hex pubkey>:<uuid>, relay?]")
                continue
            if len(t) > 2 and t[2]:
                free.append((f"{w}.relay", t[2]))
        elif name == "d":
            if not nostr.is_addressable(kind) or len(t) != 2 or not UUID_RE.match(t[1]):
                errs.append(f"{w}: d = [d, <uuid>] and only on addressable kinds")
        elif name == "i":
            if kind != nostr.KIND_METADATA or len(t) != 3 or not GITHUB_I.match(t[1]) or not GIST_ID.match(t[2]):
                errs.append(f"{w}: i = [i, github:<login>, <gist id>] and only on the kind-0 identity event")
        else:
            free += [(f"{w}.{name}", x) for x in t[1:]]
    return errs, free
