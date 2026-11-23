#!/usr/bin/env python3
"""Minimal MCP stdio server exposing the AIWB CLI tool library (stdlib only, Python 3.9+).

toolbox 1.4.0 (1.7.0: tools of switched-off modules are unavailable, modstate.py; 1.8.0: app_list / app_action,
actions of Freedom apps for the agent, apps.py). Registered as MCP server "aiwb-tools" for Claude Code and Codex on the home PC and on the site agent
(the CO site of the install), so every agent sees the same library: tool_list, tool_help (SKILL.md), tool_run.
"""
import importlib.util
import json
import os
import re
import subprocess
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import modstate  # toolbox 1.7.0: tools of switched-off Freedom modules
except ImportError:  # an older copy of the library without it
    modstate = None
try:
    import apps  # toolbox 1.8.0: actions of apps (Freedom F14b-1)
except ImportError:
    apps = None

ROOT = os.path.abspath(os.environ.get("AIWB_TOOLS")
                       or os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IS_WINDOWS = os.name == "nt"
PIP_TO_MODULE = {"python-docx": "docx", "pymupdf": "pymupdf", "python-pptx": "pptx", "pyyaml": "yaml",
                 "websocket-client": "websocket", "beautifulsoup4": "bs4", "pillow": "PIL", "pywin32": "win32api"}
# only a bare pip spec ("openpyxl", "pint>=0.20", "requests[socks]") is checked as a python module; free text such as
# "node 22+", "Microsoft Edge" or "playwright (npm, local)" describes a program and is not checked here (1.6.1)
LIST_ITEM = re.compile(r"""(?:"[^"]*"|'[^']*'|\([^()]*\)|[^,])+""")
PIP_SPEC = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*(\[[^\]]*\])?\s*([<>=!~]=?\s*[^\s,]+\s*(,\s*[<>=!~]=?\s*[^\s,]+\s*)*)?$")
DEFAULT_PROTOCOL = "2025-06-18"
INSTRUCTIONS = ("This server exposes a library of local CLI tools. Always call tool_list (optionally with a "
                "query) to discover tools, then tool_help to read a tool's SKILL.md (usage, arguments, "
                "outputs) BEFORE calling tool_run. tool_run executes the tool's script with the arguments you "
                "pass; tools write files only where you tell them (via arguments or cwd). app_list / app_action "
                "use the apps of the AI Workbench environment (Home, Calendar, ...) the way the person would.")

_OUT = sys.stdout.buffer
sys.stdout = sys.stderr  # protect the protocol channel from stray prints


def log(msg):
    sys.stderr.write("[aiwb-tools] %s\n" % msg)
    sys.stderr.flush()


def send(obj):
    _OUT.write((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
    _OUT.flush()


def parse_front_matter(text):
    lines = text.lstrip("﻿").splitlines()
    meta = {}
    if not lines or lines[0].strip() != "---":
        return meta
    for line in lines[1:]:
        if line.strip() == "---":
            break
        if ":" not in line or line.lstrip().startswith("#") or line.startswith((" ", "\t", "-")):
            continue  # nested blocks (security: exceptions) are not tool metadata
        key, _, val = line.partition(":")
        key, val = key.strip().lower(), val.strip()
        if val.startswith("[") and val.endswith("]"):
            # commas inside brackets or quotes belong to the item: [browser-agent (node 22+, Edge), "a, b"] (1.6.1)
            items = [x.strip().strip("'\"") for x in LIST_ITEM.findall(val[1:-1])]
            meta[key] = [x for x in items if x]
        else:
            meta[key] = val.strip("'\"")
    return meta


def as_list(value):
    if isinstance(value, list):
        return value
    return [value] if isinstance(value, str) and value else []


def load_tools():
    tools = {}
    try:
        entries = sorted(os.listdir(ROOT))
    except OSError as e:
        log("cannot list %s: %s" % (ROOT, e))
        return tools
    for d in entries:
        path = os.path.join(ROOT, d)
        skill = os.path.join(path, "SKILL.md")
        if d.startswith(".") or not os.path.isfile(skill):
            continue
        try:
            with open(skill, encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError as e:
            log("cannot read %s: %s" % (skill, e))
            continue
        meta = parse_front_matter(text)
        meta.update({"folder": d, "path": path, "text": text})
        tools[d] = meta
    return tools


def module_for(req):
    if not PIP_SPEC.match(req.strip()):
        return None
    name = re.split(r"[<>=!~;\[\s]", req.strip(), 1)[0].lower()
    return PIP_TO_MODULE.get(name, name.replace("-", "_"))


def unavailable_reason(meta, state=None):
    reasons = []
    if modstate is not None:
        off = modstate.off_reason(meta.get("folder", ""), state)
        if off:
            reasons.append(off)
    platforms = [p.lower() for p in as_list(meta.get("platform"))]
    if platforms and all(p.startswith("win") for p in platforms) and not IS_WINDOWS:
        reasons.append("windows only")
    missing = []
    for req in as_list(meta.get("requires")):
        mod = module_for(req)
        if not mod:
            continue
        try:
            ok = importlib.util.find_spec(mod) is not None
        except (ImportError, ValueError):
            ok = False
        if not ok:
            missing.append(req)
    if missing:
        reasons.append("missing python modules: " + ", ".join(missing))
    return "; ".join(reasons)


def clip(s, limit):
    if len(s) <= limit:
        return s
    return s[:limit] + "\n[... truncated: showing %d of %d chars]" % (limit, len(s))


def get_tool(args, tools):
    name = args.get("name")
    if not isinstance(name, str) or not name.strip():
        return None, "'name' (string) is required"
    if "/" in name or "\\" in name or name.startswith(".") or name not in tools:
        return None, "unknown tool '%s' (use tool_list)" % name
    return tools[name], None


def tool_list(args):
    query = args.get("query") or ""
    if not isinstance(query, str):
        return "'query' must be a string", True
    words = query.lower().split()
    lines, unavailable = [], []
    state = modstate.load() if modstate is not None else None
    for folder, m in load_tools().items():
        desc = str(m.get("description", ""))
        hay = ("%s %s %s" % (folder, m.get("name", ""), desc)).lower()
        if not all(w in hay for w in words):
            continue
        reason = unavailable_reason(m, state)
        if reason:
            unavailable.append("%s (%s)" % (folder, reason))
        else:
            lines.append("%s %s — %s" % (folder, m.get("version", "?"), desc))
    if not lines and not unavailable:
        return "no tools found" + (" matching '%s'" % query if query else "") + " in " + ROOT, False
    if unavailable:
        lines.append("unavailable here: " + ", ".join(unavailable))
    return "\n".join(lines), False


def tool_help(args):
    meta, problem = get_tool(args, load_tools())
    if problem:
        return problem, True
    return meta["text"], False


def tool_run(args):
    meta, problem = get_tool(args, load_tools())
    if problem:
        return problem, True
    run_args = args.get("args", [])
    if not isinstance(run_args, list) or not all(isinstance(a, str) for a in run_args):
        return "'args' must be an array of strings", True
    reason = unavailable_reason(meta)
    if reason:
        return "tool '%s' is unavailable here: %s" % (meta["folder"], reason), True
    entry = meta.get("entry")
    if not isinstance(entry, str) or not entry:
        return "tool '%s' has no 'entry' in SKILL.md" % meta["folder"], True
    script = os.path.normpath(os.path.join(meta["path"], entry))
    if not script.startswith(os.path.normpath(meta["path"]) + os.sep) or not os.path.isfile(script):
        return "entry script not found: %s" % script, True
    cwd = args.get("cwd") or os.getcwd()
    if not isinstance(cwd, str) or not os.path.isdir(cwd):
        return "'cwd' is not an existing directory: %s" % cwd, True
    timeout = args.get("timeout_sec", 300)
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or timeout <= 0:
        return "'timeout_sec' must be a positive integer", True
    timeout = max(1, min(int(timeout), 1800))
    stdin = args.get("stdin")
    if stdin is not None and not isinstance(stdin, str):
        return "'stdin' must be a string", True
    kw = {"input": stdin.encode("utf-8")} if stdin is not None else {"stdin": subprocess.DEVNULL}
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    timed_out = False
    try:
        p = subprocess.run([sys.executable, script] + run_args, cwd=cwd, env=env,
                           capture_output=True, timeout=timeout, **kw)
        code, out, err = p.returncode, p.stdout, p.stderr
    except subprocess.TimeoutExpired as e:
        timed_out, code, out, err = True, -1, e.stdout or b"", e.stderr or b""
    except OSError as e:
        return "failed to start tool: %s" % e, True
    out = out.decode("utf-8", "replace") if isinstance(out, bytes) else str(out)
    err = err.decode("utf-8", "replace") if isinstance(err, bytes) else str(err)
    text = "exit code %d" % code
    if timed_out:
        text += " (timed out after %ds, process killed)" % timeout
    text += "\n--- stdout ---\n" + clip(out, 60000)
    if err.strip():
        text += "\n--- stderr ---\n" + clip(err, 20000)
    return text, timed_out or code != 0


HANDLERS = {"tool_list": tool_list, "tool_help": tool_help, "tool_run": tool_run}
TOOLS = [
    {"name": "tool_list", "description": "List available CLI tools (name version — description). "
     "Optional query filters by words (all must match).",
     "inputSchema": {"type": "object", "properties": {"query": {"type": "string"}}}},
    {"name": "tool_help", "description": "Return the full SKILL.md of a tool. Read it before tool_run.",
     "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}},
    {"name": "tool_run", "description": "Run a tool's entry script with arguments; returns exit code, "
     "stdout and stderr.",
     "inputSchema": {"type": "object", "properties": {
         "name": {"type": "string"},
         "args": {"type": "array", "items": {"type": "string"}},
         "cwd": {"type": "string", "description": "Working directory (default: server cwd)"},
         "timeout_sec": {"type": "integer", "default": 300, "minimum": 1, "maximum": 1800},
         "stdin": {"type": "string"}}, "required": ["name", "args"]}},
]
if apps is not None:
    HANDLERS.update(apps.HANDLERS)
    TOOLS += apps.TOOLS


def handle(msg):
    if not isinstance(msg, dict):
        return {"jsonrpc": "2.0", "id": None, "error": {"code": -32600, "message": "Invalid Request"}}
    method, mid = msg.get("method"), msg.get("id")
    params = msg.get("params") if isinstance(msg.get("params"), dict) else {}
    if "id" not in msg or (method is None and ("result" in msg or "error" in msg)):
        return None  # notification or response from client
    if not isinstance(method, str):
        return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32600, "message": "Invalid Request"}}
    if method == "initialize":
        result = {"protocolVersion": params.get("protocolVersion") or DEFAULT_PROTOCOL,
                  "capabilities": {"tools": {}},
                  "serverInfo": {"name": "aiwb-tools", "version": "1.0.0"},
                  "instructions": INSTRUCTIONS}
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = {"tools": TOOLS}
    elif method == "tools/call":
        arguments = params.get("arguments") or {}
        fn = HANDLERS.get(params.get("name"))
        if fn is None:
            text, is_err = "unknown tool '%s'" % params.get("name"), True
        elif not isinstance(arguments, dict):
            text, is_err = "'arguments' must be an object", True
        else:
            try:
                text, is_err = fn(arguments)
            except Exception as e:  # never crash the server on a tool error
                log(traceback.format_exc())
                text, is_err = "internal error: %s" % e, True
        result = {"content": [{"type": "text", "text": text}], "isError": bool(is_err)}
    else:
        return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "Method not found: " + method}}
    return {"jsonrpc": "2.0", "id": mid, "result": result}


def main():
    log("serving tools from %s" % ROOT)
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            break
        if not line.strip():
            continue
        try:
            msg = json.loads(line.decode("utf-8"))
        except (ValueError, UnicodeDecodeError) as e:
            send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error: %s" % e}})
            continue
        if isinstance(msg, list):
            replies = [r for r in (handle(m) for m in msg) if r is not None]
            if replies:
                send(replies)
        else:
            reply = handle(msg)
            if reply is not None:
                send(reply)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
