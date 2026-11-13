# SPDX-License-Identifier: AGPL-3.0-or-later
"""Reverse-task check of Freedom F10 (SPEC §3.6): is this installation = the Freedom core + its modules + its data?

Lists the core (this source tree outside modules/), every module folder at home (modules/ of the tree, install.json
modules.dirs, AIWB_MODULE_DIRS) with its signature, what the server has installed and enabled, which tools of the
library each module owns (toolbox module state), and the private layer (install.json, Drive context, keyring entries;
presence only, no values). Then it checks an inventory (default release/freedom-compose.json, else --inventory FILE):
  - every non-core feature has a module folder (any of the listed ids) and the module owns the listed tools;
  - every tool of the library is a core tool (release/freedom-export.json tools + inventory coreTools) or owned by a
    module (no orphan tools);
  - what principle 1 moves out of the core is gone from the core (coreMustNot);
  - the private layer is there.
Modules that exist as folders but are not installed (or not enabled) on the server are listed as "to do", not as gaps.

  python install/freedom_compose.py [--offline] [--inventory FILE] [--json]
Exit codes: 0 no gaps, 1 error, 2 gaps (listed).
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent
sys.path.insert(0, str(HERE))
import aiwb_install  # noqa: E402
import freedom_modules as fm  # noqa: E402

DEFAULT_INVENTORY = REPO / "release" / "freedom-compose.json"
EXPORT_CONFIG = REPO / "release" / "freedom-export.json"


def core_files() -> list:
    """Tracked files of this tree outside modules/ (the core)."""
    r = subprocess.run(["git", "ls-files", "-z"], cwd=REPO, capture_output=True)
    if r.returncode:
        raise RuntimeError("git ls-files: " + r.stderr.decode(errors="replace").strip())
    return [p for p in r.stdout.decode("utf-8").split("\0") if p and not p.startswith("modules/")]


def library_tools(lib: pathlib.Path) -> dict:
    """name -> version of every tool of the library (folders with SKILL.md; _archive and dot folders skipped)."""
    out = {}
    if lib.is_dir():
        for d in sorted(lib.iterdir()):
            if d.is_dir() and not d.name.startswith((".", "_")) and (d / "SKILL.md").is_file():
                out[d.name] = fm.tool_version(lib, d.name)
    return out


def keyring_has(service: str, account: str) -> bool:
    try:
        import keyring
        return keyring.get_password(service, account) is not None
    except Exception:  # noqa: BLE001 - no keyring backend = not there
        return False


def compose(inv: dict, offline: bool = False) -> dict:
    gaps, todo, notes = [], [], []
    core = core_files()
    lib = fm.tools_root()
    tools = library_tools(lib)

    # modules at home, with signature and origin
    mods = {}
    for mid, (d, m) in sorted(fm.local_modules().items()):
        v = fm.verify(d)
        mods[mid] = {"id": mid, "version": str(m.get("version", "")), "dir": str(d),
                     "origin": "tree" if d.parent == REPO / "modules" else "private",
                     "trust": "standard" if v["standard"] else ("own/trusted" if v["trusted"] else "unsigned"),
                     "tools": [t["name"] for t in fm.tool_entries(m)],
                     "services": [s.get("id") for s in (m.get("provides") or {}).get("services", []) or []],
                     "problems": fm.check(d)}
        if mods[mid]["problems"]:
            gaps.append(f"module {mid}: manifest problems: " + "; ".join(mods[mid]["problems"]))
        if mods[mid]["trust"] == "unsigned":
            todo.append(f"module {mid}: sign it (freedom_modules.py sign)")
        for t in mods[mid]["tools"]:
            if t not in tools:
                gaps.append(f"module {mid}: tool {t} is not in the library")

    # what the server and the tool library say
    live = None
    if not offline:
        try:
            live = {x["id"]: x for x in fm.host_call({"action": "list"}).get("modules", [])}
        except Exception as e:  # noqa: BLE001 - reported, the rest of the check still runs
            notes.append(f"server not asked: {e}")
    try:
        st = fm.toolbox("modules", "--json")
        tool_state = st.get("modules", {}) if st.get("ok") else {}
    except fm.Fail as e:
        tool_state = {}
        notes.append(f"tool states not read: {e}")
    for mid, x in mods.items():
        if live is not None:
            srv = live.get(mid)
            x["server"] = None if srv is None else {"version": srv.get("version"), "enabled": bool(srv.get("enabled"))}
        ts = tool_state.get(mid)
        x["toolState"] = None if ts is None else ("on" if ts.get("enabled") else "off")
    if live is not None:
        for mid in sorted(set(live) - set(mods)):
            gaps.append(f"module {mid} is installed on the server but has no folder at home (cannot update or check it)")

    # features of the inventory
    owned = {t: [mid for mid, x in mods.items() if t in x["tools"]] for t in tools}
    features = []
    for f in inv.get("features", []):
        have = [mid for mid in f.get("modules", []) if mid in mods]
        row = {"feature": f["feature"], "class": f.get("class", ""), "modules": have, "status": "covered"}
        if not have:
            row["status"] = "future" if f.get("future") else "gap"
            if not f.get("future"):
                gaps.append(f"feature '{f['feature']}': no module folder ({' / '.join(f.get('modules', []))})")
        for t in f.get("tools", []):
            if not any(t in mods[mid]["tools"] for mid in have):
                row["status"] = "gap"
                gaps.append(f"feature '{f['feature']}': tool {t} is not owned by its module")
        for mid in have:
            srv = mods[mid].get("server", "?")
            if srv is None:
                todo.append(f"install module {mid} ({mods[mid]['origin']}): freedom_modules.py install {mods[mid]['dir']}"
                            + ("" if f.get("defaultOff") else " --enable"))
            elif isinstance(srv, dict) and not srv["enabled"] and not f.get("defaultOff"):
                todo.append(f"module {mid} is installed but off")
            elif isinstance(srv, dict) and srv["version"] != mods[mid]["version"]:
                todo.append(f"module {mid}: server has {srv['version']}, folder {mods[mid]['version']} (update)")
            if mods[mid]["tools"] and mods[mid]["toolState"] is None:
                todo.append(f"module {mid}: its tools are not recorded in the library yet (install or sync)")
        features.append(row)

    # tools: core or owned by a module
    try:
        export_tools = set(json.loads(EXPORT_CONFIG.read_text(encoding="utf-8")).get("tools", []))
    except (OSError, ValueError):
        export_tools = set()
    core_tools = export_tools | set(inv.get("coreTools", []))
    for t in sorted(core_tools - set(tools)):
        notes.append(f"core tool {t} is not in this library")
    orphans = [t for t in tools if t not in core_tools and not owned.get(t)]
    for t in orphans:
        gaps.append(f"tool {t} is neither a core tool nor owned by a module")
    for t in sorted(t for t in core_tools if owned.get(t)):
        notes.append(f"tool {t} is a core tool and also listed by module(s) {', '.join(owned[t])}")

    # principle 1: nothing of a module left in the core
    core_set = set(core)
    for c in inv.get("coreMustNot", []):
        if c.get("path") and c["path"] in core_set:
            gaps.append(f"core still has {c['path']} ({c['what']})")
        if c.get("regex"):
            rx = re.compile(c["regex"])
            for p in c.get("files", []):
                f = REPO / p
                if p in core_set and f.is_file() and rx.search(f.read_text(encoding="utf-8", errors="replace")):
                    gaps.append(f"core {p} still matches /{c['regex']}/ ({c['what']})")

    # the private layer (presence only)
    private = []
    try:
        excluded = json.loads(EXPORT_CONFIG.read_text(encoding="utf-8")).get("exclude", [])
    except (OSError, ValueError):
        excluded = []
    for pv in inv.get("private", []):
        ok = True
        if pv.get("installJson"):
            ok = aiwb_install.path().is_file() and not aiwb_install.check(aiwb_install.load())
        if pv.get("workbench"):
            ok = (aiwb_install.workbench_root() / pv["workbench"]).exists()
        if pv.get("keyring"):
            ok = keyring_has(*pv["keyring"])
        if pv.get("exportExcluded"):
            missing = [p for p in pv["exportExcluded"] if p not in excluded]
            ok = not missing
            if missing:
                gaps.append(f"private file(s) not excluded from the export: {', '.join(missing)}")
        private.append({"what": pv["what"], "ok": ok, **({"note": pv["note"]} if pv.get("note") else {})})
        if not ok and not pv.get("exportExcluded"):
            gaps.append(f"private layer: {pv['what']} missing")

    return {"core": {"files": len(core), "tools": sorted(core_tools & set(tools))},
            "modules": list(mods.values()), "features": features, "private": private,
            "library": {"root": str(lib), "tools": len(tools), "orphans": orphans},
            "gaps": gaps, "todo": sorted(set(todo)), "notes": notes}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--inventory", type=pathlib.Path, default=DEFAULT_INVENTORY)
    ap.add_argument("--offline", action="store_true", help="do not ask the server which modules it has")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    try:
        inv = json.loads(a.inventory.read_text(encoding="utf-8")) if a.inventory.is_file() else {}
        r = compose(inv, a.offline)
    except Exception as e:  # noqa: BLE001 - CLI boundary
        print(f"error: {e}", file=sys.stderr)
        return 1
    if a.json:
        print(json.dumps(r, ensure_ascii=False, indent=1))
    else:
        print(f"core: {r['core']['files']} files, {len(r['core']['tools'])} core tools")
        for m in r["modules"]:
            srv = m.get("server", "?")
            srv = "not installed" if srv is None else ("?" if srv == "?" else f"server {srv['version']} {'on' if srv['enabled'] else 'off'}")
            print(f"  module {m['id']} {m['version']} [{m['origin']}, {m['trust']}] {srv}; tools {m['toolState'] or '-'}: "
                  f"{', '.join(m['tools']) or '-'}")
        for f in r["features"]:
            print(f"  {f['status']:8} {f['feature']} ({', '.join(f['modules']) or '-'})")
        for p in r["private"]:
            print(f"  private {'ok ' if p['ok'] else 'MISSING'} {p['what']}")
        for n in r["notes"]:
            print(f"note: {n}")
        for t in r["todo"]:
            print(f"to do: {t}")
        for g in r["gaps"]:
            print(f"GAP: {g}")
        print(f"{len(r['gaps'])} gap(s), {len(r['todo'])} to do")
    return 2 if r["gaps"] else 0


if __name__ == "__main__":
    sys.exit(main())
