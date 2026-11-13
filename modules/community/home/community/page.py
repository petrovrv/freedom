# SPDX-License-Identifier: AGPL-3.0-or-later
"""The forum page of the module community (CO Web, ui.page "forum"): a short view built at home and put on the server.

The archive stays on the home PC (SQLite); the server keeps only this view for the owner's page: drafts waiting for a
yes, the inbox of the last tick, the current digest and the social budget. Foreign text is already escaped and wrapped
by read-guard (digest.render, agent inbox titles) and the page shows it as plain text, never as HTML.

  python -m community page            print the view (JSON)
  python -m community page --push     also put it on the server (push.php op community_page_put, key aiwb/co-push)
"""
from __future__ import annotations

import json
import time
import urllib.request

from . import config, digest, nostr

PUSH_OP = "community_page_put"
MAX_ITEMS = 30


def _text(v, n: int = 300) -> str:
    return " ".join(str(v or "").split())[:n]


def build(arch, wb, now: int | None = None) -> dict:
    now = int(time.time()) if now is None else now
    drafts = [{"id": d["id"], "kind": nostr.KIND_NAMES.get(d["kind"], d["kind"]), "status": d["status"],
               "created": d["created_at"], "note": _text(d["note"], 200)}
              for d in arch.drafts("pending")][:MAX_ITEMS]
    inbox = []
    if wb is not None:
        for it in wb.inbox()[:MAX_ITEMS]:
            inbox.append({"kind": str(it.get("kind", "")), "todo": _text(it.get("todo"), 120),
                          "title": _text(it.get("title"), 200), "author": str(it.get("author", ""))[:16]})
    d = digest.build(arch, wb.keys.pubkey, allow=wb.allow) if wb is not None else {"items": [], "markdown": ""}
    items = [{"title": _text(i["card"].get("title"), 200), "problem": _text(i["card"].get("problem")),
              "approach": _text(i["card"].get("approach")), "reputation": i.get("reputation"),
              "confirmations": i.get("confirmations"), "why": _text(i.get("why"), 200),
              "card": f"{i['author'][:12]}:{i['card']['id']}"} for i in d["items"]]
    budget = {}
    if wb is not None:
        try:
            budget = wb.social_budget().check()
        except Exception as e:  # noqa: BLE001 - the page shows the reason instead of failing
            budget = {"allowed": False, "error": _text(e, 200)}
    return {"v": 1, "updated": now, "drafts": drafts, "inbox": inbox, "digest": items, "budget": budget}


def push(view: dict, timeout: int = 20) -> dict:
    import keyring
    key = keyring.get_password("aiwb/co-push", "default")
    if not key:
        raise RuntimeError("no key in keyring aiwb/co-push (account default)")
    url = str((config.load_install().get("site") or {}).get("url") or "").rstrip("/")
    if not url:
        raise RuntimeError("install.json: site.url is needed")
    body = json.dumps({"op": PUSH_OP, "view": view}, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url + "/agent/push.php", data=body, method="POST", headers={
        "Content-Type": "application/json", "X-CO-Push-Key": key, "User-Agent": "freedom-community/1"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8") or "{}")
