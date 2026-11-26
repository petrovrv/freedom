# SPDX-License-Identifier: AGPL-3.0-or-later
"""Modules of a Freedom install (stage F10, SPEC §3.6): check, sign, install, switch and remove them from the home PC.

A module is a folder with module.json (manifest: id, version, ext range, permissions, provides, server, personal,
dataPolicy), its parts (ui/, server/, ...) and freedom-manifest.json - the sha256 of every file signed with Ed25519, the
same format as release manifests (install/freedom_release.py). The server never checks a signature itself: this tool
does it at home, uploads the files over SSH to ~/co-agent/modules/<id>/ and registers the module with the sha256 of
each file (db.php op modules_host, host key aiwb/co-push); the server then includes only files that still match.

  python install/freedom_modules.py new ID --app [--title T] [--icon I] [--env ENV] [--dir PARENT]  an app from the template
  python install/freedom_modules.py check DIR                       manifest and files (no network)
  python install/freedom_modules.py sign DIR [--name maintainer-1 | --own]  freedom-manifest.json signed with keyring aiwb/freedom-release
  python install/freedom_modules.py verify DIR [--trust FILE]       signature: standard (install/maintainers.json) or a trusted author
  python install/freedom_modules.py plan DIR                        what install would do (read only: compares with the server)
  python install/freedom_modules.py install DIR [--enable] [--default-person MODE] [--person ENV=MODE ...] [--allow-untrusted]
  python install/freedom_modules.py list
  python install/freedom_modules.py sync [--dry-run]                tools and services at home follow the switches on the server
  python install/freedom_modules.py enable|disable ID [--no-service]
  python install/freedom_modules.py person ID ENV MODE              default of a member's switch (auto|ask|off); never over their own choice
  python install/freedom_modules.py remove ID --data keep|erase [--yes]
  install/enable/disable/remove --server-only: only the server (no tools, services or Bridge copy on this computer)
Server: variant A over SSH (install.json site.ssh; the owner's hz.py for the default install.json) and db.php with the
host key of keyring aiwb/co-push; variant B/C (freedom_docker.py) into the app container with docker exec and db.php
through the published port with the stack's own host key (<stack>/secrets/push_key).
Trust: standard modules are signed by the maintainers (install/maintainers.json, threshold); a module of another author
installs only when its key is in install.json modules.trustedKeys [{name, key}] or with --allow-untrusted, and it never
runs PHP on the server (no server/ part is uploaded for it). Own modules are signed with the owner's key (sign --own:
keyring aiwb/freedom-release, account install.json modules.ownKey, default "owner", whose public key is in trustedKeys);
verify names the level standard | own | trusted | untrusted. Before a module that is not standard is installed, review()
scans it (toolbox security scan when the library is there, plus the page rules below) and install stops on an error.
Apps made on the fly (F14c, SPEC §3.6a item 6): `new ID --app` writes an app module from the template into the owner's
module folder (install.json modules.dirs[0] or --dir): one ui.page app with its icon, the permission data.env (the
core keeps its data per person: ext/1 coExt.data, no server code) and manifest envs = [the env it is made for], so
only that person sees it; the core refuses its pages, data and actions to everybody else. Services of a module (provides.services windowsTask) are
started and stopped with enable/disable on Windows (kind "schedule" is only re-enabled, it runs at its own time).
Tools (provides.tools [{name, minVersion}]) go into the tool library with toolbox.py install --from (the module's
tools/ or the distribution's tools/) and toolbox.py module records them as owned by the module: a tool of a
switched-off module is refused by the tool server. home/ and tools/ of a module are never uploaded to the server.
Module folders at home: modules/ of this tree + install.json modules.dirs (private modules) + AIWB_MODULE_DIRS.
A service whose `run` is a .py file of the module (no tool, no home/ cwd) has its task re-pointed to
<module folder>/<run> when its task still runs another copy (code that moved into a module) and restarted.
Settings named by the manifest's installConfig {key: "install.json path"} go to the server with install (never secrets).
Bridge's module loader reads its own copy: install puts the checked module into the local modules folder
(AIWB_MODULES_DIR, else %LOCALAPPDATA%/AIWorkbench/modules/<id>), remove deletes it. While Bridge says it supervises
(<modules>/.bridge-supervisor.json younger than 5 min) it runs the daemons (tool + run) and the schedules without a
windowsTask itself, and enable/disable leave them to it.
"""
import argparse
import base64
import gzip
import json
import os
import pathlib
import posixpath
import re
import shutil
import os
import subprocess
import sys
import tempfile
import urllib.request
from html import escape as html_escape
from xml.sax.saxutils import escape as xml_escape

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import aiwb_install  # noqa: E402
import freedom_release as fr  # noqa: E402

EXT_VERSION = 1
ID_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,62}$")
OP_RE = re.compile(r"^[a-z][a-z0-9_]{2,40}$")
SLOTS = {"ui.header", "ui.page", "ui.panel", "ui.settings", "ui.composer", "ui.hint", "ui.chatPart"}
# ext/1 ui.chatPart: the parts of a chat folder a module page may fill (co-agent modules-lib.php CO_MODULE_CHAT_PARTS)
CHAT_PARTS = {"context", "correspondence", "events", "watchers"}
# permissions an ext/1 slot needs before CO Web feeds its page anything
SLOT_PERMISSION = {"ui.hint": "composer.read"}
SEARCH_HOOKS = {"expand", "chats", "items", "text"}   # ext/1 search.provider (web/agent/search-lib.php)
PERSON_MODES = ("auto", "ask", "off")
SKIP = {"desktop.ini", "README.md"}
TOOL_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,62}$")
SERVICE_KINDS = ("daemon", "schedule")
CRON_RE = re.compile(r"^\S+( \S+){4}$")
EVERY_RE = re.compile(r"^P(?=.*\d)(\d+D)?(T(\d+H)?(\d+M)?(\d+S)?)?$")
TASK_RE = re.compile(r"^\\AIWorkbench\\[A-Za-z0-9 _.-]{1,64}$")
SECRET_RE = re.compile(r"^aiwb/[a-z0-9][a-z0-9-]{0,62}(/[a-z0-9][a-z0-9._-]{0,62})?$")
HOME_ONLY = ("home/", "tools/")  # code of the home PC: signed with the module, never uploaded to the server
KEY_RE = re.compile(r"^[a-z][A-Za-z0-9]{2,30}$")      # mode and side-job keys (modules-lib.php CO_MODULE_KEY_RE)
PATH_RE = re.compile(r"^/[a-z0-9][a-z0-9-]{1,40}$")   # ui.page path (CO_MODULE_PATH_RE)
CONFIG_KEY_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,40}$")
USER_AGENT = "freedom-modules/1"
HOOK_EVENTS = {"job.before", "job.after"}
RUN_RE = re.compile(r"^[A-Za-z0-9 ._=:/,-]{1,200}$")   # split on spaces by Bridge, never through a shell
SUPERVISOR_FRESH_SEC = 300
ENV_RE = re.compile(r"^[a-z0-9][a-z0-9_-]{0,63}$")
# a module of some environments (manifest envs) may not bring parts that work for the whole house: they have no notion
# of a person yet (home services, tools of the library, device tools, notify/search hooks, import, side jobs)
ENV_MODULE_FORBIDDEN = ("tools", "services", "deviceTools", "deviceOps", "notify", "search", "hooks", "gateway", "import",
                        "sideJobs", "context", "modes")
# page code of a module that is not standard: what a reviewer must see before the owner says yes (CSP blocks most of it
# anyway: connect-src 'none', no same-origin)
PAGE_RULES = (
    ("network", re.compile(r"\b(?:fetch|XMLHttpRequest|WebSocket|EventSource|sendBeacon|importScripts)\b")),
    ("remote-code", re.compile(r"<script[^>]+src\s*=\s*['\"]?(?:https?:)?//|\bimport\s*\(\s*['\"`](?:https?:)?//", re.I)),
    ("dynamic-code", re.compile(r"(?<![.\w])eval\s*\(|\bnew\s+Function\s*\(|setTimeout\s*\(\s*['\"`]")),
    ("external-url", re.compile(r"\b(?:https?|wss?)://(?!localhost\b)[A-Za-z0-9.-]+")),
    ("storage-escape", re.compile(r"\b(?:document\.cookie|localStorage|sessionStorage|indexedDB)\b")),
)


class Fail(Exception):
    pass


def load_manifest(root: pathlib.Path) -> dict:
    f = root / "module.json"
    if not f.is_file():
        raise Fail(f"{root}: no module.json")
    try:
        return json.loads(f.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        raise Fail(f"module.json: {e}")


def ext_ok(rng: str, have: int = EXT_VERSION) -> bool:
    rng = (rng or "").strip()
    if not rng:
        return have == 1
    if rng.isdigit():
        return int(rng) == have
    for part in rng.split():
        m = re.fullmatch(r"(>=|<=|>|<|=)?(\d+)", part)
        if not m:
            return False
        n, op = int(m.group(2)), m.group(1) or "="
        if not {">=": have >= n, "<=": have <= n, ">": have > n, "<": have < n, "=": have == n}[op]:
            return False
    return True


def check(root: pathlib.Path) -> list:
    """Problems of a module folder (empty = fine)."""
    p = []
    m = load_manifest(root)
    if m.get("schema") != 1:
        p.append("schema must be 1")
    if not ID_RE.match(str(m.get("id", ""))):
        p.append("id: [a-z0-9-], up to 63 characters")
    if not re.fullmatch(r"[0-9][0-9A-Za-z.+-]{0,31}", str(m.get("version", ""))):
        p.append("version missing or malformed")
    if not ext_ok(str(m.get("ext", ""))):
        p.append(f"ext {m.get('ext')!r} does not include ext/{EXT_VERSION}")
    if not isinstance(m.get("permissions", []), list):
        p.append("permissions must be a list")
    prov = m.get("provides") or {}
    for u in prov.get("ui", []):
        if u.get("slot") not in SLOTS:
            p.append(f"ui slot {u.get('slot')!r} unknown")
        e = str(u.get("entry", ""))
        if not re.fullmatch(r"ui/[A-Za-z0-9._/-]+\.html", e) or ".." in e or not (root / e).is_file():
            p.append(f"ui entry {e!r} missing or outside ui/")
        if "path" in u and (u.get("slot") != "ui.page" or not PATH_RE.match(str(u["path"]))):
            p.append(f"ui path {u['path']!r}: only for ui.page, /[a-z0-9-]")
        if ("window" in u or "windowKey" in u) and (u.get("slot") != "ui.page" or u.get("window") not in (True, False)
                                                   or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]{0,30}", str(u.get("windowKey", "x")))):
            p.append(f"ui window of {u.get('id')!r}: only for ui.page, window true/false, windowKey a query parameter name")
        if (u.get("slot") == "ui.chatPart") != ("part" in u) or ("part" in u and u.get("part") not in CHAT_PARTS):
            p.append(f"ui part of {u.get('id')!r}: ui.chatPart needs part, one of {sorted(CHAT_PARTS)}; other slots have none")
        need = SLOT_PERMISSION.get(u.get("slot"))
        if need and need not in (m.get("permissions") or []):
            p.append(f"ui slot {u.get('slot')} needs the permission {need}")
    for x in prov.get("import", []) or []:
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,31}", str((x or {}).get("source", ""))):
            p.append(f"import source {(x or {}).get('source')!r}: [a-z0-9-], up to 32 characters")
    if prov.get("import") and "import.write" not in (m.get("permissions") or []):
        p.append("provides.import needs the permission import.write")
    for lo in m.get("localOnly", []) or []:
        if not re.fullmatch(r"[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*/?", str(lo)) or ".." in str(lo) \
                or str(lo).startswith(("ui/", "server/")) or str(lo) in ("ui", "server", "module.json"):
            p.append(f"localOnly {lo!r}: a file or folder of the module other than ui/, server/, module.json")
    for g in m.get("generated", []) or []:
        why = generated_problem(g, m)
        if why:
            p.append(f"generated {g!r}: {why}")
    # ext/1 device.tools: tools a device (CO Desktop, CO Resident) registers through its risk classes
    if (prov.get("deviceTools") or prov.get("deviceOps") or m.get("device")) and "device.tools" not in (m.get("permissions") or []):
        p.append("deviceTools/deviceOps/device need the permission device.tools")
    names = set()
    for t in prov.get("deviceTools", []) or []:
        n = str((t or {}).get("name", ""))
        if not re.fullmatch(r"[a-z][a-z0-9_]{2,63}", n):
            p.append(f"deviceTools name {n!r}: [a-z][a-z0-9_]{{2,63}}")
        elif n in names:
            p.append(f"deviceTools name {n!r} twice")
        names.add(n)
        # "risk" is another name of "class"; CO Desktop / CO Resident load only tools with a core class (R W X N E D),
        # another kind of device (the browser extension) may use its own word; a script tool always needs a core class
        cls = str((t or {}).get("class", (t or {}).get("risk", "")))
        if cls not in ("R", "W", "X", "N", "E", "D") and ((t or {}).get("run") or not re.fullmatch(r"[a-z]{2,20}", cls)):
            p.append(f"deviceTools {n!r}: class must be one of R W X N E D")
        run = str((t or {}).get("run", ""))
        if run and (not re.fullmatch(r"device/[A-Za-z0-9._/-]+", run) or ".." in run or not (root / run).is_file()):
            p.append(f"deviceTools {n!r}: run {run!r} missing or outside device/")
    for o in prov.get("deviceOps", []) or []:
        if not OP_RE.match(str(o)):
            p.append(f"deviceOps {o!r}: [a-z][a-z0-9_]{{2,40}}")
    dev = m.get("device") or {}
    if dev.get("default", "on") not in ("on", "off"):
        p.append("device.default: on|off")
    ops_run = str(dev.get("ops", ""))
    if ops_run and (not re.fullmatch(r"device/[A-Za-z0-9._/-]+", ops_run) or ".." in ops_run or not (root / ops_run).is_file()):
        p.append(f"device.ops {ops_run!r} missing or outside device/")
    p += check_ext1(m, root)
    srv = m.get("server")
    if srv:
        entry = str(srv.get("entry", ""))
        if not re.fullmatch(r"server/[A-Za-z0-9._/-]+\.php", entry) or not (root / entry).is_file():
            p.append(f"server entry {entry!r} missing")
        for via, ops in (srv.get("ops") or {}).items():
            if via not in ("user", "push", "host"):
                p.append(f"server.ops.{via}: channel unknown")
            for o in ops or []:
                if not OP_RE.match(str(o)):
                    p.append(f"op {o!r}: [a-z][a-z0-9_]{{2,40}}")
        for r in srv.get("routes", []) or []:
            if not re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", str(r)):
                p.append(f"route {r!r} malformed")
        for u in srv.get("uploads", []) or []:
            if not re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", str(u)):
                p.append(f"upload {u!r} malformed")
    for sp in prov.get("search", []) or []:
        if not isinstance(sp, dict) or not re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", str(sp.get("id", ""))) or sp.get("id") in ("messages", "protocol"):
            p.append(f"search provider {sp!r}: id [a-z][a-z0-9_-], not messages/protocol")
        elif not set(sp.get("hooks") or []) or not set(sp.get("hooks") or []) <= SEARCH_HOOKS:
            p.append(f"search provider {sp.get('id')}: hooks from {sorted(SEARCH_HOOKS)}")
        elif not srv:
            p.append(f"search provider {sp.get('id')}: needs server.entry")
    envs = m.get("envs")
    if envs is not None:
        if not isinstance(envs, list) or not envs or not all(isinstance(e, str) and ENV_RE.match(e) for e in envs):
            p.append("envs: a non-empty list of environment ids")
        for k in ENV_MODULE_FORBIDDEN:
            if prov.get(k):
                p.append(f"provides.{k}: not for a module of some environments (envs) - it would work for the whole house")
        if m.get("device") or m.get("server"):
            p.append("device/server parts: not for a module of some environments (envs)")
    pers = m.get("personal")
    if pers is not None and pers.get("default", "ask") not in PERSON_MODES:
        p.append("personal.default: auto|ask|off")
    if (m.get("dataPolicy") or {}).get("migrations", "additive") != "additive":
        p.append("dataPolicy.migrations must be additive (SPEC §11.22)")
    p += check_home(root, prov)
    p += check_bridge(root, m, prov)
    for f in root.rglob("*"):
        rel = f.relative_to(root).as_posix()
        if f.is_file() and not re.fullmatch(r"[A-Za-z0-9._/-]+", rel):
            p.append(f"file name {rel!r}: only [A-Za-z0-9._/-]")
    return p


def tool_entries(m: dict) -> list:
    """provides.tools as [{'name', 'minVersion'}] (a plain string is a name)."""
    out = []
    for t in (m.get("provides") or {}).get("tools", []) or []:
        t = {"name": t} if isinstance(t, str) else (t if isinstance(t, dict) else {})
        out.append({"name": str(t.get("name", "")), "minVersion": str(t.get("minVersion", "") or "")})
    return out


def check_home(root: pathlib.Path, prov: dict) -> list:
    """Parts that live on the home PC: provides.tools (tool library), provides.services (daemons and schedules that
    Bridge supervises; windowsTask until its module loader exists), provides.secrets (keyring names only)."""
    p = []
    tools = set()
    for t in tool_entries({"provides": prov}):
        if not TOOL_RE.match(t["name"]):
            p.append(f"tool {t['name']!r}: [a-z0-9-]")
            continue
        tools.add(t["name"])
        if t["minVersion"] and not re.fullmatch(r"\d+(\.\d+){0,2}", t["minVersion"]):
            p.append(f"tool {t['name']}: minVersion {t['minVersion']!r} malformed")
        shipped = root / "tools" / t["name"]
        if shipped.exists() and not (shipped / "SKILL.md").is_file():
            p.append(f"tools/{t['name']} has no SKILL.md")
    ids = set()
    for s in prov.get("services", []) or []:
        s = s if isinstance(s, dict) else {}
        sid, kind = str(s.get("id", "")), s.get("kind", "daemon")
        if not ID_RE.match(sid) or sid in ids:
            p.append(f"service id {sid!r} malformed or twice")
        ids.add(sid)
        if kind not in SERVICE_KINDS:
            p.append(f"service {sid}: kind must be daemon|schedule")
        if kind == "schedule" and not CRON_RE.match(str(s.get("cron", ""))) and not EVERY_RE.match(str(s.get("every", ""))):
            p.append(f"service {sid}: a schedule needs cron (5 fields, as Bridge schedules) or every (PT1H, PT15M, P1D)")
        if s.get("tool") and s["tool"] not in tools:
            p.append(f"service {sid}: tool {s['tool']!r} is not in provides.tools")
        cwd = str(s.get("cwd", ""))
        if cwd and (not re.fullmatch(r"home(/[A-Za-z0-9._-]+)*", cwd) or ".." in cwd or not (root / cwd).is_dir()):
            p.append(f"service {sid}: cwd {cwd!r} missing or outside home/")
        if not s.get("tool") and not cwd and not s.get("windowsTask"):
            p.append(f"service {sid}: needs tool, cwd (home/ code) or windowsTask")
        if not str(s.get("run", "")).strip() and not s.get("windowsTask"):
            p.append(f"service {sid}: run missing")
        if s.get("windowsTask") and not TASK_RE.match(str(s["windowsTask"])):
            p.append(f"service {sid}: windowsTask must be under \\AIWorkbench\\")
    for sec in prov.get("secrets", []) or []:
        name = str((sec or {}).get("name", "")) if isinstance(sec, dict) else str(sec)
        if not SECRET_RE.match(name):
            p.append(f"secret {name!r}: keyring service aiwb/<name>[/<part>] (names only, never values)")
    return p


def check_ext1(m: dict, root: pathlib.Path) -> list:
    """Light type checks of the ext/1 declarations (modules/EXT1.md); the server reads them defensively anyway."""
    p = []
    prov = m.get("provides") or {}
    pages = {u.get("id") for u in prov.get("ui", []) if isinstance(u, dict) and u.get("slot") == "ui.page"}
    is_str = lambda v: isinstance(v, str) and v != ""  # noqa: E731
    kinds = ("modes", "fileActions", "messageViews", "sideJobs", "navigate", "services")
    for key in kinds + ("context",):
        if key in prov and not isinstance(prov[key], list):
            p.append(f"provides.{key} must be a list")
    lists = {k: (prov.get(k) if isinstance(prov.get(k), list) else []) for k in kinds}
    for x in lists["modes"]:
        if not isinstance(x, dict) or not KEY_RE.match(str(x.get("key", ""))) or not is_str(x.get("label")):
            p.append(f"mode {x!r}: key [a-z][A-Za-z0-9] and label needed")
        elif not (root / str(x.get("rules", ""))).is_file():
            p.append(f"mode {x['key']}: rules file {x.get('rules')!r} missing")
    for x in lists["fileActions"]:
        o = x.get("open") if isinstance(x, dict) else None
        if not isinstance(x, dict) or not is_str(x.get("label")) or not isinstance(x.get("exts"), list) or not x["exts"] \
                or not all(isinstance(e, str) and re.fullmatch(r"[a-z0-9]{1,10}", e) for e in x["exts"]):
            p.append(f"fileAction {x!r}: label and exts [a-z0-9] needed")
        elif not isinstance(o, dict) or not (("page" in o and o["page"] in pages) or is_str(o.get("mode"))):
            p.append(f"fileAction {x.get('id', '')!r}: open.page must be a ui.page id of the module (or open.mode)")
        elif o.get("target", "self") not in ("self", "blank"):
            p.append(f"fileAction {x.get('id', '')!r}: open.target self|blank")
    for x in lists["messageViews"]:
        if not isinstance(x, dict) or not is_str(x.get("match")):
            p.append(f"messageView {x!r}: match (regex) needed")
            continue
        try:
            re.compile(x["match"])
        except re.error as e:
            p.append(f"messageView match {x['match']!r}: {e}")
    for x in lists["sideJobs"]:
        if not isinstance(x, dict) or not KEY_RE.match(str(x.get("key", ""))):
            p.append(f"sideJob {x!r}: key [a-z][A-Za-z0-9]{{2,30}} needed")
        elif any(k in x and not isinstance(x[k], str) for k in ("label", "list", "item", "count", "countNoun")):
            p.append(f"sideJob {x['key']}: label/list/item/count/countNoun are strings")
    cfg = m.get("installConfig", {})
    if not isinstance(cfg, dict) or not all(CONFIG_KEY_RE.match(str(k)) and is_str(v) for k, v in cfg.items()):
        p.append('installConfig: {key: "install.json path"}')
        cfg = {}
    for x in lists["navigate"]:
        if isinstance(x, dict):
            if x.get("config") not in cfg:
                p.append(f"navigate {x!r}: config must be a key of installConfig")
        elif not (isinstance(x, str) and (re.fullmatch(r"https://[A-Za-z0-9.-]+(:\d+)?", x) or re.fullmatch(r"/[A-Za-z0-9._/-]*", x))):
            p.append(f"navigate {x!r}: an https origin, a /path of the site or {{config: key}}")
    for x in lists["services"]:
        if not isinstance(x, dict):
            p.append(f"service {x!r} must be an object")
        elif str(x.get("run", "")).endswith(".py") and "tool" not in x and "cwd" not in x:
            # a script of the module itself (its Windows task is re-pointed to it at start)
            r = str(x["run"])
            if not re.fullmatch(r"[A-Za-z0-9._/-]+\.py", r) or ".." in r or not (root / r).is_file():
                p.append(f"service {x.get('id', '')!r}: run {r!r} must be a .py file of the module")
    # F14b-1 (SPEC §3.6a item 5): actions of the app for the agent - user ops of the module itself, described for the
    # agent, with a flat argument schema and outward true|false (outward: only the person's click runs it)
    acts = prov.get("actions", [])
    if not isinstance(acts, list):
        p.append("provides.actions must be a list")
        acts = []
    user_ops = ((m.get("server") or {}).get("ops") or {}).get("user") or []
    seen = set()
    for x in acts:
        op = str((x or {}).get("op", "")) if isinstance(x, dict) else ""
        if not isinstance(x, dict) or op not in user_ops:
            p.append(f"action {x!r}: op must be one of server.ops.user")
            continue
        if op in seen:
            p.append(f"action {op}: declared twice")
        seen.add(op)
        d = x.get("description")
        if not is_str(d) or len(d) > 500:
            p.append(f"action {op}: description for the agent needed (up to 500 characters)")
        if not isinstance(x.get("outward"), bool):
            p.append(f"action {op}: outward true|false needed (true = acts outside the workbench, only a person runs it)")
        a = x.get("args", {"type": "object", "properties": {}})
        props = a.get("properties", {}) if isinstance(a, dict) else None
        if not isinstance(a, dict) or a.get("type", "object") != "object" or not isinstance(props, dict):
            p.append(f"action {op}: args must be an object schema with properties")
            continue
        for k, v in props.items():
            if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,40}", k) or not isinstance(v, dict)                     or v.get("type") not in ("string", "integer", "number", "boolean"):
                p.append(f"action {op}: argument {k!r} needs type string|integer|number|boolean")
            elif "enum" in v and not isinstance(v["enum"], list):
                p.append(f"action {op}: argument {k} enum must be a list")
        req = a.get("required", [])
        if not isinstance(req, list) or any(r not in props for r in req):
            p.append(f"action {op}: required must list arguments of properties")
    return p


def install_config(m: dict) -> dict:
    """Values of the manifest's installConfig {key: "install.json path"} from this install (strings only)."""
    out = {}
    cfg = m.get("installConfig")
    for k, path in (cfg.items() if isinstance(cfg, dict) else []):
        v = aiwb_install.get(str(path))
        if isinstance(v, str) and v:
            out[k] = v
    return out




def is_local(rel: str, local_only) -> bool:
    """A file the manifest keeps at home (localOnly: "dir/" or a file): signed with the module, never uploaded."""
    for lo in local_only or []:
        lo = str(lo)
        if (lo.endswith("/") and rel.startswith(lo)) or rel == lo:
            return True
    return False


CODE_SUFFIXES = (".py", ".pyc", ".js", ".mjs", ".cjs", ".php", ".ps1", ".psm1", ".sh", ".cmd", ".bat", ".cs", ".dll", ".exe",
                 ".html", ".htm", ".wasm", ".vbs", ".jar")


def generated_problem(entry: str, m: dict) -> str:
    """Why a "generated" entry of a manifest is not allowed ('' = fine). Generated files are not signed, so they may never
    be code the installer uploads or runs: nothing under ui/, server/, home/ or tools/, and a code file only inside a
    localOnly folder (e.g. the site config of a browser extension the person loads unpacked)."""
    e = str(entry)
    if not re.fullmatch(r"[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*/?", e) or ".." in e:
        return "malformed"
    low = e.lower()
    if low.startswith(("ui/", "server/", "home/", "tools/")) or low.rstrip("/") in ("ui", "server", "home", "tools", "module.json"):
        return "inside ui/, server/, home/ or tools/"
    if e.endswith("/") and in_local_folder(e, m):
        return "a generated folder inside a localOnly folder would leave its code unsigned (name the files)"
    if low.endswith(CODE_SUFFIXES) and not in_local_folder(e, m):
        return "a generated code file must be inside a localOnly folder"
    return ""


def in_local_folder(rel: str, m: dict) -> bool:
    return any(str(lo).endswith("/") and rel.startswith(str(lo)) for lo in m.get("localOnly") or [])


def is_generated(rel: str, m: dict) -> bool:
    """A file made per install or per build (manifest "generated", e.g. a site config or a dist/ folder): neither signed
    nor uploaded, so it never breaks the signature of a module folder. Only entries generated_problem() accepts count,
    also when the folder is verified (a manifest signed by mistake with a wider entry still verifies strictly)."""
    ok = [str(g) for g in m.get("generated") or [] if not generated_problem(g, m)]
    if rel.lower().endswith(CODE_SUFFIXES):
        # a code file only when named exactly, inside a localOnly folder (a generated folder never covers code)
        return rel in ok and in_local_folder(rel, m)
    return is_local(rel, ok)


def git_ignored(root: pathlib.Path, rels: list) -> list:
    """Files of rels that git ignores in this checkout (they would be signed but are in no other checkout); [] without git."""
    if not rels:
        return []
    try:
        # NUL-separated: text mode would turn the newlines into CRLF on Windows
        r = subprocess.run(["git", "-C", str(root), "check-ignore", "--stdin", "-z", "--no-index"],
                           input="\0".join(rels).encode("utf-8"), capture_output=True, timeout=60)
    except (OSError, subprocess.SubprocessError):
        return []
    return [x for x in r.stdout.decode("utf-8", "replace").split("\0") if x] if r.returncode in (0, 1) else []


def signed_files(root: pathlib.Path, man: dict) -> dict:
    """The release manifest of a module folder without generated files."""
    m = load_manifest(root)
    man["files"] = [f for f in man["files"] if not is_generated(str(f.get("path", "")), m)]
    return man


def verify_folder(root: pathlib.Path, man: dict, trust: dict) -> list:
    """fr.verify strict (byte-code caches removed first, then any unlisted file refused), except for the manifest's
    generated files that may lie in the folder."""
    m = load_manifest(root)
    fr.purge_bytecode(root)
    p = fr.verify(root, man, trust, strict=False)
    listed = {str(f.get("path", "")) for f in man.get("files", [])}
    for f in fr.list_files(root):
        rel = f.relative_to(root).as_posix()
        if rel not in listed and not is_generated(rel, m):
            p.append(f"not in manifest {rel}")
    return p


def check_bridge(root: pathlib.Path, m: dict, prov: dict) -> list:
    """Points Bridge's module loader serves (device tool classes: check of device.tools above): job hooks, context fragments, models, the
    model gateway, and the warning of a confidential module."""
    p = []
    perms = m.get("permissions") or []
    for sv in prov.get("services", []) or []:
        if isinstance(sv, dict) and sv.get("run") and not RUN_RE.match(str(sv["run"])):
            p.append(f"service {sv.get('id')!r}: run must be plain words (no shell)")
    for h in prov.get("hooks", []) or []:
        if h.get("event") not in HOOK_EVENTS:
            p.append(f"hook event {h.get('event')!r}: job.before|job.after")
        if not TOOL_RE.match(str(h.get("tool", ""))) or not RUN_RE.match(str(h.get("run", ""))):
            p.append(f"hook {h.get('event')!r}: tool [a-z0-9-] and run (plain words) are needed")
    if (prov.get("hooks") or []) and not ({"context", "push.send"} & set(perms)):
        p.append("hooks need the permission context or push.send")
    for c in prov.get("context", []) or []:
        f = str(c.get("file", ""))
        if f and (not re.fullmatch(r"[A-Za-z0-9._/-]+\.md", f) or ".." in f or not (root / f).is_file()):
            p.append(f"context file {f!r} missing or not .md inside the module")
    if (prov.get("context") or []) and "context" not in perms:
        p.append("context fragments need the permission context")
    for md in prov.get("models", []) or []:
        if md.get("kind") not in ("llm", "dispatcher") and not md.get("agent"):
            p.append(f"model {md.get('id')!r}: kind llm|dispatcher (gateway) or agent (a core agent)")
    gw = prov.get("gateway")
    if gw is not None:
        if not isinstance(gw, dict) or not str(gw.get("title", "")):
            p.append("gateway: title is needed")
        if not any(t.get("kind") == "llm" for t in prov.get("deviceTools", []) or []):
            p.append("gateway: a device tool of kind llm is needed")
    if m.get("confidential") and not str(m.get("warning", "")):
        p.append("a confidential module needs the warning shown when it is enabled")
    return p


# ---------- home PC: the copy Bridge's module loader reads, its supervisor ----------

def local_root() -> pathlib.Path:
    """Local modules folder of Bridge's module loader (same rule as Bridge: AIWB_MODULES_DIR, else LocalApplicationData)."""
    env = os.environ.get("AIWB_MODULES_DIR")
    if env:
        return pathlib.Path(env)
    base = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), ".local", "share")
    return pathlib.Path(base) / "AIWorkbench" / "modules"


def install_local(root: pathlib.Path, mid: str) -> pathlib.Path:
    """Copies the checked module folder to <local>/<id> via <id>.new (service logs live in <local>/.logs, untouched)."""
    base = local_root()
    base.mkdir(parents=True, exist_ok=True)
    dst, new = base / mid, base / f"{mid}.new"
    shutil.rmtree(new, ignore_errors=True)
    shutil.copytree(root, new, ignore=shutil.ignore_patterns("desktop.ini", "__pycache__", "*.pyc"))
    shutil.rmtree(dst, ignore_errors=True)
    new.rename(dst)
    return dst


def remove_local(mid: str) -> bool:
    d = local_root() / mid
    if not d.is_dir():
        return False
    shutil.rmtree(d, ignore_errors=True)
    return True


def bridge_supervises() -> bool:
    """Bridge's module loader runs (its heartbeat file is fresh): it starts and stops its services itself."""
    import datetime
    try:
        utc = json.loads((local_root() / ".bridge-supervisor.json").read_text(encoding="utf-8-sig"))["utc"]
        t = datetime.datetime.strptime(utc[:19], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=datetime.timezone.utc)
        return abs((datetime.datetime.now(datetime.timezone.utc) - t).total_seconds()) < SUPERVISOR_FRESH_SEC
    except Exception:
        return False


def bridge_runs(s: dict) -> bool:
    """Services Bridge's loader runs: daemons with tool + run, schedules without a windowsTask (tool or cwd + run)."""
    if s.get("kind", "daemon") == "schedule":
        return not s.get("windowsTask") and bool(s.get("run"))
    return bool(s.get("tool") and s.get("run"))


def module_files(root: pathlib.Path, with_server: bool = True) -> dict:
    """rel -> sha256 of the files the server gets (no README, no signature file; no server/ for untrusted code; never
    home/ or tools/, which run on the home PC only, nor the manifest's localOnly and generated files)."""
    out = {}
    m = load_manifest(root)
    for f in fr.list_files(root):
        rel = f.relative_to(root).as_posix()
        if f.name in SKIP or rel == "module.json" or rel.startswith(HOME_ONLY) or is_local(rel, m.get("localOnly")) or is_generated(rel, m):
            continue
        if rel.startswith("server/") and not with_server:
            continue
        out[rel] = fr.sha256_file(f)
    return out


def trusted_authors() -> dict:
    keys = {}
    for k in (aiwb_install.get("modules.trustedKeys") or []):
        if isinstance(k, dict) and re.fullmatch(r"[0-9a-fA-F]{64}", str(k.get("key", ""))):
            keys[k["key"].lower()] = str(k.get("name", "author"))
    return {"keys": keys, "threshold": 1}


def own_key_name() -> str:
    """Account of the owner's own signing key in keyring aiwb/freedom-release (install.json modules.ownKey, default owner)."""
    n = aiwb_install.get("modules.ownKey")
    return n if isinstance(n, str) and re.fullmatch(r"[A-Za-z0-9._-]{1,40}", n) else "owner"


def verify(root: pathlib.Path, trust_file: pathlib.Path = fr.DEFAULT_TRUST) -> dict:
    """{'standard': bool, 'trusted': bool, 'level': standard|own|trusted|untrusted, 'publisher': str, 'problems': [...]}
    for the signature of a module folder. own = signed with the owner's key (the trustedKeys entry named own_key_name())."""
    mf = root / fr.MANIFEST_NAME
    if not mf.is_file():
        return {"standard": False, "trusted": False, "level": "untrusted", "publisher": "",
                "problems": ["not signed (no freedom-manifest.json)"]}
    man = json.loads(mf.read_text(encoding="utf-8"))
    std = verify_folder(root, man, fr.load_trust(trust_file))
    if not std:
        names = [s.get("name", "") for s in man.get("signatures", [])]
        return {"standard": True, "trusted": True, "level": "standard", "publisher": ",".join(names), "problems": []}
    auth = trusted_authors()
    own = verify_folder(root, man, auth) if auth["keys"] else ["no trusted authors in install.json modules.trustedKeys"]
    if not own:
        k = next(s["key"] for s in man.get("signatures", []) if s.get("key", "").lower() in auth["keys"])
        name = auth["keys"][k.lower()]
        return {"standard": False, "trusted": True, "level": "own" if name == own_key_name() else "trusted",
                "publisher": name, "problems": []}
    return {"standard": False, "trusted": False, "level": "untrusted", "publisher": "", "problems": std}


def review(root: pathlib.Path) -> dict:
    """Check before installing a module that is not standard (SPEC §3.6): the toolbox security scan of its code (secrets,
    hosts, remote exec, writes; skipped with a note when the tool library is not here) and the page rules (network,
    remote or dynamic code, external addresses, browser storage) on ui/. {'errors': [...], 'warnings': [...], 'notes'}."""
    errors, warnings, notes = [], [], []
    try:
        sys.path.insert(0, str(tools_root() / "toolbox"))
        import security  # noqa: PLC0415 - toolbox 1.5.0+ (stdlib only); the rules of scan_tool without SKILL.md exceptions

        def add(rel, line, rule, detail, snippet, host=None):
            if rule == "host-not-allowed" and security.host_allowed(host or detail, []):
                return
            msg = f"{rule} {rel}:{line} {detail}"
            dst = errors if security.RULES[rule][0] == security.ERROR else warnings
            if msg not in dst:
                dst.append(msg)

        for f in sorted(root.rglob("*")):
            if not f.is_file() or f.suffix.lower() not in security.CODE_EXT or "__pycache__" in f.parts:
                continue
            rel = f.relative_to(root).as_posix()
            if f.suffix.lower() == ".py":
                security.scan_python(f, rel, add)
            else:
                security.regex_lines(f.read_text(encoding="utf-8", errors="replace"), rel, add)
    except Exception as e:  # no library on this computer: the page rules still run
        notes.append(f"toolbox security scan skipped ({type(e).__name__}: {e})")
    finally:
        if sys.path and sys.path[0] == str(tools_root() / "toolbox"):
            sys.path.pop(0)
    m = load_manifest(root)
    nav = bool((m.get("provides") or {}).get("navigate"))
    for f in sorted((root / "ui").rglob("*")) if (root / "ui").is_dir() else []:
        if not f.is_file() or f.suffix.lower() not in (".js", ".mjs", ".html", ".htm", ".css"):
            continue
        rel = f.relative_to(root).as_posix()
        for n, line in enumerate(f.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            for rule, rx in PAGE_RULES:
                hit = rx.search(line)
                if not hit:
                    continue
                msg = f"{rule} {rel}:{n} {hit.group(0)[:80]}"
                # an address the manifest lets the page navigate to is declared; anything else is an error
                (warnings if rule == "external-url" and nav else errors).append(msg)
    return {"errors": errors, "warnings": warnings, "notes": notes}


# ---------- apps made on the fly (F14c) ----------

APP_TEMPLATE = {
    "ui/main.html": """<!doctype html>
<html lang="{lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{title}</title><link rel="stylesheet" href="?m={id}&amp;f=ui/main.css"></head>
<body><main>
<header><span class="icon" aria-hidden="true">{icon}</span><h1>{title}</h1></header>
<form id="add"><input id="text" maxlength="300" autocomplete="off" placeholder="{placeholder}" aria-label="{placeholder}" disabled><button disabled>+</button></form>
<ul id="items"></ul>
<p id="empty" class="muted">{empty}</p>
<p id="status" class="muted" role="status"></p>
</main><script src="/ext/ext1.js"></script><script src="?m={id}&amp;f=ui/main.js"></script></body></html>
""",
    "ui/main.css": """/* Made from the Freedom app template (freedom_modules.py new --app). The core passes its theme as CSS variables. */
:root{{font:15px system-ui,sans-serif;color:var(--fg,#1d2733);background:var(--bg,#f6f7f9)}}
*{{box-sizing:border-box}}body{{margin:0}}main{{max-width:640px;margin:auto;padding:24px 16px}}
header{{display:flex;align-items:center;gap:10px}}h1{{font-size:24px;margin:0}}.icon{{font-size:26px}}
form{{display:flex;gap:8px;margin:18px 0}}input{{flex:1;min-width:0;font:inherit;padding:10px 12px;border:1px solid var(--line,#d5dbe3);border-radius:10px;background:var(--panel,#fff);color:inherit}}
button{{font:inherit;cursor:pointer;border:0;border-radius:10px;padding:8px 14px;background:var(--accent,#2b6cb0);color:var(--accent-fg,#fff)}}
ul{{list-style:none;margin:0;padding:0}}li{{display:flex;align-items:center;gap:10px;padding:10px 4px;border-bottom:1px solid var(--line,#e3e7ec)}}
li span{{flex:1;overflow-wrap:anywhere}}li.done span{{text-decoration:line-through;color:var(--muted,#6b7785)}}
li input{{flex:none;width:18px;height:18px}}li button{{background:none;color:var(--muted,#6b7785);padding:4px 8px}}.muted{{color:var(--muted,#6b7785);font-size:13px}}
""",
    "ui/main.js": """// Made from the Freedom app template (freedom_modules.py new --app): a list kept by the core for the signed-in person
// (ext/1 coExt.data, permission data.env; one key "items" with a JSON array). No network, no other storage.
(() => {{
'use strict';
const $ = (id) => document.getElementById(id);
const KEY = 'items';
let items = [];
let saving = Promise.resolve();

function render() {{
  const ul = $('items');
  ul.textContent = '';
  for (const it of items) {{
    const li = document.createElement('li');
    if (it.done) li.className = 'done';
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = !!it.done;
    box.disabled = !loaded;
    box.onchange = () => {{ if (!loaded) return; it.done = box.checked; save(); }};
    const text = document.createElement('span');
    text.textContent = it.text;
    const del = document.createElement('button');
    del.type = 'button';
    del.textContent = '×';
    del.title = 'Delete';
    del.disabled = !loaded;
    del.onclick = () => {{ if (!loaded) return; items = items.filter((x) => x !== it); save(); }};
    li.append(box, text, del);
    ul.append(li);
  }}
  $('empty').hidden = items.length > 0;
}}

// editing waits for a good load: a change made before it (or after a failed one) would overwrite the saved list
let loaded = false;
let gen = 0;   // the newest reload: an older one that finishes first leaves editing off
function editable(on) {{
  loaded = on;
  $('text').disabled = !on;
  $('add').querySelector('button').disabled = !on;
  render();
}}

async function load(n) {{
  const r = await coExt.data.get(KEY);
  if (n !== gen) return;
  if (!r || !r.ok) throw new Error((r && (r.message || r.error)) || 'no answer');
  const v = r.value ? JSON.parse(r.value) : [];   // data it cannot read is an error, never overwritten
  if (!Array.isArray(v)) throw new Error('unexpected data');
  items = v.filter((x) => x && typeof x.text === 'string');
  editable(true);
  $('status').textContent = '';
}}

// (re)load after the writes already queued, never in between
function reload() {{
  const n = ++gen;
  editable(false);
  saving = saving.then(() => load(n)).catch((e) => {{ $('status').textContent = 'Not loaded: ' + e.message; }});
  return saving;
}}

function save() {{
  if (!loaded) return;
  render();
  const value = JSON.stringify(items);
  saving = saving.then(() => coExt.data.set(KEY, value)).then((r) => {{
    $('status').textContent = r && r.ok ? '' : 'Not saved: ' + ((r && (r.message || r.error)) || 'error');
  }}, (e) => {{ $('status').textContent = 'Not saved: ' + e.message; }});
}}

$('add').onsubmit = (e) => {{
  e.preventDefault();
  if (!loaded) return;
  const t = $('text').value.trim();
  if (!t) return;
  items.push({{ text: t.slice(0, 300), done: false }});
  $('text').value = '';
  save();
}};

coExt.ready.then(() => {{
  coExt.title({title_js});
  // an agent changed the data (app_action data_set): read it again
  coExt.on('refresh', () => {{ reload(); }});
  return reload();
}}).catch((e) => {{ $('status').textContent = e.message; }});
}})();
""",
    "README.md": """# {title}

An app made on the fly (Freedom F14c, SPEC §3.6a item 6) from the template of `install/freedom_modules.py new --app`.
It is for the environment `{env}` only (manifest `envs`): nobody else sees its tile, pages or data.

- Data: kept by the core for the person (ext/1 `coExt.data`, permission `data.env`; up to 64 KB a value, 200 keys,
  1 MB). The agent reads and writes it with the core actions `data_list` / `data_get` / `data_set` / `data_delete`
  (MCP `app_list` / `app_action`). Switching the app off keeps the data; `remove --data erase` deletes it.
- No server code, no network (the page CSP has `connect-src 'none'`), no secrets.
- Change it: edit `ui/`, raise `version` in `module.json`, then `check`, `sign --own`, `install --enable`.
- Test: `python tests/test_app.py` (manifest, template rules, the review before install).
""",
    "tests/test_app.py": """# Test of an app made on the fly: the manifest and files pass the installer's check and its review before install.
# Run from anywhere: python tests/test_app.py (finds the Freedom tree through AIWB_FREEDOM, else C:\\AIWorkbench\\freedom).
import os
import pathlib
import sys

APP = pathlib.Path(__file__).resolve().parents[1]
TREE = pathlib.Path(os.environ.get("AIWB_FREEDOM") or r"C:\\AIWorkbench\\freedom")
sys.path.insert(0, str(TREE / "install"))
import freedom_modules as fm  # noqa: E402

problems = fm.check(APP)
assert not problems, problems
m = fm.load_manifest(APP)
assert m.get("envs"), "an app made on the fly is for named environments"
assert any(u.get("app") for u in m["provides"]["ui"]), "no app page"
r = fm.review(APP)
assert not r["errors"], r["errors"]
print("ok", m["id"], m["version"], *r["notes"])
""",
}


def owner_env() -> str:
    """The env of the owner of this install (install.json members role owner), '' when unknown."""
    for mem in aiwb_install.get("members") or []:
        if isinstance(mem, dict) and mem.get("role") == "owner" and isinstance(mem.get("env"), str):
            return mem["env"]
    return ""


def new_app(mid: str, parent, title: str, icon: str, env: str, label: str = "", lang: str = "en") -> pathlib.Path:
    """Writes an app module from the template into parent/<id>; refuses an existing folder."""
    if not ID_RE.match(mid):
        raise Fail("id: [a-z0-9-], up to 63 characters")
    if parent is None:
        dirs = [d for d in (aiwb_install.get("modules.dirs") or []) if isinstance(d, str)]
        if not dirs:
            raise Fail("no folder for own modules: pass --dir or set install.json modules.dirs")
        parent = pathlib.Path(dirs[0])
    env = env or owner_env()
    if not ENV_RE.match(env or ""):
        raise Fail("--env: the environment the app is made for (no owner in install.json members)")
    if mid in local_modules():
        raise Fail(f"a module {mid} already exists: {local_modules()[mid][0]}")
    root = pathlib.Path(parent) / mid
    if root.exists():
        raise Fail(f"{root} already exists")
    title = (title or mid.replace("-", " ").capitalize()).strip()[:80]
    icon = (icon or title[:1].upper()).strip()[:8]
    label = (label or title)[:40]
    ru = lang == "ru"
    fill = {"id": mid, "title": html_escape(title), "icon": html_escape(icon), "env": env, "lang": lang,
            "title_js": json.dumps(title, ensure_ascii=False),
            "placeholder": "Добавить…" if ru else "Add…", "empty": "Пока пусто." if ru else "Nothing here yet."}
    manifest = {
        "schema": 1, "id": mid, "version": "0.1.0", "title": title,
        "description": (f"{title}: приложение, сделанное агентом для одного человека (данные хранит ядро, без сети)." if ru
                        else f"{title}: an app made on the fly for one person (data kept by the core, no network)."),
        "ext": ">=1 <2", "scope": "environment", "envs": [env],
        "platforms": ["windows", "macos", "linux"], "requires": {"core": [], "modules": []},
        "permissions": ["data.env"],
        "provides": {"ui": [{"slot": "ui.page", "id": "main", "entry": "ui/main.html", "title": title, "app": True,
                             "icon": icon, "label": label}],
                     "data": {"server": "core app data (coExt.data)"}},
        "localOnly": ["tests/"],
        "dataPolicy": {"onDisable": "keep", "onRemove": "ask", "migrations": "additive"},
        "defaults": {"enabled": False},
    }
    (root / "ui").mkdir(parents=True)
    (root / "tests").mkdir()
    (root / "module.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    for rel, text in APP_TEMPLATE.items():
        (root / rel).write_text(text.format(**fill), encoding="utf-8", newline="\n")
    return root


# ---------- server ----------

def docker_variant() -> bool:
    """install.json variant B or C: the server is the Docker stack of freedom_docker.py (no SSH, its own host key)."""
    return str(aiwb_install.get("variant") or "A").upper() in ("B", "C")


_DOCKER = {}


def docker():
    """(freedom_docker, settings, Host) of the stack; prepare_install points site.home at /home/co in memory."""
    if not _DOCKER:
        import freedom_docker as fd
        s = fd.prepare_install()
        h = fd.Host(s)
        ca = None
        if s.get("variant") == "B" and s.get("tls") == "internal":   # Caddy's own CA, as freedom_docker verify reads it
            rc, ca = h.text(["docker", "exec", h.cid("caddy"), "cat", "/data/caddy/pki/authorities/local/root.crt"])
            ca = ca if rc == 0 and "BEGIN CERTIFICATE" in ca else None
        _DOCKER.update(fd=fd, s=s, h=h, ca=ca)
    return _DOCKER["fd"], _DOCKER["s"], _DOCKER["h"]


def host_call_docker(body: bytes) -> dict:
    """db.php of the stack through its published port, with the host key of the stack secrets (never the keyring
    key of another install)."""
    fd, s, h = docker()
    key = h.secret("push_key")
    if not key:
        raise Fail("no push_key in the secrets of the Docker stack (freedom_docker.py up)")
    code, _, text = fd.http_req(s, "/agent/db.php", "POST", body, {"Content-Type": "application/json", "X-CO-Push-Key": key,
                                                                    "User-Agent": USER_AGENT}, ca=_DOCKER.get("ca"), limit=None)
    try:
        j = json.loads(text)
    except ValueError:
        j = {}
    if code != 200:
        raise Fail(f"db.php modules_host: HTTP {code} {j.get('error', '') or text[:200]}")
    return j


def host_call(params: dict) -> dict:
    if docker_variant():
        raw = json.dumps({"op": "modules_host", **params}, ensure_ascii=False).encode("utf-8")
        return host_call_docker(json.dumps({"gz": base64.b64encode(gzip.compress(raw)).decode("ascii")}).encode("utf-8"))
    import keyring
    key = keyring.get_password("aiwb/co-push", "default")
    if not key:
        raise Fail("no host key in keyring aiwb/co-push (account default)")
    url = str(aiwb_install.get("site.url") or "").rstrip("/")
    if not url:
        raise Fail("install.json: site.url is needed")
    raw = json.dumps({"op": "modules_host", **params}, ensure_ascii=False).encode("utf-8")
    body = json.dumps({"gz": base64.b64encode(gzip.compress(raw)).decode("ascii")}).encode("utf-8")  # the hosting WAF
    req = urllib.request.Request(url + "/agent/db.php", data=body, method="POST",
                                 headers={"Content-Type": "application/json", "X-CO-Push-Key": key, "User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        try:
            j = json.loads(e.read().decode("utf-8"))
        except Exception:
            j = {}
        raise Fail(f"db.php modules_host: HTTP {e.code} {j.get('error', '')}")


def ssh_client():
    """SSH to the hosting: the owner's hz.py (AIWB_HZ_DIR or ~/.ssh) where it exists, else install.json site.ssh.
    With AIWB_INSTALL (another install, e.g. a rehearsal) only its own site.ssh: hz.py knows one fixed hosting."""
    import os
    if os.environ.get("AIWB_INSTALL") and not os.environ.get("AIWB_HZ_DIR"):
        default = pathlib.Path(os.environ.get("LOCALAPPDATA") or pathlib.Path.home() / ".config") / (
            "AIWorkbench" if os.environ.get("LOCALAPPDATA") else "aiwb") / "install.json"
        if os.path.normcase(str(pathlib.Path(os.environ["AIWB_INSTALL"]).resolve())) != os.path.normcase(str(default.resolve())):
            return aiwb_install.ssh_connect()
    sys.path.insert(0, os.environ.get("AIWB_HZ_DIR") or os.path.join(os.path.expanduser("~"), ".ssh"))
    try:
        import hz  # only its connect(); importing it runs no job
        return hz.connect()
    except ImportError:
        return aiwb_install.ssh_connect()


def remote_home() -> str:
    h = str(aiwb_install.get("site.home") or "")
    if not h.startswith("/"):
        raise Fail("install.json: site.home (absolute path of the hosting account) is needed")
    return h


def upload(root: pathlib.Path, mid: str, files: dict, sftp):
    """Writes the module into ~/co-agent/modules/<id>.new, then swaps it with the live folder (old one kept as .bak).
    Returns a function that puts the old folder back (when the server refuses the registration)."""
    base = posixpath.join(remote_home(), "co-agent", "modules")
    new, live, bak = f"{base}/{mid}.new", f"{base}/{mid}", f"{base}/{mid}.bak"

    def mkdirs(d):
        parts = d.strip("/").split("/")
        cur = ""
        for part in parts:
            cur += "/" + part
            try:
                sftp.stat(cur)
            except IOError:
                sftp.mkdir(cur, 0o700)

    def rmtree(d):
        try:
            for a in sftp.listdir_attr(d):
                p = f"{d}/{a.filename}"
                if a.st_mode & 0o040000:
                    rmtree(p)
                else:
                    sftp.remove(p)
            sftp.rmdir(d)
        except IOError:
            pass

    rmtree(new)
    mkdirs(new)
    for rel in ["module.json", *files]:
        dst = f"{new}/{rel}"
        mkdirs(posixpath.dirname(dst))
        sftp.put(str(root / rel), dst)
        sftp.chmod(dst, 0o600)
    rmtree(bak)
    had_old = True
    try:
        sftp.stat(live)
        sftp.rename(live, bak)
    except IOError:
        had_old = False
    try:
        sftp.rename(new, live)
    except IOError:
        if had_old:
            sftp.rename(bak, live)  # the live module keeps its old files
        raise

    def rollback():
        rmtree(live)
        if had_old:
            sftp.rename(bak, live)
    return rollback


def upload_docker(root: pathlib.Path, mid: str, files: dict):
    """upload() for variant B/C: one tar into the app container as the site user, then the same .new/.bak swap."""
    import io
    import tarfile
    import time
    fd, s, h = docker()
    cid = h.cid("app")
    if not cid:
        raise Fail("the app container of the Docker stack is not running (freedom_docker.py up)")
    t = fd.DockerTarget(h, cid)
    base = posixpath.join(fd.HOME, "co-agent", "modules")
    new, live, bak = (fd.fs._q(x) for x in (f"{base}/{mid}.new", f"{base}/{mid}", f"{base}/{mid}.bak"))
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as tar:
        for rel in ["module.json", *files]:
            data = (root / rel).read_bytes()
            ti = tarfile.TarInfo(rel)
            ti.size, ti.mode, ti.mtime = len(data), 0o600, time.time()
            tar.addfile(ti, io.BytesIO(data))
    rc, o = t.run(f"umask 077; rm -rf {new} && mkdir -p {new} && tar -x -m -C {new} -f - || exit 7; "
                  f"rm -rf {bak}; had=0; if [ -e {live} ]; then mv {live} {bak} || exit 8; had=1; fi; "
                  f"mv {new} {live} || {{ [ $had = 1 ] && mv {bak} {live}; exit 9; }}; echo had=$had",
                  stdin=buf.getvalue())
    if rc != 0:
        raise Fail(f"upload into the app container failed (rc {rc}): {o[-300:]}")
    had_old = "had=1" in o

    def rollback():
        rc2, o2 = t.run(f"rm -rf {live}" + (f" && mv {bak} {live}" if had_old else ""))
        if rc2 != 0:
            raise Fail(f"the old module files could not be put back (rc {rc2}): {o2[-200:]}; run install again")
    return rollback


def service_script(m: dict, s: dict, root=None):
    """<module folder>/<run> for a service the module runs itself (run = a .py file, no tool, no home/ cwd), else None.
    The folder is the one the module was installed or found in (install DIR, local_modules), else modules/<id> of
    this tree."""
    run = str(s.get("run") or "")
    if s.get("tool") or s.get("cwd") or not run.endswith(".py") or ".." in run or not ID_RE.match(str(m.get("id", ""))):
        return None
    base = pathlib.Path(root) if root else HERE.parent / "modules" / m["id"]
    return (base / run).resolve()


def repoint_refusal(script: pathlib.Path) -> str:
    """Why a task must not be pointed at `script` ('' = it may): a missing file, or a folder that goes away - a git
    worktree of an agent (devtree) or the read-only Drive mirror."""
    if not script.is_file():
        return f"{script} does not exist"
    try:
        g = subprocess.run(["git", "-C", str(script.parent), "rev-parse", "--git-dir", "--git-common-dir"],
                           capture_output=True, text=True, timeout=20)
        dirs = [pathlib.Path(script.parent, x.strip()).resolve() for x in g.stdout.splitlines() if x.strip()]
        if g.returncode == 0 and len(dirs) == 2 and dirs[0] != dirs[1]:
            return f"{script} is in a git worktree (run the installer from the main tree)"
    except (OSError, subprocess.SubprocessError):
        pass
    if "my drive" in str(script).lower():
        return f"{script} is on Google Drive (run the installer from the local tree)"
    return ""


def _xml_text(raw: bytes) -> str:
    """schtasks /Query /XML writes the XML in the console code page (UTF-16 when it keeps the BOM)."""
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        t = raw.decode("utf-16")
    else:
        try:
            t = raw.decode("utf-8")
        except UnicodeDecodeError:
            t = raw.decode("oem" if sys.platform == "win32" else "latin-1", errors="replace")
    return t.lstrip("\ufeff").replace("\r\r\n", "\n").replace("\r\n", "\n")


def _norm(p: str) -> str:
    p = p.strip().strip('"')
    return os.path.normcase(os.path.normpath(p)) if p else ""


def _unxml(t: str) -> str:
    return t.replace("&quot;", '"').replace("&apos;", "'").replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")


def repoint_xml(xml: str, script: pathlib.Path, python: str = ""):
    """The task XML with its (first) Exec action running `script` from its folder, or None when it already does.
    Only Command (when it is not a Python), Arguments and WorkingDirectory change; the rest of the task stays."""
    m = re.search(r"<Exec>(.*?)</Exec>", xml, re.S)
    if not m:
        raise Fail("the task has no Exec action")
    body = m.group(1)

    def tag(name):
        t = re.search(rf"<{name}>(.*?)</{name}>", body, re.S)
        return _unxml(t.group(1)) if t else ""
    cmd, args, wd = tag("Command"), tag("Arguments"), tag("WorkingDirectory")
    first = re.match(r'\s*("[^"]*"|\S+)', args)
    is_py = re.fullmatch(r"pythonw?(\d[\d.]*)?\.exe", os.path.basename(cmd.strip().strip('"')).lower()) is not None
    if is_py and first and _norm(first.group(1)) == _norm(str(script)) and _norm(wd) == _norm(str(script.parent)):
        return None
    if not is_py:
        cmd = python or str(pathlib.Path(sys.executable).with_name("pythonw.exe"))
    quoted = '"' + str(script) + '"'
    new = (f"\n      <Command>{xml_escape(cmd)}</Command>\n      <Arguments>{xml_escape(quoted)}</Arguments>"
           f"\n      <WorkingDirectory>{xml_escape(str(script.parent))}</WorkingDirectory>\n    ")
    return xml[:m.start(1)] + new + xml[m.end(1):]


def repoint(task: str, script: pathlib.Path):
    """Re-creates the task from its own XML with the action pointed at `script`; a note of what was done, None if the
    task already runs it."""
    q = subprocess.run(["schtasks", "/Query", "/TN", task, "/XML"], capture_output=True)
    if q.returncode != 0:
        return f"{task}: not found, not re-pointed (create it to run {script})"
    new = repoint_xml(_xml_text(q.stdout), script)
    if new is None:
        return None
    fd, tmp = tempfile.mkstemp(suffix=".xml", prefix="freedom-task-")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(new.replace("\n", "\r\n").encode("utf-16"))  # with BOM, as the XML declaration says
        r = subprocess.run(["schtasks", "/Create", "/TN", task, "/XML", tmp, "/F"], capture_output=True, text=True)
    finally:
        os.unlink(tmp)
    if r.returncode != 0:
        raise Fail(f"{task}: re-pointing to {script} failed: {(r.stderr or r.stdout).strip()}")
    return f"{task}: re-pointed to {script}"


def service(m: dict, action: str, root=None) -> list:
    """Starts/stops the module's Windows tasks (provides.services[].windowsTask); returns what was done.
    start first re-points a task whose action does not run the module's own script (service run = a .py file of the
    module) and restarts it, so the moved code is what runs; a task that is right is only enabled and started. A task
    that cannot be re-pointed (no such file, a worktree, an error of schtasks) keeps its old action and still starts."""
    done = []
    supervised = bridge_supervises()
    for s in (m.get("provides") or {}).get("services", []):
        if supervised and bridge_runs(s):
            done.append(f"{s.get('id')}: Bridge supervises it ({action} within a minute)")
            continue
        if sys.platform != "win32":
            continue
        task = s.get("windowsTask")
        if not task:
            continue
        moved = False
        script = service_script(m, s, root)
        if script is not None and action == "start":
            why = repoint_refusal(script)
            try:
                note = f"{task}: not re-pointed: {why}" if why else repoint(task, script)
            except Fail as e:
                note = f"{task}: not re-pointed, the old action stays: {e}"
            if note:
                done.append(note)
                moved = note.startswith(f"{task}: re-pointed")
        verb = "/ENABLE" if action == "start" else "/DISABLE"
        r = subprocess.run(["schtasks", "/Change", "/TN", task, verb], capture_output=True, text=True)
        if r.returncode == 0 and moved:
            # the old copy still runs the old code: stop it (not running is fine), then start the moved one
            subprocess.run(["schtasks", "/End", "/TN", task], capture_output=True, text=True)
        # a schedule only becomes active again: it runs at its own time, not as a surprise at enable
        if r.returncode == 0 and not (action == "start" and s.get("kind") == "schedule"):
            r = subprocess.run(["schtasks", "/Run" if action == "start" else "/End", "/TN", task], capture_output=True, text=True)
            # /End of a task that is not running is not a failure of "stop"
            if action == "stop" and r.returncode != 0 and "not running" in (r.stderr + r.stdout).lower():
                r = subprocess.CompletedProcess(r.args, 0, "", "")
        done.append(f"{task}: {'restart' if moved else action} {'ok' if r.returncode == 0 else 'failed: ' + (r.stderr or r.stdout).strip()}")
    return done


# ---------- tool library (toolbox 1.7.0+) ----------

def tools_root() -> pathlib.Path:
    """The tool library: AIWB_TOOLS, else the git working copy C:\\AIWorkbench\\tools, else <workbench>/tools."""
    import os
    if os.environ.get("AIWB_TOOLS"):
        return pathlib.Path(os.environ["AIWB_TOOLS"])
    local = pathlib.Path(r"C:\AIWorkbench\tools")
    if sys.platform == "win32" and (local / "toolbox").is_dir():
        return local
    return aiwb_install.workbench_root() / "tools"


def tool_version(lib: pathlib.Path, name: str):
    """Version from the SKILL.md frontmatter of a tool folder, None when there is no such tool."""
    f = lib / name / "SKILL.md"
    if not f.is_file():
        return None
    m = re.search(r"^version:\s*['\"]?([0-9][0-9A-Za-z.+-]*)", f.read_text(encoding="utf-8", errors="replace"), re.M)
    return m.group(1) if m else "0"


def vtuple(v: str) -> tuple:
    return tuple(int(x) for x in re.findall(r"\d+", v or "0")[:3]) or (0,)


def toolbox(*args: str) -> dict:
    """Runs toolbox.py of the library; its last JSON line (or {'ok': False, 'error': ...})."""
    script = tools_root() / "toolbox" / "toolbox.py"
    if not script.is_file():
        raise Fail(f"toolbox not found: {script} (set AIWB_TOOLS)")
    r = subprocess.run([sys.executable, str(script), *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    try:
        j = json.loads(r.stdout)
    except json.JSONDecodeError:  # notes before the result: its last JSON line
        last = next((ln for ln in reversed(r.stdout.strip().splitlines()) if ln.startswith("{")), "")
        try:
            j = json.loads(last) if last else {}
        except json.JSONDecodeError:
            j = {}
    j = j if isinstance(j, dict) else {}
    if r.returncode != 0:
        return {"ok": False, "error": (r.stdout + r.stderr).strip()[-400:]}
    return {"ok": True, **j}


def tool_sources(root: pathlib.Path) -> list:
    """Where a module's tools may come from: its own tools/, then tools/ of the distribution (Freedom export)."""
    return [d for d in (root / "tools", HERE.parent / "tools") if d.is_dir()]


def tools_plan(root: pathlib.Path, m: dict) -> list:
    """[{name, have, offered, from, action: ok|install|update|missing|old}] for provides.tools (read only)."""
    lib = tools_root()
    out = []
    for t in tool_entries(m):
        have = tool_version(lib, t["name"])
        src = next((d for d in tool_sources(root) if (d / t["name"] / "SKILL.md").is_file()), None)
        offered = tool_version(src, t["name"]) if src else None
        if offered and (have is None or vtuple(offered) > vtuple(have)):
            action, result = ("install" if have is None else "update"), offered
        elif have is None:
            action, result = "missing", None
        else:
            action, result = "ok", have
        if result is not None and t["minVersion"] and vtuple(result) < vtuple(t["minVersion"]):
            action = "old"   # what the library would end up with is still too old: change nothing
        out.append({"name": t["name"], "have": have, "offered": offered, "from": str(src) if src else None,
                    "minVersion": t["minVersion"], "action": action})
    return out


def tools_install(root: pathlib.Path, m: dict) -> list:
    """Puts the module's tools into the library (toolbox install --from) and records them as owned by the module,
    switched off (toolbox module --state disabled) until the module is enabled. Fails before anything else changes
    when a tool is neither in the library nor shipped."""
    plan = tools_plan(root, m)
    bad = [f"{t['name']} ({t['action']}{', needs ' + t['minVersion'] if t['minVersion'] else ''})" for t in plan
           if t["action"] in ("missing", "old")]
    if bad:
        raise Fail("tools not in the library and not shipped with the module: " + ", ".join(bad))
    for t in plan:
        if t["action"] in ("install", "update"):
            r = toolbox("install", t["name"], "--from", t["from"], *(["--update"] if t["action"] == "update" else []))
            if not r["ok"]:
                raise Fail(f"toolbox install {t['name']}: {r['error']}")
            t["done"] = r.get("status", "installed")
    return plan


def tools_state(m: dict, state: str, record_tools: bool = True) -> dict:
    """toolbox module <id> --state enabled|disabled|removed (with the tool list when the manifest is at hand)."""
    names = [t["name"] for t in tool_entries(m)]
    if state != "removed" and not names and not (m.get("provides") or {}).get("services"):
        return {"ok": True, "skipped": "no tools or services"}
    args = ["module", m["id"], "--state", state]
    if state != "removed" and record_tools:
        args += ["--tools", ",".join(names), "--version", str(m.get("version", ""))]
    return toolbox(*args)


def module_dirs() -> list:
    """Folders with module folders: modules/ of this tree, then install.json modules.dirs (the owner's private
    modules live outside the repository, e.g. C:\\AIWorkbench\\private-modules), then AIWB_MODULE_DIRS (os.pathsep)."""
    import os
    dirs = [HERE.parent / "modules"]
    dirs += [pathlib.Path(d) for d in (aiwb_install.get("modules.dirs") or []) if isinstance(d, str)]
    dirs += [pathlib.Path(d) for d in os.environ.get("AIWB_MODULE_DIRS", "").split(os.pathsep) if d]
    return [d for d in dirs if d.is_dir()]


def local_modules() -> dict:
    """id -> (folder, manifest) of every module folder at home (first folder wins)."""
    out = {}
    for d in module_dirs():
        for f in sorted(d.glob("*/module.json")):
            try:
                m = json.loads(f.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                continue
            if isinstance(m, dict) and ID_RE.match(str(m.get("id", ""))) and m["id"] not in out:
                out[m["id"]] = (f.parent, m)
    return out


def sync(live: list, apply: bool = True) -> list:
    """Brings the home PC in line with the switches on the server (the owner may switch a module in CO Web): tool
    states in the library and the module's services. Only modules whose state differs are touched, so a running
    daemon is not started twice. Bridge's module loader can run this on its tick."""
    have = {}
    try:
        r = toolbox("modules", "--json")
        have = r.get("modules", {}) if r.get("ok") else {}
    except Fail:
        pass
    loc = local_modules()
    done = []
    for x in live:
        if x["id"] not in loc:
            continue
        m = loc[x["id"]][1]
        if not tool_entries(m) and not (m.get("provides") or {}).get("services"):
            continue
        cur = have.get(x["id"])
        if cur is not None and bool(cur.get("enabled")) == bool(x["enabled"]):
            continue
        item = {"id": x["id"], "enabled": x["enabled"]}
        if apply:
            item.update(apply_home(m, "enabled" if x["enabled"] else "disabled", root=loc[x["id"]][0]))
        done.append(item)
    ids = {x["id"] for x in live}
    for mid in sorted(set(have) - ids):  # removed on the server
        if mid in loc:
            item = {"id": mid, "removed": True}
            if apply:
                item.update(apply_home(loc[mid][1], "removed"))
            done.append(item)
    return done


def services_ok(done: list) -> bool:
    return not any(re.search(r": (start|stop) failed", d) for d in done)


def apply_home(m: dict, target: str, services: bool = True, root=None) -> dict:
    """Brings the home PC to target (enabled|disabled|removed): services first, then the recorded tool state. The
    record is what tells sync a module is done, so it is written only after the services switched: a failed start
    or stop stays visible to the next sync. A failed stop at removal keeps the module recorded (switched off), so
    sync - which sees it gone from the server - tries the removal again."""
    out = {"ok": True}
    if services:
        out["service"] = service(m, "start" if target == "enabled" else "stop", root)
        out["ok"] = services_ok(out["service"])
    if out["ok"]:
        out["tools"] = tools_state(m, target)
    elif target == "removed":
        out["tools"] = tools_state(m, "disabled")
    if "tools" in out and not out["tools"].get("ok"):
        out["ok"] = False
    return out


def parse_people(items: list) -> dict:
    out = {}
    for it in items or []:
        env, _, mode = it.partition("=")
        if not env or mode not in PERSON_MODES:
            raise Fail(f"--person {it!r}: ENV=auto|ask|off")
        out[env] = mode
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest="cmd", required=True)
    for name in ("check", "verify", "plan"):
        p = sp.add_parser(name)
        p.add_argument("dir", type=pathlib.Path)
        p.add_argument("--trust", type=pathlib.Path, default=fr.DEFAULT_TRUST)
    p = sp.add_parser("new", help="an app module from the template (F14c)")
    p.add_argument("id")
    p.add_argument("--app", action="store_true", required=True, help="the only kind of template so far")
    p.add_argument("--title", default="")
    p.add_argument("--icon", default="", help="an emoji or up to 8 characters")
    p.add_argument("--label", default="", help="the name on the tile (default: the title)")
    p.add_argument("--env", default="", help="the environment the app is for (default: the owner's)")
    p.add_argument("--dir", type=pathlib.Path, help="parent folder (default: install.json modules.dirs[0])")
    p.add_argument("--lang", default="en", choices=("en", "ru"), help="language of the template's few words")
    p = sp.add_parser("sign")
    p.add_argument("dir", type=pathlib.Path)
    p.add_argument("--name", default="maintainer-1")
    p.add_argument("--own", action="store_true", help="the owner's own key (install.json modules.ownKey, default owner)")
    p = sp.add_parser("review", help="the check before installing a module that is not standard")
    p.add_argument("dir", type=pathlib.Path)
    p = sp.add_parser("install")
    p.add_argument("dir", type=pathlib.Path)
    p.add_argument("--trust", type=pathlib.Path, default=fr.DEFAULT_TRUST)
    p.add_argument("--enable", action="store_true")
    p.add_argument("--allow-findings", action="store_true", help="install a module that is not standard despite errors "
                   "of its review (only after the owner read them)")
    p.add_argument("--default-person", choices=PERSON_MODES, help="default switch of every member who has not chosen")
    p.add_argument("--person", action="append", help="ENV=MODE default of one member")
    p.add_argument("--allow-untrusted", action="store_true")
    p.add_argument("--server-only", action="store_true", help="only the server: no tools, services or Bridge copy "
                   "on this computer (the home PC is another one, or a rehearsal stack)")
    sp.add_parser("list")
    p = sp.add_parser("sync", help="tool states and services of the home PC follow the switches on the server")
    p.add_argument("--dry-run", action="store_true")
    for name in ("enable", "disable"):
        p = sp.add_parser(name)
        p.add_argument("id")
        p.add_argument("--no-service", action="store_true")
        p.add_argument("--server-only", action="store_true", help="only the server: no tools, services or Bridge copy "
                       "on this computer (the home PC is another one, or a rehearsal stack)")
    p = sp.add_parser("person")
    p.add_argument("id")
    p.add_argument("env")
    p.add_argument("mode", choices=PERSON_MODES)
    p = sp.add_parser("remove")
    p.add_argument("id")
    p.add_argument("--data", choices=("keep", "erase"), required=True)
    p.add_argument("--yes", action="store_true", help="needed with --data erase (cannot be undone)")
    p.add_argument("--server-only", action="store_true", help="only the server: no tools, services or Bridge copy "
                   "on this computer (the home PC is another one, or a rehearsal stack)")
    a = ap.parse_args(argv)
    try:
        return run(a)
    except Fail as e:
        print(f"error: {e}", file=sys.stderr)
        return 1


def run(a) -> int:
    out = lambda d: print(json.dumps(d, ensure_ascii=False, indent=1))  # noqa: E731
    if a.cmd == "check":
        p = check(a.dir)
        out({"ok": not p, "problems": p})
        return 0 if not p else 1
    if a.cmd == "new":
        root = new_app(a.id, a.dir, a.title, a.icon, a.env, a.label, a.lang)
        out({"ok": True, "dir": str(root), "next": [f'python install/freedom_modules.py check "{root}"',
                                                     f'python install/freedom_modules.py sign "{root}" --own',
                                                     f'python install/freedom_modules.py install "{root}" --enable']})
        return 0
    if a.cmd == "review":
        r = review(a.dir)
        out({"ok": not r["errors"], **r})
        return 0 if not r["errors"] else 1
    if a.cmd == "sign":
        p = check(a.dir)
        if p:
            raise Fail("check failed: " + "; ".join(p))
        key = None
        if a.own:
            a.name = own_key_name()
            key = fr.keyring_key(a.name, create=True)
            pub = fr.public_hex(key)
            if pub not in trusted_authors()["keys"]:
                print(f"note: add the owner's key to install.json modules.trustedKeys, else the module does not install: "
                      f'{{"name": "{a.name}", "key": "{pub}"}}', file=sys.stderr)
        m = load_manifest(a.dir)
        man = signed_files(a.dir, fr.build(a.dir, str(m["version"])))
        ignored = git_ignored(a.dir, [f["path"] for f in man["files"]])
        if ignored:
            raise Fail("git ignores files that would be signed (no other checkout has them): " + ", ".join(ignored[:10])
                       + " - delete them or list them as generated")
        man["module"] = m["id"]
        fr.write_json(a.dir / fr.MANIFEST_NAME, fr.sign(man, a.name, key or fr.keyring_key(a.name)))
        out({"ok": True, "signed": str(a.dir / fr.MANIFEST_NAME), "files": len(man["files"])})
        return 0
    if a.cmd == "verify":
        v = verify(a.dir, a.trust)
        out(v)
        return 0 if v["trusted"] else 1
    if a.cmd == "list":
        out(host_call({"action": "list"}))
        return 0
    if a.cmd == "sync":
        done = sync(host_call({"action": "list"}).get("modules", []), apply=not a.dry_run)
        ok = all(d.get("ok", True) for d in done)
        out({"ok": ok, "changed": done})
        return 0 if ok else 1
    if a.cmd in ("enable", "disable"):
        r = host_call({"action": "set", "id": a.id, "enabled": a.cmd == "enable"})
        local = local_modules().get(a.id)   # tools and services are described by the module's own manifest
        ok = bool(r.get("ok"))
        if ok and local and not a.server_only:
            r["home"] = apply_home(local[1], "enabled" if a.cmd == "enable" else "disabled", services=not a.no_service, root=local[0])
            ok = r["home"]["ok"]
        out(r)
        return 0 if ok else 1
    if a.cmd == "person":
        r = host_call({"action": "person", "id": a.id, "env": a.env, "mode": a.mode})
        out(r)
        return 0 if r.get("ok") else 1
    if a.cmd == "remove":
        if a.data == "erase" and not a.yes:
            raise Fail("--data erase deletes the module's data on the server for good; add --yes after the owner's yes")
        r = host_call({"action": "remove", "id": a.id, "data": a.data})
        local = local_modules().get(a.id)
        ok = bool(r.get("ok"))
        if ok and local and not a.server_only:
            r["home"] = apply_home(local[1], "removed")   # the tool files stay in the library
            ok = r["home"]["ok"]
        if ok and not a.server_only:
            r["local"] = "removed" if remove_local(a.id) else "none"   # Bridge's copy
        out(r)
        return 0 if ok else 1

    # plan / install
    p = check(a.dir)
    if p:
        raise Fail("check failed: " + "; ".join(p))
    m = load_manifest(a.dir)
    v = verify(a.dir, a.trust)
    if not v["trusted"] and not getattr(a, "allow_untrusted", False) and a.cmd == "install":
        raise Fail("signature not trusted: " + "; ".join(v["problems"]) + " (standard modules: install/maintainers.json; "
                   "own authors: install.json modules.trustedKeys; or --allow-untrusted)")
    rv = review(a.dir) if not v["standard"] else {"errors": [], "warnings": [], "notes": []}
    if rv["errors"] and a.cmd == "install" and not a.allow_findings:
        raise Fail("review before install found: " + "; ".join(rv["errors"][:10]) + " (fix them, or --allow-findings "
                   "after the owner read them)")
    files = module_files(a.dir, with_server=v["standard"])
    live = next((x for x in host_call({"action": "list"}).get("modules", []) if x["id"] == m["id"]), None)
    plan = {"id": m["id"], "version": m["version"], "standard": v["standard"], "level": v["level"],
            "publisher": v["publisher"], "envs": m.get("envs") or [], "review": rv,
            "signature": "ok" if v["trusted"] else "untrusted", "installed": live and live["version"],
            "files": len(files), "changed": sorted(k for k, s in files.items() if not live or live["files"].get(k) != s),
            "config": sorted(install_config(m)),
            "serverCode": v["standard"] and bool(m.get("server")), "permissions": m.get("permissions", []),
            "tools": tools_plan(a.dir, m) if tool_entries(m) else [],
            "services": [{k: s.get(k) for k in ("id", "kind", "cron", "windowsTask") if s.get(k)}
                         for s in (m.get("provides") or {}).get("services", []) or []],
            "secrets": [s.get("name") if isinstance(s, dict) else s for s in (m.get("provides") or {}).get("secrets", []) or []]}
    if a.cmd == "plan":
        out(plan)
        return 0
    people = parse_people(a.person)
    if a.default_person:
        for mem in aiwb_install.get("members") or []:
            env = mem.get("env") if isinstance(mem, dict) else None
            if env and env not in people:
                people[env] = a.default_person
    if tool_entries(m) and not a.server_only:
        plan["tools"] = tools_install(a.dir, m)  # before the server: a missing tool stops the install here
    c = None if docker_variant() else ssh_client()
    try:
        sftp = c.open_sftp() if c else None
        rollback = upload(a.dir, m["id"], files, sftp) if c else upload_docker(a.dir, m["id"], files)
        try:
            r = host_call({"action": "install", "manifest": m, "files": files, "standard": v["standard"],
                           "publisher": v["publisher"], "people": people, "config": install_config(m)})
        except Exception as e:  # timeout, network, HTTP error: the answer is lost, ask what the server registered
            r = {"ok": False, "error": f"no answer: {e}"}
            try:
                now = next((x for x in host_call({"action": "list"}).get("modules", []) if x["id"] == m["id"]), None)
            except Exception as e2:
                # neither answer came: the server may have registered the new files, so the old ones must not come
                # back blindly (their sha256 would no longer match); the new files stay and a rerun settles it
                raise Fail(f"outcome unknown ({e}; list: {e2}); the new files stay on the server - run install again")
            if now is not None and now.get("version") == m["version"] and now.get("files") == files:
                r = {"ok": True, "id": m["id"], "version": m["version"], "enabled": now.get("enabled"), "recovered": str(e)}
        if not r.get("ok"):
            # the server keeps (or put back) the old registration, so the old files must come back too
            rollback()
            raise Fail(f"server refused, old files restored: {r}")
        if sftp:
            sftp.close()
    finally:
        if c:
            c.close()
    if r.get("grew") and not r.get("enabled") and (a.enable or (live and live.get("enabled"))):
        # also without --enable: an update that was on and is now off must say so, or it looks like a broken module
        print(f"note: permissions grew ({', '.join(r['grew'])}); the module stays off until the owner turns it on "
              f"(freedom_modules.py enable {m['id']})", file=sys.stderr)
    elif a.enable and not r.get("enabled"):
        r["enable"] = host_call({"action": "set", "id": m["id"], "enabled": True})
    on = bool(r.get("enabled") or (r.get("enable") or {}).get("ok"))
    if m.get("warning") and not on:
        print(f"note: {m['warning']}", file=sys.stderr)
    if a.server_only:
        out({"plan": plan, "server": r})
        return 0
    r["local"] = str(install_local(a.dir, m["id"]))   # Bridge's module loader reads this copy
    # a module that is on gets its services started; one that was on before and stays off now (an update whose
    # permissions grew waits for the owner) gets them stopped; a fresh install that stays off does not touch them
    r["home"] = apply_home(m, "enabled" if on else "disabled", services=on or bool(live and live.get("enabled")), root=a.dir)
    out({"plan": plan, "server": r})
    return 0 if r["home"]["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
