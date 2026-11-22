"""Mail worker of one person on the home server (stage 2): answers the person's Mail page through the site.

The site is only the postman (server/ops.php): the worker long-polls its route `worker` with its own token, takes the
sealed requests of its environment, answers them from the person's store and hands the sealed answers back.

Envelope (ui/relay.js is the other side): ECDH P-256 between the page's session key (`pk`, raw uncompressed point) and
the worker's key (relay-key.pem in the person's directory, its public half published in the status); per request
HKDF-SHA256(shared, salt = "<env>|<id>", info = "freedom-mail-relay/1 req|res") -> AES-256-GCM key, 12-byte nonce,
AAD = id. A request the worker cannot open gets the plain error `bad_request`; content never leaves in clear.

Read only for now: the API reads the store (overview, list, thread, message, attachment, search) and changes nothing,
neither in the store nor at the provider.
"""
from __future__ import annotations

import base64
import json
import os
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.request
from email import policy
from email.parser import BytesParser
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

INFO = b'freedom-mail-relay/1 '
ID_RE = re.compile(r'^[a-f0-9]{32}$')
PAGE = 50
INLINE_MAX = 1024 * 1024          # cid: images up to 1 MB go inline as data: URIs
ATTACHMENT_MAX = 4 * 1024 * 1024  # base64 twice (JSON, then the sealed answer) must stay under the site's 8 MB; bigger: provider's client
REPLY_MAX = 8 * 1024 * 1024 - 4096   # ciphertext (base64) of one answer: the site's CO_MAIL_RES_MAX
BATCH_MAX = 12 * 1024 * 1024        # answers per worker call: under the site's 16 MB body limit
THREAD_MAX = 100                    # newest messages of a thread
HTML_MAX = 1536 * 1024              # characters of one message's html; longer: text only, marked truncated
FRESH = 120                         # seconds a sealed request stays valid (clock of the page vs the worker)
SYSTEM_LABELS = ['INBOX', 'STARRED', 'IMPORTANT', 'SENT', 'DRAFT', 'SPAM', 'TRASH']


def b64e(data):
    return base64.b64encode(data).decode()


def b64d(text, limit=None):
    if not isinstance(text, str) or (limit and len(text) > limit):
        raise ValueError('bad base64')
    return base64.b64decode(text, validate=True)


class Keys:
    """The worker's static P-256 key, created on first start (0600 in the person's directory)."""

    def __init__(self, path):
        path = Path(path)
        if path.is_file():
            self.private = serialization.load_pem_private_key(path.read_bytes(), None)
        else:
            self.private = ec.generate_private_key(ec.SECP256R1())
            pem = self.private.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                             serialization.NoEncryption())
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, 'wb') as fh:
                fh.write(pem)
        self.public = b64e(self.private.public_key().public_bytes(serialization.Encoding.X962,
                                                                  serialization.PublicFormat.UncompressedPoint))

    def _key(self, env, rid, page_pub, direction):
        peer = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), page_pub)
        shared = self.private.exchange(ec.ECDH(), peer)
        return HKDF(hashes.SHA256(), 32, f'{env}|{rid}'.encode(), INFO + direction).derive(shared)

    def open(self, env, req):
        rid = req.get('id', '')
        if not ID_RE.match(rid):
            raise ValueError('bad id')
        pub = b64d(req.get('pk'), 200)
        plain = AESGCM(self._key(env, rid, pub, b'req')).decrypt(b64d(req.get('iv'), 40), b64d(req.get('ct')), rid.encode())
        return pub, json.loads(plain)

    def seal(self, env, rid, page_pub, value):
        iv = os.urandom(12)
        data = json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode()
        return {'id': rid, 'iv': b64e(iv), 'ct': b64e(AESGCM(self._key(env, rid, page_pub, b'res')).encrypt(iv, data, rid.encode()))}


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

    def handle(self, request):
        action = request.get('action') if isinstance(request, dict) else None
        args = request.get('args') or {} if isinstance(request, dict) else {}
        if action not in ('overview', 'list', 'search', 'thread', 'attachment') or not isinstance(args, dict):
            raise ValueError('unknown action')
        return getattr(self, action)(args)


class Seen:
    """Ids of requests already answered (kept for twice the freshness window): a copied request is not answered again."""

    def __init__(self):
        self.ids = {}

    def first(self, rid, now):
        for k in [k for k, t in self.ids.items() if t < now - 2 * FRESH]:
            del self.ids[k]
        if rid in self.ids:
            return False
        self.ids[rid] = now
        return True


def answer(keys, api, env, req, seen=None, now=None):
    """One sealed request -> one sealed answer (or a plain error code without content)."""
    rid = str(req.get('id') or '')
    now = time.time() if now is None else now
    try:
        page_pub, request = keys.open(env, req)
    except Exception:
        return {'id': rid, 'error': 'bad_request'}
    # the time is inside the sealed part: the site can neither refresh an old request nor replay a fresh one twice
    ts = request.get('ts') if isinstance(request, dict) else None
    if not isinstance(ts, (int, float)) or abs(now - ts / 1000) > FRESH:
        return {'id': rid, 'error': 'expired'}
    if seen is not None and not seen.first(rid, now):
        return {'id': rid, 'error': 'replay'}
    try:
        result = {'ok': True, 'result': api.handle(request)}
    except (ValueError, LookupError) as exc:
        result = {'ok': False, 'error': str(exc)[:100]}
    except Exception:
        result = {'ok': False, 'error': 'worker_error'}
    sealed = keys.seal(env, rid, page_pub, result)
    return sealed if len(sealed['ct']) <= REPLY_MAX else {'id': rid, 'error': 'too_large'}


def batches(replies):
    """Split answers so that one worker call stays under the site's body limit."""
    out, cur, size = [], [], 0
    for r in replies:
        n = len(r.get('ct', '')) + 200
        if cur and size + n > BATCH_MAX:
            out.append(cur)
            cur, size = [], 0
        cur.append(r)
        size += n
    if cur:
        out.append(cur)
    return out


def serve(store, root, env, account, url, token, request=None, stop=None, log=None):
    """Long-poll loop; returns when `stop()` is true (tests) or never."""
    keys = Keys(Path(root) / 'relay-key.pem')
    api = MailApi(store, account)
    post = request or _post
    seen, waiting, delay = Seen(), [], 1
    while not (stop and stop()):
        status = {'pub': keys.public, 'unread': api.unread(), 'synced': api.synced(), 'account': account}
        parts = batches(waiting) or [[]]
        batch, rest = parts[0], [r for p in parts[1:] for r in p]
        payload = {'env': env, 'token': token, 'status': status, 'replies': batch}
        if rest:
            payload['nowait'] = 1  # more answers to hand in: do not wait for new requests yet
        try:
            code, body = post(url, payload)
        except Exception as exc:  # network: keep the answers, try again later
            code, body = 0, {'error': type(exc).__name__}
        if code in (400, 413) and batch:
            if any('ct' in r for r in batch):
                # the site refuses this batch for good (size): tell the pages it failed instead of retrying forever
                waiting = [{'id': r['id'], 'error': 'too_large'} for r in batch] + rest
            else:
                # even the plain error answers are refused: drop them (the pages time out) and back off
                waiting = rest
                time.sleep(delay)
                delay = min(delay * 2, 60)
            continue
        if code != 200:
            if log:
                log(f'worker call failed: {code} {body.get("error") if isinstance(body, dict) else ""}')
            if code == 403:
                raise PermissionError('the site refused the worker token: pair again (hubctl pair)')
            time.sleep(delay)
            delay = min(delay * 2, 60)
            continue
        delay, waiting = 1, rest
        for req in (body.get('requests') or [])[:20]:
            if isinstance(req, dict):
                waiting.append(answer(keys, api, env, req, seen))


def _post(url, payload):
    req = urllib.request.Request(url, data=json.dumps(payload).encode(), method='POST',
                                 headers={'Content-Type': 'application/json', 'User-Agent': 'freedom-concierge-mail/0.8'})
    try:
        with urllib.request.urlopen(req, timeout=40) as resp:
            return resp.status, json.loads(resp.read() or b'{}')
    except urllib.error.HTTPError as err:
        try:
            return err.code, json.loads(err.read() or b'{}')
        except ValueError:
            return err.code, {}


def main(store, root):
    """Entry of `mailcli.py relay-serve` under the unit concierge-mail-relay@<slug>."""
    cred = os.environ.get('CREDENTIALS_DIRECTORY')
    token = (Path(cred) / 'relay').read_text(encoding='utf-8').strip() if cred and (Path(cred) / 'relay').is_file() else ''
    url, env, account = os.environ.get('RELAY_URL', ''), os.environ.get('AIWB_ENV', ''), os.environ.get('ACCOUNT', '').strip().lower()
    if not (token and url.startswith('https://') and env and account):
        raise ValueError('relay-serve needs the relay credential, RELAY_URL (https), AIWB_ENV and ACCOUNT')
    store.db.execute('PRAGMA busy_timeout=10000')
    serve(store, root, env, account, url, token, log=lambda m: print(m, file=sys.stderr, flush=True))
