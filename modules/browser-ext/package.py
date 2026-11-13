# SPDX-License-Identifier: AGPL-3.0-or-later
"""Packages the browser extension of the module browser-ext (Freedom F10) as a zip for "Load unpacked" on another PC or
for a store upload: the files of extension/ plus site.js (the CO site of this install; made by
install/aiwb_install.py ext-config when missing). The manifest version must equal the module version (module.json),
so the module and the extension are released together.

  python modules/browser-ext/package.py [--out DIR] [--no-site]
"""
import argparse
import json
import pathlib
import subprocess
import sys
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
EXT = HERE / "extension"
REPO = HERE.parents[1]
# the extension runs only these; site.js is generated per install
FILES = ("manifest.json", "background.js", "page.js", "panel.html", "panel.css", "panel.js")


def problems() -> list:
    p = []
    mod = json.loads((HERE / "module.json").read_text(encoding="utf-8"))
    man = json.loads((EXT / "manifest.json").read_text(encoding="utf-8"))
    if man.get("version") != mod.get("version"):
        p.append(f"extension version {man.get('version')} != module version {mod.get('version')}")
    for f in FILES:
        if not (EXT / f).is_file():
            p.append(f"missing extension/{f}")
    for size in ("16", "32", "48", "128"):
        if not (EXT / "icons" / f"{size}.png").is_file():
            p.append(f"missing extension/icons/{size}.png")
    declared = {t["name"] for t in mod.get("provides", {}).get("deviceTools", [])}
    panel = (EXT / "panel.js").read_text(encoding="utf-8")
    import re
    offered = set(re.findall(r"name: '(browser_[a-z_]+)'", panel))
    if declared != offered:
        p.append(f"deviceTools of module.json {sorted(declared ^ offered)} differ from TOOLS of panel.js")
    return p


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=pathlib.Path, default=HERE / "dist")
    ap.add_argument("--no-site", action="store_true", help="leave site.js out (the user runs ext-config on the target PC)")
    ap.add_argument("--check", action="store_true", help="only check the extension against module.json")
    a = ap.parse_args(argv)
    p = problems()
    if p or a.check:
        print(json.dumps({"ok": not p, "problems": p}, indent=1))
        return 1 if p else 0
    site = EXT / "site.js"
    if not a.no_site and not site.is_file():
        r = subprocess.run([sys.executable, str(REPO / "install" / "aiwb_install.py"), "ext-config", str(site)])
        if r.returncode != 0:
            print("error: no site.js and ext-config failed (install.json site.url?); use --no-site", file=sys.stderr)
            return 1
    version = json.loads((EXT / "manifest.json").read_text(encoding="utf-8"))["version"]
    a.out.mkdir(parents=True, exist_ok=True)
    out = a.out / f"ai-workbench-extension-{version}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for f in FILES:
            z.write(EXT / f, f)
        for icon in sorted((EXT / "icons").glob("*.png")):
            z.write(icon, f"icons/{icon.name}")
        if not a.no_site:
            z.write(site, "site.js")
    print(json.dumps({"ok": True, "zip": str(out), "version": version, "site": not a.no_site}, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
