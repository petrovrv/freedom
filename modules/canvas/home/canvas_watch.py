# SPDX-License-Identifier: AGPL-3.0-or-later
"""Watch of the canvas on the home PC (module schedule `watch`, Bridge runs it every 3 minutes; plan 10g).

Checks and repairs, in this order:
  1. ONLYOFFICE Docs: healthcheck of 127.0.0.1:8088; down -> WSL distro kept running + container started (ds_up.py,
     the same code canvas-bridge uses when an editor page opens);
  2. canvas-bridge: GET 127.0.0.1:8089/health; down -> the Windows task \\AIWorkbench\\CanvasBridge is run;
  3. Tailscale Funnel: / -> 127.0.0.1:8088 and /cb -> 127.0.0.1:8089 published; missing -> applied again;
  4. the public address (install.json canvas.publicUrl)/cb/health answers.
A problem that stays after the repair in two of the last four runs, or two repairs within an hour, sends the owner one push
(push.php op app_notify, module canvas, permission notify.push; the server adds its own anti-repeat and limits); when
it works again after such a push, one more push says so. State: %LOCALAPPDATA%/AIWorkbenchCanvas/watch.json.

  python canvas_watch.py            one run (what Bridge does); prints only what it did or found
  python canvas_watch.py --check    report only, no repairs, no push
Exit code: 0 all fine, 1 a problem stays.
"""
from __future__ import annotations

import sys

sys.dont_write_bytecode = True  # this folder is part of the signed module

import json  # noqa: E402
import os  # noqa: E402
import subprocess  # noqa: E402
import time  # noqa: E402
import urllib.request  # noqa: E402
from pathlib import Path  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'canvas-bridge'))
import ds_up  # noqa: E402

BRIDGE_HEALTH = 'http://127.0.0.1:8089/health'
BRIDGE_TASK = '\\AIWorkbench\\CanvasBridge'
TAILSCALE = os.environ.get('CANVAS_TAILSCALE') or r'C:\Program Files\Tailscale\tailscale.exe'
STATE = ds_up.STATE_DIR / 'watch.json'
REPEAT_WINDOW_S = 3600
REMIND_S = 6 * 3600
NO_WINDOW = 0x08000000


def install_json() -> dict:
    if os.environ.get('AIWB_INSTALL'):
        p = Path(os.environ['AIWB_INSTALL'])
    else:
        p = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'AIWorkbench' / 'install.json'
    try:
        return json.loads(p.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        return {}


def get(url: str, timeout: float = 5) -> bytes | None:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return r.read(4096)
    except Exception:
        return None


def bridge_ok() -> bool:
    b = get(BRIDGE_HEALTH, 3)
    try:
        return bool(b) and json.loads(b).get('ok') is True
    except ValueError:
        return False


def funnel_missing() -> list[str]:
    """Handlers of the Funnel config that are not there: subset of ['/', '/cb']; ['tailscale'] when it cannot be read."""
    if not Path(TAILSCALE).is_file():
        return ['tailscale']
    try:
        r = subprocess.run([TAILSCALE, 'funnel', 'status', '--json'], capture_output=True, timeout=20, creationflags=NO_WINDOW)
        cfg = json.loads(r.stdout or b'{}')
    except Exception:
        return ['tailscale']
    want = {'/': '127.0.0.1:8088', '/cb': '127.0.0.1:8089'}
    have: dict = {}
    for host, web in (cfg.get('Web') or {}).items():
        if (cfg.get('AllowFunnel') or {}).get(host):
            for path, h in (web.get('Handlers') or {}).items():
                have[path] = str((h or {}).get('Proxy', ''))
    return [p for p, port in want.items() if port not in have.get(p, '')]


def funnel_apply(paths: list[str]) -> str:
    out = []
    for p in paths:
        args = [TAILSCALE, 'funnel', '--bg', '--yes'] + (['--set-path', '/cb', 'http://127.0.0.1:8089'] if p == '/cb'
                                                         else ['http://127.0.0.1:8088'])
        try:
            r = subprocess.run(args, capture_output=True, timeout=60, creationflags=NO_WINDOW)
            out.append(f'{p} exit {r.returncode}')
        except Exception as e:
            out.append(f'{p} {type(e).__name__}')
    return ', '.join(out)


def run_checks(repair: bool) -> tuple[list[str], list[str], bool]:
    """(problems left, repairs done, something was down)."""
    problems, done = [], []
    down = False
    # 1. ONLYOFFICE
    if repair:
        down = not ds_up.healthy()  # a keepalive started for a working editor is no failure
        r = ds_up.ensure(wait_s=100)
        done += r.get('steps', [])
        if not r.get('ok') and not r.get('starting'):
            problems.append('ONLYOFFICE: ' + str(r.get('error')))
    elif not ds_up.healthy():
        problems.append('ONLYOFFICE: healthcheck not answering')
    # 2. canvas-bridge
    if not bridge_ok():
        down = True
        if repair:
            try:
                subprocess.run(['schtasks', '/Run', '/TN', BRIDGE_TASK], capture_output=True, timeout=30, creationflags=NO_WINDOW)
                done.append('canvas-bridge task run')
            except Exception as e:
                done.append(f'canvas-bridge task: {type(e).__name__}')
            for _ in range(10):
                time.sleep(2)
                if bridge_ok():
                    break
        if not bridge_ok():
            problems.append('canvas-bridge (127.0.0.1:8089) not answering')
    # 3. Funnel
    miss = funnel_missing()
    down = down or bool(miss)
    if miss and miss != ['tailscale'] and repair:
        done.append('funnel ' + funnel_apply(miss))
        miss = funnel_missing()
    if miss:
        problems.append('Tailscale Funnel: ' + ('tailscale not answering' if miss == ['tailscale'] else 'missing ' + ', '.join(miss)))
    # 4. public address (only when the parts are fine: it would repeat their problem)
    public = str((install_json().get('canvas') or {}).get('publicUrl') or '').rstrip('/')
    if public and not problems and get(public + '/cb/health', 15) is None:
        problems.append(f'public address {public}/cb not answering')
    return problems, done, down or bool(problems)


def load_state() -> dict:
    try:
        return json.loads(STATE.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {}


def save_state(s: dict) -> None:
    STATE.parent.mkdir(parents=True, exist_ok=True)
    tmp = STATE.with_suffix('.tmp')
    tmp.write_text(json.dumps(s, ensure_ascii=False, indent=1), encoding='utf-8')
    os.replace(tmp, STATE)


def push(title: str, body: str) -> str:
    """app_notify of the module to the owner; returns a short result for the log (never raises)."""
    inst = install_json()
    env = next((m.get('env') for m in inst.get('members', []) if m.get('role') == 'owner'), '')
    site = str((inst.get('site') or {}).get('url') or '').rstrip('/')
    if not env or not site:
        return 'no owner/site in install.json'
    try:
        import keyring
        key = keyring.get_password('aiwb/co-push', 'default') or ''
    except Exception as e:
        return f'keyring: {type(e).__name__}'
    if not key:
        return 'keyring aiwb/co-push is empty'
    data = json.dumps({'op': 'app_notify', 'module': 'canvas', 'env': env, 'title': title, 'body': body[:300],
                       'tag': 'canvas-watch'}).encode()
    req = urllib.request.Request(site + '/agent/push.php', data=data, method='POST', headers={
        'Content-Type': 'application/json', 'X-CO-Push-Key': key, 'User-Agent': 'canvas-watch'})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return 'push ' + r.read(300).decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return f'push http {e.code}: ' + e.read(200).decode('utf-8', 'replace')
    except Exception as e:
        return f'push {type(e).__name__}: {e}'


def main(argv: list[str]) -> int:
    check_only = '--check' in argv
    problems, done, down = run_checks(repair=not check_only)
    if check_only:
        print(json.dumps({'ok': not problems, 'problems': problems}, ensure_ascii=False))
        return 1 if problems else 0
    now = time.time()
    s = load_state()
    s['repairs'] = [t for t in s.get('repairs', []) if now - t < REPEAT_WINDOW_S] + ([now] if done and down else [])
    if done:
        print('repaired: ' + '; '.join(done))
    if problems:
        print('problem: ' + '; '.join(problems))
    # the last 4 runs: a problem in 2 of them counts, so one that blinks every other run is not missed (review P3-7)
    s['hist'] = (s.get('hist', []) + [1 if problems else 0])[-4:]
    kind = ''
    if problems and sum(s['hist']) >= 2 and (not s.get('incident') or now - s.get('notifiedUtc', 0) > REMIND_S):
        kind = 'down'
    elif not problems and s.get('incident'):
        kind = 'up'
    elif not problems and len(s.get('repairs', [])) >= 2 and now - s.get('notifiedUtc', 0) > REMIND_S:
        kind = 'flaky'
    if kind:
        res = push(*{
            'down': ('Canvas: the editor does not work', 'Could not repair it: ' + '; '.join(problems)),
            'up': ('Canvas works again', '; '.join(done) or 'the editor answers again'),
            'flaky': ('Canvas keeps failing', f"Repaired {len(s.get('repairs', []))} times within an hour; last: "
                      + '; '.join(done)),
        }[kind])
        print(res)
        # notifications of canvas off for the person, or the module off: no use asking every 3 min
        if any(f'"error":"{c}"' in res.replace(' ', '') for c in ('notify_off', 'person_off', 'module_disabled', 'permission_not_declared')):
            s['notifiedUtc'] = now
            s['incident'] = kind == 'down'
        elif '"ok":true' in res.replace(' ', ''):  # sent; otherwise the next run tries again
            if kind == 'up':
                s['incident'] = False
                s['hist'] = []  # a blinking problem starts its count anew (review 10g round 2)
            else:
                s['notifiedUtc'] = now
                s['incident'] = s.get('incident', False) or kind == 'down'
    s['lastUtc'] = now
    s['last'] = {'problems': problems, 'repairs': done}
    save_state(s)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
