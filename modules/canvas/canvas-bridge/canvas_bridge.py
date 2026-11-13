# SPDX-License-Identifier: AGPL-3.0-or-later
"""canvas-bridge: opens chat files of AI Workbench in ONLYOFFICE Docs and saves the edits back to Google Drive (G:).

Stage M2 of the canvas (SPEC.md in chat CHAT-20261028152233-8P34). Runs on the home PC on 127.0.0.1:8089; Tailscale
Funnel publishes it at https://<pc>.<tailnet>.ts.net/cb (install.json canvas.publicUrl) next to ONLYOFFICE (/ -> 127.0.0.1:8088).

  GET  /cb/edit#<ticket>   the editor page; CO Web navigates here with a ticket the site signed after the role
                           check (db.php op canvas_ticket). Same origin as ONLYOFFICE: a page on the CO site may
                           not fetch ts.net on tailnet devices (Chrome Local Network Access).
  POST /cb/open {ticket}   the editor page; answers the signed editor config.
  GET  /cb/file/<sid>      ONLYOFFICE downloads the document (its JWT in Authorization).
  POST /cb/save/<sid>      ONLYOFFICE callback; status 2/6 -> the edited file is written over the file on G:
                           (in place, so Drive keeps the file id and makes a new revision).
  POST /cb/notebook/<sid>/markdown {mode}  "Save as Markdown" of the editor page (M4): the docx as .md next to it.
  POST /cb/doc/<sid>/share {format}  "Share" (M5): the document (docx without agent threads, or PDF) goes to the site;
                           the page opens <CO site>/canvas-share, where CO Web makes the Gmail draft.
  POST /cb/doc/<sid>/clean  "Clean copy" (M5): "<name> (clean).docx" next to the file, without the agent's threads.
  GET  /cb/conv/<token>    one-time download of a document for the ONLYOFFICE conversion service (PDF).
  GET  /cb/health          liveness.

Text files (.md/.txt) open as a notebook (M4, notebook.py): a docx made next to them by pandoc is what the editor edits.

Secrets (keyring): aiwb/onlyoffice / jwt (ONLYOFFICE JWT), aiwb/canvas / ticket (site ticket key).
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import logging
import logging.handlers
import os
import secrets
import shutil
import sys
import threading
import time
import urllib.parse
import urllib.request
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

# Freedom F10: this folder is part of the signed module canvas (modules/canvas/canvas-bridge); __pycache__ written
# next to the code would read as files added after signing
sys.dont_write_bytecode = True

import agent_threads  # noqa: E402
import aiwb_install  # noqa: E402  <repo>/install, put on sys.path by agent_threads (Freedom F1: names of this install)
import notebook
import share

VERSION = '0.4.1'
HOST, PORT = '127.0.0.1', 8089
# CANVAS_PUBLIC_URL / CANVAS_SITE: setups without install.json
PUBLIC = (os.environ.get('CANVAS_PUBLIC_URL') or aiwb_install.get('canvas.publicUrl') or '').rstrip('/')
PREFIX = '/cb'
DS_INTERNAL = 'http://127.0.0.1:8088'
ROOT = aiwb_install.workbench_root()
STATE_DIR = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'AIWorkbenchCanvas'
HERE = Path(__file__).resolve().parent
PAGE_HEADERS = {
    'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
                               "img-src 'self' data: blob:; frame-src 'self'; connect-src 'self'; "
                               "frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
    'Referrer-Policy': 'no-referrer',
    'X-Content-Type-Options': 'nosniff',
}
PLUGIN_GUID = 'asc.{3C0A6E7A-5D1B-4F2C-9A8E-C0A6E7A1B0C1}'
PLUGIN_FILES = {'config.json': 'application/json', 'index.html': 'text/html; charset=utf-8',
                'code.js': 'text/javascript; charset=utf-8', 'icon.png': 'image/png'}
PLUGIN_HEADERS = {'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
                  'Content-Security-Policy': "frame-ancestors 'self'"}
MAX_FILE = 150 * 1024 * 1024
SESSION_DAYS = 7
UNKNOWN_KEY_MIN = 5
FORCESAVE_MIN = float(os.environ.get('CANVAS_FORCESAVE_MIN') or 10)  # periodic forcesave of open documents, so Drive gets edits before the editor closes

# OOXML only: for ODF, ONLYOFFICE may return OOXML (assemblyFormatAsOrigin), which could not go back under the old name.
DOC_TYPES = {'docx': 'word', 'xlsx': 'cell', 'pptx': 'slide'}
NAMES = {m['env']: m['name'] for m in aiwb_install.get('members', [])}

log = logging.getLogger('canvas-bridge')
_lock = threading.RLock()
_write_lock = threading.Lock()  # one file write at a time, in callback order
_state: dict = {'sessions': {}, 'active': {}, 'notebooks': {}, 'clean': {}}
_conv: dict = {}  # one-time downloads for the conversion service: token -> (bytes, expires)
SITE = (os.environ.get('CANVAS_SITE') or aiwb_install.get('site.url', '')).rstrip('/')


# ---------- JWT (HS256) ----------

def _b64e(b: bytes) -> str:
    return base64.urlsafe_b64encode(b).rstrip(b'=').decode()


def _b64d(s: str) -> bytes:
    return base64.urlsafe_b64decode(s + '=' * (-len(s) % 4))


def jwt_encode(payload: dict, key: str) -> str:
    h = _b64e(json.dumps({'alg': 'HS256', 'typ': 'JWT'}, separators=(',', ':')).encode())
    p = _b64e(json.dumps(payload, separators=(',', ':'), ensure_ascii=False).encode())
    s = _b64e(hmac.new(key.encode(), f'{h}.{p}'.encode(), hashlib.sha256).digest())
    return f'{h}.{p}.{s}'


def jwt_decode(token: str, key: str) -> dict:
    try:
        h, p, s = token.split('.')
        head = json.loads(_b64d(h))
    except Exception as e:
        raise ValueError('bad token') from e
    if head.get('alg') != 'HS256':
        raise ValueError('bad alg')
    want = hmac.new(key.encode(), f'{h}.{p}'.encode(), hashlib.sha256).digest()
    if not hmac.compare_digest(want, _b64d(s)):
        raise ValueError('bad signature')
    body = json.loads(_b64d(p))
    if isinstance(body.get('exp'), (int, float)) and body['exp'] < time.time() - 30:
        raise ValueError('expired')
    return body


# ---------- secrets and state ----------

def _secret(service: str, account: str) -> str:
    import keyring
    v = keyring.get_password(service, account)
    if not v:
        raise SystemExit(f'keyring {service} / {account} is empty')
    return v


DS_KEY = ''
TICKET_KEY = ''


def _state_file() -> Path:
    return STATE_DIR / 'state.json'


def load_state() -> None:
    try:
        _state.update(json.loads(_state_file().read_text(encoding='utf-8')))
    except FileNotFoundError:
        pass
    except Exception as e:
        log.warning('state unreadable, starting empty: %s', e)


def save_state() -> None:
    with _lock:
        now = time.time()
        _state['sessions'] = {k: v for k, v in _state['sessions'].items() if v['created'] > now - SESSION_DAYS * 86400}
        tmp = _state_file().with_suffix('.tmp')
        tmp.write_text(json.dumps(_state, ensure_ascii=False, indent=1), encoding='utf-8')
        os.replace(tmp, _state_file())


def _stat(p: Path) -> str:
    """Content fingerprint (sha256): size and mtime miss a same-size change within one second."""
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


# ---------- paths ----------

def _base(project_id: str, chat_id: str | None) -> Path:
    return (ROOT / 'projects' / project_id / 'chats' / chat_id) if chat_id else (ROOT / 'projects' / project_id)


def inside(p: Path, project_id: str, chat_id: str | None) -> Path:
    """p for a new or rewritten file (notebook, Markdown, images): its real path, through any link, stays in the
    chat/project folder."""
    rp, rb = os.path.realpath(p), os.path.realpath(_base(project_id, chat_id))
    if os.path.commonpath([rp, rb]) != rb:
        raise ValueError('path outside its chat/project')
    return p


def resolve(rel: str, project_id: str, chat_id: str | None) -> Path:
    parts = rel.split('/')
    if any(x in ('', '.', '..') for x in parts) or '\\' in rel or ':' in rel:
        raise ValueError('invalid path')
    base = _base(project_id, chat_id)
    p = ROOT.joinpath(*parts)
    rp, rb = os.path.realpath(p), os.path.realpath(base)
    if os.path.commonpath([rp, rb]) != rb:
        raise ValueError('path outside its chat/project')
    if not p.is_file():
        raise FileNotFoundError(rel)
    return p


# ---------- ONLYOFFICE ----------

def _rel(p: Path) -> str:
    return Path(os.path.relpath(p, ROOT)).as_posix()


def open_notebook(src_rel: str, src: Path, project_id: str, chat_id: str | None) -> tuple[str, Path]:
    """The notebook docx of a text file (made or rebuilt here), see notebook.py."""
    with _write_lock:
        with _lock:
            m = dict(_state.setdefault('notebooks', {}).get(src_rel) or {})
        s_sha = notebook.sha(src)
        d = ROOT.joinpath(*m['docx'].split('/')) if m.get('docx') else None
        if d is not None and d.is_file():
            if m.get('src') == s_sha:
                return m['docx'], d
            with _lock:
                is_open = m['docx'] in _state['active']
            if not is_open and notebook.sha(d) == m.get('docx_sha'):
                target = d  # the text changed, the notebook did not: rebuild it
            else:  # both changed: the new text gets its own notebook, the edited one stays
                target = notebook.free_name(src.with_name(f'{src.stem} (notebook {time.strftime("%Y-%m-%d %H%M")}).docx'), 'n')
        else:
            target = src.with_suffix('.docx')
            if target.exists():
                target = notebook.free_name(src.with_name(f'{src.stem} (notebook).docx'), 'n')
        inside(target, project_id, chat_id)
        data = notebook.to_docx(src)
        if target == d:
            _write_in_place(d, data)
            log.info('notebook %s rebuilt from %s', d.name, src_rel)
        else:
            with open(target, 'xb') as f:  # never over an existing file
                f.write(data)
            log.info('notebook %s made from %s', target.name, src_rel)
        rec = {'docx': _rel(target), 'src': s_sha, 'docx_sha': notebook.sha(target), 'at': time.time()}
        with _lock:
            _state['notebooks'][src_rel] = rec
    save_state()
    return rec['docx'], target


def editor_config(t: dict) -> dict:
    rel = t['path']
    p = resolve(rel, t['projectId'], t.get('chatId'))
    if p.stat().st_size > MAX_FILE:
        raise ValueError('file too large')
    nb, view_abs = None, None
    if notebook.is_text(p):
        nb = p.name
        with _lock:
            m = dict((_state.get('notebooks') or {}).get(rel) or {})
        mapped = ROOT.joinpath(*m['docx'].split('/')) if m.get('docx') else None
        if t.get('edit'):
            rel, p = open_notebook(rel, p, t['projectId'], t.get('chatId'))
        elif mapped is not None and mapped.is_file() and m.get('src') == notebook.sha(p):
            rel, p = m['docx'], mapped  # a viewer sees the notebook as it was edited
        else:
            # A viewer may not write next to the file: a read-only copy in the local state folder.
            view_abs = notebook.view_copy(STATE_DIR, p)
            rel, p = rel + '#view', view_abs
    ext = p.suffix.lower().lstrip('.')
    if ext not in DOC_TYPES:
        raise ValueError('not an office file')
    now = time.time()
    with _write_lock, _lock:  # not while a save callback is deciding on this record
        act = _state['active'].get(rel)
        # One key per open document, so every user joins the same co-editing session. A closed document, or one
        # changed on disk while nobody had it open, gets a fresh key (ONLYOFFICE would serve its cached copy).
        if not act or (act['stat'] != _stat(p) and not act.get('editing')):
            act = {'key': secrets.token_hex(16), 'stat': _stat(p), 'seen': now, 'editing': False}
            _state['active'][rel] = act
        act['seen'] = now
        sid = secrets.token_urlsafe(24)
        _state['sessions'][sid] = {'rel': rel, 'key': act['key'], 'stat': act['stat'], 'projectId': t['projectId'], 'chatId': t.get('chatId'),
                                   'env': t['env'], 'email': t.get('email'), 'edit': bool(t.get('edit')) and not view_abs,
                                   'created': now}
        if view_abs:
            _state['sessions'][sid]['abs'] = str(view_abs)
        save_state()
    env = t['env']
    edit = bool(t.get('edit')) and not view_abs
    cfg = {
        'document': {
            'fileType': ext,
            'key': act['key'],
            'title': p.name if not view_abs else Path(nb).stem + '.docx',
            'url': f'{PUBLIC}{PREFIX}/file/{sid}',
            'permissions': {'edit': edit, 'comment': edit, 'review': edit, 'download': True, 'print': True,
                            'fillForms': edit, 'modifyFilter': edit, 'modifyContentControl': edit},
        },
        'documentType': DOC_TYPES[ext],
        'editorConfig': {
            'callbackUrl': f'{PUBLIC}{PREFIX}/save/{sid}',
            'mode': 'edit' if edit else 'view',
            'lang': 'en',
            'user': {'id': env, 'name': NAMES.get(env, env.capitalize())},
            'coEditing': {'mode': 'fast', 'change': True},
            'plugins': {'autostart': [PLUGIN_GUID], 'pluginsData': [f'{PUBLIC}{PREFIX}/plugin/config.json']} if edit and ext == 'docx' else {},
            'customization': {'forcesave': True, 'autosave': True, 'compactHeader': False, 'help': False,
                              'feedback': False, 'goback': False, 'uiTheme': 'default-light'},
        },
    }
    cfg['token'] = jwt_encode(cfg, DS_KEY)
    log.info('open %s env=%s edit=%s key=%s sid=%s…', rel, env, edit, act['key'], sid[:6])
    out = {'config': cfg, 'api': f'{PUBLIC}/web-apps/apps/api/documents/api.js', 'version': VERSION}
    if nb:
        out['notebook'] = {'source': nb}
    return out


def _ds_fetch(url: str) -> bytes:
    u = urllib.parse.urlsplit(url)
    pub = urllib.parse.urlsplit(PUBLIC)
    if (u.scheme, u.netloc) not in ((pub.scheme, pub.netloc), ('http', '127.0.0.1:8088'), ('http', 'localhost:8088')):
        raise ValueError(f'unexpected download host {u.netloc}')
    internal = DS_INTERNAL + urllib.parse.urlunsplit(('', '', u.path, u.query, ''))
    with urllib.request.urlopen(internal, timeout=120) as r:
        data = r.read(MAX_FILE + 1)
    if len(data) > MAX_FILE:
        raise ValueError('result too large')
    return data


BACKUP_DAYS = 14


def _backup(name: str, data: bytes | None = None, src: Path | None = None) -> Path:
    """Keep a copy in %LOCALAPPDATA%/AIWorkbenchCanvas/backup (pruned after BACKUP_DAYS days)."""
    d = STATE_DIR / 'backup'
    d.mkdir(parents=True, exist_ok=True)
    for old in d.iterdir():
        if old.stat().st_mtime < time.time() - BACKUP_DAYS * 86400:
            old.unlink(missing_ok=True)
    bak = d / f'{time.strftime("%Y%m%d-%H%M%S")}-{secrets.token_hex(3)}-{name}'
    if src is not None:
        shutil.copy2(src, bak)
    else:
        bak.write_bytes(data or b'')
    return bak


def _write_in_place(p: Path, data: bytes) -> None:
    """Overwrite p keeping its Drive file id. The previous content is kept in backup/ first, and restored if the write
    fails half way."""
    bak = _backup(p.name, src=p)
    try:
        with open(p, 'r+b') as f:
            f.write(data)
            f.truncate()
            f.flush()
            os.fsync(f.fileno())
    except OSError:
        log.exception('write of %s failed, restoring the previous content from %s', p, bak)
        with open(p, 'r+b') as f:
            f.write(bak.read_bytes())
            f.truncate()
        raise


def _conflict_copy(p: Path) -> Path:
    """Free name next to p for the canvas version when p changed on disk while it was open."""
    stamp = time.strftime('%Y-%m-%d %H%M')
    for i in range(1, 100):
        c = p.with_name(f'{p.stem} (canvas {stamp}{"" if i == 1 else f" {i}"}){p.suffix}')
        if not c.exists():
            return c
    raise RuntimeError('no free conflict name')


def handle_callback(sid: str, body: dict) -> dict:
    status = int(body.get('status') or 0)
    with _lock:
        s = _state['sessions'].get(sid)
    if not s:
        log.error('callback for unknown session %s… status=%s', sid[:6], status)
        return {'error': 1}
    rel = s['rel']
    key = body.get('key')
    # The whole callback under one lock, shared with /open: the open-document record cannot change between the
    # stale-key check, the write and the close (ONLYOFFICE 10 sends lastsave with status 2/6, seen in its log 31.10).
    with _write_lock:
        with _lock:
            act = _state['active'].get(rel)
            if not act and key and key == s.get('key') and 'stat' in s:
                # The open-document record was lost (state file, an early "key unknown"): this session's key is still
                # valid; rebuild the record from the session, with the content it opened for conflict detection.
                act = {'key': key, 'stat': s['stat'], 'seen': time.time(), 'editing': True}
                _state['active'][rel] = act
                log.warning('open record of %s rebuilt from session %s…', rel, sid[:6])
            if act and act['key'] == key:
                act['seen'] = time.time()
                if status == 1:
                    act['editing'] = True
        if status in (2, 6) and s.get('abs'):
            log.warning('save callback for the read-only notebook %s ignored', rel)
        elif status in (2, 6):
            p = resolve(rel, s['projectId'], s.get('chatId'))
            ft = (body.get('filetype') or p.suffix.lstrip('.')).lower()
            data = _ds_fetch(str(body.get('url') or ''))
            if not act or act['key'] != key or key != s.get('key'):
                # A late callback of an older editing session: never let it overwrite the current file.
                bak = _backup(p.stem + '.' + ft, data)
                log.warning('stale callback for %s (key %s, status %d): result kept in %s, file untouched', rel, key, status, bak)
            elif ft != p.suffix.lower().lstrip('.'):
                bak = _backup(p.stem + '.' + ft, data)
                log.error('ONLYOFFICE returned %s for %s: result kept in %s, file untouched', ft, rel, bak)
            else:
                last = str(body.get('lastsave') or '')
                if last and act.get('lastsave') and last < act['lastsave']:
                    bak = _backup(p.name, data)
                    log.warning('older save of %s arrived late (%s < %s): kept in %s', rel, last, act['lastsave'], bak)
                else:
                    target = Path(act['target']) if act.get('target') else p
                    if target == p and _stat(p) != act['stat']:
                        # Changed on disk while open (an agent, Drive): keep both, the canvas goes on in a copy.
                        target = _conflict_copy(p)
                        target.write_bytes(b'')
                        log.warning('conflict: %s changed on disk while open; canvas edits go to %s', rel, target.name)
                        with _lock:
                            act['target'] = str(target)
                    _write_in_place(target, data)
                    with _lock:
                        if body.get('userdata'):
                            act['flushed'] = str(body['userdata'])
                        if target == p:
                            act['stat'] = _stat(p)
                        if last:
                            act['lastsave'] = last
                    log.info('saved %s (%d bytes, status %d, users %s)', target.name if target != p else rel, len(data),
                             status, body.get('users'))
        if status in (2, 4):
            with _lock:
                if _state['active'].get(rel, {}).get('key') == key:  # the current record, not the one read above
                    _state['active'].pop(rel, None)
            log.info('closed %s (status %d)', rel, status)
        if status in (3, 7):
            log.error('ONLYOFFICE could not build %s (status %d)', rel, status)
    save_state()
    return {'error': 0}


def _forcesave(key: str, userdata: str = '') -> int | None:
    cmd = {'c': 'forcesave', 'key': key}
    if userdata:
        cmd['userdata'] = userdata  # comes back in the callback of this very save
    # The token both in the body and in the header (ONLYOFFICE 10 accepts either; checked 31.10).
    req = urllib.request.Request(DS_INTERNAL + '/command',
                                 data=json.dumps({**cmd, 'token': jwt_encode(cmd, DS_KEY)}).encode(),
                                 method='POST',
                                 headers={'Content-Type': 'application/json',
                                          'Authorization': 'Bearer ' + jwt_encode(cmd, DS_KEY)})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read() or b'{}').get('error')


def flush(rel: str, timeout: float = 30) -> None:
    """Bring the editors' current state into the file on disk: forcesave, then wait for its callback."""
    with _lock:
        a = _state['active'].get(rel)
        key = a['key'] if a else None
    if not key:
        return
    t0 = time.time()
    mark = 'flush-' + secrets.token_hex(8)
    err = _forcesave(key, mark)
    if err in (1, 4):  # 1: nobody is editing, 4: no changes since the last save - the file on disk is current
        return
    if err != 0:
        raise RuntimeError(f'the editor could not save (ONLYOFFICE error {err})')
    while time.time() - t0 < timeout:
        with _lock:
            a = _state['active'].get(rel)
            # only the callback of this forcesave counts: one already in flight may carry older edits
            if not a or a.get('key') != key or a.get('flushed') == mark:
                return
        time.sleep(0.5)
    raise TimeoutError('the editor did not save in time, try again')


def _media_clash(md: Path, media: dict[str, bytes]) -> bool:
    """An image of the export would replace a different existing file."""
    for name, b in media.items():
        mp = md.parent.joinpath(*name.split('/'))
        if mp.exists() and (not mp.is_file() or mp.read_bytes() != b):
            return True
    return False


def _copy_name(md: Path) -> Path:
    """A free "<stem> (canvas <date time>[ n]).md" whose image folder is free too."""
    stamp = time.strftime('%Y-%m-%d %H%M')
    for i in range(1, 100):
        c = md.with_name(f'{md.stem} (canvas {stamp}{"" if i == 1 else f" {i}"}).md')
        if not c.exists() and not c.with_name(notebook.media_dir(c)).exists():
            return c
    raise RuntimeError('no free name')


def save_markdown(s: dict, mode: str) -> dict:
    """Save as Markdown (M4): the docx as .md next to it; an existing different .md only with mode replace or copy."""
    if not s.get('edit') or s.get('abs'):
        raise PermissionError('no edit rights')
    rel = s['rel']
    p = resolve(rel, s['projectId'], s.get('chatId'))
    if p.suffix.lower() != '.docx':
        raise ValueError('not a Word document')
    flush(rel)
    with _lock:
        a = _state['active'].get(rel) or {}
        src = Path(a['target']) if a.get('target') else p  # a conflict copy holds the canvas edits
        # the .md this docx was last imported from or saved to
        known = [k for k, v in sorted((_state.get('notebooks') or {}).items(), key=lambda kv: -kv[1].get('at', 0))
                 if v.get('docx') == _rel(src) and k.lower().endswith(('.md', '.markdown'))]
    md = ROOT.joinpath(*known[0].split('/')) if known else src.with_suffix('.md')
    # under the write lock: a save callback must not rewrite the docx while pandoc reads it
    with _write_lock:
        data, media = notebook.to_markdown(src, md)
        docx_sha = notebook.sha(src)
        same_text = md.exists() and notebook.sha(md) == hashlib.sha256(data).hexdigest()
        clash = _media_clash(md, media)
        if same_text and not clash and all(md.parent.joinpath(*n.split('/')).exists() for n in media):
            return {'ok': True, 'name': md.name, 'unchanged': True}
        if (md.exists() and not same_text) or clash:
            # never replaced silently (the text or an image of its folder): the page asks the user
            if mode == 'copy':
                md = _copy_name(md)
                data, media = notebook.to_markdown(src, md)  # the copy links its own, free image folder
            elif mode != 'replace':
                return {'exists': md.name if md.exists() and not same_text else notebook.media_dir(md)}
        inside(md, s['projectId'], s.get('chatId'))
        for name in media:
            inside(md.parent.joinpath(*name.split('/')), s['projectId'], s.get('chatId'))
        if md.exists():
            _write_in_place(md, data)  # keeps the Drive file id: the old text stays in its version history
        else:
            with open(md, 'xb') as f:
                f.write(data)
        for name, b in media.items():  # images of the document, linked relatively from the .md
            mp = md.parent.joinpath(*name.split('/'))
            mp.parent.mkdir(parents=True, exist_ok=True)
            if not mp.exists():
                mp.write_bytes(b)
            elif mp.read_bytes() != b:
                _write_in_place(mp, b)
        rec = {'docx': _rel(src), 'src': notebook.sha(md), 'docx_sha': docx_sha, 'at': time.time()}
        with _lock:
            _state.setdefault('notebooks', {})[_rel(md)] = rec
    save_state()
    log.info('markdown %s saved from %s (%d bytes)', md.name, src.name, len(data))
    return {'ok': True, 'name': md.name}


# ---------- share and clean copy (M5) ----------

def _current(s: dict) -> tuple[Path, bytes]:
    """The document as the editors have it now: forcesave first; a conflict copy holds the canvas edits."""
    p = Path(s['abs']) if s.get('abs') else resolve(s['rel'], s['projectId'], s.get('chatId'))
    if not s.get('abs'):
        flush(s['rel'])
    with _lock:
        a = _state['active'].get(s['rel']) or {}
        src = Path(a['target']) if a.get('target') else p
    with _write_lock:  # not half way through a save callback
        return src, src.read_bytes()


def _to_pdf(name: str, data: bytes) -> bytes:
    tok = secrets.token_urlsafe(24)
    with _lock:
        now = time.time()
        for k in [k for k, v in _conv.items() if v[1] < now]:
            _conv.pop(k, None)
        _conv[tok] = (data, now + 300)
    try:
        ext = Path(name).suffix.lower().lstrip('.')
        req = share.converter_request(DS_INTERNAL, 'pdf-' + share.sha256(data)[:40], f'{PUBLIC}{PREFIX}/conv/{tok}', ext,
                                      name, jwt_encode, DS_KEY)
        with urllib.request.urlopen(req, timeout=180) as r:
            ans = json.loads(r.read() or b'{}')
        if ans.get('error') or not ans.get('endConvert') or not ans.get('fileUrl'):
            raise RuntimeError(f'PDF conversion failed (ONLYOFFICE error {ans.get("error")})')
        return _ds_fetch(ans['fileUrl'])
    finally:
        with _lock:
            _conv.pop(tok, None)


def prepare(s: dict, fmt: str) -> tuple[str, bytes, int]:
    """(file name, bytes, agent comments removed) of the document to hand out: docx without agent threads, or PDF."""
    src, data = _current(s)
    name = src.name if not s.get('abs') else Path(s['rel'].split('#')[0]).stem + '.docx'
    removed = 0
    if src.suffix.lower() == '.docx':
        data, removed = share.clean_docx(data)
    if fmt == 'pdf':
        data = _to_pdf(src.name, data)
        name = Path(name).stem + '.pdf'
    elif fmt != 'original':
        raise ValueError('unknown format')
    return name, data, removed


def share_to_site(s: dict, fmt: str) -> dict:
    """Share (M5): the prepared file goes to the site in chunks; CO Web makes the Gmail draft with the user's token."""
    name, data, removed = prepare(s, fmt)
    if len(data) > share.MAX_ATTACH:
        return {'error': 'too_large', 'size': len(data)}
    ext = Path(name).suffix.lower().lstrip('.')
    sid, res = None, {}
    for seq, total, b64 in share.chunks(data):
        res = agent_threads._db({'op': 'canvas_share_put', 'id': sid, 'seq': seq, 'total': total, 'env': s['env'],
                                 'email': s.get('email') or '', 'path': s['rel'].split('#')[0], 'name': name,
                                 'mime': share.MIME.get(ext, 'application/octet-stream'), 'size': len(data),
                                 'sha256': share.sha256(data), 'data': b64})
        if res.get('error') or not res.get('id'):
            raise RuntimeError(f'the site refused the file ({res.get("error") or res.get("httpStatus")})')
        sid = res['id']
    log.info('share %s as %s (%d bytes, %d agent comments removed) -> %s…', s['rel'], name, len(data), removed, sid[:6])
    return {'ok': True, 'url': f'{SITE}/canvas-share?id={urllib.parse.quote(sid)}', 'name': name, 'removed': removed}


def clean_copy(s: dict) -> dict:
    """Clean copy (M5): "<name> (clean).docx" next to the file, without agent threads; a later one rewrites it in place."""
    if not s.get('edit') or s.get('abs'):
        raise PermissionError('no edit rights')
    src, data = _current(s)
    if src.suffix.lower() != '.docx':
        raise ValueError('not a Word document')
    data, removed = share.clean_docx(data)
    with _write_lock:
        with _lock:
            known = (_state.get('clean') or {}).get(_rel(src))
        if isinstance(known, str):
            known = {'rel': known}
        target = ROOT.joinpath(*known['rel'].split('/')) if known else None
        # our earlier clean copy is rewritten only if nobody changed it since (same content as we wrote)
        if target is None or not target.is_file() or _stat(target) != known.get('sha'):
            target = src.with_name(f'{src.stem} (clean){src.suffix}')
            if target.exists():
                target = _conflict_copy(target)  # an edited or foreign file of that name: "(clean) (canvas <date>)"
        inside(target, s['projectId'], s.get('chatId'))
        if target.exists():
            _write_in_place(target, data)  # our earlier clean copy: a new Drive revision of it
        else:
            with open(target, 'xb') as f:
                f.write(data)
        with _lock:
            _state.setdefault('clean', {})[_rel(src)] = {'rel': _rel(target), 'sha': _stat(target)}
    save_state()
    log.info('clean copy %s of %s (%d agent comments removed)', target.name, src.name, removed)
    return {'ok': True, 'name': target.name, 'removed': removed}


def forcesave_loop() -> None:
    while True:
        time.sleep(FORCESAVE_MIN * 60)
        with _lock:
            # Every open document: an answer "key unknown" is the only way, besides status 2/4, to learn that it closed.
            keys = [(rel, a['key']) for rel, a in _state['active'].items()]
        for rel, key in keys:
            try:
                err = _forcesave(key)
                # 0 = save started, 4 = no changes, 1 = key unknown: all editors left, or nobody has connected yet,
                # so it closes the record only after UNKNOWN_KEY_MIN without callbacks.
                if err == 1:
                    with _lock:
                        a = _state['active'].get(rel, {})
                        if a.get('key') == key and a.get('seen', 0) < time.time() - UNKNOWN_KEY_MIN * 60:
                            _state['active'].pop(rel, None)
                    save_state()
                elif err == 0:
                    log.info('forcesave %s started', rel)
                elif err != 4:
                    log.warning('forcesave %s: error %s', rel, err)
            except Exception as e:
                log.warning('forcesave %s failed: %s', rel, e)


# ---------- HTTP ----------

def _short(path: str) -> str:
    """Request path for the log without the session secret."""
    head, _, sid = path.rpartition('/')
    return f'{head}/{sid[:6]}…' if head else path[:40]


class Handler(BaseHTTPRequestHandler):
    server_version = 'canvas-bridge/' + VERSION
    protocol_version = 'HTTP/1.1'

    def log_message(self, fmt, *args):  # noqa: N802 - quiet default access log
        log.debug('%s %s', self.address_string(), fmt % args)

    def _path(self) -> str:
        p = urllib.parse.urlsplit(self.path).path
        return p[len(PREFIX):] if p == PREFIX or p.startswith(PREFIX + '/') else p  # Funnel may strip /cb

    def _send(self, code: int, obj=None, body: bytes | None = None, ctype='application/json', extra=None):
        data = body if body is not None else json.dumps(obj).encode()
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        if self.command != 'HEAD':
            self.wfile.write(data)

    def _body(self, limit=1_000_000) -> dict:
        # Funnel forwards some bodies chunked (ONLYOFFICE callbacks): an unread rest would be parsed as the next
        # request on this keep-alive connection (seen as 400/501 answers to ONLYOFFICE).
        self.close_connection = True
        if 'chunked' in (self.headers.get('Transfer-Encoding') or '').lower():
            data = b''
            while True:
                size = int(self.rfile.readline().split(b';')[0].strip() or b'0', 16)
                if size == 0:
                    while self.rfile.readline().strip():
                        pass
                    break
                if len(data) + size > limit:
                    raise ValueError('body too large')  # checked before reading: the endpoint is public
                data += self.rfile.read(size)
                self.rfile.readline()
        else:
            n = int(self.headers.get('Content-Length') or 0)
            if n > limit:
                raise ValueError('body too large')
            data = self.rfile.read(n)
        return json.loads(data or b'{}')

    def _ds_auth(self) -> dict:
        h = self.headers.get('Authorization') or ''
        if not h.startswith('Bearer '):
            raise PermissionError('no token')
        return jwt_decode(h[7:].strip(), DS_KEY)

    def _agent_session(self, path: str) -> tuple[str, dict]:
        parts = path.split('/')  # '', 'agent', sid, action
        if len(parts) != 4:
            raise PermissionError('bad agent path')
        with _lock:
            s = _state['sessions'].get(parts[2])
        if not s:
            raise PermissionError('unknown session')
        return parts[2], s

    def do_GET(self):  # noqa: N802
        path = self._path()
        try:
            if path == '/health':
                return self._send(200, {'ok': True, 'version': VERSION})
            if path in ('/edit', '/edit.js'):
                # The editor page (CO Web navigates to /cb/edit#<ticket>): same origin as ONLYOFFICE.
                name, ctype = ('edit.html', 'text/html; charset=utf-8') if path == '/edit' else ('edit.js', 'text/javascript; charset=utf-8')
                return self._send(200, body=(HERE / name).read_bytes(), ctype=ctype, extra=PAGE_HEADERS)
            if path.startswith('/plugin/'):
                # The CO Agent plugin (M3); ONLYOFFICE loads it in a frame of the editor, same origin.
                name = path[len('/plugin/'):]
                if name not in PLUGIN_FILES:
                    return self._send(404, {'error': 'not_found'})
                body = (HERE / 'plugin' / name).read_bytes()
                if name == 'config.json':  # baseUrl of this install
                    body = body.replace(b'__PUBLIC__', PUBLIC.encode())
                return self._send(200, body=body, ctype=PLUGIN_FILES[name], extra=PLUGIN_HEADERS)
            if path.startswith('/agent/') and path.endswith('/poll'):
                sid, s = self._agent_session(path)
                return self._send(200, agent_threads.poll(s, sid))
            if path.startswith('/conv/'):
                # M5: the conversion service downloads the prepared document once (unguessable token, 5 min)
                with _lock:
                    item = _conv.pop(path[len('/conv/'):], None)
                if not item or item[1] < time.time():
                    return self._send(404, {'error': 'not_found'})
                return self._send(200, body=item[0], ctype='application/octet-stream')
            if path.startswith('/file/'):
                self._ds_auth()
                sid = path[len('/file/'):]
                with _lock:
                    s = _state['sessions'].get(sid)
                if not s:
                    return self._send(404, {'error': 'not_found'})
                p = Path(s['abs']) if s.get('abs') else resolve(s['rel'], s['projectId'], s.get('chatId'))
                data = p.read_bytes()
                log.info('serve %s (%d bytes)', s['rel'], len(data))
                return self._send(200, body=data, ctype='application/octet-stream')
            self._send(404, {'error': 'not_found'})
        except PermissionError as e:
            log.warning('GET %s refused: %s', _short(path), e)
            self._send(403, {'error': 'forbidden'})
        except (ValueError, FileNotFoundError) as e:
            log.warning('GET %s: %s', _short(path), e)
            self._send(404, {'error': 'not_found'})
        except Exception:
            log.exception('GET %s', _short(path))
            self._send(500, {'error': 'internal'})

    def do_POST(self):  # noqa: N802
        path = self._path()
        self.close_connection = True
        try:
            if path == '/open':
                body = self._body()
                try:
                    t = jwt_decode(str(body.get('ticket') or ''), TICKET_KEY)
                except ValueError as e:
                    log.warning('open refused: ticket %s', e)
                    return self._send(403, {'error': 'bad_ticket'})
                try:
                    return self._send(200, editor_config(t))
                except FileNotFoundError:
                    return self._send(404, {'error': 'file_not_found'})
                except ValueError as e:
                    return self._send(400, {'error': str(e)})
                except RuntimeError as e:  # pandoc
                    log.error('notebook of %s: %s', t.get('path'), e)
                    return self._send(500, {'error': 'notebook_failed'})
            if path.startswith('/notebook/') and path.endswith('/markdown'):
                # "Save as Markdown" of the editor page (M4); the session id is known only to the editor's users
                sid, s = self._agent_session(path)
                body = self._body()
                try:
                    return self._send(200, save_markdown(s, str(body.get('mode') or '')))
                except TimeoutError as e:
                    return self._send(409, {'error': str(e)})
                except (ValueError, RuntimeError, OSError) as e:  # OSError: file gone, ONLYOFFICE not answering
                    log.warning('markdown for %s…: %s', sid[:6], e)
                    return self._send(400, {'error': str(e)})
            if path.startswith('/doc/') and path.endswith(('/share', '/clean')):
                # Share and Clean copy of the editor page (M5); the session id is known only to the editor's users
                sid, s = self._agent_session(path)
                body = self._body()
                try:
                    if path.endswith('/clean'):
                        return self._send(200, clean_copy(s))
                    return self._send(200, share_to_site(s, 'pdf' if body.get('format') == 'pdf' else 'original'))
                except TimeoutError as e:
                    return self._send(409, {'error': str(e)})
                except (ValueError, RuntimeError, OSError, zipfile.BadZipFile) as e:
                    log.warning('%s for %s…: %s', path.rsplit('/', 1)[-1], sid[:6], e)
                    return self._send(400, {'error': str(e)})
            if path.startswith('/agent/'):
                # CO Agent plugin (M3): the session id comes from the editor's callback URL, known only to its users
                sid, s = self._agent_session(path)
                body = self._body(limit=4_000_000)
                if path.endswith('/ask'):
                    try:
                        return self._send(200, agent_threads.ask(s, body))
                    except ValueError as e:
                        return self._send(400, {'error': str(e)})
                if path.endswith('/applied'):
                    return self._send(200, agent_threads.applied(s, sid, body))
                return self._send(404, {'error': 'not_found'})
            if path.startswith('/save/'):
                tok = self._ds_auth()
                self._body()  # read after the token check; the signed copy in Authorization is what counts
                body = tok.get('payload') if isinstance(tok.get('payload'), dict) else tok
                return self._send(200, handle_callback(path[len('/save/'):], body))
            self._send(404, {'error': 'not_found'})
        except PermissionError as e:
            log.warning('POST %s refused: %s', _short(path), e)
            self._send(403, {'error': 'forbidden'})
        except Exception:
            log.exception('POST %s', _short(path))
            self._send(200 if path.startswith('/save/') else 500, {'error': 1})


class ExclusiveServer(ThreadingHTTPServer):
    # Python's default SO_REUSEADDR lets a second copy bind the same port on Windows (two bridges answered at once on
    # 31.10): the port is taken exclusively, so a second start fails with code 4.
    allow_reuse_address = False

    def server_bind(self):
        import socket
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def main() -> None:
    global DS_KEY, TICKET_KEY
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    h = logging.handlers.RotatingFileHandler(STATE_DIR / 'canvas-bridge.log', maxBytes=2_000_000, backupCount=3,
                                             encoding='utf-8')
    h.setFormatter(logging.Formatter('%(asctime)s %(levelname)s %(message)s'))
    log.addHandler(h)
    if sys.stderr:
        log.addHandler(logging.StreamHandler())
    log.setLevel(logging.INFO)
    # Freedom F1: the names of this install come from install.json (or the env variables); without them the editor
    # would get broken URLs, so stop here with the reason in the log.
    missing = [n for n, v in (('canvas.publicUrl / CANVAS_PUBLIC_URL', PUBLIC), ('site.url / CANVAS_SITE', SITE),
                              ('drive.root / AIWB_ROOT', ROOT.is_absolute() and ROOT.is_dir())) if not v]
    if missing:
        log.error('not configured: %s (install.json %s, see install/install.example.json)', ', '.join(missing),
                  aiwb_install.path())
        sys.exit(2)
    DS_KEY = _secret('aiwb/onlyoffice', 'jwt')
    TICKET_KEY = _secret('aiwb/canvas', 'ticket')
    try:
        srv = ExclusiveServer((HOST, PORT), Handler)  # first: a second copy stops here, before it starts any worker
    except OSError as e:
        log.error('cannot listen on %s:%d: %s (already running?)', HOST, PORT, e)
        raise SystemExit(4)
    load_state()
    agent_threads.init(STATE_DIR)
    threading.Thread(target=forcesave_loop, daemon=True).start()
    log.info('canvas-bridge %s on http://%s:%d, root %s', VERSION, HOST, PORT, ROOT)
    srv.serve_forever()


if __name__ == '__main__':
    main()
