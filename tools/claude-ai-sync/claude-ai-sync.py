#!/usr/bin/env python3
"""claude-ai-sync: one-way copy of claude.ai chats/projects and local Cowork sessions into AI Workbench.

Commands:
  fetch    download projects (+knowledge docs) and conversations from claude.ai into the local cache (incremental)
  cowork   copy local Cowork sessions (Claude Desktop) into the cache
  index    print/write a compact index of cached conversations (title, project, dates, message count, first words)
  import   write chats into AI Workbench according to a plan JSON (full chats + pointer index)
  sync     fetch + cowork + import of new/changed items for the saved plan (for the scheduler)
  check    verify the session key; takes a newer one from the CO browser when it changed or expired (exit 3 + push when that fails)
  refresh-cookie  copy sessionKey from the CO browser (Edge of CO Desktop, signed in to claude.ai) into keyring
"""
import argparse, datetime as dt, glob, gzip, base64, hashlib, importlib.util, json, os, re, sys, time, uuid
from pathlib import Path

CACHE = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'AIWorkbench' / 'claude-ai'
API = 'https://claude.ai/api'
KR_SERVICE, KR_ACCOUNT = 'aiwb/claude-ai', 'sessionKey'
AIWB = Path(r'G:\My Drive\AI-Workbench')


def install_get(key, default=None):
    """0.3.1: one value of install.json (AIWB_INSTALL, else %LOCALAPPDATA%/AIWorkbench/install.json); default if missing."""
    path = os.environ.get('AIWB_INSTALL') or os.path.join(os.environ.get('LOCALAPPDATA', str(Path.home())), 'AIWorkbench', 'install.json')
    try:
        with open(path, encoding='utf-8-sig') as f:
            cur = json.load(f)
    except (OSError, ValueError):
        return default
    for part in key.split('.'):
        if not isinstance(cur, dict) or part not in cur:
            return default
        cur = cur[part]
    return cur


# the site and the owner's environment of this install (Freedom module chat-import): env, else install.json
SITE_URL = (os.environ.get('AIWB_SITE_URL') or install_get('site.url') or 'https://co.example.org').rstrip('/')
DB_URL = SITE_URL + '/agent/db.php'
OWNER_ENV = os.environ.get('AIWB_OWNER_ENV') or next((m.get('env') for m in install_get('members', []) or []
                                                      if isinstance(m, dict) and m.get('role') == 'owner' and m.get('env')), None) or 'owner'
COWORK_ROOT = Path(os.environ.get('APPDATA', '')) / 'Claude' / 'local-agent-mode-sessions'


def log(*a):
    print(*a, file=sys.stderr, flush=True)


def now_utc():
    return dt.datetime.now(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3] + 'Z'


def jload(p, default=None):
    try:
        return json.loads(Path(p).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return default


def jsave(p, obj, indent=1):
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_suffix(p.suffix + '.tmp')
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=indent), encoding='utf-8')
    os.replace(tmp, p)


# ---------------------------------------------------------------- claude.ai client

class KeyExpired(Exception):
    pass


class Claude:
    def __init__(self, key=None):
        import keyring
        from curl_cffi import requests as cr  # plain HTTP clients get 403 from Cloudflare
        key = key or keyring.get_password(KR_SERVICE, KR_ACCOUNT)
        if not key:
            raise KeyExpired(f'no key in keyring {KR_SERVICE}/{KR_ACCOUNT}')
        self.s = cr.Session(impersonate='chrome')
        self.s.headers.update({'Cookie': 'sessionKey=' + key, 'Accept': 'application/json'})
        self.org = None

    def get(self, path, raw=False):
        for attempt in range(4):
            try:
                r = self.s.get(API + path, timeout=60)
            except Exception as e:  # network
                err = str(e)
            else:
                if r.status_code in (401, 403) and 'organizations' in path and path.count('/') <= 2:
                    raise KeyExpired(f'HTTP {r.status_code}')
                if r.status_code == 429 or r.status_code >= 500:
                    err = f'HTTP {r.status_code}'
                elif r.status_code >= 400:
                    raise RuntimeError(f'GET {path}: HTTP {r.status_code} {r.text[:200]}')
                else:
                    return r.content if raw else r.json()
            time.sleep(3 * (attempt + 1) ** 2)
        raise RuntimeError(f'GET {path}: {err}')

    def org_id(self):
        if not self.org:
            orgs = self.get('/organizations')
            self.org = next(o['uuid'] for o in orgs if 'chat' in (o.get('capabilities') or []))
        return self.org


# ---------------------------------------------------------------- fetch

def cmd_fetch(a):
    c = Claude()
    org = c.org_id()
    (CACHE / 'conv').mkdir(parents=True, exist_ok=True)
    (CACHE / 'projects').mkdir(parents=True, exist_ok=True)
    projects = c.get(f'/organizations/{org}/projects')
    jsave(CACHE / 'projects.json', projects)
    for p in projects:
        pf = CACHE / 'projects' / f"{p['uuid']}.json"
        old = jload(pf, {})
        if old.get('updated_at') == p.get('updated_at') and not a.force:
            continue
        full = c.get(f"/organizations/{org}/projects/{p['uuid']}")
        try:
            full['docs'] = c.get(f"/organizations/{org}/projects/{p['uuid']}/docs")
        except RuntimeError as e:
            full['docs'] = []
            log('docs:', p['name'], e)
        jsave(pf, full)
    chats = c.get(f'/organizations/{org}/chat_conversations')
    jsave(CACHE / 'chats.json', chats)
    todo = []
    for ch in chats:
        f = CACHE / 'conv' / f"{ch['uuid']}.json"
        old = jload(f, None) if f.exists() else None
        if old is None or a.force or old.get('updated_at') != ch.get('updated_at'):
            todo.append(ch)
    if a.limit:
        todo = todo[:a.limit]
    log(f'projects {len(projects)}, chats {len(chats)}, to download {len(todo)}')
    for i, ch in enumerate(todo, 1):
        try:
            conv = c.get(f"/organizations/{org}/chat_conversations/{ch['uuid']}?tree=True&rendering_mode=messages&render_all_tools=true")
            jsave(CACHE / 'conv' / f"{ch['uuid']}.json", conv, indent=None)
        except RuntimeError as e:
            log('fail', ch['uuid'], e)
        if i % 50 == 0:
            log(f'  {i}/{len(todo)}')
        time.sleep(a.delay)
    print(json.dumps({'projects': len(projects), 'chats': len(chats), 'downloaded': len(todo)}))


# ---------------------------------------------------------------- conversation helpers

def main_branch(conv):
    """Messages of the current branch (leaf back to root), oldest first."""
    msgs = conv.get('chat_messages') or []
    by = {m['uuid']: m for m in msgs}
    leaf = conv.get('current_leaf_message_uuid')
    if leaf in by:
        out, cur, seen = [], by[leaf], set()
        while cur and cur['uuid'] not in seen:
            seen.add(cur['uuid'])
            out.append(cur)
            cur = by.get(cur.get('parent_message_uuid'))
        return out[::-1]
    return sorted(msgs, key=lambda m: (m.get('index', 0), m.get('created_at', '')))


def msg_text(m, tools=True):
    parts = []
    for b in m.get('content') or []:
        t = b.get('type')
        if t == 'text' and b.get('text'):
            parts.append(b['text'])
        elif tools and t == 'tool_use':
            inp = b.get('input') or {}
            name = b.get('name')
            if name == 'artifacts':
                parts.append(f"_[artifact {inp.get('command', '')}: {inp.get('title') or inp.get('id')} → artifacts/]_")
            elif name and name.startswith('message_compose'):
                for v in inp.get('variants') or [inp]:
                    head = ' · '.join(x for x in (v.get('label'), v.get('subject')) if x)
                    parts.append(f"**✉ {head or inp.get('summary_title') or 'draft'}**\n\n{v.get('body') or ''}".strip())
            elif name == 'present_files':
                parts.append('_[files: ' + ', '.join(Path(x).name for x in inp.get('filepaths') or []) + ' → outputs/]_')
            else:
                brief = inp.get('query') or inp.get('url') or inp.get('description') or inp.get('path') or inp.get('command') or ''
                brief = re.sub(r'\s+', ' ', str(brief))[:150]
                parts.append(f"_[tool: {name}{' — ' + brief if brief else ''}]_")
    if not parts and m.get('text'):
        parts.append(m['text'])
    text = '\n\n'.join(parts).strip()
    return re.sub(r'(_\[tool: [^\]]*\]_)(\n\n_\[tool: [^\]]*\]_)+', lambda x: x.group(0).replace('\n\n', ' '), text) if tools else text


def conv_stats(conv):
    br = main_branch(conv)
    last = max([m.get('created_at') or '' for m in br] or [conv.get('created_at') or ''])
    first_user = next((msg_text(m, False) for m in br if m.get('sender') == 'human'), '')
    return {'messages': len(br), 'last': last, 'first': re.sub(r'\s+', ' ', first_user)[:300]}


# ---------------------------------------------------------------- cowork (local Claude Desktop sessions)

def cowork_sessions():
    for meta in glob.glob(str(COWORK_ROOT / '*' / '*' / 'local_*.json')):
        j = jload(meta)
        if not j or not j.get('sessionId'):
            continue
        d = Path(meta).with_suffix('')
        j['_dir'] = str(d)
        yield j


def cowork_transcript(sess):
    """Find the Claude Code jsonl of a Cowork session and return [(role, text, ts)]."""
    d = Path(sess['_dir'])
    files = [str(d / 'audit.jsonl')] if (d / 'audit.jsonl').is_file() else []
    out = []
    for f in files[:1]:
        for line in open(f, encoding='utf-8', errors='replace'):
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if e.get('type') not in ('user', 'assistant') or e.get('isSidechain') or e.get('isMeta') or e.get('parent_tool_use_id'):
                continue
            msg = e.get('message') or {}
            c = msg.get('content')
            if isinstance(c, str):
                text = c
            else:
                text = '\n\n'.join(b.get('text', '') for b in c or [] if b.get('type') == 'text').strip()
            if not text or text.startswith('<') and 'command-name' in text[:200]:
                continue
            out.append((e['type'], text, e.get('timestamp')))
    # merge consecutive same-role pieces
    merged = []
    for r, t, ts in out:
        t = t.strip()
        dup = len(t) < 20000 and re.fullmatch(r'(.{3,}?)\s+\1', t, re.S)  # audit.jsonl often repeats the prompt: "x  x"
        if dup:
            t = dup.group(1).strip()
        if merged and merged[-1][0] == r and t in merged[-1][1]:
            continue
        if merged and merged[-1][0] == r:
            merged[-1] = (r, merged[-1][1] + '\n\n' + t, merged[-1][2])
        else:
            merged.append((r, t, ts))
    return merged


def cmd_cowork(a):
    (CACHE / 'cowork').mkdir(parents=True, exist_ok=True)
    n = 0
    for s in cowork_sessions():
        tr = cowork_transcript(s)
        rec = {k: s.get(k) for k in ('sessionId', 'title', 'createdAt', 'lastActivityAt', 'model', 'userSelectedFolders', 'isArchived')}
        rec['_dir'] = s['_dir']
        rec['messages'] = [{'role': r, 'text': t, 'ts': ts} for r, t, ts in tr]
        jsave(CACHE / 'cowork' / f"{s['sessionId']}.json", rec, indent=None)
        n += 1
    print(json.dumps({'cowork': n}))


# ---------------------------------------------------------------- index

def cmd_index(a):
    rows, projects = rows_all()
    for r in rows:
        r['project'] = (projects.get(r.get('project_uuid')) or {}).get('name')
        r['src'] = r['kind']
        r.pop('cowork', None)
    jsave(CACHE / 'index.json', rows)
    if a.tsv:
        for r in sorted(rows, key=lambda r: r['last'] or '', reverse=True):
            print('\t'.join(str(r[k] or '') for k in ('id', 'src', 'project', 'created', 'last', 'messages', 'title', 'first')))
    else:
        print(json.dumps({'rows': len(rows), 'file': str(CACHE / 'index.json')}))


# ---------------------------------------------------------------- AI Workbench writer

class Db:
    def __init__(self):
        import keyring, requests
        key = keyring.get_password('aiwb/co-push', 'default')
        if not key:
            raise SystemExit('no key in keyring aiwb/co-push/default')
        self.s = requests.Session()
        self.s.headers.update({'X-CO-Push-Key': key, 'Content-Type': 'application/json; charset=utf-8',
                               'User-Agent': 'claude-ai-sync/0.1'})

    def call(self, payload):
        raw = json.dumps(payload, ensure_ascii=False).encode('utf-8')
        body = json.dumps({'gz': base64.b64encode(gzip.compress(raw)).decode('ascii')})
        err = ''
        for attempt in range(4):
            try:
                r = self.s.post(DB_URL, data=body, timeout=120)
                if r.status_code < 500:
                    if r.status_code >= 400:
                        raise RuntimeError(f"db {payload.get('op')}: HTTP {r.status_code} {r.text[:300]}")
                    return r.json()
                err = f'HTTP {r.status_code}'
            except (OSError, ValueError) as e:
                err = str(e)
            time.sleep(3 * (attempt + 1))
        raise RuntimeError(f"db {payload.get('op')}: {err}")

    def put(self, **kinds):
        res = self.call({'op': 'put', **kinds})
        if res.get('rejected'):
            raise RuntimeError(f"db put rejected: {res['rejected'][:3]}")
        return res

    def put_events(self, events):
        batch, size = [], 0
        for e in events:
            n = len(e.get('text') or '')
            if batch and (len(batch) >= 200 or size + n > 4_000_000):
                self.put(events=batch)
                batch, size = [], 0
            batch.append(e)
            size += n
        if batch:
            self.put(events=batch)

    def max_seq(self, chat_id):
        try:
            res = self.call({'op': 'chat_read', 'chatId': chat_id})
        except RuntimeError:
            return 0
        return max([m.get('seq', 0) for m in res.get('messages') or []] or [0])


def iso(ts):
    """claude.ai / epoch-ms timestamp -> CO format 2026-10-18T19:15:17.524Z"""
    if ts is None or ts == '':
        return now_utc()
    if isinstance(ts, (int, float)):
        d = dt.datetime.fromtimestamp(ts / 1000, dt.timezone.utc)
    else:
        d = dt.datetime.fromisoformat(str(ts).replace('Z', '+00:00'))
    return d.astimezone(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3] + 'Z'


def make_chat_id(src_id, created):
    stamp = re.sub(r'\D', '', iso(created))[:14]
    h = hashlib.sha1(src_id.encode()).hexdigest().upper()
    alnum = ''.join(c for c in h if c.isalnum())[:4]
    return f'CHAT-{stamp}-{alnum}'


def safe_name(name, default='file'):
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]+', '_', str(name or '')).strip(' .') or default
    return name[:120]


def project_dir(pid):
    return AIWB / 'projects' / pid


def ensure_project(db, pid, spec, st):
    d = project_dir(pid)
    pf = d / 'project.json'
    if pf.exists():
        return jload(pf)
    ts = now_utc()
    proj = {'projectId': pid, 'title': spec.get('title') or pid, 'description': spec.get('description') or '',
            'defaultAgentId': 'claude', 'environmentId': OWNER_ENV, 'createdUtc': ts, 'updatedUtc': ts, 'archived': False}
    (d / 'chats').mkdir(parents=True, exist_ok=True)
    (d / 'context').mkdir(parents=True, exist_ok=True)
    jsave(pf, proj, indent=2)
    if spec.get('instructions'):
        (d / 'context' / 'project.md').write_text(spec['instructions'], encoding='utf-8')
    db.put(projects=[proj])
    st.setdefault('projects', []).append(pid)
    log('project created:', pid)
    return proj


def write_chat(db, pid, chat_id, title, created, updated, parent=None, src=None, model=None, cowork=False):
    d = project_dir(pid) / 'chats' / chat_id
    (d / 'scratch').mkdir(parents=True, exist_ok=True)
    doc = {'chatId': chat_id, 'projectId': pid, 'title': title or 'Untitled', 'rootBranchId': 'BR-root',
           'createdUtc': iso(created), 'updatedUtc': iso(updated), 'defaultMode': 'cowork' if cowork else 'chat'}
    if parent:
        doc['parent'] = parent
    if src:
        doc['claudeAi'] = src  # {id, kind, url, syncedUtc}
    old = jload(d / 'chat.json')
    if old:  # keep what the user changed in CO (title, archive, parent, move)
        keep = {k: old[k] for k in old if k not in ('updatedUtc', 'claudeAi')}
        doc = {**doc, **keep, 'updatedUtc': doc['updatedUtc'], 'claudeAi': src or old.get('claudeAi')}
    jsave(d / 'chat.json', doc, indent=2)
    db.put(chats=[doc])
    return d


def ev(chat_id, seq, eid, role, text, created, src_id, model=None, author='claude.ai'):
    e = {'schemaVersion': 1, 'eventId': eid, 'sequence': seq,
         'type': 'user_message' if role == 'user' else 'assistant_message',
         'createdUtc': iso(created), 'chatId': chat_id, 'branchId': 'BR-root', 'text': text,
         'imported': {'source': 'claude', 'originalId': src_id}}
    if role == 'user':
        e['author'] = author
    else:
        e.update({'agent': 'claude', 'status': 'completed', 'model': model})
    return e


ART_EXT = {'text/markdown': '.md', 'text/html': '.html', 'image/svg+xml': '.svg', 'application/vnd.ant.react': '.jsx',
           'application/vnd.ant.mermaid': '.mmd', 'application/vnd.ant.code': ''}


def save_artifacts(msgs, scratch):
    arts = {}
    for m in msgs:
        for b in m.get('content') or []:
            if b.get('type') != 'tool_use' or b.get('name') != 'artifacts':
                continue
            i = b.get('input') or {}
            aid = i.get('id') or 'artifact'
            a = arts.setdefault(aid, {'title': i.get('title') or aid, 'type': i.get('type'), 'lang': i.get('language'), 'content': ''})
            if i.get('title'):
                a['title'] = i['title']
            if i.get('type'):
                a['type'] = i['type']
            if i.get('command') in ('create', 'rewrite') and i.get('content') is not None:
                a['content'] = i['content']
            elif i.get('command') == 'update' and i.get('old_str') is not None:
                a['content'] = a['content'].replace(i['old_str'], i.get('new_str') or '', 1)
    n = 0
    for aid, a in arts.items():
        if not a['content']:
            continue
        ext = ART_EXT.get(a['type'] or '', '.txt')
        if ext == '' and a.get('lang'):
            ext = {'python': '.py', 'javascript': '.js', 'typescript': '.ts', 'bash': '.sh', 'powershell': '.ps1',
                   'json': '.json', 'sql': '.sql', 'html': '.html', 'css': '.css', 'vba': '.bas'}.get(a['lang'], '.txt')
        elif ext == '':
            ext = '.txt'
        f = scratch / 'artifacts' / (safe_name(a['title']) + ext)
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(a['content'], encoding='utf-8')
        n += 1
    return n


def download_files(claude, conv, msgs, scratch, max_mb=50):
    org = claude.org_id()
    got, failed = 0, 0
    want = []
    for m in msgs:
        for b in m.get('content') or []:
            if b.get('type') == 'tool_use' and b.get('name') == 'present_files':
                want += [('outputs', p) for p in (b.get('input') or {}).get('filepaths') or []]
        for a in m.get('files') or []:
            if a.get('path'):
                want.append(('uploads', a['path']))
    for sub, path in dict.fromkeys(want):
        f = scratch / sub / safe_name(Path(path).name)
        if f.exists():
            continue
        try:
            data = claude.get(f"/organizations/{org}/conversations/{conv['uuid']}/wiggle/download-file?path={path}", raw=True)
            if len(data) > max_mb * 1024 * 1024:
                failed += 1
                continue
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_bytes(data)
            got += 1
        except Exception:
            failed += 1
    return got, failed


def save_attachments(msgs, scratch):
    notes = {}
    for i, m in enumerate(msgs):
        for a in m.get('attachments') or []:
            if not a.get('extracted_content'):
                continue
            name = safe_name(a.get('file_name') or 'attachment')
            if not re.search(r'\.(txt|md)$', name, re.I):
                name += '.txt'
            f = scratch / 'imported' / f'{i:04d}-{name}'
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_text(a['extracted_content'], encoding='utf-8')
            notes.setdefault(m['uuid'], []).append(f"📎 {a.get('file_name')} → imported/{f.name}")
        for a in m.get('files') or []:
            notes.setdefault(m['uuid'], []).append(f"📎 {a.get('file_name')} → uploads/ (if still available)")
    return notes


def claude_url(cid, kind):
    return f'https://claude.ai/chat/{cid}' if kind == 'claude' else f'cowork:{cid}'


# ---------------------------------------------------------------- plan resolution

def load_plan(path=None):
    p = Path(path) if path else CACHE / 'plan.json'
    plan = jload(p)
    if not plan:
        raise SystemExit(f'no plan: {p}')
    return plan


def resolve(plan, row):
    """-> (target projectId, hub key or None, full bool) or None to skip."""
    cid = row['id']
    if cid in set(plan.get('skip') or []):
        return None
    o = (plan.get('chats') or {}).get(cid, {})
    g = (plan.get('groups') or {}).get(row.get('project_uuid') or '', {})
    target = o.get('target') or g.get('target') or plan['default_target']
    hub = o['hub'] if 'hub' in o else (row.get('project_uuid') if g.get('hub') else None)
    if 'hub' not in o and not g and plan.get('default_hub') and target == plan['default_target']:
        hub = plan['default_hub']
    full = o.get('full')
    if full is None:
        full = (row.get('last') or '') >= plan['fresh_after'] and (row.get('messages') or 0) >= plan.get('min_messages', 2)
    return target, hub, bool(full)


def load_conv(cid):
    return jload(CACHE / 'conv' / f'{cid}.json')


def rows_all():
    projects = {p['uuid']: p for p in jload(CACHE / 'projects.json', [])}
    stats = jload(CACHE / 'stats.json', {}) or {}
    rows, dirty = [], False
    for ch in jload(CACHE / 'chats.json', []):
        f = CACHE / 'conv' / f"{ch['uuid']}.json"
        if not f.exists():
            continue
        st = stats.get(ch['uuid'])
        if not st or st.get('updated') != ch.get('updated_at') or st.get('mtime') != f.stat().st_mtime:
            conv = jload(f)
            if not conv:
                continue
            st = {**conv_stats(conv), 'updated': ch.get('updated_at'), 'mtime': f.stat().st_mtime}
            stats[ch['uuid']] = st
            dirty = True
        rows.append({'id': ch['uuid'], 'kind': 'claude', 'title': ch.get('name') or 'Untitled', 'project_uuid': ch.get('project_uuid'),
                     'created': ch.get('created_at'), 'last': st['last'], 'messages': st['messages'], 'first': st['first'],
                     'updated': ch.get('updated_at')})
    if dirty:
        jsave(CACHE / 'stats.json', stats, indent=None)
    for f in glob.glob(str(CACHE / 'cowork' / '*.json')):
        s = jload(f)
        ms = s.get('messages') or []
        rows.append({'id': s['sessionId'], 'kind': 'cowork', 'title': s.get('title') or 'Cowork', 'project_uuid': None,
                     'created': iso(s.get('createdAt')), 'last': iso(s.get('lastActivityAt')), 'messages': len(ms),
                     'first': re.sub(r'\s+', ' ', next((m['text'] for m in ms if m['role'] == 'user'), ''))[:300],
                     'updated': iso(s.get('lastActivityAt')), 'cowork': s})
    return rows, projects



# ---------------------------------------------------------------- triage of new chats (shared with chatgpt-sync)

def aiwb_projects():
    """Existing AI Workbench projects: [{id, title, description}] (not archived)."""
    out = []
    for f in sorted((AIWB / 'projects').glob('*/project.json')):
        j = jload(f) or {}
        if j.get('projectId') and not j.get('archived'):
            out.append({'id': j['projectId'], 'title': j.get('title') or j['projectId'], 'description': (j.get('description') or '')[:200]})
    return out


def pending(plan, rows, st, project_name=lambda u: None):
    """New items the plan has no decision for: chats (fresh, never imported, no override, source project not mapped)
    and source projects without a group."""
    groups, over, skip = plan.get('groups') or {}, plan.get('chats') or {}, set(plan.get('skip') or [])
    chats, projs = [], {}
    for r in rows:
        if r['id'] in st.get('chats', {}) or r['id'] in over or r['id'] in skip:
            continue
        pu = r.get('project_uuid')
        if (pu and pu in groups) or (r.get('last') or '') < plan['fresh_after']:
            continue  # decided by the group, or old: stays a pointer in the default project
        if pu:
            projs.setdefault(pu, {'id': pu, 'name': project_name(pu), 'chats': 0})['chats'] += 1
            continue
        chats.append({'id': r['id'], 'kind': r.get('kind'), 'title': r.get('title'), 'last': (r.get('last') or '')[:10],
                      'messages': r.get('messages'), 'first': (r.get('first') or '')[:200]})
    return {'chats': sorted(chats, key=lambda x: x['last'], reverse=True), 'projects': list(projs.values())}


def plan_assign(plan_file, a):
    """Write decisions into the plan: chats -> plan.chats, source projects (--project) -> plan.groups; new AIWB project -> plan.targets."""
    plan = jload(plan_file) or {}
    t = a.target
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,60}', t):
        raise SystemExit('bad --target (letters, digits, _ -)')
    known = {p['id'] for p in aiwb_projects()} | set(plan.get('targets') or {})
    if t not in known:
        if not a.title:
            raise SystemExit(f'project {t} does not exist: give --title (and --description) to create it')
        plan.setdefault('targets', {})[t] = {'title': a.title, 'description': a.description or ''}
    for i in a.ids:
        if a.project:
            plan.setdefault('groups', {})[i] = {'target': t, 'hub': bool(a.hub_folder)}
        else:
            o = {'target': t}
            if a.hub:
                o['hub'] = a.hub
                if a.hub_title:
                    plan.setdefault('hubs', {})[a.hub] = {'title': a.hub_title}
            plan.setdefault('chats', {})[i] = o
    jsave(plan_file, plan, indent=1)
    print(json.dumps({'assigned': len(a.ids), 'target': t, 'new_project': t not in known}))


def add_triage_parsers(sub):
    sub.add_parser('projects', help='list AI Workbench projects (targets for assign)')
    p = sub.add_parser('pending', help='new chats/projects without a decision in the plan')
    p.add_argument('--plan')
    p = sub.add_parser('assign', help='put chats (or source projects with --project) into an AI Workbench project')
    p.add_argument('ids', nargs='+')
    p.add_argument('--target', required=True, help='AI Workbench projectId (existing or new)')
    p.add_argument('--title', help='title of a NEW project'); p.add_argument('--description')
    p.add_argument('--project', action='store_true', help='ids are source projects: all their chats go to --target')
    p.add_argument('--hub-folder', action='store_true', help='with --project: the source project becomes a folder chat with subchats')
    p.add_argument('--hub', help='folder key inside the target (chats only)'); p.add_argument('--hub-title')
    p.add_argument('--plan')


def cmd_projects(a):
    for p in aiwb_projects():
        print('\t'.join([p['id'], p['title'], p['description']]))


# ---------------------------------------------------------------- import

def import_full(db, claude, st, row, pid, parent, a):
    rec = st['chats'].get(row['id'])
    if rec and rec.get('updated') == row['updated'] and rec.get('full') and not a.force:
        return rec, 'same'
    chat_id = rec['chatId'] if rec else make_chat_id(row['id'], row['created'])
    if rec and rec.get('projectId') != pid and rec.get('projectId'):
        pid = rec['projectId']  # never move a chat the user may have moved/continued; plan changes apply to new chats only
    src = {'id': row['id'], 'kind': row['kind'], 'url': claude_url(row['id'], row['kind']), 'syncedUtc': now_utc()}
    if row['kind'] == 'claude':
        conv = load_conv(row['id'])
        msgs = main_branch(conv)
        d = write_chat(db, pid, chat_id, row['title'], row['created'], row['last'], parent, src)
        scratch = d / 'scratch'
        notes = save_attachments(msgs, scratch)
        if not a.no_files:
            save_artifacts(msgs, scratch)
            if claude:
                download_files(claude, conv, msgs, scratch)
        items = []
        for m in msgs:
            role = 'user' if m.get('sender') == 'human' else 'assistant'
            text = msg_text(m)
            if notes.get(m['uuid']):
                text = (text + '\n\n' + '\n'.join(notes[m['uuid']])).strip()
            if not text:
                continue
            items.append((f"EVT-cai-{m['uuid']}", role, text, m.get('created_at')))
        model = conv.get('model')
    else:
        s = row['cowork']
        d = write_chat(db, pid, chat_id, row['title'], row['created'], row['last'], parent, src, cowork=True)
        items = [(f"EVT-cow-{hashlib.sha1((row['id'] + str(i)).encode()).hexdigest()[:24]}", m['role'], m['text'], m.get('ts'))
                 for i, m in enumerate(s.get('messages') or [])]
        model = s.get('model')
        if not a.no_files:
            out = Path(s['_dir']) / 'outputs'
            if out.is_dir():
                total = 0
                for root, dirs, files in os.walk(out):
                    dirs[:] = [x for x in dirs if x not in ('node_modules', '.git', '__pycache__', '.venv', 'venv')]
                    for name in files:
                        f = Path(root) / name
                        try:
                            size = f.stat().st_size
                            if total + size >= 300 * 1024 * 1024 or size >= 50 * 1024 * 1024:
                                continue
                            dst = d / 'scratch' / 'outputs' / f.relative_to(out)
                            if not dst.exists():
                                dst.parent.mkdir(parents=True, exist_ok=True)
                                dst.write_bytes(f.read_bytes())
                            total += size
                        except OSError:
                            continue
    # sequence: keep numbers of events already written, append new ones after the current max
    seqmap = dict(rec.get('seqs') or {}) if rec else {}
    nxt = max([db.max_seq(chat_id)] + list(seqmap.values()) + [0]) + 1 if rec else 1
    events = []
    for eid, role, text, ts in items:
        if eid not in seqmap:
            seqmap[eid] = nxt
            nxt += 1
        events.append(ev(chat_id, seqmap[eid], eid, role, text, ts, row['id'], model))
    db.put_events(events)
    rec = {'projectId': pid, 'chatId': chat_id, 'updated': row['updated'], 'full': True, 'seqs': seqmap, 'title': row['title']}
    st['chats'][row['id']] = rec
    return rec, 'imported'


def pointer_line(row, rec):
    last = (row.get('last') or '')[:10]
    where = f"→ чат `{rec['chatId']}`" if rec and rec.get('full') else f"[claude.ai]({claude_url(row['id'], row['kind'])})" if row['kind'] == 'claude' else f"cowork `{row['id']}`"
    first = (row.get('first') or '').replace('|', '/')[:200]
    return f"| {last} | {row['title'].replace('|', '/')} | {row.get('messages')} | {where} | {first} |"


def index_md(title, desc, rows, st, instr=None):
    out = [f'## {title}', '']
    if desc:
        out += [desc.strip(), '']
    if instr:
        out += ['<details><summary>Инструкции проекта claude.ai</summary>', '', instr.strip()[:4000], '', '</details>', '']
    out += ['| Последнее | Чат | Сообщ. | Где | Начало |', '|---|---|---|---|---|']
    for r in sorted(rows, key=lambda r: r.get('last') or '', reverse=True):
        out.append(pointer_line(r, st['chats'].get(r['id'])))
    return '\n'.join(out) + '\n'


INDEX_HEAD = ('# Архив claude.ai и Cowork\n\nПеренесено инструментом `claude-ai-sync` (односторонняя синхронизация claude.ai → AI Workbench). '
              'Свежие чаты перенесены целиком («→ чат CHAT-…»), старые — только указатели. Подтянуть старый чат целиком: '
              '`python "G:\\My Drive\\AI-Workbench\\tools\\claude-ai-sync\\claude-ai-sync.py" pull <id>` (id — из ссылки claude.ai).\n\n')


def cmd_import(a):
    plan = load_plan(a.plan)
    st = jload(CACHE / 'state.json', {}) or {}
    st.setdefault('chats', {}); st.setdefault('hubs', {})
    db = Db()
    claude = None
    if not a.no_files:
        try:
            claude = Claude()
        except KeyExpired:
            claude = None
    rows, projects = rows_all()
    only = set(a.only.split(',')) if a.only else None
    in_only = lambda r: only is None or r['id'] in only or (r.get('project_uuid') or '') in only
    targets = plan.get('targets') or {}
    hubs_spec = plan.get('hubs') or {}
    per_target = {}
    stats = {'imported': 0, 'same': 0, 'pointer': 0, 'skipped': 0, 'failed': 0}
    resolved = []
    for r in rows:
        res = resolve(plan, r)
        if not res:
            stats['skipped'] += 1
            continue
        resolved.append((r, *res))
    # hubs first; grouped claude projects without chats get a hub only when they carry instructions or knowledge
    need = [(r, pid, hub) for r, pid, hub, _ in resolved]
    if True:
        used = {h for _, _, h, _ in resolved}
        for gid, g in (plan.get('groups') or {}).items():
            pj = jload(CACHE / 'projects' / f'{gid}.json', {}) or {}
            if g.get('hub') and gid not in used and (pj.get('docs') or (pj.get('prompt_template') or '').strip()):
                need.append((None, g['target'], gid))
                per_target.setdefault(g['target'], {}).setdefault(gid, [])
    for r, pid, hub in need:
        ensure_project(db, pid, targets.get(pid, {}), st)
        if hub and hub not in st['hubs']:
            p = projects.get(hub) or {}
            spec = hubs_spec.get(hub, {})
            title = spec.get('title') or p.get('name') or hub
            created = p.get('created_at') or now_utc()
            chat_id = make_chat_id('hub:' + hub, created)
            write_chat(db, pid, chat_id, '📁 ' + title, created, now_utc(), None,
                       {'id': hub, 'kind': 'project', 'url': f'https://claude.ai/project/{hub}' if p else '', 'syncedUtc': now_utc()})
            st['hubs'][hub] = {'projectId': pid, 'chatId': chat_id, 'title': title}
    jsave(CACHE / 'state.json', st)
    n = 0
    for r, pid, hub, full in resolved:
        per_target.setdefault(pid, {}).setdefault(hub, []).append(r)
        if not in_only(r):
            continue
        rec = st['chats'].get(r['id'])
        if not (full or (rec and rec.get('full'))):
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
            _, how = import_full(db, claude, st, r, pid, parent, a)
            stats[how] += 1
        except Exception as e:
            stats['failed'] += 1
            log('FAIL', r['id'], r['title'], e)
        n += 1
        if n % 10 == 0:
            jsave(CACHE / 'state.json', st)
            log(f'  {n} chats, {stats}')
    jsave(CACHE / 'state.json', st)
    if a.dry_run:
        print(json.dumps(stats))
        return
    # pointers: hub chat message + context/claude-ai-index.md per target project
    for pid, groups in per_target.items():
        parts = [INDEX_HEAD]
        for hub, rws in sorted(groups.items(), key=lambda x: (x[0] is not None, x[0] or '')):
            p = projects.get(hub) or {}
            title = (st['hubs'].get(hub) or {}).get('title') or p.get('name') or 'Чаты проекта'
            block = index_md(title, p.get('description'), rws, st, p.get('prompt_template'))
            parts.append(block)
            if hub:
                h = st['hubs'][hub]
                docs = [d.get('file_name') for d in (jload(CACHE / 'projects' / f'{hub}.json', {}) or {}).get('docs') or []]
                text = block + ('\n\nФайлы знаний проекта: ' + ', '.join(docs) + ' → scratch/knowledge/' if docs else '')
                db.put_events([ev(h['chatId'], 1, f"EVT-hub-{hashlib.sha1(hub.encode()).hexdigest()[:24]}", 'assistant', text,
                                  p.get('created_at') or now_utc(), hub)])
                kd = project_dir(pid) / 'chats' / h['chatId'] / 'scratch' / 'knowledge'
                for d in (jload(CACHE / 'projects' / f'{hub}.json', {}) or {}).get('docs') or []:
                    f = kd / safe_name(d.get('file_name') or 'doc.txt')
                    if not f.exists() and d.get('content'):
                        f.parent.mkdir(parents=True, exist_ok=True)
                        f.write_text(d['content'], encoding='utf-8')
        flat = groups.get(None) or []
        if flat:  # top-level chats of the project: a visible index chat
            key = 'index:' + pid
            if key not in st['hubs']:
                chat_id = make_chat_id(key, now_utc())
                write_chat(db, pid, chat_id, '📇 Архив claude.ai', now_utc(), now_utc(), None,
                           {'id': key, 'kind': 'index', 'url': '', 'syncedUtc': now_utc()})
                st['hubs'][key] = {'projectId': pid, 'chatId': chat_id, 'title': 'Архив claude.ai'}
            text = INDEX_HEAD + index_md('Чаты проекта', None, flat, st)
            db.put_events([ev(st['hubs'][key]['chatId'], 1, f"EVT-idx-{hashlib.sha1(key.encode()).hexdigest()[:24]}",
                              'assistant', text, now_utc(), key)])
        ctx = project_dir(pid) / 'context'
        ctx.mkdir(parents=True, exist_ok=True)
        (ctx / 'claude-ai-index.md').write_text('\n'.join(parts), encoding='utf-8')
    # flat projects: knowledge docs into files/claude-ai/
    for gid, g in (plan.get('groups') or {}).items():
        if g.get('hub'):
            continue
        pj = jload(CACHE / 'projects' / f'{gid}.json', {}) or {}
        for d in pj.get('docs') or []:
            f = project_dir(g['target']) / 'files' / 'claude-ai' / safe_name(d.get('file_name') or 'doc.txt')
            if not f.exists() and d.get('content'):
                f.parent.mkdir(parents=True, exist_ok=True)
                f.write_text(d['content'], encoding='utf-8')
    # CO Web importer de-duplication index
    idx_f = AIWB / 'imports' / 'index.json'
    idx = jload(idx_f, {}) or {}
    ent = idx.setdefault('entries', {})
    for sid, rec in st['chats'].items():
        if rec.get('full'):
            ent.setdefault(f'claude:{sid}', {'projectId': rec['projectId'], 'chatId': rec['chatId'], 'importedUtc': now_utc()})
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
    a.only = ','.join(a.ids)
    a.force = False
    cmd_import(a)


def push(title, body):
    try:
        import keyring, requests
        key = keyring.get_password('aiwb/co-push', 'default')
        requests.post(SITE_URL + '/agent/push.php', json={'op': 'notify', 'env': OWNER_ENV, 'title': title[:120], 'body': body[:380]},
                      headers={'X-CO-Push-Key': key}, timeout=30)
    except Exception as e:
        log('push failed:', e)


def cmd_pending(a):
    plan = load_plan(a.plan)
    st = jload(CACHE / 'state.json', {}) or {}
    rows, projects = rows_all()
    print(json.dumps(pending(plan, rows, st, lambda u: (projects.get(u) or {}).get('name')), ensure_ascii=False, indent=1))


def cmd_assign(a):
    plan_assign(Path(a.plan) if a.plan else CACHE / 'plan.json', a)


def browser_key():
    """sessionKey of claude.ai from the CO browser (Edge of CO Desktop) over CDP; the value is never printed."""
    spec = importlib.util.spec_from_file_location('co_cookies', Path(__file__).resolve().parent.parent / 'browser-agent' / 'co_cookies.py')
    cob = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(cob)
    c = cob.site_cookies('claude.ai').get(KR_ACCOUNT)
    if not c:
        raise KeyExpired('CO browser is not signed in to claude.ai')
    return c['value'], dt.datetime.fromtimestamp(c['expires'], dt.timezone.utc).strftime('%Y-%m-%d') if c.get('expires', 0) > 0 else 'session'


def refresh_from_browser(force=False):
    """Copy the browser sessionKey into keyring when it differs and works. Returns True when keyring changed."""
    import keyring
    key, exp = browser_key()
    if key == keyring.get_password(KR_SERVICE, KR_ACCOUNT) and not force:
        return False
    Claude(key).org_id()  # raises KeyExpired when the browser key does not work
    keyring.set_password(KR_SERVICE, KR_ACCOUNT, key)
    log(f'sessionKey copied from the CO browser (expires {exp})')
    return True


def cmd_refresh_cookie(a):
    try:
        refresh_from_browser(force=True)
    except KeyExpired as e:
        log('renew from the CO browser failed:', e)
        sys.exit(3)
    except Exception as e:
        log(e)
        sys.exit(1)
    Claude().org_id()
    print('ok')


def cmd_check(a):
    if not getattr(a, 'no_browser', False):
        try:
            refresh_from_browser()
        except Exception as e:
            log('renew from the CO browser failed:', e)
    try:
        c = Claude()
        c.org_id()
        print('ok')
    except KeyExpired as e:
        push('claude.ai sync: session key expired',
             'Не смог взять sessionKey из браузера CO. Войди в claude.ai в браузере CO (оранжевое окно) и запусти claude-ai-sync refresh-cookie, или введи ключ через secret-prompt.')
        log('expired:', e)
        sys.exit(3)


def cmd_sync(a):
    cmd_check(argparse.Namespace())
    if a.stage in ('all', 'fetch'):
        cmd_fetch(argparse.Namespace(force=False, limit=None, delay=0.3))
        cmd_cowork(a)
    if a.stage in ('all', 'import'):
        a.only, a.force, a.dry_run = None, False, False
        cmd_import(a)


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('fetch'); p.add_argument('--force', action='store_true'); p.add_argument('--limit', type=int); p.add_argument('--delay', type=float, default=0.4)
    sub.add_parser('cowork')
    p = sub.add_parser('index'); p.add_argument('--tsv', action='store_true')
    for name in ('import', 'pull', 'sync'):
        p = sub.add_parser(name)
        p.add_argument('--plan', help='plan JSON (default: cache plan.json)')
        p.add_argument('--no-files', action='store_true', help='skip artifacts/files download')
        if name == 'import':
            p.add_argument('--only', help='comma list of chat ids or claude project uuids')
            p.add_argument('--force', action='store_true')
            p.add_argument('--dry-run', action='store_true')
        if name == 'pull':
            p.add_argument('ids', nargs='+', help='claude.ai chat uuid(s) or cowork session id(s) to import in full')
            p.set_defaults(dry_run=False)
        if name == 'sync':
            p.add_argument('--stage', choices=['all', 'fetch', 'import'], default='all',
                           help='fetch: download only (then pending/assign); import: write to AI Workbench only')
    p = sub.add_parser('check'); p.add_argument('--no-browser', action='store_true', help='do not take sessionKey from the CO browser')
    sub.add_parser('refresh-cookie')
    add_triage_parsers(sub)
    a = ap.parse_args()
    try:
        {'fetch': cmd_fetch, 'cowork': cmd_cowork, 'index': cmd_index, 'import': cmd_import, 'pull': cmd_pull,
         'sync': cmd_sync, 'check': cmd_check, 'refresh-cookie': cmd_refresh_cookie,
         'projects': cmd_projects, 'pending': cmd_pending, 'assign': cmd_assign}[a.cmd](a)
    except KeyExpired as e:
        log('claude.ai session key expired or missing:', e)
        sys.exit(3)


if __name__ == '__main__':
    main()
