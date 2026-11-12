#!/usr/bin/env python3
"""docx-diff.py - compare two revisions of a Word document (.docx) and report changes.

The document body is read in order (paragraphs, tables, content controls) and turned
into "blocks": one block per non-empty paragraph (headings prefixed "#"/"##"...) and
one block per table row ("| c1 | c2 |", horizontally merged cells deduplicated).
Blocks are aligned with difflib; similar replaced blocks are shown as modifications
with a word-level inline diff. Block indices (§N) are 1-based: new document index for
insertions/modifications, old document index for deletions.

Tracked changes: by default the original text is read (w:ins ignored, w:del kept);
with --accept-changes, insertions are kept and deletions dropped.

Examples:
  docx-diff.py spec_v1.docx spec_v2.docx
  docx-diff.py old.docx new.docx --format json --accept-changes
  docx-diff.py old.docx new.docx --format docx --out changes.docx
"""
import argparse
import difflib
import json
import re
import sys

from docx import Document
from docx.oxml.ns import qn
from docx.shared import RGBColor
from docx.table import Table
from docx.text.paragraph import Paragraph

W_P, W_TBL, W_SDT, W_R = qn("w:p"), qn("w:tbl"), qn("w:sdt"), qn("w:r")
INS_TAGS = {qn("w:ins"), qn("w:moveTo")}
DEL_TAGS = {qn("w:del"), qn("w:moveFrom")}
TEXT_TAGS = {qn("w:t"), qn("w:delText")}
TAB, BRK = qn("w:tab"), {qn("w:br"), qn("w:cr")}


def para_text(p, accept):
    """Text of a w:p element, resolving tracked insertions/deletions explicitly."""
    out = []
    for el in p.iter():
        tag = el.tag
        if tag not in TEXT_TAGS:
            if not (tag == TAB or tag in BRK) or el.getparent().tag != W_R:
                continue
        anc = {a.tag for a in el.iterancestors()}
        in_del = tag == qn("w:delText") or bool(anc & DEL_TAGS)
        in_ins = bool(anc & INS_TAGS)
        if (accept and in_del) or (not accept and in_ins):
            continue
        out.append((el.text or "") if tag in TEXT_TAGS else "\t" if tag == TAB else "\n")
    return "".join(out)


def heading_prefix(p, doc):
    try:
        name = Paragraph(p, doc).style.name or ""
    except Exception:
        return ""
    m = re.match(r"Heading (\d)", name)
    if m:
        return "#" * int(m.group(1)) + " "
    return "# " if name == "Title" else ""


def walk(container, doc, accept, tag=""):
    blocks = []
    for el in container.iterchildren():
        if el.tag == W_P:
            t = para_text(el, accept).strip()
            if t:
                blocks.append(tag + heading_prefix(el, doc) + t)
        elif el.tag == W_TBL:
            for row in Table(el, doc).rows:
                cells, prev = [], None
                for c in row.cells:
                    if c._tc is prev:
                        continue
                    prev = c._tc
                    parts = (para_text(x, accept).strip() for x in c._tc.iterchildren(W_P))
                    cells.append(" ".join(filter(None, parts)))
                if any(cells):
                    blocks.append(tag + "| " + " | ".join(cells) + " |")
        elif el.tag == W_SDT:
            content = el.find(qn("w:sdtContent"))
            if content is not None:
                blocks += walk(content, doc, accept, tag)
    return blocks


def load(path, a):
    doc = Document(path)
    blocks = []
    sec = doc.sections[0] if a.headers and len(doc.sections) else None
    if sec is not None and not sec.header.is_linked_to_previous:
        blocks += walk(sec.header._element, doc, a.accept_changes, "[header] ")
    blocks += walk(doc.element.body, doc, a.accept_changes)
    if sec is not None and not sec.footer.is_linked_to_previous:
        blocks += walk(sec.footer._element, doc, a.accept_changes, "[footer] ")
    return blocks


def norm(s, a):
    if a.ignore_whitespace:
        s = re.sub(r"\s+", " ", s).strip()
    return s.casefold() if a.ignore_case else s


def word_diff(old, new, a):
    ot, nt = re.findall(r"\s+|\S+", old), re.findall(r"\s+|\S+", new)
    key = lambda t: " " if a.ignore_whitespace and t.isspace() else norm(t, a)
    sm = difflib.SequenceMatcher(None, [key(t) for t in ot], [key(t) for t in nt], autojunk=False)
    segs = []
    for op, i1, i2, j1, j2 in sm.get_opcodes():
        if op == "equal":
            segs.append(("=", "".join(nt[j1:j2])))
        else:
            if i2 > i1:
                segs.append(("-", "".join(ot[i1:i2])))
            if j2 > j1:
                segs.append(("+", "".join(nt[j1:j2])))
    return segs


def render(segs, md=False):
    out = []
    for op, s in segs:
        if op == "=":
            out.append(s)
        elif not md:
            out.append(f"[-{s}-]" if op == "-" else f"{{+{s}+}}")
        else:
            lead, core, trail = re.match(r"(\s*)(.*?)(\s*)$", s, re.S).groups()
            out.append(lead + ((f"~~{core}~~" if op == "-" else f"**{core}**") if core else "") + trail)
    return "".join(out)


def compare(old, new, a):
    ko, kn = [norm(b, a) for b in old], [norm(b, a) for b in new]
    sm = difflib.SequenceMatcher(None, ko, kn, autojunk=False)
    changes = []

    def add(op, i=None, j=None):
        c = {"op": op, "old_index": None if i is None else i + 1, "new_index": None if j is None else j + 1,
             "old": None if i is None else old[i], "new": None if j is None else new[j], "inline": None}
        if op == "modify":
            c["segs"] = word_diff(old[i], new[j], a)
            c["inline"] = render(c["segs"])
        changes.append(c)

    for op, i1, i2, j1, j2 in sm.get_opcodes():
        if op == "equal":
            continue
        j = j1
        for i in range(i1, i2):
            best, bk = a.min_ratio - 1e-12, None
            for k in range(j, j2 if op == "replace" else j):
                r = difflib.SequenceMatcher(None, ko[i], kn[k], autojunk=False)
                if r.real_quick_ratio() > best and r.quick_ratio() > best and (x := r.ratio()) > best:
                    best, bk = x, k
            if bk is None:
                add("delete", i)
            else:
                for k in range(j, bk):
                    add("insert", j=k)
                add("modify", i, bk)
                j = bk + 1
        for k in range(j, j2):
            add("insert", j=k)
    count = lambda op: sum(c["op"] == op for c in changes)
    summary = {"modified": count("modify"), "inserted": count("insert"), "deleted": count("delete"),
               "similarity": round(sm.ratio() * 100, 1), "old_blocks": len(old), "new_blocks": len(new),
               "identical": not changes}
    return changes, summary


loc = lambda c: c["new_index"] if c["new_index"] is not None else c["old_index"]
body = lambda c: c["inline"] if c["op"] == "modify" else (c["old"] if c["op"] == "delete" else c["new"])


def summary_line(s):
    return (f"{s['modified']} modified, {s['inserted']} inserted, {s['deleted']} deleted, "
            f"similarity {s['similarity']}%")


def out_text(changes, s):
    sym = {"modify": "~", "insert": "+", "delete": "-"}
    for c in changes:
        print(f"{sym[c['op']]} §{loc(c)}: {body(c)}")
    if not changes:
        print("identical")
    print(("\n" if changes else "") + "Summary: " + summary_line(s))


def out_md(changes, s, a):
    print(f"# Document comparison\n\n**Old:** `{a.old}`  \n**New:** `{a.new}`\n")
    print("| Metric | Value |\n|---|---|")
    for k in ("modified", "inserted", "deleted", "old_blocks", "new_blocks"):
        print(f"| {k.replace('_', ' ').capitalize()} | {s[k]} |")
    print(f"| Similarity | {s['similarity']}% |\n\n## Changes\n")
    if not changes:
        print("_identical_")
    for c in changes:
        if c["op"] == "modify":
            text = render(c["segs"], md=True)
        else:
            text = render([("-" if c["op"] == "delete" else "+", body(c))], md=True)
        print(f"- **§{loc(c)} {c['op']}** — {text}")


def out_docx(changes, s, a):
    d = Document()
    d.add_heading("Document comparison", 1)
    d.add_paragraph(f"Old: {a.old}    New: {a.new}")
    d.add_paragraph("Summary: " + summary_line(s))
    if not changes:
        d.add_paragraph("identical")
    for c in changes:
        p = d.add_paragraph()
        p.add_run(f"§{loc(c)} {c['op']}: ").bold = True
        segs = c.get("segs") or [("-" if c["op"] == "delete" else "+", body(c))]
        for op, t in segs:
            r = p.add_run(t)
            if op == "-":
                r.font.strike, r.font.color.rgb = True, RGBColor(0xC0, 0x00, 0x00)
            elif op == "+":
                r.font.underline, r.font.color.rgb = True, RGBColor(0x00, 0x40, 0xC0)
    d.save(a.out)
    print(f"Written {a.out} - " + ("identical" if not changes else summary_line(s)))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description="Compare two revisions of a Word document (.docx).",
                                 formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    ap.add_argument("old", help="original .docx file")
    ap.add_argument("new", help="revised .docx file")
    ap.add_argument("--format", choices=["text", "json", "md", "docx"], default="text",
                    help="output format (default: text)")
    ap.add_argument("--out", help="output file (required for --format docx)")
    ap.add_argument("--headers", action="store_true", help="include header/footer of the first section")
    ap.add_argument("--accept-changes", action="store_true",
                    help="read tracked changes as accepted (keep w:ins, drop w:del)")
    ap.add_argument("--ignore-case", action="store_true", help="case-insensitive comparison")
    ap.add_argument("--ignore-whitespace", action="store_true", help="collapse whitespace before comparing")
    ap.add_argument("--min-ratio", type=float, default=0.5,
                    help="minimum similarity to pair replaced blocks as modified (default: 0.5)")
    a = ap.parse_args()
    try:
        if a.format == "docx" and not a.out:
            raise ValueError("--out FILE is required with --format docx")
        if not 0.0 <= a.min_ratio <= 1.0:
            raise ValueError("--min-ratio must be between 0 and 1")
        changes, s = compare(load(a.old, a), load(a.new, a), a)
        if a.format == "json":
            clean = [{k: v for k, v in c.items() if k != "segs"} for c in changes]
            print(json.dumps({"summary": s, "changes": clean}, ensure_ascii=False, indent=2))
        elif a.format == "md":
            out_md(changes, s, a)
        elif a.format == "docx":
            out_docx(changes, s, a)
        else:
            out_text(changes, s)
    except Exception as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
