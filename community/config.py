# SPDX-License-Identifier: AGPL-3.0-or-later
"""Where things live: data folder, workbench id, install.json, the tool library.

Data folder (archive.sqlite, community.json): AIWB_COMMUNITY_DIR, else next to install.json
(%LOCALAPPDATA%/AIWorkbench/community on Windows, ~/.config/aiwb/community elsewhere). Local disk on purpose: SQLite
on a synced Drive folder breaks (the git-on-Drive lesson of 2026-10-15).
"""
from __future__ import annotations

import json
import os
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent


def install_path() -> Path:
    if os.environ.get("AIWB_INSTALL"):
        return Path(os.environ["AIWB_INSTALL"])
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"]) / "AIWorkbench" / "install.json"
    return Path.home() / ".config" / "aiwb" / "install.json"


def load_install(path: Path | None = None) -> dict:
    p = path or install_path()
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def data_dir() -> Path:
    if os.environ.get("AIWB_COMMUNITY_DIR"):
        d = Path(os.environ["AIWB_COMMUNITY_DIR"])
    else:
        d = install_path().parent / "community"
    d.mkdir(parents=True, exist_ok=True)
    return d


def tools_dir() -> Path:
    """The tool library (toolbox, read-guard, aiwb-budget, aiwb-dossier): AIWB_TOOLS, else the usual places."""
    cands = [os.environ.get("AIWB_TOOLS"), r"C:\AIWorkbench\tools", r"G:\My Drive\AI-Workbench\tools",
             str(Path.home() / "AIWorkbench" / "tools")]
    for c in cands:
        if c and (Path(c) / "toolbox").is_dir():
            return Path(c)
    return Path(cands[1])


DEFAULTS = {
    "relays": [],                       # ws(s):// URLs; several at once (SPEC §6.1)
    "socialSharePercent": 3.0,          # SPEC §6.2
    "tickCostPercent": 0.3,             # estimated share of the week one tick spends
    "volunteerSharePercent": 10.0,      # SPEC §7.1
    "budgetAgent": "claude",
    "autoPublish": [],                  # kinds the owner allowed to publish without a yes (card, report, discussion)
    "filterAllow": [],                  # owner's exceptions for false positives of the outbound filter
    "extraNames": [], "extraDomains": [], "extraPaths": [],
    "dossier": True,                    # read names from aiwb-dossier for the filter
    "allowlist": "",                    # relay allowlist JSON (authors + GitHub identities); default allowlist.json here
    "fetchOverlapSeconds": 172800,      # tick re-reads 2 days behind the relay cursor
}


class SettingsError(RuntimeError):
    """community.json exists but cannot be used: never replaced silently (it holds the workbench id and policy)."""


def atomic_write(path: Path, text: str, backup: bool = True) -> None:
    """Write via a temp file in the same folder + fsync + os.replace (never a half-written file); with backup the
    previous version is kept as <name>.bak."""
    path = Path(path)
    tmp = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    if backup and path.exists():
        bak = path.with_name(path.name + ".bak")
        with open(path, "rb") as src, open(bak, "wb") as dst:
            dst.write(src.read())
    os.replace(tmp, path)


def load_settings(ddir: Path | None = None) -> dict:
    """community.json in the data folder. Created (with a new workbench id) only when it does not exist; an existing
    file that cannot be read, is not a JSON object or has no workbench id raises SettingsError and is left as is."""
    ddir = ddir or data_dir()
    p = ddir / "community.json"
    if p.exists():
        try:
            s = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            raise SettingsError(f"{p} cannot be read ({type(e).__name__}); fix it or restore {p.name}.bak") from None
        if not isinstance(s, dict) or not isinstance(s.get("workbenchId"), str) or not s["workbenchId"]:
            raise SettingsError(f"{p} has no workbench id; fix it or restore {p.name}.bak (a new id is never made "
                                "for an existing file)")
    else:
        inst = load_install()
        s = {"workbenchId": (inst.get("community") or {}).get("workbenchId") or "wb-" + uuid.uuid4().hex[:12]}
    changed = not p.exists()
    for k, v in DEFAULTS.items():
        if k not in s:
            s[k] = v
            changed = True
    if changed:
        save_settings(s, ddir)
    return s


def save_settings(s: dict, ddir: Path | None = None) -> None:
    ddir = ddir or data_dir()
    if not isinstance(s.get("workbenchId"), str) or not s["workbenchId"]:
        raise SettingsError("refusing to save settings without a workbench id")
    atomic_write(ddir / "community.json", json.dumps(s, ensure_ascii=False, indent=1))
