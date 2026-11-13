#!/usr/bin/env python3
"""chatgpt-sync: one-way copy of chatgpt.com chats/projects (personal account) into AI Workbench.

Commands:
  fetch    download projects, conversations (not archived), memories and scheduled tasks into the local cache (incremental)
  index    print/write a compact index of cached conversations (title, project, dates, message count, first words)
  import   write chats into AI Workbench according to a plan JSON (full chats + pointer index)
  pull     import given chats in full (adds them to the plan)
  sync     check + fetch + import for the saved plan (for the scheduler)
  check    verify the session cookie; renews it from the CO browser when it expired or ends within 14 days (exit 3 + push when that fails)
  refresh-cookie  copy the session cookie from the CO browser (Edge of CO Desktop, signed in to chatgpt.com) into keyring

Writer (CO database, projects, ids) is shared with claude-ai-sync; the plan format is the same.
"""
import argparse, datetime as dt, hashlib, importlib.util, json, os, re, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
CACHE = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'AIWorkbench' / 'chatgpt'
SITE = 'https://chatgpt.com'
API = SITE + '/backend-api'
KR_SERVICE = 'aiwb/chatgpt'  # accounts session-token-0 / session-token-1 (chunks of the cookie), or session-token (unsplit)
COOKIE = '__Secure-next-auth.session-token'


def _load(name, rel):
    spec = importlib.util.spec_from_file_location(name, HERE.parent / rel)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


cas = _load('claude_ai_sync', 'claude-ai-sync/claude-ai-sync.py')
sec = _load('aiwb_secret', 'secret-prompt/aiwb_secret.py')
cob = _load('co_cookies', 'browser-agent/co_cookies.py')
RENEW_DAYS = 14
log, now_utc, jload, jsave, iso, safe_name = cas.log, cas.now_utc, cas.jload, cas.jsave, cas.iso, cas.safe_name
make_chat_id, project_dir, ensure_project, Db, push = cas.make_chat_id, cas.project_dir, cas.ensure_project, cas.Db, cas.push
AIWB = cas.AIWB


def ts_iso(x):
    """chatgpt epoch seconds / ISO string -> ISO string (or '')."""
    if x in (None, ''):
        return ''
    if isinstance(x, (int, float)):
        return iso(x * 1000)
    return iso(x)


# ---------------------------------------------------------------- chatgpt.com client

class KeyExpired(Exception):
    pass


class GPT:
    def __init__(self):
        from curl_cffi import requests as cr  # plain HTTP clients get Cloudflare challenges
        self.s = cr.Session(impersonate='chrome')
        parts = [sec.get_secret(KR_SERVICE, f'session-token-{i}') for i in (0, 1)]
        if parts[0]:
            for i, p in enumerate(parts):
                if p:
                    self.s.cookies.set(f'{COOKIE}.{i}', p, domain='chatgpt.com')
        else:
            whole = sec.get_secret(KR_SERVICE, 'session-token')
            if not whole:
                raise KeyExpired(f'no cookie in keyring {KR_SERVICE}')
            self.s.cookies.set(COOKIE, whole, domain='chatgpt.com')
        r = self.s.get(SITE + '/api/auth/session', timeout=60)
        j = r.json() if r.status_code == 200 else {}
        if not j.get('accessToken'):
            raise KeyExpired(f'HTTP {r.status_code}, no accessToken')
        self.expires = j.get('expires')
        self.s.headers.update({'Authorization': 'Bearer ' + j['accessToken'], 'Accept': 'application/json'})

    def get(self, path, raw=False, tries=5):
        url = path if path.startswith('http') else API + path
        err, attempt, waits = '', 0, 0
        while attempt < tries:
            try:
                r = self.s.get(url, timeout=90)
            except Exception as e:
                err = str(e)
            else:
                if r.status_code == 401:
                    raise KeyExpired('HTTP 401')
                if r.status_code == 429 and waits < 12:  # rate limit: wait, does not use up tries
                    waits += 1
                    wait = int(r.headers.get('retry-after') or 0) or min(30 * waits, 300)
                    log(f'  429, waiting {wait}s')
                    time.sleep(wait)
                    continue
                if r.status_code == 429 or r.status_code >= 500:
                    err = f'HTTP {r.status_code}'
                elif r.status_code >= 400:
                    raise RuntimeError(f'GET {path}: HTTP {r.status_code} {r.text[:200]}')
                else:
                    return r.content if raw else r.json()
            attempt += 1
            if attempt < tries:
                time.sleep(5)
        raise RuntimeError(f'GET {path}: {err}')


# ---------------------------------------------------------------- fetch

def list_projects(c):
    out, cursor = [], None
    while True:
        j = c.get('/gizmos/snorlax/sidebar?conversations_per_gizmo=0' + (f'&cursor={cursor}' if cursor else ''))
        out += [it['gizmo'] for it in j.get('items') or []]
        cursor = j.get('cursor')
        if not cursor or not j.get('items'):
            return out


def list_conversations(c):
    seen = {}
    off = 0
    while True:
        j = c.get(f'/conversations?offset={off}&limit=100&order=updated')
        items = j.get('items') or []
        for it in items:
            seen[it['id']] = it
        if len(items) < 100:
            break
        off += 100
        time.sleep(0.3)
    return seen


def list_project_conversations(c, gid):
    out, cursor = [], '0'
    while cursor is not None:
        j = c.get(f'/gizmos/{gid}/conversations?cursor={cursor}')
        out += j.get('items') or []
        cursor = j.get('cursor')
        if not j.get('items'):
            break
    return out


def cmd_fetch(a):
    c = GPT()
    (CACHE / 'conv').mkdir(parents=True, exist_ok=True)
    if a.no_list and (CACHE / 'chats.json').exists():
        chats = jload(CACHE / 'chats.json', [])
        projects = jload(CACHE / 'projects.json', [])
    else:
        projects = list_projects(c)
        jsave(CACHE / 'projects.json', projects)
        found = list_conversations(c)
        for p in projects:
            gid = p['gizmo']['id']
            for it in list_project_conversations(c, gid):
                it.setdefault('gizmo_id', gid)
                found.setdefault(it['id'], it)
        chats = [ch for ch in found.values() if not ch.get('is_archived') and not ch.get('is_temporary_chat')]
        jsave(CACHE / 'chats.json', chats)
        for name, path in (('memories', '/memories'), ('automations', '/automations')):
            try:
                jsave(CACHE / f'{name}.json', c.get(path))
            except RuntimeError as e:
                log(name, e)
    fetched = jload(CACHE / 'fetched.json', {}) or {}  # id -> list update_time of the cached copy
    failed = jload(CACHE / 'failed.json', {}) or {}  # id -> error (chatgpt.com answers 500 for some very long chats)
    todo = [ch for ch in chats if a.force or fetched.get(ch['id']) != ch.get('update_time')
            or not (CACHE / 'conv' / f"{ch['id']}.json").exists()]
    if not a.retry_failed:
        todo = [ch for ch in todo if failed.get(ch['id'], {}).get('update') != ch.get('update_time')]
    if getattr(a, 'since', None):
        todo = [ch for ch in todo if (ch.get('update_time') or '') >= a.since]
    if a.limit:
        todo = todo[:a.limit]
    log(f'projects {len(projects)}, chats {len(chats)}, to download {len(todo)}')
    import threading
    from concurrent.futures import ThreadPoolExecutor
    local, lock, done = threading.local(), threading.Lock(), [0]

    def one(ch):
        if not hasattr(local, 'c'):
            local.c = GPT()  # one HTTP session per thread
        try:
            conv = local.c.get(f"/conversation/{ch['id']}", tries=2)
            jsave(CACHE / 'conv' / f"{ch['id']}.json", conv, indent=None)
            with lock:
                fetched[ch['id']] = ch.get('update_time')
                failed.pop(ch['id'], None)
        except RuntimeError as e:
            with lock:
                failed[ch['id']] = {'update': ch.get('update_time'), 'error': str(e)[:200]}
            log('fail', ch['id'], ch.get('title'), e)
        with lock:
            done[0] += 1
            if done[0] % 50 == 0:
                log(f'  {done[0]}/{len(todo)}')
                jsave(CACHE / 'fetched.json', fetched)
                jsave(CACHE / 'failed.json', failed)
        time.sleep(a.delay)

    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        list(ex.map(one, todo))
    jsave(CACHE / 'fetched.json', fetched)
    jsave(CACHE / 'failed.json', failed)
    print(json.dumps({'projects': len(projects), 'chats': len(chats), 'downloaded': len(todo) - sum(1 for ch in todo if ch['id'] in failed),
                      'failed': len(failed)}))


# ---------------------------------------------------------------- conversation helpers

HIDDEN_TYPES = {'user_editable_context', 'model_editable_context', 'thoughts', 'reasoning_recap', 'system_error'}
INLINE = re.compile('\ue200([^\ue201]*)\ue201')
IMAGE_TOOLS = {'dalle.text2im', 't2uay3k.sj1i4kz', 'image_gen'}  # generated images; screenshots of other tools are skipped


def inline(x):
    """ChatGPT inline markup: entity -> name, url -> [label](url), citations -> dropped."""
    k, *rest = x.group(1).split('\ue202')
    if k == 'entity' and rest:
        try:
            return str(json.loads(rest[0])[1])
        except (ValueError, IndexError, TypeError):
            return ''
    if k == 'url' and len(rest) >= 2:
        return f'[{rest[0]}]({rest[1]})'
    return ''


def main_branch(conv):
    mp = conv.get('mapping') or {}
    out, cur, seen = [], conv.get('current_node'), set()
    while cur in mp and cur not in seen:
        seen.add(cur)
        if mp[cur].get('message'):
            out.append(mp[cur]['message'])
        cur = mp[cur].get('parent')
    return out[::-1]


def asset_id(ptr):
    """file-service://file-XXX / sediment://file_XXX -> file id"""
    return (ptr or '').split('://', 1)[-1]


def msg_parts(m):
    """-> (role or None, text, [file ids of images], [(file id, name)] attachments)"""
    role = (m.get('author') or {}).get('role')
    c = m.get('content') or {}
    ct = c.get('content_type')
    md = m.get('metadata') or {}
    images = []
    if md.get('is_visually_hidden_from_conversation') or ct in HIDDEN_TYPES or role == 'system':
        return None, '', [], []
    texts = []
    for p in c.get('parts') or []:
        if isinstance(p, str):
            texts.append(p)
        elif isinstance(p, dict):
            if p.get('content_type') == 'image_asset_pointer':
                images.append(asset_id(p.get('asset_pointer')))
            elif p.get('content_type') == 'audio_transcription' and p.get('text'):
                texts.append(p['text'])
    if role == 'tool':  # tool output: keep generated images only
        ok = images and (m.get('author') or {}).get('name') in IMAGE_TOOLS
        return ('assistant', '', images, []) if ok else (None, '', [], [])
    rec = m.get('recipient') or 'all'
    if role == 'assistant' and rec != 'all' and ct == 'text':  # e.g. web.run queries
        brief = re.sub(r'\s+', ' ', ' '.join(t for t in texts if t) or c.get('text') or '')[:150]
        return 'assistant', f'_[tool: {rec}' + (f' — {brief}' if brief else '') + ']_', [], []
    if ct == 'code' and role == 'assistant':
        rec = m.get('recipient') or ''
        if rec.startswith('canmore.'):
            return 'assistant', f'_[canvas: {rec.split(".", 1)[1]} → artifacts/]_', [], []
        brief = re.sub(r'\s+', ' ', c.get('text') or '')[:150]
        return 'assistant', f'_[tool: {rec or c.get("language") or "code"} — {brief}]_', [], []
    if ct in ('tether_browsing_display', 'tether_quote', 'execution_output', 'sonic_webpage', 'system_error'):
        return None, '', [], []
    text = INLINE.sub(inline, '\n\n'.join(t for t in texts if t)).strip()
    att = [(x.get('id'), x.get('name') or x.get('id')) for x in md.get('attachments') or [] if x.get('id')]
    return ('user' if role == 'user' else 'assistant'), text, images, att


def conv_stats(conv):
    br = main_branch(conv)
    vis = [(m, *msg_parts(m)) for m in br]
    vis = [v for v in vis if v[1]]
    first = next((v[2] for v in vis if v[1] == 'user'), '')
    return {'messages': len(vis), 'last': ts_iso(conv.get('update_time')), 'first': re.sub(r'\s+', ' ', first)[:300]}


def save_canvas(msgs, scratch):
    docs, last = {}, None
    for m in msgs:
        rec = m.get('recipient') or ''
        if not rec.startswith('canmore.'):
            continue
        try:
            j = json.loads((m.get('content') or {}).get('text') or '{}')
        except ValueError:
            continue
        if rec == 'canmore.create_textdoc':
            last = j.get('name') or f'canvas{len(docs) + 1}'
            docs[last] = {'type': j.get('type') or '', 'content': j.get('content') or ''}
        elif rec == 'canmore.update_textdoc' and last in docs:
            for u in j.get('updates') or []:
                try:
                    docs[last]['content'] = re.sub(u.get('pattern') or '', (u.get('replacement') or '').replace('\\', '\\\\'),
                                                   docs[last]['content'], count=0 if u.get('multiple') else 1, flags=re.S)
                except re.error:
                    pass
    for name, d in docs.items():
        ext = '.md' if d['type'] in ('document', '') else '.' + d['type'].split('/')[-1].replace('react', 'jsx')
        f = scratch / 'artifacts' / (safe_name(name) + (ext if len(ext) <= 6 else '.txt'))
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(d['content'], encoding='utf-8')
    return len(docs)


EXPORT_FILES = CACHE / 'export-files'  # media from an official data export (see cmd_export)


def from_export(fid):
    if not fid or not EXPORT_FILES.is_dir():
        return None
    hits = sorted(EXPORT_FILES.glob(glob_escape(fid) + '*'))
    return hits[0] if hits else None


def glob_escape(x):
    return re.sub(r"([\[\]*?])", lambda m: "[" + m.group(1) + "]", x)


def download_file(c, conv_id, fid, folder, name=None, max_mb=50):
    """-> saved file name or None (export archive first, then chatgpt.com)"""
    hit = from_export(fid)
    if hit:
        data = hit.read_bytes()
        name = name or hit.name
    elif c is None:
        return None
    else:
        try:
            j = c.get(f'/files/download/{fid}?conversation_id={conv_id}&inline=false')
            url = j.get('download_url')
            if not url:
                return None
            data = c.get(url if url.startswith('http') else SITE + url, raw=True)
        except Exception:
            return None
    if len(data) > max_mb * 1024 * 1024:
        return None
    if not name:
        ext = '.png' if data[:4] == b'\x89PNG' else '.webp' if data[8:12] == b'WEBP' else '.jpg' if data[:2] == b'\xff\xd8' else '.bin'
        name = fid + ext
    f = folder / safe_name(name)
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_bytes(data)
    return f.name


# ---------------------------------------------------------------- index / rows

def projects_map():
    return {p['gizmo']['id']: p for p in jload(CACHE / 'projects.json', [])}


def pname(p):
    return ((p or {}).get('gizmo') or {}).get('display', {}).get('name')


class LazyConv(dict):
    """Conversation JSON loaded on first access (the cache holds GBs; rows keep only stats)."""

    def __init__(self, path, stub):
        super().__init__(stub)
        self.path, self.loaded = path, False

    def _load(self):
        if not self.loaded:
            self.update(jload(self.path, {}) or {})
            self.loaded = True

    def __getitem__(self, k):
        self._load()
        return super().__getitem__(k)

    def get(self, k, d=None):
        self._load()
        return super().get(k, d)

    def __contains__(self, k):
        self._load()
        return super().__contains__(k)


def rows_all():
    projects = projects_map()
    fetched = jload(CACHE / 'fetched.json', {}) or {}
    cache = jload(CACHE / 'stats.json', {}) or {}  # id -> {upd, messages, first, created, model}
    rows, dirty = [], False
    for ch in jload(CACHE / 'chats.json', []):
        f = CACHE / 'conv' / f"{ch['id']}.json"
        have = f.exists() and ch['id'] in fetched
        stub = {'create_time': ch.get('create_time'), 'update_time': ch.get('update_time')}
        if have:
            sc = cache.get(ch['id'])
            if not sc or sc.get('upd') != fetched[ch['id']]:
                conv = jload(f, {}) or {}
                st = conv_stats(conv)
                sc = cache[ch['id']] = {'upd': fetched[ch['id']], 'messages': st['messages'], 'first': st['first']}
                dirty = True
            conv = LazyConv(f, stub)
            st = {'messages': sc['messages'], 'first': sc['first']}
        else:  # not downloaded (failed or pending): pointer from the list data only
            conv, st = stub, {'messages': None, 'first': (ch.get('snippet') or '')[:300]}
        gid = ch.get('gizmo_id')
        rows.append({'id': ch['id'], 'kind': 'chatgpt', 'title': ch.get('title') or 'Untitled',
                     'project_uuid': gid if (gid or '').startswith('g-p-') else None, 'gpt': gid if gid and not gid.startswith('g-p-') else None,
                     'created': ts_iso(ch.get('create_time')), 'last': ts_iso(ch.get('update_time')), 'messages': st['messages'], 'first': st['first'],
                     'updated': ts_iso(ch.get('update_time')), 'conv': conv})
    if dirty:
        jsave(CACHE / 'stats.json', cache)
    return rows, projects


def cmd_index(a):
    rows, projects = rows_all()
    out = [{k: r[k] for k in ('id', 'title', 'created', 'last', 'messages', 'first', 'gpt')} | {'project': pname(projects.get(r['project_uuid'])), 'project_uuid': r['project_uuid']}
           for r in rows]
    jsave(CACHE / 'index.json', out)
    if a.tsv:
        for r in sorted(out, key=lambda r: r['last'] or '', reverse=True):
            print('\t'.join(str(r[k] or '') for k in ('id', 'project', 'created', 'last', 'messages', 'title', 'first')))
    else:
        print(json.dumps({'rows': len(out), 'file': str(CACHE / 'index.json')}))


# ---------------------------------------------------------------- AI Workbench writer

def gpt_url(cid, kind='chatgpt'):
    return f'{SITE}/c/{cid}' if kind == 'chatgpt' else f'{SITE}/g/{cid}/project' if kind == 'project' else ''


def write_chat(db, pid, chat_id, title, created, updated, parent=None, src=None):
    d = project_dir(pid) / 'chats' / chat_id
    (d / 'scratch').mkdir(parents=True, exist_ok=True)
    doc = {'chatId': chat_id, 'projectId': pid, 'title': title or 'Untitled', 'rootBranchId': 'BR-root',
           'createdUtc': iso(created), 'updatedUtc': iso(updated), 'defaultMode': 'chat'}
    if parent:
        doc['parent'] = parent
    if src:
        doc['chatgpt'] = src  # {id, kind, url, syncedUtc}
    old = jload(d / 'chat.json')
    if old:  # keep what the user changed in CO (title, archive, parent, move)
        keep = {k: old[k] for k in old if k not in ('updatedUtc', 'chatgpt')}
        doc = {**doc, **keep, 'updatedUtc': doc['updatedUtc'], 'chatgpt': src or old.get('chatgpt')}
    jsave(d / 'chat.json', doc, indent=2)
    db.put(chats=[doc])
    return d


def ev(chat_id, seq, eid, role, text, created, src_id, model=None):
    e = {'schemaVersion': 1, 'eventId': eid, 'sequence': seq,
         'type': 'user_message' if role == 'user' else 'assistant_message',
         'createdUtc': iso(created), 'chatId': chat_id, 'branchId': 'BR-root', 'text': text,
         'imported': {'source': 'chatgpt', 'originalId': src_id}}
    if role == 'user':
        e['author'] = 'chatgpt.com'
    else:
        e.update({'agent': 'codex', 'status': 'completed', 'model': model})
    return e


TOOL_RUN = re.compile(r'(?:_\[tool: [^\]]*\]_ ?){4,}')


def short_run(x):
    marks = re.findall(r'_\[tool: [^\]]*\]_', x.group(0))
    return ' '.join(marks[:2]) + f' _[… +{len(marks) - 2} tools]_ '


def import_full(db, c, st, row, pid, parent, a):
    rec = st['chats'].get(row['id'])
    if rec and rec.get('updated') == row['updated'] and rec.get('full') and not a.force:
        return rec, 'same'
    chat_id = rec['chatId'] if rec else make_chat_id('chatgpt:' + row['id'], row['created'])
    if rec and rec.get('projectId'):
        pid = rec['projectId']  # never move a chat the user may have moved/continued
    conv = row['conv']
    msgs = main_branch(conv)
    src = {'id': row['id'], 'kind': 'chatgpt', 'url': gpt_url(row['id']), 'syncedUtc': now_utc()}
    if row.get('gpt'):
        src['gpt'] = row['gpt']
    d = write_chat(db, pid, chat_id, row['title'], row['created'], row['last'], parent, src)
    scratch = d / 'scratch'
    if not a.no_files:
        save_canvas(msgs, scratch)
    items, model = [], conv.get('default_model_slug')
    for m in msgs:
        role, text, images, att = msg_parts(m)
        if not role:
            continue
        notes = []
        for fid in images:
            got = download_file(c, row['id'], fid, scratch / 'images') if not a.no_files else None
            notes.append(f'🖼 images/{got}' if got else f'🖼 изображение {fid} (не перенесено)')
        for fid, name in att:
            got = download_file(c, row['id'], fid, scratch / 'uploads', name) if not a.no_files else None
            notes.append(f'📎 {name} → uploads/{got}' if got else f'📎 {name} (не перенесено)')
        text = '\n\n'.join(x for x in [text, '\n'.join(notes)] if x).strip()
        if not text:
            continue
        model = (m.get('metadata') or {}).get('model_slug') or model
        items.append((f"EVT-gpt-{m['id']}", role, text, ts_iso(m.get('create_time')) or row['created']))
    # merge consecutive assistant pieces (tool calls + answer) into one message; tool markers of a run on one line
    merged = []
    for it in items:
        if merged and merged[-1][1] == 'assistant' == it[1]:
            sep = ' ' if merged[-1][2].endswith(']_') and it[2].startswith('_[') else '\n\n'
            merged[-1] = (merged[-1][0], 'assistant', merged[-1][2] + sep + it[2], merged[-1][3])
        else:
            merged.append(it)
    merged = [(e, r, TOOL_RUN.sub(short_run, t), ts) for e, r, t, ts in merged]
    seqmap = dict(rec.get('seqs') or {}) if rec else {}
    nxt = max([db.max_seq(chat_id)] + list(seqmap.values()) + [0]) + 1 if rec else 1
    events = []
    for eid, role, text, t in merged:
        if eid not in seqmap:
            seqmap[eid] = nxt
            nxt += 1
        events.append(ev(chat_id, seqmap[eid], eid, role, text, t, row['id'], model))
    db.put_events(events)
    rec = {'projectId': pid, 'chatId': chat_id, 'updated': row['updated'], 'full': True, 'seqs': seqmap, 'title': row['title']}
    st['chats'][row['id']] = rec
    return rec, 'imported'


def pointer_line(row, rec):
    where = f"→ чат `{rec['chatId']}`" if rec and rec.get('full') else f"[chatgpt.com]({gpt_url(row['id'])})"
    first = (row.get('first') or '').replace('|', '/').replace('\n', ' ')[:200]
    return f"| {(row.get('last') or '')[:10]} | {row['title'].replace('|', '/')} | {row.get('messages')} | {where} | {first} |"


def index_md(title, desc, rows, st, instr=None):
    out = [f'## {title}', '']
    if desc:
        out += [desc.strip(), '']
    if instr:
        out += ['<details><summary>Инструкции проекта ChatGPT</summary>', '', instr.strip()[:4000], '', '</details>', '']
    out += ['| Последнее | Чат | Сообщ. | Где | Начало |', '|---|---|---|---|---|']
    for r in sorted(rows, key=lambda r: r.get('last') or '', reverse=True):
        out.append(pointer_line(r, st['chats'].get(r['id'])))
    return '\n'.join(out) + '\n'


INDEX_HEAD = ('# Архив ChatGPT\n\nПеренесено инструментом `chatgpt-sync` (односторонняя синхронизация chatgpt.com → AI Workbench). '
              'Свежие чаты перенесены целиком («→ чат CHAT-…»), старые — только указатели. Подтянуть старый чат целиком: '
              '`python "G:\\My Drive\\AI-Workbench\\tools\\chatgpt-sync\\chatgpt-sync.py" pull <id>` (id — из ссылки chatgpt.com/c/<id>).\n\n')


def load_plan(path=None):
    p = Path(path) if path else CACHE / 'plan.json'
    plan = jload(p)
    if not plan:
        raise SystemExit(f'no plan: {p}')
    return plan


def cmd_import(a):
    plan = load_plan(a.plan)
    st = jload(CACHE / 'state.json', {}) or {}
    st.setdefault('chats', {}); st.setdefault('hubs', {})
    db = Db()
    c = None
    if not a.no_files:
        try:
            c = GPT()
        except KeyExpired:
            c = None
    rows, projects = rows_all()
    if a.only:
        only = set(a.only.split(','))
        rows = [r for r in rows if r['id'] in only or (r.get('project_uuid') or '') in only]
    targets, hubs_spec = plan.get('targets') or {}, plan.get('hubs') or {}
    stats = {'imported': 0, 'same': 0, 'pointer': 0, 'skipped': 0, 'failed': 0}
    resolved = []
    for r in rows:
        res = cas.resolve(plan, r)
        if res:
            resolved.append((r, *res))
        else:
            stats['skipped'] += 1
    for r, pid, hub, _ in resolved:
        ensure_project(db, pid, targets.get(pid, {}), st)
        if hub and hub not in st['hubs']:
            p = projects.get(hub)
            title = (hubs_spec.get(hub) or {}).get('title') or pname(p) or hub
            created = ts_iso(((p or {}).get('gizmo') or {}).get('created_at')) or now_utc()
            chat_id = make_chat_id('chatgpt-hub:' + hub, created)
            write_chat(db, pid, chat_id, '📁 ' + title, created, now_utc(), None,
                       {'id': hub, 'kind': 'project', 'url': gpt_url(hub, 'project') if p else '', 'syncedUtc': now_utc()})
            st['hubs'][hub] = {'projectId': pid, 'chatId': chat_id, 'title': title}
    jsave(CACHE / 'state.json', st)
    per_target, n = {}, 0
    for r, pid, hub, full in resolved:
        per_target.setdefault((st['chats'].get(r['id']) or {}).get('projectId') or pid, {}).setdefault(hub, []).append(r)
        rec = st['chats'].get(r['id'])
        if not (full or (rec and rec.get('full'))) or not isinstance(r['conv'], LazyConv):
            stats['pointer'] += 1
            continue
        if a.dry_run:
            stats['imported'] += 1
            continue
        parent = None
        if hub:
            h = st['hubs'][hub]
            parent = {'chatId': h['chatId'], 'seq': 0, 'kind': 'subtask', 'title': '📁 ' + h['title']}
        try:
            _, how = import_full(db, c, st, r, pid, parent, a)
            stats[how] += 1
            r['conv'] = {}  # free memory
        except Exception as e:
            stats['failed'] += 1
            log('FAIL', r['id'], r['title'], repr(e))
        n += 1
        if n % 10 == 0:
            jsave(CACHE / 'state.json', st)
            log(f'  {n} chats, {stats}')
    jsave(CACHE / 'state.json', st)
    if a.dry_run:
        print(json.dumps(stats))
        return
    if a.only:  # partial run: keep the full index files as they are
        print(json.dumps(stats))
        return
    for pid, groups in per_target.items():
        parts = [INDEX_HEAD]
        for hub, rws in sorted(groups.items(), key=lambda x: (x[0] is not None, x[0] or '')):
            g = (projects.get(hub) or {}).get('gizmo') or {}
            title = (st['hubs'].get(hub) or {}).get('title') or pname(projects.get(hub)) or 'Чаты ChatGPT'
            block = index_md(title, g.get('display', {}).get('description'), rws, st, g.get('instructions'))
            parts.append(block)
            if hub:
                h = st['hubs'][hub]
                db.put_events([ev(h['chatId'], 1, f"EVT-gpthub-{hashlib.sha1(hub.encode()).hexdigest()[:24]}", 'assistant', block,
                                  ts_iso(g.get('created_at')) or now_utc(), hub)])
        ctx = project_dir(pid) / 'context'
        ctx.mkdir(parents=True, exist_ok=True)
        (ctx / 'chatgpt-index.md').write_text('\n'.join(parts), encoding='utf-8')
    idx_f = AIWB / 'imports' / 'index.json'
    idx = jload(idx_f, {}) or {}
    ent = idx.setdefault('entries', {})
    for sid, rec in st['chats'].items():
        if rec.get('full'):
            ent.setdefault(f'chatgpt:{sid}', {'projectId': rec['projectId'], 'chatId': rec['chatId'], 'importedUtc': now_utc()})
    idx['schemaVersion'] = 1
    jsave(idx_f, idx)
    st['lastImportUtc'] = now_utc()
    jsave(CACHE / 'state.json', st)
    print(json.dumps(stats))


def cmd_pull(a):
    plan = load_plan(a.plan)
    for cid in a.ids:
        plan.setdefault('chats', {}).setdefault(cid, {})['full'] = True
    jsave(Path(a.plan) if a.plan else CACHE / 'plan.json', plan, indent=1)
    listed = {ch['id']: ch for ch in jload(CACHE / 'chats.json', [])}
    fetched = jload(CACHE / 'fetched.json', {}) or {}
    c = GPT()
    for cid in a.ids:  # old chats are pointers only: download them first
        if cid in listed and fetched.get(cid) != listed[cid].get('update_time'):
            jsave(CACHE / 'conv' / f'{cid}.json', c.get(f'/conversation/{cid}', tries=2), indent=None)
            fetched[cid] = listed[cid].get('update_time')
    jsave(CACHE / 'fetched.json', fetched)
    a.only, a.force = ','.join(a.ids), False
    cmd_import(a)


def cmd_export(a):
    """Load an official ChatGPT data export (zip or unpacked folder): conversations -> conv cache, media -> export-files."""
    import zipfile
    src = Path(a.path)
    zf = zipfile.ZipFile(src) if src.is_file() else None
    names = zf.namelist() if zf else [str(p.relative_to(src)).replace('\\', '/') for p in src.rglob('*') if p.is_file()]
    read = (lambda n: zf.read(n)) if zf else (lambda n: (src / n).read_bytes())
    listed = {ch['id']: ch for ch in jload(CACHE / 'chats.json', [])}
    fetched = jload(CACHE / 'fetched.json', {}) or {}
    (CACHE / 'conv').mkdir(parents=True, exist_ok=True)
    EXPORT_FILES.mkdir(parents=True, exist_ok=True)
    n = media = 0
    for name in names:
        base = name.rsplit('/', 1)[-1]
        if re.fullmatch(r'conversations(-\d+)?\.json', base):
            for conv in json.loads(read(name)):
                cid = conv.get('conversation_id') or conv.get('id')
                ch = listed.get(cid)
                if not ch or fetched.get(cid) == ch.get('update_time'):
                    continue  # archived/unknown, or the API copy is current
                # export is a snapshot: accept it when it is not older than the list entry (1 s tolerance)
                if (conv.get('update_time') or 0) + 1 < dt.datetime.fromisoformat(ts_iso(ch.get('update_time')).replace('Z', '+00:00')).timestamp():
                    continue
                conv['id'] = cid
                jsave(CACHE / 'conv' / f'{cid}.json', conv, indent=None)
                fetched[cid] = ch.get('update_time')
                n += 1
        elif re.match(r'file[-_]', base) and not base.endswith('.json'):
            f = EXPORT_FILES / base
            if not f.exists():
                f.write_bytes(read(name))
                media += 1
    jsave(CACHE / 'fetched.json', fetched)
    print(json.dumps({'conversations': n, 'media': media}))


def refresh_from_browser():
    """Session cookie of chatgpt.com from the CO browser -> keyring. Returns a message; the value is never printed."""
    jar = cob.site_cookies('chatgpt.com')
    parts = [jar.get(f'{COOKIE}.{i}', {}).get('value') for i in (0, 1)]
    whole = jar.get(COOKIE, {}).get('value')
    if not parts[0] and not whole:
        raise KeyExpired('CO browser is not signed in to chatgpt.com')
    import keyring
    for acc, val in (('session-token-0', parts[0]), ('session-token-1', parts[1]), ('session-token', None if parts[0] else whole)):
        if val:
            sec.set_secret(KR_SERVICE, acc, val)
        else:
            sec._delete_parts(KR_SERVICE, acc)
            try:
                keyring.delete_password(KR_SERVICE, acc)
            except keyring.errors.PasswordDeleteError:
                pass
    return 'cookie copied from the CO browser (' + ('2 parts' if parts[1] else '1 part') + ')'


def days_left(expires):
    try:
        return (dt.datetime.fromisoformat(expires.replace('Z', '+00:00')) - dt.datetime.now(dt.timezone.utc)).days
    except (AttributeError, ValueError):
        return None


def cmd_refresh_cookie(a):
    try:
        log(refresh_from_browser())
    except cob.CoBrowserNotRunning as e:
        log(e)
        sys.exit(1)
    c = GPT()
    print('ok, cookie expires', c.expires)


def cmd_check(a):
    try:
        c = GPT()
        left = days_left(c.expires)
        if left is None or left >= RENEW_DAYS or getattr(a, 'no_browser', False):
            print('ok, cookie expires', c.expires)
            return
        why = f'ends in {left} days'
    except KeyExpired as e:
        if getattr(a, 'no_browser', False):
            why = None
        else:
            why = f'expired ({e})'
        c = None
    if why:
        log('cookie', why, '- renewing from the CO browser')
        try:
            log(refresh_from_browser())
            c = GPT()
            print('ok, cookie renewed, expires', c.expires)
            return
        except Exception as e:
            log('renew from the CO browser failed:', e)
    if c is not None:  # still valid, only close to the end
        print('ok, cookie expires', c.expires, '(renew from the CO browser failed)')
        return
    push('ChatGPT sync: session cookie expired',
         'Не смог взять cookie из браузера CO. Войди в chatgpt.com в браузере CO (оранжевое окно) и запусти chatgpt-sync refresh-cookie, или введи cookie через secret-prompt.')
    sys.exit(3)


def cmd_sync(a):
    if a.stage in ('all', 'fetch'):
        cmd_check(a)
        since = (jload(Path(a.plan) if a.plan else CACHE / 'plan.json', {}) or {}).get('fresh_after')  # old chats stay pointers; pull fetches them
        cmd_fetch(argparse.Namespace(force=False, limit=None, delay=3, workers=1, no_list=False, retry_failed=False, since=since))
    if a.stage in ('all', 'import'):
        a.only, a.force, a.dry_run = None, False, False
        cmd_import(a)


def cmd_pending(a):
    plan = load_plan(a.plan)
    st = jload(CACHE / 'state.json', {}) or {}
    rows, projects = rows_all()
    print(json.dumps(cas.pending(plan, rows, st, lambda u: pname(projects.get(u))), ensure_ascii=False, indent=1))


def cmd_assign(a):
    cas.plan_assign(Path(a.plan) if a.plan else CACHE / 'plan.json', a)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('fetch'); p.add_argument('--force', action='store_true'); p.add_argument('--limit', type=int); p.add_argument('--delay', type=float, default=0.3)
    p.add_argument('--workers', type=int, default=2); p.add_argument('--since', help='only chats updated on/after YYYY-MM-DD'); p.add_argument('--no-list', action='store_true', help='reuse cached chat list')
    p.add_argument('--retry-failed', action='store_true', help='try again chats that failed for the same version')
    p = sub.add_parser('index'); p.add_argument('--tsv', action='store_true')
    for name in ('import', 'pull', 'sync'):
        p = sub.add_parser(name)
        p.add_argument('--plan', help='plan JSON (default: cache plan.json)')
        p.add_argument('--no-files', action='store_true', help='skip images/uploads/canvas')
        if name == 'import':
            p.add_argument('--only', help='comma list of chat ids or ChatGPT project ids (g-p-…)')
            p.add_argument('--force', action='store_true')
            p.add_argument('--dry-run', action='store_true')
        if name == 'pull':
            p.add_argument('ids', nargs='+', help='chatgpt.com conversation id(s) to import in full')
            p.set_defaults(dry_run=False)
        if name == 'sync':
            p.add_argument('--stage', choices=['all', 'fetch', 'import'], default='all',
                           help='fetch: download only (then pending/assign); import: write to AI Workbench only')
    p = sub.add_parser('check'); p.add_argument('--no-browser', action='store_true', help='do not renew the cookie from the CO browser')
    sub.add_parser('refresh-cookie')
    cas.add_triage_parsers(sub)
    p = sub.add_parser('export'); p.add_argument('path', help='official data export zip (Settings → Data controls → Export) or its folder')
    a = ap.parse_args()
    try:
        {'fetch': cmd_fetch, 'index': cmd_index, 'import': cmd_import, 'pull': cmd_pull, 'sync': cmd_sync, 'check': cmd_check, 'refresh-cookie': cmd_refresh_cookie, 'export': cmd_export,
         'projects': cas.cmd_projects, 'pending': cmd_pending, 'assign': cmd_assign}[a.cmd](a)
    except KeyExpired as e:
        log('chatgpt.com session cookie expired or missing:', e)
        sys.exit(3)


if __name__ == '__main__':
    main()
