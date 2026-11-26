# SPDX-License-Identifier: AGPL-3.0-or-later
"""ONLYOFFICE Docs on the home PC, brought up on demand (canvas 1.1, plan 10g).

ONLYOFFICE runs as the Docker container `onlyoffice` in a WSL distro (127.0.0.1:8088). After `wsl --shutdown`, an update
or a crash of WSL nothing used to start it again until the next Windows logon, and the editor page said "The editor did
not load". `ensure()` checks the healthcheck and, when it fails, starts the distro, keeps it running with one detached
`wsl.exe -d <distro> --exec sleep infinity` (its pid in %LOCALAPPDATA%/AIWorkbenchCanvas/wsl-keepalive.pid) and starts
(or, when missing, creates) the container. canvas-bridge calls it when an editor page opens (GET /cb/ds); the module's
watch (home/canvas_watch.py, a Bridge schedule) calls it every few minutes.

Only one ensure runs at a time on the PC (a lock file next to the pid file); a second caller gets {starting: true}.
Settings: CANVAS_WSL_DISTRO (default Ubuntu), CANVAS_DS_CONTAINER (default onlyoffice). Secret for a new container:
keyring aiwb/onlyoffice / jwt (never written anywhere else; passed to WSL through WSLENV).
"""
from __future__ import annotations

import ctypes
import os
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

DISTRO = os.environ.get('CANVAS_WSL_DISTRO') or 'Ubuntu'
CONTAINER = os.environ.get('CANVAS_DS_CONTAINER') or 'onlyoffice'
HEALTH_URL = 'http://127.0.0.1:8088/healthcheck'
STATE_DIR = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'AIWorkbenchCanvas'
PID_FILE = STATE_DIR / 'wsl-keepalive.pid'
LOCK_FILE = STATE_DIR / 'ds-ensure.lock'
NO_WINDOW = 0x08000000
NEW_GROUP = 0x00000200
BREAKAWAY = 0x01000000
RUN_IMAGE = ('docker run -d --name {name} --restart unless-stopped -p 127.0.0.1:8088:80 -e JWT_ENABLED=true -e JWT_SECRET '
             '-e JWT_HEADER=Authorization -v /opt/onlyoffice/logs:/var/log/onlyoffice '
             '-v /opt/onlyoffice/data:/var/www/onlyoffice/Data -v /opt/onlyoffice/lib:/var/lib/onlyoffice '
             '-v /opt/onlyoffice/db:/var/lib/postgresql onlyoffice/documentserver:latest')


def healthy(timeout: float = 3) -> bool:
    try:
        with urllib.request.urlopen(HEALTH_URL, timeout=timeout) as r:
            return r.read(16).strip().lower() == b'true'
    except Exception:
        return False


def _run(args: list[str], timeout: float = 60, env: dict | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(args, capture_output=True, timeout=timeout, creationflags=NO_WINDOW, env=env)


def _text(b: bytes) -> str:
    # wsl.exe itself answers in UTF-16LE; commands run inside the distro answer in UTF-8
    if b[:2] == b'\xff\xfe' or (len(b) > 1 and b[1:2] == b'\x00'):
        return b.decode('utf-16-le', 'replace').replace('\x00', '')
    return b.decode('utf-8', 'replace')


def _proc_created(pid: int) -> int:
    """Creation time (FILETIME ticks) of a live wsl.exe process; 0 when pid is not one (gone, another program)."""
    if os.name != 'nt' or pid <= 0:
        return 0
    k = ctypes.windll.kernel32
    h = k.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return 0
    try:
        code = ctypes.c_ulong()
        if not k.GetExitCodeProcess(h, ctypes.byref(code)) or code.value != 259:  # STILL_ACTIVE
            return 0
        buf = ctypes.create_unicode_buffer(1024)
        size = ctypes.c_ulong(len(buf))
        if not k.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)) or not buf.value.lower().endswith(r'\wsl.exe'):
            return 0
        t = [ctypes.c_ulonglong() for _ in range(4)]
        if not k.GetProcessTimes(h, *[ctypes.byref(x) for x in t]):
            return 0
        return t[0].value
    finally:
        k.CloseHandle(h)


def keepalive_alive() -> bool:
    """Our keepalive runs: the pid of the file is a wsl.exe created at the recorded time (a reused pid does not count)."""
    try:
        pid, created = (int(x) for x in PID_FILE.read_text(encoding='ascii').split()[:2])
    except (OSError, ValueError):
        return False
    return created != 0 and _proc_created(pid) == created


# The keepalive is started by a short-lived middle process, so it has no live parent: `taskkill /T` of Bridge (a schedule
# run stopped) or of the task CanvasBridge does not reach it (review 10g-A P2-2); it leaves the job object when it may.
_SPAWN = (
    "import subprocess,sys\n"
    "a=['wsl.exe','-d',sys.argv[1],'--exec','sleep','infinity']\n"
    "kw=dict(stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,close_fds=True)\n"
    "try:\n p=subprocess.Popen(a,creationflags=0x08000000|0x00000200|0x01000000,**kw)\n"
    "except OSError:\n p=subprocess.Popen(a,creationflags=0x08000000|0x00000200,**kw)\n"
    "print(p.pid)\n"
)


def start_keepalive() -> int:
    """One detached `wsl.exe -d DISTRO --exec sleep infinity`: starts the distro and keeps it running (vmIdleTimeout
    keeps only the VM). Its pid and creation time go to PID_FILE."""
    if keepalive_alive():
        return int(PID_FILE.read_text(encoding='ascii').split()[0])
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    py = sys.executable
    if py.lower().endswith('pythonw.exe') and os.path.isfile(py[:-11] + 'python.exe'):
        py = py[:-11] + 'python.exe'  # canvas-bridge runs under pythonw; the middle process prints the pid
    args = [py, '-I', '-c', _SPAWN, DISTRO]
    kw = dict(capture_output=True, timeout=30, stdin=subprocess.DEVNULL)
    try:
        r = subprocess.run(args, creationflags=NO_WINDOW | NEW_GROUP | BREAKAWAY, **kw)
    except OSError:  # the job object of the caller does not allow breakaway
        r = subprocess.run(args, creationflags=NO_WINDOW | NEW_GROUP, **kw)
    out = r.stdout.decode('ascii', 'replace').strip()
    pid = int(out) if out.isdigit() else 0
    if pid <= 0:
        raise RuntimeError('keepalive did not start: ' + _text(r.stderr).strip()[:200])
    PID_FILE.write_text(f'{pid} {_proc_created(pid)}', encoding='ascii')
    return pid


def _container_state() -> str:
    """'running' | 'exited' | 'paused' | ... | 'missing' | 'nodocker' (the daemon is not answering yet)."""
    try:
        r = _run(['wsl.exe', '-d', DISTRO, '--', 'docker', 'inspect', '-f', '{{.State.Status}}', CONTAINER], timeout=60)
    except subprocess.TimeoutExpired:  # wsl.exe hangs while the distro boots: try again
        return 'nodocker'
    out = _text(r.stdout).strip()
    if r.returncode == 0 and out:
        return out
    err = _text(r.stderr).lower()
    if 'no such object' in err or 'no such container' in err:
        return 'missing'
    return 'nodocker'


def _create_container() -> str:
    import keyring
    secret = keyring.get_password('aiwb/onlyoffice', 'jwt') or ''
    if not secret:
        return 'no JWT secret in keyring aiwb/onlyoffice / jwt'
    env = dict(os.environ, JWT_SECRET=secret, WSLENV='JWT_SECRET')
    r = _run(['wsl.exe', '-d', DISTRO, '--', 'bash', '-c', RUN_IMAGE.format(name=CONTAINER)], timeout=600, env=env)
    return '' if r.returncode == 0 else 'docker run failed: ' + _text(r.stderr).strip()[:300]


class _Lock:
    """Non-blocking lock of the PC (msvcrt on a file): one ensure at a time across canvas-bridge and the watch."""

    def __init__(self):
        self.f = None

    def __enter__(self):
        import msvcrt
        STATE_DIR.mkdir(parents=True, exist_ok=True)
        self.f = open(LOCK_FILE, 'a+b')
        try:
            self.f.seek(0)
            msvcrt.locking(self.f.fileno(), msvcrt.LK_NBLCK, 1)
            return True
        except OSError:
            self.f.close()
            self.f = None
            return False

    def __exit__(self, *exc):
        if self.f:
            import msvcrt
            try:
                self.f.seek(0)
                msvcrt.locking(self.f.fileno(), msvcrt.LK_UNLCK, 1)
            finally:
                self.f.close()


def ensure(wait_s: float = 180, log=None) -> dict:
    """Brings ONLYOFFICE up. Answer: {ok, steps[], error?, starting?}. ok is True when the healthcheck answers; steps say
    what was done (empty when it was already up). A second concurrent call answers {ok: False, starting: True}."""
    say = log or (lambda m: None)
    if keepalive_alive() and healthy():
        return {'ok': True, 'steps': []}
    with _Lock() as got:
        if not got:
            return {'ok': False, 'starting': True, 'steps': [], 'error': 'another start is running'}
        steps: list[str] = []
        try:
            if not keepalive_alive():
                pid = start_keepalive()
                steps.append(f'wsl {DISTRO} kept running (pid {pid})')
                say(steps[-1])
                if healthy():  # it was up, only nobody of ours kept the distro running
                    return {'ok': True, 'steps': steps}
            deadline = time.time() + wait_s
            state = ''
            while time.time() < deadline:
                state = _container_state()
                if state != 'nodocker':
                    break
                time.sleep(3)  # systemd still starting docker
            if state == 'missing':
                err = _create_container()
                if err:
                    return {'ok': False, 'steps': steps, 'error': err}
                steps.append(f'container {CONTAINER} created')
                say(steps[-1])
            elif state not in ('running', 'restarting'):
                verb = 'unpause' if state == 'paused' else 'start'
                r = _run(['wsl.exe', '-d', DISTRO, '--', 'docker', verb, CONTAINER], timeout=120)
                if r.returncode != 0:
                    return {'ok': False, 'steps': steps, 'error': f'docker {verb} ({state or "no answer"}): '
                                                                  + _text(r.stderr).strip()[:300]}
                steps.append(f'container {CONTAINER} started (was {state})')
                say(steps[-1])
            while time.time() < deadline:
                if healthy():
                    return {'ok': True, 'steps': steps}
                time.sleep(3)
            return {'ok': False, 'steps': steps, 'error': f'healthcheck not answering after {int(wait_s)} s (container {state})'}
        except Exception as e:  # wsl.exe missing, timeouts
            return {'ok': False, 'steps': steps, 'error': f'{type(e).__name__}: {e}'}

