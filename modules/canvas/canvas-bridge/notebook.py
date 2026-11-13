# SPDX-License-Identifier: AGPL-3.0-or-later
"""Notebook (canvas stage M4, SPEC.md 3.4): text files (.md/.txt) open in ONLYOFFICE as a Word document.

The docx is the notebook's source. Open in canvas on notes.md makes notes.docx next to it (pandoc: # ... ##### ->
Heading 1-5, lists, tables, bold/italic, links) and opens that; next time the same docx opens again. If notes.md changed
since (an agent rewrote it) and the docx was not edited, the docx is rebuilt; if both changed, the new text goes to a
new docx "notes (notebook <date time>).docx" and nothing is lost. "Save as Markdown" (the editor page header) writes
the docx back as Markdown next to it; an existing .md is replaced only after the user confirms (Drive keeps the old
version), or the result goes to a copy.

Users with view rights get a read-only notebook converted into %LOCALAPPDATA%/AIWorkbenchCanvas/view (nothing is
written next to the file).
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import urllib.parse
from pathlib import Path

EXTS = {'md': 'gfm', 'markdown': 'gfm', 'txt': 'gfm+hard_line_breaks'}  # txt: every line stays a line
MAX_TEXT = 20 * 1024 * 1024
VIEW_DAYS = 2


def is_text(p: Path | str) -> bool:
    return Path(p).suffix.lower().lstrip('.') in EXTS


def pandoc() -> str:
    """pandoc.exe: PATH may be older than the install for a process started at sign-in."""
    for c in (shutil.which('pandoc'),
              os.path.join(os.environ.get('ProgramFiles', r'C:\Program Files'), 'Pandoc', 'pandoc.exe'),
              os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Pandoc', 'pandoc.exe'),
              r'C:\AIWorkbench\pandoc\pandoc.exe'):
        if c and os.path.isfile(c):
            return c
    raise RuntimeError('pandoc is not installed')


def _run(args: list[str]) -> None:
    r = subprocess.run([pandoc(), *args], capture_output=True, timeout=120,
                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if r.returncode != 0:
        raise RuntimeError('pandoc failed: ' + r.stderr.decode('utf-8', 'replace')[:500])


def to_docx(src: Path) -> bytes:
    """Markdown/plain text -> docx bytes. Text that is not UTF-8 is read as cp1252 (old Windows notes)."""
    if src.stat().st_size > MAX_TEXT:
        raise ValueError('text file too large')
    raw = src.read_bytes()
    try:
        text = raw.decode('utf-8-sig')
    except UnicodeDecodeError:
        text = raw.decode('cp1252', 'replace')
    fmt = EXTS[src.suffix.lower().lstrip('.')]
    with tempfile.TemporaryDirectory(prefix='cb-nb-') as d:
        i, j, o = Path(d) / 'in.md', Path(d) / 'in.json', Path(d) / 'out.docx'
        i.write_text(text, encoding='utf-8')
        _run(['-f', fmt, '-t', 'json', '-o', str(j), str(i)])
        doc = json.loads(j.read_text(encoding='utf-8'))
        res = Path(d) / 'res'
        res.mkdir()
        _images(doc, src.parent, res)
        j.write_text(json.dumps(doc), encoding='utf-8')
        # pandoc sees only the vetted copies: nothing outside the file's folder, no URLs
        _run(['-f', 'json', '-t', 'docx', '--resource-path', str(res), '-o', str(o), str(j)])
        return o.read_bytes()


def _images(node, base: Path, res: Path) -> None:
    """Images of the text: a relative path that stays inside the text's folder is copied to res and kept; any other
    (absolute, '..', a URL, a link outside through a junction) becomes its caption text."""
    rb = os.path.realpath(base)
    if isinstance(node, list):
        for k, x in enumerate(node):
            if isinstance(x, dict) and x.get('t') == 'Image':
                attr, caption, (target, title) = x['c']
                ok = None
                if target and not re.match(r'^[A-Za-z][\w+.-]*:|^[/\\]', target) and '..' not in target.replace('\\', '/').split('/'):
                    p = os.path.realpath(base.joinpath(*urllib.parse.unquote(target).replace('\\', '/').split('/')))
                    if os.path.commonpath([p, rb]) == rb and os.path.isfile(p) and os.path.getsize(p) <= MAX_TEXT:
                        ok = f'i{k}-{len(list(res.iterdir()))}{Path(p).suffix.lower()}'
                        shutil.copyfile(p, res / ok)
                node[k] = {'t': 'Image', 'c': [attr, caption, [ok, title]]} if ok else {'t': 'Span', 'c': [['', [], []], caption]}
                if ok:
                    continue
            _images(node[k], base, res)
    elif isinstance(node, dict):
        for v in node.values():
            if isinstance(v, (list, dict)):
                _images(v, base, res)


def media_dir(md: Path) -> str:
    """Folder next to the .md for its images: "<md stem>.media" (a copy of the .md gets its own)."""
    return md.stem + '.media'


def to_markdown(src: Path, md: Path) -> tuple[bytes, dict[str, bytes]]:
    """docx -> GitHub Markdown (no raw HTML) and its images {path relative to the .md folder: bytes} (images go to media_dir(md),
    the text links them relatively). Tracked changes are taken as accepted, comments are left out, no line wrapping."""
    with tempfile.TemporaryDirectory(prefix='cb-nb-') as d:
        i, o = Path(d) / 'in.docx', Path(d) / 'out.md'
        shutil.copyfile(src, i)
        r = subprocess.run([pandoc(), '-f', 'docx', '-t', 'gfm-raw_html', '--wrap=none', '--track-changes=accept',
                            '--extract-media=' + media_dir(md), '-o', str(o), str(i)],
                           capture_output=True, timeout=120, cwd=d, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        if r.returncode != 0:
            raise RuntimeError('pandoc failed: ' + r.stderr.decode('utf-8', 'replace')[:500])
        media = {f.relative_to(d).as_posix(): f.read_bytes()
                 for f in (Path(d) / media_dir(md)).rglob('*') if f.is_file()}
        # pandoc writes a link's URL as its docx tooltip and reads it back as a title: [x](u "u") -> [x](u)
        return re.sub(rb'\]\((\S+) "\1"\)', rb'](\1)', o.read_bytes()), media


def sha(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def free_name(p: Path, label: str) -> Path:
    """p itself if free, else "<stem> (<label>[ n]).<ext>"."""
    if not p.exists():
        return p
    for i in range(1, 100):
        c = p.with_name(f'{p.stem} ({label}{"" if i == 1 else f" {i}"}){p.suffix}')
        if not c.exists():
            return c
    raise RuntimeError('no free name')


def view_copy(state_dir: Path, src: Path) -> Path:
    """Read-only notebook for a viewer: the docx goes to the local state folder, never next to the file."""
    d = state_dir / 'view'
    d.mkdir(parents=True, exist_ok=True)
    for old in d.iterdir():
        if old.stat().st_mtime < time.time() - VIEW_DAYS * 86400:
            old.unlink(missing_ok=True)
    # built anew on every open: images the text links may have changed while the text did not
    out = d / f'{sha(src)[:16]}-{os.getpid()}-{time.time_ns()}.docx'
    out.write_bytes(to_docx(src))
    return out
