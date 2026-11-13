# SPDX-License-Identifier: AGPL-3.0-or-later
"""Neutral release artifacts of Freedom (0.4.0+): what a new user downloads from the release page.

  python release/build_installers.py OUT --version X.Y.Z [--rev HEAD] [--makensis PATH] [--skip-desktop]

OUT gets:
  freedom-X.Y.Z-src.tar.gz           git archive --prefix=freedom-X.Y.Z/ of REV (the wizard, Bridge sources, modules)
  CO-Desktop-Setup-<app>.exe         CO Desktop built WITHOUT install.json: no site, members, Google project or OAuth
                                     client inside; it reads the install.json of the setup wizard or asks at first start
  ../freedom-X.Y.Z-notes.md          draft of the release text next to OUT (edit before publishing)
Then sign: python install/freedom_release.py build OUT --version X.Y.Z; sign ... --name <maintainer>; verify --strict.

No Authenticode signature: the maintainer's personal code-signing certificate is never used for public builds; users
check the installer with the signed manifest (freedom_setup.py desktop --installer FILE), SmartScreen warns once.
The CO Desktop build uses its own build root (%LOCALAPPDATA%/AIWorkbenchBuild/co-desktop-neutral), so a maintainer's
own builds (with their install.json) are not touched, and fails when a private term (git config freedom.terms) or the
site of the local install.json is found in the uncompressed assemblies.
"""
from __future__ import annotations

import argparse
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[1]


def run(args, **kw):
    print('>', ' '.join(str(a) for a in args), flush=True)
    subprocess.run([str(a) for a in args], check=True, **kw)


def app_version() -> str:
    m = re.search(r'<Version>(\d+\.\d+\.\d+)</Version>', (REPO / 'co-desktop/src/CO.Desktop/CO.Desktop.csproj').read_text(encoding='utf-8'))
    if not m:
        raise SystemExit('CO Desktop <Version> not found')
    return m.group(1)


def find_makensis(given: str | None) -> str:
    for c in [given, shutil.which('makensis'), r'C:\Program Files (x86)\NSIS\makensis.exe', r'C:\Program Files\NSIS\makensis.exe']:
        if c and pathlib.Path(c).is_file():
            return c
    raise SystemExit('makensis not found (winget install NSIS.NSIS, or --makensis PATH)')


def private_words() -> list[str]:
    """Words that must not be inside a neutral build: the private terms of this clone and the local install's names."""
    words = []
    r = subprocess.run(['git', 'config', '--get', 'freedom.terms'], cwd=REPO, capture_output=True, text=True)
    terms = pathlib.Path(r.stdout.strip()) if r.returncode == 0 and r.stdout.strip() else None
    if terms and terms.is_file():
        for line in terms.read_text(encoding='utf-8-sig').splitlines():
            line = line.strip()
            if line and not line.startswith('#') and not line.startswith('re:'):
                words.append(line)
    sys.path.insert(0, str(REPO / 'install'))
    import aiwb_install
    url = (aiwb_install.get('site.url') or '').split('//')[-1]
    words += [w for w in [url] + [m.get('email', '') for m in aiwb_install.get('members', []) or []] if w]
    return [w for w in words if len(w) >= 4]


def leak_check(bin_root: pathlib.Path, words: list[str]) -> list[str]:
    """Own assemblies only (bin/<project>/**/<project>.dll): third-party files are not ours and hit short words by chance."""
    pats = [re.compile(r'(?<![a-z0-9])' + re.escape(w.lower()) + r'(?![a-z0-9])') for w in words]
    found = []
    for proj in [d for d in bin_root.iterdir() if d.is_dir()] if bin_root.is_dir() else []:
        for f in proj.rglob(proj.name + '.dll'):
            data = f.read_bytes()
            text = (data.decode('latin-1') + '\n' + data.decode('utf-16-le', errors='replace')).lower()
            for w, rx in zip(words, pats):
                if rx.search(text):
                    found.append(f'{f.name}: a private word ({len(w)} chars)')
    return found


def build_desktop(out: pathlib.Path, makensis: str):
    ver = app_version()
    root = pathlib.Path(os.environ['LOCALAPPDATA']) / 'AIWorkbenchBuild' / 'co-desktop-neutral'
    shutil.rmtree(root, ignore_errors=True)
    pub = root / 'publish'
    env = dict(os.environ)
    with tempfile.TemporaryDirectory() as t:
        env['AIWB_INSTALL'] = str(pathlib.Path(t) / 'none' / 'install.json')   # no install.json -> nothing embedded
        run(['dotnet', 'publish', REPO / 'co-desktop/src/CO.Desktop', '-c', 'Release', '-o', pub,
             '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', f'-p:AiwbBuildRoot={root}\\'], env=env)
    embedded = list(root.glob('obj/**/install.client.json'))
    if embedded:
        raise SystemExit(f'install.json was embedded: {embedded[0]}')
    leaks = leak_check(root / 'bin', private_words())
    if leaks:
        raise SystemExit('neutral build contains private data:\n  ' + '\n  '.join(sorted(set(leaks))))
    nsi = REPO / 'co-desktop/installer/co-desktop.nsi'
    run([makensis, '/V2', '/INPUTCHARSET', 'UTF8', f'/DVERSION={ver}', f'/DAPPDIR={pub}', nsi])
    setup = nsi.parent / f'CO-Desktop-Setup-{ver}.exe'
    shutil.move(str(setup), out / setup.name)
    print(f'ok: {out / setup.name} (neutral, no Authenticode signature)')
    return ver


def build_src(out: pathlib.Path, version: str, rev: str):
    tar = out / f'freedom-{version}-src.tar.gz'
    run(['git', 'archive', f'--prefix=freedom-{version}/', '-o', tar, rev], cwd=REPO)
    print(f'ok: {tar}')


NOTES = """# Freedom {version} — installers

**Install on Windows (≈ 2 h, most of it waiting for Google Cloud and the hosting):** download from this page

- `freedom-{version}-src.tar.gz` — the setup wizard, the server, Bridge sources, modules;
- `CO-Desktop-Setup-{app}.exe` — CO Desktop (the window of your workbench on this PC);
- `freedom-manifest.json` — the signed list of files (checks both downloads).

Unpack the archive, open a terminal in the folder and follow `install/PILOT-FRIEND.md` (start:
`python install/freedom_setup.py preflight`). The installer has no site inside: at first start CO Desktop reads the
`install.json` the wizard wrote, or asks for your own site address and Google desktop client.

**Unsigned installer:** Windows SmartScreen says "Windows protected your PC" → More info → Run anyway. Check the file
first: `python install/freedom_setup.py desktop --installer CO-Desktop-Setup-{app}.exe` (manifest signature of the
Freedom maintainers + sha256), it then installs it for you.

## Changes

(from CHANGELOG.md, section {version})
"""


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('out')
    ap.add_argument('--version', required=True)
    ap.add_argument('--rev', default='HEAD')
    ap.add_argument('--makensis')
    ap.add_argument('--skip-desktop', action='store_true')
    a = ap.parse_args(argv)
    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    app = app_version() if a.skip_desktop else build_desktop(out, find_makensis(a.makensis))
    build_src(out, a.version, a.rev)
    notes = out.parent / f'freedom-{a.version}-notes.md'   # the release text, not an asset
    if not notes.exists():
        notes.write_text(NOTES.format(version=a.version, app=app), encoding='utf-8')
    print('next: python install/freedom_release.py build', out, '--version', a.version, '&& ... sign && verify --strict')
    return 0


if __name__ == '__main__':
    sys.exit(main())
