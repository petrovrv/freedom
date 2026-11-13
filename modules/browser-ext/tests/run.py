# SPDX-License-Identifier: AGPL-3.0-or-later
"""Tests of the AI Workbench browser extension (not loaded by the extension itself).

  python run.py tools            # tool-level test (local fake job site) + Strict session cookie in the panel frame
  python run.py live [--dist D]  # live: CO (local build D, prod API) in the panel -> real Claude job on home-main
                                 # -> browser_* tools fill a field; deletes the chat and revokes the test device

Needs Node 22+, playwright in source/co-web/node_modules and a Playwright Chromium (branded Chrome/Edge no longer
load unpacked extensions from the command line). Live mode also needs the aiwb-e2e tool (Bridge Google token, SSH to
the database).
"""
import argparse, glob, json, os, subprocess, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
EXT = HERE.parent / 'extension'  # the unpacked extension of the module
CO_WEB = HERE.parents[2] / 'co-web'
E2E = Path(r'C:\AIWorkbench\tools\aiwb-e2e')


def chromium() -> str:
    found = sorted(glob.glob(os.path.expandvars(r'%LOCALAPPDATA%\ms-playwright\chromium-*\chrome-win64\chrome.exe')))
    if not found:
        sys.exit('No Playwright Chromium: run "npx playwright install chromium" in source/co-web')
    return found[-1]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('mode', choices=['tools', 'live'])
    ap.add_argument('--dist', default=str(CO_WEB / 'dist'))
    # outside the module folder: a file in it would break the module's signature
    ap.add_argument('--shot', default=os.path.join(os.environ.get('TEMP') or '/tmp', 'browser-ext-live-failure.png'))
    a = ap.parse_args()
    pw = (CO_WEB / 'node_modules' / 'playwright' / 'index.mjs').as_uri()
    env = dict(os.environ, PW_CHROME=chromium())
    if a.mode == 'tools':
        rc = subprocess.run(['node', str(HERE / 'tools-test.mjs'), str(EXT), pw], env=env).returncode
        return subprocess.run(['node', str(HERE / 'cookie-test.mjs'), str(EXT), pw], env=env).returncode or rc

    sys.path.insert(0, str(E2E))
    import importlib.util
    spec = importlib.util.spec_from_file_location('aiwb_e2e', E2E / 'aiwb-e2e.py')
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
    import e2e_db
    token, email = m.google_token_and_email()
    conn = e2e_db._connect()
    before = {r[0] for r in e2e_db.sql(conn, "SELECT device_id FROM web_devices WHERE name LIKE 'Browser extension%'")}
    cfg = {'token': token, 'email': email, 'ext': str(EXT), 'dist': a.dist, 'pwModule': pw, 'chrome': env['PW_CHROME'], 'shot': a.shot}
    p = subprocess.run(['node', str(HERE / 'live-test.mjs')], input=json.dumps(cfg), capture_output=True, text=True,
                       encoding='utf-8', timeout=600)
    print(p.stderr.replace(token, '***')[-3000:])
    out = p.stdout.replace(token, '***').strip().splitlines()
    print(out[-1] if out else '(no result)')
    conn = e2e_db._connect()
    new = [r[0] for r in e2e_db.sql(conn, "SELECT device_id FROM web_devices WHERE name LIKE 'Browser extension%' AND revoked = 0")
           if r[0] not in before]
    if new:
        ids = ','.join("'" + i + "'" for i in new if i.startswith('w-') and i[2:].isalnum())
        e2e_db.sql(conn, f"UPDATE web_devices SET revoked = 1 WHERE device_id IN ({ids})")
        print('revoked test devices:', ids)
    try:
        return 0 if json.loads(out[-1]).get('status') == 'green' else 1
    except Exception:
        return 1


if __name__ == '__main__':
    sys.exit(main())
