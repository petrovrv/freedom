#!/usr/bin/env python3
"""devtree: isolated git worktrees for AI agents sharing one repo, plus atomic file locks."""
import argparse
import datetime
import fnmatch
import json
import os
import pathlib
import subprocess
import sys
import time

LOCAL = os.environ.get("LOCALAPPDATA") or str(pathlib.Path.home() / "AppData" / "Local")
# 0.8.0: the main tree can be chosen by a config file (the switch of the core to the public repository changes one
# file, not every agent's command line). Order: command line > environment > config file > built-in default.
CONFIG_PATH = os.environ.get("AIWB_DEVTREE_CONFIG") or os.path.join(LOCAL, "AIWorkbench", "devtree.json")


def _load_config(path):
    try:
        with open(path, encoding="utf-8-sig") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except FileNotFoundError:
        return {}
    except (OSError, ValueError) as e:
        print(f"warning: devtree config ignored ({path}): {e}", file=sys.stderr)
        return {}


CONFIG = _load_config(CONFIG_PATH)
# 0.8.1: the core is Freedom (C:\AIWorkbench\freedom); AIWB_SOURCE is still read as an older name of AIWB_FREEDOM
DEF_REPO = (os.environ.get("AIWB_FREEDOM") or os.environ.get("AIWB_SOURCE") or CONFIG.get("repo")
            or r"C:\AIWorkbench\freedom")
# Read-only mirror clone on Drive (same origin); empty string disables mirror updates
DEF_MIRROR = os.environ["AIWB_MIRROR"] if "AIWB_MIRROR" in os.environ else CONFIG.get("mirror", r"G:\My Drive\AI-Workbench\freedom")
DEF_WORK = CONFIG.get("workRoot") or os.path.join(LOCAL, "AIWorkbenchWork")
DEF_LOCKS = CONFIG.get("locks") or r"G:\My Drive\AI-Workbench\transport\locks"
# 0.8.0: a repository may ask for checks of its own in .devtree.json at its root (e.g. a public repository: nothing
# personal may be merged or pushed): {"guard": [argv with {range} {base} {head}], "hooksPath": "dir"}
REPO_FILE = ".devtree.json"
ALLOWED = set("abcdefghijklmnopqrstuvwxyz0123456789-")
POLL = 2
MERGE_LOCK = "source-merge"
# 0.6.0: paths whose change needs the owner's own "yes" (--owner-approved), even after a green review; the list lives in
# the repository and is itself protected
PROTECTED_FILE = "protected-paths.txt"
ENV = dict(os.environ, GIT_TERMINAL_PROMPT="0")


class GitError(Exception):
    def __init__(self, args, rc, stderr):
        super().__init__(f"git {' '.join(args)} failed ({rc}): {stderr}")
        self.rc, self.stderr = rc, stderr


class Exit(Exception):
    """Controlled termination with exit code and optional JSON data."""
    def __init__(self, code, msg, **data):
        super().__init__(msg)
        self.code, self.data = code, data


class Parser(argparse.ArgumentParser):
    def error(self, message):  # usage errors -> exit 1 (argparse default is 2)
        self.print_usage(sys.stderr)
        print(f"{self.prog}: error: {message}", file=sys.stderr)
        sys.exit(1)


class Out:
    """Human lines, or one JSON object at the end."""
    def __init__(self, as_json):
        self.json, self.lines, self.data = as_json, [], {}

    def say(self, msg):
        self.lines.append(msg)
        if not self.json:
            print(msg)

    def finish(self, code, error=None):
        if self.json:
            obj = {"ok": code == 0, "exitCode": code, **self.data, "messages": self.lines}
            if error:
                obj["error"] = error
            print(json.dumps(obj, ensure_ascii=False, default=str))
        elif error:
            print(f"error: {error}", file=sys.stderr)
        return code


def clean_err(text):
    # Drop harmless Drive-induced warnings such as "ignoring broken ref refs/desktop.ini"
    return "\n".join(l for l in text.splitlines()
                     if not l.startswith("warning: ignoring broken ref")).strip()


def run(args, cwd, check=True):
    p = subprocess.run(["git", *args], cwd=str(cwd), capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=ENV)
    if check and p.returncode != 0:
        raise GitError(args, p.returncode, clean_err(p.stderr) or p.stdout.strip())
    return p


def check_name(value, what="name"):
    if not (1 <= len(value) <= 40 and set(value) <= ALLOWED):
        raise Exit(1, f"invalid {what} {value!r}: must match [a-z0-9-]{{1,40}}")


# ---------------- locks ----------------

def now_utc():
    return datetime.datetime.now(datetime.timezone.utc)


def parse_iso(s):
    dt = datetime.datetime.fromisoformat(s.replace("Z", "+00:00"))
    return dt if dt.tzinfo else dt.replace(tzinfo=datetime.timezone.utc)


def lock_path(locks, res):
    return pathlib.Path(locks) / f"{res}.lock"


def read_lock(p):
    """Return (info or None, exists)."""
    try:
        text = p.read_text(encoding="utf-8")
    except FileNotFoundError:
        return None, False
    except OSError:
        return None, True
    try:
        info = json.loads(text)
        return (info if isinstance(info, dict) else None), True
    except ValueError:
        return None, True


def lock_expired(info):
    """True/False, or None if the content is malformed."""
    try:
        end = parse_iso(info["acquiredUtc"]) + datetime.timedelta(minutes=float(info["ttlMinutes"]))
        return end < now_utc()
    except (KeyError, ValueError, TypeError, AttributeError):
        return None


def describe(info):
    if not info:
        return "unknown holder (lock file unreadable)"
    return (f"{info.get('owner')} on {info.get('host')} pid {info.get('pid')} "
            f"since {info.get('acquiredUtc')} (ttl {info.get('ttlMinutes')} min)")


def break_lock(p, out):
    stale = p.with_name(f"{p.name}.stale-{int(time.time())}")
    try:
        os.replace(p, stale)
        out.say(f"broke stale lock -> {stale.name}")
    except FileNotFoundError:
        pass
    except PermissionError:
        time.sleep(POLL)  # file in use (e.g. Drive sync); retry later


def acquire(locks, res, owner, ttl, wait, out):
    """Return (info, status) with status in acquired|refreshed|busy."""
    p = lock_path(locks, res)
    p.parent.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + wait
    while True:
        rec = {"resource": res, "owner": owner,
               "acquiredUtc": now_utc().strftime("%Y-%m-%dT%H:%M:%SZ"),
               "ttlMinutes": ttl, "pid": os.getpid(), "host": os.environ.get("COMPUTERNAME", "")}
        try:
            fd = os.open(p, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            info, exists = read_lock(p)
            if not exists:
                continue
            exp = lock_expired(info) if info else None
            if exp is None:  # unreadable: stale only if old enough
                try:
                    age = time.time() - p.stat().st_mtime
                except FileNotFoundError:
                    continue
                if age > 60:
                    break_lock(p, out)
                    continue
            elif exp:
                break_lock(p, out)
                continue
            elif info.get("owner") == owner:  # re-entrant: refresh timestamp atomically
                tmp = p.with_name(f"{p.name}.tmp-{os.getpid()}")
                tmp.write_text(json.dumps(rec), encoding="utf-8")
                os.replace(tmp, p)
                return rec, "refreshed"
            if time.monotonic() >= deadline:
                return info, "busy"
            time.sleep(POLL)
            continue
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(rec, f)
        return rec, "acquired"


def release(locks, res, owner, force, out):
    p = lock_path(locks, res)
    info, exists = read_lock(p)
    if not exists:
        out.say(f"lock {res} not held (no-op)")
        return "missing"
    holder = info.get("owner") if info else None
    if holder != owner and not force:
        raise Exit(3, f"lock {res} held by {describe(info)}; not released", holder=info)
    try:
        os.remove(p)
    except FileNotFoundError:
        pass
    out.say(f"released lock {res}")
    return "released"


def cmd_lock(a, out):
    if a.lock_cmd == "status":
        d = pathlib.Path(a.locks)
        if a.resource:
            check_name(a.resource, "resource")
            files = [lock_path(d, a.resource)]
        else:
            files = sorted(d.glob("*.lock")) if d.is_dir() else []
        locks = []
        for p in files:
            info, exists = read_lock(p)
            if not exists:
                out.say(f"{p.stem}: free")
                locks.append({"resource": p.stem, "held": False})
                continue
            exp = lock_expired(info) if info else None
            state = "unreadable" if exp is None else ("expired" if exp else "held")
            out.say(f"{p.stem}: {state} by {describe(info)}")
            locks.append({"resource": p.stem, "held": True, "state": state, "info": info})
        if not files:
            out.say("no locks")
        out.data["locks"] = locks
        return
    check_name(a.resource, "resource")
    if a.lock_cmd == "acquire":
        info, st = acquire(a.locks, a.resource, a.owner, a.ttl_min, a.wait_sec, out)
        out.data.update(status=st, lock=info)
        if st == "busy":
            raise Exit(3, f"lock {a.resource} busy: {describe(info)}")
        out.say(f"{st} lock {a.resource} as {a.owner} (ttl {a.ttl_min} min)")
    else:
        out.data["status"] = release(a.locks, a.resource, a.owner, a.force, out)


# ---------------- git worktrees ----------------

def worktrees(repo):
    res, cur = [], {}
    for line in run(["worktree", "list", "--porcelain"], repo).stdout.splitlines() + [""]:
        if not line:
            if cur:
                res.append(cur)
            cur = {}
            continue
        k, _, v = line.partition(" ")
        cur[k] = v
    return res


def agent_wt(repo, name):
    for w in worktrees(repo):
        if w.get("branch") == f"refs/heads/agent/{name}":
            return pathlib.Path(w["worktree"])
    return None


def branch_exists(repo, br):
    return run(["rev-parse", "--verify", "--quiet", f"refs/heads/{br}"], repo, check=False).returncode == 0


def porcelain(path, repo):
    return [l for l in run(["-C", str(path), "status", "--porcelain"], repo).stdout.splitlines() if l.strip()]


def main_dirty(repo):
    return [l for l in porcelain(repo, repo) if not l.rstrip().strip('"').lower().endswith("desktop.ini")]


def heal_phantom(repo, out):
    """Re-stage files that git falsely reports as modified after a merge on Google Drive.

    Google Drive for Desktop can make git record wrong stat data (e.g. size 16384) for files
    written by `git merge --ff-only`; they then show as " M" although their content matches
    HEAD. `git update-index --refresh` does not fix this, `git add <path>` does, so only files
    whose content hash equals HEAD are re-added.
    """
    healed = []
    for line in main_dirty(repo):
        # Only worktree-modified, not staged
        if len(line) < 4 or line[:2] != " M":
            continue
        path = line[3:]
        if len(path) >= 2 and path.startswith('"') and path.endswith('"'):
            path = path[1:-1]
        worktree = run(["hash-object", "--", path], repo, check=False)
        if worktree.returncode != 0:
            continue
        head = run(["rev-parse", f"HEAD:{path}"], repo, check=False)
        if head.returncode != 0:
            continue
        if worktree.stdout.strip() == head.stdout.strip():
            healed.append(path)
    if healed:
        run(["add", "--", *healed], repo)
        out.say(f"refreshed git stat of {len(healed)} file(s) after merge (Drive)")
    return healed


def link_node_modules(repo, worktree, out) -> list[str]:
    """Junction worktree/<dir>/node_modules -> repo/<dir>/node_modules (e.g. co-web). Never raises."""
    linked: list[str] = []
    try:
        repo = pathlib.Path(repo).resolve()
        worktree = pathlib.Path(worktree).resolve()
        for pkg in sorted(worktree.glob("*/package.json")):
            d = pkg.parent.name
            target = repo / d / "node_modules"
            link = worktree / d / "node_modules"
            # lexists also catches a dangling junction already in place
            if not target.is_dir() or os.path.lexists(link):
                continue
            try:
                r = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
                                   capture_output=True, text=True)
            except OSError as e:
                out.say(f"warning: could not link {d}/node_modules: {e}")
                continue
            if r.returncode == 0:
                linked.append(d)
                out.say(f"linked {d}/node_modules")
            else:
                msg = (r.stderr or r.stdout or "").strip().splitlines()
                out.say(f"warning: could not link {d}/node_modules: " + (msg[0] if msg else f"exit code {r.returncode}"))
    except Exception as e:
        out.say(f"warning: node_modules linking failed: {e}")
    return linked


def unlink_node_modules(worktree) -> list[str]:
    """Remove node_modules junctions (the link only, never the target). Never raises."""
    removed: list[str] = []
    try:
        for p in pathlib.Path(worktree).glob("*/node_modules"):
            if os.path.isjunction(p):
                try:
                    os.rmdir(p)  # removes the junction itself, target untouched
                    removed.append(p.parent.name)
                except OSError:
                    pass
    except Exception:
        pass
    return removed


def repo_settings(tree):
    """.devtree.json of a working tree ({} when absent or broken; a broken file is reported, not fatal)."""
    p = pathlib.Path(tree) / REPO_FILE
    if not p.is_file():
        return {}
    try:
        data = json.loads(p.read_text(encoding="utf-8-sig"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError) as e:
        raise Exit(1, f"{p}: {e}")


def ensure_hooks(repo, out):
    """Set core.hooksPath asked by .devtree.json (e.g. the pre-push guard of a public repository)."""
    want = repo_settings(repo).get("hooksPath")
    if not want:
        return
    have = run(["config", "--get", "core.hooksPath"], repo, check=False).stdout.strip()
    if have != want:
        run(["config", "core.hooksPath", want], repo)
        out.say(f"core.hooksPath set to {want} (.devtree.json)")


def guard_gate(tree, repo, base, head, out, when):
    """Run the repository's own guard (.devtree.json "guard") on base..head; exit 7 when it finds something.
    --force does not bypass it: what it protects (personal data in a public history) cannot be taken back."""
    cmd = repo_settings(tree).get("guard")
    if not cmd:
        return
    if not isinstance(cmd, list) or not all(isinstance(x, str) for x in cmd):
        raise Exit(1, f"{REPO_FILE}: guard must be a list of strings")
    base_sha = run(["rev-parse", base], tree).stdout.strip()
    head_sha = run(["rev-parse", head], tree).stdout.strip()
    rng = f"{base_sha}..{head_sha}"
    argv = [x.replace("{range}", rng).replace("{base}", base_sha).replace("{head}", head_sha) for x in cmd]
    if argv and argv[0] in ("python", "python3", "py"):
        argv[0] = sys.executable
    out.say(f"guard ({when}): {' '.join(cmd)}")
    try:
        p = subprocess.run(argv, cwd=str(tree), capture_output=True, text=True, encoding="utf-8", errors="replace",
                           timeout=900, env=ENV)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise Exit(7, f"guard could not run: {e}", guard={"ok": False, "when": when, "error": str(e)})
    tail = [l for l in (p.stdout + p.stderr).splitlines() if l.strip()][-40:]
    out.data["guard"] = {"ok": p.returncode == 0, "when": when, "exitCode": p.returncode, "range": rng}
    if p.returncode:
        for l in tail:
            out.say("  " + l)
        raise Exit(7, f"guard refused {rng} (exit {p.returncode}): fix the branch (amend/rebase it, nothing is "
                      "published yet) and repeat", guard=out.data["guard"])
    out.say("guard clean" + (f": {tail[-1]}" if tail else ""))


def cmd_start(a, out):
    check_name(a.name)
    ensure_hooks(a.repo, out)
    br, path = f"agent/{a.name}", pathlib.Path(a.work_root) / a.name
    if branch_exists(a.repo, br):
        raise Exit(1, f"branch {br} already exists")
    if path.exists():
        raise Exit(1, f"path already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    run(["worktree", "add", "-b", br, str(path), a.base], a.repo)
    linked = link_node_modules(a.repo, path, out)
    out.data.update(name=a.name, branch=br, path=str(path), linked=linked)
    out.say(str(path))


def cmd_list(a, out):
    items = []
    for w in worktrees(a.repo):
        br = w.get("branch", "").removeprefix("refs/heads/")
        if not br.startswith("agent/"):
            continue
        r = run(["rev-list", "--left-right", "--count", f"main...{br}"], a.repo, check=False)
        behind = ahead = None
        if r.returncode == 0 and len(r.stdout.split()) == 2:
            behind, ahead = (int(x) for x in r.stdout.split())
        try:
            dirty = "yes" if porcelain(w["worktree"], a.repo) else "no"
        except GitError:
            dirty = "?"
        item = {"name": br[6:], "path": str(pathlib.Path(w["worktree"])),
                "ahead": ahead, "behind": behind, "dirty": dirty}
        items.append(item)
        out.say(f"{item['name']:<20} ahead {ahead} behind {behind} dirty {dirty}  {item['path']}")
    if not items:
        out.say("no agent worktrees")
    out.data["worktrees"] = items


def codex_review(wt: pathlib.Path, repo, title: str, a, out) -> None:
    """0.4.0: cross-review of an agent branch by Codex before merge (codex review --base main). Raises Exit(4) on
    a blocking verdict unless --force; review text is saved to AIWorkbenchWork/reviews/."""
    import re
    import shutil

    INSTRUCTIONS = (
        "Review ONLY the diff of this branch against main, and report only real defects:\n"
        "- correctness bugs\n"
        "- security problems (secrets in code, injection, unsafe file/path handling)\n"
        "- data loss\n"
        "- broken compatibility with existing data/configs\n"
        "- missing error handling on external calls (processes, network, filesystem, DB)\n"
        "Ignore style, naming, formatting and nitpicks. Be concise (max ~25 lines).\n"
        "The LAST line of your answer must be exactly one of:\n"
        "VERDICT: OK\n"
        "VERDICT: BLOCK - <one-line reason>\n"
        "Use BLOCK only for defects that would break production, lose data or leak secrets.\n"
    )
    CODE_EXT = {".cs", ".ts", ".tsx", ".js", ".mjs", ".php", ".py", ".ps1", ".cmd",
                ".sql", ".sh", ".nsi", ".csproj"}

    mode = str(getattr(a, "review", "auto") or "auto").strip().lower()
    if mode not in {"auto", "on", "off"}:
        raise Exit(2, f"invalid review mode: {mode!r} (expected auto|on|off)")
    if mode == "off":
        return

    codex = shutil.which("codex")
    if not codex:
        if mode == "on":
            raise Exit(4, "codex CLI not found")
        out.say("review: skipped (codex not found)")
        return

    diff = run(["-C", str(wt), "diff", "--name-only", "main...HEAD"], repo, check=False)
    changed = [f.strip() for f in (diff.stdout or "").splitlines() if f.strip()]
    if not changed:
        return
    if mode == "auto" and not any(
        pathlib.PurePosixPath(f).suffix.lower() in CODE_EXT for f in changed
    ):
        out.say("review: skipped (no code changes)")
        return

    def _fail(reason: str) -> None:
        if mode == "on":
            raise Exit(4, f"codex review failed ({reason})")
        out.say(f"review: warning: codex review failed ({reason}); continuing")

    timeout = getattr(a, "review_timeout", None)
    timeout = float(timeout) if timeout else None
    # `codex review --base` takes no custom instructions, so codex exec gets them with the diff (read-only sandbox:
    # it may still open files of the worktree for context).
    patch = run(["-C", str(wt), "diff", "main...HEAD"], repo, check=False).stdout or ""
    if len(patch) > 200_000:
        patch = patch[:200_000] + "\n[... diff truncated ...]\n"
    prompt = f"{INSTRUCTIONS}\nChange title: {title}\n\nDIFF (git diff main...HEAD):\n{patch}"
    last = pathlib.Path(wt.parent / "reviews" / f".last-{wt.name}.txt")
    last.parent.mkdir(parents=True, exist_ok=True)
    from codex_models import run_review

    def command(model, effort):
        cmd = [codex, "exec", "--skip-git-repo-check", "-m", model,
               "-s", "read-only", "-c", 'approval_policy="never"',
               "-o", str(last), "-"]
        if effort:
            cmd[2:2] = ["-c", f'model_reasoning_effort="{effort}"']
        return cmd

    out.say(f"review: codex is reviewing {len(changed)} changed file(s)...")
    try:
        proc = run_review(codex, wt, a.review_model, command, prompt, timeout, out.say)
    except (RuntimeError, ValueError) as e:
        _fail(str(e))
        return
    except subprocess.TimeoutExpired:
        _fail(f"timed out after {timeout:g}s" if timeout else "timed out")
        return
    except OSError as e:
        _fail(f"could not start codex: {e}")
        return
    if proc.returncode != 0:
        err = [l.strip() for l in (proc.stderr or proc.stdout or "").splitlines() if l.strip()]
        detail = err[-1][:200] if err else "no output"
        _fail(f"exit {proc.returncode}: {detail}")
        return

    try:
        text = last.read_text(encoding="utf-8", errors="replace")
        last.unlink()
    except OSError:
        text = proc.stdout or ""
    reviews_dir = wt.parent / "reviews"
    reviews_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    path = reviews_dir / f"{wt.name}-{stamp}.md"
    path.write_text(f"# Codex review: {title}\n\n{text}", encoding="utf-8")

    verdict, reason = "UNKNOWN", ""
    rx = re.compile(r"^\s*VERDICT:\s*(OK|BLOCK)\b(.*)$", re.IGNORECASE)
    lines = text.splitlines()
    for line in reversed(lines):
        m = rx.match(line)
        if m:
            verdict = m.group(1).upper()
            reason = m.group(2).strip().lstrip("-–—:").strip().rstrip(".")
            break

    out.data["review"] = {"file": str(path), "verdict": verdict}

    for line in [l for l in lines if l.strip()][-15:]:
        out.say(f"review: {line}")

    if verdict == "BLOCK":
        if not getattr(a, "force", False):
            raise Exit(
                4,
                f"Codex review blocked the merge: {reason or 'no reason given'}. "
                f"Review: {path}. Fix and finish again, or pass --force.",
                review=str(path),
            )
        out.say("review: BLOCK overridden by --force")
    elif verdict == "UNKNOWN":
        out.say("review: no verdict line; continuing")


def e2e_gate(wt, repo, a, out) -> None:
    """0.5.0: end-to-end gate before merge: aiwb-e2e (CO Web in Playwright, "reply pong" through the job queue)
    when the diff touches co-web, co-web-auth, co-agent, bridge or co-desktop; co-web is built from the branch and
    served locally against the production API. Red stops the merge (exit 5) unless --force; a run that cannot happen
    (busy host, no token) only warns in auto mode. Runs outside the source-merge lock, so other finishes go on."""
    mode = (getattr(a, "e2e", "auto") or "auto").lower()
    if mode not in ("auto", "on", "off"):
        raise Exit(1, f"invalid --e2e mode: {mode!r} (expected auto/on/off)")
    if mode == "off":
        return
    force = bool(getattr(a, "force", False))

    def cannot(reason):
        out.data["e2e"] = {"status": "skipped", "reason": reason}
        if mode == "on" and not force:
            raise Exit(5, f"e2e could not run ({reason})")
        out.say(f"e2e: warning: could not run ({reason}); continuing")

    def red(reason, summary):
        out.data["e2e"] = dict(summary, status="red", reason=reason)
        if not force:
            raise Exit(5, f"e2e is red: {reason}. Fix and finish again, or pass --force.", e2e=out.data["e2e"])
        out.say("e2e: RED overridden by --force")

    def last_line(text):
        lines = [l.strip() for l in (text or "").splitlines() if l.strip()]
        return lines[-1] if lines else ""

    diff = run(["-C", str(wt), "diff", "--name-only", "main...HEAD"], repo, check=False).stdout or ""
    changed = [l.strip() for l in diff.splitlines() if l.strip()]
    prefixes = ("co-web/", "co-web-auth/", "co-agent/", "bridge/", "co-desktop/")
    touched = sorted({c.rstrip("/") for f in changed for c in prefixes if f.startswith(c)})
    if mode == "auto" and not touched:
        out.say("e2e: skipped (no co-web/co-agent/bridge/co-desktop changes)")
        return

    tool = pathlib.Path(__file__).resolve().parent.parent / "aiwb-e2e" / "aiwb-e2e.py"
    if not tool.is_file():
        cannot("aiwb-e2e not found")
        return

    dist = None
    if "co-web" in touched:
        web = pathlib.Path(wt) / "co-web"
        if not (web / "node_modules").is_dir():
            cannot("co-web/node_modules missing; production smoke only")
        else:
            out.say("e2e: building co-web…")
            try:
                b = subprocess.run("npm run build", cwd=web, shell=True, capture_output=True, text=True,
                                   encoding="utf-8", errors="replace", timeout=600)
            except (subprocess.TimeoutExpired, OSError) as e:
                cannot(str(e))
            else:
                if b.returncode != 0:
                    red("co-web build failed: " + last_line((b.stdout or "") + "\n" + (b.stderr or ""))[:200], {})
                    return
                if (web / "dist" / "index.html").is_file():
                    dist = web / "dist"

    cmd = [sys.executable, str(tool), "run", "--json"]
    cmd += ["--dist", str(dist)] if dist else []
    cmd += ["--expect", a.e2e_expect] if getattr(a, "e2e_expect", None) else []
    cmd += ["--host", getattr(a, "e2e_host", None) or "auto",
            "--queue-wait", str(getattr(a, "e2e_queue_wait", 120) or 120)]
    kind = "local co-web build, production API" if dist else "production smoke"
    out.say(f"e2e: running aiwb-e2e ({kind}; {', '.join(touched) or 'forced'})…")
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                              timeout=float(getattr(a, "e2e_timeout", 1500) or 1500))
    except (subprocess.TimeoutExpired, OSError) as e:
        cannot(f"aiwb-e2e failed to run: {e}")
        return

    try:
        res = json.loads(last_line(proc.stdout))
        if not isinstance(res, dict):
            res = {}
    except (ValueError, TypeError):
        res = {}
    keys = ("runId", "status", "reason", "chatId", "reply", "timings", "cleanup", "screenshot", "dist", "jobs")
    summary = {k: res[k] for k in keys if res.get(k) is not None}
    for k in ("runId", "chatId", "reply", "cleanup", "reason", "screenshot"):
        if k in summary:
            v = summary[k]
            out.say(f"e2e: {k}: {str(v)[:120] if k == 'reply' else v}")
    if isinstance(summary.get("timings"), dict) and summary["timings"]:
        out.say("e2e: timings: " + ", ".join(f"{k}={v}s" for k, v in summary["timings"].items()))

    rc, status = proc.returncode, res.get("status")
    if rc == 0 and status == "green":
        out.data["e2e"] = summary
        out.say("e2e: GREEN")
        return
    if rc == 1 or status == "red":
        red(res.get("reason") or f"exit {rc}", summary)
        return
    cannot(res.get("reason") or last_line(proc.stderr)[:200] or f"exit {rc}")


def protected_patterns(repo, br):
    """Patterns of PROTECTED_FILE on main and on the branch (a branch can add entries, not drop them)."""
    pats = {PROTECTED_FILE}
    for ref in ("main", br):
        r = run(["show", f"{ref}:{PROTECTED_FILE}"], repo, check=False)
        if r.returncode:
            continue
        for line in r.stdout.splitlines():
            line = line.split("#", 1)[0].strip().replace("\\", "/")
            if line:
                pats.add(line)
    return sorted(pats)


def path_protected(path, pats):
    """gitignore-like: "dir/" covers everything under it, a pattern without "/" matches the file name anywhere,
    otherwise fnmatch on the whole repo path ("*" also crosses "/"); case-insensitive."""
    p = path.replace("\\", "/").lower()
    base = p.rsplit("/", 1)[-1]
    for pat in pats:
        q = pat.lower().lstrip("/")
        if q.endswith("/"):
            if p.startswith(q) or fnmatch.fnmatchcase(p, q + "*"):
                return pat
        elif "/" not in q:
            if fnmatch.fnmatchcase(base, q):
                return pat
        elif fnmatch.fnmatchcase(p, q):
            return pat
    return None


def protected_gate(wt, repo, br, a, out, stage):
    """Exit 6 when the branch changes a protected path and the owner's approval is missing; log the approval."""
    r = run(["-C", str(wt), "diff", "--name-only", "--no-renames", "main...HEAD"], repo, check=False)
    if r.returncode:
        raise Exit(1, f"cannot list changed files: {clean_err(r.stderr)}")
    pats = protected_patterns(repo, br)
    hits = [(f, m) for f in r.stdout.splitlines() if f for m in [path_protected(f, pats)] if m]
    out.data["protected"] = [f for f, _ in hits]
    if not hits:
        return
    for f, m in hits:
        out.say(f"protected: {f}  ({m})")
    approval = (a.owner_approved or "").strip()
    if len(approval) < 3:
        raise Exit(6, f"{len(hits)} protected path(s) changed: ask the owner, then repeat finish with "
                      '--owner-approved "<chat message number or the owner quote>" (--force does not skip this)',
                   protected=[f for f, _ in hits])
    if stage != "final":
        return
    sha = run(["-C", str(wt), "rev-parse", "HEAD"], repo, check=False).stdout.strip()
    rec = {"utc": now_utc().isoformat(timespec="seconds"), "name": a.name, "branch": br, "head": sha, "message": (a.message or "").splitlines()[0],
           "files": [f for f, _ in hits], "approval": approval}
    logs = [pathlib.Path(a.work_root) / "reviews" / "protected-approvals.jsonl"]
    if a.locks:
        logs.append(pathlib.Path(a.locks).parent / "devtree-approvals.jsonl")
    for log in logs:
        try:
            log.parent.mkdir(parents=True, exist_ok=True)
            with open(log, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(rec, ensure_ascii=False) + "\n")
        except OSError as e:
            out.say(f"warning: approval log {log}: {e}")
    out.data["ownerApproved"] = approval
    out.say(f"owner approval logged: {approval}")


def cmd_finish(a, out):
    check_name(a.name)
    repo, br, owner = a.repo, f"agent/{a.name}", f"devtree:{a.name}"
    wt = agent_wt(repo, a.name)
    if not wt:
        raise Exit(1, f"no worktree for {br}")
    W = lambda *args, check=True: run(["-C", str(wt), *args], repo, check)
    ensure_hooks(repo, out)
    if porcelain(wt, repo):
        W("add", "-A")
        W("commit", "-m", a.message)
        out.say("committed pending changes")
    protected_gate(wt, repo, br, a, out, "early")
    codex_review(wt, repo, a.message.splitlines()[0] if a.message else a.name, a, out)
    e2e_gate(wt, repo, a, out)
    for attempt in (1, 2):
        r = W("rebase", "main", check=False)
        if r.returncode:
            files = [f for f in W("diff", "--name-only", "--diff-filter=U", check=False).stdout.splitlines() if f]
            W("rebase", "--abort", check=False)
            for f in files:
                out.say(f"conflict: {f}")
            raise Exit(2, f"rebase onto main failed and was aborted: {clean_err(r.stderr)}", conflicts=files)
        protected_gate(wt, repo, br, a, out, "final")  # after the rebase: main's list and diff as they are merged
        guard_gate(wt, repo, "main", "HEAD", out, "final")  # 0.8.0: the repository's own check of what is merged
        info, st = acquire(a.locks, MERGE_LOCK, owner, 10, 120, out)
        if st == "busy":
            raise Exit(3, f"lock {MERGE_LOCK} busy: {describe(info)}", holder=info)
        try:
            heal_phantom(repo, out)  # leftovers of an earlier merge must not block this one
            dirty = main_dirty(repo)
            if dirty:
                for l in dirty:
                    out.say(l)
                raise Exit(3, "main tree has uncommitted changes", dirty=dirty)
            head = run(["symbolic-ref", "--short", "-q", "HEAD"], repo, check=False).stdout.strip()
            if head != "main":
                raise Exit(3, f"main tree is on {head or 'detached HEAD'}, not main")
            m = run(["merge", "--ff-only", br], repo, check=False)
            if m.returncode == 0:
                heal_phantom(repo, out)
        finally:
            try:
                release(a.locks, MERGE_LOCK, owner, False, out)
            except Exit as e:
                out.say(f"warning: {e}")
        if m.returncode == 0:
            break
        moved = run(["merge-base", "--is-ancestor", "main", br], repo, check=False).returncode != 0
        if not moved:
            raise Exit(1, f"fast-forward failed: {clean_err(m.stderr)}")
        if attempt == 2:
            raise Exit(2, "main moved again during retry; fast-forward failed")
        out.say("main moved; rebasing again")
    if not a.keep:
        unlink_node_modules(wt)
        clean = not porcelain(wt, repo)
        run(["worktree", "remove", *(["--force"] if clean else []), str(wt)], repo)
        run(["branch", "-d", br], repo)
        out.say(f"removed worktree {wt} and branch {br}")
    head = run(["log", "-1", "--format=%h %s"], repo).stdout.strip()
    out.data.update(name=a.name, head=head, kept=a.keep)
    out.say(f"main HEAD: {head}")
    publish_or_exit(a, out)


def _norm(path):
    """Normalized absolute path for comparison."""
    return os.path.normcase(os.path.abspath(path))


def _drop_drive_junk(repo):
    """Delete desktop.ini files Google Drive puts into .git/refs and .git/logs (git reads them as broken refs)."""
    removed = 0
    for sub in ("refs", "logs"):
        for root, _dirs, files in os.walk(os.path.join(repo, ".git", sub)):
            for f in files:
                if f.lower() == "desktop.ini":
                    try:
                        os.remove(os.path.join(root, f))
                        removed += 1
                    except OSError:
                        pass
    return removed


def publish(a, out) -> dict:
    """Push main to origin and fast-forward the read-only mirror. Never raises."""
    result = {"pushed": False, "mirror": None}

    if not a.no_push:
        try:
            remote = run(["remote", "get-url", "origin"], a.repo, check=False)
            if remote.returncode == 0 and repo_settings(a.repo).get("guard"):
                # 0.8.0: what has not reached origin yet passes the repository's guard before it is pushed
                upstream = "origin/main" if run(["rev-parse", "-q", "--verify", "origin/main"], a.repo,
                                               check=False).returncode == 0 else "main~0"
                try:
                    guard_gate(a.repo, a.repo, upstream, "main", out, "push")
                except Exit as e:
                    result["guard"] = False
                    out.say(f"push refused: {e}")
                    remote = None
            if remote is not None and remote.returncode == 0:
                r = run(["push", "origin", "main"], a.repo, check=False)
                if r.returncode == 0:
                    result["pushed"] = True
                    out.say("pushed main to origin")
                else:
                    out.say("warning: push failed: " + clean_err(r.stderr or r.stdout))
        except Exception as e:
            out.say("warning: push failed: " + clean_err(str(e)))

    mirror = a.mirror
    if mirror:
        try:
            if not os.path.isdir(os.path.join(mirror, ".git")):
                out.say(f"warning: mirror not updated: not a git clone: {mirror}")
            elif _norm(mirror) != _norm(a.repo):
                result["mirror"] = False
                _drop_drive_junk(mirror)
                r = run(["fetch", "origin"], mirror, check=False)
                if r.returncode != 0:
                    out.say("warning: mirror not updated: fetch failed: " + clean_err(r.stderr or r.stdout))
                else:
                    r = run(["merge", "--ff-only", "origin/main"], mirror, check=False)
                    if r.returncode != 0:
                        out.say("warning: mirror not updated: merge failed: " + clean_err(r.stderr or r.stdout))
                    else:
                        heal_phantom(mirror, out)  # Drive sync can leave files falsely shown as modified
                        result["mirror"] = True
                        out.say(f"mirror updated: {mirror}")
        except Exception as e:
            result["mirror"] = False
            out.say("warning: mirror not updated: " + clean_err(str(e)))

    out.data["publish"] = result
    return result


def publish_or_exit(a, out):
    """publish(); a push refused by the guard ends the command with exit 7 (main stays merged locally, unpushed)."""
    result = publish(a, out)
    if result.get("guard") is False:
        raise Exit(7, "main was not pushed: the guard found something in origin/main..main; fix it with a new "
                      "commit that removes it AND rewrite the unpushed commits (they are not public yet)")
    return result


def cmd_sync(a, out):
    """Push main to origin and update the mirror."""
    ensure_hooks(a.repo, out)
    publish_or_exit(a, out)


def cmd_drop(a, out):
    check_name(a.name)
    br, wt = f"agent/{a.name}", agent_wt(a.repo, a.name)
    has_branch = branch_exists(a.repo, br)
    if not has_branch and not wt:
        raise Exit(1, f"nothing to drop for {br}")
    if has_branch and not a.force:
        n = int(run(["rev-list", "--count", f"main..{br}"], a.repo).stdout.strip() or 0)
        if n:
            raise Exit(1, f"{br} has {n} commit(s) not in main; use --force")
    if wt:
        unlink_node_modules(wt)
        run(["worktree", "remove", *(["--force"] if a.force else []), str(wt)], a.repo)
        out.say(f"removed worktree {wt}")
    run(["worktree", "prune"], a.repo, check=False)
    if has_branch:
        run(["branch", "-D" if a.force else "-d", br], a.repo)
        out.say(f"deleted branch {br}")
    out.data.update(name=a.name, dropped=True)


# ---------------- CLI ----------------

def build_parser():
    def add_globals(p, real):
        S = argparse.SUPPRESS
        p.add_argument("--json", action="store_true", default=False if real else S, help="print one JSON object")
        p.add_argument("--repo", default=DEF_REPO if real else S, help="main working tree")
        p.add_argument("--work-root", default=DEF_WORK if real else S, help="root for agent worktrees")
        p.add_argument("--locks", default=DEF_LOCKS if real else S, help="lock directory")
        p.add_argument("--mirror", default=DEF_MIRROR if real else S,
                       help='read-only mirror clone updated after finish; "" to skip')
        p.add_argument("--no-push", action="store_true", default=False if real else S,
                       help="do not push main to origin")

    common = Parser(add_help=False)
    add_globals(common, False)  # allow global options after the subcommand too
    ap = Parser(prog="devtree", description="Isolated git worktrees for AI agents + atomic locks.")
    add_globals(ap, True)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("start", parents=[common], help="create agent/NAME branch and worktree")
    p.add_argument("name")
    p.add_argument("--base", default="main")
    sub.add_parser("list", parents=[common], help="list agent worktrees")
    sub.add_parser("sync", parents=[common], help="push main to origin and update the mirror")
    p = sub.add_parser("finish", parents=[common], help="commit, rebase, fast-forward main")
    p.add_argument("name")
    p.add_argument("-m", "--message", required=True)
    p.add_argument("--keep", action="store_true", help="keep worktree and branch")
    p.add_argument("--review", choices=["auto", "on", "off"], default="auto",
                   help="Codex cross-review of the diff before merge (auto: only when code changed)")
    p.add_argument("--review-model", default=None, help="Preferred Codex review model; resolved against the live catalog")
    p.add_argument("--review-timeout", type=int, default=600, help="seconds")
    p.add_argument("--e2e", choices=["auto", "on", "off"], default="auto",
                   help="aiwb-e2e before merge (auto: only when co-web, co-web-auth, co-agent, bridge or co-desktop changed)")
    p.add_argument("--e2e-timeout", type=int, default=1500, help="seconds")
    p.add_argument("--e2e-expect", default=None, help="regex for the e2e reply (default: aiwb-e2e's 'pong')")
    p.add_argument("--e2e-host", choices=["auto", "home-main", "site"], default="auto",
                   help="executor for the e2e job (auto: home-main if a Bridge slot frees in time, else site)")
    p.add_argument("--e2e-queue-wait", type=int, default=120, help="seconds to wait for a free home-main slot")
    p.add_argument("--force", action="store_true", help="merge even when the review says BLOCK or e2e is red")
    p.add_argument("--owner-approved", default=None, metavar="TEXT",
                   help="the owner's yes for protected paths (chat message number or quote); logged")
    p = sub.add_parser("drop", parents=[common], help="remove worktree and branch")
    p.add_argument("name")
    p.add_argument("--force", action="store_true")

    lk = sub.add_parser("lock", parents=[common], help="atomic file locks")
    ls = lk.add_subparsers(dest="lock_cmd", required=True)
    q = ls.add_parser("acquire", parents=[common], help="acquire a lock")
    q.add_argument("resource")
    q.add_argument("--owner", required=True)
    q.add_argument("--ttl-min", type=int, default=60)
    q.add_argument("--wait-sec", type=float, default=0)
    q = ls.add_parser("release", parents=[common], help="release a lock")
    q.add_argument("resource")
    q.add_argument("--owner", required=True)
    q.add_argument("--force", action="store_true")
    q = ls.add_parser("status", parents=[common], help="show lock status")
    q.add_argument("resource", nargs="?")
    return ap


HANDLERS = {"start": cmd_start, "list": cmd_list, "finish": cmd_finish, "drop": cmd_drop, "lock": cmd_lock,
            "sync": cmd_sync}


def main(argv=None):
    a = build_parser().parse_args(argv)
    out = Out(a.json)
    try:
        HANDLERS[a.cmd](a, out)
        return out.finish(0)
    except Exit as e:
        out.data.update(e.data)
        return out.finish(e.code, str(e))
    except (GitError, OSError) as e:
        return out.finish(1, str(e))
    except KeyboardInterrupt:
        return out.finish(1, "interrupted")


if __name__ == "__main__":
    sys.exit(main())
