#!/usr/bin/env python3
"""units.py - engineering unit calculations (pint 0.26), so an AI agent never does unit arithmetic in its head.

Shorthand powers are accepted: m3 -> m**3, s2 -> s**2, km2 -> km**2 (unless the token is itself a unit, e.g. Nm3).

Examples:
  python units.py convert "12 bar" psi
  python units.py convert "25 degC" degF --digits 4 --json
  python units.py calc "3 m3/h * 1000 kg/m3 * 9.81 m/s2 * 50 m" --to kW
  python units.py calc "1500 Nm3/h * 24 h"
  python units.py check "1 kWh" "3.6 MJ"
  python units.py info psi
  python units.py info MEUR --json
"""
import argparse
import json
import re
import sys

import pint

ureg = pint.UnitRegistry(autoconvert_offset_to_baseunit=True)
Q = ureg.Quantity

DEFS = [
    # Normal cubic metre: volume at normal conditions; no conversion to actual volume is performed.
    ("Nm3", "Nm3 = m**3"),
    ("ppm", "ppm = 1e-6"),
    ("EUR", "EUR = [currency]"),
    ("kEUR", "kEUR = 1000 * EUR"),
    ("MEUR", "MEUR = 1e6 * EUR"),
]

_POW = re.compile(r"\b([A-Za-z_]+)([23])\b")
_NUM_UNIT = re.compile(r"^\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([A-Za-z_°].*?)\s*$")


def is_unit(token: str) -> bool:
    try:
        return token in ureg
    except Exception:
        return False


for _name, _definition in DEFS:
    if not is_unit(_name):
        ureg.define(_definition)


def prep(text: str) -> str:
    """Rewrite shorthand powers (m3 -> m**3) for tokens that are not defined units."""
    def repl(m: re.Match) -> str:
        tok = m.group(0)
        return tok if is_unit(tok) else f"{m.group(1)}**{m.group(2)}"
    return _POW.sub(repl, text)


def as_quantity(value) -> pint.Quantity:
    return value if isinstance(value, Q) else Q(value)


def parse(text: str) -> pint.Quantity:
    """Parse '<number> <unit>' via Quantity (offset-safe), else as a full expression."""
    s = prep(text)
    m = _NUM_UNIT.match(s)
    if m:
        try:
            return Q(float(m.group(1)), m.group(2))
        except Exception:
            pass
    return as_quantity(ureg.parse_expression(s))


def unit_str(q: pint.Quantity) -> str:
    return f"{q.units:~C}"


def fmt(q: pint.Quantity, digits: int) -> str:
    return f"{q.magnitude:.{digits}g} {unit_str(q)}".strip()


def as_dict(q: pint.Quantity, digits: int) -> dict:
    return {"value": float(f"{q.magnitude:.{digits}g}"), "unit": unit_str(q), "text": fmt(q, digits)}


def emit(data: dict, as_json: bool, lines: list[str]) -> None:
    print(json.dumps(data, ensure_ascii=False, indent=2) if as_json else "\n".join(lines))


def cmd_convert(a) -> None:
    q = parse(a.quantity).to(prep(a.target))
    emit({"input": a.quantity, **as_dict(q, a.digits)}, a.json, [fmt(q, a.digits)])


def cmd_calc(a) -> None:
    q = as_quantity(ureg.parse_expression(prep(a.expression)))
    if a.to:
        r = q.to(prep(a.to))
        emit({"input": a.expression, **as_dict(r, a.digits)}, a.json, [fmt(r, a.digits)])
        return
    compact, base = q.to_compact(), q.to_base_units()
    data = {"input": a.expression, "result": as_dict(q, a.digits),
            "compact": as_dict(compact, a.digits), "base_si": as_dict(base, a.digits)}
    emit(data, a.json, [f"result:  {fmt(q, a.digits)}",
                        f"compact: {fmt(compact, a.digits)}",
                        f"base SI: {fmt(base, a.digits)}"])


def cmd_check(a) -> None:
    qa, qb = parse(a.a), parse(a.b)
    ok = qa.dimensionality == qb.dimensionality
    data = {"compatible": ok, "a": {"input": a.a, "dimensionality": str(qa.dimensionality)},
            "b": {"input": a.b, "dimensionality": str(qb.dimensionality)}}
    emit(data, a.json, [f"compatible: {'yes' if ok else 'no'}",
                        f"A: {qa.dimensionality}", f"B: {qb.dimensionality}"])


def cmd_info(a) -> None:
    q = Q(1, prep(a.unit))
    base = q.to_base_units()
    data = {"unit": a.unit, "dimensionality": str(q.dimensionality), "si": as_dict(base, a.digits)}
    emit(data, a.json, [f"unit:           {a.unit}",
                        f"dimensionality: {q.dimensionality}",
                        f"SI equivalent:  1 {unit_str(q)} = {fmt(base, a.digits)}"])


class Parser(argparse.ArgumentParser):
    def error(self, message: str):
        self.print_usage(sys.stderr)
        sys.stderr.write(f"error: {message}\n")
        sys.exit(1)


def build_parser() -> Parser:
    p = Parser(description="Engineering unit calculations (pint).",
               formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    sub = p.add_subparsers(dest="command", required=True)

    def common(sp):
        sp.add_argument("--digits", type=int, default=6, help="significant digits (default: 6)")
        sp.add_argument("--json", action="store_true", help="output JSON")

    sp = sub.add_parser("convert", help="convert a quantity to a target unit")
    sp.add_argument("quantity", help='quantity, e.g. "12 bar" or "25 degC"')
    sp.add_argument("target", help="target unit, e.g. psi")
    common(sp)
    sp.set_defaults(func=cmd_convert)

    sp = sub.add_parser("calc", help="evaluate a unit expression")
    sp.add_argument("expression", help='e.g. "3 m3/h * 1000 kg/m3 * 9.81 m/s2 * 50 m"')
    sp.add_argument("--to", help="convert result to this unit")
    common(sp)
    sp.set_defaults(func=cmd_calc)

    sp = sub.add_parser("check", help="check dimensional compatibility of two quantities/units")
    sp.add_argument("a", help="first quantity or unit")
    sp.add_argument("b", help="second quantity or unit")
    sp.add_argument("--json", action="store_true", help="output JSON")
    sp.set_defaults(func=cmd_check)

    sp = sub.add_parser("info", help="dimensionality and SI equivalent of 1 UNIT")
    sp.add_argument("unit", help="unit name, e.g. psi, Nm3, MEUR")
    common(sp)
    sp.set_defaults(func=cmd_info)
    return p


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    args = build_parser().parse_args()
    if getattr(args, "digits", 6) < 1:
        sys.stderr.write("error: --digits must be >= 1\n")
        sys.exit(1)
    try:
        args.func(args)
    except Exception as exc:
        sys.stderr.write(f"error: {type(exc).__name__}: {exc}\n")
        sys.exit(1)


if __name__ == "__main__":
    main()
