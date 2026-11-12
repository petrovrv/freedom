"""Read cookies of the CO browser (Edge of CO Desktop) over CDP - for tools that keep a site session in keyring.

    from co_cookies import site_cookies, CoBrowserNotRunning
    jar = site_cookies('chatgpt.com')      # {name: {'value', 'expires', 'domain', ...}} incl. httpOnly cookies

Reads the browser-level cookie store (Storage.getCookies) of the running Edge; never starts it, opens no tab,
attaches to no target. Values must go straight to keyring - never print or log them.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
from urllib.request import urlopen

PROFILE = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'CO' / 'browser'


class CoBrowserNotRunning(Exception):
    pass


def _browser_ws(profile: Path = PROFILE) -> str:
    try:
        port = int((profile / 'DevToolsActivePort').read_text(encoding='utf-8').split()[0])
        with urlopen(f'http://127.0.0.1:{port}/json/version', timeout=5) as r:
            return json.load(r)['webSocketDebuggerUrl']
    except (OSError, ValueError, KeyError, IndexError) as e:
        raise CoBrowserNotRunning(f'CO browser is not running ({profile}): {e}') from None


def all_cookies(profile: Path = PROFILE) -> list[dict]:
    import websocket  # websocket-client
    ws = websocket.create_connection(_browser_ws(profile), timeout=15, suppress_origin=True)
    try:
        ws.send(json.dumps({'id': 1, 'method': 'Storage.getCookies'}))
        while True:
            m = json.loads(ws.recv())
            if m.get('id') == 1:
                if 'error' in m:
                    raise RuntimeError(f"Storage.getCookies: {m['error'].get('message')}")
                return m['result']['cookies']
    finally:
        ws.close()


def site_cookies(domain: str, profile: Path = PROFILE) -> dict[str, dict]:
    """Cookies of domain and its subdomains, by name (the one with the latest expiry wins)."""
    out: dict[str, dict] = {}
    for c in all_cookies(profile):
        d = c.get('domain', '').lstrip('.')
        if d == domain or d.endswith('.' + domain):
            old = out.get(c['name'])
            if old is None or c.get('expires', 0) > old.get('expires', 0):
                out[c['name']] = c
    return out
