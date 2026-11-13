"""aiwb-subtask.py - run AI Workbench subtasks as parallel child chats.

An agent working in an AI Workbench chat can spawn subtasks. Each subtask runs
as a CHILD CHAT with its own agent session on the job queue. The parent agent
then waits for the answers and reports them in its own reply.

Commands:
  spawn   create a child chat (Drive + DB) and queue its job
  wait    poll jobs until all are finished (or timeout) and print the answers
  status  poll jobs once and print the current state (answers truncated)
  post    send a message into an existing chat (resumes its agent session)
  models  model resources this chat may call as subtasks (own ones and the ones others allowed as slaves)
  hosts   executors of the workbench (Bridges on any machine and the site agent, Freedom F12) with their health

spawn --resource, models, wait and status talk to db.php only (no SSH, no Drive), so they also run on the site host:
the site agent passes AIWB_DB_URL, AIWB_DB_KEY_FILE and AIWB_CHAT_ID.
"""
import argparse
import base64
import ctypes
import ctypes.wintypes as wt
import gzip
import hashlib
import json
import os
import re
import secrets
import string
import sys
import time
from datetime import datetime, timezone

import urllib.error
import urllib.request

# keyring, requests and hz (SSH, paramiko) are imported where needed: the site host has none of them


def _core_dirs():
    """Core tree with install/aiwb_install.py: AIWB_FREEDOM / AIWB_SOURCE, devtree.json "repo", C:/AIWorkbench/freedom."""
    dirs = [os.environ.get("AIWB_FREEDOM"), os.environ.get("AIWB_SOURCE")]
    local = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Local")
    try:
        with open(os.environ.get("AIWB_DEVTREE_CONFIG") or os.path.join(local, "AIWorkbench", "devtree.json"),
                  encoding="utf-8-sig") as f:
            dirs.append((json.load(f) or {}).get("repo"))
    except (OSError, ValueError, AttributeError):
        pass
    return dirs + [r"C:\AIWorkbench\freedom", os.path.expanduser("~/AIWorkbench/freedom")]


def _install():
    """install.json of this install (Freedom) through install/aiwb_install.py of the source tree, or None."""
    for d in _core_dirs():
        if d and os.path.isfile(os.path.join(d, "install", "aiwb_install.py")):
            if os.path.join(d, "install") not in sys.path:
                sys.path.insert(0, os.path.join(d, "install"))
            import aiwb_install
            return aiwb_install
    return None


class _InstallSSH:
    """hz-like SSH helper over install.json site.ssh (aiwb_install.ssh_connect)."""

    @staticmethod
    def connect():
        ai = _install()
        if ai is None:
            raise RuntimeError("no hz.py and no install/aiwb_install.py (set AIWB_FREEDOM to the Freedom tree)")
        return ai.ssh_connect()


def _hz():
    """SSH helper with .connect() -> paramiko SSHClient: the owner's hz.py (AIWB_HZ_DIR, else ~/.ssh) where it
    exists, else install.json site.ssh."""
    d = os.environ.get("AIWB_HZ_DIR") or os.path.join(os.path.expanduser("~"), ".ssh")
    if d not in sys.path:
        sys.path.insert(0, d)
    try:
        import hz  # noqa: E402
        return hz
    except ImportError:
        return _InstallSSH


def _install_get(key):
    ai = _install()
    return ai.get(key) if ai else None


def _mysql():
    """mysql command on the site; database: AIWB_DB_NAME, else install.json site.db.name."""
    name = os.environ.get("AIWB_DB_NAME") or _install_get("site.db.name") or "co_aiwb"
    return f"mysql --defaults-extra-file=$HOME/co-agent/secrets/my.cnf {name} -N -B"


def _db_api():
    """db.php of the site: AIWB_DB_URL (the site agent passes it), else install.json site.url + /agent/db.php."""
    if os.environ.get("AIWB_DB_URL"):
        return os.environ["AIWB_DB_URL"]
    url = _install_get("site.url")
    if not url:
        raise RuntimeError("site unknown: set AIWB_DB_URL or site.url in install.json")
    return url.rstrip("/") + "/agent/db.php"

UA = "aiwb-subtask/0.7"
CRED_TARGET = "AIWorkbench.Bridge.Google"
TOKEN_URL = "https://oauth2.googleapis.com/token"
DRIVE = "https://www.googleapis.com/drive/v3/"
UPLOAD = "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&supportsAllDrives=true"
FOLDER_MIME = "application/vnd.google-apps.folder"
CHAT_RE = re.compile(r"[\\/]chats[\\/](CHAT-\d{14}-[A-Z0-9]{4})(?:[\\/]|$)")
JOB_RE = re.compile(r"^JOB-\d{14}-[A-Z0-9]{6}$")
ALNUM = string.ascii_uppercase + string.digits
POLL_SECONDS = 10
# Freedom F12 (schema 28): any executor id, not only home-main / site
HOST_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$")


def host_id(v):
    if not HOST_RE.match(v or ""):
        raise argparse.ArgumentTypeError(f"invalid executor id {v!r} (letters, digits, _ and -, at most 32)")
    return v


def check_host(host):
    """A host other than home-main/site must be an executor the server knows (db.php executors_host); a server before
    schema 28 knows only those two."""
    if host in ("home-main", "site"):
        return
    try:
        known = {e.get("id") for e in (db_api({"op": "executors_host"}) or {}).get("executors") or []}
    except RuntimeError as e:
        raise RuntimeError(f"executor {host!r}: the server does not list executors ({e}); only home-main and site work there")
    if host not in known:
        raise RuntimeError(f"unknown executor {host!r}; known: {', '.join(sorted(x for x in known if x))} (aiwb-subtask hosts)")


# ---------------------------------------------------------------- SQL
def sql(conn, query):
    stdin, stdout, stderr = conn.exec_command(_mysql(), timeout=60)
    stdin.write(query)
    stdin.channel.shutdown_write()
    out = stdout.read().decode("utf-8", "replace")
    err = stderr.read().decode("utf-8", "replace").strip()
    if err:
        raise RuntimeError(err)
    return [l.split("\t") for l in out.splitlines() if l]


def q(s):
    return "NULL" if s is None else "'" + str(s).replace("\\", "\\\\").replace("'", "''") + "'"


_UNESC = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "b": "\b", "\\": "\\"}


def unescape(s):
    """Undo mysql -B escaping of text columns."""
    return re.sub(r"\\(.)", lambda m: _UNESC.get(m.group(1), m.group(1)), s, flags=re.S)


# ---------------------------------------------------------------- helpers
def rand(n):
    return "".join(secrets.choice(ALNUM) for _ in range(n))


def now_iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"


def push_key():
    """Host key of db.php: a file on the site (AIWB_DB_KEY_FILE), keyring aiwb/co-push on the home PC."""
    path = os.environ.get("AIWB_DB_KEY_FILE")
    if path:
        with open(path, encoding="utf-8") as f:
            return f.read().strip()
    import keyring
    key = keyring.get_password("aiwb/co-push", "default")
    if not key:
        raise RuntimeError("push key 'aiwb/co-push' not found in keyring")
    return key


def db_api(payload, timeout=60):
    """POST a host op to db.php (gzip body: the hosting WAF blocks plain bodies with code); returns the JSON."""
    raw = gzip.compress(json.dumps(payload, ensure_ascii=False).encode("utf-8"))
    body = json.dumps({"gz": base64.b64encode(raw).decode("ascii")}).encode("ascii")
    req = urllib.request.Request(_db_api(), data=body, method="POST", headers={
        "X-CO-Push-Key": push_key(), "Content-Type": "application/json", "User-Agent": UA})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            text = r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"db.php {payload.get('op')}: HTTP {e.code}: {e.read().decode('utf-8', 'replace')[:300]}")
    try:
        res = json.loads(text)
    except ValueError:
        return None
    if isinstance(res, dict) and (res.get("ok") is False or res.get("error")):
        detail = f" - {res['detail']}" if res.get("detail") else ""
        raise RuntimeError(f"db.php {payload.get('op')}: {res.get('error')}{detail}")
    return res


# ---------------------------------------------------------------- Google Drive
class CREDENTIAL(ctypes.Structure):
    _fields_ = [("Flags", wt.DWORD), ("Type", wt.DWORD), ("TargetName", wt.LPWSTR),
                ("Comment", wt.LPWSTR), ("LastWritten", wt.FILETIME),
                ("CredentialBlobSize", wt.DWORD), ("CredentialBlob", ctypes.POINTER(ctypes.c_ubyte)),
                ("Persist", wt.DWORD), ("AttributeCount", wt.DWORD), ("Attributes", ctypes.c_void_p),
                ("TargetAlias", wt.LPWSTR), ("UserName", wt.LPWSTR)]


def read_credential(target):
    adv = ctypes.WinDLL("advapi32", use_last_error=True)
    adv.CredReadW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.POINTER(ctypes.POINTER(CREDENTIAL))]
    adv.CredReadW.restype = wt.BOOL
    adv.CredFree.argtypes = [ctypes.c_void_p]
    pcred = ctypes.POINTER(CREDENTIAL)()
    if not adv.CredReadW(target, 1, 0, ctypes.byref(pcred)):
        raise RuntimeError(f"CredReadW('{target}') failed (error {ctypes.get_last_error()})")
    try:
        c = pcred.contents
        blob = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize)
    finally:
        adv.CredFree(pcred)
    if len(blob) >= 2 and blob[1:2] == b"\x00":
        return blob.decode("utf-16-le").rstrip("\0")
    return blob.decode("utf-8").rstrip("\0")


def google_token():
    import requests
    cred = json.loads(read_credential(CRED_TARGET))
    r = requests.post(TOKEN_URL, data={
        "client_id": cred["client_id"], "client_secret": cred["client_secret"],
        "refresh_token": cred["refresh_token"], "grant_type": "refresh_token"}, timeout=60)
    if r.status_code != 200:
        raise RuntimeError(f"Google token exchange failed: HTTP {r.status_code}: {r.text[:300]}")
    return r.json()["access_token"]


def _check(r, what):
    if r.status_code >= 400:
        raise RuntimeError(f"Drive {what}: HTTP {r.status_code}: {r.text[:300]}")
    return r.json()


def drive_find(tok, parent, name):
    import requests
    esc = lambda s: s.replace("\\", "\\\\").replace("'", "\\'")
    params = {"q": f"'{esc(parent)}' in parents and name='{esc(name)}' and trashed=false",
              "fields": "files(id,name)", "supportsAllDrives": "true", "includeItemsFromAllDrives": "true"}
    r = requests.get(DRIVE + "files", params=params, headers={"Authorization": f"Bearer {tok}"}, timeout=60)
    files = _check(r, f"list '{name}'").get("files", [])
    return files[0]["id"] if files else None


def drive_mkdir(tok, parent, name):
    import requests
    r = requests.post(DRIVE + "files?supportsAllDrives=true", headers={"Authorization": f"Bearer {tok}"},
                      json={"name": name, "mimeType": FOLDER_MIME, "parents": [parent]}, timeout=60)
    return _check(r, f"create folder '{name}'")["id"]


def drive_upload_json(tok, parent, name, obj):
    import requests
    b = "aiwb" + secrets.token_hex(12)
    meta = json.dumps({"name": name, "parents": [parent], "mimeType": "application/json"})
    content = json.dumps(obj, ensure_ascii=False, indent=2)
    body = (f"--{b}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{meta}\r\n"
            f"--{b}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{content}\r\n--{b}--\r\n")
    r = requests.post(UPLOAD, data=body.encode("utf-8"), timeout=60, headers={
        "Authorization": f"Bearer {tok}", "Content-Type": f"multipart/related; boundary={b}"})
    return _check(r, f"upload '{name}'")["id"]


# ---------------------------------------------------------------- commands
def read_prompt(args):
    if args.prompt_file is not None:
        if args.prompt_file == "-":
            return sys.stdin.read()
        with open(args.prompt_file, encoding="utf-8") as f:
            return f.read()
    return sys.stdin.read() if args.prompt == "-" else args.prompt


def parse_context_folders(specs):
    folders = []
    for spec in specs or []:
        parts = spec.split(":")
        if len(parts) == 2:
            root_id, relative_path = parts
            access = "read"
        elif len(parts) == 3:
            root_id, relative_path, access = parts
        else:
            raise RuntimeError(f"invalid --context-folder spec (expected rootId:relativePath[:access]): {spec}")
        if not re.fullmatch(r"[a-z]+", root_id):
            raise RuntimeError(f"invalid rootId in --context-folder: {root_id!r}")
        if not relative_path:
            raise RuntimeError(f"empty relativePath in --context-folder: {spec}")
        if ".." in relative_path:
            raise RuntimeError(f"relativePath must not contain '..': {relative_path}")
        if access not in ("read", "write"):
            raise RuntimeError(f"invalid access in --context-folder (read or write): {access!r}")
        label = re.split(r"[/\\]", relative_path.rstrip("/\\"))[-1]
        folders.append({"rootId": root_id, "relativePath": relative_path, "access": access, "label": label})
    return folders


def detect_parent(given):
    parent = given or os.environ.get("AIWB_CHAT_ID") or ""
    if not parent:
        m = CHAT_RE.search(os.getcwd() + os.sep)
        if not m:
            raise RuntimeError("cannot detect parent chat from current directory; use --parent")
        parent = m.group(1)
    if not re.fullmatch(r"CHAT-\d{14}-[A-Z0-9]{4}", parent):
        raise RuntimeError(f"invalid parent chat id: {parent}")
    return parent


def request_key(payload, args):
    """0.6.1 (review S5-02): the server keeps one subtask per key and parent. The key is the same for the same subtask
    in the same calling job (AIWB_JOB_ID), so repeating the command after a lost answer gets the subtask already queued
    instead of a second one; --again makes a new key on purpose."""
    if args.again:
        return "r-" + secrets.token_hex(16)
    basis = json.dumps([os.environ.get("AIWB_JOB_ID") or "", payload.get("parentChatId"), payload.get("resourceId"),
                        payload.get("model"), payload.get("mode"), payload.get("title"), payload.get("devices"),
                        payload.get("timeoutMinutes"), payload.get("prompt")], ensure_ascii=False)
    return "s-" + hashlib.sha256(basis.encode("utf-8")).hexdigest()[:40]


def spawn_with_key(payload, attempts=4):
    """subtask_spawn with retries of the same key: a timeout or network error is repeated (the server answers the
    first creation again), 'in_progress' (the same key still being created) is waited out."""
    for i in range(attempts):
        last = i == attempts - 1
        try:
            return db_api(payload) or {}
        except RuntimeError as e:
            if last or not ("in_progress" in str(e) or ": HTTP 5" in str(e)):
                raise
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError):
            if last:
                raise
        time.sleep(5 * (i + 1))
    return {}


def cmd_spawn_resource(args):
    """S5: the server creates the child chat and queues the job on the resource's host (own or allowed slave)."""
    if args.top_level or args.context_folder:
        raise RuntimeError("--resource does not support --top-level or --context-folder")
    prompt = read_prompt(args).strip()
    if not prompt:
        raise RuntimeError("empty prompt")
    payload = {"op": "subtask_spawn", "parentChatId": detect_parent(args.parent), "resourceId": args.resource,
               "prompt": prompt, "mode": args.mode, "timeoutMinutes": args.timeout_minutes}
    if args.title:
        payload["title"] = args.title
    if args.model:
        payload["model"] = args.model
    if args.device:
        payload["devices"] = list(args.device)
    payload["requestKey"] = request_key(payload, args)
    res = spawn_with_key(payload)
    if not res.get("jobId"):
        raise RuntimeError(f"subtask_spawn: unexpected answer {res!r}")
    out = {k: res.get(k) for k in ("chatId", "jobId", "title", "host", "agent", "model")}
    if res.get("online") is False:
        out["warning"] = f"host {res.get('host')} is offline now; the job waits in its queue"
    if res.get("repeated"):
        out["note"] = "this subtask was already started by the same spawn (requestKey); --again starts another one"
    print(json.dumps(out, ensure_ascii=False))
    return 0


def cmd_models(args):
    payload = {"op": "models_host"}
    if args.env:
        payload["env"] = args.env
    else:
        payload["chatId"] = detect_parent(args.parent)
    res = db_api(payload) or {}
    items = res.get("resources") or []
    if args.json:
        print(json.dumps(items, ensure_ascii=False, indent=2))
        return 0
    print(f"Model resources for environment {res.get('env')} (call with: spawn --resource ID):")
    for r in items:
        state = {True: "online", False: "offline"}.get(r.get("online"), "?")
        flags = ["own" if r.get("own") else f"slave of {r.get('env')}"]
        if r.get("confidential"):
            flags.append("confidential: only inside its environment")
        if not r.get("callable"):
            flags.append("not callable yet")
        print(f"\n- {r['id']}  {r.get('title')}  [{r.get('kind')}, {r.get('agent')} on {r.get('host') or '-'}, {state}; {', '.join(flags)}]")
        card = r.get("card") or {}
        for k in ("summary", "strengths", "limits", "dataStaysIn", "notes"):
            if card.get(k):
                print(f"    {k}: {card[k]}")
        models = [m.get("id") for m in r.get("models") or [] if m.get("id")]
        if models:
            print(f"    models: {', '.join(models)}" + (f" (default {r['defaultModel']})" if r.get("defaultModel") else ""))
    return 0


def cmd_hosts(args):
    payload = {"op": "executors_host"}
    if args.env:
        payload["env"] = args.env
    items = (db_api(payload) or {}).get("executors") or []
    if args.json:
        print(json.dumps(items, ensure_ascii=False, indent=2))
        return 0
    for e in items:
        state = "takes jobs" if e.get("takesJobs") else ("switched off" if not e.get("enabled", True) else e.get("state") or "silent")
        whose = f", {e['ownerEnv']}'s" if e.get("ownerEnv") else ""
        print(f"- {e['id']}  {e.get('title') or ''}  [{e.get('kind')}, {e.get('os') or '?'}{whose}, v{e.get('version') or '?'}; {state}; "
              f"agents {', '.join(e.get('agents') or []) or '-'}; heard {e.get('seenUtc') or 'never'}]")
    return 0


def cmd_spawn(args):
    if args.resource:
        return cmd_spawn_resource(args)
    check_host(args.host)
    if args.agent == "dispatcher":
        if not args.device:
            raise RuntimeError("dispatcher needs --device (the device with engie_chat)")
        if not args.model:
            args.model = "claude-opus-5.5"
    devices = list(args.device or [])
    for device_id in devices:
        if not re.fullmatch(r"d-[0-9a-f]{16}", device_id):
            raise RuntimeError(f"invalid device id (expected d-<16 hex>): {device_id!r}")
    context_folders = parse_context_folders(args.context_folder)
    prompt = read_prompt(args).strip()
    if not prompt:
        raise RuntimeError("empty prompt")
    parent = detect_parent(args.parent)

    conn = _hz().connect()
    try:
        rows = sql(conn, "SELECT c.project_id, p.env, p.folder_id, c.title FROM chats c "
                         "JOIN projects p ON p.project_id=c.project_id "
                         f"WHERE c.chat_id={q(parent)};\n")
        if not rows:
            raise RuntimeError(f"parent chat {parent} not found")
        row = [unescape(x) for x in rows[0]] + [""] * 4
        project_id, env, project_folder, parent_title = row[:4]
        if not project_folder or project_folder == "NULL":
            raise RuntimeError(f"project {project_id} has no folder_id")

        dt = datetime.now(timezone.utc)
        stamp = dt.strftime("%Y%m%d%H%M%S")
        chat_id, job_id, event_id = f"CHAT-{stamp}-{rand(4)}", f"JOB-{stamp}-{rand(6)}", f"EVT-{stamp}-{rand(6)}"
        now = now_iso(dt)
        title = ("" if args.top_level else "\u2937 ") + (args.title or " ".join(prompt.split())[:60])

        tok = google_token()
        chats_folder = drive_find(tok, project_folder, "chats") or drive_mkdir(tok, project_folder, "chats")
        chat_folder = drive_mkdir(tok, chats_folder, chat_id)
        chat_doc = {
            "chatId": chat_id,
            "projectId": project_id,
            "title": title,
            "rootBranchId": "BR-root",
            "createdUtc": now,
            "updatedUtc": now,
            "defaultMode": args.mode,
        }
        if not args.top_level:
            chat_doc["parent"] = {
                "chatId": parent,
                "seq": 0,
                "kind": "subtask",
                "title": parent_title,
            }
        drive_upload_json(tok, chat_folder, "chat.json", chat_doc)
        db_api({"op": "put", "chats": [dict(chat_doc, folderId=chat_folder)]})
        db_api({
            "op": "append_event",
            "event": {
                "schemaVersion": 1,
                "eventId": event_id,
                "type": "user_message",
                "createdUtc": now,
                "chatId": chat_id,
                "branchId": "BR-root",
                "jobId": job_id,
                "hostId": args.host,
                "agent": args.agent,
                "author": "user" if args.top_level else "subtask",
                "text": prompt,
                "attachments": [],
                "returnFiles": [],
                "conversation": {"operation": "new", "sessionId": None},
                "mode": args.mode,
                "model": args.model,
                "effort": None,
            },
        })
        request = {
            "schemaVersion": 3,
            "protocolMinor": 2,
            "hostId": args.host,
            "jobId": job_id,
            "environmentId": env,
            "projectId": project_id,
            "chatId": chat_id,
            "branchId": "BR-root",
            "agent": args.agent,
            "instruction": prompt if args.top_level else f'[Subtask of chat "{parent_title}" ({parent}): work on it on your own and end with a concise result]\n\n' + prompt,
            "conversation": {"operation": "new", "sessionId": None},
            "attachments": [],
            "artifactPaths": [],
            "timeoutMinutes": args.timeout_minutes,
            "mode": args.mode,
            "model": args.model,
            "effort": None,
            "output": {"autoExport": True},
            "permissions": {"files": "write", "shell": True, "web": True},
            "contextFolders": context_folders,
            **({"devices": devices} if devices else {}),
        }
        rj = q(json.dumps(request, ensure_ascii=False))
        sql(conn, f"INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin, queued_utc, request, sha, synced_utc) VALUES ({q(job_id)}, {q(args.host)}, {q(env)}, {q(project_id)}, {q(chat_id)}, {q(args.agent)}, {q(args.mode)}, {q(args.model)}, 'inbox', 'db', UTC_TIMESTAMP(3), {rj}, SHA2({rj}, 256), UTC_TIMESTAMP(3));\n")
        print(json.dumps({"chatId": chat_id, "jobId": job_id, "title": title}, ensure_ascii=False))
        return 0
    finally:
        conn.close()


def cmd_post(args):
    """Send a message into an EXISTING chat and queue a job that resumes its agent session."""
    prompt = read_prompt(args).strip()
    if not prompt:
        raise RuntimeError("empty prompt")
    chat_id = args.chat
    if not re.fullmatch(r"CHAT-\d{14}-[A-Z0-9]{4}", chat_id):
        raise RuntimeError(f"invalid chat id: {chat_id}")

    conn = _hz().connect()
    try:
        rows = sql(conn, "SELECT c.project_id, p.env FROM chats c JOIN projects p ON p.project_id=c.project_id "
                         f"WHERE c.chat_id={q(chat_id)};\n")
        if not rows:
            raise RuntimeError(f"chat {chat_id} not found")
        project_id, env = [unescape(x) for x in rows[0][:2]]
        busy = sql(conn, f"SELECT job_id FROM jobs WHERE chat_id={q(chat_id)} AND state IN ('inbox','claimed','running');\n")
        if busy:
            raise RuntimeError(f"chat {chat_id} has an unfinished job {busy[0][0]}; try later")
        last = sql(conn, f"SELECT request, IFNULL(JSON_VALUE(result,'$.sessionId'),'') FROM jobs "
                         f"WHERE chat_id={q(chat_id)} AND state='completed' ORDER BY queued_utc DESC LIMIT 1;\n")
        prev, session = ({}, None)
        if last:
            prev = json.loads(unescape(last[0][0]))
            session = unescape(last[0][1]) or (prev.get("conversation") or {}).get("sessionId")
        else:
            # no completed job (e.g. the first one failed): settings of the latest job, new session
            any_last = sql(conn, f"SELECT request FROM jobs WHERE chat_id={q(chat_id)} ORDER BY queued_utc DESC LIMIT 1;\n")
            if any_last:
                prev = json.loads(unescape(any_last[0][0]))
        agent = args.agent or prev.get("agent") or "claude"
        host = args.host or prev.get("hostId") or "home-main"
        if args.host:
            check_host(host)
        if session and (agent != prev.get("agent") or host != (prev.get("hostId") or "home-main")):
            session = None  # provider sessions live only on the host and agent that made them
        mode = prev.get("mode") or "chat"
        model = args.model if args.model is not None else prev.get("model")
        conversation = {"operation": "resume", "sessionId": session} if session else {"operation": "new", "sessionId": None}

        dt = datetime.now(timezone.utc)
        stamp = dt.strftime("%Y%m%d%H%M%S")
        job_id, event_id = f"JOB-{stamp}-{rand(6)}", f"EVT-{stamp}-{rand(6)}"
        db_api({
            "op": "append_event",
            "event": {
                "schemaVersion": 1,
                "eventId": event_id,
                "type": "user_message",
                "createdUtc": now_iso(dt),
                "chatId": chat_id,
                "branchId": "BR-root",
                "jobId": job_id,
                "hostId": host,
                "agent": agent,
                "author": args.author,
                "text": prompt,
                "attachments": [],
                "returnFiles": [],
                "conversation": conversation,
                "mode": mode,
                "model": model,
                "effort": None,
            },
        })
        request = {
            "schemaVersion": 3,
            "protocolMinor": 2,
            "hostId": host,
            "jobId": job_id,
            "environmentId": env,
            "projectId": project_id,
            "chatId": chat_id,
            "branchId": "BR-root",
            "agent": agent,
            "instruction": prompt,
            "conversation": conversation,
            "attachments": [],
            "artifactPaths": [],
            "timeoutMinutes": args.timeout_minutes,
            "mode": mode,
            "model": model,
            "effort": None,
            "output": {"autoExport": True},
            "permissions": prev.get("permissions") or {"files": "write", "shell": True, "web": True},
            "contextFolders": prev.get("contextFolders") or [],
            **({"devices": prev["devices"]} if prev.get("devices") else {}),
        }
        rj = q(json.dumps(request, ensure_ascii=False))
        sql(conn, f"INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin, queued_utc, request, sha, synced_utc) VALUES ({q(job_id)}, {q(host)}, {q(env)}, {q(project_id)}, {q(chat_id)}, {q(agent)}, {q(mode)}, {q(model)}, 'inbox', 'db', UTC_TIMESTAMP(3), {rj}, SHA2({rj}, 256), UTC_TIMESTAMP(3));\n")
        print(json.dumps({"chatId": chat_id, "jobId": job_id, "resume": bool(session)}, ensure_ascii=False))
        return 0
    finally:
        conn.close()


def poll(conn, ids):
    for i in ids:
        if not JOB_RE.match(i):
            raise ValueError(f"invalid job id: {i}")
    in_list = ", ".join(q(i) for i in ids)
    rows = sql(conn, f"SELECT job_id, state, IFNULL(status,''), IFNULL(JSON_VALUE(result,'$.agentFinalResponse'),''), chat_id FROM jobs WHERE job_id IN ({in_list});\n")
    res = {}
    for r in rows or []:
        job_id, state, status, answer, chat = (unescape(x) if x is not None else "" for x in r[:5])
        res[job_id] = (state, status, answer, chat)
    return res


def show(ids, res, limit=None):
    for i in ids:
        if i not in res:
            print(f"=== {i}: not found ===")
            continue
        state, status, answer, chat = res[i]
        print(f"=== {i} ({chat}): {status or state} ===")
        if answer:
            if limit and len(answer) > limit:
                answer = answer[:limit] + "..."
            print(answer)


def poll_server(ids):
    for i in ids:
        if not JOB_RE.match(i):
            raise ValueError(f"invalid job id: {i}")
    res = {}
    for k in range(0, len(ids), 20):
        for j in (db_api({"op": "subtask_poll", "jobIds": ids[k:k + 20]}) or {}).get("jobs") or []:
            if j.get("state") != "missing":
                res[j["jobId"]] = (j.get("state") or "", j.get("status") or "", j.get("answer") or "", j.get("chatId") or "")
    return res


class Poller:
    """db.php subtask_poll (home and site); SSH to the database when the server is older than schema 23."""

    def __init__(self):
        self.conn = None

    def __call__(self, ids):
        if self.conn is None:
            try:
                return poll_server(ids)
            except RuntimeError as e:
                if "unknown_op" not in str(e) and "gz_ops_only" not in str(e):
                    raise
                self.conn = _hz().connect()
        return poll(self.conn, ids)

    def close(self):
        if self.conn is not None:
            self.conn.close()


def cmd_wait(args):
    p = Poller()
    try:
        deadline = time.monotonic() + args.timeout
        timed_out = False
        while True:
            res = p(args.job_ids)
            if all(i in res and res[i][1] for i in args.job_ids):
                break
            if time.monotonic() >= deadline:
                timed_out = True
                break
            time.sleep(POLL_SECONDS)
        show(args.job_ids, res)
        if timed_out:
            print("timeout", file=sys.stderr)
        ok = all(i in res and res[i][1] == "completed" for i in args.job_ids)
        return 0 if ok else 1
    finally:
        p.close()


def cmd_status(args):
    p = Poller()
    try:
        show(args.job_ids, p(args.job_ids), limit=500)
        return 0
    finally:
        p.close()


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("spawn", help="start a subtask as a child chat")
    sp.add_argument("--prompt", help="the subtask; '-' reads stdin")
    sp.add_argument("--prompt-file", help="file with the subtask; '-' reads stdin")
    sp.add_argument("--title")
    sp.add_argument("--agent", choices=["claude", "codex", "dispatcher"], default="claude")
    sp.add_argument("--model")
    sp.add_argument("--timeout-minutes", type=int, default=120, help="job time limit in minutes (default 120; 0 = the host default for the mode)")
    sp.add_argument("--mode", choices=["chat", "cowork"], default="cowork")
    sp.add_argument("--host", type=host_id, default="home-main", help="executor id (default home-main; see 'hosts')")
    sp.add_argument("--resource", metavar="ID", help="model resource from 'models' (e.g. home-main/codex, site/claude); "
                    "the server picks host, agent and default model; own resources and allowed slaves only")
    sp.add_argument("--parent", help="parent chat id (default: from the current directory)")
    sp.add_argument("--again", action="store_true", help="with --resource: start the same subtask once more on purpose "
                    "(by default a repeat of the same spawn in the same job returns the subtask already started)")
    sp.add_argument("--top-level", action="store_true", help="create an ordinary top-level chat in the parent's project (no subtask link)")
    sp.add_argument("--device", metavar="ID", action="append", default=None,
                    help="target device id, e.g. d-b903ab04eea11ba2 (repeatable)")
    sp.add_argument("--context-folder", metavar="SPEC", action="append", default=None,
                    help="rootId:relativePath[:access], access is read or write (default read), repeatable")
    sp.set_defaults(func=cmd_spawn)

    pp = sub.add_parser("post", help="send a message into an existing chat and queue its agent")
    pp.add_argument("--chat", required=True, help="target chat id")
    pp.add_argument("--prompt", help="the message; '-' reads stdin")
    pp.add_argument("--prompt-file", help="file with the message; '-' reads stdin")
    pp.add_argument("--author", default="agent", help="author shown in CO (default: agent)")
    pp.add_argument("--agent", choices=["claude", "codex", "dispatcher"], help="default: agent of the chat's last job")
    pp.add_argument("--model")
    pp.add_argument("--timeout-minutes", type=int, default=120, help="job time limit in minutes (default 120; 0 = the host default for the mode)")
    pp.add_argument("--host", type=host_id, help="executor id (default: the host of the chat's last job; see 'hosts')")
    pp.set_defaults(func=cmd_post)

    mp = sub.add_parser("models", help="model resources this chat may call (cards, slave allowed)")
    mp.add_argument("--parent", help="chat whose environment decides (default: from the current directory / AIWB_CHAT_ID)")
    mp.add_argument("--env", help="environment instead of a chat")
    mp.add_argument("--json", action="store_true")
    mp.set_defaults(func=cmd_models)

    hp = sub.add_parser("hosts", help="executors of the workbench (Freedom F12): id, kind, OS, whose, health")
    hp.add_argument("--env", help="the order and visibility of this environment (default: the owner's)")
    hp.add_argument("--json", action="store_true")
    hp.set_defaults(func=cmd_hosts)

    wp = sub.add_parser("wait", help="wait for subtasks and print their answers")
    wp.add_argument("job_ids", nargs="+")
    wp.add_argument("--timeout", type=int, default=1800)
    wp.set_defaults(func=cmd_wait)

    stp = sub.add_parser("status", help="current state of subtasks")
    stp.add_argument("job_ids", nargs="+")
    stp.set_defaults(func=cmd_status)

    args = parser.parse_args()
    if args.cmd in ("spawn", "post") and not (args.prompt or args.prompt_file):
        parser.error(f"{args.cmd} requires --prompt or --prompt-file")

    try:
        return args.func(args) or 0
    except Exception as e:
        print(f"error: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
