"""Local SQLite MVP; PostgreSQL backend and authenticated-code routing are deferred.

Raw mail and MIME attachments are immutable SHA256 blobs. Credentials never enter
this store. Authentication-code messages are refused before any persistence.
"""
from __future__ import annotations
import hashlib
import json
import base64
import imaplib
import ssl
import os
from pathlib import Path
import re
import sqlite3
import tempfile
import sys
from html import unescape
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools" / "read-guard"))
import read_guard
from email import policy
from email.parser import BytesParser
from email.utils import getaddresses, parseaddr, parsedate_to_datetime


class SensitiveMessageError(ValueError):
    pass


class UnsafeMessageError(ValueError):
    pass


# Benign "<word> code <digits>" phrases (postcodes, vacancy and order numbers).
_BENIGN_CODE = re.compile(r'(?i)\b(?:zip|post(?:al)?|vacancy|job|reference|ref|product|promo|discount|voucher|tracking|order|area|country|course|error|source|tax|vat)[ -]?code\b')
_OTP = re.compile(r'(?i)\b(?:one[ -]?time|verification|security|authentication|confirmation|login|log[ -]?in|sign[ -]?in|access|2fa|two[ -]factor)\s+(?:code|pin|passcode|password|link|token)\b'
                  r'|\b(?:reset|change|recover)\s+(?:your\s+)?password\b|\bpassword\s+(?:reset|recovery|change)\b|\bnew\s+password\b|\bforgot\s+(?:your\s+)?password\b'
                  r'|\b(?:verify|confirm)\s+(?:your\s+)?(?:e-?mail(?:\s+address)?|account|identity|sign[ -]?in|login)\b|\b2-step\s+verification\b'
                  r'|wachtwoord\s+(?:herstellen|resetten|wijzigen|vergeten)|nieuw\s+wachtwoord|(?:сброс|восстановлени|смен)\w*\s+парол|нов\w*\s+парол|\bmagic\s+link\b|\bpasscode\b'
                  r'|\b(?:code|pin|passcode)\W{0,3}(?:is\b)?\W{0,3}\d{4,8}\b|\bclick\b.{0,40}\bsign[ -]?in\b|\b(?:sign|log)[ -]?in\b.{0,40}https?://'
                  r'|код\s+(?:подтверждения|входа|доступа)|\bкод\W{0,3}\d{4,8}\b|одноразов\w*\s+(?:код|парол)'
                  r'|verificatiecode|beveiligingscode|inlogcode|eenmalige\s+code')


def _normalize(text):
    return re.sub(r'\s+', ' ', unescape(re.sub('<[^>]*>', ' ', text)))


def is_authentication(text):
    """Codes, sign-in links and password resets (section 11). Heuristic."""
    return bool(_OTP.search(_BENIGN_CODE.sub(' ', _normalize(text))))


def guard_reasons(text):
    result = read_guard.scan(text)
    return sorted({s['rule'] for s in result.signals} or {'suspicious'}) if result.suspicious else []


def check_content(text):
    """Strict gate for text the owner's side writes (drafts, cases, promises)."""
    if is_authentication(text):
        raise SensitiveMessageError('Authentication message requires ephemeral handling; not stored')
    if guard_reasons(text):
        raise UnsafeMessageError('Untrusted text requires explicit review; not stored')


REDACTED_SUBJECT = '[authentication message]'
REDACTED_BODY ='[authentication message: content not stored, see section 11]'


class Store:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.blobs = self.root / 'blobs'
        self.blobs.mkdir(exist_ok=True)
        self.db = sqlite3.connect(self.root / 'mail.sqlite3')
        self.db.row_factory = sqlite3.Row
        self.db.execute('PRAGMA foreign_keys=ON')
        self.db.executescript('''
        CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS threads(id INTEGER PRIMARY KEY, account TEXT NOT NULL REFERENCES accounts(id), thread_key TEXT NOT NULL, UNIQUE(account,thread_key));
        CREATE TABLE IF NOT EXISTS cases(id INTEGER PRIMARY KEY, title TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY, account TEXT NOT NULL REFERENCES accounts(id), thread_id INTEGER NOT NULL REFERENCES threads(id), raw_sha256 TEXT NOT NULL, message_id TEXT NOT NULL, subject TEXT NOT NULL, sender TEXT NOT NULL, recipients TEXT NOT NULL, date TEXT NOT NULL, body TEXT NOT NULL, html TEXT NOT NULL, case_id INTEGER REFERENCES cases(id), created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, UNIQUE(account,raw_sha256));
        CREATE TABLE IF NOT EXISTS message_locations(account TEXT NOT NULL,folder TEXT NOT NULL,uidvalidity TEXT NOT NULL,uid TEXT NOT NULL,message_id INTEGER NOT NULL REFERENCES messages(id),PRIMARY KEY(account,folder,uidvalidity,uid));
        CREATE TABLE IF NOT EXISTS attachments(id INTEGER PRIMARY KEY,message_id INTEGER NOT NULL REFERENCES messages(id),filename TEXT NOT NULL,content_type TEXT NOT NULL,sha256 TEXT NOT NULL,size INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS reply_edges(account TEXT NOT NULL, child_mid TEXT NOT NULL, parent_mid TEXT NOT NULL, PRIMARY KEY(account,child_mid,parent_mid));
        CREATE INDEX IF NOT EXISTS reply_edges_parent ON reply_edges(account,parent_mid);
        CREATE TABLE IF NOT EXISTS sent_replies(account TEXT NOT NULL, parent_mid TEXT NOT NULL, PRIMARY KEY(account,parent_mid));
        CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,action TEXT NOT NULL,message_id INTEGER,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(subject,sender,body);
        ''')
        columns = {r[1] for r in self.db.execute('PRAGMA table_info(messages)')}
        for name, ddl in [('is_sent', 'INTEGER NOT NULL DEFAULT 0'), ('sensitive', 'INTEGER NOT NULL DEFAULT 0'),
                          ('tainted', 'INTEGER NOT NULL DEFAULT 0'), ('taint_reasons', "TEXT NOT NULL DEFAULT '[]'")]:
            if name not in columns:
                self.db.execute('ALTER TABLE messages ADD COLUMN ' + name + ' ' + ddl)
        self.db.commit()

    def close(self):
        self.db.close()

    def _blob(self, data):
        digest = hashlib.sha256(data).hexdigest()
        target = self.blobs / digest
        if target.exists():
            if hashlib.sha256(target.read_bytes()).hexdigest() != digest:
                raise ValueError('Blob integrity failure')
            return digest
        fd, temp = tempfile.mkstemp(prefix='.pending-', dir=self.blobs)
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temp, target)
        finally:
            if os.path.exists(temp):
                os.unlink(temp)
        return digest

    def read_blob(self, digest):
        if not re.fullmatch(r'[0-9a-f]{64}', digest):
            raise ValueError('Invalid blob digest')
        data = (self.blobs / digest).read_bytes()
        if hashlib.sha256(data).hexdigest() != digest:
            raise ValueError('Blob integrity failure')
        return data

    def ingest(self, account, raw, folder='', uid='', uidvalidity='', sent=False):
        if not isinstance(raw, bytes):
            raise TypeError('raw must be bytes')
        if not account or len(account) > 320:
            raise ValueError('Account identifier required')
        mail = BytesParser(policy=policy.default).parsebytes(raw)
        plain, html, attachments = [], [], []
        def visit(part):
            filename = part.get_filename()
            if part.get_content_type() == 'message/rfc822':
                data = b'\n'.join(m.as_bytes(policy=policy.default) for m in part.get_payload())
                attachments.append((filename or 'message.eml', part.get_content_type(), data))
                return
            if part.is_multipart():
                for child in part.iter_parts():
                    visit(child)
                return
            data = part.get_payload(decode=True) or b''
            if filename is not None or part.get_content_disposition() == 'attachment':
                attachments.append((filename or '', part.get_content_type(), data))
            elif part.get_content_type() in ('text/plain', 'text/html'):
                try:
                    text = data.decode(part.get_content_charset() or 'utf-8', errors='replace')
                except LookupError:
                    text = data.decode('utf-8', errors='replace')
                (plain if part.get_content_type() == 'text/plain' else html).append(text)
        visit(mail)
        # Classify every decoded text part, nested ones included. Binary parts are
        # not decoded as text: their analysis belongs to attachment extraction.
        texts = [str(mail.get('subject', ''))]
        for part in mail.walk():
            texts.append(' '.join(str(v) for _, v in part.items()))
            if not part.is_multipart() and part.get_content_maintype() == 'text':
                payload = part.get_payload(decode=True) or b''
                try:
                    texts.append(payload.decode(part.get_content_charset() or 'utf-8', errors='replace'))
                except LookupError:
                    texts.append(payload.decode('utf-8', errors='replace'))
        sensitive = any(is_authentication(t) for t in texts)
        # Suspicious mail is kept (evidence, nothing silently lost) but tainted:
        # actions on its thread wait for an explicit yes (section 16).
        reasons = sorted({r for t in texts for r in guard_reasons(t)})
        subject = str(mail.get('subject', ''))
        body, html_body = '\n'.join(plain), '\n'.join(html)
        if sensitive:
            # Section 11: the code never reaches disk. Keep a visible stub only.
            # Codes may be alphanumeric and links may sit in the subject: keep none of it.
            subject = REDACTED_SUBJECT
            body, html_body, attachments = REDACTED_BODY, '', []
            digest = hashlib.sha256(raw).hexdigest()
        else:
            digest = self._blob(raw)
        sender, recipients, date = str(mail.get('from', '')), str(mail.get('to', '')), str(mail.get('date', ''))
        mid = str(mail.get('message-id', ''))
        refs = re.findall(r'<[^>]+>', str(mail.get('references', '')))
        thread_key = (refs[0] if refs else str(mail.get('in-reply-to', ''))) or mid or digest
        parent_ids = re.findall(r'<[^>]+>', str(mail.get('in-reply-to', ''))) + list(reversed(refs))
        if sensitive:
            # Headers may carry the code too: keep the sender's domain, bare
            # recipient addresses and a normalized date; the stub is not threaded.
            sender = '@' + parseaddr(sender)[1].rpartition('@')[2].lower()
            recipients = ', '.join(a for _, a in getaddresses([recipients]) if a)
            try:
                date = parsedate_to_datetime(date).isoformat()
            except (TypeError, ValueError, IndexError):
                date = ''
            mid, parent_ids, thread_key = '', [], digest
        for parent_mid in parent_ids:
            parent = self.db.execute('SELECT t.thread_key FROM messages m JOIN threads t ON t.id=m.thread_id WHERE m.account=? AND m.message_id=?', (account, parent_mid)).fetchone()
            if parent:
                thread_key = parent[0]
                break
        with self.db:
            self.db.execute('INSERT OR IGNORE INTO accounts VALUES (?)', (account,))
            self.db.execute('INSERT OR IGNORE INTO threads(account,thread_key) VALUES (?,?)', (account,thread_key))
            thread = self.db.execute('SELECT id FROM threads WHERE account=? AND thread_key=?', (account,thread_key)).fetchone()[0]
            existing = self.db.execute('SELECT id FROM messages WHERE account=? AND raw_sha256=?', (account,digest)).fetchone()
            if existing:
                message_id = existing[0]
            else:
                cur = self.db.execute('INSERT INTO messages(account,thread_id,raw_sha256,message_id,subject,sender,recipients,date,body,html,sensitive,tainted,taint_reasons) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)', (account,thread,digest,mid,subject,sender,recipients,date,body,html_body,int(sensitive),int(bool(reasons)),json.dumps(reasons)))
                message_id = cur.lastrowid
                for filename, content_type, data in attachments:
                    sha = self._blob(data)
                    self.db.execute('INSERT INTO attachments(message_id,filename,content_type,sha256,size) VALUES (?,?,?,?,?)', (message_id,filename,content_type,sha,len(data)))
                self.db.execute('INSERT INTO messages_fts(rowid,subject,sender,body) VALUES (?,?,?,?)', (message_id,subject,sender,body or re.sub('<[^>]*>', ' ', html_body)))
                action = 'ingest.redacted' if sensitive else 'ingest.tainted' if reasons else 'ingest'
                self.db.execute('INSERT INTO audit(action,message_id) VALUES (?,?)', (action,message_id))
            if mid:
                for parent_mid in parent_ids:
                    self.db.execute('INSERT OR IGNORE INTO reply_edges VALUES (?,?,?)', (account, mid, parent_mid))
                self._reconcile_threads(account, mid)
            if sent:
                self.db.execute('UPDATE messages SET is_sent=1 WHERE id=?', (message_id,))
                for parent_mid in parent_ids:
                    self.db.execute('INSERT OR IGNORE INTO sent_replies VALUES (?,?)', (account, parent_mid))
            if uid:
                location = (account,str(folder),str(uidvalidity),str(uid))
                old = self.db.execute('SELECT message_id FROM message_locations WHERE account=? AND folder=? AND uidvalidity=? AND uid=?',location).fetchone()
                if old and old[0] != message_id:
                    raise ValueError('IMAP UID maps to different content')
                self.db.execute('INSERT OR IGNORE INTO message_locations VALUES (?,?,?,?,?)', (*location,message_id))
        return self.get_message(message_id)

    def _reconcile_threads(self, account, mid):
        # UNION terminates cycles; missing parents are nodes too, so siblings join.
        rows = self.db.execute("""WITH RECURSIVE connected(mid) AS (
            SELECT ? UNION SELECT CASE WHEN e.child_mid=c.mid THEN e.parent_mid ELSE e.child_mid END
            FROM reply_edges e JOIN connected c ON e.child_mid=c.mid OR e.parent_mid=c.mid
            WHERE e.account=?)
            SELECT DISTINCT thread_id FROM messages WHERE account=? AND message_id IN (SELECT mid FROM connected)
            ORDER BY thread_id""", (mid, account, account)).fetchall()
        ids = [r[0] for r in rows]
        if len(ids) < 2:
            return
        slots = ','.join('?' for _ in ids)
        drafted = []
        if self.db.execute("SELECT 1 FROM sqlite_master WHERE name='wf_draft_versions'").fetchone():
            drafted = [r[0] for r in self.db.execute(
                'SELECT DISTINCT thread_id FROM wf_draft_versions WHERE thread_id IN (' + slots + ')', ids)]
        if len(drafted) > 1:
            raise ValueError('Thread merge requires review: multiple draft histories; import rolled back')
        target = drafted[0] if drafted else ids[0]
        # Preserve the draft-bearing thread id and its immutable revision history.
        self.db.execute('UPDATE messages SET thread_id=? WHERE thread_id IN (' + slots + ')', [target, *ids])
        self.db.execute("INSERT INTO audit(action) VALUES ('thread.reconcile')")

    def get_message(self, message_id):
        row = self.db.execute('SELECT * FROM messages WHERE id=?', (message_id,)).fetchone()
        if row is None:
            return None
        result = dict(row)
        result['attachments'] = [dict(r) for r in self.db.execute('SELECT * FROM attachments WHERE message_id=?',(message_id,))]
        result['taint_reasons'] = json.loads(result['taint_reasons'])
        return result

    def list_messages(self, limit=50):
        return [dict(r) for r in self.db.execute('SELECT * FROM messages ORDER BY id DESC LIMIT ?', (max(0,min(int(limit),1000)),))]

    def search(self, query, limit=50):
        words = re.findall(r'\w+', query, re.UNICODE)
        if not words:
            return []
        literal = ' AND '.join('"' + w + '"' for w in words)
        return [dict(r) for r in self.db.execute('SELECT m.* FROM messages m JOIN messages_fts f ON m.id=f.rowid WHERE messages_fts MATCH ? ORDER BY rank LIMIT ?', (literal,max(0,min(int(limit),1000))))]

    def assign_case(self, message_id, title):
        check_content(title)
        title = title.strip()
        if not title or len(title) > 500:
            raise ValueError('Case title required (maximum 500 characters)')
        if self.get_message(message_id) is None:
            raise ValueError('Unknown message')
        with self.db:
            self.db.execute('INSERT OR IGNORE INTO cases(title) VALUES (?)',(title,))
            case = dict(self.db.execute('SELECT * FROM cases WHERE title=?',(title,)).fetchone())
            self.db.execute('UPDATE messages SET case_id=? WHERE id=?',(case['id'],message_id))
            self.db.execute('INSERT INTO audit(action,message_id) VALUES (?,?)',('assign_case',message_id))
        return case

    def list_cases(self):
        return [dict(r) for r in self.db.execute('SELECT c.*, count(m.id) AS message_count FROM cases c LEFT JOIN messages m ON c.id=m.case_id GROUP BY c.id ORDER BY c.id DESC')]

    def audit(self, limit=100):
        return [dict(r) for r in self.db.execute('SELECT * FROM audit ORDER BY id DESC LIMIT ?', (max(0,min(int(limit),1000)),))]


def encode_mailbox(folder):
    """IMAP4rev1 modified UTF-7 (RFC 3501 section 5.1.3)."""
    if not folder or any(ord(c) < 32 or ord(c) == 127 for c in folder):
        raise ValueError('Invalid IMAP mailbox name')
    def shifted(match):
        encoded = base64.b64encode(match.group().encode('utf-16-be')).decode('ascii')
        return '&' + encoded.rstrip('=').replace('/', ',') + '-'
    return re.sub(r'[^\x20-\x7e]+', shifted, folder.replace('&', '&-'))


def sync_imap(store, account, host='imap.gmail.com', folder='INBOX', limit=100, sent=False):
    """Fetch a bounded read-only snapshot. Never STORE, EXPUNGE, CLOSE or send."""
    folder_wire = encode_mailbox(folder)
    quoted_folder = '"' + folder_wire.replace('\\', '\\\\').replace('"', '\\"') + '"'
    import keyring
    password = keyring.get_password('aiwb/gmail', account)
    if not password:
        raise ValueError('Mail credential missing from keyring')
    client = imaplib.IMAP4_SSL(host, ssl_context=ssl.create_default_context(), timeout=30)
    result = {'fetched': 0, 'stored': 0, 'redacted': 0, 'tainted': 0}
    try:
        status, _ = client.login(account,password)
        del password
        if status != 'OK':
            raise RuntimeError('IMAP login failed')
        status, _ = client.select(quoted_folder, readonly=True)
        if status != 'OK':
            raise RuntimeError('IMAP folder unavailable')
        _, validity = client.response('UIDVALIDITY')
        uidvalidity = (validity[0].decode('ascii') if validity and validity[0] else '')
        if not uidvalidity:
            raise RuntimeError('IMAP UIDVALIDITY unavailable')
        status, rows = client.uid('search', None, 'ALL')
        if status != 'OK':
            raise RuntimeError('IMAP search failed')
        maximum = max(0,min(int(limit),10000))
        uids = (rows[0] or b'').split()[-maximum:] if maximum else []
        for uid in uids:
            status, parts = client.uid('fetch',uid,'(BODY.PEEK[])')
            if status != 'OK':
                raise RuntimeError('IMAP fetch failed')
            chunks = [p[1] for p in parts if isinstance(p,tuple) and isinstance(p[1],bytes)]
            if len(chunks) != 1:
                raise RuntimeError('Unexpected IMAP message response')
            result['fetched'] += 1
            row = store.ingest(account,chunks[0],folder,uid.decode('ascii'),uidvalidity, sent=sent)
            result['stored'] += 1
            result['redacted'] += row['sensitive']
            result['tainted'] += row['tainted']
        return result
    except imaplib.IMAP4.error:
        raise RuntimeError('IMAP operation failed; check credentials and account access') from None
    finally:
        try:
            client.logout()
        except (OSError,imaplib.IMAP4.error):
            pass



def sync_account(store, account, sent_folder='[Gmail]/Sent Mail', limit=100):
    """Gmail snapshot includes Sent. Explicit override supports localized folders.

    Completion of both snapshots is required; an unavailable Sent folder fails.
    This bounded snapshot is not evidence that the entire mailbox was checked.
    """
    sent = sync_imap(store, account, folder=sent_folder, limit=limit, sent=True)
    inbox = sync_imap(store, account, limit=limit)
    return dict(inbox=inbox, sent=sent, complete_mailbox=False)
