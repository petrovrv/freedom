#!/usr/bin/python3
"""aiwb-secret: the sandbox user's own secret store (AI Workbench family sandbox).

Each user of aiwb-family has their own store in their home (on their own disk image):
~/.local/share/aiwb-secrets/secrets.json (real home of the uid, not $HOME), folder 0700, file 0600. Other sandbox users cannot see the home at all;
the owner has no account in the sandbox. Root of the distro (wsl -u root from Windows) can read it: that is the
trust boundary of the home PC.

  aiwb-secret set NAME [--account A]        value from the terminal (hidden) or from stdin; never from argv
  aiwb-secret get NAME [--account A]        print the value (for $(...) in scripts; do not print it into a chat)
  aiwb-secret has NAME [--account A]        exit 0 present, 4 missing
  aiwb-secret list                          names and accounts only
  aiwb-secret delete NAME [--account A]
  aiwb-secret exec NAME VAR -- CMD [ARGS]   run CMD with the value in environment variable VAR (no output of it)

Python: keyring.get_password("aiwb/NAME", "A") works too: PYTHON_KEYRING_BACKEND=aiwb_secret_store.SandboxKeyring
(set by /etc/profile.d/10-aiwb-env.sh).
Exit codes: 0 ok, 1 error, 2 bad usage, 4 missing.
"""
from __future__ import annotations

import fcntl
import getpass
import json
import os
import pwd
import re
import sys
from pathlib import Path

NAME_RE = re.compile(r"^(?:aiwb/)?([a-z0-9][a-z0-9._-]{0,63})$")
ACCOUNT_RE = re.compile(r"^[A-Za-z0-9@._+-]{1,128}$")
MAX_VALUE = 64 * 1024


class StoreError(Exception):
    pass


def store_dir() -> Path:
    """Always in the real home of the calling uid (not $HOME / $XDG_DATA_HOME, which the caller can point elsewhere)."""
    return Path(pwd.getpwuid(os.getuid()).pw_dir) / ".local" / "share" / "aiwb-secrets"


def _name(name: str) -> str:
    m = NAME_RE.match(name or "")
    if not m:
        raise StoreError("name: lowercase letters, digits, . _ - (up to 64), optionally aiwb/<name>")
    return m.group(1)


def _account(account: str | None) -> str:
    account = account or "default"
    if not ACCOUNT_RE.match(account):
        raise StoreError("account: letters, digits, @ . _ + - (up to 128)")
    return account


def _open_dir() -> Path:
    d = store_dir()
    old = os.umask(0o077)
    try:
        d.mkdir(parents=True, exist_ok=True)
    finally:
        os.umask(old)
    st = d.lstat()
    if not d.is_dir() or d.is_symlink() or st.st_uid != os.getuid():
        raise StoreError(f"{d} is not this user's own folder")
    os.chmod(d, 0o700)
    return d


class _Locked:
    def __init__(self, write: bool):
        self.write = write

    def __enter__(self):
        d = _open_dir()
        self.path = d / "secrets.json"
        self.lock = os.open(d / ".lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        fcntl.flock(self.lock, fcntl.LOCK_EX if self.write else fcntl.LOCK_SH)
        try:
            raw = self.path.read_text(encoding="utf-8") if self.path.exists() else "{}"
            self.data = json.loads(raw) if raw.strip() else {}
            if not isinstance(self.data, dict):
                raise ValueError
        except (OSError, ValueError):
            self.__exit__()
            raise StoreError(f"{self.path} is unreadable")
        return self

    def save(self):
        tmp = self.path.with_name(f".secrets.{os.getpid()}.tmp")
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(self.data, f, ensure_ascii=False)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, self.path)

    def __exit__(self, *exc):
        fcntl.flock(self.lock, fcntl.LOCK_UN)
        os.close(self.lock)


def get(name: str, account: str | None = None) -> str | None:
    n, a = _name(name), _account(account)
    if not (store_dir() / "secrets.json").exists():
        return None
    with _Locked(False) as s:
        v = (s.data.get(n) or {}).get(a)
        return v if isinstance(v, str) else None


def put(name: str, account: str | None, value: str) -> None:
    n, a = _name(name), _account(account)
    if not value or len(value) > MAX_VALUE or "\x00" in value:
        raise StoreError("value: empty, too long (64 KB) or with NUL")
    with _Locked(True) as s:
        s.data.setdefault(n, {})[a] = value
        s.save()


def delete(name: str, account: str | None = None) -> bool:
    n, a = _name(name), _account(account)
    with _Locked(True) as s:
        accounts = s.data.get(n) or {}
        if a not in accounts:
            return False
        del accounts[a]
        if not accounts:
            s.data.pop(n, None)
        s.save()
        return True


def names() -> list[tuple[str, str]]:
    if not (store_dir() / "secrets.json").exists():
        return []
    with _Locked(False) as s:
        return sorted((n, a) for n, acc in s.data.items() if isinstance(acc, dict) for a in acc)


def read_value() -> str:
    """The value from the terminal (hidden, asked twice) or from stdin (first line); never from argv."""
    if sys.stdin.isatty():
        v1 = getpass.getpass("value (hidden): ")
        v2 = getpass.getpass("again: ")
        if v1 != v2:
            raise StoreError("the two entries differ")
        return v1.strip()
    try:
        data = sys.stdin.buffer.read(MAX_VALUE + 2).decode("utf-8", "strict")
    except UnicodeDecodeError:
        raise StoreError("value: not UTF-8")
    return data.splitlines()[0].strip() if data.strip() else ""


try:   # a keyring backend when the keyring package is installed (pip install keyring in a venv)
    from keyring.backend import KeyringBackend
    from keyring.errors import PasswordDeleteError, PasswordSetError

    class SandboxKeyring(KeyringBackend):
        """keyring backend over this user's aiwb-secret store (service aiwb/<name>); selected by
        PYTHON_KEYRING_BACKEND, so priority -1 keeps it out of keyring's own choice and chainer."""
        priority = -1

        def get_password(self, service, username):
            try:
                return get(service, username)
            except StoreError:
                return None

        def set_password(self, service, username, password):
            try:
                put(service, username, password)
            except StoreError as e:
                raise PasswordSetError(str(e))

        def delete_password(self, service, username):
            if not delete(service, username):
                raise PasswordDeleteError("not found")
except ImportError:
    pass


def main(argv=None) -> int:
    import argparse
    p = argparse.ArgumentParser(prog="aiwb-secret", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
                                allow_abbrev=False)
    sub = p.add_subparsers(dest="cmd", required=True)
    for c in ("set", "get", "has", "delete"):
        s = sub.add_parser(c)
        s.add_argument("name")
        s.add_argument("--account", default="default")
    sub.add_parser("list")
    s = sub.add_parser("exec")
    s.add_argument("name")
    s.add_argument("var")
    s.add_argument("--account", default="default")
    s.add_argument("command", nargs=argparse.REMAINDER)
    a = p.parse_args(argv)
    try:
        if a.cmd == "set":
            put(a.name, a.account, read_value())
            print(f"saved {_name(a.name)} / {_account(a.account)}")
        elif a.cmd in ("get", "has"):
            v = get(a.name, a.account)
            if v is None:
                print(f"missing {_name(a.name)} / {_account(a.account)}", file=sys.stderr)
                return 4
            if a.cmd == "get":
                sys.stdout.write(v + ("\n" if sys.stdout.isatty() else ""))
            else:
                print("present")
        elif a.cmd == "delete":
            if not delete(a.name, a.account):
                print("missing", file=sys.stderr)
                return 4
            print("deleted")
        elif a.cmd == "list":
            for n, acc in names():
                print(f"{n}\t{acc}")
        elif a.cmd == "exec":
            cmd = a.command[1:] if a.command[:1] == ["--"] else a.command
            if not cmd or not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", a.var):
                print("usage: aiwb-secret exec NAME VAR -- CMD [ARGS]", file=sys.stderr)
                return 2
            v = get(a.name, a.account)
            if v is None:
                print(f"missing {_name(a.name)} / {_account(a.account)}", file=sys.stderr)
                return 4
            os.execvpe(cmd[0], cmd, {**os.environ, a.var: v})
    except (StoreError, OSError) as e:
        print(f"aiwb-secret: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
