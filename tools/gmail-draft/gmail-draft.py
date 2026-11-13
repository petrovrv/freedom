#!/usr/bin/env python3
"""gmail-draft.py - create a Gmail DRAFT over IMAP (never sends)."""
import argparse
import html
import imaplib
import json
import mimetypes
import os
import re
import sys
import time
from email import policy
from email.message import EmailMessage
from email.parser import BytesHeaderParser
from email.utils import formataddr, formatdate, getaddresses, make_msgid

import keyring

HOST = "imap.gmail.com"
SERVICE = "aiwb/gmail"
MAX_BYTES = 25 * 1024 * 1024
FIELDS = "MESSAGE-ID SUBJECT REFERENCES FROM REPLY-TO"


def q(name):
    """Quote a mailbox name for IMAP commands."""
    return '"' + name.replace("\\", "\\\\").replace('"', '\\"') + '"'


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


def password(account):
    pw = keyring.get_password(SERVICE, account)
    if not pw:
        sys.exit(f"No keyring entry {SERVICE} / {account}. "
                 f"Run: python gmail-attachments.py login --account {account}")
    return pw


def find_box(imap, flag):
    """Return the mailbox name carrying a special-use flag (e.g. \\All)."""
    typ, data = imap.list()
    if typ != "OK":
        sys.exit("IMAP LIST failed")
    for raw in data:
        line = raw.decode("utf-8", "replace") if isinstance(raw, bytes) else str(raw)
        m = re.match(r"\((.*?)\)", line)
        if m and flag.lower() in m.group(1).lower().split():
            return line.rsplit(' "/" ', 1)[-1].strip().strip('"')
    sys.exit(f"No mailbox with flag {flag} found")


def find_original(imap, query):
    """Search All Mail with X-GM-RAW, return parsed headers of the newest match."""
    typ, _ = imap.select(q(find_box(imap, r"\All")), readonly=True)
    if typ != "OK":
        sys.exit("Cannot select All Mail")
    uids = gm_search(imap, query)
    if not uids:
        sys.exit(f"No message matches --reply-to query: {query}")
    uid = max(uids, key=int)
    typ, data = imap.uid("FETCH", uid, f"(BODY.PEEK[HEADER.FIELDS ({FIELDS})])")
    raw = next((p[1] for p in data if isinstance(p, tuple)), None)
    if typ != "OK" or raw is None:
        sys.exit("Cannot fetch headers of matching message")
    return BytesHeaderParser(policy=policy.default).parsebytes(raw)


def gm_search(imap, query):
    """UID SEARCH X-GM-RAW in the selected mailbox; returns list of UIDs."""
    if query.isascii():
        typ, data = imap.uid("SEARCH", "X-GM-RAW", '"' + query.replace('"', '\\"') + '"')
    else:
        imap.literal = query.encode()
        typ, data = imap.uid("SEARCH", "CHARSET", "UTF-8", "X-GM-RAW")
    return data[0].split() if typ == "OK" and data and data[0] else []


def drop_old_drafts(imap, query, keep_id):
    """Delete drafts matching a Gmail query, except the one with Message-ID keep_id."""
    typ, _ = imap.select(q(find_box(imap, r"\Drafts")))
    if typ != "OK":
        sys.exit("Cannot select Drafts")
    removed = []
    for uid in gm_search(imap, query):
        typ, data = imap.uid("FETCH", uid, "(BODY.PEEK[HEADER.FIELDS (MESSAGE-ID SUBJECT TO)])")
        raw = next((p[1] for p in data if isinstance(p, tuple)), None)
        if raw is None:
            continue
        h = BytesHeaderParser(policy=policy.default).parsebytes(raw)
        if str(h.get("Message-ID", "")).strip() == keep_id:
            continue
        imap.uid("STORE", uid, "+FLAGS", r"(\Deleted)")
        removed.append({"to": str(h.get("To", "")), "subject": str(h.get("Subject", ""))})
    if removed:
        imap.expunge()
    return removed


def reply_subject(orig):
    subj = " ".join(str(orig.get("Subject", "")).split())
    return subj if re.match(r"(re|aw|antw)\s*:", subj, re.I) else "RE: " + subj


def parse_attach(spec):
    """Handle 'PATH=Display name.ext'; split on last '=' only when sensible."""
    if "=" in spec:
        left, right = spec.rsplit("=", 1)
        if os.path.splitext(right)[1] and os.path.isfile(left):
            return left, right
    return spec, os.path.basename(spec)


def html_to_text(s):
    s = re.sub(r"(?is)<(script|style).*?</\1>", "", s)
    s = re.sub(r"(?i)<br\s*/?>|</(p|div|li|tr|h[1-6])>", "\n", s)
    s = html.unescape(re.sub(r"<[^>]+>", "", s))
    return re.sub(r"\n{3,}", "\n\n", s).strip() + "\n"


def main():
    ap = argparse.ArgumentParser(allow_abbrev=False, description="Create a Gmail draft over IMAP (never sends).")
    ap.add_argument("--reply-to", metavar="QUERY", help="Gmail search; newest match is replied to")
    ap.add_argument("--to", nargs="*", default=[])
    ap.add_argument("--cc", nargs="*", default=[])
    ap.add_argument("--bcc", nargs="*", default=[])
    ap.add_argument("--subject")
    body = ap.add_mutually_exclusive_group(required=True)
    body.add_argument("--body")
    body.add_argument("--body-file")
    ap.add_argument("--html", action="store_true", help="body is HTML")
    ap.add_argument("--attach", nargs="+", default=[], metavar="PATH",
                    help="files; 'PATH=Name.pdf' renames the attachment")
    ap.add_argument("--from-name", default=install_get("mail.fromName") or "",
                    help="display name in From (default: install.json mail.fromName)")
    ap.add_argument("--account", default=default_account(),
                    help="Gmail address (default: install.json mail.address, else the owner's email)")
    ap.add_argument("--replace", metavar="QUERY",
                    help="after the new draft is saved, delete other drafts matching this Gmail search")
    ap.add_argument("--dry-run", action="store_true", help="build and print summary, do not upload")
    a = ap.parse_args()

    if not a.account:
        ap.error("no Gmail address: give --account or set mail.address (or the owner's email) in install.json")
    if not a.reply_to and not a.subject:
        ap.error("--subject is required without --reply-to")

    # Body
    if a.body_file:
        with open(a.body_file, encoding="utf-8") as f:
            text = f.read()
    else:
        text = a.body

    # Attachments: validate and check total size
    atts = []
    for spec in a.attach:
        path, name = parse_attach(spec)
        if not os.path.isfile(path):
            sys.exit(f"Attachment not found: {path}")
        atts.append((path, name, os.path.getsize(path)))
    total = sum(s for _, _, s in atts)
    if total >= MAX_BYTES:
        sys.exit(f"Attachments total {total / 1048576:.1f} MB, exceeds Gmail's 25 MB limit")

    imap = None
    try:
        need_imap = a.reply_to or not a.dry_run
        if need_imap:
            pw = password(a.account)
            imap = imaplib.IMAP4_SSL(HOST)
            imap.login(a.account, pw)

        to, subject, in_reply_to, refs = list(a.to), a.subject, None, None
        if a.reply_to:
            orig = find_original(imap, a.reply_to)
            if not a.to:
                src = orig.get("Reply-To") or orig.get("From") or ""
                to = [formataddr(p) for p in getaddresses([str(src)]) if p[1]]
            subject = a.subject or reply_subject(orig)
            in_reply_to = str(orig.get("Message-ID", "")).strip() or None
            if in_reply_to:
                refs = " ".join((str(orig.get("References", "")).split()) + [in_reply_to])

        if not to:
            sys.exit("No recipient: give --to or use --reply-to")

        # Build message
        msg = EmailMessage()
        msg["From"] = formataddr((a.from_name, a.account))
        msg["To"] = ", ".join(to)
        if a.cc:
            msg["Cc"] = ", ".join(a.cc)
        if a.bcc:
            msg["Bcc"] = ", ".join(a.bcc)
        msg["Subject"] = subject
        msg["Date"] = formatdate(localtime=True)
        msg["Message-ID"] = make_msgid(domain=a.account.split("@")[-1])
        if in_reply_to:
            msg["In-Reply-To"] = in_reply_to
            msg["References"] = refs
        if a.html:
            msg.set_content(html_to_text(text))
            msg.add_alternative(text, subtype="html")
        else:
            msg.set_content(text)
        for path, name, _ in atts:
            ctype = mimetypes.guess_type(name)[0] or mimetypes.guess_type(path)[0] \
                or "application/octet-stream"
            maintype, subtype = ctype.split("/", 1)
            with open(path, "rb") as f:
                msg.add_attachment(f.read(), maintype=maintype, subtype=subtype, filename=name)

        # Upload to Drafts
        append = None
        if not a.dry_run:
            typ, resp = imap.append(q(find_box(imap, r"\Drafts")), r"\Draft",
                                    imaplib.Time2Internaldate(time.time()), msg.as_bytes())
            append = b" ".join(r for r in resp if isinstance(r, bytes)).decode("utf-8", "replace")
            if typ != "OK":
                sys.exit(f"APPEND failed: {append}")
        replaced = (drop_old_drafts(imap, a.replace, msg["Message-ID"])
                    if a.replace and not a.dry_run else [])

        print(json.dumps({
            "status": "dry-run" if a.dry_run else "draft",
            "to": to,
            "cc": a.cc,
            "subject": subject,
            "in_reply_to": in_reply_to,
            "attachments": [{"name": n, "size": s} for _, n, s in atts],
            "append": append,
            "replaced": replaced,
        }, ensure_ascii=False, indent=2))
    except imaplib.IMAP4.error as e:
        sys.exit(f"IMAP error: {e}")
    finally:
        if imap is not None:
            try:
                imap.logout()
            except Exception:
                pass


if __name__ == "__main__":
    main()
