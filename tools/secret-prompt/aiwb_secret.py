"""Keyring access for long secrets (Windows Credential Manager holds at most 2560 bytes = 1280 UTF-16 chars).

Values longer than CHUNK chars are stored as parts in accounts "<account>#1".."<account>#N";
the main account then holds the marker "aiwb-chunked:N". Short values are stored as is.

    sys.path.insert(0, r"G:\\My Drive\\AI-Workbench\\tools\\secret-prompt")
    from aiwb_secret import get_secret
    token = get_secret("aiwb/chatgpt", "session-token")
"""
from __future__ import annotations

import keyring

CHUNK = 1000
MARKER = "aiwb-chunked:"


def get_secret(service: str, account: str = "default") -> str | None:
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
    _delete_parts(service, account)
    if len(value) <= CHUNK and not value.startswith(MARKER):
        keyring.set_password(service, account, value)
        return
    parts = [value[i:i + CHUNK] for i in range(0, len(value), CHUNK)]
    for i, part in enumerate(parts, 1):
        keyring.set_password(service, f"{account}#{i}", part)
    keyring.set_password(service, account, f"{MARKER}{len(parts)}")
