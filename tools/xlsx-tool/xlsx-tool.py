#!/usr/bin/env python3
"""xlsx-tool: inspect, read, search and edit .xlsx/.xlsm files from the command line.

Dependency: openpyxl. Macros are preserved for .xlsm files (keep_vba=True).

Examples:
  xlsx-tool.py info book.xlsx
  xlsx-tool.py read book.xlsx --sheet Data --range A1:D20 --format md --header
  xlsx-tool.py find book.xlsx "total" --regex
  xlsx-tool.py set book.xlsx B2 "=SUM(A1:A10)" --sheet Data
  xlsx-tool.py set-many book.xlsx edits.json --out new.xlsx
  xlsx-tool.py to-csv book.xlsx out_dir
"""
import argparse, csv, json, os, re, shutil, sys
from datetime import date, datetime, time, timedelta
from itertools import islice
import openpyxl
from openpyxl.utils import get_column_letter
from openpyxl.utils.cell import range_boundaries

INT_RE = re.compile(r'[+-]?\d+')
FLOAT_RE = re.compile(r'[+-]?(\d+\.\d*|\.\d+|\d+)([eE][+-]?\d+)?')
DATE_RE = re.compile(r'\d{4}-\d{2}-\d{2}')
CELL_RE = re.compile(r'[A-Z]{1,3}[1-9]\d*')


def load(path, data_only=False):
    if not os.path.isfile(path):
        raise FileNotFoundError(f"file not found: {path}")
    return openpyxl.load_workbook(path, data_only=data_only, keep_vba=path.lower().endswith('.xlsm'))


def get_ws(wb, name):
    if name is None:
        return wb.active
    if name not in wb.sheetnames:
        raise ValueError(f"sheet not found: {name!r} (available: {', '.join(wb.sheetnames)})")
    return wb[name]


def formula_cells(ws):
    return [c.coordinate for row in ws.iter_rows() for c in row if c.data_type == 'f']


def warn_uncached(fwb, vwb, names=None):
    for ws in fwb.worksheets:
        if names and ws.title not in names:
            continue
        coords = formula_cells(ws)
        if coords and all(vwb[ws.title][k].value is None for k in coords):
            print(f"warning: sheet '{ws.title}' has {len(coords)} formula(s) but no cached values "
                  "(file never recalculated?); use --formulas or recalculate in Excel/LibreOffice",
                  file=sys.stderr)


def conv(v):
    if isinstance(v, (datetime, date, time)):
        return v.isoformat()
    if isinstance(v, timedelta):
        return str(v)
    if hasattr(v, 'text') and not isinstance(v, str):  # ArrayFormula
        return v.text
    return v


def parse_value(v):
    if not isinstance(v, str) or v.startswith('='):
        return v
    if v.startswith("'"):  # Excel-style escape: keep the rest as literal text
        return v[1:]
    if v.lower() in ('true', 'false'):
        return v.lower() == 'true'
    if INT_RE.fullmatch(v):
        return int(v)
    if FLOAT_RE.fullmatch(v):
        return float(v)
    if DATE_RE.fullmatch(v):
        try:
            return datetime.strptime(v, '%Y-%m-%d')
        except ValueError:
            pass
    return v


def save(wb, path, out):
    if out:
        wb.save(out)
    else:
        shutil.copy2(path, path + '.bak')
        wb.save(path)
    print(f"saved {out or path}", file=sys.stderr)


def apply(wb, sheet, cell, value):
    ws, cell = get_ws(wb, sheet), str(cell).upper()
    if not CELL_RE.fullmatch(cell):
        raise ValueError(f"invalid cell reference: {cell!r}")
    ws[cell].value = parse_value(value)


def cmd_info(a):
    fwb, vwb = load(a.file), load(a.file, True)
    warn_uncached(fwb, vwb)
    sheets = [{"name": ws.title, "max_row": ws.max_row, "max_column": ws.max_column,
               "formulas": len(formula_cells(ws))} for ws in fwb.worksheets]
    dn, names = fwb.defined_names, {}
    items = dn.items() if hasattr(dn, 'items') else ((d.name, d) for d in dn.definedName)
    for n, d in items:
        names[n] = d.attr_text
    for ws in fwb.worksheets:
        for n, d in getattr(ws, 'defined_names', {}).items():
            names[f"{ws.title}!{n}"] = d.attr_text
    print(json.dumps({"file": a.file, "sheets": sheets, "defined_names": names}, ensure_ascii=False, indent=2))


def cmd_read(a):
    wb = load(a.file, data_only=not a.formulas)
    ws = get_ws(wb, a.sheet)
    if not a.formulas:
        warn_uncached(load(a.file), wb, [ws.title])
    kw = {}
    if a.range:
        c1, r1, c2, r2 = range_boundaries(a.range.upper())
        kw = dict(min_col=c1, min_row=r1, max_col=c2, max_row=r2)
    rows = ws.iter_rows(values_only=True, **kw)
    header = None
    if a.header:
        first = next(rows, None) or ()
        header = [str(h) if h is not None else f"col{i + 1}" for i, h in enumerate(first)]
    if a.max_rows > 0:
        rows = islice(rows, a.max_rows)
    rows = [[conv(v) for v in r] for r in rows]
    if a.format == 'json':
        data = [dict(zip(header, r)) for r in rows] if header else rows
        print(json.dumps(data, ensure_ascii=False, indent=2, default=str))
    elif a.format == 'csv':
        w = csv.writer(sys.stdout, lineterminator='\n')
        if header:
            w.writerow(header)
        w.writerows([['' if v is None else v for v in r] for r in rows])
    else:
        ncols = max([len(header or [])] + [len(r) for r in rows])
        start = kw.get('min_col') or 1
        hdr = header or [get_column_letter(start + i) for i in range(ncols)]
        esc = lambda v: '' if v is None else str(v).replace('|', '\\|').replace('\n', '<br>')
        print('| ' + ' | '.join(esc(h) for h in hdr) + ' |')
        print('|' + '---|' * len(hdr))
        for r in rows:
            print('| ' + ' | '.join(esc(v) for v in r) + ' |')


def cmd_find(a):
    fwb, vwb = load(a.file), load(a.file, True)
    pat = re.compile(a.text if a.regex else re.escape(a.text), re.IGNORECASE)
    sheets = [get_ws(fwb, a.sheet)] if a.sheet else fwb.worksheets
    for ws in sheets:
        if not hasattr(ws, 'iter_rows'):
            continue
        vws = vwb[ws.title]
        for row in ws.iter_rows():
            for c in row:
                if c.value is None:
                    continue
                cands = [vws[c.coordinate].value] + ([c.value] if c.data_type == 'f' else [])
                for v in cands:
                    if v is not None and pat.search(str(conv(v))):
                        print(json.dumps({"sheet": ws.title, "cell": c.coordinate, "value": conv(v)},
                                         ensure_ascii=False, default=str))
                        break


def cmd_set(a):
    wb = load(a.file)
    apply(wb, a.sheet, a.cell, a.value)
    save(wb, a.file, a.out)


def cmd_set_many(a):
    with open(a.jsonfile, encoding='utf-8') as f:
        edits = json.load(f)
    if not isinstance(edits, list):
        raise ValueError("JSONFILE must contain a list of {sheet, cell, value} objects")
    wb = load(a.file)
    for i, e in enumerate(edits):
        if not isinstance(e, dict) or 'cell' not in e or 'value' not in e:
            raise ValueError(f"entry {i}: expected object with 'cell' and 'value'")
        apply(wb, e.get('sheet'), e['cell'], e['value'])
    save(wb, a.file, a.out)
    print(f"{len(edits)} cell(s) updated", file=sys.stderr)


def cmd_to_csv(a):
    wb = load(a.file, True)
    warn_uncached(load(a.file), wb)
    os.makedirs(a.outdir, exist_ok=True)
    for ws in wb.worksheets:
        fn = os.path.join(a.outdir, re.sub(r'[\\/:*?"<>|]', '_', ws.title) + '.csv')
        with open(fn, 'w', newline='', encoding='utf-8-sig') as f:
            w = csv.writer(f)
            for r in ws.iter_rows(values_only=True):
                w.writerow(['' if v is None else conv(v) for v in r])
        print(fn)


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    p = argparse.ArgumentParser(description="Inspect, read, search and edit Excel (.xlsx/.xlsm) files.")
    sp = p.add_subparsers(dest='cmd', required=True)
    s = sp.add_parser('info', help="JSON summary: sheets, dimensions, formula counts, defined names")
    s.add_argument('file'); s.set_defaults(func=cmd_info)
    s = sp.add_parser('read', help="read cell values (cached values unless --formulas)")
    s.add_argument('file'); s.add_argument('--sheet', help="sheet name (default: active)")
    s.add_argument('--range', help="cell range, e.g. A1:D20")
    s.add_argument('--formulas', action='store_true', help="show formulas instead of cached values")
    s.add_argument('--format', choices=['csv', 'json', 'md'], default='csv', help="output format (default: csv)")
    s.add_argument('--header', action='store_true', help="treat first row as header")
    s.add_argument('--max-rows', type=int, default=200, help="max data rows, 0 = unlimited (default: 200)")
    s.set_defaults(func=cmd_read)
    s = sp.add_parser('find', help="case-insensitive search; prints JSON lines {sheet, cell, value}")
    s.add_argument('file'); s.add_argument('text'); s.add_argument('--sheet', help="limit to one sheet")
    s.add_argument('--regex', action='store_true', help="treat TEXT as a regular expression")
    s.set_defaults(func=cmd_find)
    s = sp.add_parser('set', help="set one cell ('=...' formula, number, YYYY-MM-DD date, true/false, text)")
    s.add_argument('file'); s.add_argument('cell'); s.add_argument('value')
    s.add_argument('--sheet', help="sheet name (default: active)")
    s.add_argument('--out', help="output file (default: in place, backup to FILE.bak)")
    s.set_defaults(func=cmd_set)
    s = sp.add_parser('set-many', help='set cells from a JSON list of {"sheet","cell","value"}')
    s.add_argument('file'); s.add_argument('jsonfile')
    s.add_argument('--out', help="output file (default: in place, backup to FILE.bak)")
    s.set_defaults(func=cmd_set_many)
    s = sp.add_parser('to-csv', help="export each sheet to OUTDIR/<sheet>.csv (utf-8-sig)")
    s.add_argument('file'); s.add_argument('outdir'); s.set_defaults(func=cmd_to_csv)
    a = p.parse_args()
    try:
        a.func(a)
    except Exception as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
