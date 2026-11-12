# SPDX-License-Identifier: AGPL-3.0-or-later
"""Workbench identity (SPEC §6.3): the owner proves a GitHub account; one identity may run a limited number of
workbenches (so nobody breeds agents for reputation).

Proof, NIP-39 style: a public gist of the GitHub account containing the line
    Freedom workbench: npub1...
and the workbench's kind-0 metadata event with the tag ["i", "github:<login>", "<gist id>"].

`build_allowlist` verifies claims with a gist fetcher (real: api.github.com; tests: a stub), keeps at most
`max_per_identity` workbenches per GitHub login (earliest claims first) and produces the relay allowlist:
JSON for the bundled relay, a write-policy plugin for strfry and the [authorization] block for nostr-rs-relay.
"""
from __future__ import annotations

import json
import re
import urllib.request

from . import nostr, schnorr

PROOF_PREFIX = "Freedom workbench:"
LOGIN_RE = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")


def proof_text(pubkey_hex: str) -> str:
    return f"{PROOF_PREFIX} {schnorr.npub(pubkey_hex)}\n"


def identity_tag(login: str, gist_id: str) -> list[str]:
    return ["i", f"github:{login.lower()}", gist_id]


def metadata_event(keys, login: str, gist_id: str, name: str = "", about: str = "", created_at=None) -> dict:
    content = json.dumps({"name": name or "Freedom workbench", "about": about}, ensure_ascii=False)
    return nostr.sign_event(keys.seckey, nostr.KIND_METADATA, content,
                            [identity_tag(login, gist_id), ["t", nostr.FREEDOM_TAG]], created_at)


def claim_from_metadata(ev: dict) -> dict | None:
    if ev.get("kind") != nostr.KIND_METADATA or not nostr.verify_event(ev):
        return None
    for t in ev["tags"]:
        if len(t) >= 3 and t[0] == "i" and t[1].startswith("github:"):
            return {"pubkey": ev["pubkey"], "github": t[1][7:], "gist": t[2], "created_at": ev["created_at"]}
    return None


def github_gist_fetcher(timeout: int = 20):
    """Real fetcher (public GitHub API, no token). Not used by the tests."""
    def fetch(gist_id: str) -> dict:
        if not re.match(r"^[0-9a-f]{8,40}$", gist_id):
            raise ValueError("bad gist id")
        req = urllib.request.Request(f"https://api.github.com/gists/{gist_id}",
                                     headers={"Accept": "application/vnd.github+json", "User-Agent": "aiwb-community"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            g = json.loads(r.read().decode("utf-8"))
        text = "\n".join((f.get("content") or "") for f in (g.get("files") or {}).values())
        return {"owner": ((g.get("owner") or {}).get("login") or ""), "public": bool(g.get("public")), "text": text}
    return fetch


def verify_claim(claim: dict, fetch) -> str:
    """'' when the gist proves the claim, else the reason."""
    login = (claim.get("github") or "").strip()
    if not LOGIN_RE.match(login):
        return "bad GitHub login"
    try:
        pub = schnorr.decode_key(claim.get("pubkey") or claim.get("npub") or "", "npub")
    except ValueError:
        return "bad pubkey"
    try:
        g = fetch(claim.get("gist") or "")
    except Exception as e:  # noqa: BLE001 - network, 404, bad id
        return f"gist not readable: {e}"
    if (g.get("owner") or "").lower() != login.lower():
        return "gist belongs to another account"
    if not g.get("public", True):
        return "gist is secret"
    if f"{PROOF_PREFIX} {schnorr.npub(pub)}" not in g.get("text", ""):
        return "gist does not contain the npub"
    return ""


def build_allowlist(claims: list[dict], fetch, max_per_identity: int = 3) -> dict:
    accepted, identities, rejected, per = [], {}, [], {}
    for c in sorted(claims, key=lambda c: (c.get("created_at", 0), c.get("github", ""), c.get("pubkey", ""))):
        why = verify_claim(c, fetch)
        pub = None
        if not why:
            pub = schnorr.decode_key(c.get("pubkey") or c.get("npub"), "npub")
            login = c["github"].lower()
            if pub in identities:
                why = "duplicate claim"
            elif per.get(login, 0) >= max_per_identity:
                why = f"limit of {max_per_identity} workbenches per GitHub identity reached"
        if why:
            rejected.append({"pubkey": pub or c.get("pubkey") or c.get("npub"), "github": c.get("github"), "reason": why})
            continue
        per[login] = per.get(login, 0) + 1
        accepted.append(pub)
        identities[pub] = login
    return {"pubkeys": accepted, "identities": identities, "rejected": rejected, "maxPerIdentity": max_per_identity}


def load_allowlist(path) -> dict:
    """JSON {pubkeys, identities} or a text file with one hex/npub per line."""
    with open(path, encoding="utf-8") as f:
        raw = f.read()
    try:
        d = json.loads(raw)
        if isinstance(d, list):
            d = {"pubkeys": d}
    except ValueError:
        d = {"pubkeys": [ln.strip() for ln in raw.splitlines() if ln.strip() and not ln.startswith("#")]}
    d["pubkeys"] = [schnorr.decode_key(p, "npub") for p in d.get("pubkeys", [])]
    d.setdefault("identities", {})
    return d


def nostr_rs_relay_toml(allow: dict) -> str:
    keys = ",\n  ".join(f'"{p}"' for p in allow["pubkeys"])
    return ("# Generated by `python -m community allowlist build` - paste into nostr-rs-relay config.toml\n"
            "[authorization]\n"
            "# only these authors may publish; reading stays open\n"
            f"pubkey_whitelist = [\n  {keys}\n]\n"
            "nip42_auth = true\n")


STRFRY_PLUGIN = '''#!/usr/bin/env python3
# Generated by `python -m community allowlist build`: strfry write-policy plugin (relay.writePolicy.plugin).
# Accepts events only from the Freedom allowlist; reading stays open. Reloads the list when the file changes.
import json, os, sys
ALLOW = {path!r}
_m, _keys = None, set()
def keys():
    global _m, _keys
    m = os.path.getmtime(ALLOW)
    if m != _m:
        with open(ALLOW, encoding="utf-8") as f:
            _keys, _m = set(json.load(f)["pubkeys"]), m
    return _keys
for line in sys.stdin:
    req = json.loads(line)
    ev = req.get("event", {{}})
    ok = ev.get("pubkey") in keys()
    print(json.dumps({{"id": ev.get("id"), "action": "accept" if ok else "reject",
                      "msg": "" if ok else "restricted: author is not on the Freedom allowlist"}}), flush=True)
'''


def strfry_plugin(allowlist_json_path: str) -> str:
    return STRFRY_PLUGIN.format(path=str(allowlist_json_path))
