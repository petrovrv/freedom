"""Gmail over IMAP: find messages by a Gmail search query, add a label, mark read, archive.

Uses the IMAP app password from keyring aiwb/gmail (same as gmail-draft / gmail-attachments).
Never deletes and never sends. Dry run by default: lists matches; --apply changes them.
"""
import argparse
import email
import email.header
import email.policy
import html
import imaplib
import json
import os
import re
import sys
from pathlib import Path

import keyring

HOST = "imap.gmail.com"
CHUNK = 300
SERVICE = "aiwb/gmail"


def install_get(key, default=None):
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


def default_account():
    """Gmail address: install.json mail.address, else the e-mail of the owner (members, role owner), else None."""
    owner = next((m for m in install_get("members", []) or [] if m.get("role") == "owner"), {})
    return install_get("mail.address") or owner.get("email") or None



def load_presets():
    """Named Gmail queries for --preset and rules files: install.json mail.labelPresets {name: query}
    (e.g. "job-echo" for automatic job-application echoes)."""
    presets = install_get("mail.labelPresets") or {}
    return {k: v for k, v in presets.items() if isinstance(v, str)} if isinstance(presets, dict) else {}


PRESETS = load_presets()


def utf7(name):
    """IMAP modified UTF-7 (RFC 3501 5.1.3) so non-ASCII label names (e.g. Cyrillic) pass imaplib."""
    import base64
    out, buf = [], ""
    for ch in name + "\0":
        if ch != "\0" and not (0x20 <= ord(ch) <= 0x7E):
            buf += ch
            continue
        if buf:
            b = base64.b64encode(buf.encode("utf-16-be")).decode().rstrip("=").replace("/", ",")
            out.append("&" + b + "-")
            buf = ""
        if ch != "\0":
            out.append("&-" if ch == "&" else ch)
    return "".join(out)


def q(name, encode=True):
    """Quote a mailbox/label name; encode=False for names already in modified UTF-7 (from LIST)."""
    if encode:
        name = utf7(name)
    return '"' + name.replace("\\", "\\\\").replace('"', '\\"') + '"'


def dec(s):
    parts = []
    for b, cs in email.header.decode_header(s or ""):
        if isinstance(b, bytes):
            try:
                b = b.decode(cs or "utf-8", "replace")
            except LookupError:  # unknown-8bit and other non-codecs
                b = b.decode("utf-8", "replace")
        parts.append(b)
    return "".join(parts).replace("\r", "").replace("\n", " ").strip()


def find_box(imap, flag):
    typ, data = imap.list()
    for line in data:
        s = line.decode("utf-8", "replace")
        if flag in s:
            return s.rsplit(' "/" ', 1)[-1].strip().strip('"')
    sys.exit(f"mailbox with {flag} not found")


def read_guard(obj, source):
    """read-guard wraps mail text with signs of prompt injection as data (tools/read-guard; no-op if missing)."""
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "read-guard"))
        import read_guard as rg
        if isinstance(obj, (dict, list)):
            return rg.guard_obj(obj, source, "gmail-label")
        return rg.guard_text(obj, source, "gmail-label")
    except Exception:
        return obj


def senders_summary(rows):
    """Group messages by the registrable part of the sender domain (mail.notion.so -> notion.so)."""
    from email.utils import parseaddr, parsedate_to_datetime
    groups = {}
    for r in rows:
        name, addr = parseaddr(r["from"])
        host = addr.rsplit("@", 1)[-1].lower() if "@" in addr else (addr.lower() or "?")
        parts = host.split(".")
        n = 3 if len(parts) > 2 and parts[-2] in ("co", "com", "org", "net", "ac", "gov") and len(parts[-1]) == 2 else 2
        dom = ".".join(parts[-n:])
        try:
            when = parsedate_to_datetime(r["date"]).strftime("%Y-%m-%d")
        except Exception:
            when = ""
        g = groups.setdefault(dom, {"domain": dom, "count": 0, "last": "", "first": "9999", "names": [], "addresses": [], "subjects": []})
        g["count"] += 1
        if when:
            g["last"], g["first"] = max(g["last"], when), min(g["first"], when)
        if name and name not in g["names"] and len(g["names"]) < 3:
            g["names"].append(name)
        if addr and addr not in g["addresses"] and len(g["addresses"]) < 3:
            g["addresses"].append(addr)
        if r["subject"] and r["subject"] not in g["subjects"]:
            g["subjects"] = ([r["subject"]] + g["subjects"])[:3] if when == g["last"] else (g["subjects"] + [r["subject"]])[:3]
    doms = sorted(groups.values(), key=lambda g: -g["count"])
    return {"ok": True, "count": len(rows), "domains": doms}


def search(imap, query, limit):
    imap.literal = query.encode("utf-8")
    typ, data = imap.uid("SEARCH", "CHARSET", "UTF-8", "X-GM-RAW")
    return data[0].split()[-limit:] if data and data[0] else []


def fetch_headers(imap, uids):
    rows = []
    for i in range(0, len(uids), CHUNK):
        typ, d = imap.uid("FETCH", b",".join(uids[i:i + CHUNK]), "(UID FLAGS BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)])")
        for part in d:
            if isinstance(part, tuple):
                hdr = email.message_from_bytes(part[1])
                rows.append({"date": hdr.get("Date", "")[:16], "from": dec(hdr.get("From"))[:40], "subject": dec(hdr.get("Subject"))[:90]})
    return rows


def store(imap, uids, label=None, read=False, archive=False, unarchive=False):
    if label:
        imap.create(q(label))  # error "already exists" is fine
    for i in range(0, len(uids), CHUNK):
        chunk = b",".join(uids[i:i + CHUNK])
        if label:
            imap.uid("STORE", chunk, "+X-GM-LABELS", "(" + q(label) + ")")
        if read:
            imap.uid("STORE", chunk, "+FLAGS", "(\\Seen)")
        if archive:
            imap.uid("STORE", chunk, "-X-GM-LABELS", "(\\Inbox)")
        if unarchive:
            imap.uid("STORE", chunk, "+X-GM-LABELS", "(\\Inbox)")


def run_rules(imap, path, extra, apply, limit, samples):
    """Rules file: {"base": "<query ANDed to every rule>", "rules": [{"name", "query" | "preset", "label", "read", "archive"}]}.
    Rules run in order; a message caught by an earlier rule is not counted again."""
    spec = json.loads(Path(path).read_text(encoding="utf-8"))
    seen, out = set(), []
    for r in spec["rules"]:
        if r.get("enabled") is False:
            continue
        query = " ".join(x for x in (spec.get("base", ""), PRESETS.get(r.get("preset"), ""), r.get("query", ""), extra) if x)
        uids = [u for u in search(imap, query, limit) if u not in seen]
        seen.update(uids)
        if apply and uids:
            store(imap, uids, r.get("label"), r.get("read", False), r.get("archive", False))
        out.append({"name": r["name"], "label": r.get("label"), "count": len(uids), "sample": fetch_headers(imap, uids[-samples:]) if samples else []})
    return {"ok": True, "applied": apply, "total": len(seen), "rules": out}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--account", default=default_account(),
                    help="Gmail address (default: install.json mail.address, else the owner's email)")
    ap.add_argument("--preset", choices=sorted(PRESETS) or None,
                    help="named query from install.json mail.labelPresets; --query is then ANDed to it")
    ap.add_argument("--query", default="", help="Gmail search syntax (X-GM-RAW), e.g. 'in:inbox subject:(thank you for applying)'")
    ap.add_argument("--label", help="label to add (nested with /); created if missing")
    ap.add_argument("--read", action="store_true", help="mark as read")
    ap.add_argument("--archive", action="store_true", help="remove from Inbox")
    ap.add_argument("--unarchive", action="store_true", help="put back into Inbox (undo --archive), e.g. --query 'label:Marketing from:shop.be' --unarchive --apply")
    ap.add_argument("--apply", action="store_true", help="make the changes (default: dry run, list only)")
    ap.add_argument("--max", type=int, default=500)
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--body", action="store_true", help="include message text and Message-ID; BODY.PEEK does not mark read")
    ap.add_argument("--senders", action="store_true", help="summary by sender domain (count, last date, sample subjects) instead of a message list; read only")
    ap.add_argument("--rules", help="JSON rules file: several label/read/archive rules in one run (--query is ANDed to each)")
    ap.add_argument("--samples", type=int, default=3, help="with --rules: newest N messages shown per rule")
    a = ap.parse_args()
    if a.senders and a.apply:
        sys.exit("--senders is read only, drop --apply")

    if not a.account:
        sys.exit("no Gmail address: give --account or set mail.address (or the owner's email) in install.json")
    if a.preset and a.preset not in PRESETS:
        sys.exit(f"unknown preset {a.preset!r}: define it in install.json mail.labelPresets")
    query = " ".join(x for x in (PRESETS.get(a.preset, ""), a.query) if x)
    if not query and not a.rules:
        sys.exit("give --query, --preset or --rules")
    pw = keyring.get_password(SERVICE, a.account)
    if not pw:
        sys.exit(f"No keyring entry {SERVICE} / {a.account}. Run: python gmail-attachments.py login --account {a.account}")
    imap = imaplib.IMAP4_SSL(HOST)
    imap.login(a.account, pw)
    try:
        allbox = find_box(imap, r"\All")
        imap.select(q(allbox, False), readonly=not a.apply)
        if a.rules:
            res = run_rules(imap, a.rules, a.query, a.apply, a.max, a.samples)
            if a.json:
                print(json.dumps(read_guard(res, f"Gmail rules {a.rules}"), ensure_ascii=False))
            else:
                lines = [f"{'APPLIED' if a.apply else 'DRY RUN'}: {res['total']} message(s) by {len(res['rules'])} rule(s)"]
                for r in res["rules"]:
                    lines.append(f"{r['count']:6}  {r['name']}  -> {r['label'] or '-'}")
                    lines += [f"          {s['date']:16} | {s['from']:40} | {s['subject']}" for s in r["sample"]]
                print(read_guard("\n".join(lines), f"Gmail rules {a.rules}"))
            return
        uids = search(imap, query, a.max)
        rows = []
        # one FETCH/STORE per chunk of UIDs, not per message (thousands of messages took > 30 min)
        chunks = [b",".join(uids[i:i + CHUNK]) for i in range(0, len(uids), CHUNK)]
        for chunk in chunks:
            fields = "BODY.PEEK[]" if a.body else "BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)]"
            typ, d = imap.uid("FETCH", chunk, "(UID FLAGS X-GM-LABELS " + fields + ")")
            for part in d:
                if not isinstance(part, tuple):
                    continue
                meta = part[0].decode("utf-8", "replace")
                m = re.search(r"UID (\d+)", meta)
                hdr = email.message_from_bytes(part[1])
                rows.append({"uid": m.group(1) if m else "", "date": hdr.get("Date", "")[:31], "from": dec(hdr.get("From")),
                             "subject": dec(hdr.get("Subject")), "unread": "\\Seen" not in meta,
                             "inbox": "\\Inbox" in meta})
                if a.body:
                    msg = email.message_from_bytes(part[1], policy=email.policy.default)
                    body = msg.get_body(preferencelist=("plain", "html"))
                    content = body.get_content() if body else ""
                    if body and body.get_content_type() == "text/html":
                        content = re.sub(r"(?is)<(script|style)\b[^>]*>.*?</\1>", "", content)
                        content = html.unescape(re.sub(r"<[^>]+>", " ", content))
                    rows[-1].update(message_id=str(msg.get("Message-ID", "")), body=content)
        if a.apply and uids:
            store(imap, uids, a.label, a.read, a.archive, a.unarchive)
        if a.senders:
            res = senders_summary(rows)
            if a.json:
                print(json.dumps(read_guard(res, f"Gmail {query}"), ensure_ascii=False))
            else:
                lines = [f"{res['count']} message(s), {len(res['domains'])} sender domain(s)"]
                for d in res["domains"]:
                    lines.append(f"{d['count']:6} {d['last'][:10]} {d['domain'][:32]:32} | {', '.join(d['names'])[:40]:40} | {' / '.join(d['subjects'])[:110]}")
                print(read_guard("\n".join(lines), f"Gmail {query}"))
            return
        res = {"ok": True, "applied": bool(a.apply), "count": len(rows), "messages": rows}
        if a.json:
            print(json.dumps(read_guard(res, f"Gmail {query}"), ensure_ascii=False))
        else:
            lines = [f"{'APPLIED' if a.apply else 'DRY RUN'}: {len(rows)} message(s)"]
            for r in rows:
                lines.append(f"{'U' if r['unread'] else ' '}{'I' if r['inbox'] else ' '} {r['date'][:16]:16} | {r['from'][:40]:40} | {r['subject'][:90]}")
                if a.body:
                    lines.append(r["body"])
            print(read_guard("\n".join(lines), f"Gmail {query}"))
    finally:
        imap.logout()


if __name__ == "__main__":
    main()
