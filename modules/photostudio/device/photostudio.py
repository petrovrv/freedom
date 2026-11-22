# SPDX-License-Identifier: AGPL-3.0-or-later
"""Device part of the photostudio module (ext/1 device ops and device tools, run by CO Desktop).

Stdin: one JSON request {"ext":1,"kind":"op"|"tool", "op"|"tool", "args", "stateDir", ...}; stdout: the answer
(ops: JSON, tools: text for the model). Finds PhotoStudio.exe and photostudio-cli.exe in this order: config.json of
the module's state folder ({"app": "...", "cli": "..."}), PHOTOSTUDIO_APP / PHOTOSTUDIO_CLI, the default install
folder %LOCALAPPDATA%\\Programs\\PhotoStudio (cli next to the app or in cli\\), PATH. A path relative to AI-Workbench
(projects/...) is resolved through the workbench's install.json (drive.root + drive.workbench).
"""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

PHOTO_EXTS = {".jpg", ".jpeg", ".png", ".tif", ".tiff", ".cr2"}


class Fail(Exception):
    pass


def state_dir(req):
    return Path(req.get("stateDir") or os.environ.get("AIWB_MODULE_STATE") or ".")


def config(req):
    try:
        c = json.loads((state_dir(req) / "config.json").read_text(encoding="utf-8"))
        return c if isinstance(c, dict) else {}
    except (OSError, ValueError):
        return {}


def find(req, key, exe):
    """Path of PhotoStudio.exe (key app) or photostudio-cli.exe (key cli), or None."""
    cands = []
    c = config(req).get(key)
    if isinstance(c, str) and c:
        cands.append(Path(os.path.expandvars(c)))
    e = os.environ.get("PHOTOSTUDIO_" + key.upper())
    if e:
        cands.append(Path(e))
    base = Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "PhotoStudio"
    for d in (base, base / "cli"):
        cands.append(d / exe)
    if key == "cli":
        a = find(req, "app", "PhotoStudio.exe")
        if a:
            cands += [a.parent / exe, a.parent / "cli" / exe]
    w = shutil.which(exe)
    if w:
        cands.append(Path(w))
    for p in cands:
        if p.is_file():
            return p
    return None


def workbench():
    f = Path(os.environ.get("LOCALAPPDATA", "")) / "AIWorkbench" / "install.json"
    try:
        d = json.loads(f.read_text(encoding="utf-8")).get("drive") or {}
    except (OSError, ValueError):
        d = {}
    root, wb = d.get("root"), d.get("workbench") or "AI-Workbench"
    return Path(root) / wb if root else None


def local_path(p, must_exist=True):
    """An absolute path, or one relative to AI-Workbench (projects/..., no . or .. parts)."""
    if not isinstance(p, str) or not p.strip() or "\x00" in p:
        raise Fail("no file given")
    p = p.strip()
    if p.replace("\\", "/").startswith("projects/"):
        parts = p.replace("\\", "/").split("/")
        if any(x in ("", ".", "..") for x in parts):
            raise Fail("bad path: " + p)
        wb = workbench()
        if wb is None:
            raise Fail("AI-Workbench folder unknown on this computer (install.json drive.root)")
        path = wb.joinpath(*parts)
    else:
        path = Path(p)
        if not path.is_absolute():
            raise Fail("give an absolute path or one starting with projects/")
    if must_exist and not path.is_file():
        raise Fail("file not found: " + str(path))
    return path


def launch(app, files):
    flags = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP: outlives this script
    args = [str(app)] + [str(f) for f in files]
    kw = dict(cwd=str(app.parent), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)
    try:
        subprocess.Popen(args, creationflags=flags | 0x01000000, **kw)  # + CREATE_BREAKAWAY_FROM_JOB when allowed
    except OSError:
        subprocess.Popen(args, creationflags=flags, **kw)


def not_installed():
    return ("PhotoStudio is not installed on this computer: put it into %LOCALAPPDATA%\\Programs\\PhotoStudio or name "
            "PhotoStudio.exe in config.json of the module's state folder")


def cli(req, args, timeout):
    exe = find(req, "cli", "photostudio-cli.exe")
    if not exe:
        raise Fail("photostudio-cli.exe not found. " + not_installed())
    try:
        r = subprocess.run([str(exe)] + args, cwd=str(exe.parent), capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=timeout, stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        raise Fail(f"photostudio-cli did not finish in {timeout // 60} min")
    if r.returncode != 0:
        raise Fail(f"photostudio-cli failed ({r.returncode}): " + (r.stderr or r.stdout)[-1500:])
    return r.stdout.strip()


# ---------------------------------------------------------------- device ops (the app page)

def op_status(req, a):
    app, c = find(req, "app", "PhotoStudio.exe"), find(req, "cli", "photostudio-cli.exe")
    return {"installed": bool(app), "app": str(app) if app else None, "cli": str(c) if c else None,
            "hint": None if app else not_installed()}


def op_open(req, a):
    app = find(req, "app", "PhotoStudio.exe")
    if not app:
        return {"ok": False, "error": "not_installed", "hint": not_installed()}
    files = []
    if a.get("path"):
        f = local_path(a["path"])
        if f.suffix.lower() not in PHOTO_EXTS:
            raise Fail("not a photo PhotoStudio opens: " + f.name)
        files.append(f)
    launch(app, files)
    return {"ok": True, "opened": [f.name for f in files]}


# ---------------------------------------------------------------- device tools (agents)

def tool_open(req, a):
    app = find(req, "app", "PhotoStudio.exe")
    if not app:
        raise Fail(not_installed())
    files = [local_path(f) for f in (a.get("files") or [])][:20]
    launch(app, files)
    return "PhotoStudio opened" + (": " + ", ".join(f.name for f in files) if files else "") + " on this computer."


def tool_info(req, a):
    return cli(req, ["info", str(local_path(a.get("file")))], 120)


def tool_enhance(req, a):
    src = local_path(a.get("file"))
    out = local_path(a["out"], must_exist=False) if a.get("out") else src.with_name(src.stem + ".enhanced.jpg")
    agent = "codex" if a.get("agent") == "codex" else "claude"
    plan = cli(req, ["enhance", str(src), str(out), agent], 25 * 60)
    return f"Enhanced copy: {out}\nPlan: {plan[-2000:]}"


def tool_idphoto(req, a):
    src = local_path(a.get("file"))
    out = local_path(a["outDir"], must_exist=False) if a.get("outDir") else src.with_name(src.stem + ".idphoto")
    args = ["idphoto", str(src), str(out)]
    for k, dflt in (("spec", "be-eid"), ("sheet", "lab-10x15")):
        v = a.get(k) or dflt
        if not isinstance(v, str) or not v.replace("-", "").replace("_", "").isalnum():
            raise Fail(f"bad {k}: {v}")
        args.append(v)
    text = cli(req, args, 25 * 60)
    files = sorted(p.name for p in out.glob("*.jpg")) if out.is_dir() else []
    return f"Folder: {out}\nFiles: {', '.join(files) or '-'}\n{text[-3000:]}"


OPS = {"ps_status": op_status, "ps_open": op_open}
TOOLS = {"photostudio_open": tool_open, "photostudio_info": tool_info, "photostudio_enhance": tool_enhance,
         "photostudio_idphoto": tool_idphoto}


def main():
    try:
        req = json.loads(sys.stdin.read() or "{}")
    except ValueError:
        req = {}
    a = req.get("args") if isinstance(req.get("args"), dict) else {}
    if req.get("kind") == "op":
        f = OPS.get(req.get("op"))
        try:
            res = f(req, a) if f else {"ok": False, "error": "op_unknown"}
        except Fail as e:
            res = {"ok": False, "error": str(e)}
        except OSError as e:  # the program could not be started (access denied, removed after it was found, ...)
            res = {"ok": False, "error": "could not start PhotoStudio: " + str(e)}
        print(json.dumps(res, ensure_ascii=False))
        return 0
    f = TOOLS.get(req.get("tool"))
    if not f:
        print("unknown tool", file=sys.stderr)
        return 2
    try:
        print(f(req, a))
        return 0
    except Fail as e:
        print(str(e), file=sys.stderr)
        return 1
    except OSError as e:
        print("could not start PhotoStudio: " + str(e), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
