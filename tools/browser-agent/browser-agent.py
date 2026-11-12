#!/usr/bin/env python3
"""browser-agent.py - CLI client for the browser-agent Node daemon (daemon.mjs)."""
import argparse
import re
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HOST = "127.0.0.1"
HTTP_TIMEOUT = 60
START_TIMEOUT = 150  # Edge start 15 s + two CDP attach attempts of up to 60 s
SCRIPT_DIR = Path(__file__).resolve().parent
BASE_DIR = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "AIWorkbenchWork" / "browser-agent"
SESSIONS_DIR = BASE_DIR / "sessions"


class BAError(Exception):
    """Fatal client-side error: reported as JSON and exit code 2."""

    def __init__(self, code, error, extra=None):
        super().__init__(error)
        self.code, self.error, self.extra = code, error, extra or {}

    def payload(self):
        return {"ok": False, "code": self.code, "error": self.error, **self.extra}


def read_guard(obj, source):
    """0.10.0: read-guard wraps page text with signs of prompt injection as data (tools/read-guard; no-op if missing)."""
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "read-guard"))
        import read_guard as rg
        if isinstance(obj, (dict, list)):
            return rg.guard_obj(obj, source, "browser-agent")
        return rg.guard_text(obj, source, "browser-agent")
    except Exception:
        return obj


def read_guard_hold(summary, kind="other", always=False):
    """None when the action may run; else read-guard's owner_confirm_required answer. 0.10.3 (read-guard 0.2.0):
    0.12.0: in every chat only purchases wait (always, kind purchase); --confirm and type-secret only in a chat that
    read injected content."""
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "read-guard"))
        import read_guard as rg
        if getattr(rg, "VERSION", "0") < "0.2.0":
            return rg.guard_check_action(summary, tool="browser-agent")
        return rg.guard_check_action(summary, tool="browser-agent", kind=kind, always=always, command=summary)
    except Exception:
        return None


def emit(obj):
    if isinstance(obj, dict) and obj.get("ok") is not False:
        obj = read_guard(obj, f"page {obj.get('url') or ''}".strip())
    if isinstance(obj, dict) and LAST_RESORT_NOTE:
        obj = {**obj, "lastResort": LAST_RESORT_NOTE}
    print(json.dumps(obj, ensure_ascii=False, indent=None))


def state_path(session):
    return SESSIONS_DIR / f"{session}.json"


def log_path(session):
    return SESSIONS_DIR / f"{session}.log"


def read_state(session):
    """Return the parsed state file, or None if missing or invalid."""
    try:
        data = json.loads(state_path(session).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) and "port" in data and "token" in data else None


def tail_log(session, n=20):
    try:
        lines = log_path(session).read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return "(no log available)"
    return "\n".join(lines[-n:]) or "(log is empty)"


# 0.15.0: browser settings of the chat (Bridge sets them for the agent process); the daemon serves many jobs and
# chats, so they travel with every request. The daemon clamps the values; missing ones mean built-in defaults.
BROWSER_ENV = {"tabs": "AIWB_BROWSER_TABS", "idleMin": "AIWB_BROWSER_IDLE_MIN",
               "formIdleMin": "AIWB_BROWSER_FORM_IDLE_MIN", "captchaKeep": "AIWB_BROWSER_CAPTCHA_KEEP"}


def browser_settings_env():
    return {k: os.environ[v].strip() for k, v in BROWSER_ENV.items() if os.environ.get(v, "").strip()}


def call(state, cmd, args=None):
    """Send one command to the daemon and return its JSON reply."""
    url = f"http://{HOST}:{state['port']}/cmd"
    data = json.dumps({"cmd": cmd, "args": args or {}, "owner": OWNER,
                       "chat": os.environ.get("AIWB_CHAT_ID", "").strip() or None,
                       "browser": browser_settings_env()}).encode("utf-8")
    req = urllib.request.Request(url, data=data, method="POST", headers={
        "Content-Type": "application/json", "X-Token": str(state["token"])})
    try:
        with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT) as resp:
            body = resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        try:
            reply = json.loads(e.read().decode("utf-8", errors="replace"))
        except (OSError, ValueError):
            reply = {}
        if not isinstance(reply, dict):
            reply = {}
        code = "forbidden" if e.code == 403 else "http_error"
        raise BAError(code, reply.get("error") or f"HTTP {e.code}", {"status": e.code, "reply": reply})
    except urllib.error.URLError as e:
        refused = isinstance(e.reason, ConnectionRefusedError)
        raise BAError("connection_refused" if refused else "connection_error",
                      f"cannot reach daemon: {e.reason}")
    except OSError as e:  # timeouts, resets
        raise BAError("connection_error", f"daemon communication failed: {e}")
    try:
        reply = json.loads(body)
    except ValueError:
        raise BAError("bad_reply", "daemon returned invalid JSON")
    if not isinstance(reply, dict):
        raise BAError("bad_reply", "daemon reply is not a JSON object")
    return reply


AGENT_EXES = {"claude.exe", "codex.exe"}
OWNER = "cli"


def detect_owner():
    """Owner of the tabs = pid of the nearest claude/codex ancestor (one agent job), else 'cli' (0.7.0)."""
    env = os.environ.get("AIWB_BA_OWNER", "").strip()
    if env:
        return re.sub(r"[^A-Za-z0-9_.:-]", "-", env)[:64]
    if os.name != "nt":
        return "cli"
    try:
        import ctypes
        from ctypes import wintypes as w

        class PE(ctypes.Structure):
            _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("th32ProcessID", w.DWORD),
                        ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", w.DWORD), ("cntThreads", w.DWORD),
                        ("th32ParentProcessID", w.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", w.DWORD),
                        ("szExeFile", ctypes.c_wchar * 260)]
        k = ctypes.WinDLL("kernel32", use_last_error=True)
        k.CreateToolhelp32Snapshot.restype = w.HANDLE
        snap = k.CreateToolhelp32Snapshot(0x2, 0)
        procs = {}
        try:
            e = PE()
            e.dwSize = ctypes.sizeof(PE)
            ok = k.Process32FirstW(snap, ctypes.byref(e))
            while ok:
                procs[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile.lower())
                ok = k.Process32NextW(snap, ctypes.byref(e))
        finally:
            k.CloseHandle(snap)
        pid, seen = os.getpid(), set()
        while pid in procs and pid not in seen:
            seen.add(pid)
            ppid, exe = procs[pid]
            if exe in AGENT_EXES:
                return str(pid)
            pid = ppid
    except Exception:
        pass
    return "cli"


def live_state(session):
    """Return the state if the session answers 'status' successfully, else None."""
    state = read_state(session)
    if state is None:
        return None
    try:
        r = call(state, "ping")  # 0.7.0: does not create or take over tabs
        return state if r.get("ok") or r.get("code") == "unknown_command" else None  # unknown: daemon <= 0.6
    except BAError:
        return None


def parse_allow(text):
    items = [p.strip() for p in (text or "*").split(",") if p.strip()]
    return items or ["*"]


def _kernel32():
    import ctypes
    from ctypes import wintypes as w
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.GetCurrentProcess.restype = w.HANDLE
    k.OpenProcess.restype = w.HANDLE
    k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
    k.IsProcessInJob.argtypes = [w.HANDLE, w.HANDLE, ctypes.POINTER(w.BOOL)]
    k.GetExitCodeProcess.argtypes = [w.HANDLE, ctypes.POINTER(w.DWORD)]
    k.CloseHandle.argtypes = [w.HANDLE]
    return k, ctypes, w


def in_windows_job():
    try:
        k, ctypes, w = _kernel32()
        r = w.BOOL()
        return bool(k.IsProcessInJob(k.GetCurrentProcess(), None, ctypes.byref(r)) and r.value)
    except Exception:
        return False


def pid_alive(pid):
    k, ctypes, w = _kernel32()
    h = k.OpenProcess(0x1000, False, int(pid))  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return False
    try:
        code = w.DWORD()
        return bool(k.GetExitCodeProcess(h, ctypes.byref(code))) and code.value == 259  # STILL_ACTIVE
    finally:
        k.CloseHandle(h)


def profile_locked(session):
    """Legacy (<= 0.6.x own Chromium profiles): True if a running Chromium holds the old profile."""
    lock = BASE_DIR / "profiles" / session / "lockfile"
    if not lock.exists():
        return False
    try:
        open(lock, "a").close()
        return False
    except PermissionError:
        return True
    except OSError:
        return False


def launch_outside_job(node, daemon, session, config):
    """Start the daemon through WMI Win32_Process.Create (not a member of our job). Returns pid or None."""
    cfg_file = SESSIONS_DIR / f"{session}.start.json"
    if os.environ.get("PW_MODULE"):
        config = {**config, "pwModule": os.environ["PW_MODULE"]}
    if os.environ.get("BA_SITES_FILE"):
        config = {**config, "sitesFile": os.environ["BA_SITES_FILE"]}  # tests only
    cfg_file.write_text(json.dumps(config), encoding="utf-8")
    cmdline = subprocess.list2cmdline([node, str(daemon), f"BA_CONFIG_FILE={cfg_file}"])
    # 0.12.1: without ProcessStartupInformation WMI gives node a visible console (a Windows Terminal window per
    # daemon start); DETACHED_PROCESS (8) = no console at all, ShowWindow 0 = hidden if one is made anyway.
    ps = ("$si = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly "
          "-Property @{ShowWindow=[uint16]0; CreateFlags=[uint32]8}; "
          "$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments "
          "@{CommandLine=$env:BA_CMDLINE; CurrentDirectory=$env:BA_CWD; ProcessStartupInformation=$si}; "
          "if ($r.ReturnValue -eq 0) { $r.ProcessId } else { 'ERR' + $r.ReturnValue }")
    env = dict(os.environ, BA_CMDLINE=cmdline, BA_CWD=str(SCRIPT_DIR))
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps], env=env,
                             capture_output=True, text=True, timeout=30,
                             creationflags=subprocess.CREATE_NO_WINDOW).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        out = ""
    if out.isdigit():
        return int(out)
    try:
        cfg_file.unlink(missing_ok=True)
    except OSError:
        pass
    return None  # fall back to a normal child process


# 0.10.2: one CO browser window. A profile other than default is a separate Edge process (another window, without
# the CO Desktop taskbar icon); agents invented profiles (captcha, portals1) and the owner saw extra browsers.
# Only the owner allows a profile: %LOCALAPPDATA%\CO\browser-profiles.json {"allowed": ["anna"]}.
PROFILES_FILE = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local") / "CO" / "browser-profiles.json"


def check_profile(profile):
    name = (profile or "default").lower()
    if name == "default":
        return
    try:
        allowed = {str(x).lower() for x in json.loads(PROFILES_FILE.read_text(encoding="utf-8-sig")).get("allowed", [])}
    except (OSError, ValueError, AttributeError):
        allowed = set()
    if name not in allowed:
        raise BAError("profile_not_allowed",
                      f"profile '{profile}' is not allowed: it would open a second Edge window without the CO look. "
                      "Use the default profile (each job has its own tabs there; a paused tab does not block others). "
                      f"A separate profile (another account) only on the owner's request: owner adds it to {PROFILES_FILE}")


# 0.14.0 (owner 08.11): the CO browser is the owner's Edge on the home PC. Jobs of other environments (family
# members) work in the browser of their own device (CO Desktop / CO Resident, tools mcp__dev_<device>__browser_*);
# here only as a last resort with a reason: --last-resort "<why>" or env AIWB_BA_LAST_RESORT (wrappers inherit it).


def _owner_env():
    """The owner's environment (0.15.4, Freedom): AIWB_OWNER_ENV, else the member with role "owner" in install.json
    (AIWB_INSTALL, else %LOCALAPPDATA%\\AIWorkbench\\install.json, else ~/.config/aiwb/install.json); None if unknown
    (then every environment counts as the owner's)."""
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


OWNER_ENV = _owner_env()
PROJECTS_DIR = Path(os.environ.get("AIWB_PROJECTS_DIR", r"G:\My Drive\AI-Workbench\projects"))
LAST_RESORT_LOG = BASE_DIR / "last-resort.jsonl"
LAST_RESORT_NOTE = None


def job_environment():
    """Environment of the calling job: project.json of the working directory, else of the chat AIWB_CHAT_ID; None if unknown."""
    cwd = Path(os.getcwd()).resolve()
    for d in [cwd, *cwd.parents]:
        if d.parent.name == "projects" and (d / "project.json").is_file():
            proj = d / "project.json"
            break
    else:
        chat = os.environ.get("AIWB_CHAT_ID", "")
        found = list(PROJECTS_DIR.glob(f"*/chats/{chat}")) if CHAT_RE.fullmatch(chat) else []
        if not found:
            return None
        proj = found[0].parent.parent / "project.json"
    try:
        return json.loads(proj.read_text(encoding="utf-8-sig")).get("environmentId") or None
    except (OSError, ValueError, AttributeError):
        return None


def check_environment(a):
    env = job_environment()
    if not env or not OWNER_ENV or env == OWNER_ENV:
        return None
    reason = (getattr(a, "last_resort", None) or os.environ.get("AIWB_BA_LAST_RESORT") or "").strip()
    if not reason:
        raise BAError("own_device_first",
                      f"this job belongs to environment '{env}': the CO browser is the owner's Edge on the home PC. "
                      f"Use the browser of the {env} device (tools mcp__dev_<device>__browser_*; attach the device in the "
                      "composer or the schedule's devices). Only if that is impossible (device offline, tool fails), "
                      'retry with --last-resort "<why>" (or env AIWB_BA_LAST_RESORT) and say so in the answer.',
                      {"environment": env})
    try:
        BASE_DIR.mkdir(parents=True, exist_ok=True)
        with LAST_RESORT_LOG.open("a", encoding="utf-8") as f:
            f.write(json.dumps({"utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "env": env,
                                "job": os.environ.get("AIWB_JOB_ID"), "chat": os.environ.get("AIWB_CHAT_ID"),
                                "command": a.command, "session": a.session, "reason": reason[:500]},
                               ensure_ascii=False) + "\n")
    except OSError:
        pass
    return f"last resort: environment '{env}' uses the owner's CO browser ({reason[:200]}); tell the user"


def start_daemon(session, headed, allow, idle_minutes, upload_dirs=None, shots=True, confirm_clicks=False,
                 profile="default", keep_tabs=False):
    """Spawn daemon.mjs detached and wait until it is reachable."""
    check_profile(profile)
    node = os.environ.get("NODE") or shutil.which("node")
    if not node:
        raise BAError("node_missing", "Node.js not found (set NODE or add node to PATH)")
    daemon = SCRIPT_DIR / "daemon.mjs"
    if not daemon.is_file():
        raise BAError("daemon_missing", f"daemon not found: {daemon}")
    SESSIONS_DIR.mkdir(parents=True, exist_ok=True)
    # Never launch a second browser on a busy profile: Chromium hands the launch to the running
    # window, which opens one more about:blank tab per attempt (tab storm seen 2026-10-30).
    old = read_state(session)
    if old and old.get("pid") and pid_alive(old["pid"]):
        raise BAError("session_busy", f"daemon pid {old['pid']} of session '{session}' is alive but not answering; "
                      "wait and retry, or run 'stop' first - not starting a second browser")
    try:
        state_path(session).unlink(missing_ok=True)  # remove stale state file
    except OSError:
        pass
    config = {"session": session, "headed": headed, "allow": allow,
              "idleMinutes": idle_minutes, "baseDir": str(BASE_DIR),
              "uploadDirs": [os.path.abspath(d) for d in (upload_dirs or [])], "shots": shots,
              "confirmClicks": bool(confirm_clicks), "profile": profile or "default", "keepTabs": bool(keep_tabs)}
    env = dict(os.environ)  # PW_MODULE passes through if set
    env["BA_CONFIG"] = json.dumps(config)
    kwargs = dict(cwd=str(SCRIPT_DIR), env=env, stdin=subprocess.DEVNULL,
                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)
    if os.name == "nt":
        kwargs["creationflags"] = (subprocess.DETACHED_PROCESS
                                   | subprocess.CREATE_NEW_PROCESS_GROUP
                                   | subprocess.CREATE_NO_WINDOW)
    else:
        kwargs["start_new_session"] = True
    proc = wmi_pid = None
    if os.name == "nt" and in_windows_job():
        # A job runner (Bridge, KILL_ON_JOB_CLOSE) would kill the browser when the job ends;
        # a process created by WMI is outside the job, so the window stays for the user.
        wmi_pid = launch_outside_job(node, daemon, session, config)
    if wmi_pid is None:
        try:
            proc = subprocess.Popen([node, str(daemon)], **kwargs)
        except OSError as e:
            raise BAError("start_failed", f"cannot launch node: {e}")
    deadline = time.monotonic() + START_TIMEOUT
    while time.monotonic() < deadline:
        state = live_state(session)
        if state:
            return state
        if (proc.poll() is not None) if proc else not pid_alive(wmi_pid):
            tail = tail_log(session)
            if any("startup failed: profile_busy" in ln for ln in tail.splitlines()[-12:]):
                raise BAError("profile_busy", "the CO browser is open but its DevTools port does not answer; "
                              "close all CO browser windows and retry")
            raise BAError("start_failed", f"daemon exited early; "
                          f"last log lines:\n{tail_log(session)}")
        time.sleep(0.25)
    raise BAError("start_timeout", f"daemon not ready after {START_TIMEOUT}s; "
                  f"last log lines:\n{tail_log(session)}")


def public(state):
    """State info without the secret token."""
    return {k: v for k, v in state.items() if k != "token"}


def cmd_start(a):
    state = live_state(a.session)
    if state:
        emit({"ok": True, "session": a.session, "already_running": True, **public(state)})
        return 0
    state = start_daemon(a.session, a.headed, parse_allow(a.allow), a.idle_minutes, a.upload_dir, not a.no_shots,
                         confirm_clicks=a.confirm_clicks, profile=a.profile, keep_tabs=a.keep_tabs)
    emit({"ok": True, "session": a.session, "already_running": False, **public(state)})
    return 0


def cmd_stop(a):
    state = live_state(a.session)
    if state:
        try:
            call(state, "stop")
        except BAError:
            pass  # daemon may close the connection while exiting
    emit({"ok": True, "stopped": bool(state)})
    return 0


# Map subcommand -> (daemon command, args builder)
ACTIONS = {
    "status": ("status", lambda a: {}),
    "navigate": ("navigate", lambda a: {"url": a.url, "newTab": getattr(a, "new_tab", False)}),
    "back": ("back", lambda a: {}),
    "reload": ("reload", lambda a: {}),
    "snapshot": ("snapshot", lambda a: {"maxChars": a.max_chars if snapshot_full(a) else FETCH_ALL, "tab": a.tab}),
    "click": ("click", lambda a: {"ref": a.ref, "confirm": a.confirm}),
    "type": ("type", lambda a: {"ref": a.ref, "text": a.text, "submit": a.submit, "confirm": a.confirm}),
    "select": ("select", lambda a: {"ref": a.ref, "value": a.value}),
    "press": ("press", lambda a: {"key": a.key, "confirm": a.confirm}),
    "scroll": ("scroll", lambda a: {"dy": -a.pixels if a.up else a.pixels}),
    "wait": ("wait", lambda a: {"text": a.text, "ms": a.ms, "load": a.load}),
    "screenshot": ("screenshot", lambda a: {"out": os.path.abspath(a.out), "full": a.full}),
    "tabs": ("tabs", lambda a: {"all": True} if a.all else {}),
    "tab": ("tab", lambda a: {"index": a.index}),
    "close-tab": ("close-tab", lambda a: {"index": a.index}),
    "type-secret": ("type-secret", lambda a: {"ref": a.ref, "secret": keyring_secret(a)}),
    "upload": ("upload", lambda a: {"ref": a.ref, "path": os.path.abspath(a.path), "confirm": a.confirm}),
}


def keyring_secret(a):
    """Read (or with --generate create) the secret for type-secret; it goes only to the daemon."""
    if not a.keyring.startswith("aiwb/"):
        raise BAError("forbidden", "keyring service must start with aiwb/")
    try:
        import keyring
    except ImportError:
        raise BAError("error", "python package keyring is not installed")
    value = keyring.get_password(a.keyring, a.account)
    if value is None and a.generate:
        import secrets
        import string
        alphabet = string.ascii_letters + string.digits
        n = max(12, a.generate)
        while True:
            body = "".join(secrets.choice(alphabet) for _ in range(n - 2))
            if any(c.isupper() for c in body) and any(c.islower() for c in body) and any(c.isdigit() for c in body):
                break
        value = body + secrets.choice("!#%*") + secrets.choice(string.digits)
        keyring.set_password(a.keyring, a.account, value)
        a._generated = True
    if value is None:
        raise BAError("error", f"no secret in keyring {a.keyring} / {a.account}; use --generate N or secret-prompt")
    return value


# ---------- Captcha by permission (0.8.0) ----------
YES_RE = re.compile(r"(?<!\w)(да|yes|ok|ок|окей|разрешаю|можно|давай|go|ja|oui)(?!\w)", re.I)
NO_RE = re.compile(r"(?<!\w)(нет|no|nee|non|не надо|не нужно|отмена|cancel|стоп|stop)(?!\w)", re.I)
CHAT_RE = re.compile(r"CHAT-\d{14}-[0-9A-Z]{4}")


def chat_id(a):
    """--chat or the chat of the working directory (scratch of an AI Workbench chat)."""
    if a.chat:
        if not CHAT_RE.fullmatch(a.chat):
            raise BAError("error", "bad --chat id")
        return a.chat
    m = CHAT_RE.search(os.getcwd())
    if not m:
        raise BAError("error", "cannot tell the chat from the working directory; pass --chat CHAT-...")
    return m.group(0)


def chat_reader():
    """aiwb-chat client (sibling tool): reads the chat from the CO database with the home PC key."""
    import importlib.util
    path = SCRIPT_DIR.parent / "aiwb-chat" / "aiwb-chat.py"
    spec = importlib.util.spec_from_file_location("aiwb_chat", path)
    if not spec or not path.exists():
        raise BAError("error", f"aiwb-chat tool not found at {path}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def parse_utc(text):
    from datetime import datetime, timezone
    t = str(text or "").replace("T", " ").rstrip("Z")
    for fmt in ("%Y-%m-%d %H:%M:%S.%f", "%Y-%m-%d %H:%M:%S"):
        try:
            return datetime.strptime(t[:26], fmt).replace(tzinfo=timezone.utc).timestamp()
        except ValueError:
            pass
    return None


def find_permission(chat, code, created_utc):
    """The user's own message after the request that names the code and says yes (and not no)."""
    mod = chat_reader()
    created = parse_utc(created_utc) or 0
    try:
        res = mod.call("chat_read", {"chatId": chat, "limit": 15})
    except mod.CliError as e:
        raise BAError("error", f"cannot read the chat: {e}")
    for msg in reversed(res.get("messages") or []):
        if msg.get("type") != "user_message":
            continue
        t = parse_utc(msg.get("createdUtc"))
        if t is None or t < created - 5:
            continue
        text = msg.get("text") or ""
        if len(text) >= 115:  # list gives the first 120 chars: read the whole message
            try:
                full = mod.call("chat_read", {"chatId": chat, "seq": msg.get("seq"), "around": 0})
                text = next((m.get("text") or "" for m in full.get("messages") or [] if m.get("seq") == msg.get("seq")), text)
            except mod.CliError:
                pass
        if not re.search(r"(?<!\d)" + re.escape(code) + r"(?!\d)", text):
            continue
        if NO_RE.search(text):
            return None, f"message #{msg.get('seq')} names the code but says no"
        if YES_RE.search(text):
            return {"chat": chat, "seq": msg.get("seq"), "quote": text[:200]}, None
        return None, f"message #{msg.get('seq')} names the code but has no clear yes"
    return None, "no reply of the user with the code after the request"


def cmd_captcha(a):
    state = live_state(a.session)
    if state is None:
        raise BAError("no_session", f"browser session '{a.session}' is not running")
    act = a.captcha_cmd
    out = os.path.abspath(a.out) if getattr(a, "out", None) else None
    if act == "request":
        reply = call(state, "captcha-request", {"out": out})
    elif act == "grant":
        info = call(state, "captcha-info", {"id": a.id})
        if not info.get("ok"):
            emit(info)
            return 1
        evidence, why = find_permission(chat_id(a), info["code"], info["createdUtc"])
        if not evidence:
            emit({"ok": False, "code": "captcha_not_granted", "id": a.id, "error": why,
                  "hint": "wait for the user's reply with the code and yes; never write it yourself"})
            return 1
        reply = call(state, "captcha-grant", {"id": a.id, "evidence": evidence, "out": out})
    elif act == "tap":
        reply = call(state, "captcha-tap", {"id": a.id, "x": a.x, "y": a.y, "out": out})
    elif act == "drag":
        reply = call(state, "captcha-drag", {"id": a.id, "x1": a.x1, "y1": a.y1, "x2": a.x2, "y2": a.y2, "out": out})
    elif act == "type":
        reply = call(state, "captcha-type", {"id": a.id, "text": a.text, "out": out})
    elif act == "key":
        reply = call(state, "captcha-key", {"id": a.id, "key": a.key, "out": out})
    elif act == "shot":
        reply = call(state, "captcha-shot", {"id": a.id, "out": out})
    elif act == "cancel":
        reply = call(state, "captcha-cancel", {"id": a.id})
    else:
        reply = call(state, "captcha-list", {})
    reply = {k: v for k, v in reply.items() if v is not None}
    emit(reply)
    return 0 if reply.get("ok") else 1


def cmd_action(a):
    cmd, build = ACTIONS[a.command]
    state, warning = live_state(a.session), None
    if a.command == "navigate":
        if state is None:
            # Options only exist on some subparsers (e.g. navigate), hence getattr
            state = start_daemon(a.session, a.headed, parse_allow(a.allow), 30,
                                 getattr(a, "upload_dir", None), not getattr(a, "no_shots", False),
                                 confirm_clicks=getattr(a, "confirm_clicks", False),
                                 profile=getattr(a, "profile", "default"), keep_tabs=getattr(a, "keep_tabs", False))
        elif a.allow is not None:
            warning = "session already running; --allow ignored (restart the session to change it)"
    if state is None:
        raise BAError("no_session", f"browser session '{a.session}' is not running; "
                      "run: browser-agent.py start")
    if getattr(a, "confirm", False) or a.command == "type-secret":
        # 0.10.0: after a read with signs of prompt injection, submits/purchases/passwords need the owner's yes
        confirm = bool(getattr(a, "confirm", False))
        held = read_guard_hold(f"browser-agent {a.command} {getattr(a, 'ref', '') or getattr(a, 'key', '') or ''}"
                               f"{' --confirm' if confirm else ''}",
                               kind="submit" if confirm else "secret")
        if held:
            emit(held)
            return 1
    args = {k: v for k, v in build(a).items() if v is not None}
    reply = call(state, cmd, args)
    if reply.get("code") == "purchase_hold":
        # 0.12.0 (owner 03.11): only money waits for the owner in every chat - a click/Enter on pay/buy/order/book
        held = read_guard_hold(f"purchase in the browser: {reply.get('element') or a.command}", kind="purchase", always=True)
        if held:
            emit(held)
            return 1
        reply = call(state, cmd, {**args, "paid": True})
    if warning:
        reply["warning"] = warning
    if a.command == "snapshot" and reply.get("ok"):
        return print_snapshot(a, reply)
    if getattr(a, "_generated", False):
        reply["generated"] = True
    emit(reply)
    return 0 if reply.get("ok") else 1


# ---------- Compact snapshots (0.9.0) ----------
FETCH_ALL = 5_000_000  # chars asked from the daemon when the full text goes to a file


def snapshot_full(a):
    """Old behaviour: --full, or an explicit --max-chars without a compact option (callers before 0.9.0)."""
    if a.find or a.grep or a.ref or a.role or a.lines or a.compact:
        return False
    return a.full or a.max_chars is not None


def snap_query(a, text, title, url, path, default):
    """Render the view asked by --ref/--role/--lines/--find/--grep (query) or the default one."""
    import snapcompact as sc
    kw = dict(budget=a.budget, title=title, url=url, path=path)
    query = getattr(a, "query", None)
    try:
        if a.ref:
            return sc.subtree(text, a.ref, **kw)
        if a.role:
            return sc.by_role(text, a.role, **kw)
        if a.lines:
            return sc.fragment(text, a.lines, **kw)
        if a.grep or a.find or query:
            regex = bool(a.grep) or bool(getattr(a, "regex", False))
            return sc.find(text, a.grep or a.find or query, regex=regex, context=a.context, **kw)
    except ValueError as e:
        raise BAError("error", str(e))
    return default()


def print_snapshot(a, reply):
    import snapcompact as sc
    if snapshot_full(a):
        if a.max_chars is None:
            a.max_chars = 20000
        if a.text:
            print(f"# {reply.get('title', '')} | {reply.get('url', '')} | untrusted page content")
            print(read_guard(reply.get("snapshot") or "", f"page {reply.get('url', '')}"))
        else:
            emit(reply)
        return 0
    path = sc.save(OWNER, a.session, reply)
    text, title, url = reply.get("snapshot") or "", reply.get("title", ""), reply.get("url", "")
    body = snap_query(a, text, title, url, path,
                      lambda: sc.summary(text, title=title, url=url, path=path, budget=a.budget))
    if a.json:
        emit({"ok": True, "url": url, "title": title, "tab": reply.get("tab"), "untrusted": True,
              "file": str(path), "chars": len(text), "summary": body})
    else:
        print(read_guard(body, f"page {url}"))
    return 0


def cmd_search(a):
    """Search a saved snapshot file offline (no daemon call); 'last' = newest file of this job and session."""
    import snapcompact as sc
    f = a.file
    if f == "last":
        f = sc.latest(OWNER, a.session)
        if not f:
            raise BAError("error", "no saved snapshot for this job and session; run snapshot first")
    try:
        meta, text = sc.load(f)
    except OSError as e:
        raise BAError("error", f"cannot read {f}: {e}")
    title, url = meta.get("title", ""), meta.get("url", "")
    print(read_guard(snap_query(a, text, title, url, f,
                                lambda: sc.summary(text, title=title, url=url, path=f, budget=a.budget)), f"page {url}"))
    return 0


def query_opts(s):
    s.add_argument("--find", default=None, metavar="TEXT", help="only nodes containing TEXT (case-insensitive)")
    s.add_argument("--grep", default=None, metavar="REGEX", help="only nodes matching REGEX (case-insensitive)")
    s.add_argument("--ref", default=None, metavar="eN", help="the node with this ref and its subtree (form, dialog, row)")
    s.add_argument("--role", default=None, metavar="ROLE[,ROLE]", help="all nodes of these roles (textbox, button, row...)")
    s.add_argument("--lines", default=None, metavar="A:B", help="raw snapshot lines A..B (1-based), 'A:' to the end")
    s.add_argument("--context", type=int, default=1, help="lines of context around --find/--grep hits (default 1)")
    s.add_argument("--budget", type=int, default=4000, help="max chars printed (default 4000)")


def build_parser():
    common = argparse.ArgumentParser(allow_abbrev=False, add_help=False)
    common.add_argument("--session", default="default", help="session name: journal, settings, kept tabs (default: default)")
    common.add_argument("--owner", default=None, help="tab owner (default: the calling agent job; env AIWB_BA_OWNER)")
    common.add_argument("--last-resort", default=None, metavar="WHY",
                        help="job of another environment (not the owner's): use the CO browser anyway, with the reason "
                             "(env AIWB_BA_LAST_RESORT); otherwise own_device_first")
    p = argparse.ArgumentParser(allow_abbrev=False, prog="browser-agent.py", description="Drive the CO browser (Edge of CO Desktop) in own tabs.")
    sub = p.add_subparsers(dest="command", required=True)
    add = lambda name, h: sub.add_parser(name, allow_abbrev=False, parents=[common], help=h)

    s = add("start", "start a browser session")
    s.add_argument("--headed", action="store_true", help="ignored since 0.7.0 (the CO browser is always visible)")
    s.add_argument("--profile", default="default", help="CO browser profile: default, or a name the owner allowed in CO\browser-profiles.json (own Edge window)")
    s.add_argument("--keep-tabs", action="store_true", help="tabs stay open after the job and the daemon (drafts for the user)")
    s.add_argument("--allow", default="*", help='comma-separated host patterns (default "*")')
    s.add_argument("--idle-minutes", type=int, default=30, help="auto-stop after idle minutes")
    s.add_argument("--upload-dir", action="append", default=None, help="folder files may be uploaded from; repeatable")
    s.add_argument("--no-shots", action="store_true", help="do not save a screenshot after each action")
    s.add_argument("--confirm-clicks", action="store_true",
                   help="require --confirm for clicks with unclear effect (category 'click')")
    add("stop", "stop the session")
    add("status", "show session status")
    s = add("navigate", "open a URL (auto-starts the session)")
    s.add_argument("url")
    s.add_argument("--allow", default=None, help="host patterns used if the session is auto-started")
    s.add_argument("--headed", action="store_true", help="ignored since 0.7.0")
    s.add_argument("--profile", default="default", help="CO browser profile: default, or a name the owner allowed in CO\browser-profiles.json (own Edge window)")
    s.add_argument("--keep-tabs", action="store_true", help="tabs stay open after the job and the daemon (drafts for the user)")
    s.add_argument("--upload-dir", action="append", default=None, help="folder files may be uploaded from; repeatable")
    s.add_argument("--no-shots", action="store_true", help="do not save a screenshot after each action")
    s.add_argument("--new-tab", action="store_true", help="open the URL in a new tab (other tabs keep their state)")
    s.add_argument("--confirm-clicks", action="store_true",
                   help="require --confirm for clicks with unclear effect (category 'click')")
    add("back", "go back")
    add("reload", "reload the page")
    s = add("snapshot", "page snapshot: compact summary by default, full text saved to a file")
    s.add_argument("--full", action="store_true", help="old output: JSON with the whole snapshot (up to --max-chars)")
    s.add_argument("--max-chars", type=int, default=None,
                   help="full mode limit (default 20000); given alone it selects the full mode (pre-0.9.0 callers)")
    s.add_argument("--tab", type=int, default=None)
    s.add_argument("--text", action="store_true", help="full mode: print plain text instead of JSON")
    s.add_argument("--compact", action="store_true", help="compact mode even with --max-chars")
    query_opts(s)
    s.add_argument("--json", action="store_true", help="compact mode: one JSON line with summary and file")
    s = add("search", "query a saved snapshot file offline (path printed by snapshot, or 'last'); summary if no query")
    s.add_argument("file", help="snapshot file or 'last' (newest of this job and --session)")
    s.add_argument("query", nargs="?", default=None, help="text to find (case-insensitive)")
    s.add_argument("--regex", action="store_true", help="query is a regular expression")
    query_opts(s)
    s = add("click", "click an element")
    s.add_argument("ref")
    s.add_argument("--confirm", action="store_true")
    s = add("type", "type text into an element")
    s.add_argument("ref")
    s.add_argument("text")
    s.add_argument("--submit", action="store_true")
    s.add_argument("--confirm", action="store_true")
    s = add("type-secret", "fill a password/one-time-code field from the keyring (value never printed)")
    s.add_argument("ref")
    s.add_argument("--keyring", required=True, help="keyring service, must start with aiwb/")
    s.add_argument("--account", default="default", help="keyring account (default: default)")
    s.add_argument("--generate", type=int, default=0, metavar="N",
                   help="if the secret is missing, generate an N-char password (min 12) and save it to the keyring")
    s = add("select", "select an option")
    s.add_argument("ref")
    s.add_argument("value")
    s = add("press", "press a key")
    s.add_argument("key")
    s.add_argument("--confirm", action="store_true")
    s = add("scroll", "scroll the page")
    s.add_argument("--up", action="store_true")
    s.add_argument("--pixels", type=int, default=800)
    s = add("wait", "wait for text, time or load")
    s.add_argument("--text", default=None)
    s.add_argument("--ms", type=int, default=None)
    s.add_argument("--load", action="store_true")
    s = add("screenshot", "save a screenshot")
    s.add_argument("--out", required=True)
    s.add_argument("--full", action="store_true")
    s = add("tabs", "list tabs")
    s.add_argument("--all", action="store_true", help="every tab of the CO browser as the tab chamberlain sees it (holder, lock, free)")
    s = add("tab", "switch to tab")
    s.add_argument("index", type=int)
    s = add("close-tab", "close a tab without the leave-page prompt (unsent text in it is discarded)")
    s.add_argument("index", type=int)
    s = add("upload", "upload a local file into a file input")
    s.add_argument("ref")
    s.add_argument("path")
    s.add_argument("--confirm", action="store_true")
    s = add("captcha", "solve one captcha only with the user's permission for it (see SKILL.md)")
    cs = s.add_subparsers(dest="captcha_cmd", required=True)
    cadd = lambda name, h: cs.add_parser(name, allow_abbrev=False, parents=[common], help=h)
    c = cadd("request", "open a request for the captcha in the current tab: id, one-time code, screenshot")
    c.add_argument("--out", required=True, help="PNG for the user (put it into the chat folder)")
    c = cadd("grant", "check the user's reply with the code in the chat and unlock the request")
    c.add_argument("id")
    c.add_argument("--chat", default=None, help="chat id (default: from the working directory)")
    c.add_argument("--out", default=None)
    c = cadd("tap", "click at CSS pixel X Y of the screenshot")
    c.add_argument("id"); c.add_argument("x", type=float); c.add_argument("y", type=float)
    c.add_argument("--out", default=None)
    c = cadd("drag", "drag from X1 Y1 to X2 Y2 (slider)")
    c.add_argument("id")
    for n in ("x1", "y1", "x2", "y2"):
        c.add_argument(n, type=float)
    c.add_argument("--out", default=None)
    c = cadd("type", "type the captcha text into the focused field")
    c.add_argument("id"); c.add_argument("text")
    c.add_argument("--out", default=None)
    c = cadd("key", "press Enter, Tab, Backspace, Space, Escape or an arrow")
    c.add_argument("id"); c.add_argument("key")
    c.add_argument("--out", default=None)
    c = cadd("shot", "new screenshot of the request tab")
    c.add_argument("id"); c.add_argument("--out", required=True)
    c = cadd("cancel", "end the request (the captcha stays for the user in the browser window)")
    c.add_argument("id")
    cadd("list", "requests of this session and the 24 h count")
    return p


def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    a = build_parser().parse_args(argv)
    global OWNER
    OWNER = re.sub(r"[^A-Za-z0-9_.:-]", "-", a.owner)[:64] if a.owner else detect_owner()
    global LAST_RESORT_NOTE
    try:
        if a.command not in ("search", "status", "stop"):
            LAST_RESORT_NOTE = check_environment(a)
        if a.command == "start":
            return cmd_start(a)
        if a.command == "stop":
            return cmd_stop(a)
        if a.command == "captcha":
            return cmd_captcha(a)
        if a.command == "search":
            return cmd_search(a)
        return cmd_action(a)
    except BAError as e:
        emit(e.payload())
        return 2


if __name__ == "__main__":
    sys.exit(main())
