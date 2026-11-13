# SPDX-License-Identifier: AGPL-3.0-or-later
"""Workbench Nostr key pair in the OS keyring (SPEC §1.2, §6.3): service aiwb/nostr, account = workbench id.

The secret key never goes to files, logs or stdout; `MemoryKeyStore` exists for tests and simulations.
"""
from __future__ import annotations

from dataclasses import dataclass

from . import schnorr

SERVICE = "aiwb/nostr"


class MemoryKeyStore:
    def __init__(self):
        self._d: dict[str, str] = {}

    def get(self, account: str) -> str | None:
        return self._d.get(account)

    def set(self, account: str, value: str) -> None:
        self._d[account] = value

    def delete(self, account: str) -> None:
        self._d.pop(account, None)


class KeyringStore:
    def __init__(self, service: str = SERVICE):
        import keyring  # noqa: F401  (fail early with a clear ImportError)
        self.service = service

    def get(self, account: str) -> str | None:
        import keyring
        return keyring.get_password(self.service, account)

    def set(self, account: str, value: str) -> None:
        import keyring
        keyring.set_password(self.service, account, value)

    def delete(self, account: str) -> None:
        import keyring
        from keyring.errors import PasswordDeleteError
        try:
            keyring.delete_password(self.service, account)
        except PasswordDeleteError:
            pass


@dataclass(frozen=True)
class Keys:
    seckey: str          # hex; keep out of repr
    pubkey: str

    def __repr__(self):
        return f"Keys(pubkey={self.pubkey[:12]}…)"

    @property
    def npub(self) -> str:
        return schnorr.npub(self.pubkey)


def from_seckey(seckey_hex: str) -> Keys:
    return Keys(seckey_hex, schnorr.pubkey_gen(bytes.fromhex(seckey_hex)).hex())


def generate() -> Keys:
    return from_seckey(schnorr.new_seckey().hex())


def load(store, account: str) -> Keys | None:
    sk = store.get(account)
    return from_seckey(schnorr.decode_key(sk, "nsec")) if sk else None


def load_or_create(store, account: str) -> tuple[Keys, bool]:
    k = load(store, account)
    if k:
        return k, False
    k = generate()
    store.set(account, k.seckey)
    return k, True
