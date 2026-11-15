#!/usr/bin/env python3
"""whatsapp-agent: the agent's own WhatsApp account (linked device, headless) - login, status, send, read, inbox.

The service (service.mjs, whatsapp-web.js in headless Edge) keeps the session; this CLI starts it and talks to its
local API. Output: one JSON line. Exit 0 ok, 1 refused/failed, 2 cannot run.
"""
import argparse
import json
import os
import secrets
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "AIWorkbench" / "wa-agent"
RUNTIME = BASE / "runtime"
STATE = BASE / "state"
PORT = int(os.environ.get("WA_AGENT_PORT", "8789"))
KR_SERVICE, KR_ACCOUNT = "aiwb/whatsapp-agent", "local-api"
WWEBJS = "whatsapp-web.js@1.34.8"


def out(obj, code=0):
    print(json.dumps(obj, ensure_ascii=False))
    sys.exit(code)


def token():
    import keyring
    t = keyring.get_password(KR_SERVICE, KR_ACCOUNT)
    if not t:
        t = secrets.token_urlsafe(32)
        keyring.set_password(KR_SERVICE, KR_ACCOUNT, t)
    return t


def api(path, body=None, timeout=60):
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}{path}", data=json.dumps(body or {}).encode(),
                                 headers={"x-token": token(), "content-type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        return json.loads(e.read() or b'{"ok":false}')
    except OSError:
        return None


def file_status():
    try:
        return json.loads((STATE / "status.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def cmd_setup(a):
    RUNTIME.mkdir(parents=True, exist_ok=True)
    if not (RUNTIME / "package.json").exists():
        (RUNTIME / "package.json").write_text('{"name":"wa-agent-runtime","private":true}', encoding="utf-8")
    env = dict(os.environ, PUPPETEER_SKIP_DOWNLOAD="1")
    npm = "npm.cmd" if os.name == "nt" else "npm"
    r = subprocess.run([npm, "install", WWEBJS, "qrcode@1", "--no-audit", "--no-fund"], cwd=RUNTIME, env=env,
                       capture_output=True, text=True)
    out({"ok": r.returncode == 0, "runtime": str(RUNTIME), "log": (r.stdout + r.stderr)[-800:]}, 0 if r.returncode == 0 else 2)


def service_cmd(a):
    node = "node"
    cmd = [node, str(HERE / "service.mjs"), "--state", str(STATE), "--runtime", str(RUNTIME), "--port", str(PORT)]
    if a.pair_phone:
        cmd += ["--pair-phone", a.pair_phone]
    if a.qr_out:
        cmd += ["--qr-out", a.qr_out]
    if a.forward:
        # "co" = the CO module whatsapp: this script's `forward` puts the message into the configured chat
        fwd = f'"{sys.executable}" "{Path(__file__).resolve()}" forward' if a.forward == "co" else a.forward
        cmd += ["--forward", fwd]
    if a.headless:
        cmd += ["--headless"]
    return cmd


def cmd_run(a):
    """Foreground (for a module service / scheduled task): restarts the session when it drops."""
    if not (RUNTIME / "node_modules" / "whatsapp-web.js").exists():
        out({"ok": False, "error": "runtime_missing", "hint": "whatsapp-agent.py setup"}, 2)
    env = dict(os.environ, WA_AGENT_TOKEN=token())
    while True:
        r = subprocess.run(service_cmd(a), env=env, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if r.returncode in (0, 2) and not a.forever:
            sys.exit(r.returncode)
        time.sleep(3)


def cmd_start(a):
    s = api("/status", timeout=5)
    if s and s.get("ok"):
        out({"ok": True, "already": True, **{k: s.get(k) for k in ("state", "me", "pairingCode")}})
    if not (RUNTIME / "node_modules" / "whatsapp-web.js").exists():
        out({"ok": False, "error": "runtime_missing", "hint": "whatsapp-agent.py setup"}, 2)
    STATE.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, WA_AGENT_TOKEN=token())
    logf = open(STATE / "service.log", "ab")
    subprocess.Popen(service_cmd(a), env=env, stdout=logf, stderr=logf, stdin=subprocess.DEVNULL,
                     creationflags=getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NO_WINDOW", 0))
    deadline = time.time() + a.timeout
    s = None
    while time.time() < deadline:
        time.sleep(2)
        s = file_status()
        if s and s.get("state") in ("ready", "login_required", "auth_failure", "start_failed") and (
                s.get("state") != "login_required" or not a.pair_phone or s.get("pairingCode")):
            break
    s = s or {}
    res = {"ok": s.get("state") in ("ready", "login_required", "authenticated"), "state": s.get("state"), "me": s.get("me"),
           "error": s.get("error")}
    if s.get("state") == "login_required":
        res["pairingCode"] = s.get("pairingCode")
        res["qr"] = str(STATE / "qr.png")
    out(res, 0 if res["ok"] else 1)


def cmd_status(a):
    s = api("/status", timeout=5)
    if s is None:
        out({"ok": False, "state": "stopped", "last": file_status()}, 1)
    out(s)


def cmd_stop(a):
    s = file_status()
    if not s or not s.get("pid"):
        out({"ok": True, "stopped": False})
    subprocess.run(["taskkill", "/PID", str(s["pid"]), "/T", "/F"], capture_output=True)
    out({"ok": True, "stopped": True, "pid": s["pid"]})


def cmd_send(a):
    # household members' jobs may not speak through the owner's agent account
    if os.environ.get("AIWB_JOB_ROLE", "owner") not in ("owner", ""):
        out({"ok": False, "error": "forbidden_role"}, 1)
    text = Path(a.text_file).read_text(encoding="utf-8") if a.text_file else a.text
    if not text or not text.strip():
        out({"ok": False, "error": "empty_text"}, 1)
    r = api("/send", {"to": a.to, "text": text, "by": os.environ.get("AIWB_JOB_ID")}, timeout=60)
    if r is None:
        out({"ok": False, "error": "service_stopped"}, 2)
    out(r, 0 if r.get("ok") else 1)


def simple(path, body):
    r = api(path, body, timeout=60)
    if r is None:
        out({"ok": False, "error": "service_stopped"}, 2)
    out(r, 0 if r.get("ok") else 1)


def cmd_inbox(a):
    p = STATE / "inbox.jsonl"
    rows = [json.loads(l) for l in p.read_text(encoding="utf-8").splitlines() if l.strip()] if p.exists() else []
    if a.contacts_only:
        rows = [r for r in rows if r.get("contact")]
    out({"ok": True, "messages": rows[-a.limit:]})


CONFIG = BASE / "config.json"
FORWARD_WAIT_S = 600


def load_config():
    try:
        return json.loads(CONFIG.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def install_get(key, default=None):
    try:
        d = json.loads((BASE.parent / "install.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default
    for part in key.split("."):
        d = d.get(part) if isinstance(d, dict) else None
    return d if d is not None else default


def co_call(op, params, timeout=30):
    """Host op of db.php with the home PC's key (keyring aiwb/co-push), as aiwb-chat does."""
    import base64
    import gzip
    import keyring
    key = keyring.get_password("aiwb/co-push", "default")
    url = os.environ.get("AIWB_DB_URL") or ((install_get("site.url") or "").rstrip("/") + "/agent/db.php")
    if not key or not url.startswith("https://"):
        raise RuntimeError("co_unconfigured")
    raw = gzip.compress(json.dumps({"op": op, **params}, ensure_ascii=False).encode())
    req = urllib.request.Request(url, data=json.dumps({"gz": base64.b64encode(raw).decode()}).encode(), method="POST",
                                 headers={"Content-Type": "application/json", "X-CO-Push-Key": key,
                                          "User-Agent": "whatsapp-agent/0.2.0"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        try:
            return json.loads(e.read())
        except ValueError:
            return {"error": f"http_{e.code}"}


def cmd_config(a):
    c = load_config()
    for k in ("chat", "env", "assistant"):
        v = getattr(a, k)
        if v is not None:
            c[k] = v
    if not c.get("env"):
        owner = next((m for m in install_get("members", []) or [] if m.get("role") == "owner"), {})
        if owner.get("env"):
            c["env"] = owner["env"]
    BASE.mkdir(parents=True, exist_ok=True)
    CONFIG.write_text(json.dumps(c, ensure_ascii=False, indent=1), encoding="utf-8")
    out({"ok": True, "config": c})


def cmd_forward(a):
    """Called by the service per contact turn: message JSON on stdin -> {"reply", "jobId"} after the CO job ends."""
    item = json.loads(sys.stdin.read() or "{}")
    c = load_config()
    if not c.get("chat") or not c.get("env"):
        out({"error": "no_chat", "hint": "whatsapp-agent.py config --chat CHAT-..."}, 1)
    payload = {"chatId": c["chat"], "env": c["env"], "contact": item.get("from"), "name": item.get("name") or item.get("pushname"),
               "number": item.get("number"), "text": item.get("text"), "assistant": c.get("assistant")}
    deadline = time.time() + FORWARD_WAIT_S
    job = {}
    while time.time() < deadline:
        job = co_call("wa_ask", payload)
        if "busy" not in job:
            break
        time.sleep(5)
    if not job.get("jobId"):
        out({"error": job.get("error") or "busy"}, 1)
    while time.time() < deadline:
        time.sleep(3)
        try:
            r = co_call("wa_answer", {"jobId": job["jobId"]})
        except OSError:
            continue
        if r.get("state") in ("completed", "failed", "cancelled", "canceled") or r.get("error"):
            reply = (r.get("answer") or "").strip()
            out({"reply": reply or None, "jobId": job["jobId"], "state": r.get("state"), "failure": r.get("failure")},
                0 if reply else 1)
    out({"error": "timeout", "jobId": job["jobId"]}, 1)


def cmd_qr_window(a):
    """Small topmost window on the PC desktop with the live QR (WhatsApp changes it every ~20 s); closes on login."""
    if not a.child:
        exe = Path(sys.executable).with_name("pythonw.exe")
        subprocess.Popen([str(exe if exe.exists() else sys.executable), str(Path(__file__)), "qr-window", "--child"],
                         creationflags=getattr(subprocess, "DETACHED_PROCESS", 0))
        out({"ok": True, "window": True})
    import tkinter as tk
    root = tk.Tk()
    root.title("WhatsApp агента CO - вход")
    root.attributes("-topmost", True)
    tk.Label(root, text="Телефон агента: WhatsApp > Настройки > Связанные устройства >\nПривязка устройства, наведите на код",
             font=("Segoe UI", 10)).pack(padx=10, pady=6)
    pic = tk.Label(root)
    pic.pack(padx=10, pady=6)
    note = tk.Label(root, text="", font=("Segoe UI", 9), fg="#a00")
    note.pack(pady=4)
    state = {"m": 0}

    def tick():
        s = file_status() or {}
        if s.get("state") in ("authenticated", "ready"):
            root.destroy()
            return
        q = STATE / "qr.png"
        try:
            m = q.stat().st_mtime
            note.configure(text="" if time.time() - m < 25 else "Код устарел, жду новый...")
            pic.configure(state="normal" if time.time() - m < 25 else "disabled")
            if m != state["m"]:
                state["m"] = m
                img = tk.PhotoImage(file=str(q))
                pic.configure(image=img)
                pic.image = img
        except OSError:
            note.configure(text="Жду новый код...")
            pic.configure(state="disabled")
        except tk.TclError:
            pass
        root.after(1500, tick)

    tick()
    root.mainloop()
    sys.exit(0)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("setup")
    for name in ("start", "run"):
        p = sub.add_parser(name)
        p.add_argument("--pair-phone", help="number of the agent account: log in by an 8-letter code instead of QR")
        p.add_argument("--qr-out", help="also copy the QR picture here")
        p.add_argument("--forward", help="command that answers a contact: JSON message on stdin, {\"reply\"} on stdout")
        p.add_argument("--timeout", type=int, default=90)
        p.add_argument("--forever", action="store_true")
        p.add_argument("--headless", action="store_true", help="no window at all (WhatsApp may refuse to link it)")
    sub.add_parser("status")
    sub.add_parser("stop")
    p = sub.add_parser("send")
    p.add_argument("--to", required=True, help="contact name, +number or chat id")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--text")
    g.add_argument("--text-file")
    p = sub.add_parser("chats")
    p.add_argument("--limit", type=int, default=30)
    p = sub.add_parser("read")
    p.add_argument("--chat", required=True)
    p.add_argument("--limit", type=int, default=30)
    sub.add_parser("contacts")
    p = sub.add_parser("config", help="CO chat the contacts' messages go to (module whatsapp)")
    p.add_argument("--chat")
    p.add_argument("--env")
    p.add_argument("--assistant", help="how the agent introduces itself")
    sub.add_parser("forward", help=argparse.SUPPRESS)
    p = sub.add_parser("qr-window")
    p.add_argument("--child", action="store_true", help=argparse.SUPPRESS)
    p = sub.add_parser("inbox")
    p.add_argument("--limit", type=int, default=30)
    p.add_argument("--contacts-only", action="store_true")
    a = ap.parse_args()
    {"setup": cmd_setup, "start": cmd_start, "run": cmd_run, "status": cmd_status, "stop": cmd_stop, "send": cmd_send,
     "chats": lambda a: simple("/chats", {"limit": a.limit}),
     "read": lambda a: simple("/read", {"chat": a.chat, "limit": a.limit}),
     "contacts": lambda a: simple("/contacts", {}), "qr-window": cmd_qr_window, "inbox": cmd_inbox,
     "config": cmd_config, "forward": cmd_forward}[a.cmd](a)


if __name__ == "__main__":
    main()
