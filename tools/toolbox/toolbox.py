"""Manage the AI Workbench tool library: list, search, check, scan, index, new, commit, archive."""
import argparse
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import json
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import security  # noqa: E402  1.5.0: static security scan

# 1.3.0: the working copy is C:\AIWorkbench\tools (local disk, origin = GitHub); G:\My Drive\AI-Workbench\tools is a
# read-only mirror for running tools. Whichever copy this script runs from, writes go to the local working copy.
LOCAL = Path(os.environ.get("AIWB_TOOLS") or r"C:\AIWorkbench\tools")
MIRROR = Path(os.environ.get("AIWB_TOOLS_MIRROR") or r"G:\My Drive\AI-Workbench\tools")
ROOT = LOCAL if (LOCAL / ".git").is_dir() else Path(__file__).resolve().parent.parent
INDEX = ROOT / "INDEX.md"
CLAUDE_MD = Path.home() / ".claude" / "CLAUDE.md"
# Global instructions of Codex (GPT agents) get the same list (1.2.0).
AGENTS_MD = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex") / "AGENTS.md"
BEGIN = "<!-- aiwb-tools-index:begin -->"
END = "<!-- aiwb-tools-index:end -->"
REQUIRED = ("name", "description", "version", "entry")
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9-]*$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+$")


def parse_frontmatter(path: Path) -> dict:
    """Parse the simple key: value frontmatter of a SKILL.md (no YAML dependency)."""
    text = path.read_text(encoding="utf-8")
    m = re.match(r"^---\r?\n(.*?)\r?\n---", text, re.S)
    if not m:
        return {}
    meta = {}
    for line in m.group(1).splitlines():
        if ":" in line and not line.startswith((" ", "-")):
            key, value = line.split(":", 1)
            meta[key.strip()] = value.strip()
    return meta


def tools() -> list[tuple[Path, dict]]:
    result = []
    for d in sorted(ROOT.iterdir()):
        if d.is_dir() and not d.name.startswith(("_", ".")) and (d / "SKILL.md").exists():
            result.append((d, parse_frontmatter(d / "SKILL.md")))
    return result


def problems(d: Path, meta: dict) -> list[str]:
    errs = [f"missing field '{k}'" for k in REQUIRED if not meta.get(k)]
    if meta.get("name") and meta["name"] != d.name:
        errs.append(f"name '{meta['name']}' != folder '{d.name}'")
    if not NAME_RE.match(d.name):
        errs.append("folder name must be lowercase latin, digits, hyphens")
    if meta.get("version") and not VERSION_RE.match(meta["version"]):
        errs.append(f"version '{meta['version']}' is not MAJOR.MINOR.PATCH")
    if meta.get("entry") and not (d / meta["entry"]).exists():
        errs.append(f"entry '{meta['entry']}' not found")
    return errs


def index_lines() -> list[str]:
    lines = []
    for d, meta in tools():
        lines.append(f"- `{d.name}` {meta.get('version', '?')} — {meta.get('description', '')}")
    return lines or ["- (пусто)"]


def cmd_list(args) -> int:
    print("\n".join(index_lines()))
    return 0


def cmd_search(args) -> int:
    words = [w.lower() for w in args.words]
    found = 0
    for d, meta in tools():
        text = (d / "SKILL.md").read_text(encoding="utf-8").lower()
        if all(w in text for w in words):
            print(f"{d.name} {meta.get('version', '?')} — {meta.get('description', '')}")
            print(f"  {d / 'SKILL.md'}")
            found += 1
    if not found:
        print("nothing found")
    return 0


def run_scan(dirs: list[Path], verbose: bool = False, as_json: bool = False) -> int:
    """Security scan (security.py); prints errors and warnings, returns the number of unexcused errors."""
    findings = []
    for d in dirs:
        findings += security.scan_tool(d)
    total = security.summarize(findings)
    if as_json:
        print(json.dumps({"summary": total, "findings": [f.as_dict() for f in findings]}, ensure_ascii=False, indent=1))
        return total["errors"]
    for f in findings:
        if verbose or not f.excused:
            print(security.format_finding(f))
    print(f"security scan: {len(dirs)} tools, {total['errors']} errors, {total['warnings']} warnings, "
          f"{total['excused']} excused (SKILL.md security:)")
    return total["errors"]


def cmd_check(args) -> int:
    bad = 0
    for d, meta in tools():
        errs = problems(d, meta)
        for e in errs:
            print(f"{d.name}: {e}")
        bad += bool(errs)
    print(f"{len(tools())} tools, {bad} with problems")
    errors = run_scan([d for d, _ in tools()])
    return 1 if bad or errors else 0


def cmd_scan(args) -> int:
    all_tools = {d.name: d for d, _ in tools()}
    if args.all or not args.names:
        dirs = list(all_tools.values())
    else:
        missing = [n for n in args.names if n not in all_tools]
        if missing:
            print("no such tools: " + ", ".join(missing))
            return 1
        dirs = [all_tools[n] for n in args.names]
    return 1 if run_scan(dirs, verbose=args.verbose, as_json=args.json) else 0


def write_index() -> None:
    body = "\n".join(index_lines())
    INDEX.write_text(
        "# Инструменты AI Workbench\n\n"
        "Генерируется `python toolbox/toolbox.py index`; вручную не править. "
        "Правила — `README.md`.\n\n" + body + "\n",
        encoding="utf-8",
    )
    # Mirror the index into the global CLAUDE.md and the Codex AGENTS.md so every agent session sees it.
    for md in (CLAUDE_MD, AGENTS_MD):
        if not md.exists():
            continue
        text = md.read_text(encoding="utf-8")
        if BEGIN in text and END in text:
            head, rest = text.split(BEGIN, 1)
            _, tail = rest.split(END, 1)
            md.write_text(f"{head}{BEGIN}\n{body}\n{END}{tail}", encoding="utf-8")
        elif md == AGENTS_MD:
            md.write_text(f"{text.rstrip()}\n\nCurrent tools:\n\n{BEGIN}\n{body}\n{END}\n", encoding="utf-8")


def cmd_index(args) -> int:
    write_index()
    print(f"written {INDEX}")
    return 0


def cmd_new(args) -> int:
    name = args.name
    if not NAME_RE.match(name):
        print("name must be lowercase latin, digits, hyphens")
        return 1
    dest = ROOT / name
    if dest.exists():
        print(f"{dest} already exists")
        return 1
    shutil.copytree(ROOT / "_template", dest)
    (dest / "NAME.py").rename(dest / f"{name}.py")
    for f in dest.iterdir():
        if f.is_file():
            t = f.read_text(encoding="utf-8").replace("NAME", name)
            f.write_text(t.replace("YYYY-MM-DD", date.today().isoformat()), encoding="utf-8")
    print(f"created {dest}; fill SKILL.md and {name}.py there (the working copy; the G: copy is a mirror)")
    return 0


def git(*argv: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", str(ROOT), *argv], capture_output=True, text=True, encoding="utf-8")


def push_origin() -> None:
    """Push main to origin if an 'origin' remote exists (the library's GitHub repository). Never raises."""
    try:
        if git("remote", "get-url", "origin").returncode != 0:
            return
        # Google Drive drops desktop.ini files into .git; git then sees broken refs
        for sub in ("refs", "logs"):
            base = ROOT / ".git" / sub
            if not base.is_dir():
                continue
            for p in base.rglob("desktop.ini"):
                try:
                    os.chmod(p, stat.S_IWRITE)  # clear read-only attribute if set
                    p.unlink()
                except OSError:
                    pass
        r = git("push", "origin", "main")
        if r.returncode == 0:
            print("pushed to origin")
        else:
            lines = (r.stderr or "").strip().splitlines()
            print("warning: push failed: " + (lines[0] if lines else f"exit code {r.returncode}"))
    except Exception as e:
        print(f"warning: push failed: {e}")


# 1.4.0: the site agent runs the same library through the aiwb-tools MCP server (mcp_server.py);
# after every commit the committed tree is published to ~/co-agent/tools on the hosting (git archive + hz.py).
# 1.6.2: hz.py is looked up in AIWB_HZ_DIR, else ~/.ssh; without it site-sync is skipped.
HZ_DIR = Path(os.environ.get("AIWB_HZ_DIR") or Path.home() / ".ssh")
LOCK = HZ_DIR / "_job.lock"
JOBF = HZ_DIR / "_job.json"
HZ_PY = HZ_DIR / "hz.py"
PYEXE = r"C:\ProgramData\anaconda3\python.exe" if os.path.isfile(r"C:\ProgramData\anaconda3\python.exe") else sys.executable


def hz_job(job: dict, timeout: int = 300) -> str:
    deadline = time.time() + 60
    fd = None
    while fd is None:
        try:
            fd = os.open(str(LOCK), os.O_CREAT | os.O_EXCL)
        except FileExistsError:
            try:
                if time.time() - LOCK.stat().st_mtime > 300:
                    LOCK.unlink(missing_ok=True)
                    continue
            except FileNotFoundError:
                continue
            if time.time() > deadline:
                raise RuntimeError("timeout acquiring hz job lock")
            time.sleep(0.5)
    try:
        os.close(fd)
        JOBF.write_text(json.dumps(job), encoding="utf-8")
        proc = subprocess.run(
            [PYEXE, str(HZ_PY)],
            capture_output=True, text=True, encoding="utf-8", timeout=timeout,
        )
        out = (proc.stdout or "") + (proc.stderr or "")
        if proc.returncode != 0:
            raise RuntimeError(f"hz.py exited {proc.returncode}: {out}")
        m = re.search(r"--- rc=(\d+) ---", out)
        if m and int(m.group(1)) != 0:
            raise RuntimeError(f"job rc={m.group(1)}: {out}")
        return out
    finally:
        LOCK.unlink(missing_ok=True)


def site_sync(quiet: bool = False) -> bool:
    if not HZ_PY.exists():
        return False
    tmp_path = None
    try:
        sha = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"],
            capture_output=True, text=True, check=True,
        ).stdout.strip()
        fd, tmp_path = tempfile.mkstemp(suffix=".tar.gz")
        os.close(fd)
        subprocess.run(
            ["git", "-C", str(ROOT), "archive", "--format=tar.gz", "-o", tmp_path, "HEAD"],
            check=True,
        )
        hz_job({"op": "put", "local": tmp_path, "remote": "co-agent/state/tools.tar.gz"})
        cmd = (
            "set -e; cd ~/co-agent; rm -rf tools.new tools.old; mkdir tools.new; "
            "tar -xzf state/tools.tar.gz -C tools.new; "
            f"echo {sha} > tools.new/.revision; "
            "[ -d tools ] && mv tools tools.old; mv tools.new tools; rm -rf tools.old; "
            f"echo synced {sha}"
        )
        hz_job({"op": "run", "cmd": cmd})
        if not quiet:
            print(f"site: tools synced {sha}")
        return True
    except Exception as e:
        print(f"warning: site not synced: {e}")
        return False
    finally:
        if tmp_path:
            Path(tmp_path).unlink(missing_ok=True)


def cmd_site_sync(args) -> int:
    return 0 if site_sync() else 1


def update_mirror() -> None:
    """Fast-forward the Drive mirror to origin/main after a push. Never raises."""
    try:
        if not (MIRROR / ".git").is_dir() or MIRROR.resolve() == ROOT.resolve():
            return
        for sub in ("refs", "logs"):  # Google Drive drops desktop.ini files into .git
            for f in (MIRROR / ".git" / sub).rglob("desktop.ini"):
                try:
                    os.chmod(f, stat.S_IWRITE)
                    f.unlink()
                except OSError:
                    pass
        run = lambda *a: subprocess.run(["git", "-C", str(MIRROR), *a], capture_output=True, text=True)
        r = run("fetch", "origin")
        if r.returncode == 0:
            # Drive rewrites file stat data, so git may list unchanged files as modified;
            # when contents match HEAD, re-stage them so the mirror shows clean.
            if run("diff", "--quiet").returncode == 0:
                run("add", "-u")
            r = run("merge", "--ff-only", "origin/main")
        print("mirror updated" if r.returncode == 0 else
              "warning: mirror not updated: " + ((r.stderr or "").strip().splitlines() or ["?"])[0])
    except Exception as e:
        print(f"warning: mirror not updated: {e}")


def cmd_commit(args) -> int:
    paths = [p for p in args.names if (ROOT / p).exists()]
    if not paths:
        print("no such tool folders")
        return 1
    # 1.5.0: a tool with unexcused security errors is not committed
    scanned = [ROOT / p for p in paths if (ROOT / p / "SKILL.md").exists()]
    if scanned and run_scan(scanned):
        print("commit refused: fix the errors or add an exception with a reason under security: in SKILL.md")
        return 1
    write_index()
    paths.append("INDEX.md")
    for attempt in range(30):
        r = git("add", "--all", "--", *paths)
        if r.returncode == 0:
            # Commit only these paths so parallel agents' staged work is left alone.
            r = git("commit", "-m", args.message, "--", *paths)
            if r.returncode == 0 or "nothing to commit" in r.stdout + r.stderr:
                print(r.stdout.strip() or "nothing to commit")
                push_origin()  # also covers earlier commits that are still unpushed
                update_mirror()
                site_sync()
                return 0
        if "index.lock" not in r.stderr:
            print(r.stdout + r.stderr)
            return 1
        time.sleep(2)
    print("git index.lock stayed busy for 60 s")
    return 1


def cmd_archive(args) -> int:
    """1.6.0: move an unused tool to _archive/<name> (hidden from INDEX, list, MCP and the site) and commit."""
    src = ROOT / args.name
    dst = ROOT / "_archive" / args.name
    if not NAME_RE.match(args.name) or not (src / "SKILL.md").exists():
        print(f"no such tool: {args.name}")
        return 1
    if dst.exists():
        print(f"already archived: {dst}")
        return 1
    skill = src / "SKILL.md"
    text = skill.read_text(encoding="utf-8").rstrip("\n")
    note = f"- archived — {date.today().isoformat()} — {args.message}"
    text = text + ("\n" if re.search(r"^## История\s*$", text, re.M) else "\n\n## История\n\n") + note + "\n"
    skill.write_text(text, encoding="utf-8")
    dst.parent.mkdir(exist_ok=True)
    r = git("mv", args.name, f"_archive/{args.name}")
    if r.returncode != 0:
        print(r.stdout + r.stderr)
        return 1
    write_index()
    git("add", "--all", "--", f"_archive/{args.name}", "INDEX.md")
    # the old folder is gone, so commit (not cmd_commit) takes both paths of the move explicitly
    r = git("commit", "-m", f"{args.name}: archived ({args.message})", "--", args.name, f"_archive/{args.name}", "INDEX.md")
    if r.returncode != 0:
        print(r.stdout + r.stderr)
        return 1
    print(r.stdout.strip())
    push_origin()
    update_mirror()
    site_sync()
    print(f"archived {args.name} -> _archive/{args.name}; restore: git mv _archive/{args.name} {args.name}, then toolbox.py commit {args.name}")
    return 0


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", help="one line per tool").set_defaults(fn=cmd_list)
    s = sub.add_parser("search", help="tools whose SKILL.md contains all words")
    s.add_argument("words", nargs="+")
    s.set_defaults(fn=cmd_search)
    sub.add_parser("check", help="validate SKILL.md frontmatter and run the security scan").set_defaults(fn=cmd_check)
    sc = sub.add_parser("scan", help="security scan of tools (all when no names given)")
    sc.add_argument("names", nargs="*")
    sc.add_argument("--all", action="store_true", help="every tool (default when no names are given)")
    sc.add_argument("--json", action="store_true", help="findings as JSON")
    sc.add_argument("-v", "--verbose", action="store_true", help="also list excused findings")
    sc.set_defaults(fn=cmd_scan)
    sub.add_parser("index", help="regenerate INDEX.md and the CLAUDE.md block").set_defaults(fn=cmd_index)
    n = sub.add_parser("new", help="create a tool from _template")
    n.add_argument("name")
    n.set_defaults(fn=cmd_new)
    sub.add_parser("site-sync", help="publish the committed library to the site agent (the site of install.json)").set_defaults(fn=cmd_site_sync)
    c = sub.add_parser("commit", help="update index and commit the given tool folders")
    c.add_argument("names", nargs="+")
    c.add_argument("-m", "--message", required=True)
    c.set_defaults(fn=cmd_commit)
    a = sub.add_parser("archive", help="move an unused tool to _archive/<name> and commit")
    a.add_argument("name")
    a.add_argument("-m", "--message", required=True, help="why (e.g. no agent job used it for 60 days)")
    a.set_defaults(fn=cmd_archive)
    args = p.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
