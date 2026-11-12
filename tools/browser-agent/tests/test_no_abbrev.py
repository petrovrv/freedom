# SPDX-License-Identifier: AGPL-3.0-or-later
"""0.15.3 (review S1-01): abbreviations of long options are refused, so "--conf" is never --confirm.
python browser-agent/tests/test_no_abbrev.py  (parses only; no browser, no daemon)"""
import contextlib, importlib.util, io, pathlib, sys

spec = importlib.util.spec_from_file_location("ba", pathlib.Path(__file__).resolve().parents[1] / "browser-agent.py")
ba = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ba)
p = ba.build_parser()
fail = 0
for argv in (["click", "e12", "--conf"], ["click", "e12", "--c"], ["type", "e1", "x", "--sub"], ["press", "Enter", "--confir"],
             ["upload", "e1", "f.txt", "--conf=1"], ["navigate", "https://x", "--new"]):
    try:
        with contextlib.redirect_stderr(io.StringIO()):
            p.parse_args(argv)
        print("FAIL accepted", argv)
        fail += 1
    except SystemExit:
        print("ok   refused", argv)
a = p.parse_args(["click", "e12", "--confirm"])
fail += 0 if a.confirm else 1
print("ok   full --confirm still parses" if a.confirm else "FAIL full --confirm")
sys.exit(1 if fail else 0)
