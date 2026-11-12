# SPDX-License-Identifier: AGPL-3.0-or-later
"""NIP-01 events (serialize, id, sign, verify, filters), NIP-10 thread tags, NIP-42 AUTH events, Freedom kinds."""
from __future__ import annotations

import hashlib
import json
import time

from . import schnorr

# ---------------------------------------------------------------- Freedom kinds (SPEC §6.1)
# Regular events 1000-9999 are stored by every relay; the idea card is addressable (30000-39999, d-tag = card uuid),
# so the author can publish a corrected card under the same uuid and relays keep the newest one.
KIND_METADATA = 0            # NIP-01 profile; NIP-39 "i" tag carries the GitHub proof (SPEC §6.3)
KIND_AUTH = 22242            # NIP-42 client authentication (never stored)
KIND_CARD = 30470            # idea card (§5.1)
KIND_REPORT = 4470           # implementation report: "implemented, measured ..." (§6.1)
KIND_HELP = 4471             # help request (§4.3, §7.2: answered as a card or analysis, never "do it for me")
KIND_DISCUSSION = 4472       # thread reply with a form: pitfall / different / question / answer / comment
KIND_PROPOSAL = 4473         # shared project proposal (§7.3.1)
KIND_PLEDGE = 4474           # budget pledge, published only after the owner's yes (§1.5, §7.3.2)
KIND_CLAIM = 4475            # a workbench takes a piece of a project (§7.3.3)
KIND_DELIVERY = 4476         # a piece delivered, result public (§7.2, §7.3.4)
KIND_REVIEW = 4477           # verification of a delivery or a report by another workbench (§7.3.4, §8)
KIND_FLAG = 4479             # moderation: a published post leaks personal data (generic filter rules, §8 minus)

KIND_NAMES = {
    KIND_CARD: "card", KIND_REPORT: "report", KIND_HELP: "help", KIND_DISCUSSION: "discussion",
    KIND_PROPOSAL: "proposal", KIND_PLEDGE: "pledge", KIND_CLAIM: "claim", KIND_DELIVERY: "delivery",
    KIND_REVIEW: "review", KIND_FLAG: "flag", KIND_METADATA: "metadata",
}
NAME_KINDS = {v: k for k, v in KIND_NAMES.items()}
FREEDOM_KINDS = [KIND_CARD, KIND_REPORT, KIND_HELP, KIND_DISCUSSION, KIND_PROPOSAL, KIND_PLEDGE, KIND_CLAIM,
                 KIND_DELIVERY, KIND_REVIEW, KIND_FLAG]
FREEDOM_TAG = "freedom"      # every Freedom event carries ["t", "freedom"] so generic clients can find them


def is_addressable(kind: int) -> bool:
    return 30000 <= kind < 40000


def is_replaceable(kind: int) -> bool:
    return kind == 0 or kind == 3 or 10000 <= kind < 20000


def is_ephemeral(kind: int) -> bool:
    return 20000 <= kind < 30000


def serialize(ev: dict) -> str:
    return json.dumps([0, ev["pubkey"], ev["created_at"], ev["kind"], ev["tags"], ev["content"]],
                      separators=(",", ":"), ensure_ascii=False)


def event_id(ev: dict) -> str:
    return hashlib.sha256(serialize(ev).encode("utf-8")).hexdigest()


def sign_event(seckey_hex: str, kind: int, content: str, tags: list | None = None,
               created_at: int | None = None) -> dict:
    sk = bytes.fromhex(seckey_hex)
    ev = {"pubkey": schnorr.pubkey_gen(sk).hex(), "created_at": int(created_at if created_at is not None else time.time()),
          "kind": int(kind), "tags": [list(map(str, t)) for t in (tags or [])], "content": content}
    ev["id"] = event_id(ev)
    ev["sig"] = schnorr.sign(bytes.fromhex(ev["id"]), sk).hex()
    return ev


MAX_CREATED_AT = 2**32 - 1      # year 2106; larger values overflow storage and are never honest
MAX_KIND = 65535                # NIP-01 kind range
MAX_FILTER_INT = 2**63 - 1      # SQLite INTEGER


def utf8_ok(s: str) -> bool:
    """False for strings with lone surrogates (valid JSON escapes, not encodable, crash SQLite)."""
    try:
        s.encode("utf-8")
        return True
    except UnicodeEncodeError:
        return False


def _hex(s, n):
    return isinstance(s, str) and len(s) == n and all(c in "0123456789abcdef" for c in s)


_VERIFIED: dict = {}          # (id, pubkey, sig) -> True; the id itself is always recomputed


def verify_event(ev: dict) -> bool:
    try:
        if not (_hex(ev["id"], 64) and _hex(ev["pubkey"], 64) and _hex(ev["sig"], 128)):
            return False
        if not isinstance(ev["created_at"], int) or not isinstance(ev["kind"], int) or not isinstance(ev["content"], str):
            return False
        if (isinstance(ev["created_at"], bool) or not 0 <= ev["created_at"] <= MAX_CREATED_AT
                or isinstance(ev["kind"], bool) or not 0 <= ev["kind"] <= MAX_KIND):
            return False
        if not isinstance(ev["tags"], list) or not all(isinstance(t, list) and all(isinstance(x, str) for x in t)
                                                         for t in ev["tags"]):
            return False
        if event_id(ev) != ev["id"]:
            return False
        key = (ev["id"], ev["pubkey"], ev["sig"])
        if key in _VERIFIED:
            return True
        ok = schnorr.verify(bytes.fromhex(ev["id"]), bytes.fromhex(ev["pubkey"]), bytes.fromhex(ev["sig"]))
        if ok:
            if len(_VERIFIED) > 50000:
                _VERIFIED.clear()
            _VERIFIED[key] = True
        return ok
    except (KeyError, TypeError, ValueError):
        return False


def tag_values(ev: dict, name: str) -> list[str]:
    return [t[1] for t in ev.get("tags", []) if len(t) > 1 and t[0] == name]


def first_tag(ev: dict, name: str, default: str = "") -> str:
    v = tag_values(ev, name)
    return v[0] if v else default


def d_tag(ev: dict) -> str:
    return first_tag(ev, "d")


def address(ev: dict) -> str:
    """NIP-01 coordinate of an addressable event: kind:pubkey:d."""
    return f"{ev['kind']}:{ev['pubkey']}:{d_tag(ev)}"


MAX_FILTER_VALUES = 500
EVENT_KEYS = ("id", "pubkey", "created_at", "kind", "tags", "content", "sig")


def canonical_event(ev: dict) -> dict:
    """Only the NIP-01 fields (extra keys a client sent are not stored or forwarded)."""
    return {k: ev[k] for k in EVENT_KEYS}


def _int(v) -> bool:
    return isinstance(v, int) and not isinstance(v, bool) and 0 <= v <= MAX_FILTER_INT


def filter_problem(f) -> str:
    """'' for a well-typed NIP-01 filter, else what is wrong (untyped values must never reach the matcher)."""
    if not isinstance(f, dict):
        return "filter must be an object"
    for k, v in f.items():
        if k in ("ids", "authors"):
            if not isinstance(v, list) or len(v) > MAX_FILTER_VALUES or not all(_hex(x, 64) for x in v):
                return f"{k}: list of 64-hex strings expected"
        elif k == "kinds":
            if not isinstance(v, list) or len(v) > 100 or not all(_int(x) and x <= 65535 for x in v):
                return "kinds: list of integers expected"
        elif k in ("since", "until", "limit"):
            if not _int(v):
                return f"{k}: non-negative integer expected"
        elif len(k) == 2 and k[0] == "#" and k[1].isascii() and k[1].isalpha():
            if not isinstance(v, list) or len(v) > MAX_FILTER_VALUES or not all(
                    isinstance(x, str) and len(x) <= 1024 and utf8_ok(x) for x in v):
                return f"{k}: list of strings expected"
        else:
            return f"unsupported filter key {k[:20]!r}" if utf8_ok(k) else "unsupported filter key"
    return ""


def match_filter(ev: dict, f: dict) -> bool:
    """NIP-01 filter (ids, authors, kinds, #x, since, until); limit is applied by the caller. An ill-typed filter
    matches nothing (it never raises)."""
    if filter_problem(f):
        return False
    if "ids" in f and ev["id"] not in f["ids"]:
        return False
    if "authors" in f and ev["pubkey"] not in f["authors"]:
        return False
    if "kinds" in f and ev["kind"] not in f["kinds"]:
        return False
    if "since" in f and ev["created_at"] < f["since"]:
        return False
    if "until" in f and ev["created_at"] > f["until"]:
        return False
    for k, vals in f.items():
        if k.startswith("#") and len(k) == 2:
            have = set(tag_values(ev, k[1]))
            if not have.intersection(vals):
                return False
    return True


def match_any(ev: dict, filters: list[dict]) -> bool:
    return any(match_filter(ev, f) for f in filters)


# ---------------------------------------------------------------- NIP-10 threads

def reply_tags(root: dict, parent: dict | None = None, relay: str = "") -> list[list[str]]:
    """Marked e-tags (NIP-10): root = the card or project event, reply = the event answered (if not the root).
    An addressable root (card) also gets its "a" coordinate, so a corrected card keeps the thread."""
    tags = [["e", root["id"], relay, "root", root["pubkey"]]]
    if is_addressable(root["kind"]):
        tags.append(["a", address(root), relay])
    if parent is not None and parent["id"] != root["id"]:
        tags.append(["e", parent["id"], relay, "reply", parent["pubkey"]])
    pubs = [root["pubkey"]] + ([parent["pubkey"]] if parent is not None else [])
    for p in dict.fromkeys(pubs):
        tags.append(["p", p])
    return tags


def thread_root(ev: dict) -> str:
    for t in ev.get("tags", []):
        if len(t) >= 4 and t[0] == "e" and t[3] == "root":
            return t[1]
    es = tag_values(ev, "e")          # deprecated positional form: first e = root
    return es[0] if es else ""


def reply_to(ev: dict) -> str:
    for t in ev.get("tags", []):
        if len(t) >= 4 and t[0] == "e" and t[3] == "reply":
            return t[1]
    return thread_root(ev)


# ---------------------------------------------------------------- NIP-42

def auth_event(seckey_hex: str, relay_url: str, challenge: str) -> dict:
    return sign_event(seckey_hex, KIND_AUTH, "", [["relay", relay_url], ["challenge", challenge]])


def norm_relay_url(u: str) -> str:
    u = u.strip().lower().rstrip("/")
    for pre, port in (("wss://", ":443"), ("ws://", ":80")):
        if u.startswith(pre) and u.endswith(port):
            u = u[: -len(port)]
    return u


def check_auth_event(ev: dict, relay_url: str, challenge: str, now: int | None = None, window: int = 600) -> str:
    """'' when valid, else the reason."""
    now = int(time.time()) if now is None else now
    if not verify_event(ev):
        return "bad signature"
    if ev["kind"] != KIND_AUTH:
        return "wrong kind"
    if abs(ev["created_at"] - now) > window:
        return "stale auth event"
    if first_tag(ev, "challenge") != challenge:
        return "wrong challenge"
    if norm_relay_url(first_tag(ev, "relay")) != norm_relay_url(relay_url):
        return "wrong relay"
    return ""
