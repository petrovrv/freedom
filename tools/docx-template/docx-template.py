#!/usr/bin/env python3
"""docx-template.py - Fill Word (.docx) templates containing Jinja2 tags (docxtpl).

Supports {{ var }}, {% for %}, {%tr for %} table rows, {%p if %} paragraphs, etc.

Examples:
  python docx-template.py vars report.docx
  python docx-template.py vars report.docx --json
  python docx-template.py make-sample report.docx --out sample.json
  python docx-template.py render report.docx data.json --out report_filled.docx
  python docx-template.py render letter.docx data.json --out letter.docx --strict

Special values in DATA.json (anywhere, recursively):
  {"$richtext": "Text", "bold": true, "italic": false, "color": "FF0000", "size": 12}
  {"$image": "logo.png", "width_mm": 80, "height_mm": 30}   (path relative to DATA.json)
  {"$listing": "line 1\\nline 2"}

Filters: {{ d|date }}, {{ d|date('%Y/%m/%d') }}, {{ x|num }}, {{ x|num(0, '.') }}
"""
import argparse
import json
import sys
from datetime import date, datetime
from pathlib import Path

import jinja2
from docx.shared import Mm
from docxtpl import DocxTemplate, InlineImage, Listing, RichText


class CliError(Exception):
    pass


def f_date(value, fmt="%d.%m.%Y"):
    if isinstance(value, (date, datetime)):
        return value.strftime(fmt)
    if value is None or value == "":
        return ""
    try:
        return datetime.fromisoformat(str(value).strip()).strftime(fmt)
    except ValueError:
        return value


def f_num(value, decimals=2, sep=" "):
    if value is None or value == "":
        return ""
    try:
        number = float(value)
    except (TypeError, ValueError):
        return value
    s = f"{number:,.{int(decimals)}f}"
    return s.replace(",", "\x00").replace(".", ",").replace("\x00", sep)


def make_env():
    env = jinja2.Environment(autoescape=True)
    env.filters["date"] = f_date
    env.filters["num"] = f_num
    return env


def load_template(path):
    p = Path(path)
    if not p.is_file():
        raise CliError(f"template not found: {p}")
    try:
        return DocxTemplate(str(p))
    except Exception as exc:
        raise CliError(f"cannot open template {p}: {exc}") from exc


def template_vars(path):
    tpl = load_template(path)
    try:
        return sorted(tpl.get_undeclared_template_variables(make_env()))
    except jinja2.TemplateSyntaxError as exc:
        raise CliError(f"template syntax error: {exc.message} (line {exc.lineno})") from exc


def convert(obj, tpl, base):
    if isinstance(obj, list):
        return [convert(v, tpl, base) for v in obj]
    if not isinstance(obj, dict):
        return obj
    if "$richtext" in obj:
        size = obj.get("size")
        return RichText(str(obj["$richtext"]), bold=bool(obj.get("bold", False)),
                        italic=bool(obj.get("italic", False)), color=obj.get("color"),
                        size=int(size * 2) if size is not None else None)
    if "$image" in obj:
        img = Path(obj["$image"])
        if not img.is_absolute():
            img = base / img
        if not img.is_file():
            raise CliError(f"image not found: {img}")
        kwargs = {}
        if obj.get("width_mm") is not None:
            kwargs["width"] = Mm(float(obj["width_mm"]))
        if obj.get("height_mm") is not None:
            kwargs["height"] = Mm(float(obj["height_mm"]))
        return InlineImage(tpl, str(img), **kwargs)
    if "$listing" in obj:
        return Listing(str(obj["$listing"]))
    return {k: convert(v, tpl, base) for k, v in obj.items()}


def cmd_vars(args):
    names = template_vars(args.template)
    if args.json:
        print(json.dumps(names, ensure_ascii=False, indent=2))
    else:
        for name in names:
            print(name)


def cmd_make_sample(args):
    sample = {name: f"<{name}>" for name in template_vars(args.template)}
    out = Path(args.out)
    out.write_text(json.dumps(sample, sort_keys=True, indent=2, ensure_ascii=False) + "\n",
                   encoding="utf-8")
    print(f"Sample written: {out} ({len(sample)} variables)")


def cmd_render(args):
    data_path = Path(args.data)
    if not data_path.is_file():
        raise CliError(f"data file not found: {data_path}")
    try:
        data = json.loads(data_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise CliError(f"invalid JSON in {data_path}: {exc}") from exc
    if not isinstance(data, dict):
        raise CliError("DATA must be a JSON object")
    names = set(template_vars(args.template))
    missing = sorted(names - data.keys())
    if args.strict and missing:
        raise CliError("missing variables: " + ", ".join(missing))
    tpl = load_template(args.template)
    context = convert(data, tpl, data_path.resolve().parent)
    try:
        tpl.render(context, jinja_env=make_env(), autoescape=True)
    except jinja2.TemplateError as exc:
        raise CliError(f"render failed: {exc}") from exc
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    tpl.save(str(out))
    print(f"Saved: {out}")
    print(f"Variables filled: {len(names & data.keys())}/{len(names)}")
    if missing:
        print("Warning: unfilled variables: " + ", ".join(missing), file=sys.stderr)


def build_parser():
    parser = argparse.ArgumentParser(
        prog="docx-template.py",
        description="Fill Word templates containing Jinja2 tags (docxtpl).",
        epilog="Run '<command> -h' for command help.")
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("vars", help="List undeclared template variables.")
    p.add_argument("template", help="Template .docx file")
    p.add_argument("--json", action="store_true", help="Output as JSON list")
    p.set_defaults(func=cmd_vars)
    p = sub.add_parser("render", help="Render template with JSON data.")
    p.add_argument("template", help="Template .docx file")
    p.add_argument("data", help="JSON file containing an object")
    p.add_argument("--out", required=True, help="Output .docx file")
    p.add_argument("--strict", action="store_true",
                   help="Fail if template variables are missing from DATA")
    p.set_defaults(func=cmd_render)
    p = sub.add_parser("make-sample", help="Write sample JSON with all variables.")
    p.add_argument("template", help="Template .docx file")
    p.add_argument("--out", required=True, help="Output JSON file")
    p.set_defaults(func=cmd_make_sample)
    return parser


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    args = build_parser().parse_args()
    try:
        args.func(args)
    except CliError as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
    except Exception as exc:
        print(f"Error: {type(exc).__name__}: {exc}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
