"""Keyring access for long secrets (Windows Credential Manager holds at most 2560 bytes = 1280 UTF-16 chars).

Values longer than CHUNK chars are stored as parts in accounts "<account>#1".."<account>#N";
the main account then holds the marker "aiwb-chunked:N". Short values are stored as is.

    sys.path.insert(0, r"G:\\My Drive\\AI-Workbench\\tools\\secret-prompt")
    from aiwb_secret import get_secret
    token = get_secret("aiwb/chatgpt", "session-token")

0.4.0 (Freedom, "each person has their own keyring"): the keyring of this computer belongs to its owner. A job of a
household member (Bridge sets AIWB_JOB_ROLE=member, or the job's project belongs to another environment than the
owner's) gets MemberRefused (code own_device_first) here; for tools that call keyring directly Bridge also sets
PYTHON_KEYRING_BACKEND=aiwb_secret.MemberRefusedKeyring (and this folder on PYTHONPATH), so every keyring call of
such a job is refused in this one place.
"""
from __future__ import annotations

import json
import os
import re
from pathlib import Path

import keyring
import keyring.errors
from keyring.backend import KeyringBackend

CHUNK = 1000
MARKER = "aiwb-chunked:"

OWN_DEVICE_HINT = ("the keyring of this computer belongs to its owner; a household member's secret is entered on the "
                   "person's own device: device tool secret_prompt (mcp__dev_<device>__secret_prompt), and the tools "
                   "that need it run there (or in the person's sandbox: aiwb-secret inside aiwb-family)")
CHAT_RE = re.compile(r"CHAT-\d{14}-[A-Z0-9]{4}")


class MemberRefused(keyring.errors.KeyringError):
    """A household member's job asked for the owner's keyring."""
    code = "own_device_first"

    def __init__(self, env: str | None = None):
        self.environment = env
        who = f"environment '{env}'" if env else "a household member"
        super().__init__(f"own_device_first: this job belongs to {who}: {OWN_DEVICE_HINT}")


def owner_env() -> str | None:
    """The owner's environment: AIWB_OWNER_ENV, else the member with role "owner" in install.json (AIWB_INSTALL, else
    %LOCALAPPDATA%\\AIWorkbench\\install.json, else ~/.config/aiwb/install.json); None if unknown."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    if os.environ.get("AIWB_INSTALL"):
        cfg = Path(os.environ["AIWB_INSTALL"])
    elif os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        cfg = Path(os.environ["LOCALAPPDATA"]) / "AIWorkbench" / "install.json"
    else:
        cfg = Path.home() / ".config" / "aiwb" / "install.json"
    try:
        members = json.loads(cfg.read_text(encoding="utf-8-sig")).get("members") or []
        return next((m.get("env") for m in members if isinstance(m, dict) and m.get("role") == "owner"), None) or None
    except (OSError, ValueError, AttributeError):
        return None


def job_environment() -> str | None:
    """Environment of the calling job: AIWB_JOB_ENV (Bridge), else project.json of the working directory, else of the
    chat AIWB_CHAT_ID; None if unknown (same rule as browser-agent's own_device_first)."""
    if os.environ.get("AIWB_JOB_ENV"):
        return os.environ["AIWB_JOB_ENV"]
    try:
        cwd = Path(os.getcwd()).resolve()
    except OSError:
        return None
    proj = None
    for d in [cwd, *cwd.parents]:
        if d.parent.name == "projects" and (d / "project.json").is_file():
            proj = d / "project.json"
            break
    if proj is None:
        chat = os.environ.get("AIWB_CHAT_ID", "")
        if not CHAT_RE.fullmatch(chat):
            return None
        root = Path(os.environ.get("AIWB_PROJECTS_DIR", r"G:\My Drive\AI-Workbench\projects"))
        try:
            found = list(root.glob(f"*/chats/{chat}"))
        except OSError:
            found = []
        if not found:
            return None
        proj = found[0].parent.parent / "project.json"
    try:
        return json.loads(proj.read_text(encoding="utf-8-sig")).get("environmentId") or None
    except (OSError, ValueError, AttributeError):
        return None


def job_role() -> str:
    """"owner" or "member". Bridge's AIWB_JOB_ROLE decides; without it a job whose environment is known and differs
    from the known owner's is a member's (only inside an agent job: AIWB_JOB_ENV / AIWB_JOB_ID / AIWB_CHAT_ID set). Unknown = owner."""
    role = (os.environ.get("AIWB_JOB_ROLE") or "").strip().lower()
    if role in ("owner", "member"):
        return role
    if not (os.environ.get("AIWB_JOB_ENV") or os.environ.get("AIWB_JOB_ID") or os.environ.get("AIWB_CHAT_ID")):
        return "owner"   # not an agent job (the owner's own scripts, tasks, CO Desktop): no guessing from folders
    env, owner = job_environment(), owner_env()
    return "member" if env and owner and env != owner else "owner"


def guard() -> None:
    """Raise MemberRefused when the calling job is a household member's."""
    if job_role() == "member":
        raise MemberRefused(os.environ.get("AIWB_JOB_ENV") or job_environment())


class MemberRefusedKeyring(KeyringBackend):
    """Keyring backend of a household member's job on the owner's computer: every call is refused (own_device_first).
    Bridge selects it with PYTHON_KEYRING_BACKEND=aiwb_secret.MemberRefusedKeyring (or member_site/sitecustomize.py).
    priority -1: never a candidate of keyring's own choice (a positive priority would put it into the chainer of every
    owner process that imports this module, and a missing owner secret would raise instead of returning None)."""
    priority = -1

    def _refuse(self, *_args, **_kwargs):
        raise MemberRefused(os.environ.get("AIWB_JOB_ENV") or None)

    get_password = set_password = delete_password = get_credential = _refuse


def get_secret(service: str, account: str = "default") -> str | None:
    guard()
    head = keyring.get_password(service, account)
    if head is None or not head.startswith(MARKER):
        return head
    parts = []
    for i in range(1, int(head[len(MARKER):]) + 1):
        part = keyring.get_password(service, f"{account}#{i}")
        if part is None:
            return None
        parts.append(part)
    return "".join(parts)


def _delete_parts(service: str, account: str) -> None:
    head = keyring.get_password(service, account)
    if head and head.startswith(MARKER):
        for i in range(1, int(head[len(MARKER):]) + 1):
            try:
                keyring.delete_password(service, f"{account}#{i}")
            except keyring.errors.PasswordDeleteError:
                pass


def set_secret(service: str, account: str, value: str) -> None:
    guard()
    _delete_parts(service, account)
    if len(value) <= CHUNK and not value.startswith(MARKER):
        keyring.set_password(service, account, value)
        return
    parts = [value[i:i + CHUNK] for i in range(0, len(value), CHUNK)]
    for i, part in enumerate(parts, 1):
        keyring.set_password(service, f"{account}#{i}", part)
    keyring.set_password(service, account, f"{MARKER}{len(parts)}")
