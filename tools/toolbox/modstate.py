"""Which tools of the library belong to a Freedom module and whether that module is on (toolbox 1.7.0, SPEC §3.6).

A module (install/freedom_modules.py of the source tree) lists its library tools in module.json provides.tools. The
installer records them here; a tool that only switched-off modules own is "unavailable" (mcp_server.py refuses
tool_run with "module <id> is off", the CLAUDE.md/AGENTS.md block leaves it out). A tool no module owns (core tools,
the owner's own tools) is not affected. No state file = nothing is managed, the library works as before 1.7.0.

State file: AIWB_TOOL_MODULES, else tool-modules.json next to install.json (AIWB_INSTALL's folder,
%LOCALAPPDATA%/AIWorkbench on Windows, ~/.config/aiwb elsewhere). It lives outside the library on purpose: the
library is a git working copy mirrored to Drive and the site, the switch is a fact of one install.
Stdlib only, Python 3.9+ (mcp_server.py imports it).
"""
import datetime
import json
import os
import re

ID_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,62}$")


def state_path():
    if os.environ.get("AIWB_TOOL_MODULES"):
        return os.environ["AIWB_TOOL_MODULES"]
    if os.environ.get("AIWB_INSTALL"):
        return os.path.join(os.path.dirname(os.path.abspath(os.environ["AIWB_INSTALL"])), "tool-modules.json")
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return os.path.join(os.environ["LOCALAPPDATA"], "AIWorkbench", "tool-modules.json")
    return os.path.join(os.path.expanduser("~"), ".config", "aiwb", "tool-modules.json")


def load(path=None):
    path = path or state_path()
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError):
        return {"schema": 1, "modules": {}}
    mods = data.get("modules") if isinstance(data, dict) else None
    return {"schema": 1, "modules": mods if isinstance(mods, dict) else {}}


def save(state, path=None):
    path = path or state_path()
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
    tmp = path + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=1)
        f.write("\n")
    os.replace(tmp, path)


def owners(state):
    """tool -> [(module id, enabled)]"""
    out = {}
    for mid, m in sorted(state.get("modules", {}).items()):
        if not isinstance(m, dict):
            continue
        for t in m.get("tools") or []:
            if isinstance(t, str):
                out.setdefault(t, []).append((mid, bool(m.get("enabled"))))
    return out


def off_reason(tool, state=None):
    """'' when the tool may run; else why not (every module that owns it is switched off)."""
    state = load() if state is None else state
    own = owners(state).get(tool)
    if not own or any(on for _, on in own):
        return ""
    return "module %s is off (turn it on in CO Web -> Host -> Modules or install/freedom_modules.py enable)" % (
        ", ".join(mid for mid, _ in own))


def set_module(mid, enabled=None, tools=None, version=None, remove=False, path=None):
    """Registers/updates one module's tools and switch; remove=True drops it (the tool files stay). Returns the entry."""
    if not ID_RE.match(mid or ""):
        raise ValueError("bad module id %r" % mid)
    state = load(path)
    mods = state["modules"]
    if remove:
        old = mods.pop(mid, None)
        save(state, path)
        return old
    m = mods.get(mid) if isinstance(mods.get(mid), dict) else {"enabled": False, "tools": []}
    if tools is not None:
        m["tools"] = sorted(set(t for t in tools if isinstance(t, str) and t))
    if enabled is not None:
        m["enabled"] = bool(enabled)
    if version is not None:
        m["version"] = str(version)
    m["updatedUtc"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    mods[mid] = m
    save(state, path)
    return m
