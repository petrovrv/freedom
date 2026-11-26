"""Mail handler of one person for the home relay (module home-relay): answers the person's Mail page.

The worker home-relay@<slug> on the home server opens the sealed requests of the person's pages and hands the ones for
this module to this handler (`mailcli.py relay-handler`, one long-lived process, JSON lines on stdin/stdout, see
modules/home-relay/home/relayd.py); it seals the answers again. The handler sees plain requests, never keys or the site.

Binding: the handler serves the mailbox of /etc/concierge-mail/<slug>.env only when that mailbox belongs to the
environment the worker serves (AIWB_ENV of the mailbox == HOME_RELAY_ENV); otherwise every call gets `not_bound`.

The API reads the store (overview, list, thread, message, attachment, search); writing to the provider is the module
compose (on the person's click). 0.12.0: the only thing the handler keeps itself is the person's links of threads to
CO chats (table chat_links: thread -> chat id, a copy of the chat's title and project for display; no message content
leaves the store), so a chat's correspondence lives here and is not copied into the chat. `images` fetches the remote pictures of one stored message on the person's
click (home/remoteimg.py); the page itself never loads anything from the internet.
"""
from __future__ import annotations

import base64
import html as htmllib
import json
import os
import re
import sqlite3
import sys
import threading
from email import policy
from email.parser import BytesParser

import compose
import gmailsync
import remoteimg

PAGE = 50
INLINE_MAX = 1024 * 1024          # cid: images up to 1 MB go inline as data: URIs
ATTACHMENT_MAX = 4 * 1024 * 1024  # base64 twice (JSON, then the sealed answer) must stay under the site's 8 MB; bigger: provider's client
THREAD_MAX = 100                    # newest messages of a thread
HTML_MAX = 1536 * 1024              # characters of one message's html; longer: text only, marked truncated
SYSTEM_LABELS = ['INBOX', 'STARRED', 'IMPORTANT', 'SENT', 'DRAFT', 'SPAM', 'TRASH']
THREAD_ID = re.compile(r'[A-Za-z0-9_-]{1,64}')
CHAT_ID = re.compile(r'CHAT-\d{14}-[A-Z0-9]{4}')
PROJECT_ID = re.compile(r'[A-Za-z0-9_.-]{1,64}')
CHATS_PER_THREAD = 20               # links of one thread
TITLE_MAX = 200


def ensure_links(db):
    """The person's links of mail threads to CO chats (0.12.0)."""
    db.execute('CREATE TABLE IF NOT EXISTS chat_links(account TEXT NOT NULL, thread_id TEXT NOT NULL, chat_id TEXT NOT NULL,'
               ' chat_title TEXT NOT NULL, project_id TEXT NOT NULL, linked TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,'
               ' PRIMARY KEY(account, thread_id, chat_id))')
    db.execute('CREATE INDEX IF NOT EXISTS chat_links_chat ON chat_links(account, chat_id)')
    db.commit()


def _chat_title(value):
    t = re.sub(r'\s+', ' ', str(value or '')).strip()[:TITLE_MAX]
    return t or '(untitled)'


def b64e(data):
    return base64.b64encode(data).decode()


def _labels_of(text):
    try:
        v = json.loads(text or '[]')
        return [str(x) for x in v] if isinstance(v, list) else []
    except ValueError:
        return []


_HIDDEN = re.compile('[\u00ad\u034f\u180e\u200b\u200e\u200f\u2060-\u2064\u2800\ufeff]')  # preheader padding of newsletters
_JOINERS = re.compile('[\u200c\u200d]+')
_SEL_TOKEN = re.compile(r'[\w.#*:>+~\[\]=\'"()%^$|,-]{1,200}')
_TAGS = r'(a|p|b|i|u|td|th|tr|table|tbody|body|html|div|span|img|li|ul|ol|h[1-6]|font|center|strong|em)'
_SEL_MORE = re.compile(r'[.#*\[>+~,]|[\w-]*[.#\[:][\w-]|' + _TAGS + r',?$')  # selector parts before the last one
_SEL_CSS = re.compile(r'[.\[@*]|#[a-zA-Z_-]|' + _TAGS + r'\b')                     # a selector that is surely CSS (`#12345` is not)
_DECL = re.compile(r'[a-zA-Z-]{1,60}\s*:[^;{}]{0,200};')
_BLOCK_MAX = 4000    # characters inside one style block that are examined
_CSS_BUDGET = 400000  # characters examined per snippet (every block costs at least 400): the work stays linear


def _drop_joiners(text):
    """Zero-width joiners of newsletter padding (next to a space or an edge); inside a word (emoji, scripts) they stay."""
    def keep(m):
        a, b = m.start(), m.end()
        inside = a > 0 and b < len(text) and not text[a - 1].isspace() and not text[b].isspace()
        return m.group(0) if inside else ''
    return _JOINERS.sub(keep, text)


def _selector_start(out, end):
    """Index in `out` where the selector of a style block whose `{` is out[end] starts (looks back 400 characters)."""
    head = ''.join(out[max(0, end - 400):end])
    base = end - len(head)
    at = head.rfind('@')
    if at >= 0 and not re.search(r'[;{}]', head[at:]):  # @media screen and (min-width: 480px)
        return base + at
    start = end
    for n, m in enumerate(reversed(list(re.finditer(r'\S+', head)))):
        t = m.group(0)
        if not _SEL_TOKEN.fullmatch(t) or (n and not _SEL_MORE.match(t)):
            break  # `Deal 96 *{` keeps `Deal 96`; `.x td`, `#outlook a`, `body, td` go
        start = base + m.start()
    return start


def _drop_style_blocks(text):
    """Remove `selector { declarations }` and the @media blocks around them in one pass; other braces stay.

    A block goes when it holds `prop: value;`, or holds one declaration without `;` / nothing at all after a selector
    that is surely CSS (`.x`, `#id`, `[a]`, `@media`, `*`, a tag name): `code {id: 5}`, `Hello {} world` stay."""
    out, opened, budget = [], [], _CSS_BUDGET
    for ch in text.replace('\0', ' '):
        if ch == '{':
            opened.append(len(out))
        elif ch == '}' and opened:
            at = opened.pop()
            size = len(out) - at - 1
            budget -= max(400, min(size, _BLOCK_MAX))
            if size <= _BLOCK_MAX and budget > 0:
                inner = ''.join(out[at + 1:])
                start = _selector_start(out, at)
                sure = start < at and _SEL_CSS.match(''.join(out[start:at]))
                if '\0' in inner or (';' in inner and _DECL.search(inner)) or (
                        sure and (not inner.strip() or _DECL.fullmatch(inner.strip() + ';'))):
                    del out[start:]
                    out.append('\0' if opened else ' ')  # NUL: the enclosing block held a style block, it goes too
                    continue
        out.append(ch)
    return ''.join(out).replace('\0', ' ')


def _snippet(body, html):
    """First words of a message for the list: no head, style sheets, comments, tags, entities or hidden padding."""
    text, from_html = body or '', False
    if not text.strip() or re.search(r'(?i)<(style|html|head|body|table|div)\b', text[:200000]):  # plain part that is really html
        text, from_html = (html or text), True
    text = text[:200000]
    if from_html:
        text = re.sub(r'(?is)<!--.*?(-->|$)|<(style|script|title|xml)\b.*?(</\2\s*>|$)'
                      r'|<head\b.*?(</head\s*>|(?=<(body|p|div|table|span|a|img|h[1-6]|center|font)\b)|$)', ' ', text)
        text = htmllib.unescape(re.sub(r'<[^<>]*>', ' ', text))
    else:  # entities some senders put into the text part; only complete ones (`?a=1&copy=2` stays)
        text = re.sub(r'&(#\d{1,7}|#[xX][0-9a-fA-F]{1,6}|[a-zA-Z]{2,8});', lambda m: htmllib.unescape(m.group(0)), text)
    if '{' in text:
        text = _drop_comments(text)
        text = re.sub(r'@(import|charset)\s++(url\([^()\s{}]{0,2000}\))?+[^;{}<>\n]{0,500}+;', ' ', text)  # possessive: no backtracking
        text = _drop_style_blocks(text)
    return re.sub(r'\s+', ' ', _drop_joiners(_HIDDEN.sub('', text))).strip()[:160]


def _drop_comments(text):
    """CSS comments /* ... */ (an unclosed one stays)."""
    parts, i = [], 0
    while True:
        a = text.find('/*', i)
        b = text.find('*/', a + 2) if a >= 0 else -1
        if b < 0:
            parts.append(text[i:])
            return ' '.join(parts)
        parts.append(text[i:a])
        i = b + 2


def _clean_html(html, inline):
    """Remove active content (the page renders the rest under its CSP: no scripts, no remote images); cid: -> data:."""
    html = re.sub(r'(?is)<(script|iframe|object|embed|frame|frameset|applet|meta|base|link|form)\b.*?(</\1\s*>|/?>)', '', html)
    html = re.sub(r'(?i)\son[a-z]+\s*=\s*("[^"]*"|\'[^\']*\'|[^\s>]+)', '', html)
    html = re.sub(r'(?i)(href|src)\s*=\s*(["\']?)\s*(javascript|vbscript):[^"\'>\s]*\2', r'\1=\2#\2', html)

    def cid(m):
        return m.group(1) + inline.get(m.group(2).strip('<>').lower(), '') + m.group(3)
    return re.sub(r'(?i)(src\s*=\s*["\'])cid:([^"\']+)(["\'])', cid, html)


class MailApi:
    """Read-only queries of one person's store for the Mail page."""

    def __init__(self, store, account, composer=None):
        self.store, self.db, self.account = store, store.db, account
        self.db.row_factory = sqlite3.Row
        self.composer = composer  # writing (module compose): only in the handler of a bound mailbox
        ensure_links(self.db)

    def unread(self):
        row = self.db.execute("SELECT COUNT(DISTINCT thread_id) FROM gmail_messages WHERE account=? AND gone=0"
                              " AND labels LIKE '%\"INBOX\"%' AND labels LIKE '%\"UNREAD\"%'", (self.account,)).fetchone()
        return int(row[0] or 0)

    def synced(self):
        try:
            row = self.db.execute('SELECT last_sync FROM gmail_state WHERE account=?', (self.account,)).fetchone()
        except sqlite3.OperationalError:
            return ''
        return (row and row[0]) or ''

    def overview(self, args):
        try:
            names = {r['label_id']: (r['name'], r['type']) for r in
                     self.db.execute('SELECT label_id,name,type FROM gmail_labels WHERE account=?', (self.account,))}
        except sqlite3.OperationalError:
            names = {}
        counts = {}
        for r in self.db.execute('SELECT labels FROM gmail_messages WHERE account=? AND gone=0', (self.account,)):
            labels = _labels_of(r['labels'])
            unread = 'UNREAD' in labels
            for l in labels:
                c = counts.setdefault(l, [0, 0])
                c[0] += 1
                c[1] += unread
        out = [{'id': l, 'name': l.title(), 'system': True, 'total': counts.get(l, [0, 0])[0], 'unread': counts.get(l, [0, 0])[1]}
               for l in SYSTEM_LABELS]
        for lid, (name, kind) in sorted(names.items(), key=lambda kv: kv[1][0].lower()):
            if kind == 'user':
                out.append({'id': lid, 'name': name, 'system': False, 'total': counts.get(lid, [0, 0])[0],
                            'unread': counts.get(lid, [0, 0])[1]})
        return {'account': self.account, 'labels': out, 'unread': self.unread(), 'synced': self.synced()}

    def _threads(self, where, params, before, limit):
        limit = max(1, min(int(limit or PAGE), 100))
        # cursor "<date>:<thread>" (opaque to the page): threads with the same date are not skipped between pages
        having, hp = '', []
        if before:
            m = re.fullmatch(r'(\d{1,15})(?::([A-Za-z0-9_-]{1,64}))?', str(before))
            if not m:
                raise ValueError('bad cursor')
            having, hp = ('HAVING d < ? OR (d = ? AND thread_id < ?)', [int(m[1]), int(m[1]), m[2]]) if m[2] else ('HAVING d < ?', [int(m[1])])
        rows = self.db.execute(
            f'SELECT thread_id, MAX(internal_date) d, COUNT(*) n, SUM(labels LIKE \'%"UNREAD"%\') u, GROUP_CONCAT(labels, \'\x1f\') ls'
            f' FROM gmail_messages WHERE account=? AND gone=0 {where} GROUP BY thread_id {having} ORDER BY d DESC, thread_id DESC LIMIT ?',
            [self.account, *params, *hp, limit]).fetchall()
        items = []
        for r in rows:
            last = self.db.execute(
                'SELECT m.id, m.subject, m.sender, m.recipients, m.date, m.body, m.html, m.sensitive, m.tainted,'
                ' (SELECT COUNT(*) FROM attachments a WHERE a.message_id=m.id) att'
                ' FROM gmail_messages g JOIN messages m ON m.id=g.message_id WHERE g.account=? AND g.thread_id=? AND g.gone=0'
                ' ORDER BY g.internal_date DESC LIMIT 1', (self.account, r['thread_id'])).fetchone()
            senders = [s['sender'] for s in self.db.execute(
                'SELECT DISTINCT m.sender FROM gmail_messages g JOIN messages m ON m.id=g.message_id'
                ' WHERE g.account=? AND g.thread_id=? AND g.gone=0 ORDER BY g.internal_date LIMIT 4', (self.account, r['thread_id']))]
            labels = sorted({l for part in (r['ls'] or '').split('\x1f') for l in _labels_of(part)})
            # read-guard flag of any message in the thread, not only the newest one
            tainted = self.db.execute(
                'SELECT 1 FROM gmail_messages g JOIN messages m ON m.id=g.message_id'
                ' WHERE g.account=? AND g.thread_id=? AND g.gone=0 AND m.tainted LIMIT 1', (self.account, r['thread_id'])).fetchone()
            chats = [{'chat': c['chat_id'], 'title': c['chat_title'], 'project': c['project_id']} for c in self.db.execute(
                'SELECT chat_id, chat_title, project_id FROM chat_links WHERE account=? AND thread_id=? ORDER BY linked LIMIT 3',
                (self.account, r['thread_id']))]
            items.append({'thread': r['thread_id'], 'date': r['d'], 'count': r['n'], 'unread': int(r['u'] or 0), 'chats': chats,
                          'labels': labels, 'subject': '' if last['sensitive'] else last['subject'],
                          'from': senders, 'snippet': '' if last['sensitive'] else _snippet(last['body'], last['html']),
                          'attachments': int(last['att']), 'sensitive': bool(last['sensitive']), 'tainted': tainted is not None})
        return {'threads': items, 'next': f"{items[-1]['date']}:{items[-1]['thread']}" if len(items) == limit else None}

    def list(self, args):
        label = str(args.get('label') or 'INBOX')
        if label == 'ALL':
            return self._threads("AND labels NOT LIKE '%\"SPAM\"%' AND labels NOT LIKE '%\"TRASH\"%'", [], args.get('before'), args.get('limit'))
        if not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', label):
            raise ValueError('bad label')
        return self._threads('AND labels LIKE ?', ['%"' + label + '"%'], args.get('before'), args.get('limit'))

    def search(self, args):
        words = re.findall(r'\w+', str(args.get('q') or ''), re.UNICODE)[:12]
        if not words:
            return {'threads': [], 'next': None}
        match = ' AND '.join('"' + w + '"' for w in words)
        return self._threads('AND message_id IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH ?)', [match],
                             args.get('before'), args.get('limit'))

    # ---------- 0.12.0: correspondence of chats ----------

    def _thread_id(self, args):
        tid = str(args.get('thread') or '')
        if not THREAD_ID.fullmatch(tid):
            raise ValueError('bad thread')
        if not self.db.execute('SELECT 1 FROM gmail_messages WHERE account=? AND thread_id=? AND gone=0 LIMIT 1',
                               (self.account, tid)).fetchone():
            raise LookupError('no thread')
        return tid

    @staticmethod
    def _chat_id(args):
        cid = str(args.get('chat') or '')
        if not CHAT_ID.fullmatch(cid):
            raise ValueError('bad chat')
        return cid

    def links(self, args):
        """The chats a thread belongs to."""
        tid = self._thread_id(args)
        rows = self.db.execute('SELECT chat_id, chat_title, project_id, linked FROM chat_links WHERE account=? AND thread_id=?'
                               ' ORDER BY linked', (self.account, tid))
        return {'thread': tid, 'chats': [{'chat': r['chat_id'], 'title': r['chat_title'], 'project': r['project_id'],
                                          'linked': r['linked']} for r in rows]}

    def link(self, args):
        """The person puts a thread into a chat (again: the chat's title and project are refreshed)."""
        tid, cid = self._thread_id(args), self._chat_id(args)
        project = str(args.get('project') or '')
        if not PROJECT_ID.fullmatch(project):
            raise ValueError('bad project')
        known = self.db.execute('SELECT 1 FROM chat_links WHERE account=? AND thread_id=? AND chat_id=?',
                                (self.account, tid, cid)).fetchone()
        if not known and self.db.execute('SELECT COUNT(*) FROM chat_links WHERE account=? AND thread_id=?',
                                         (self.account, tid)).fetchone()[0] >= CHATS_PER_THREAD:
            raise ValueError('too many chats')
        self.db.execute('INSERT INTO chat_links(account, thread_id, chat_id, chat_title, project_id) VALUES(?,?,?,?,?)'
                        ' ON CONFLICT(account, thread_id, chat_id) DO UPDATE SET chat_title=excluded.chat_title,'
                        ' project_id=excluded.project_id', (self.account, tid, cid, _chat_title(args.get('title')), project))
        # every thread of the chat shows the chat's newest title (a renamed chat)
        self.db.execute('UPDATE chat_links SET chat_title=?, project_id=? WHERE account=? AND chat_id=?',
                        (_chat_title(args.get('title')), project, self.account, cid))
        self.db.commit()
        return self.links({'thread': tid})

    def unlink(self, args):
        tid, cid = self._thread_id(args), self._chat_id(args)
        self.db.execute('DELETE FROM chat_links WHERE account=? AND thread_id=? AND chat_id=?', (self.account, tid, cid))
        self.db.commit()
        return self.links({'thread': tid})

    def chats(self, args):
        """Chats that have correspondence here, newest mail first, with their unread threads."""
        rows = self.db.execute(
            "SELECT l.chat_id, MAX(l.chat_title) title, MAX(l.project_id) project, COUNT(DISTINCT l.thread_id) n,"
            " COUNT(DISTINCT CASE WHEN g.labels LIKE '%\"UNREAD\"%' THEN g.thread_id END) u, MAX(g.internal_date) d"
            " FROM chat_links l LEFT JOIN gmail_messages g ON g.account=l.account AND g.thread_id=l.thread_id AND g.gone=0"
            " WHERE l.account=? GROUP BY l.chat_id ORDER BY d DESC LIMIT 5000", (self.account,))
        return {'chats': [{'chat': r['chat_id'], 'title': r['title'], 'project': r['project'], 'threads': r['n'],
                           'unread': int(r['u'] or 0), 'date': r['d'] or 0} for r in rows]}

    def chat_threads(self, args):
        """The correspondence of one chat: its threads, newest first (paged like a folder)."""
        cid = self._chat_id(args)
        return self._threads('AND thread_id IN (SELECT thread_id FROM chat_links WHERE account=? AND chat_id=?)',
                             [self.account, cid], args.get('before'), args.get('limit'))

    def _inline(self, raw):
        out = {}
        try:
            mail = BytesParser(policy=policy.default).parsebytes(raw)
        except Exception:
            return out
        for part in mail.walk():
            cid = (part.get('Content-ID') or '').strip().strip('<>').lower()
            if cid and part.get_content_maintype() == 'image':
                data = part.get_payload(decode=True) or b''
                if 0 < len(data) <= INLINE_MAX:
                    out[cid] = f'data:{part.get_content_type()};base64,' + b64e(data)
        return out

    def thread(self, args):
        tid = str(args.get('thread') or '')
        if not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', tid):
            raise ValueError('bad thread')
        msgs = []
        for r in self.db.execute(
                'SELECT g.gmail_id, g.labels, g.internal_date, m.* FROM gmail_messages g JOIN messages m ON m.id=g.message_id'
                ' WHERE g.account=? AND g.thread_id=? AND g.gone=0 ORDER BY g.internal_date DESC LIMIT ?', (self.account, tid, THREAD_MAX + 1)):
            msgs.append(self._message(r))
        more = len(msgs) > THREAD_MAX
        return {'thread': tid, 'messages': list(reversed(msgs[:THREAD_MAX])), 'older': more}

    def _message(self, r):
        base = {'id': r['gmail_id'], 'date': r['internal_date'], 'labels': _labels_of(r['labels']), 'from': r['sender'],
                'sensitive': bool(r['sensitive']), 'tainted': bool(r['tainted'])}
        if r['sensitive']:
            # a sign-in code, link or password reset: only who and when (section 11); the provider keeps the original
            return {**base, 'subject': '', 'to': '', 'text': '', 'html': '', 'attachments': [], 'reasons': []}
        atts = [{'sha': a['sha256'], 'name': a['filename'], 'type': a['content_type'], 'size': a['size']}
                for a in self.db.execute('SELECT sha256,filename,content_type,size FROM attachments WHERE message_id=?', (r['id'],))]
        html = r['html'] or ''
        if html:
            try:
                html = _clean_html(html, self._inline(self.store.read_blob(r['raw_sha256'])) if 'cid:' in html.lower() else {})
            except Exception:
                html = _clean_html(html, {})
        truncated = len(html) > HTML_MAX
        if truncated:
            html = ''
        return {**base, 'subject': r['subject'], 'to': r['recipients'], 'text': r['body'][:HTML_MAX], 'html': html, 'truncated': truncated,
                'attachments': atts, 'reasons': _labels_of(r['taint_reasons'])}

    def attachment(self, args):
        sha = str(args.get('sha') or '')
        if not re.fullmatch(r'[0-9a-f]{64}', sha):
            raise ValueError('bad attachment')
        row = self.db.execute(
            'SELECT a.filename, a.content_type, a.size FROM attachments a JOIN messages m ON m.id=a.message_id'
            ' WHERE a.sha256=? AND m.account=? AND m.sensitive=0 LIMIT 1', (sha, self.account)).fetchone()
        if not row:
            raise LookupError('no attachment')
        if row['size'] > ATTACHMENT_MAX:
            return {'name': row['filename'], 'type': row['content_type'], 'size': row['size'], 'tooBig': True}
        return {'name': row['filename'], 'type': row['content_type'], 'size': row['size'], 'data': b64e(self.store.read_blob(sha))}

    def images(self, args, fetch_all=None):
        """Remote images of one message, fetched here on the person's click (module remoteimg)."""
        gid = str(args.get('id') or '')
        if not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', gid):
            raise ValueError('bad message')
        row = self.db.execute('SELECT m.html, m.sensitive FROM gmail_messages g JOIN messages m ON m.id=g.message_id'
                              ' WHERE g.account=? AND g.gmail_id=? AND g.gone=0', (self.account, gid)).fetchone()
        if not row or row['sensitive']:
            raise LookupError('no message')
        cache = getattr(self.store, 'root', None)
        images, skipped = (fetch_all or remoteimg.fetch_all)(remoteimg.image_urls(row['html']),
                                                             cache=cache / 'imgcache' if cache else None)
        return {'id': gid, 'images': images, 'skipped': skipped}

    def handle(self, request):
        action = request.get('action') if isinstance(request, dict) else None
        args = request.get('args') or {} if isinstance(request, dict) else {}
        if not isinstance(args, dict):
            raise ValueError('unknown action')
        if action in compose.ACTIONS and self.composer is not None:
            return self.composer.handle(action, args)
        if action not in ('overview', 'list', 'search', 'thread', 'attachment', 'images', 'links', 'link', 'unlink', 'chats',
                          'chat_threads'):
            raise ValueError('unknown action')
        return getattr(self, action)(args)


TEXT_MAX = 8000  # characters of one message for an agent


def chat_mail(api, chat, thread=None, limit=20):
    """mailcli chat-mail: an agent of a chat reads that chat's correspondence (text only, no html, codes stay stubs)."""
    if not thread:
        listed = api.chat_threads({'chat': chat, 'limit': max(1, min(int(limit or 20), 100))})
        return {'chat': chat, 'threads': [{k: t[k] for k in ('thread', 'date', 'count', 'unread', 'subject', 'from', 'snippet',
                                                            'attachments', 'sensitive', 'tainted')} for t in listed['threads']]}
    cid = api._chat_id({'chat': chat})
    tid = api._thread_id({'thread': thread})
    if not api.db.execute('SELECT 1 FROM chat_links WHERE account=? AND thread_id=? AND chat_id=?', (api.account, tid, cid)).fetchone():
        raise LookupError('thread not in this chat')
    got = api.thread({'thread': tid})
    msgs = got['messages'][-max(1, min(int(limit or 20), THREAD_MAX)):]
    return {'chat': cid, 'thread': tid, 'older': got['older'] or len(msgs) < len(got['messages']),
            'messages': [{'id': m['id'], 'date': m['date'], 'from': m['from'], 'to': m['to'], 'subject': m['subject'],
                          'text': (m['text'] or '')[:TEXT_MAX], 'attachments': [a['name'] for a in m['attachments']],
                          'sensitive': m['sensitive'], 'tainted': m['tainted'], 'reasons': m['reasons']} for m in msgs]}


class LazyToken:
    """The write token of the person's account, made on first use (and again after the person gave consent)."""

    def __init__(self, make):
        self._make, self._source = make, None

    def __call__(self):
        if self._source is None:
            self._source = self._make()  # PermissionError while mail.compose / mail.modify are not granted
        return self._source()

    def invalidate(self, token):
        if self._source is not None:
            self._source.invalidate(token)


def write_token(environ):
    """LazyToken for the Gmail writes, or None when the handler was started without the account credential."""
    directory = environ.get('CREDENTIALS_DIRECTORY')
    if not directory or not os.path.isfile(os.path.join(directory, 'account')):
        return None
    path = os.path.join(directory, 'account')
    return LazyToken(lambda: gmailsync.TokenSource(path, capabilities=compose.CAPABILITIES))


def handle_line(api, bound, line):
    """One JSON line of the relay worker -> one answer dict."""
    try:
        req = json.loads(line)
    except ValueError:
        return {'ok': False, 'error': 'bad_request'}
    op = req.get('op') if isinstance(req, dict) else None
    if op == 'status':
        if not bound:
            return {'status': {'bound': False}}
        return {'status': {'bound': True, 'unread': api.unread(), 'synced': api.synced(), 'account': api.account}}
    if op != 'call':
        return {'ok': False, 'error': 'bad_request'}
    if not bound:
        return {'ok': False, 'error': 'not_bound'}
    try:
        return {'ok': True, 'result': api.handle({'action': req.get('action'), 'args': req.get('args') or {}})}
    except (ValueError, LookupError) as exc:
        return {'ok': False, 'error': str(exc)[:100]}
    except Exception:
        return {'ok': False, 'error': 'worker_error'}


def handler_main(store, stdin=None, stdout=None, environ=None):
    """Entry of `mailcli.py relay-handler` (started by the home-relay worker with the mailbox's env file)."""
    environ = os.environ if environ is None else environ
    stdin, stdout = stdin or sys.stdin.buffer, stdout or sys.stdout.buffer
    account = environ.get('ACCOUNT', '').strip().lower()
    bound = bool(account) and bool(environ.get('AIWB_ENV')) and environ.get('AIWB_ENV') == environ.get('HOME_RELAY_ENV')
    if not bound:
        print('relay-handler: this mailbox does not belong to the environment of the worker', file=sys.stderr, flush=True)
    store.db.execute('PRAGMA busy_timeout=10000')
    remoteimg.prune(store.root / 'imgcache')
    api = MailApi(store, account)
    stop, sender = threading.Event(), None
    if bound:
        token = write_token(environ)
        api.composer = compose.Composer(store, account, token, gmailsync._http if token else None)
        sender = threading.Thread(target=api.composer.run_sender, args=(store.root / 'mail.sqlite3', stop), daemon=True)
        sender.start()
    for line in iter(stdin.readline, b''):
        out = handle_line(api, bound, line)
        stdout.write(json.dumps(out, ensure_ascii=False, separators=(',', ':')).encode() + b'\n')
        stdout.flush()
    if sender:  # the worker closed our stdin: let the outbox thread close its database
        stop.set()
        api.composer._wake.set()
        sender.join(5)
