# SPDX-License-Identifier: AGPL-3.0-or-later
# Freedom F10 rule (SPEC §3.6 principle 1): the core knows no module. For every module in modules/ its id, the ops,
# uploads and routes of its server part and its table prefix m_<id>_ must not appear in the code of the core. Not core:
# modules/, tests, docs and Markdown (changelogs keep history), the release and installer folders, and the tool library
# (tools are named by modules, not the other way round). Files listed for a module id in moduleIdAllow of
# release/freedom-export.json (each with its reason there, e.g. legacy wire tokens equal to a module id) are excused.
import fnmatch
import json
import pathlib
import re
import subprocess
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]
NOT_CORE = re.compile(r"^(modules/|tools/|docs/|release/|install/|lab/|.*/tests?/|.*\.selftest\.ts$|.*\.md$|.*/CHANGELOG|.*\.json$)")
CODE = re.compile(r"\.(php|ts|tsx|js|mjs|cs|py|ps1|sh|sql|css|html)$")
# platform words equal to a module id: the HTML <canvas> element and drawing on it
DOM_IDIOMS = re.compile(r"createElement\(\s*['\"]canvas['\"]\s*\)|HTMLCanvasElement|OffscreenCanvas|<canvas|CanvasRenderingContext2D")


def module_terms() -> dict:
    """module id -> terms the core must not contain."""
    out = {}
    for mj in sorted((REPO / "modules").glob("*/module.json")):
        m = json.loads(mj.read_text(encoding="utf-8"))
        srv = m.get("server") or {}
        terms = {m["id"], "m_" + m["id"].replace("-", "_") + "_"}
        for ops in (srv.get("ops") or {}).values():
            terms.update(ops or [])
        terms.update(srv.get("uploads") or [])
        terms.update(f"{r}.php" for r in srv.get("routes") or [])
        # ext/1 names (modules/EXT1.md): chat mode keys and their document fields, side-job keys, page addresses
        prov = m.get("provides") or {}
        for md in prov.get("modes") or []:
            k = md.get("key")
            if k:
                terms.update({k, k + "Log", k + "Default", k + "Doc"})
        terms.update(s["key"] for s in prov.get("sideJobs") or [] if s.get("key"))
        terms.update(u["path"] for u in prov.get("ui") or [] if u.get("path"))
        # ext/1 provides.client: the id of a module's browser extension (CO Web gets it at build time, the server from
        # the modules table)
        if isinstance(prov.get("client"), dict) and prov["client"].get("id"):
            terms.add(str(prov["client"]["id"]))
        out[m["id"]] = sorted(t for t in terms if len(t) >= 4)
    return out


def core_files() -> list:
    names = subprocess.run(["git", "ls-files"], cwd=REPO, capture_output=True, text=True, check=True).stdout.splitlines()
    return [n for n in names if CODE.search(n) and not NOT_CORE.match(n)]


def allowed() -> dict:
    """module id -> globs of core files excused for it (moduleIdAllow of the export config; '*' = every module)."""
    cfg = json.loads((REPO / "release" / "freedom-export.json").read_text(encoding="utf-8"))
    return {k: list(v or []) for k, v in (cfg.get("moduleIdAllow") or {}).items()}


def hits() -> list:
    terms = module_terms()
    allow = allowed()
    pats = {mid: re.compile(r"(?<![A-Za-z0-9_-])(" + "|".join(re.escape(t) for t in ts) + r")(?![A-Za-z0-9_])") for mid, ts in terms.items()}
    found = []
    for rel in core_files():
        try:
            text = (REPO / rel).read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for no, line in enumerate(text.splitlines(), 1):
            line = DOM_IDIOMS.sub("", line)
            for mid, p in pats.items():
                if any(fnmatch.fnmatch(rel, g) for g in allow.get(mid, []) + allow.get("*", [])):
                    continue
                m = p.search(line)
                if m:
                    found.append(f"{rel}:{no}: {m.group(1)} (module {mid})")
    return found


class CoreFreeOfModulesTest(unittest.TestCase):
    def test_core_names_no_module(self):
        found = hits()
        self.assertEqual(found, [], "the core names modules:\n" + "\n".join(found[:80]))


if __name__ == "__main__":
    print("\n".join(hits()) or "core is free of module names")
