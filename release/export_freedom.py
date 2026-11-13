# SPDX-License-Identifier: AGPL-3.0-or-later
"""Freedom F7: export the public `freedom` repository with a clean history.

Takes a commit of this repository (default HEAD), drops the private files listed in
release/freedom-export.json, puts the public files from release/public/ in their place,
optionally adds core tools of the tool library (and the tools the modules list in provides.tools), checks the result for personal data and
secrets and for module ids named by the core (SPEC §3.6 principle 1: a module id of modules/*/module.json as a
string or path in core code), and writes a new git repository with ONE initial commit (SPEC §10.1 item 2).

Nothing is pushed: publishing the repository is the owner's decision.

Since the development direction is reversed (docs/DEVELOPMENT.md) the public repository is the upstream of the core
and the export becomes a CHECK: `guard` scans a tree of the public repository itself and every commit of a range
(added lines, file names, commit messages, author and committer) before it is merged or pushed. `sync` is the
transition helper: it exports a private tree into an existing clone of the public repository as one normal commit.

  python release/export_freedom.py check  [--rev HEAD] [--terms FILE] [--tools DIR]
  python release/export_freedom.py export --out DIR [--rev HEAD] [--terms FILE] [--tools DIR]
                                          [--version 0.1.0] [--author "Name <mail>"] [--force]
  python release/export_freedom.py sync   [--into CLONE] [--rev HEAD] [--tools DIR] [-m MSG] [--author "Name <mail>"]
  python release/export_freedom.py guard  [--repo DIR] [--rev HEAD] [--range A..B] [--require-terms]
  python release/export_freedom.py tools  --into WORKTREE [--tools DIR]   refresh tools/ from the tool library

--owner-config FILE: the owner's part of the config (private files of the owner's tree that are dropped by an export
and must never appear in the public tree, export-only rewrites that name the owner); default
release/owner/freedom-export.owner.json of this tree, else %LOCALAPPDATA%\\AIWorkbench\\freedom-export-owner.json
(environment AIWB_FREEDOM_OWNER_CONFIG overrides both; a clone of the public repository: git config freedom.ownerConfig).

--terms FILE: private words (one per line, '#' comments; a line 're:<regex>' is a regex) that must
not appear in the export: names of people, own domains, hosting account ids, machine names. The
file lives outside the repository (it is personal data itself); default
%LOCALAPPDATA%\\AIWorkbench\\freedom-export-terms.txt if it exists.
Exit codes: 0 clean, 1 error, 2 findings (export not written, commit or push refused).
"""
from __future__ import annotations

import argparse
import datetime
import fnmatch
import hashlib
import io
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tarfile

REPO = pathlib.Path(__file__).resolve().parents[1]
CONFIG = REPO / "release" / "freedom-export.json"
PUBLIC = REPO / "release" / "public"
OWNER_HOME = pathlib.Path(os.environ.get("LOCALAPPDATA", str(pathlib.Path.home()))) / "AIWorkbench"
DEFAULT_TERMS = OWNER_HOME / "freedom-export-terms.txt"
OWNER_CONFIGS = [REPO / "release" / "owner" / "freedom-export.owner.json", OWNER_HOME / "freedom-export-owner.json"]
DEFAULT_CLONE = pathlib.Path(os.environ.get("AIWB_FREEDOM", r"C:\AIWorkbench\freedom"))
DEFAULT_TOOLS = pathlib.Path(os.environ.get("AIWB_TOOLS", r"C:\AIWorkbench\tools"))
NEUTRAL_AUTHOR = "Freedom contributors <noreply@example.org>"
# merged from the owner's part: lists are added to the public ones
OWNER_KEYS = ("exclude", "rewrite", "patterns", "allowedEmails", "emailAllow", "termsAllow", "tools", "toolsExclude")

TEXT_EXT = {".py", ".php", ".cs", ".ts", ".tsx", ".js", ".mjs", ".cjs", ".json", ".md", ".txt", ".ps1", ".cmd",
            ".bat", ".sh", ".sql", ".yml", ".yaml", ".toml", ".xml", ".csproj", ".props", ".nsi", ".html", ".css",
            ".ini", ".cfg", ".conf", ".example", ".template", ".plist", ".svg", ".htaccess", ""}


def owner_config_path(explicit: pathlib.Path | None = None) -> pathlib.Path | None:
    if explicit:
        return explicit
    env = os.environ.get("AIWB_FREEDOM_OWNER_CONFIG")
    if env:
        return pathlib.Path(env)
    found = next((p for p in OWNER_CONFIGS if p.is_file()), None)
    if found:
        return found
    # a maintainer's clone of the public repository names it in its local git config (also its worktrees)
    try:
        r = subprocess.run(["git", "config", "--local", "--get", "freedom.ownerConfig"], cwd=REPO, capture_output=True,
                           text=True)
    except OSError:
        return None
    value = r.stdout.strip() if r.returncode == 0 else ""
    return (REPO / value).resolve() if value else None


def load_config(owner: pathlib.Path | None = None, use_owner: bool = True) -> dict:
    """The public config (release/freedom-export.json, ships with the public tree) plus the owner's part if any."""
    cfg = json.loads(CONFIG.read_text(encoding="utf-8"))
    path = owner_config_path(owner) if use_owner else None
    cfg["ownerConfig"] = None
    if path:
        if not path.is_file():
            raise RuntimeError(f"owner config not found: {path}")
        extra = json.loads(path.read_text(encoding="utf-8-sig"))
        for k in OWNER_KEYS:
            if k in extra:
                cfg[k] = list(cfg.get(k, [])) + list(extra[k])
        for mid, globs in (extra.get("moduleIdAllow") or {}).items():
            cfg.setdefault("moduleIdAllow", {}).setdefault(mid, [])
            cfg["moduleIdAllow"][mid] = cfg["moduleIdAllow"][mid] + list(globs)
        cfg["ownerConfig"] = str(path)
    for k in ("exclude", "rewrite", "patterns", "tools", "toolsExclude"):
        cfg.setdefault(k, [])
    return cfg


def match_any(path: str, globs: list[str]) -> bool:
    p = path.lower()
    for g in globs:
        g = g.lower()
        if g.endswith("/") and p.startswith(g):
            return True
        if fnmatch.fnmatchcase(p, g):
            return True
    return False


def git(*args: str, cwd: pathlib.Path = REPO, data: bytes | None = None) -> bytes:
    r = subprocess.run(["git", *args], cwd=cwd, input=data, capture_output=True)
    if r.returncode:
        raise RuntimeError(f"git {' '.join(args)}: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout


# executable files of the last tree read by source_files (git mode 100755): export and sync keep the bit
EXEC_PATHS: set[str] = set()


def source_files(rev: str, repo: pathlib.Path = REPO) -> dict[str, bytes]:
    """All files of `rev` (git archive, so the working tree state does not matter)."""
    raw = git("archive", "--format=tar", rev, cwd=repo)
    files: dict[str, bytes] = {}
    EXEC_PATHS.clear()
    with tarfile.open(fileobj=io.BytesIO(raw)) as tf:
        for m in tf.getmembers():
            if m.isfile():
                files[m.name] = tf.extractfile(m).read()
                if m.mode & 0o111:
                    EXEC_PATHS.add(m.name)
    return files


def tool_files(tools_dir: pathlib.Path, names: list[str]) -> dict[str, bytes]:
    out: dict[str, bytes] = {}
    skip_dirs = {"__pycache__", "node_modules", ".pytest_cache", "out", "tmp", ".git"}
    for name in names:
        root = tools_dir / name
        if not root.is_dir():
            raise RuntimeError(f"tool not found: {root}")
        for p in sorted(root.rglob("*")):
            if p.is_dir() or any(part in skip_dirs for part in p.relative_to(tools_dir).parts):
                continue
            if p.name == "desktop.ini" or p.suffix in (".pyc", ".log"):
                continue
            out["tools/" + p.relative_to(tools_dir).as_posix()] = p.read_bytes()
    return out


def load_terms(path: pathlib.Path | None) -> list[tuple]:
    """(rule name, regex, lowercase literal or None): the literal is a fast pre-filter (substring test) before the
    regex with its word-boundary lookarounds, which is slow on large files."""
    if not path or not path.exists():
        return []
    terms = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("re:"):
            terms.append(("private term (regex)", re.compile(line[3:], re.I), None))
        else:
            # also inside identifiers: a digit or a capital letter after it, a capital or a digit before it
            # (anna2, CoAnna, annaNet); a plain lowercase word around it is another word (annals)
            pat = r"(?:(?<![A-Za-z0-9])|(?<=[a-z0-9])(?=[A-Z])|(?<=[0-9_]))(?i:" + re.escape(line) + r")(?:(?![A-Za-z0-9])|(?=[A-Z0-9_]))"
            terms.append(("private term", re.compile(pat), line.lower()))
    return terms


def build_tree(cfg: dict, rev: str, tools_dir: pathlib.Path | None) -> tuple[dict[str, bytes], list[str]]:
    files = source_files(rev)
    if not cfg.get("ownerConfig"):
        # without the owner's part the private files of the owner's tree would not be dropped
        raise RuntimeError("export/sync needs the owner's part of the config (--owner-config); guard needs none")
    dropped = sorted(p for p in files if match_any(p, cfg["exclude"]))
    tree = {p: b for p, b in files.items() if p not in dropped}
    for p in sorted(PUBLIC.rglob("*")):
        if p.is_file() and p.name != "desktop.ini":
            tree[p.relative_to(PUBLIC).as_posix()] = p.read_bytes()
    if tools_dir:
        # toolsExclude: module tools that stay private (employer-specific); the module ships, the user brings own tools
        keep = (set(cfg.get("tools", [])) | set(module_tools(tree))) - set(cfg.get("toolsExclude", []))
        tree.update(tool_files(tools_dir, sorted(keep)))
    for rule in cfg.get("rewrite", []):
        rx = re.compile(rule["regex"])
        for path in [p for p in tree if match_any(p, rule["files"])]:
            text = tree[path].decode("utf-8")
            new, n = rx.subn(rule["replace"], text)
            # since the reversed direction (docs/DEVELOPMENT.md) the core tree is already public: a rewrite that
            # finds nothing is done, not stale; the guard still refuses the private text wherever it reappears
            if n:
                tree[path] = new.encode("utf-8")
    return tree, dropped


EMAIL_RE = re.compile(r"(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")


def _rules(cfg: dict) -> list[tuple]:
    return [(r["name"], re.compile(r["regex"], re.I if r.get("ignoreCase", True) else 0), r.get("allow", []))
            for r in cfg["patterns"]]


def scan_text(path: str, text: str, cfg: dict, terms: list[tuple], rules: list[tuple] | None = None,
              lineno_of=None) -> list[dict]:
    """Findings of one text. Each rule first searches the whole text (most files have no hit), only then line by line.
    lineno_of(i) maps the index of a line of `text` to the line number reported (diff hunks)."""
    rules = _rules(cfg) if rules is None else rules
    allowed_emails = [re.compile(a, re.I) for a in cfg.get("allowedEmails", [])]
    low = text.lower()
    active = [(name, rx) for name, rx, allow in rules if not match_any(path, allow) and rx.search(text)]
    if not match_any(path, cfg.get("termsAllow", [])):
        active += [(name, rx) for name, rx, lit in terms if (lit is None or lit in low) and rx.search(text)]
    check_mail = "@" in text and not match_any(path, cfg.get("emailAllow", []))
    if not active and not check_mail:
        return []
    num = lineno_of or (lambda i: i + 1)
    out = []
    for i, line in enumerate(text.split("\n")):  # the same split as the diff line numbers (form feeds stay inside)
        for name, rx in active:
            if rx.search(line):
                hidden = name.startswith("private term")
                out.append({"file": path, "line": num(i), "rule": name,
                            "text": "(hidden: private term)" if hidden else line.strip()[:160]})
        if check_mail and "@" in line:
            for m in EMAIL_RE.finditer(line):
                addr = m.group(0)
                if not any(a.fullmatch(addr) for a in allowed_emails):
                    out.append({"file": path, "line": num(i), "rule": "email address", "text": addr})
    return out


def decode_text(data: bytes) -> str:
    """Text of a file: UTF-16 with a byte order mark (files written by Windows tools, e.g. exported task XML), else UTF-8."""
    if data[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return data.decode("utf-16", errors="replace")
    return data.decode("utf-8", errors="replace")


def is_binary(path: str, data: bytes) -> bool:
    if data[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return False   # UTF-16 text has zero bytes but is scanned like any text
    return pathlib.PurePosixPath(path).suffix.lower() not in TEXT_EXT and b"\0" in data[:4096]


def scan(tree: dict[str, bytes], cfg: dict, terms: list[tuple]) -> list[dict]:
    rules = _rules(cfg)
    findings = []
    for path, data in sorted(tree.items()):
        if is_binary(path, data):
            continue
        findings += scan_text(path, decode_text(data), cfg, terms, rules)
    return findings


CODE_EXT = {".py", ".php", ".cs", ".ts", ".tsx", ".js", ".mjs", ".cjs", ".json", ".ps1", ".cmd", ".bat", ".sh", ".sql",
            ".html", ".css", ".csproj", ".xml", ".nsi"}
TEST_GLOBS = ["*/tests/*", "*.selftest.ts", "*/test_*.py", "*.test.ts"]


def module_ids(tree: dict[str, bytes]) -> list[str]:
    """Ids of the modules of the tree (modules/<folder>/module.json)."""
    ids = set()
    for path, data in tree.items():
        parts = path.split("/")
        if len(parts) == 3 and parts[0] == "modules" and parts[2] == "module.json":
            try:
                mid = json.loads(data.decode("utf-8")).get("id")
            except (ValueError, UnicodeDecodeError, AttributeError):
                continue
            if isinstance(mid, str) and re.fullmatch(r"[a-z0-9][a-z0-9-]{0,62}", mid):
                ids.add(mid)
    return sorted(ids)


def module_tools(tree: dict[str, bytes]) -> list[str]:
    """Library tools the modules of the tree list in provides.tools: they ship in tools/ of the export, where
    install/freedom_modules.py takes them from (toolbox install --from)."""
    names = set()
    for path, data in tree.items():
        parts = path.split("/")
        if len(parts) == 3 and parts[0] == "modules" and parts[2] == "module.json":
            try:
                tools = (json.loads(data.decode("utf-8")).get("provides") or {}).get("tools") or []
            except (ValueError, UnicodeDecodeError, AttributeError):
                continue
            for t in tools:
                n = t if isinstance(t, str) else (t or {}).get("name") if isinstance(t, dict) else None
                if isinstance(n, str) and re.fullmatch(r"[a-z0-9][a-z0-9-]{0,62}", n):
                    names.add(n)
    return sorted(names)


def core_module_findings(tree: dict[str, bytes], cfg: dict) -> list[dict]:
    """SPEC §3.6 principle 1: the core names no module. A module id as a string literal ('geo-presence', "community")
    or a path (modules/<id>/) in a code file outside modules/ and tools/ is a finding. Tests may use modules as
    fixtures; moduleIdAllow {id or "*": [globs]} in the config excuses other files (say why in the config comment)."""
    ids = module_ids(tree)
    if not ids:
        return []
    allow = cfg.get("moduleIdAllow", {})
    rx = re.compile(r"""(?:['"`]|\bmodules[/\\])(""" + "|".join(re.escape(i) for i in ids) + r""")(?=['"`/\\])""")
    out = []
    for path, data in sorted(tree.items()):
        # release/ holds the checks themselves and their configs: they name modules by design
        if path.startswith(("modules/", "tools/", "release/")) or pathlib.PurePosixPath(path).suffix.lower() not in CODE_EXT:
            continue
        if match_any(path, TEST_GLOBS + allow.get("*", [])):
            continue
        for lineno, line in enumerate(data.decode("utf-8", errors="replace").splitlines(), 1):
            for m in rx.finditer(line):
                if not match_any(path, allow.get(m.group(1), [])):
                    out.append({"file": path, "line": lineno, "rule": f"module id in the core: {m.group(1)}",
                                "text": line.strip()[:160]})
    return out


def module_signature_findings(tree: dict[str, bytes]) -> list[dict]:
    """A module of the tree must verify as published (install/freedom_modules.py verify): every file its signed
    freedom-manifest.json lists is in the tree with that sha256. A rewrite or toolsExclude that changes or drops a file
    of a signed module, or an edit without re-signing, breaks the module for every user (Freedom 0.2.0 shipped
    task-hints that way: the export rewrote ui/hint-data.js after signing; found by the rehearsal of 13.11)."""
    out = []
    for path, data in sorted(tree.items()):
        parts = path.split("/")
        if not (len(parts) == 3 and parts[0] == "modules" and parts[2] == "freedom-manifest.json"):
            continue
        try:
            files = json.loads(data.decode("utf-8")).get("files") or []
            if not isinstance(files, list) or not all(isinstance(f, dict) for f in files):
                raise ValueError("files is not a list of objects")
        except (ValueError, UnicodeDecodeError, AttributeError):
            out.append({"file": path, "line": 1, "rule": "module manifest unreadable", "text": ""})
            continue
        for f in files:
            rel = f"modules/{parts[1]}/{f.get('path', '')}"
            have = tree.get(rel)
            if have is None:
                out.append({"file": rel, "line": 1, "rule": "signed module file missing", "text": ""})
            elif hashlib.sha256(have).hexdigest() != f.get("sha256"):
                out.append({"file": rel, "line": 1, "rule": "signed module file changed after signing",
                            "text": "the module would not verify: make the source neutral and re-sign the module"})
    return out


def write_repo(tree: dict[str, bytes], out: pathlib.Path, version: str, author: str | None, force: bool) -> str:
    if out.exists() and any(out.iterdir()):
        if not force:
            raise RuntimeError(f"{out} is not empty (use --force to replace it)")
        def writable(func, path, _exc):  # git objects are read-only on Windows
            os.chmod(path, 0o600)
            func(path)
        shutil.rmtree(out, onerror=writable)
    out.mkdir(parents=True, exist_ok=True)
    for path, data in tree.items():
        dest = out / path
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)
    git("init", "-q", "-b", "main", cwd=out)
    git("add", "-A", cwd=out)
    set_exec_modes(out, tree)
    env_author = []
    if author:
        name, mail = split_author(author)
        env_author = ["-c", f"user.name={name}", "-c", f"user.email={mail}"]
    msg = f"Freedom {version}: initial public release (AGPL-3.0-or-later)"
    git(*env_author, "commit", "-q", "-m", msg, cwd=out)
    return git("rev-parse", "HEAD", cwd=out).decode().strip()


def split_author(author: str) -> tuple[str, str]:
    m = re.fullmatch(r"\s*(.+?)\s*<([^>]+)>\s*", author or "")
    if not m:
        raise RuntimeError("--author must look like 'Name <mail>'")
    return m.group(1), m.group(2)


# ---------------------------------------------------------------- guard: the export as a check of the public tree

EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
GITHUB_NOREPLY = re.compile(r"[A-Za-z0-9._+-]+@users\.noreply\.github\.com", re.I)
HUNK = re.compile(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")


def path_findings(paths, cfg: dict, terms: list[tuple], where: str = "") -> list[dict]:
    """A file of the owner's private list (exclude of the owner config) or a private term in a file name."""
    out = []
    for p in sorted(paths):
        if match_any(p, cfg.get("exclude", [])):
            out.append({"file": p, "line": 0, "rule": "private file of the owner's tree" + where, "text": p})
        for name, rx, lit in terms:
            if (lit is None or lit in p.lower()) and rx.search(p):
                out.append({"file": p, "line": 0, "rule": name + " in a file name" + where,
                            "text": "(hidden: private term)"})
    return out


def guard_tree(repo: pathlib.Path, rev: str, cfg: dict, terms: list[tuple]) -> tuple[int, list[dict]]:
    tree = source_files(rev, repo)
    return len(tree), (path_findings(tree, cfg, terms) + scan(tree, cfg, terms) + core_module_findings(tree, cfg)
                       + module_signature_findings(tree))


def commit_meta_findings(repo: pathlib.Path, sha: str, cfg: dict, terms: list[tuple]) -> list[dict]:
    """Author, committer and message of one commit: they are published with it."""
    an, ae, cn, ce, msg = git("show", "-s", "--format=%an%x00%ae%x00%cn%x00%ce%x00%B", sha, cwd=repo) \
        .decode("utf-8", errors="replace").split("\0", 4)
    where = f"commit {sha[:10]}"
    allowed = [re.compile(x, re.I) for x in cfg.get("allowedEmails", [])]
    out = []
    for role, mail in (("author", ae), ("committer", ce)):
        if not GITHUB_NOREPLY.fullmatch(mail) and not any(x.fullmatch(mail) for x in allowed):
            out.append({"file": where, "line": 0, "rule": f"{role} e-mail outside the example domains", "text": mail})
    # a GitHub noreply address is public by design (the account that pushes); the rest is checked as text
    text = GITHUB_NOREPLY.sub("<noreply>", f"{an}\n{cn}\n{msg}")
    out += scan_text(where, text, cfg, terms)
    return out


C_ESCAPES = {"a": 7, "b": 8, "t": 9, "n": 10, "v": 11, "f": 12, "r": 13, '"': 34, "\\": 92}


def unquote_path(s: str) -> str:
    """A path of a git diff header: as is, or C-quoted ("b/caf\\303\\251 \\"x\\".txt") when it has special bytes."""
    s = s.rstrip("\r")
    if s.endswith("\t"):  # git ends the header name with a tab when it contains a space
        s = s[:-1]
    if not (len(s) >= 2 and s[0] == '"' and s[-1] == '"'):
        return s
    body, out, i = s[1:-1], bytearray(), 0
    while i < len(body):
        c = body[i]
        if c == "\\" and i + 1 < len(body):
            n = body[i + 1]
            if n in "01234567" and i + 3 < len(body) + 1 and all(x in "01234567" for x in body[i + 1:i + 4]):
                out.append(int(body[i + 1:i + 4], 8))
                i += 4
                continue
            out.append(C_ESCAPES.get(n, ord(n)))
            i += 2
            continue
        out += c.encode("utf-8")
        i += 1
    return out.decode("utf-8", errors="replace")


def commit_diff_findings(repo: pathlib.Path, sha: str, cfg: dict, terms: list[tuple]) -> list[dict]:
    """Added lines and new file names of one commit (against its first parent): the history keeps them even when a
    later commit removes them."""
    parents = git("rev-list", "--parents", "-n", "1", sha, cwd=repo).decode().split()[1:]
    base = parents[0] if parents else EMPTY_TREE
    # fixed prefixes and no textconv: the user's git config (diff.noprefix, diff.*Prefix, textconv) must not hide lines
    raw = git("diff", "-U0", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--text",
              "--src-prefix=a/", "--dst-prefix=b/", base, sha, cwd=repo)
    rules = _rules(cfg)
    where = f" (commit {sha[:10]})"
    out, added_paths = [], []
    path, lines, nums, num = None, [], [], 0

    def flush():
        if path and lines:
            for f in scan_text(path, "\n".join(lines), cfg, terms, rules, lineno_of=lambda i: nums[i]):
                f["rule"] += where
                out.append(f)

    in_header = False
    for line in raw.decode("utf-8", errors="replace").split("\n"):
        if line.startswith("diff --git "):
            flush()
            path, lines, nums, in_header = None, [], [], True
        elif in_header and line.startswith("+++ "):
            # only in the file header: an added content line "++ x" also reads "+++ x" in the hunk body
            name = unquote_path(line[4:])
            path = name[2:] if name.startswith("b/") else None
        elif line.startswith("@@") and (in_header or path):
            in_header = False
            m = HUNK.match(line)
            num = int(m.group(1)) if m else 0
        elif not in_header and line.startswith("+") and path:
            lines.append(line[1:])
            nums.append(num)
            num += 1
    flush()
    names = git("diff", "--name-only", "-z", "--diff-filter=AR", "--no-renames", base, sha, cwd=repo)
    added_paths = [p for p in names.decode("utf-8", "replace").split("\0") if p]
    return path_findings(added_paths, cfg, terms, where) + out


def tag_findings(repo: pathlib.Path, rev: str, cfg: dict, terms: list[tuple]) -> list[dict]:
    """An annotated tag publishes its tagger and message with it."""
    if git("cat-file", "-t", rev, cwd=repo).decode().strip() != "tag":
        return []
    raw = git("cat-file", "tag", rev, cwd=repo).decode("utf-8", errors="replace")
    head, _, msg = raw.partition("\n\n")
    where = f"tag {rev[:10]}"
    out = []
    for line in head.splitlines():
        if line.startswith("tagger "):
            m = re.match(r"tagger (.*) <([^>]*)>", line)
            if m:
                mail = m.group(2)
                allowed = [re.compile(x, re.I) for x in cfg.get("allowedEmails", [])]
                if not GITHUB_NOREPLY.fullmatch(mail) and not any(x.fullmatch(mail) for x in allowed):
                    out.append({"file": where, "line": 0, "rule": "tagger e-mail outside the example domains", "text": mail})
                out += scan_text(where, GITHUB_NOREPLY.sub("<noreply>", m.group(1)), cfg, terms)
        elif line.startswith("tag "):
            out += scan_text(where, line[4:], cfg, terms)
    return out + scan_text(where, GITHUB_NOREPLY.sub("<noreply>", msg), cfg, terms)


def guard(repo: pathlib.Path, rev: str, rng: str | None, cfg: dict, terms: list[tuple]) -> dict:
    files, findings = guard_tree(repo, rev, cfg, terms)
    findings += tag_findings(repo, rev, cfg, terms)
    commits = []
    if rng:
        commits = git("rev-list", "--reverse", *rng.split(), cwd=repo).decode().split()  # "A..B" or "X --not --remotes=R"
        for sha in commits:
            findings += commit_meta_findings(repo, sha, cfg, terms) + commit_diff_findings(repo, sha, cfg, terms)
    return {"repo": str(repo), "rev": git("rev-parse", rev, cwd=repo).decode().strip(), "files": files,
            "commits": len(commits), "privateTerms": len(terms), "ownerConfig": cfg.get("ownerConfig"),
            "findings": findings}


# ---------------------------------------------------------------- sync: one normal commit on a clone

def set_exec_modes(repo: pathlib.Path, tree: dict[str, bytes]) -> None:
    """Index modes = the executable bits of the exported files (EXEC_PATHS, from the source commit): on Windows git
    does not see them in the working tree, and a hook without +x is silently ignored on Unix."""
    staged = git("ls-files", "-s", "-z", cwd=repo).decode("utf-8").split("\0")
    for entry in staged:
        if not entry:
            continue
        meta, path = entry.split("\t", 1)
        mode = meta.split()[0]
        want = path in EXEC_PATHS
        if path in tree and want != (mode == "100755") and mode in ("100644", "100755"):
            git("update-index", "--chmod=" + ("+x" if want else "-x"), "--", path, cwd=repo)


def sync_clone(tree: dict[str, bytes], clone: pathlib.Path, message: str, author: str | None) -> dict:
    """Make the working tree of the public clone equal to `tree` and commit the difference on its main branch."""
    if not (clone / ".git").exists():
        raise RuntimeError(f"not a git clone: {clone} (git clone the public repository there first)")
    if git("status", "--porcelain", cwd=clone).strip():
        raise RuntimeError(f"{clone} has uncommitted changes")
    branch = git("symbolic-ref", "--short", "-q", "HEAD", cwd=clone).decode().strip()
    if branch != "main":
        raise RuntimeError(f"{clone} is on {branch or 'a detached HEAD'}, not main")
    tracked = [p for p in git("ls-files", "-z", cwd=clone).decode("utf-8").split("\0") if p]
    removed = sorted(p for p in tracked if p not in tree)
    for p in removed:
        (clone / p).unlink()
    root = clone.resolve()
    written = 0
    for path, data in tree.items():
        dest = clone / path
        # never write through a link: a tracked symlink (or a link in a parent folder) could point outside the clone
        for q in [dest, *dest.parents]:
            if q == clone or q == root:
                break
            if q.is_symlink():
                raise RuntimeError(f"{q} is a symbolic link; sync writes only regular files inside the clone")
        if dest.is_file() and dest.read_bytes() == data:
            continue
        dest.parent.mkdir(parents=True, exist_ok=True)
        if not dest.resolve().is_relative_to(root):
            raise RuntimeError(f"{dest} resolves outside the clone")
        dest.write_bytes(data)
        written += 1
    git("add", "-A", cwd=clone)
    set_exec_modes(clone, tree)
    if subprocess.run(["git", "diff", "--cached", "--quiet"], cwd=clone).returncode == 0:
        return {"commit": None, "written": 0, "removed": [], "stat": "no change"}
    ident = []
    if author:
        name, mail = split_author(author)
        ident = ["-c", f"user.name={name}", "-c", f"user.email={mail}"]
    elif not git("config", "--get", "user.email", cwd=clone).strip():
        name, mail = split_author(NEUTRAL_AUTHOR)
        ident = ["-c", f"user.name={name}", "-c", f"user.email={mail}"]
    stat = git("diff", "--cached", "--shortstat", cwd=clone).decode().strip()
    git(*ident, "commit", "-q", "-m", message, cwd=clone)
    return {"commit": git("rev-parse", "HEAD", cwd=clone).decode().strip(), "written": written, "removed": removed,
            "stat": stat}


def refresh_tools(into: pathlib.Path, tools_dir: pathlib.Path, cfg: dict, terms: list[tuple]) -> dict:
    """tools/ of a working tree := the core tools of the config + the tools its modules list, from the tool library.
    Checked first; nothing is written when the check finds something. Committing is left to the caller (devtree)."""
    mods = {p.relative_to(into).as_posix(): p.read_bytes() for p in sorted((into / "modules").glob("*/module.json"))}
    keep = sorted((set(cfg.get("tools", [])) | set(module_tools(mods))) - set(cfg.get("toolsExclude", [])))
    new = tool_files(tools_dir, keep)
    findings = scan(new, cfg, terms)
    if findings:
        return {"tools": keep, "findings": findings, "written": 0}
    old = into / "tools"
    for q in (into, old):
        if q.is_symlink() or (hasattr(q, "is_junction") and q.is_junction()):
            raise RuntimeError(f"{q} is a link: tools refreshes only a real tools/ folder of a working tree")
    if old.is_dir():
        for p in sorted(old.rglob("*"), reverse=True):
            if p.is_file() or p.is_symlink():
                p.unlink()
            elif p.is_dir() and not any(p.iterdir()):
                p.rmdir()
    for path, data in new.items():
        dest = into / path
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)
    return {"tools": keep, "findings": [], "written": len(new)}


def print_findings(findings: list[dict]) -> None:
    for f in findings:
        print(f"  {f['file']}:{f['line']}: {f['rule']}: {f['text']}")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["check", "export", "sync", "guard", "tools"])
    ap.add_argument("--rev", default="HEAD")
    ap.add_argument("--terms", type=pathlib.Path, default=DEFAULT_TERMS)
    ap.add_argument("--require-terms", action="store_true",
                    help="fail when the private terms file is missing (the owner's clone; CI has no terms)")
    ap.add_argument("--owner-config", type=pathlib.Path, default=None, help="the owner's part of the config")
    ap.add_argument("--tools", type=pathlib.Path, help="tool library folder; adds the core tools of the config "
                    "(sync: default %%AIWB_TOOLS%% or C:\\AIWorkbench\\tools)")
    ap.add_argument("--out", type=pathlib.Path)
    ap.add_argument("--into", type=pathlib.Path, default=DEFAULT_CLONE,
                    help="sync: clone of the public repository; tools: the working tree whose tools/ is refreshed")
    ap.add_argument("-m", "--message", default=None, help="sync: commit message")
    ap.add_argument("--repo", type=pathlib.Path, default=REPO, help="guard: repository to check (default: this one)")
    ap.add_argument("--range", default=None, help="guard: commits to check one by one, e.g. origin/main..HEAD")
    ap.add_argument("--version", default="0.1.0")
    ap.add_argument("--author", default=None,
                    help="export: author of the initial commit (default: a neutral placeholder); sync: author of the "
                         "commit (default: the clone's own user.name/user.email, else the placeholder)")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    try:
        if a.require_terms and not (a.terms and a.terms.is_file()):
            raise RuntimeError(f"private terms file not found: {a.terms}")
        terms = load_terms(a.terms)
        if a.cmd == "guard":
            cfg = load_config(a.owner_config)
            report = guard(a.repo.resolve(), a.rev, a.range, cfg, terms)
            findings = report["findings"]
            if a.json:
                print(json.dumps(report, ensure_ascii=False, indent=1))
            else:
                print(f"guard {report['rev'][:10]}: {report['files']} files, {report['commits']} commit(s), "
                      f"{report['privateTerms']} private terms, owner config: {'yes' if report['ownerConfig'] else 'no'}")
                print_findings(findings)
                print(f"{len(findings)} finding(s): nothing may be published; fix the commits (rewrite them, "
                      "they are not public yet) or the public config" if findings else "clean")
            return 2 if findings else 0
        if a.cmd == "tools":
            cfg = load_config(a.owner_config)
            into = a.into.resolve()
            r = refresh_tools(into, a.tools or DEFAULT_TOOLS, cfg, terms)
            if a.json:
                print(json.dumps(r, ensure_ascii=False, indent=1))
            else:
                print_findings(r["findings"])
                print(f"{len(r['findings'])} finding(s) in the tool library; tools/ not changed" if r["findings"] else
                      f"tools/ of {into}: {len(r['tools'])} tools, {r['written']} files; commit with devtree finish")
            return 2 if r["findings"] else 0
        cfg = load_config(a.owner_config)
        tools = a.tools or (DEFAULT_TOOLS if a.cmd == "sync" and DEFAULT_TOOLS.is_dir() else None)
        tree, dropped = build_tree(cfg, a.rev, tools)
        findings = scan(tree, cfg, terms) + core_module_findings(tree, cfg) + module_signature_findings(tree)
        report = {"rev": git("rev-parse", a.rev).decode().strip(), "files": len(tree), "dropped": dropped,
                  "privateTerms": len(terms), "findings": findings}
        if a.cmd == "export" and not findings:
            if not a.out:
                raise RuntimeError("export needs --out DIR")
            report["commit"] = write_repo(tree, a.out, a.version, a.author or NEUTRAL_AUTHOR, a.force)
            report["out"] = str(a.out)
        if a.cmd == "sync" and not findings:
            clone = a.into.resolve()
            before = git("rev-parse", "HEAD", cwd=clone).decode().strip()
            msg = a.message or f"Freedom core: sync from the maintainer tree ({datetime.date.today().isoformat()})"
            report["sync"] = sync_clone(tree, clone, msg, a.author)
            if report["sync"]["commit"]:
                # the same check the pre-push hook runs, on the new commit itself
                g = guard(clone, "HEAD", f"{before}..HEAD", load_config(a.owner_config), terms)
                report["guard"] = {"files": g["files"], "findings": g["findings"]}
                findings = g["findings"]
    except Exception as e:  # noqa: BLE001 - CLI boundary
        print(f"error: {e}", file=sys.stderr)
        return 1
    if a.json:
        print(json.dumps(report, ensure_ascii=False, indent=1))
    else:
        print(f"rev {report['rev'][:10]}: {report['files']} files, {len(dropped)} dropped, "
              f"{report['privateTerms']} private terms checked")
        print_findings(findings)
        if findings and "guard" in report:
            print(f"{len(findings)} finding(s) in the new commit of {a.into}: do not push; undo it with "
                  "git reset --hard HEAD~1 there, fix the source or the config")
        elif findings:
            print(f"{len(findings)} finding(s): fix the source or the config; nothing written")
        elif a.cmd == "export":
            print(f"written {report['out']} (commit {report['commit'][:10]}); not pushed")
        elif a.cmd == "sync":
            s = report["sync"]
            if s["commit"]:
                print(f"committed {s['commit'][:10]} in {a.into}: {s['stat']}; {len(s['removed'])} file(s) removed; "
                      "guard clean; not pushed")
                for p in s["removed"]:
                    print(f"  removed {p}")
            else:
                print(f"{a.into} already equals the export; nothing committed")
        else:
            print("clean")
    return 2 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
