"""Writing mail from the Mail page: drafts at the provider, sending with a 10-second undo, marks (read, archive, trash, star).

Everything here happens only on the person's click in the page (home-relay action of that page); an agent never reaches
it. The provider (Gmail API) is the transport: a letter being written is a Gmail draft from its first save, so it is not
lost when the page closes, and it shows in Gmail as well. Sending queues the draft in the outbox for UNDO_SECONDS; the
handler's sender thread then sends it (drafts.send) unless the person pressed "Cancel" in time, which leaves the draft.

The local state of a letter being written (fields and attachments) lives in the person's store (tables compose and
outbox, attachments as blobs), so an autosave sends only the fields and the draft is rebuilt here with its attachments.

Token: only mail.compose + mail.modify of the person's shared account (module provider-accounts, narrowed refresh);
without that consent every write answers `needs_consent` and nothing else changes.
"""
from __future__ import annotations

import base64
import html as htmllib
import json
import re
import secrets
import sqlite3
import threading
import time
import urllib.parse
from email import policy
from email.message import EmailMessage
from email.parser import BytesParser
from email.utils import formataddr, getaddresses, make_msgid, formatdate

API = 'https://gmail.googleapis.com/gmail/v1/users/me/'
UPLOAD = 'https://gmail.googleapis.com/upload/gmail/v1/users/me/'
CAPABILITIES = ['mail.compose', 'mail.modify']
UNDO_SECONDS = 10
RECONCILE = 20                       # seconds between provider checks of a send with an unknown outcome
ATTACH_TOTAL = 20 * 1024 * 1024      # Gmail takes 25 MB per letter, encoding included
CHUNK_MAX = 512 * 1024               # bytes of one upload piece from the page (the site takes ~1 MB per sealed request)
FIELD_MAX = 512 * 1024               # characters of the text of a letter
RCPT_MAX = 100
MARKS = {'read': ([], ['UNREAD']), 'unread': (['UNREAD'], []), 'archive': ([], ['INBOX']), 'inbox': (['INBOX'], []),
         'star': (['STARRED'], []), 'unstar': ([], ['STARRED']), 'spam': (['SPAM'], ['INBOX']), 'notspam': (['INBOX'], ['SPAM'])}
ID_RE = re.compile(r'[A-Za-z0-9_-]{1,64}')
SHA_RE = re.compile(r'[0-9a-f]{64}')


def schema(db):
    db.executescript('''
    CREATE TABLE IF NOT EXISTS compose(key TEXT PRIMARY KEY, account TEXT NOT NULL, draft_id TEXT, message_id TEXT,
        thread_id TEXT, fields TEXT NOT NULL DEFAULT '{}', attachments TEXT NOT NULL DEFAULT '[]', updated REAL NOT NULL);
    CREATE TABLE IF NOT EXISTS compose_upload(key TEXT NOT NULL, upload TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
        size INTEGER NOT NULL, PRIMARY KEY(key, upload));
    CREATE TABLE IF NOT EXISTS outbox(key TEXT PRIMARY KEY, account TEXT NOT NULL, draft_id TEXT NOT NULL, due REAL NOT NULL,
        state TEXT NOT NULL, error TEXT NOT NULL DEFAULT '', sent_id TEXT NOT NULL DEFAULT '', updated REAL NOT NULL);
    ''')
    db.commit()


class NeedsConsent(Exception):
    pass


class Uncertain(Exception):
    """The provider may or may not have done it (no answer, 5xx): a send is reconciled, never repeated blindly."""


class GmailWrite:
    """The few write calls of the Gmail API the page needs; retries rate limits, one re-auth on 401."""

    def __init__(self, token, request, sleep=time.sleep):
        self.token, self._request, self._sleep = token, request, sleep

    def call(self, method, path, body=None, upload=False, once=False):
        """once: a call that must not be repeated after an unclear outcome (sending) — a 5xx or a lost answer raises
        Uncertain instead of retrying; 401 (re-auth) and 429 (refused before work) are still retried."""
        url = (UPLOAD if upload else API) + path
        data, ctype = None, None
        if body is not None:
            data, ctype = json.dumps(body).encode(), 'application/json'
        retried = False
        for attempt in range(5):
            try:
                access = self.token()
            except PermissionError as exc:
                raise NeedsConsent(str(exc)) from exc
            headers = {'Authorization': 'Bearer ' + access}
            if ctype:
                headers['Content-Type'] = ctype
            try:
                status, out = self._request(method, url, headers, data)
            except Exception as exc:  # noqa: BLE001 - a lost connection or timeout
                if once:
                    raise Uncertain(type(exc).__name__) from exc
                if attempt == 4:
                    raise RuntimeError('provider unreachable') from exc
                self._sleep(min(16, 2 ** attempt))
                continue
            if status == 401 and not retried:
                retried = True
                invalidate = getattr(self.token, 'invalidate', None)
                if callable(invalidate):
                    invalidate(access)
                continue
            if status in (200, 204):
                return out if isinstance(out, dict) else {}
            if status == 403 and 'insufficient' in json.dumps(out).lower():
                raise NeedsConsent('the account did not allow writing mail')
            if status == 404:
                raise LookupError('gone at the provider')
            if once and status >= 500:
                raise Uncertain(f'provider {status}')
            if status in (429, 500, 502, 503, 504) or (status == 403 and 'rate' in json.dumps(out).lower()):
                self._sleep(min(16, 2 ** attempt))
                continue
            raise RuntimeError(f'provider {status}')
        raise RuntimeError('provider busy')

    def draft_put(self, draft_id, raw, thread_id):
        msg = {'raw': base64.urlsafe_b64encode(raw).decode()}
        if thread_id:
            msg['threadId'] = thread_id
        if draft_id:
            return self.call('PUT', 'drafts/' + draft_id, {'id': draft_id, 'message': msg})
        return self.call('POST', 'drafts', {'message': msg})

    def draft_delete(self, draft_id):
        return self.call('DELETE', 'drafts/' + draft_id)

    def draft_send(self, draft_id):
        return self.call('POST', 'drafts/send', {'id': draft_id}, once=True)

    def draft_exists(self, draft_id):
        try:
            self.call('GET', 'drafts/' + draft_id + '?format=minimal')
            return True
        except LookupError:
            return False

    def drafts(self):
        out, page = [], None
        while True:
            q = {'maxResults': 500, **({'pageToken': page} if page else {})}
            data = self.call('GET', 'drafts?' + urllib.parse.urlencode(q))
            out += data.get('drafts') or []
            page = data.get('nextPageToken')
            if not page or len(out) >= 5000:
                return out

    def modify(self, thread_id, add, remove):
        return self.call('POST', f'threads/{thread_id}/modify', {'addLabelIds': add, 'removeLabelIds': remove})

    def trash(self, thread_id, undo=False):
        return self.call('POST', f'threads/{thread_id}/{"untrash" if undo else "trash"}')

    def send_as(self):
        return self.call('GET', 'settings/sendAs').get('sendAs') or []


def _addresses(value, field):
    text = str(value or '')
    if re.search(r'[\r\n\x00]', text):
        raise ValueError('bad ' + field)
    out = []
    for name, addr in getaddresses([text]):
        addr = addr.strip()
        if not addr:
            continue
        if not re.fullmatch(r'[^@\s<>",;]+@[^@\s<>",;]+\.[^@\s<>",;]+', addr):
            raise ValueError('bad address: ' + addr[:80])
        out.append(formataddr((name.strip(), addr)) if name.strip() else addr)
    if len(out) > RCPT_MAX:
        raise ValueError('too many recipients')
    return out


def _text_of_html(html):
    text = re.sub(r'(?is)<(script|style)\b.*?</\1\s*>', '', html or '')
    text = re.sub(r'(?i)<br\s*/?>|</(p|div|tr|li|h\d)>', '\n', text)
    text = re.sub(r'<[^>]+>', '', text)
    return re.sub(r'\n{3,}', '\n\n', htmllib.unescape(text)).strip()


def _html_of_text(text):
    out = []
    for part in re.split(r'(https?://[^\s<>"]+)', text):
        if re.match(r'https?://', part):
            out.append(f'<a href="{htmllib.escape(part, quote=True)}">{htmllib.escape(part)}</a>')
        else:
            out.append(htmllib.escape(part))
    return ''.join(out).replace('\n', '<br>\n')


class Composer:
    """State and actions of the letters a person writes; one per relay handler process."""

    def __init__(self, store, account, token=None, request=None, clock=time.time, sleep=time.sleep):
        self.store, self.db, self.account = store, store.db, account
        self.token, self._request, self.clock = token, request, clock
        self._gmail = GmailWrite(token, request, sleep) if token and request else None
        self._identity = None
        self._wake = threading.Event()
        schema(self.db)

    # -- helpers

    def gmail(self):
        if not self._gmail:
            raise NeedsConsent('writing is not set up for this mailbox')
        return self._gmail

    def identity(self):
        """(display name, signature html) of the account's own address (Gmail settings), cached per process."""
        if self._identity is None:
            name, sig = '', ''
            try:
                for one in self.gmail().send_as():
                    if str(one.get('sendAsEmail', '')).lower() == self.account:
                        name, sig = one.get('displayName') or '', one.get('signature') or ''
            except (RuntimeError, LookupError):
                pass
            self._identity = (name, sig)
        return self._identity

    def _row(self, key):
        if not isinstance(key, str) or not re.fullmatch(r'[a-f0-9]{32}', key):
            raise ValueError('bad letter')
        return self.db.execute('SELECT * FROM compose WHERE key=? AND account=?', (key, self.account)).fetchone()

    def _original(self, gid):
        if not gid:
            return None
        if not ID_RE.fullmatch(str(gid)):
            raise ValueError('bad message')
        row = self.db.execute('SELECT g.gmail_id, g.thread_id, m.* FROM gmail_messages g JOIN messages m ON m.id=g.message_id'
                              ' WHERE g.account=? AND g.gmail_id=?', (self.account, gid)).fetchone()
        if not row:
            raise LookupError('no message')
        if row['sensitive']:
            raise ValueError('protected message')
        return row

    def _headers_of(self, row):
        try:
            return BytesParser(policy=policy.default).parsebytes(self.store.read_blob(row['raw_sha256']), headersonly=True)
        except (OSError, ValueError):
            return {}

    # -- building the letter

    def build(self, fields, attachments):
        """MIME bytes of the letter: text/plain + text/html (the text, the account's Gmail signature, the quote)."""
        msg = EmailMessage(policy=policy.SMTP)
        name, sig = self.identity()
        msg['From'] = formataddr((name, self.account)) if name else self.account
        for field in ('to', 'cc', 'bcc'):
            addrs = _addresses(fields.get(field), field)
            if addrs:
                msg[field.capitalize()] = ', '.join(addrs)
        subject = str(fields.get('subject') or '')
        if re.search(r'[\r\n\x00]', subject):
            raise ValueError('bad subject')
        msg['Subject'] = subject[:500]
        msg['Date'] = formatdate(localtime=True)
        msg['Message-ID'] = make_msgid(domain=self.account.split('@')[-1])
        text = str(fields.get('text') or '')[:FIELD_MAX]
        quote_text, quote_html = '', ''
        orig = self._original(fields.get('of')) if fields.get('of') else None
        if orig is not None:
            head = self._headers_of(orig)
            mid = orig['message_id'] or head.get('Message-ID', '')
            if fields.get('kind') in ('reply', 'replyall') and mid:
                refs = ' '.join(x for x in [str(head.get('References') or '').strip(), mid] if x)
                msg['In-Reply-To'] = mid
                msg['References'] = refs[-4000:]
            when = orig['date'] or ''
            body = orig['body'] or _text_of_html(orig['html'])
            if fields.get('kind') == 'forward':
                block = (f"---------- Forwarded message ---------\nFrom: {orig['sender']}\nDate: {when}\n"
                         f"Subject: {orig['subject']}\nTo: {orig['recipients']}\n\n")
                quote_text = '\n\n' + block + body
                quote_html = ('<br><br><div>' + _html_of_text(block) + '</div>' +
                              ('<div>' + orig['html'] + '</div>' if orig['html'] else _html_of_text(body)))
            elif fields.get('quote', True):
                lead = f"{when}, {orig['sender']}:"
                quote_text = '\n\n' + lead + '\n' + '\n'.join('> ' + line for line in body.splitlines())
                quote_html = ('<br><br><div>' + htmllib.escape(lead) + '</div><blockquote style="margin:0 0 0 .8ex;'
                              'border-left:1px solid #ccc;padding-left:1ex">' + (orig['html'] or _html_of_text(body)) + '</blockquote>')
        sig_text = ('\n\n-- \n' + _text_of_html(sig)) if sig and fields.get('signature', True) else ''
        sig_html = ('<br><br><div>' + sig + '</div>') if sig and fields.get('signature', True) else ''
        msg.set_content(text + sig_text + quote_text)
        msg.add_alternative('<div dir="auto">' + _html_of_text(text) + '</div>' + sig_html + quote_html, subtype='html')
        for att in attachments:
            data = self.store.read_blob(att['sha'])
            maintype, _, subtype = (att.get('type') or 'application/octet-stream').partition('/')
            msg.add_attachment(data, maintype=maintype or 'application', subtype=subtype or 'octet-stream',
                               filename=att.get('name') or 'file')
        return msg.as_bytes()

    def _save(self, key, row, fields, attachments):
        raw = self.build(fields, attachments)
        orig = self._original(fields.get('of')) if fields.get('of') else None
        thread = row['thread_id'] if row and row['thread_id'] else (orig['thread_id'] if orig is not None and
                                                                     fields.get('kind') in ('reply', 'replyall') else None)
        try:
            out = self.gmail().draft_put(row['draft_id'] if row else None, raw, thread)
        except LookupError:  # deleted in Gmail meanwhile: a new draft
            out = self.gmail().draft_put(None, raw, thread)
        draft = out.get('id') or ''
        message = (out.get('message') or {}).get('id') or ''
        thread = (out.get('message') or {}).get('threadId') or thread or ''
        with self.db:
            self.db.execute('INSERT INTO compose(key,account,draft_id,message_id,thread_id,fields,attachments,updated) VALUES(?,?,?,?,?,?,?,?)'
                            ' ON CONFLICT(key) DO UPDATE SET draft_id=excluded.draft_id, message_id=excluded.message_id,'
                            ' thread_id=excluded.thread_id, fields=excluded.fields, attachments=excluded.attachments, updated=excluded.updated',
                            (key, self.account, draft, message, thread, json.dumps(fields, ensure_ascii=False),
                             json.dumps(attachments), self.clock()))
        return {'key': key, 'draft': draft, 'thread': thread, 'attachments': self._public(attachments), 'saved': self.clock()}

    @staticmethod
    def _public(attachments):
        return [{'sha': a['sha'], 'name': a['name'], 'type': a['type'], 'size': a['size']} for a in attachments]

    def _waiting(self, key):
        row = self.db.execute("SELECT state FROM outbox WHERE key=?", (key,)).fetchone()
        return row and row['state'] in ('wait', 'sending', 'unknown', 'sent')

    # -- actions of the page

    def start(self, args):
        """A new letter: empty, or a reply / reply all / forward of a stored message (fields prepared here)."""
        kind = str(args.get('kind') or 'new')
        if kind not in ('new', 'reply', 'replyall', 'forward'):
            raise ValueError('bad kind')
        key = secrets.token_hex(16)
        fields = {'kind': kind, 'to': '', 'cc': '', 'bcc': '', 'subject': '', 'text': ''}
        attachments = []
        if kind != 'new':
            orig = self._original(args.get('of'))
            head = self._headers_of(orig)
            fields['of'] = orig['gmail_id']
            subject = orig['subject'] or ''
            if kind == 'forward':
                fields['subject'] = subject if re.match(r'(?i)^(fwd?|пересл)\s*:', subject) else 'Fwd: ' + subject
                attachments = [{'sha': a['sha256'], 'name': a['filename'], 'type': a['content_type'], 'size': a['size']}
                               for a in self.db.execute('SELECT sha256,filename,content_type,size FROM attachments WHERE message_id=?',
                                                        (orig['id'],))]
            else:
                fields['subject'] = subject if re.match(r'(?i)^(re|отв)\s*:', subject) else 'Re: ' + subject
                reply_to = str(head.get('Reply-To') or '') or orig['sender']
                mine = orig['is_sent'] or self.account in orig['sender'].lower()
                to = [orig['recipients'] if mine else reply_to]
                fields['to'] = ', '.join(a for a in _addresses(', '.join(to), 'to'))
                if kind == 'replyall':
                    others = _addresses(', '.join([orig['recipients'], str(head.get('Cc') or '')]), 'cc')
                    taken = {getaddresses([a])[0][1].lower() for a in _addresses(fields['to'], 'to')} | {self.account}
                    fields['cc'] = ', '.join(a for a in others if getaddresses([a])[0][1].lower() not in taken)
        # forwarded attachments: the page hands their shas back with the first save
        return {'key': key, 'fields': fields, 'attachments': self._public(attachments), 'draft': '', 'saved': 0,
                'pending': [a['sha'] for a in attachments]}

    def save(self, args):
        """Autosave / save: the fields of the page; the draft at the provider is created or replaced."""
        key = str(args.get('key') or '')
        row = self._row(key)
        if self._waiting(key):
            raise ValueError('already sending')
        given = args.get('fields') or {}
        if not isinstance(given, dict):
            raise ValueError('bad fields')
        fields = json.loads(row['fields']) if row else {}
        for k in ('kind', 'of', 'to', 'cc', 'bcc', 'subject', 'text', 'quote', 'signature'):
            if k in given:
                if k in ('kind', 'of') and row:  # what a letter answers is fixed at its first save
                    continue
                fields[k] = given[k] if isinstance(given[k], bool) else str(given[k])[:FIELD_MAX]
        if fields.get('kind') not in ('new', 'reply', 'replyall', 'forward'):
            raise ValueError('bad kind')
        attachments = json.loads(row['attachments']) if row else []
        if not row and isinstance(args.get('pending'), list):
            # the attachments of a forwarded message (shas of this mailbox only)
            for sha in args['pending'][:50]:
                one = self.db.execute('SELECT a.sha256,a.filename,a.content_type,a.size FROM attachments a JOIN messages m ON m.id=a.message_id'
                                      ' WHERE a.sha256=? AND m.account=? AND m.sensitive=0 LIMIT 1', (str(sha), self.account)).fetchone()
                if one:
                    attachments.append({'sha': one[0], 'name': one[1], 'type': one[2], 'size': one[3]})
        return self._save(key, row, fields, attachments)

    def upload(self, args):
        """One piece of a file the person attaches (pieces in order); the last piece adds it to the draft."""
        key, upload = str(args.get('key') or ''), str(args.get('upload') or '')
        row = self._row(key)
        if not row:
            raise ValueError('save first')
        if self._waiting(key) or not re.fullmatch(r'[a-f0-9]{16}', upload):
            raise ValueError('bad upload')
        piece = base64.b64decode(str(args.get('data') or ''), validate=True)
        if len(piece) > CHUNK_MAX:
            raise ValueError('piece too big')
        name = re.sub(r'[\x00-\x1f\\/:*?"<>|]', '_', str(args.get('name') or 'file'))[:180] or 'file'
        ctype = str(args.get('type') or 'application/octet-stream')
        if not re.fullmatch(r'[a-z0-9.+-]+/[a-z0-9.+-]+', ctype.lower()):
            ctype = 'application/octet-stream'
        attachments = json.loads(row['attachments'])
        used = sum(a['size'] for a in attachments)
        with self.db:
            self.db.execute('INSERT OR IGNORE INTO compose_upload(key,upload,name,type,size) VALUES(?,?,?,?,0)', (key, upload, name, ctype))
        cur = self.db.execute('SELECT name,type,size FROM compose_upload WHERE key=? AND upload=?', (key, upload)).fetchone()
        part = self._part(key, upload)
        if int(args.get('offset', -1)) != cur['size']:
            raise ValueError('bad offset')
        if used + cur['size'] + len(piece) > ATTACH_TOTAL:
            self._drop_upload(key, upload)
            raise ValueError('too big')
        part.parent.mkdir(mode=0o700, exist_ok=True)
        with open(part, 'ab') as fh:
            if fh.tell() != cur['size']:  # the file and the row disagree (a crash between them): start this file again
                fh.close()
                self._drop_upload(key, upload)
                raise ValueError('bad offset')
            fh.write(piece)
        with self.db:
            self.db.execute('UPDATE compose_upload SET size=size+? WHERE key=? AND upload=?', (len(piece), key, upload))
        if not args.get('last'):
            return {'key': key, 'upload': upload, 'received': cur['size'] + len(piece)}
        sha = self.store._blob(part.read_bytes())
        attachments.append({'sha': sha, 'name': cur['name'], 'type': cur['type'], 'size': cur['size'] + len(piece)})
        self._drop_upload(key, upload)
        return self._save(key, row, json.loads(row['fields']), attachments)

    def _part(self, key, upload):
        return self.store.root / 'uploads' / f'{key}-{upload}.part'

    def _drop_upload(self, key, upload=None):
        with self.db:
            if upload:
                self.db.execute('DELETE FROM compose_upload WHERE key=? AND upload=?', (key, upload))
            else:
                self.db.execute('DELETE FROM compose_upload WHERE key=?', (key,))
        for part in (self.store.root / 'uploads').glob(f'{key}-{upload or "*"}.part'):
            try:
                part.unlink()
            except OSError:
                pass

    def detach(self, args):
        key, sha = str(args.get('key') or ''), str(args.get('sha') or '')
        row = self._row(key)
        if not row or not SHA_RE.fullmatch(sha) or self._waiting(key):
            raise ValueError('bad attachment')
        attachments = [a for a in json.loads(row['attachments']) if a['sha'] != sha]
        return self._save(key, row, json.loads(row['fields']), attachments)

    def discard(self, args):
        key = str(args.get('key') or '')
        row = self._row(key)
        if self._waiting(key):
            raise ValueError('already sending')
        if row and row['draft_id']:
            try:
                self.gmail().draft_delete(row['draft_id'])
            except LookupError:
                pass
        with self.db:
            self.db.execute('DELETE FROM compose WHERE key=?', (key,))
        self._drop_upload(key)
        return {'key': key, 'discarded': True}

    def send(self, args):
        """Save the last fields, then queue the draft: it goes out after UNDO_SECONDS unless `unsend` comes first."""
        key = str(args.get('key') or '')
        saved = self.save(args) if args.get('fields') else None
        row = self._row(key)
        if not row or not row['draft_id']:
            raise ValueError('save first')
        fields = json.loads(row['fields'])
        if not (_addresses(fields.get('to'), 'to') or _addresses(fields.get('cc'), 'cc') or _addresses(fields.get('bcc'), 'bcc')):
            raise ValueError('no recipients')
        due = self.clock() + UNDO_SECONDS
        with self.db:
            cur = self.db.execute("INSERT INTO outbox(key,account,draft_id,due,state,updated) VALUES(?,?,?,?, 'wait', ?)"
                                  " ON CONFLICT(key) DO UPDATE SET draft_id=excluded.draft_id, due=excluded.due, state='wait',"
                                  " error='', updated=excluded.updated WHERE outbox.state IN ('cancelled','failed')",
                                  (key, self.account, row['draft_id'], due, self.clock()))
            if not cur.rowcount:
                raise ValueError('already sending')
        self._wake.set()
        return {'key': key, 'state': 'wait', 'due': due, 'undo': UNDO_SECONDS, **({'draft': saved['draft']} if saved else {})}

    def unsend(self, args):
        key = str(args.get('key') or '')
        self._row(key)
        with self.db:
            cur = self.db.execute("UPDATE outbox SET state='cancelled', updated=? WHERE key=? AND account=? AND state='wait'",
                                  (self.clock(), key, self.account))
        state = self.db.execute('SELECT state FROM outbox WHERE key=?', (key,)).fetchone()
        return {'key': key, 'cancelled': bool(cur.rowcount), 'state': state['state'] if state else 'none'}

    def outbox(self, args):
        rows = self.db.execute('SELECT key,state,error,due,updated FROM outbox WHERE account=? AND (state IN (\'wait\',\'sending\')'
                               ' OR updated > ?) ORDER BY updated DESC LIMIT 50', (self.account, self.clock() - 3600)).fetchall()
        return {'items': [dict(r) for r in rows]}

    def drafts(self, args):
        """Letters being written here (newest first), so the page can go on with one."""
        rows = self.db.execute('SELECT c.key, c.fields, c.attachments, c.draft_id, c.message_id, c.updated FROM compose c'
                               ' LEFT JOIN outbox o ON o.key=c.key WHERE c.account=? AND (o.state IS NULL OR o.state IN'
                               " ('cancelled','failed')) ORDER BY c.updated DESC LIMIT 100", (self.account,)).fetchall()
        return {'drafts': [{'key': r['key'], 'fields': json.loads(r['fields']), 'attachments': self._public(json.loads(r['attachments'])),
                            'draft': r['draft_id'], 'message': r['message_id'], 'saved': r['updated']} for r in rows]}

    def open_draft(self, args):
        """A draft from Gmail's Drafts (also one written elsewhere): its fields, to go on writing it here."""
        gid = str(args.get('id') or '')
        if not ID_RE.fullmatch(gid):
            raise ValueError('bad message')
        row = self.db.execute('SELECT * FROM compose WHERE account=? AND message_id=?', (self.account, gid)).fetchone()
        if row:
            return {'key': row['key'], 'fields': json.loads(row['fields']), 'attachments': self._public(json.loads(row['attachments'])),
                    'draft': row['draft_id'], 'saved': row['updated']}
        draft = next((d for d in self.gmail().drafts() if (d.get('message') or {}).get('id') == gid), None)
        if not draft:
            raise LookupError('no draft')
        msg = self._original(gid)
        head = self._headers_of(msg)
        fields = {'kind': 'new', 'to': str(head.get('To') or ''), 'cc': str(head.get('Cc') or ''), 'bcc': str(head.get('Bcc') or ''),
                  'subject': msg['subject'], 'text': msg['body'] or _text_of_html(msg['html']), 'quote': False, 'signature': False}
        attachments = [{'sha': a['sha256'], 'name': a['filename'], 'type': a['content_type'], 'size': a['size']}
                       for a in self.db.execute('SELECT sha256,filename,content_type,size FROM attachments WHERE message_id=?', (msg['id'],))]
        key = secrets.token_hex(16)
        with self.db:
            self.db.execute('INSERT INTO compose(key,account,draft_id,message_id,thread_id,fields,attachments,updated) VALUES(?,?,?,?,?,?,?,?)',
                            (key, self.account, draft['id'], gid, msg['thread_id'], json.dumps(fields, ensure_ascii=False),
                             json.dumps(attachments), self.clock()))
        return {'key': key, 'fields': fields, 'attachments': self._public(attachments), 'draft': draft['id'], 'saved': self.clock()}

    def mark(self, args):
        """read / unread / archive / inbox / star / unstar / spam / notspam / trash / untrash of a whole thread."""
        tid, what = str(args.get('thread') or ''), str(args.get('mark') or '')
        if not ID_RE.fullmatch(tid):
            raise ValueError('bad thread')
        if what in ('trash', 'untrash'):
            self.gmail().trash(tid, undo=what == 'untrash')
            add, remove = (['TRASH'], ['INBOX']) if what == 'trash' else ([], ['TRASH'])
        elif what in MARKS:
            add, remove = MARKS[what]
            self.gmail().modify(tid, add, remove)
        else:
            raise ValueError('bad mark')
        # the store follows at once; the next sync confirms it from the provider
        with self.db:
            for r in self.db.execute('SELECT gmail_id, labels FROM gmail_messages WHERE account=? AND thread_id=? AND gone=0',
                                     (self.account, tid)).fetchall():
                labels = [l for l in json.loads(r['labels'] or '[]') if l not in remove]
                labels += [l for l in add if l not in labels]
                if what == 'untrash' and 'INBOX' not in labels and 'SENT' not in labels:
                    labels.append('INBOX')
                self.db.execute('UPDATE gmail_messages SET labels=? WHERE account=? AND gmail_id=?',
                                (json.dumps(labels), self.account, r['gmail_id']))
        return {'thread': tid, 'mark': what}

    # -- the sender (own thread and SQLite connection)

    def run_sender(self, db_path, stop=None):
        db = sqlite3.connect(db_path, timeout=10)
        db.row_factory = sqlite3.Row
        db.execute('PRAGMA busy_timeout=10000')
        stop = stop or threading.Event()
        # a letter caught mid-send by a restart is not sent twice blindly: its fate is asked from the provider first
        with db:
            db.execute("UPDATE outbox SET state='unknown', error='interrupted', updated=0 WHERE state='sending' AND account=?", (self.account,))
        while not stop.is_set():
            self.send_due(db)
            self.reconcile(db)
            nxt = db.execute("SELECT MIN(due) FROM outbox WHERE state='wait' AND account=?", (self.account,)).fetchone()[0]
            if db.execute("SELECT 1 FROM outbox WHERE state='unknown' AND account=? LIMIT 1", (self.account,)).fetchone():
                nxt = min(nxt or 1e18, self.clock() + RECONCILE)
            self._wake.wait(max(0.2, min(30.0, (nxt - self.clock()) if nxt else 30.0)))
            self._wake.clear()
        db.close()

    def send_due(self, db):
        now = self.clock()
        for r in db.execute("SELECT key FROM outbox WHERE state='wait' AND due<=? AND account=?", (now, self.account)).fetchall():
            # claim only a row still due now: after a cancel and a new Send meanwhile its due is later
            with db:
                took = db.execute("UPDATE outbox SET state='sending', updated=? WHERE key=? AND state='wait' AND due<=?",
                                  (now, r['key'], now)).rowcount
                draft_id = db.execute('SELECT draft_id FROM outbox WHERE key=?', (r['key'],)).fetchone()['draft_id'] if took else None
            if not took:
                continue  # cancelled (or sent again) at the last moment
            try:
                out = self.gmail().draft_send(draft_id)
                with db:
                    db.execute("UPDATE outbox SET state='sent', sent_id=?, updated=? WHERE key=?", (out.get('id') or '', self.clock(), r['key']))
                    db.execute('DELETE FROM compose WHERE key=?', (r['key'],))
            except NeedsConsent:
                with db:
                    db.execute("UPDATE outbox SET state='failed', error='needs_consent', updated=? WHERE key=?", (self.clock(), r['key']))
            except LookupError:  # a send is never repeated, so a missing draft was removed in Gmail, not sent by us
                with db:
                    db.execute("UPDATE outbox SET state='failed', error='draft gone', updated=? WHERE key=?", (self.clock(), r['key']))
            except Uncertain as exc:
                # maybe sent (a lost answer or a 5xx): asked from the provider before anything else
                with db:
                    db.execute("UPDATE outbox SET state='unknown', error=?, updated=? WHERE key=?", (str(exc)[:200], self.clock(), r['key']))
            except Exception as exc:  # noqa: BLE001 - a definite refusal: the person sees it and the draft stays
                with db:
                    db.execute("UPDATE outbox SET state='failed', error=?, updated=? WHERE key=?", (str(exc)[:200], self.clock(), r['key']))

    def reconcile(self, db):
        """Letters whose send outcome is unknown: drafts.send removes the draft, so a draft that is gone was sent and a
        draft still there was not (then the person may send again). Asked at most every RECONCILE seconds."""
        now = self.clock()
        for r in db.execute("SELECT key, draft_id FROM outbox WHERE state='unknown' AND updated<=? AND account=?",
                            (now - RECONCILE, self.account)).fetchall():
            try:
                there = self.gmail().draft_exists(r['draft_id'])
            except Exception:  # noqa: BLE001 - still unclear: ask again later
                with db:
                    db.execute("UPDATE outbox SET updated=? WHERE key=? AND state='unknown'", (now, r['key']))
                continue
            with db:
                if there:
                    db.execute("UPDATE outbox SET state='failed', error='not sent', updated=? WHERE key=? AND state='unknown'", (now, r['key']))
                else:
                    db.execute("UPDATE outbox SET state='sent', updated=? WHERE key=? AND state='unknown'", (now, r['key']))
                    db.execute('DELETE FROM compose WHERE key=?', (r['key'],))

    def handle(self, action, args):
        fn = {'compose_start': self.start, 'compose_save': self.save, 'compose_upload': self.upload,
              'compose_detach': self.detach, 'compose_discard': self.discard, 'compose_send': self.send,
              'compose_unsend': self.unsend, 'compose_outbox': self.outbox, 'compose_drafts': self.drafts,
              'compose_open': self.open_draft, 'mark': self.mark}.get(action)
        if not fn:
            raise ValueError('unknown action')
        try:
            return fn(args)
        except NeedsConsent:
            raise ValueError('needs_consent')


ACTIONS = ('compose_start', 'compose_save', 'compose_upload', 'compose_detach', 'compose_discard', 'compose_send',
           'compose_unsend', 'compose_outbox', 'compose_drafts', 'compose_open', 'mark')
