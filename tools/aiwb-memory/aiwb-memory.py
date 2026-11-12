"""aiwb-memory: facts and decisions of an AI Workbench project in projects/<id>/context/memory.md.

CO Web gives this file (plus summaries of the 5 newest chats) to every new agent session of the project; the user
edits it on the Project tab. Entries are lines "- YYYY-MM-DD: [tag] text"; any other content is kept as is.
Entry numbers (list -n, get, search) are the order of the entry lines, the same as in the index CO Web 0.70.0+ sends
instead of the whole file. Entries replaced by accepted consolidation proposals live in context/memory-archive.md as
"<entry> — superseded YYYY-MM-DD by: <new entry>" or "<entry> — dropped YYYY-MM-DD: <reason>".
"""
import argparse
import datetime
import os
import re
import sys
import tempfile
from pathlib import Path

HEADER = "# Project memory\n\n"
ENTRY = re.compile(r"^- (\d{4}-\d{2}-\d{2}): (?:\[[^\]]*\] )?(.*)$")
ARCHIVED = re.compile(r"^(- .*?) — (superseded|dropped) (\d{4}-\d{2}-\d{2})(?:(?: by)?: (.*))?$")


def root(args) -> Path:
    return Path(args.root or os.environ.get("AIWB_ROOT") or r"G:\My Drive\AI-Workbench")


def project_dir(args) -> Path:
    projects = root(args) / "projects"
    if args.project:
        d = projects / args.project
        if not d.is_dir():
            sys.exit(f"error: project folder not found: {d}")
        return d
    cwd = Path.cwd().resolve()
    base = str(projects.resolve()).lower().rstrip("\\/")
    for p in [cwd, *cwd.parents]:
        if str(p.parent).lower().rstrip("\\/") == base:
            return p
    sys.exit(f"error: not inside {projects}\\<id>; pass --project <id>")


def load(f: Path):
    if not f.exists():
        return HEADER, "\n"
    s = f.read_text(encoding="utf-8")
    nl = "\r\n" if "\r\n" in s else "\n"
    return s.replace("\r\n", "\n"), nl


def save(f: Path, s: str, nl: str) -> None:
    f.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=f.parent, prefix=".memory-", suffix=".tmp")
    with os.fdopen(fd, "w", encoding="utf-8", newline="") as h:
        h.write(s.replace("\n", nl))
    os.replace(tmp, f)


def is_entry(line: str) -> bool:
    return line.startswith("- ") and line[2:].strip() != ""


def entry_day(line: str) -> str:
    m = ENTRY.match(line)
    return m.group(1) if m else ""


def archived(f: Path):
    """(entry, how, day, note) of memory-archive.md next to memory.md."""
    a = f.with_name("memory-archive.md")
    if not a.exists():
        return []
    out = []
    for x in a.read_text(encoding="utf-8-sig").replace("\r\n", "\n").split("\n"):
        m = ARCHIVED.match(x.rstrip())
        if m:
            out.append((m.group(1), m.group(2), m.group(3), m.group(4) or ""))
    return out


def score(text: str, words) -> int:
    low = text.lower()
    return sum(1 for w in words if w in low)


def main() -> int:
    for s in (sys.stdout, sys.stderr):
        s.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--project", help="project id (default: detected from the current folder)")
    ap.add_argument("--root", help=r"AI Workbench root (default: AIWB_ROOT or G:\My Drive\AI-Workbench)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    a = sub.add_parser("add", help="append a dated entry")
    a.add_argument("text")
    a.add_argument("--tag", help="e.g. decision, fact, todo")
    l = sub.add_parser("list", help="print entries")
    l.add_argument("--grep")
    l.add_argument("--last", type=int)
    l.add_argument("-n", action="store_true", help="prefix entry numbers")
    g = sub.add_parser("get", help="print entries by number (as in the CO Web index)")
    g.add_argument("numbers", nargs="+", type=int)
    q = sub.add_parser("search", help="entries containing the query words, most words first")
    q.add_argument("query")
    q.add_argument("--archive", action="store_true", help="also superseded/dropped entries of memory-archive.md")
    q.add_argument("--limit", type=int, default=15)
    o = sub.add_parser("asof", help="memory as it was on a date (current and archived entries)")
    o.add_argument("date", help="YYYY-MM-DD")
    r = sub.add_parser("remove", help="remove entries containing a substring")
    r.add_argument("substring")
    r.add_argument("--all", action="store_true", help="remove every match, not just a single one")
    sub.add_parser("path", help="print the memory file path")
    sub.add_parser("show", help="print the whole file")
    args = ap.parse_args()

    f = project_dir(args) / "context" / "memory.md"
    if args.cmd == "path":
        print(f)
        return 0
    s, nl = load(f)
    lines = s.split("\n")
    entries = [x for x in lines if is_entry(x)]

    if args.cmd == "show":
        print(s, end="" if s.endswith("\n") else "\n")
    elif args.cmd == "list":
        out = [f"[{i}] {x[2:]}" if args.n else x for i, x in enumerate(entries, 1)
               if not args.grep or args.grep.lower() in x.lower()]
        print("\n".join(out[-args.last:] if args.last else out))
    elif args.cmd == "get":
        bad = [n for n in args.numbers if not 1 <= n <= len(entries)]
        for n in args.numbers:
            if n not in bad:
                print(f"[{n}] {entries[n - 1][2:]}")
        if bad:
            print(f"error: no entry {', '.join(map(str, bad))} (1..{len(entries)})", file=sys.stderr)
            return 1
    elif args.cmd == "search":
        words = [w for w in re.split(r"\W+", args.query.lower()) if w]
        if not words:
            print("error: empty query", file=sys.stderr)
            return 1
        hits = [(score(x, words), i, f"[{i}] {x[2:]}") for i, x in enumerate(entries, 1)]
        if args.archive:
            hits += [(score(e + " " + note, words), 0, f"[{how} {day}] {e[2:]}" + (f" -> {note}" if note else ""))
                     for e, how, day, note in archived(f)]
        hits = sorted((h for h in hits if h[0]), key=lambda h: (-h[0], -h[1]))
        print("\n".join(h[2] for h in hits[:args.limit]) if hits else "no matching entry")
    elif args.cmd == "asof":
        if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", args.date):
            print("error: date must be YYYY-MM-DD", file=sys.stderr)
            return 1
        rows = [(entry_day(x), x) for x in entries if entry_day(x) <= args.date]
        rows += [(entry_day(e), f"{e}   (later {how} {day})") for e, how, day, _ in archived(f)
                 if entry_day(e) <= args.date < day]
        rows.sort(key=lambda r: r[0])
        print("\n".join(r[1] for r in rows))
    elif args.cmd == "add":
        text = " ".join(args.text.split())
        if not text:
            print("error: empty text", file=sys.stderr)
            return 1
        for x in entries:
            m = ENTRY.match(x)
            if m and m.group(2).strip() == text:
                print(f"already present: {x}")
                return 0
        tag = f"[{args.tag}] " if args.tag else ""
        line = f"- {datetime.date.today():%Y-%m-%d}: {tag}{text}"
        save(f, s.rstrip("\n") + "\n" + line + "\n", nl)
        print(f"{line}\n-> {f}")
    elif args.cmd == "remove":
        hits = [x for x in entries if args.substring.lower() in x.lower()]
        if not hits:
            print("error: no matching entry", file=sys.stderr)
            return 1
        if len(hits) > 1 and not args.all:
            print("error: several entries match (use --all):\n" + "\n".join(hits), file=sys.stderr)
            return 1
        save(f, "\n".join(x for x in lines if x not in hits), nl)
        print("removed:\n" + "\n".join(hits))
    return 0


if __name__ == "__main__":
    sys.exit(main())
