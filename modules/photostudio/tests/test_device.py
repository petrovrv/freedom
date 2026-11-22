# SPDX-License-Identifier: AGPL-3.0-or-later
"""python modules/photostudio/tests/test_device.py [--live] - checks of device/photostudio.py.

Without --live: the request protocol and path rules with a fake install (no PhotoStudio needed).
--live: also asks ps_status and photostudio_info of the real install named by PHOTOSTUDIO_APP / PHOTOSTUDIO_CLI.
"""
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "device" / "photostudio.py"
fail = 0


def ok(name, cond):
    global fail
    fail += 0 if cond else 1
    print(("ok   " if cond else "FAIL ") + name)


def run(req, env=None):
    e = dict(os.environ)
    e.update(env or {})
    r = subprocess.run([sys.executable, "-I", str(SCRIPT)], input=json.dumps(req), capture_output=True, text=True,
                       encoding="utf-8", env=e, timeout=300)
    return r.returncode, r.stdout.strip(), r.stderr.strip()


with tempfile.TemporaryDirectory() as tmp:
    t = Path(tmp)
    clean = {"PHOTOSTUDIO_APP": "", "PHOTOSTUDIO_CLI": "", "LOCALAPPDATA": str(t / "lad"), "PATH": os.environ.get("SystemRoot", "C:\\Windows")}
    code, out, _ = run({"kind": "op", "op": "ps_status", "args": {}, "stateDir": str(t)}, clean)
    st = json.loads(out)
    ok("status without an install", code == 0 and st["installed"] is False and "not installed" in st["hint"])
    code, out, _ = run({"kind": "op", "op": "ps_open", "args": {}, "stateDir": str(t)}, clean)
    ok("open without an install says so", json.loads(out).get("error") == "not_installed")
    code, out, _ = run({"kind": "op", "op": "rm_rf", "args": {}, "stateDir": str(t)}, clean)
    ok("unknown op refused", json.loads(out).get("error") == "op_unknown")

    # a fake install named in config.json of the state folder
    (t / "app").mkdir()
    (t / "app" / "PhotoStudio.exe").write_bytes(b"")
    (t / "app" / "photostudio-cli.exe").write_bytes(b"")
    (t / "config.json").write_text(json.dumps({"app": str(t / "app" / "PhotoStudio.exe")}), encoding="utf-8")
    code, out, _ = run({"kind": "op", "op": "ps_status", "args": {}, "stateDir": str(t)}, clean)
    st = json.loads(out)
    ok("app from config.json", st["installed"] and st["app"].endswith("PhotoStudio.exe"))
    ok("cli found next to the app", (st["cli"] or "").endswith("photostudio-cli.exe"))

    code, _, err = run({"kind": "tool", "tool": "photostudio_info", "args": {"file": "projects/../secret.jpg"}, "stateDir": str(t)}, clean)
    ok("dot-dot path refused", code == 1 and "bad path" in err)
    code, _, err = run({"kind": "tool", "tool": "photostudio_info", "args": {"file": "relative\\x.jpg"}, "stateDir": str(t)}, clean)
    ok("relative path outside projects/ refused", code == 1 and "absolute" in err)
    code, _, err = run({"kind": "tool", "tool": "photostudio_info", "args": {"file": str(t / "none.jpg")}, "stateDir": str(t)}, clean)
    ok("missing file refused", code == 1 and "not found" in err)
    # projects/ paths resolve through install.json
    (t / "lad" / "AIWorkbench").mkdir(parents=True)
    (t / "lad" / "AIWorkbench" / "install.json").write_text(json.dumps({"drive": {"root": str(t / "drive"), "workbench": "AW"}}), encoding="utf-8")
    p = t / "drive" / "AW" / "projects" / "P" / "a.txt"
    p.parent.mkdir(parents=True)
    p.write_text("x", encoding="utf-8")
    code, out, _ = run({"kind": "op", "op": "ps_open", "args": {"path": "projects/P/a.txt"}, "stateDir": str(t)}, clean)
    ok("workbench path resolved, non-photo refused", "not a photo" in json.loads(out).get("error", ""))
    code, _, err = run({"kind": "tool", "tool": "photostudio_idphoto", "args": {"file": str(p), "spec": "be eid;x"}, "stateDir": str(t)}, clean)
    ok("bad spec refused", code == 1 and "bad spec" in err)
    code, _, err = run({"kind": "tool", "tool": "nope", "args": {}}, clean)
    ok("unknown tool fails", code == 2)

if "--live" in sys.argv:
    with tempfile.TemporaryDirectory() as tmp:
        code, out, _ = run({"kind": "op", "op": "ps_status", "args": {}, "stateDir": tmp})
        st = json.loads(out)
        ok("live: installed", st["installed"] and bool(st["cli"]))
        img = Path(os.environ.get("PHOTOSTUDIO_TEST_IMAGE", ""))
        if img.is_file():
            code, out, err = run({"kind": "tool", "tool": "photostudio_info", "args": {"file": str(img)}, "stateDir": tmp})
            print("     " + (out or err)[:200])
            ok("live: info", code == 0)

print("FAILED" if fail else "all ok")
sys.exit(1 if fail else 0)
