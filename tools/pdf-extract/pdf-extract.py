#!/usr/bin/env python3
"""pdf-extract.py - Inspect PDFs and extract text, tables and search hits (PyMuPDF only).

Examples:
  pdf-extract.py info report.pdf --tables
  pdf-extract.py text report.pdf --pages 1-3,5 --layout
  pdf-extract.py tables report.pdf --format csv --out tables/ --strategy text
  pdf-extract.py grep report.pdf "net capacity" --context 60
  pdf-extract.py grep report.pdf "\\d+(\\.\\d+)? ?MW" --regex
"""
import argparse, csv, json, os, re, sys
import pymupdf

SCANNED_THRESHOLD = 20  # pages with fewer text chars are likely scanned images


class CliError(Exception):
    pass


def open_doc(path):
    """Open a PDF; try empty password if encrypted. Returns (doc, was_encrypted)."""
    if not os.path.isfile(path):
        raise CliError(f"file not found: {path}")
    try:
        doc = pymupdf.open(path)
    except Exception as e:
        raise CliError(f"cannot open PDF '{path}': {e}")
    encrypted = bool(doc.is_encrypted or doc.needs_pass)
    if doc.needs_pass and not doc.authenticate(""):
        raise CliError(f"PDF is password-protected and cannot be opened: {path}")
    return doc, encrypted


def parse_pages(spec, n):
    """'1-3,5' -> [0,1,2,4] (0-based, ordered, deduplicated), validated against n pages."""
    if not spec:
        return list(range(n))
    out = []
    for part in (p.strip() for p in spec.split(",")):
        if not part:
            continue
        m = re.fullmatch(r"(\d+)\s*-\s*(\d+)|(\d+)", part)
        if not m:
            raise CliError(f"invalid page spec element: '{part}'")
        a, b = (int(m[1]), int(m[2])) if m[1] else (int(m[3]),) * 2
        if a > b:
            raise CliError(f"invalid range (start > end): '{part}'")
        if a < 1 or b > n:
            raise CliError(f"page out of range (document has {n} pages): '{part}'")
        out.extend(range(a - 1, b))
    if not out:
        raise CliError(f"empty page spec: '{spec}'")
    return list(dict.fromkeys(out))


def find_tables(page, strategy="lines"):
    return page.find_tables(strategy=strategy).tables


def cell(c):
    return "" if c is None else str(c)


def clean(c):
    return re.sub(r"\s*[\r\n]+\s*", " ", cell(c)).strip()


def to_md(rows):
    rows = [[clean(c).replace("|", "\\|") for c in r] for r in rows]
    if not rows:
        return ""
    w = max(len(r) for r in rows)
    rows = [r + [""] * (w - len(r)) for r in rows]
    lines = ["| " + " | ".join(rows[0]) + " |", "|" + "---|" * w]
    return "\n".join(lines + ["| " + " | ".join(r) + " |" for r in rows[1:]])


def cmd_info(a):
    doc, encrypted = open_doc(a.file)
    pages = []
    for i, page in enumerate(doc):
        n = len(page.get_text("text").strip())
        d = {"page": i + 1, "chars": n, "likely_scanned": n < SCANNED_THRESHOLD}
        if a.tables:
            d["tables"] = len(find_tables(page, a.strategy))
        pages.append(d)
    print(json.dumps({
        "file": os.path.abspath(a.file), "page_count": doc.page_count,
        "is_encrypted": encrypted, "metadata": doc.metadata,
        "likely_scanned_pages": [p["page"] for p in pages if p["likely_scanned"]],
        "pages": pages,
    }, ensure_ascii=False, indent=2))


def cmd_text(a):
    doc, _ = open_doc(a.file)
    for p in parse_pages(a.pages, doc.page_count):
        page = doc[p]
        text = page.get_text("text", sort=True) if a.layout else page.get_text("text")
        print(f"=== page {p + 1} ===")
        print(text.rstrip("\n"))
        print()


def cmd_tables(a):
    doc, _ = open_doc(a.file)
    pages = parse_pages(a.pages, doc.page_count)
    if a.out:
        os.makedirs(a.out, exist_ok=True)
    results, count = [], 0
    for p in pages:
        for k, t in enumerate(find_tables(doc[p], a.strategy), 1):
            rows, count = t.extract(), count + 1
            if a.format == "json":
                results.append({"page": p + 1, "index": k,
                                "bbox": [round(v, 2) for v in tuple(t.bbox)], "rows": rows})
                continue
            header = f"=== page {p + 1}, table {k} ({t.row_count} x {t.col_count}) ==="
            if a.out:
                ext = "csv" if a.format == "csv" else "md"
                path = os.path.join(a.out, f"page{p + 1}_table{k}.{ext}")
                if a.format == "csv":
                    with open(path, "w", newline="", encoding="utf-8-sig") as f:
                        csv.writer(f).writerows([[cell(c) for c in r] for r in rows])
                else:
                    with open(path, "w", encoding="utf-8") as f:
                        f.write(to_md(rows) + "\n")
                print(f"{header} -> {path}")
                continue
            print(header)
            if a.format == "csv":
                csv.writer(sys.stdout, lineterminator="\n").writerows([[cell(c) for c in r] for r in rows])
            else:
                print(to_md(rows))
            print()
    if a.format == "json":
        data = json.dumps(results, ensure_ascii=False, indent=2)
        if a.out:
            path = os.path.join(a.out, "tables.json")
            with open(path, "w", encoding="utf-8") as f:
                f.write(data + "\n")
            print(f"wrote {path}")
        else:
            print(data)
    print(f"{count} table(s) found on {len(pages)} page(s)", file=sys.stderr)


def cmd_grep(a):
    doc, _ = open_doc(a.file)
    if a.context < 0:
        raise CliError("--context must be >= 0")
    try:
        rx = re.compile(a.pattern if a.regex else re.escape(a.pattern), re.IGNORECASE)
    except re.error as e:
        raise CliError(f"invalid regex: {e}")
    total = 0
    for p in parse_pages(a.pages, doc.page_count):
        text = doc[p].get_text("text")
        for m in rx.finditer(text):
            if m.start() == m.end():
                continue
            s, e = max(0, m.start() - a.context), min(len(text), m.end() + a.context)
            norm = lambda x: re.sub(r"\s+", " ", x)
            snip = (("..." if s > 0 else "") + norm(text[s:m.start()]) + "[[" + norm(m.group())
                    + "]]" + norm(text[m.end():e]) + ("..." if e < len(text) else ""))
            print(f"page {p + 1}: {snip.strip()}")
            total += 1
    print(f"{total} match(es)", file=sys.stderr)


def build_parser():
    ap = argparse.ArgumentParser(prog="pdf-extract.py", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True, metavar="COMMAND")
    pages_help = "pages to process, 1-based, e.g. '1-3,5' (default: all)"
    s = sub.add_parser("info", help="document info as JSON (metadata, text chars per page, scanned detection)")
    s.add_argument("file", help="PDF file")
    s.add_argument("--tables", action="store_true", help="also count tables per page (slower)")
    s.add_argument("--strategy", choices=["lines", "text"], default="lines", help="table detection strategy")
    s.set_defaults(func=cmd_info)
    s = sub.add_parser("text", help="extract plain text per page")
    s.add_argument("file", help="PDF file")
    s.add_argument("--pages", help=pages_help)
    s.add_argument("--layout", action="store_true", help="sort text in reading order (top-left to bottom-right)")
    s.set_defaults(func=cmd_text)
    s = sub.add_parser("tables", help="extract tables as Markdown, CSV or JSON")
    s.add_argument("file", help="PDF file")
    s.add_argument("--pages", help=pages_help)
    s.add_argument("--format", choices=["md", "csv", "json"], default="md", help="output format (default: md)")
    s.add_argument("--out", metavar="DIR", help="write one file per table into DIR (json: DIR/tables.json)")
    s.add_argument("--strategy", choices=["lines", "text"], default="lines",
                   help="'lines' uses ruling lines (default), 'text' uses text alignment")
    s.set_defaults(func=cmd_tables)
    s = sub.add_parser("grep", help="case-insensitive search with page number and context snippet")
    s.add_argument("file", help="PDF file")
    s.add_argument("pattern", help="search string (or regex with --regex)")
    s.add_argument("--regex", action="store_true", help="treat PATTERN as a regular expression")
    s.add_argument("--context", type=int, default=80, help="characters of context around match (default: 80)")
    s.add_argument("--pages", help=pages_help)
    s.set_defaults(func=cmd_grep)
    return ap


def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    args = build_parser().parse_args(argv)
    try:
        args.func(args)
    except CliError as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)
    except BrokenPipeError:
        sys.exit(0)
    except Exception as e:
        print(f"error: {type(e).__name__}: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
