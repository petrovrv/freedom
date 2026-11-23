"""Mail handler of one person for the home relay (module home-relay): answers the person's Mail page.

The worker home-relay@<slug> on the home server opens the sealed requests of the person's pages and hands the ones for
this module to this handler (`mailcli.py relay-handler`, one long-lived process, JSON lines on stdin/stdout, see
modules/home-relay/home/relayd.py); it seals the answers again. The handler sees plain requests, never keys or the site.

Binding: the handler serves the mailbox of /etc/concierge-mail/<slug>.env only when that mailbox belongs to the
environment the worker serves (AIWB_ENV of the mailbox == HOME_RELAY_ENV); otherwise every call gets `not_bound`.

Read only for now: the API reads the store (overview, list, thread, message, attachment, search) and changes nothing,
neither in the store nor at the provider. `images` fetches the remote pictures of one stored message on the person's
click (home/remoteimg.py); the page itself never loads anything from the internet.
"""
from __future__ import annotations

import base64
import json
import os
import re
import sqlite3
import sys
from email import policy
from email.parser import BytesParser

import remoteimg

PAGE = 50
INLINE_MAX = 1024 * 1024          # cid: images up to 1 MB go inline as data: URIs
ATTACHMENT_MAX = 4 * 1024 * 1024  # base64 twice (JSON, then the sealed answer) must stay under the site's 8 MB; bigger: provider's client
THREAD_MAX = 100                    # newest messages of a thread
HTML_MAX = 1536 * 1024              # characters of one message's html; longer: text only, marked truncated
SYSTEM_LABELS = ['INBOX', 'STARRED', 'IMPORTANT', 'SENT', 'DRAFT', 'SPAM', 'TRASH']


def b64e(data):
    return base64.b64encode(data).decode()


def _labels_of(text):
    try:
        v = json.loads(text or '[]')
        return [str(x) for x in v] if isinstance(v, list) else []
    except ValueError:
        return []


def _snippet(body, html):
    text = body or re.sub(r'<[^>]*>', ' ', html or '')
    return re.sub(r'\s+', ' ', text).strip()[:160]


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

    def __init__(self, store, account):
        self.store, self.db, self.account = store, store.db, account
        self.db.row_factory = sqlite3.Row

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
            items.append({'thread': r['thread_id'], 'date': r['d'], 'count': r['n'], 'unread': int(r['u'] or 0),
                          'labels': labels, 'subject': '' if last['sensitive'] else last['subject'],
                          'from': senders, 'snippet': '' if last['sensitive'] else _snippet(last['body'], last['html']),
                          'attachments': int(last['att']), 'sensitive': bool(last['sensitive']), 'tainted': bool(last['tainted'])})
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
        if action not in ('overview', 'list', 'search', 'thread', 'attachment', 'images') or not isinstance(args, dict):
            raise ValueError('unknown action')
        return getattr(self, action)(args)


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
    for line in iter(stdin.readline, b''):
        out = handle_line(api, bound, line)
        stdout.write(json.dumps(out, ensure_ascii=False, separators=(',', ':')).encode() + b'\n')
        stdout.flush()
