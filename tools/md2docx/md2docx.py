"""Convert simple Markdown (headings, paragraphs, bullet/numbered lists, pipe tables,
**bold**, URLs) into a clean .docx using python-docx."""
import argparse
import re
import sys

from docx import Document
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Pt, RGBColor

INLINE = re.compile(r"(\*\*[^*]+\*\*|https?://\S+)")


def add_hyperlink(par, url):
    part = par.part
    r_id = part.relate_to(url, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", is_external=True)
    link = OxmlElement("w:hyperlink")
    link.set(qn("r:id"), r_id)
    run = OxmlElement("w:r")
    rpr = OxmlElement("w:rPr")
    color = OxmlElement("w:color")
    color.set(qn("w:val"), "1F4E9A")
    u = OxmlElement("w:u")
    u.set(qn("w:val"), "single")
    rpr.append(color)
    rpr.append(u)
    run.append(rpr)
    t = OxmlElement("w:t")
    t.text = url
    t.set(qn("xml:space"), "preserve")
    run.append(t)
    link.append(run)
    par._p.append(link)


def add_inline(par, text, bold=False):
    for piece in INLINE.split(text):
        if not piece:
            continue
        if piece.startswith("**") and piece.endswith("**"):
            par.add_run(piece[2:-2]).bold = True
        elif piece.startswith("http"):
            add_hyperlink(par, piece.rstrip(".,;)"))
        else:
            r = par.add_run(piece)
            r.bold = bold


def shade(cell, hex_color):
    tcpr = cell._tc.get_or_add_tcPr()
    shd = OxmlElement("w:shd")
    shd.set(qn("w:val"), "clear")
    shd.set(qn("w:color"), "auto")
    shd.set(qn("w:fill"), hex_color)
    tcpr.append(shd)


def flush_table(doc, rows):
    rows = [r for r in rows if not re.match(r"^\|\s*:?-{2,}", r)]
    cells = [[c.strip() for c in r.strip().strip("|").split("|")] for r in rows]
    ncol = max(len(c) for c in cells)
    table = doc.add_table(rows=len(cells), cols=ncol)
    table.style = "Table Grid"
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    for i, row in enumerate(cells):
        for j in range(ncol):
            cell = table.cell(i, j)
            cell.text = ""
            p = cell.paragraphs[0]
            add_inline(p, row[j] if j < len(row) else "", bold=(i == 0))
            for r in p.runs:
                r.font.size = Pt(9)
            if i == 0:
                shade(cell, "D9E2F3")
    doc.add_paragraph()


def convert(md_text, out_path, font="Calibri", size=10.5):
    doc = Document()
    st = doc.styles["Normal"]
    st.font.name = font
    st.font.size = Pt(size)
    st.element.rPr.rFonts.set(qn("w:eastAsia"), font)
    for sec in doc.sections:
        sec.left_margin = sec.right_margin = Pt(56)
    table_rows = []
    para_buf = []

    def flush_para():
        if para_buf:
            p = doc.add_paragraph()
            for k, line in enumerate(para_buf):
                if k:
                    p.add_run().add_break()
                add_inline(p, line)
            para_buf.clear()

    for raw in md_text.splitlines():
        line = raw.rstrip()
        if line.startswith("|"):
            flush_para()
            table_rows.append(line)
            continue
        if table_rows:
            flush_table(doc, table_rows)
            table_rows = []
        m = re.match(r"^(#{1,4})\s+(.*)", line)
        if m:
            flush_para()
            h = doc.add_heading(level=len(m.group(1)) - 1 if len(m.group(1)) > 1 else 0)
            add_inline(h, m.group(2))
            for r in h.runs:
                r.font.color.rgb = RGBColor(0x1F, 0x3A, 0x5F)
            continue
        m = re.match(r"^(\s*)[-*]\s+(.*)", line)
        if m:
            flush_para()
            p = doc.add_paragraph(style="List Bullet 2" if len(m.group(1)) >= 2 else "List Bullet")
            add_inline(p, m.group(2))
            continue
        m = re.match(r"^(\s*)\d+[.)]\s+(.*)", line)
        if m:
            flush_para()
            p = doc.add_paragraph(style="List Number")
            add_inline(p, m.group(2))
            continue
        if not line.strip():
            flush_para()
            continue
        para_buf.append(line.strip())
    flush_para()
    if table_rows:
        flush_table(doc, table_rows)
    doc.save(out_path)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("input", help="Markdown file (UTF-8)")
    ap.add_argument("output", help="Target .docx")
    ap.add_argument("--font", default="Calibri")
    ap.add_argument("--size", type=float, default=10.5)
    a = ap.parse_args()
    with open(a.input, encoding="utf-8") as f:
        convert(f.read(), a.output, a.font, a.size)
    print(a.output)


if __name__ == "__main__":
    sys.exit(main())
