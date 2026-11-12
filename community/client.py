# SPDX-License-Identifier: AGPL-3.0-or-later
"""Nostr client: publish one event to several relays at once, read with filters; NIP-42 AUTH when a relay asks.

Synchronous wrappers (`publish`, `query`) run their own event loop; every inbound event is signature-checked here,
so callers only see verified events.
"""
from __future__ import annotations

import asyncio
import json
import secrets

from . import nostr


async def _recv(ws, timeout):
    raw = await asyncio.wait_for(ws.recv(), timeout)
    try:
        return json.loads(raw)
    except ValueError:
        return ["NOTICE", "bad json from relay"]


async def _auth(ws, url, keys, challenge, timeout):
    ev = nostr.auth_event(keys.seckey, url, challenge)
    await ws.send(json.dumps(["AUTH", ev]))
    loop = asyncio.get_running_loop()
    end = loop.time() + timeout
    while loop.time() < end:
        m = await _recv(ws, end - loop.time())
        if m and m[0] == "OK" and len(m) >= 3 and m[1] == ev["id"]:
            return bool(m[2]), (m[3] if len(m) > 3 else "")
    return False, "auth timeout"


async def _connect(url, keys, timeout):
    from websockets.asyncio.client import connect
    ws = await asyncio.wait_for(connect(url, max_size=512 * 1024, open_timeout=timeout, close_timeout=2), timeout)
    authed = False
    # relays that want AUTH usually send the challenge right away
    try:
        m = await _recv(ws, 0.5)
        if m and m[0] == "AUTH" and keys is not None:
            authed, _ = await _auth(ws, url, keys, m[1], timeout)
    except asyncio.TimeoutError:
        pass
    return ws, authed


async def publish_one(url: str, ev: dict, keys=None, timeout: float = 10) -> tuple[bool, str]:
    try:
        ws, authed = await _connect(url, keys, timeout)
    except Exception as e:  # noqa: BLE001 - one dead relay must not break the others
        return False, f"connect: {e}"
    try:
        loop = asyncio.get_running_loop()
        retried = False
        await ws.send(json.dumps(["EVENT", ev], ensure_ascii=False))
        end = loop.time() + timeout
        challenge = None
        while loop.time() < end:
            m = await _recv(ws, end - loop.time())
            if not m:
                continue
            if m[0] == "AUTH":
                challenge = m[1]
            elif m[0] == "OK" and m[1] == ev["id"]:
                ok, msg = bool(m[2]), (m[3] if len(m) > 3 else "")
                if not ok and msg.startswith("auth-required") and not retried and keys is not None and challenge \
                        and not authed:
                    retried = True
                    authed, _ = await _auth(ws, url, keys, challenge, timeout)
                    await ws.send(json.dumps(["EVENT", ev], ensure_ascii=False))
                    continue
                return ok, msg
        return False, "timeout"
    except Exception as e:  # noqa: BLE001
        return False, f"error: {e}"
    finally:
        await ws.close()


async def query_one(url: str, filters: list[dict], keys=None, timeout: float = 10) -> list[dict]:
    return (await query_one_status(url, filters, keys, timeout))[0]


async def query_one_status(url: str, filters: list[dict], keys=None, timeout: float = 10) -> tuple[list[dict], bool]:
    """(events, complete). complete only when the relay sent EOSE for our subscription; a timeout, a disconnect, an
    error or CLOSED means the list may be partial (Codex review 1)."""
    try:
        ws, _ = await _connect(url, keys, timeout)
    except Exception:  # noqa: BLE001
        return [], False
    sid = secrets.token_hex(6)
    out, eose = [], False
    try:
        await ws.send(json.dumps(["REQ", sid] + filters))
        loop = asyncio.get_running_loop()
        end = loop.time() + timeout
        while loop.time() < end:
            m = await _recv(ws, end - loop.time())
            if m and m[0] == "EVENT" and len(m) >= 3 and m[1] == sid:
                if nostr.verify_event(m[2]) and nostr.match_any(m[2], filters):
                    out.append(m[2])
            elif m and m[0] == "EOSE" and m[1] == sid:
                eose = True
                break
            elif m and m[0] == "CLOSED" and m[1] == sid:
                break
        await ws.send(json.dumps(["CLOSE", sid]))
    except Exception:  # noqa: BLE001
        pass
    finally:
        try:
            await ws.close()
        except Exception:  # noqa: BLE001
            pass
    return out, eose


def publish(urls: list[str], ev: dict, keys=None, timeout: float = 10) -> dict:
    """{url: [ok, message]} - sent to all relays concurrently."""
    async def run():
        res = await asyncio.gather(*(publish_one(u, ev, keys, timeout) for u in urls))
        return {u: list(r) for u, r in zip(urls, res)}
    return asyncio.run(run()) if urls else {}


def query(urls: list[str], filters: list[dict], keys=None, timeout: float = 10, per_relay: bool = False):
    """Verified events from all relays, de-duplicated by id (or {url: [events]} with per_relay)."""
    async def run():
        return await asyncio.gather(*(query_one(u, filters, keys, timeout) for u in urls))
    res = asyncio.run(run()) if urls else []
    if per_relay:
        return {u: r for u, r in zip(urls, res)}
    seen, out = set(), []
    for r in res:
        for ev in r:
            if ev["id"] not in seen:
                seen.add(ev["id"])
                out.append(ev)
    return sorted(out, key=lambda e: (e["created_at"], e["id"]))


class NetTransport:
    """What Workbench uses to talk to relays; tests may pass another object with the same two methods."""

    def __init__(self, timeout: float = 10):
        self.timeout = timeout

    def publish(self, urls, ev, keys):
        return publish(urls, ev, keys, self.timeout)

    def query(self, urls, filters, keys):
        return query(urls, filters, keys, self.timeout, per_relay=True)

    def query_status(self, urls, filters, keys):
        """{url: (events, complete)} - complete only after EOSE."""
        async def run():
            return await asyncio.gather(*(query_one_status(u, filters, keys, self.timeout) for u in urls))
        res = asyncio.run(run()) if urls else []
        return {u: r for u, r in zip(urls, res)}
