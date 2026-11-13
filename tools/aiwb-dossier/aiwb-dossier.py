#!/usr/bin/env python3
"""Read and fix the dossier of an AI Workbench environment (about me / people / current affairs) in the CO database."""

from __future__ import annotations

import argparse
import base64
import gzip
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from typing import Any

import keyring

KEYRING_SERVICE = "aiwb/co-push"
KEYRING_ACCOUNT = "default"
USER_AGENT = "aiwb-dossier/0.2.1"
TIMEOUT = 60
BACKOFFS = (3, 6)
SECTIONS = ("me", "people", "state")


class CliError(Exception):
    pass


def install_get(key: str, default: Any = None) -> Any:
    """One value of install.json: AIWB_INSTALL, else %LOCALAPPDATA%/AIWorkbench/install.json (Windows), else
    ~/.config/aiwb/install.json; the default when the file or the key is missing."""
    path = os.environ.get("AIWB_INSTALL") or (
        os.path.join(os.environ["LOCALAPPDATA"], "AIWorkbench", "install.json")
        if os.name == "nt" and os.environ.get("LOCALAPPDATA")
        else os.path.join(os.path.expanduser("~"), ".config", "aiwb", "install.json"))
    try:
        with open(path, encoding="utf-8-sig") as f:
            cur = json.load(f)
    except (OSError, ValueError):
        return default
    for part in key.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return default
        cur = cur[part]
    return cur


def endpoint() -> str:
    """db.php of the site: AIWB_DB_URL, else install.json site.url + /agent/db.php."""
    if os.environ.get("AIWB_DB_URL"):
        return os.environ["AIWB_DB_URL"]
    url = install_get("site.url")
    if not url:
        raise CliError("error: site unknown - set site.url in install.json or AIWB_DB_URL")
    return url.rstrip("/") + "/agent/db.php"


def owner_env() -> str:
    """Environment of the owner: AIWB_OWNER_ENV, else the member with role owner in install.json, else "owner"."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    owner = next((m for m in install_get("members", []) or [] if m.get("role") == "owner"), {})
    return owner.get("env") or "owner"


def call(params: dict[str, Any]) -> dict[str, Any]:
    key = keyring.get_password(KEYRING_SERVICE, KEYRING_ACCOUNT)
    if not key:
        raise CliError(f"error: no key in keyring {KEYRING_SERVICE} (account {KEYRING_ACCOUNT})")
    raw = json.dumps({"op": "dossier_host", **params}, ensure_ascii=False).encode("utf-8")
    body = json.dumps({"gz": base64.b64encode(gzip.compress(raw)).decode("ascii")}).encode("utf-8")
    headers = {"Content-Type": "application/json", "X-CO-Push-Key": key, "User-Agent": USER_AGENT}
    last = ""
    url = endpoint()
    for attempt in range(len(BACKOFFS) + 1):
        req = urllib.request.Request(url, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            text = exc.read().decode("utf-8", errors="replace")
            if 400 <= exc.code < 500:
                raise CliError(f"error: HTTP {exc.code} {text[:300]}") from exc
            last = f"error: HTTP {exc.code} {text[:300]}"
        except (urllib.error.URLError, TimeoutError) as exc:
            if os.environ.get("CODEX_SANDBOX_NETWORK_DISABLED") == "1":
                raise CliError("error: no network in the Codex sandbox; run through the aiwb-tools MCP (tool_run aiwb-dossier ...)") from exc
            last = f"error: {getattr(exc, 'reason', exc)}"
        if attempt < len(BACKOFFS):
            time.sleep(BACKOFFS[attempt])
    raise CliError(last or "error: request failed")


def fmt(r: dict[str, Any]) -> str:
    src = ""
    if r.get("chatId"):
        src = f" · {r['chatId']}" + (f" #{r['msg']}" if r.get("msg") else "")
    status = "" if r.get("status") == "active" else f" [{r.get('status')}]"
    subj = f"{r['subject']}: " if r.get("subject") else ""
    return f"d{r['id']} {r['section']}{status} {subj}{r['text']}  ({r.get('date') or '-'} · {r.get('author')}{src})"


CHECK_PROMPT = """You audit the dossier of one person (the user, "me") kept by AI agents: facts about the user (me), current affairs (state) and people around them (people). Rows were written at different dates by different authors (import = bulk import from old chats, often stale or garbled; secretary/agent = extracted live; human = typed by the owner).

Find rows that CONTRADICT each other: same person with different ages or birth dates, swapped names (facts of one person attached to another), different universities/cities/jobs/relations for the same moment, a past plan stated as current fact when newer rows show what happened. Ignore rows that merely differ in detail or describe different moments without conflict.

Life moves on: when rows conflict, the NEWER row (later date) is right unless it is clearly garbled; facts confirmed by several newer rows beat one old row. Fix the older row: rewrite it in the same language and style (keep its other facts), or delete it when nothing true is left.

Answer ONLY with JSON: {"conflicts":[{"ids":["d12","d40"],"person":"...","problem":"one sentence","fix":{"id":"d12","action":"edit|delete|ask","text":"new full text for edit"},"question":"for action ask only: what to ask the owner"}]}. Use action ask when the rows do not show which is true. Empty list if no conflicts.

Rows (id section [status] subject: text (date · author · source)):
"""


def run_check(env: str, model: str, apply: bool, as_json: bool) -> int:
    rows = call({"env": env, "action": "list", "all": False}).get("rows", [])
    rows = [r for r in rows if r.get("status") == "active"]
    if not rows:
        print("no rows")
        return 0
    ids = {f"d{r['id']}" for r in rows}
    prompt = CHECK_PROMPT + "\n".join(fmt(r) for r in rows)
    exe = shutil.which("claude") or "claude"
    cmd = [exe, "-p", "--model", model, "--tools", "", "--strict-mcp-config", "--no-session-persistence",
           "--output-format", "json"]
    try:
        out = subprocess.run(cmd, input=prompt, capture_output=True, text=True, encoding="utf-8", timeout=900)
    except FileNotFoundError as exc:
        raise CliError("error: claude CLI not found") from exc
    if out.returncode != 0:
        raise CliError(f"error: claude exit {out.returncode}: {(out.stderr or out.stdout)[:300]}")
    reply = json.loads(out.stdout).get("result", "")
    m = re.search(r"\{.*\}", reply, re.S)
    if not m:
        raise CliError(f"error: no JSON in the model answer: {reply[:300]}")
    conflicts = [c for c in json.loads(m.group(0)).get("conflicts", []) if isinstance(c, dict)]
    applied = 0
    for c in conflicts:
        fix = c.get("fix") or {}
        fid, act = str(fix.get("id", "")), fix.get("action")
        c["applied"] = False
        if not apply or fid not in ids:
            continue
        if act == "edit" and fix.get("text"):
            call({"env": env, "action": "edit", "id": fid, "text": fix["text"]})
        elif act == "delete":
            call({"env": env, "action": "delete", "id": fid})
        else:
            continue
        c["applied"] = True
        applied += 1
    if as_json:
        print(json.dumps({"rows": len(rows), "conflicts": conflicts, "applied": applied}, ensure_ascii=False, indent=1))
        return 0
    if not conflicts:
        print(f"{len(rows)} rows, no conflicts")
        return 0
    for c in conflicts:
        fix = c.get("fix") or {}
        mark = "applied" if c["applied"] else fix.get("action", "?")
        print(f"[{mark}] {', '.join(c.get('ids', []))} {c.get('person', '')}: {c.get('problem', '')}")
        if fix.get("action") == "edit":
            print(f"    {fix.get('id')} -> {fix.get('text', '')}")
        elif fix.get("action") == "delete":
            print(f"    delete {fix.get('id')}")
        elif c.get("question"):
            print(f"    ? {c['question']}")
    print(f"{len(rows)} rows, {len(conflicts)} conflicts, {applied} applied" + ("" if apply else " (dry run; --apply to fix)"))
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--env", default=os.environ.get("AIWB_ENV") or owner_env(),
                    help="environment (default $AIWB_ENV, else the owner's from install.json)")
    ap.add_argument("--json", action="store_true", help="raw server answer")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("list", help="active rows (all statuses with --all)")
    p.add_argument("--all", action="store_true")
    p.add_argument("--section", choices=SECTIONS)
    p = sub.add_parser("search", help="full-text search over subject and text")
    p.add_argument("words", nargs="+")
    p.add_argument("--limit", type=int, default=30)
    p = sub.add_parser("context", help="the block new agent sessions get")
    p.add_argument("--people-only", action="store_true")
    p = sub.add_parser("add", help="add a row")
    p.add_argument("section", choices=SECTIONS)
    p.add_argument("subject", help="person (people), topic (state), short key or '' (me)")
    p.add_argument("text")
    p.add_argument("--chat", help="source chat id")
    p.add_argument("--msg", type=int, help="source message number")
    p.add_argument("--date", help="YYYY-MM-DD when the fact was said (default today)")
    p = sub.add_parser("edit", help="change a row in place")
    p.add_argument("id")
    p.add_argument("--text")
    p.add_argument("--subject")
    p.add_argument("--section", choices=SECTIONS)
    for name, hlp in (("close", "a finished affair (state)"), ("delete", "a wrong row"), ("restore", "back to active")):
        p = sub.add_parser(name, help=hlp)
        p.add_argument("id")
    p = sub.add_parser("import", help="bulk add rows from a JSON file: [{section, subject, text, chatId?, msg?, date?}]")
    p.add_argument("file")
    p.add_argument("--model", help="model that extracted the rows")
    p = sub.add_parser("check", help="find contradicting rows with a model (newer wins); --apply edits/deletes the older rows")
    p.add_argument("--model", default="sonnet")
    p.add_argument("--apply", action="store_true")
    a = ap.parse_args()

    if a.cmd == "check":
        return run_check(a.env, a.model, a.apply, a.json)

    base: dict[str, Any] = {"env": a.env, "action": a.cmd}
    if a.cmd == "list":
        base["all"] = a.all
    elif a.cmd == "search":
        base.update(q=" ".join(a.words), limit=a.limit)
    elif a.cmd == "context":
        base["peopleOnly"] = a.people_only
    elif a.cmd == "add":
        base.update(section=a.section, subject=a.subject, text=a.text)
        if a.chat:
            base["chatId"] = a.chat
        if a.msg is not None:
            base["msg"] = a.msg
        if a.date:
            base["date"] = a.date
    elif a.cmd == "edit":
        base["id"] = a.id
        for k in ("text", "subject", "section"):
            if getattr(a, k) is not None:
                base[k] = getattr(a, k)
    elif a.cmd in ("close", "delete", "restore"):
        base["id"] = a.id
    elif a.cmd == "import":
        with open(a.file, encoding="utf-8") as f:
            rows = json.load(f)
        if not isinstance(rows, list):
            raise CliError("error: the file must hold a JSON list")
        added = skipped = 0
        for i in range(0, len(rows), 400):
            r = call({**base, "rows": rows[i:i + 400], **({"model": a.model} if a.model else {})})
            added += int(r.get("added", 0))
            skipped += int(r.get("skipped", 0))
        print(json.dumps({"added": added, "skipped": skipped}) if a.json else f"added {added}, skipped {skipped}")
        return 0

    r = call(base)
    if a.json:
        print(json.dumps(r, ensure_ascii=False, indent=1))
    elif "rows" in r:
        rows = r["rows"]
        if a.cmd == "list" and a.section:
            rows = [x for x in rows if x.get("section") == a.section]
        print("\n".join(fmt(x) for x in rows) if rows else "no rows")
    elif "text" in r:
        print(r["text"] or "(empty dossier)")
    elif "id" in r:
        print(f"d{r['id']}")
    else:
        print("ok" if r.get("ok") else "no change (no such row?)")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except CliError as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
