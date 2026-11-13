# SPDX-License-Identifier: AGPL-3.0-or-later
"""Freedom F10 (SPEC §3.6 principle 1): the core knows no module by name.

Scans the core files of a commit (git-tracked, outside modules/ and the paths in release/core-names.json "outside") for
the names of the modules in modules/*/module.json (module id, its server ops, device tools, library tools, gateway
model ids) and for the extra terms of the config (e.g. employer brands). Legacy wire tokens that deployed Bridges,
devices or old rows still use are masked first (config "legacyWire": exact strings, case-sensitive).

What is left today is pinned in release/core-names-baseline.json (path -> hits): the check fails when a file has more
hits than its baseline or a file without a baseline gets hits, so the core only moves towards zero. After a cleanup:
--update writes the lower counts.

  python release/check_core_names.py [--rev HEAD] [--update] [--list]
Exit codes: 0 clean (or not worse than the baseline), 2 new hits, 1 error.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import pathlib
import re
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[1]
CONFIG = REPO / "release" / "core-names.json"
BASELINE = REPO / "release" / "core-names-baseline.json"


def git(*args: str) -> str:
    return subprocess.run(["git", "-C", str(REPO), *args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", check=True).stdout


def module_terms(rev: str) -> list[str]:
    """Names of gateway modules (id, model ids, device tools; minimum length 4); the ids of all modules are checked by
    release/tests/test_core_free_of_modules.py."""
    terms = set()
    for path in git("ls-tree", "-r", "--name-only", rev, "--", "modules").splitlines():
        if not re.fullmatch(r"modules/[^/]+/module\.json", path):
            continue
        try:
            m = json.loads(git("show", f"{rev}:{path}"))
        except json.JSONDecodeError:
            continue
        prov = m.get("provides") or {}
        if prov.get("gateway") is not None:   # other module ids: release/tests/test_core_free_of_modules.py
            terms.add(str(m.get("id", "")))   # a gateway module's model ids and tools (ops, ids of other modules:
            for md in prov.get("models", []) or []:   # release/tests/test_core_free_of_modules.py)
                terms.add(str(md.get("id", "")))
            for t in prov.get("deviceTools", []) or []:
                terms.add(str(t.get("name", "")))
    return sorted(t for t in terms if len(t) >= 4)


def load_config() -> dict:
    return json.loads(CONFIG.read_text(encoding="utf-8"))


def scan(rev: str = "HEAD") -> dict[str, list[str]]:
    """path -> list of 'line: term' hits in the core files of rev."""
    cfg = load_config()
    terms = module_terms(rev) + list(cfg.get("terms", []))
    generic = set(cfg.get("genericWords", []))
    terms = [t for t in terms if t.lower() not in generic]
    rx = re.compile("|".join(re.escape(t) for t in sorted(set(terms), key=len, reverse=True)), re.IGNORECASE)
    wire = sorted(cfg.get("legacyWire", []), key=len, reverse=True)
    outside = cfg.get("outside", [])
    hits: dict[str, list[str]] = {}
    args = ["grep", "-n", "-I", "-i", "-F"]
    for t in sorted(set(terms)):
        args += ["-e", t]
    try:
        out = git(*args, rev, "--")
    except subprocess.CalledProcessError as e:
        if e.returncode == 1:   # no match at all
            return hits
        raise
    for row in out.splitlines():
        # <rev>:<path>:<line>:<text>
        _, path, n, line = row.split(":", 3)
        if path.startswith("modules/") or any(fnmatch.fnmatch(path, o) or (o.endswith("/") and path.startswith(o))
                                              for o in outside):
            continue
        if not re.search(r"\.(cs|php|ts|tsx|js|mjs|py|ps1|cmd|sh|json|css|html|sql|csproj|props)$", path):
            continue
        masked = line
        for w in wire:
            masked = masked.replace(w, " ")
        for mt in rx.finditer(masked):
            hits.setdefault(path, []).append(f"{n}: {mt.group(0)}")
    return hits


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rev", default="HEAD")
    ap.add_argument("--update", action="store_true", help="write the current counts as the new baseline")
    ap.add_argument("--list", action="store_true", help="print every hit")
    a = ap.parse_args(argv)
    try:
        hits = scan(a.rev)
    except (subprocess.CalledProcessError, OSError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    counts = {p: len(h) for p, h in sorted(hits.items())}
    if a.update:
        BASELINE.write_text(json.dumps(counts, indent=1, sort_keys=True) + "\n", encoding="utf-8")
        print(f"baseline: {sum(counts.values())} hits in {len(counts)} files")
        return 0
    base = json.loads(BASELINE.read_text(encoding="utf-8")) if BASELINE.is_file() else {}
    worse = {p: (c, base.get(p, 0)) for p, c in counts.items() if c > base.get(p, 0)}
    if a.list:
        for p, h in sorted(hits.items()):
            for x in h:
                print(f"{p}:{x}")
    better = {p: (counts.get(p, 0), b) for p, b in base.items() if counts.get(p, 0) < b}
    print(f"core names: {sum(counts.values())} hits in {len(counts)} files (baseline {sum(base.values())} in {len(base)})")
    if better:
        print(f"lower than the baseline in {len(better)} files: run with --update to pin it")
    for p, (c, b) in sorted(worse.items()):
        print(f"NEW: {p}: {c} hits (baseline {b}): " + "; ".join(hits[p][:5]))
    return 2 if worse else 0


if __name__ == "__main__":
    sys.exit(main())
