"""Download Gmail attachments over IMAP using a Gmail search query.

Credentials: Google app password in keyring, service "aiwb/gmail", account = Gmail address.
`login` asks for the password in a GUI dialog, verifies it against IMAP and stores it.
"""
import argparse
import email
import email.header
import email.utils
import imaplib
import json
import os
import re
import sys
from pathlib import Path

SERVICE = "aiwb/gmail"
HOST = "imap.gmail.com"


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


def read_guard(obj, source):
    """read-guard wraps mail text with signs of prompt injection as data (tools/read-guard; no-op if missing)."""
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "read-guard"))
        import read_guard as rg
        return rg.guard_obj(obj, source, "gmail-attachments")
    except Exception:
        return obj


def decode(value):
    if not value:
        return ""
    parts = []
    for text, charset in email.header.decode_header(value):
        if isinstance(text, bytes):
            text = text.decode(charset or "utf-8", errors="replace")
        parts.append(text)
    return "".join(parts).strip()


def safe_name(name):
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .")
    return name or "attachment"


def unique_path(folder, name):
    base, ext = os.path.splitext(name)
    path, n = os.path.join(folder, name), 1
    while os.path.exists(path):
        n += 1
        path = os.path.join(folder, f"{base} ({n}){ext}")
    return path


def connect(account, password):
    imap = imaplib.IMAP4_SSL(HOST)
    imap.login(account, password)
    return imap


def all_mail_box(imap):
    # Folder name is localized ("[Gmail]/All Mail", "[Gmail]/Вся почта"), find it by the \All flag.
    _, boxes = imap.list()
    for line in boxes:
        line = line.decode("utf-8", errors="replace")
        if "\\All" in line:
            # LIST already quotes the name (modified UTF-7); return it bare, select() adds the quotes.
            return line.rsplit(' "/" ', 1)[-1].strip().strip('"')
    return "INBOX"


def ask_password(account):
    import tkinter
    from tkinter import simpledialog
    root = tkinter.Tk()
    root.withdraw()
    root.attributes("-topmost", True)
    pw = simpledialog.askstring(
        "AI Workbench: Gmail",
        f"App password for {account}\n(Google Account > Security > App passwords):",
        show="*", parent=root)
    root.destroy()
    return normalize_app_password(pw or "")


# Russian (JCUKEN) key -> the Latin (QWERTY) key in the same position, for passwords typed in the wrong layout.
RU_TO_EN = dict(zip("йцукенгшщзхъфывапролджэячсмитьбюЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ",
                    "qwertyuiop[]asdfghjkl;'zxcvbnm,.QWERTYUIOP[]ASDFGHJKL;'ZXCVBNM,."))


def normalize_app_password(pw):
    # App passwords are 16 Latin letters shown in groups of four; Google accepts them in any case.
    return "".join(RU_TO_EN.get(ch, ch) for ch in pw).replace(" ", "").lower()


def get_password(account):
    import keyring
    pw = keyring.get_password(SERVICE, account)
    if not pw:
        sys.exit(f"No keyring entry {SERVICE} / {account}. Run: python gmail-attachments.py login --account {account}")
    return pw


def show(title, text, error=False):
    # Message box, so the result is visible when started via pythonw (no console).
    import tkinter
    from tkinter import messagebox
    root = tkinter.Tk()
    root.withdraw()
    root.attributes("-topmost", True)
    (messagebox.showerror if error else messagebox.showinfo)(title, text, parent=root)
    root.destroy()


def cmd_login(args):
    import keyring
    for _ in range(3):
        pw = ask_password(args.account)
        if not pw:
            sys.exit("cancelled")
        try:
            connect(args.account, pw).logout()
        except (imaplib.IMAP4.error, OSError) as e:
            msg = e.args[0].decode(errors="replace") if e.args and isinstance(e.args[0], bytes) else str(e)
            print(f"IMAP login failed: {msg}", file=sys.stderr)
            show("AI Workbench: Gmail", f"Gmail rejected the login for {args.account}:\n{msg}\n\n"
                 "Use a 16-character app password, not the account password.", error=True)
            continue
        keyring.set_password(SERVICE, args.account, pw)
        print(f"saved {SERVICE} / {args.account}")
        show("AI Workbench: Gmail", f"Saved to keyring: {SERVICE} / {args.account}")
        return
    sys.exit("login failed 3 times")


def cmd_fetch(args):
    imap = connect(args.account, get_password(args.account))
    try:
        imap.select(f'"{all_mail_box(imap)}"' if not args.mailbox else args.mailbox, readonly=True)
        if args.query.isascii():
            typ, data = imap.uid("SEARCH", "X-GM-RAW", '"' + args.query.replace('"', '\\"') + '"')
        else:
            # Non-ASCII query goes as an IMAP literal.
            imap.literal = args.query.encode("utf-8")
            typ, data = imap.uid("SEARCH", "CHARSET", "UTF-8", "X-GM-RAW")
        if typ != "OK":
            sys.exit(f"search failed: {data}")
        uids = data[0].split()[-args.limit:] if args.limit else data[0].split()
        exts = {e.lower().lstrip(".") for e in args.ext} if args.ext else None
        if not args.list:
            os.makedirs(args.out, exist_ok=True)
        result = []
        for uid in uids:
            _, msg_data = imap.uid("FETCH", uid, "(BODY.PEEK[])")
            msg = email.message_from_bytes(msg_data[0][1])
            date = email.utils.parsedate_to_datetime(msg["Date"]) if msg["Date"] else None
            item = {"uid": uid.decode(), "date": date.isoformat() if date else "",
                    "from": decode(msg["From"]), "subject": decode(msg["Subject"]), "attachments": []}
            for part in msg.walk():
                fname = part.get_filename()
                if not fname or part.get_content_maintype() == "multipart":
                    continue
                fname = safe_name(decode(fname))
                if exts and os.path.splitext(fname)[1].lower().lstrip(".") not in exts:
                    continue
                payload = part.get_payload(decode=True) or b""
                att = {"name": fname, "size": len(payload)}
                if not args.list:
                    name = f"{date:%Y-%m-%d} {fname}" if args.date_prefix and date else fname
                    path = unique_path(args.out, name)
                    with open(path, "wb") as f:
                        f.write(payload)
                    att["path"] = path
                item["attachments"].append(att)
            result.append(item)
        print(json.dumps(read_guard(result, f"Gmail {args.query}"), ensure_ascii=False, indent=2))
    finally:
        imap.logout()


def main():
    p = argparse.ArgumentParser(allow_abbrev=False, description="Download Gmail attachments over IMAP by Gmail search query.")
    sub = p.add_subparsers(dest="cmd", required=True)
    lg = sub.add_parser("login", allow_abbrev=False, help="ask for the app password in a dialog, verify, store in keyring")
    lg.add_argument("--account", default=default_account(), help="Gmail address (default: install.json mail.address, else the owner's email)")
    f = sub.add_parser("fetch", allow_abbrev=False, help="find messages and save their attachments")
    f.add_argument("query", help='Gmail search, e.g. "from:fod.fgov.be has:attachment after:2025/01/01"')
    f.add_argument("--out", default=".", help="folder for saved files")
    f.add_argument("--account", default=default_account(), help="Gmail address (default: install.json mail.address, else the owner's email)")
    f.add_argument("--ext", nargs="*", help="only these extensions, e.g. pdf docx")
    f.add_argument("--limit", type=int, default=50, help="newest N messages (0 = all)")
    f.add_argument("--list", action="store_true", help="only list messages and attachments, save nothing")
    f.add_argument("--date-prefix", action="store_true", help="prefix file names with message date YYYY-MM-DD")
    f.add_argument("--mailbox", help="IMAP folder (default: All Mail)")
    args = p.parse_args()
    if not args.account:
        p.error("no Gmail address: give --account or set mail.address (or the owner's email) in install.json")
    {"login": cmd_login, "fetch": cmd_fetch}[args.cmd](args)


if __name__ == "__main__":
    main()
