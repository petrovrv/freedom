"""aiwb-docs: full-text search over the documents of AI Workbench projects (database schema 7, table docs).

  index   extract text of new/changed files under AI-Workbench/projects (docx, pdf, xlsx, pptx, md, txt, csv, ...)
          and send it to the database; files gone from disk leave the index
  search  find documents by words (the same index CO Web searches)
  stats   number of indexed documents per project

Talks to db.php of the site (AIWB_DB_URL, else install.json site.url + /agent/db.php) with the home PC key (keyring aiwb/co-push, account default).
"""
import argparse
import base64
import gzip
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import warnings
from pathlib import Path


def install_get(key, default=None):
    """One value of install.json: AIWB_INSTALL, else %LOCALAPPDATA%/AIWorkbench/install.json (Windows), else
    ~/.config/aiwb/install.json; the default when the file or the key is missing."""
    path = os.environ.get("AIWB_INSTALL") or (
        os.path.join(os.environ["LOCALAPPDATA"], "AIWorkbench", "install.json")
        if os.name == "nt" and os.environ.get("LOCALAPPDATA")
        else os.path.join(os.path.expanduser("~"), ".config", "aiwb", "install.json"))
    try:
        with open(path, encoding="utf-8-sig") as f:
            cur = json.load(f)
    except (OSError, ValueError):
        return default
    for part in key.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return default
        cur = cur[part]
    return cur



SITE_URL = (install_get("site.url") or "").rstrip("/")
API = os.environ.get("AIWB_DB_URL") or (SITE_URL + "/agent/db.php" if SITE_URL else "")
# AI-Workbench folder in Drive: AIWB_ROOT, else install.json drive.root / drive.workbench
PROJECTS = Path(os.environ.get("AIWB_ROOT") or os.path.join(install_get("drive.root") or r"G:\My Drive",
                                                            install_get("drive.workbench") or "AI-Workbench")) / "projects"
EXTS = {".docx", ".docm", ".pdf", ".xlsx", ".xlsm", ".pptx", ".md", ".txt", ".csv", ".html", ".htm", ".eml"}
# 0.2.0: chat files live once in scratch (CO Web 0.55, Bridge 1.6); artifacts/ holds old per-job copies, tmp/ throwaway files.
SKIP_DIRS = {".git", "node_modules", "events", "artifacts", "tmp", "temp", "_tmp", "__pycache__", ".venv", "venv", "dist", "build", ".tmp", "_rels"}
MAX_FILE = 60 * 1024 * 1024
MAX_TEXT = 600_000
CHUNK = 3000
BATCH_BYTES = 3_000_000


def key_of(rel: str) -> str:
    return hashlib.sha1(rel.encode("utf-8")).hexdigest()


def call(op: str, **body) -> dict:
    import keyring

    secret = keyring.get_password("aiwb/co-push", "default")
    if not secret:
        raise SystemExit("error: no key in keyring aiwb/co-push (account default)")
    raw = json.dumps({"op": op, **body}, ensure_ascii=False).encode("utf-8")
    data = json.dumps({"gz": base64.b64encode(gzip.compress(raw)).decode("ascii")}).encode("ascii")
    req = urllib.request.Request(API, data=data, method="POST", headers={
        "Content-Type": "application/json", "X-CO-Push-Key": secret, "User-Agent": "aiwb-docs/0.1"})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:300]
            if e.code < 500 or attempt == 2:
                raise SystemExit(f"error: {op}: HTTP {e.code} {detail}")
        except urllib.error.URLError as e:
            if attempt == 2:
                raise SystemExit(f"error: {op}: {e}")
        time.sleep(3 * (attempt + 1))
    return {}


# ---- text extraction ----

def text_docx(p: Path) -> str:
    import docx

    d = docx.Document(str(p))
    out = [para.text for para in d.paragraphs]
    for t in d.tables:
        for row in t.rows:
            cells = []
            for c in row.cells:
                if not cells or c.text != cells[-1]:  # merged cells repeat
                    cells.append(c.text)
            out.append(" | ".join(cells))
    return "\n".join(out)


def text_pdf(p: Path) -> str:
    import pymupdf

    with pymupdf.open(str(p)) as d:
        out, n = [], 0
        for page in d:
            t = page.get_text()
            out.append(t)
            n += len(t)
            if n > MAX_TEXT:
                break
        return "\n".join(out)


def text_xlsx(p: Path) -> str:
    import openpyxl

    wb = openpyxl.load_workbook(str(p), read_only=True, data_only=True)
    out, n = [], 0
    try:
        for ws in wb.worksheets:
            out.append(f"## {ws.title}")
            for row in ws.iter_rows(values_only=True):
                vals = [str(v) for v in row if v is not None and str(v).strip()]
                if vals:
                    line = "\t".join(vals)
                    out.append(line)
                    n += len(line)
                    if n > MAX_TEXT:
                        return "\n".join(out)
    finally:
        wb.close()
    return "\n".join(out)


def text_pptx(p: Path) -> str:
    import pptx

    out = []
    for i, slide in enumerate(pptx.Presentation(str(p)).slides, 1):
        out.append(f"## Slide {i}")
        for sh in slide.shapes:
            if sh.has_text_frame:
                out.append(sh.text_frame.text)
            if getattr(sh, "has_table", False) and sh.has_table:
                for row in sh.table.rows:
                    out.append(" | ".join(c.text for c in row.cells))
        if slide.has_notes_slide:
            out.append(slide.notes_slide.notes_text_frame.text)
    return "\n".join(out)


def text_plain(p: Path) -> str:
    b = p.read_bytes()[: MAX_TEXT * 2]
    for enc in ("utf-8-sig", "cp1251", "latin-1"):
        try:
            t = b.decode(enc)
            break
        except UnicodeDecodeError:
            continue
    if p.suffix.lower() in (".html", ".htm"):
        t = re.sub(r"(?is)<(script|style)\b.*?</\1>", " ", t)
        t = re.sub(r"<[^>]+>", " ", t)
    return t


def extract(p: Path) -> str:
    ext = p.suffix.lower()
    f = {".docx": text_docx, ".docm": text_docx, ".pdf": text_pdf, ".xlsx": text_xlsx, ".xlsm": text_xlsx,
         ".pptx": text_pptx}.get(ext, text_plain)
    t = f(p)
    t = re.sub(r"[ \t\r\f\v]+\n", "\n", t)
    t = re.sub(r"\n{3,}", "\n\n", t)
    return t[:MAX_TEXT].strip()


def chunks(text: str) -> list[str]:
    if not text:
        return [""]
    out, cur = [], ""
    for para in text.split("\n"):
        while len(para) > CHUNK:
            if cur:
                out.append(cur)
                cur = ""
            out.append(para[:CHUNK])
            para = para[CHUNK:]
        if len(cur) + len(para) + 1 > CHUNK and cur:
            out.append(cur)
            cur = ""
        cur = f"{cur}\n{para}" if cur else para
    if cur:
        out.append(cur)
    return out[:400]


# ---- commands ----

def scan(project: str | None):
    roots = [PROJECTS / project] if project else [d for d in PROJECTS.iterdir() if d.is_dir()]
    for root in roots:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d.lower() not in SKIP_DIRS and not d.startswith(".")]
            for fn in filenames:
                if fn.startswith("~$") or Path(fn).suffix.lower() not in EXTS:
                    continue
                p = Path(dirpath) / fn
                try:
                    st = p.stat()
                except OSError:
                    continue
                if st.st_size > MAX_FILE:
                    continue
                rel = p.relative_to(PROJECTS).as_posix()
                yield p, rel, root.name, int(st.st_mtime), st.st_size


def cmd_index(args) -> int:
    state = call("docs_state").get("docs") or {}
    seen, batch, size, put, failed = set(), [], 0, 0, 0

    def flush():
        nonlocal batch, size, put
        if batch and not args.dry_run:
            put += call("docs_put", docs=batch).get("put", 0)
        elif batch:
            put += len(batch)
        batch, size = [], 0

    todo = 0
    for p, rel, project, mtime, fsize in scan(args.project):
        key = key_of(rel)
        seen.add(key)
        old = state.get(key)
        if old and old[0] == mtime and old[1] == fsize:
            continue
        if args.limit and todo >= args.limit:
            continue
        todo += 1
        try:
            text = extract(p)
        except Exception as e:  # damaged or protected file: index the name only, retry when it changes
            failed += 1
            print(f"warning: {rel}: {type(e).__name__}: {str(e)[:120]}", file=sys.stderr)
            text = ""
        if args.verbose:
            print(f"{len(text):>8}  {rel}")
        doc = {"key": key, "projectId": project, "path": rel, "name": p.name, "mtime": mtime, "size": fsize,
               "chunks": chunks(text)}
        batch.append(doc)
        size += len(text) + 500
        if size > BATCH_BYTES or len(batch) >= 100:
            flush()
    flush()

    prefix = f"{args.project}/" if args.project else ""
    gone = [k for k, v in state.items() if k not in seen and str(v[2]).startswith(prefix)]
    if args.limit and todo >= args.limit:
        gone = []  # partial run: do not judge deletions
    deleted = 0
    if gone and not args.dry_run:
        for i in range(0, len(gone), 1000):
            deleted += call("docs_delete", keys=gone[i:i + 1000]).get("deleted", 0)
    print(f"indexed {put} changed file(s), {failed} unreadable, removed {len(gone)} gone file(s)"
          f"{' (dry run)' if args.dry_run else ''}; {len(seen)} file(s) on disk")
    return 0


def cmd_search(args) -> int:
    body = {"q": args.query, "limit": args.limit}
    if args.project:
        body["projectId"] = args.project
    if args.env:
        body["env"] = args.env
    docs = call("docs_search", **body).get("docs") or []
    if args.json:
        print(json.dumps(docs, ensure_ascii=False, indent=2))
        return 0
    if not docs:
        print("nothing found")
    for d in docs:
        print(f"{PROJECTS / d['path']}  ({d['modified'][:10]}, score {d['score']:.1f})")
        for s in d["snippets"]:
            print("    " + " ".join(s.split()))
    return 0


def cmd_stats(args) -> int:
    state = call("docs_state").get("docs") or {}
    per = {}
    for v in state.values():
        per[str(v[2]).split("/")[0]] = per.get(str(v[2]).split("/")[0], 0) + 1
    for k in sorted(per):
        print(f"{per[k]:>6}  {k}")
    print(f"{len(state):>6}  total")
    return 0


def main() -> int:
    warnings.filterwarnings("ignore")  # openpyxl: unsupported extensions
    for s in (sys.stdout, sys.stderr):
        if s is not None:  # None under pythonw (scheduled task)
            s.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    a = sub.add_parser("index", help="index new and changed files")
    a.add_argument("--project", help="only this project id")
    a.add_argument("--limit", type=int, help="at most N changed files this run")
    a.add_argument("--dry-run", action="store_true")
    a.add_argument("-v", "--verbose", action="store_true")
    a.add_argument("--log", help="append output to this file (scheduled task under pythonw)")
    s = sub.add_parser("search", help="search documents")
    s.add_argument("query")
    s.add_argument("--project")
    s.add_argument("--env", help="environment (default: all)")
    s.add_argument("--limit", type=int, default=10)
    s.add_argument("--json", action="store_true")
    sub.add_parser("stats", help="indexed documents per project")
    args = ap.parse_args()
    if getattr(args, "log", None):
        Path(args.log).parent.mkdir(parents=True, exist_ok=True)
        sys.stdout = sys.stderr = open(args.log, "a", encoding="utf-8", buffering=1)
        print(time.strftime("%Y-%m-%d %H:%M:%S"), "index")
    return {"index": cmd_index, "search": cmd_search, "stats": cmd_stats}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
