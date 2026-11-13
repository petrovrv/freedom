"""Read and write Google Sheets with the AI Workbench Bridge Google token (Credential Manager)."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import re
import sys
from urllib.parse import quote

import requests

CRED_TARGET = "AIWorkbench.Bridge.Google"
TOKEN_URL = "https://oauth2.googleapis.com/token"
API = "https://sheets.googleapis.com/v4/spreadsheets/"


class CREDENTIAL(ctypes.Structure):
    _fields_ = [("Flags", wt.DWORD), ("Type", wt.DWORD), ("TargetName", wt.LPWSTR), ("Comment", wt.LPWSTR),
                ("LastWritten", wt.FILETIME), ("CredentialBlobSize", wt.DWORD),
                ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)), ("Persist", wt.DWORD),
                ("AttributeCount", wt.DWORD), ("Attributes", ctypes.c_void_p), ("TargetAlias", wt.LPWSTR),
                ("UserName", wt.LPWSTR)]


def read_credential(target):
    adv = ctypes.WinDLL("advapi32", use_last_error=True)
    adv.CredReadW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.POINTER(ctypes.POINTER(CREDENTIAL))]
    adv.CredReadW.restype = wt.BOOL
    adv.CredFree.argtypes = [ctypes.c_void_p]
    p = ctypes.POINTER(CREDENTIAL)()
    if not adv.CredReadW(target, 1, 0, ctypes.byref(p)):
        sys.exit(f"CredReadW('{target}') failed (error {ctypes.get_last_error()})")
    try:
        blob = ctypes.string_at(p.contents.CredentialBlob, p.contents.CredentialBlobSize)
    finally:
        adv.CredFree(p)
    if blob[1:2] == b"\x00":
        return blob.decode("utf-16-le").rstrip("\0")
    return blob.decode("utf-8").rstrip("\0")


def token():
    c = json.loads(read_credential(CRED_TARGET))
    r = requests.post(TOKEN_URL, data={"client_id": c["client_id"], "client_secret": c["client_secret"],
                                       "refresh_token": c["refresh_token"], "grant_type": "refresh_token"}, timeout=60)
    if r.status_code != 200:
        sys.exit(f"token exchange failed: HTTP {r.status_code}: {r.text[:300]}")
    return r.json()["access_token"]


def sheet_id(s):
    m = re.search(r"/d/([A-Za-z0-9_-]+)", s)
    return m.group(1) if m else s


def check(r):
    if r.status_code >= 400:
        sys.exit(f"HTTP {r.status_code}: {r.text[:500]}")
    return r.json()


def load_rows(path):
    src = sys.stdin if path == "-" else open(path, encoding="utf-8")
    rows = json.load(src)
    if not isinstance(rows, list) or (rows and not isinstance(rows[0], list)):
        sys.exit("values must be a JSON array of rows (arrays)")
    return rows


def out(data, path):
    text = json.dumps(data, ensure_ascii=False, indent=1)
    if path:
        open(path, "w", encoding="utf-8").write(text)
        print(f"wrote {path}")
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(text)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("spreadsheet", help="spreadsheet id or full docs.google.com URL")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("meta", help="list tabs with ids and grid sizes")
    g = sub.add_parser("get", help="read a range (A1 notation, e.g. \"'Run log'!A1:F\")")
    g.add_argument("range")
    g.add_argument("--render", default="FORMATTED_VALUE", choices=["FORMATTED_VALUE", "UNFORMATTED_VALUE", "FORMULA"])
    g.add_argument("-o", "--out", help="write JSON rows to this file instead of stdout")
    for name, hlp in (("update", "overwrite a range with JSON rows"), ("append", "append JSON rows after the table")):
        p = sub.add_parser(name, help=hlp)
        p.add_argument("range")
        p.add_argument("values", help="JSON file with an array of rows, or - for stdin")
        p.add_argument("--raw", action="store_true", help="valueInputOption RAW (default USER_ENTERED)")
    n = sub.add_parser("add-sheet", help="add a new tab (fails if the title exists)")
    n.add_argument("title")
    n.add_argument("--rows", type=int, default=200)
    n.add_argument("--cols", type=int, default=26)
    n.add_argument("--frozen", type=int, default=0, help="frozen header rows")
    b = sub.add_parser("batch", help="raw spreadsheets.batchUpdate: JSON array of request objects (formatting etc.)")
    b.add_argument("requests", help="JSON file with an array of requests, or - for stdin")
    a = ap.parse_args()

    base = API + sheet_id(a.spreadsheet)
    h = {"Authorization": "Bearer " + token()}
    if a.cmd == "meta":
        props = check(requests.get(base, params={"fields": "properties.title,sheets.properties"}, headers=h, timeout=60))
        out(props, None)
    elif a.cmd in ("add-sheet", "batch"):
        if a.cmd == "add-sheet":
            reqs = [{"addSheet": {"properties": {"title": a.title, "gridProperties": {
                "rowCount": a.rows, "columnCount": a.cols, "frozenRowCount": a.frozen}}}}]
        else:
            src = sys.stdin if a.requests == "-" else open(a.requests, encoding="utf-8")
            reqs = json.load(src)
            if not isinstance(reqs, list):
                sys.exit("requests must be a JSON array")
        r = check(requests.post(base + ":batchUpdate", json={"requests": reqs}, headers=h, timeout=120))
        out(r.get("replies", r), None)
    elif a.cmd == "get":
        r = check(requests.get(base + "/values/" + quote(a.range), params={"valueRenderOption": a.render},
                               headers=h, timeout=120))
        out(r.get("values", []), a.out)
    else:
        rows = load_rows(a.values)
        params = {"valueInputOption": "RAW" if a.raw else "USER_ENTERED"}
        url = base + "/values/" + quote(a.range)
        if a.cmd == "update":
            r = check(requests.put(url, params=params, json={"values": rows}, headers=h, timeout=120))
        else:
            params["insertDataOption"] = "INSERT_ROWS"
            r = check(requests.post(url + ":append", params=params, json={"values": rows}, headers=h, timeout=120))
        out(r.get("updates", r), None)


if __name__ == "__main__":
    main()
