"""Local case, promise and draft workflow. No network or sending capabilities."""
import json
import re
from mailstore import check_content
from datetime import datetime, timezone
from email import policy
from email.parser import BytesParser
from email.utils import getaddresses, parseaddr, parsedate_to_datetime

STATES = ('inbox', 'waiting_them', 'your_turn', 'closed')
_UNSET = object()


def _date(value):
    if value is None or value == '':
        return None
    if isinstance(value, datetime):
        parsed = value
    elif re.fullmatch(r'\d{4}-\d{2}-\d{2}', str(value)):
        parsed = datetime.fromisoformat(value).replace(tzinfo=timezone.utc)
    else:
        parsed = datetime.fromisoformat(str(value).replace('Z', '+00:00'))
    if parsed.tzinfo is None:
        raise ValueError('Timestamp must include a timezone')
    return parsed.astimezone(timezone.utc).isoformat()


def critic(body, subject='', attachments=()):
    """Return advisory findings; never approve or send a message."""
    text = subject + '\n' + body
    findings = []
    def add(code, severity, message):
        findings.append(dict(code=code, severity=severity, message=message))
    if re.search(r'❓|\{\{.*?\}\}|\[\[.*?\]\]|\b(?:TODO|TBD)\b|@(?:co|со)\b', text, re.I | re.S):
        add('placeholder', 'block', 'Draft contains unfinished markers or editor instructions.')
    if not attachments and re.search(r'\b(?:attached|attachment|enclosed|bijlage|bijgevoegd)\b|прилагаю|во вложении|прикрепл', text, re.I):
        add('missing_attachment', 'block', 'Text mentions an attachment, but none is selected.')
    if re.search(r'[€$£]|\b(?:EUR|USD|GBP|payment|invoice|bank|IBAN|betaling|factuur)\b|оплат|платёж|платеж|деньг|счёт|счет', text, re.I):
        add('financial', 'review', 'Financial content requires human review.')
    if re.search(r'\b(?:legal|lawyer|contract|settlement|liability|lawsuit|advocaat|rechtbank|overeenkomst)\b|юрид|адвокат|договор|обязательств|суд', text, re.I):
        add('legal', 'review', 'Legal content requires human review.')
    return findings


TRUSTED_AUTHSERV = ('mx.google.com',)


def _strip_comments(text):
    # RFC 5322 comments may nest; drop them and quoted strings before parsing.
    text = re.sub(r'"(?:[^"\\]|\\.)*"', ' ', text)
    out, depth = [], 0
    for char in text:
        if char == '(':
            depth += 1
        elif char == ')' and depth:
            depth -= 1
        elif not depth:
            out.append(char)
    return ''.join(out)


def sender_authenticated(raw, host, trusted=TRUSTED_AUTHSERV):
    """True when the receiving provider's Authentication-Results show a pass aligned with host.

    Only the topmost header counts and only when its authserv-id is a trusted
    receiving provider (RFC 8601: it removes forged headers with its own id).
    Each result is parsed separately: DMARC pass with header.from, DKIM pass with
    header.d/header.i, or SPF pass with smtp.mailfrom, aligned with host.
    """
    if not host:
        return False
    headers = BytesParser(policy=policy.compat32).parsebytes(raw, headersonly=True).get_all('authentication-results') or []
    if not headers:
        return False
    parts = [p.strip() for p in _strip_comments(re.sub(r'\s+', ' ', str(headers[0]))).lower().split(';')]
    if parts[0].split(' ')[0] not in trusted:
        return False
    def aligned(domain):
        domain = domain.strip('.').rpartition('@')[2]
        return bool(domain) and (domain == host or domain.endswith('.' + host) or host.endswith('.' + domain))
    wanted = {'dmarc': ('header.from',), 'dkim': ('header.d', 'header.i'), 'spf': ('smtp.mailfrom',)}
    for resinfo in parts[1:]:
        tokens = resinfo.split()
        if not tokens or '=' not in tokens[0]:
            continue
        method, _, result = tokens[0].partition('=')
        if method not in wanted or result != 'pass':
            continue
        props = dict(t.split('=', 1) for t in tokens[1:] if '=' in t)
        if any(aligned(props[k]) for k in wanted[method] if k in props):
            return True
    return False


class Workflow:
    def __init__(self, store):
        self.store, self.db = store, store.db
        self.db.executescript('''
CREATE TABLE IF NOT EXISTS wf_case_state(case_id INTEGER PRIMARY KEY REFERENCES cases(id), state TEXT NOT NULL CHECK(state IN ('inbox','waiting_them','your_turn','closed')), updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS wf_promises(id INTEGER PRIMARY KEY, case_id INTEGER NOT NULL REFERENCES cases(id), message_id INTEGER REFERENCES messages(id), title TEXT NOT NULL, owner TEXT NOT NULL CHECK(owner IN ('me','them')), due_at TEXT, status TEXT NOT NULL DEFAULT 'open' CHECK(status IN ('open','resolved','cancelled')), created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS wf_draft_versions(id INTEGER PRIMARY KEY, thread_id INTEGER NOT NULL REFERENCES threads(id), version INTEGER NOT NULL, parent_id INTEGER REFERENCES wf_draft_versions(id), subject TEXT NOT NULL, body TEXT NOT NULL, attachments_json TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, UNIQUE(thread_id,version));
CREATE TABLE IF NOT EXISTS wf_draft_heads(thread_id INTEGER PRIMARY KEY REFERENCES threads(id), version_id INTEGER NOT NULL REFERENCES wf_draft_versions(id));
''')
        self.db.commit()

    def _exists(self, table, value):
        if not self.db.execute('SELECT id FROM ' + table + ' WHERE id=?', (value,)).fetchone():
            raise ValueError('Unknown ' + table + ' id')

    def _audit(self, action):
        self.db.execute('INSERT INTO audit(action) VALUES (?)', (action,))

    def case_state(self, case_id):
        self._exists('cases', case_id)
        row = self.db.execute('SELECT state FROM wf_case_state WHERE case_id=?', (case_id,)).fetchone()
        return row['state'] if row else 'inbox'

    def transition_case(self, case_id, state):
        self._exists('cases', case_id)
        if state not in STATES:
            raise ValueError('Invalid case state')
        with self.db:
            self.db.execute('INSERT INTO wf_case_state(case_id,state) VALUES (?,?) ON CONFLICT(case_id) DO UPDATE SET state=excluded.state,updated_at=CURRENT_TIMESTAMP', (case_id, state))
            self._audit('workflow.case:' + str(case_id) + ':' + state)
        return self.case_state(case_id)

    def create_promise(self, case_id, title, owner='me', due_at=None, message_id=None):
        self._exists('cases', case_id)
        self._validate_promise(title, owner)
        if message_id is not None:
            row = self.db.execute('SELECT case_id FROM messages WHERE id=?', (message_id,)).fetchone()
            if not row or row['case_id'] != case_id:
                raise ValueError('Message must belong to the case')
        with self.db:
            cursor = self.db.execute('INSERT INTO wf_promises(case_id,title,owner,due_at,message_id) VALUES (?,?,?,?,?)', (case_id, title.strip(), owner, _date(due_at), message_id))
            self._audit('workflow.promise.create:' + str(cursor.lastrowid))
        return self.get_promise(cursor.lastrowid)

    @staticmethod
    def _validate_promise(title, owner):
        if not isinstance(title, str) or not title.strip() or len(title) > 2000:
            raise ValueError('Promise title required, maximum 2000 characters')
        check_content(title)
        if owner not in ('me', 'them'):
            raise ValueError('Owner must be me or them')

    def get_promise(self, promise_id):
        row = self.db.execute('SELECT * FROM wf_promises WHERE id=?', (promise_id,)).fetchone()
        if not row:
            raise ValueError('Unknown promise')
        return dict(row)

    def update_promise(self, promise_id, title=_UNSET, owner=_UNSET, due_at=_UNSET):
        old = self.get_promise(promise_id)
        title = old['title'] if title is _UNSET else title
        owner = old['owner'] if owner is _UNSET else owner
        due_at = old['due_at'] if due_at is _UNSET else _date(due_at)
        self._validate_promise(title, owner)
        with self.db:
            self.db.execute('UPDATE wf_promises SET title=?,owner=?,due_at=?,updated_at=CURRENT_TIMESTAMP WHERE id=?', (title.strip(), owner, due_at, promise_id))
            self._audit('workflow.promise.update:' + str(promise_id))
        return self.get_promise(promise_id)

    def _promise_status(self, promise_id, status):
        self.get_promise(promise_id)
        with self.db:
            self.db.execute('UPDATE wf_promises SET status=?,updated_at=CURRENT_TIMESTAMP WHERE id=?', (status, promise_id))
            self._audit('workflow.promise.' + status + ':' + str(promise_id))
        return self.get_promise(promise_id)

    def resolve_promise(self, promise_id):
        return self._promise_status(promise_id, 'resolved')

    def delete_promise(self, promise_id):
        """Cancel while retaining the audit trail."""
        return self._promise_status(promise_id, 'cancelled')

    def list_promises(self, case_id=None, status='open'):
        if status not in (None, 'open', 'resolved', 'cancelled'):
            raise ValueError('Invalid promise status')
        clauses, params = [], []
        if case_id is not None:
            clauses.append('case_id=?'); params.append(case_id)
        if status is not None:
            clauses.append('status=?'); params.append(status)
        sql = 'SELECT * FROM wf_promises' + (' WHERE ' + ' AND '.join(clauses) if clauses else '') + ' ORDER BY due_at IS NULL,due_at,id'
        return [dict(r) for r in self.db.execute(sql, params)]

    def already_answered(self, thread_id):
        """Use Gmail milliseconds, otherwise RFC Date (timezone-less means UTC).

        Missing/invalid dates have unknown chronology: an unanswered such message
        keeps drafting available. Equal latest timestamps also remain answerable
        if any incoming at that timestamp is unanswered. Neither rule uses ingest id.
        """
        has_gmail = self.db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='gmail_messages'").fetchone()
        incoming = []
        for row in self.db.execute('SELECT * FROM messages WHERE thread_id=? AND is_sent=0', (thread_id,)):
            stamp = None
            if has_gmail:
                dates = self.db.execute('SELECT internal_date FROM gmail_messages WHERE account=? AND message_id=? AND internal_date IS NOT NULL', (row['account'], row['id']))
                # Multiple provider mappings use their greatest valid timestamp.
                for date in dates:
                    if re.fullmatch(r'-?\d+', str(date[0])):
                        value = int(date[0])
                        stamp = value if stamp is None else max(stamp, value)
            if stamp is None:
                try:
                    parsed = parsedate_to_datetime(row['date'])
                    if parsed.tzinfo is None:
                        parsed = parsed.replace(tzinfo=timezone.utc)
                    delta = parsed.astimezone(timezone.utc) - datetime(1970, 1, 1, tzinfo=timezone.utc)
                    stamp = (delta.days * 86400 + delta.seconds) * 1000 + delta.microseconds // 1000
                except (TypeError, ValueError, IndexError, OverflowError):
                    pass
            answered = bool(self.db.execute('SELECT 1 FROM sent_replies WHERE account=? AND parent_mid=?', (row['account'], row['message_id'])).fetchone())
            incoming.append((stamp, answered))
        if not incoming or any(stamp is None and not answered for stamp, answered in incoming):
            return False
        latest = max((stamp for stamp, _ in incoming if stamp is not None), default=None)
        return all(answered for stamp, answered in incoming if stamp == latest)

    def tainted(self, thread_id):
        """read-guard flagged a message of the thread: acting on it needs an explicit yes."""
        return bool(self.db.execute('SELECT 1 FROM messages WHERE thread_id=? AND tainted=1', (thread_id,)).fetchone())

    def _draft(self, row):
        if row is None:
            return None
        result = dict(row)
        result['attachments'] = json.loads(result.pop('attachments_json'))
        result['findings'] = critic(result['body'], result['subject'], result['attachments'])
        if self.tainted(result['thread_id']):
            result['findings'].append(dict(code='untrusted_source', severity='review', message='The thread has a message flagged by read-guard; verify instructions and links in it.'))
        if self.already_answered(result['thread_id']):
            result['findings'].append(dict(code='already_answered', severity='block', message='A sent reply exists; review this draft before use.'))
        return result

    def get_draft(self, thread_id):
        return self._draft(self.db.execute('SELECT v.* FROM wf_draft_versions v JOIN wf_draft_heads h ON h.version_id=v.id WHERE h.thread_id=?', (thread_id,)).fetchone())

    def save_draft(self, thread_id, body, subject='', attachments=(), expected_version=None, confirm_untrusted=False):
        self._exists('threads', thread_id)
        if self.tainted(thread_id) and not confirm_untrusted:
            raise ValueError('Thread contains a message flagged by read-guard; drafting needs the owner\'s explicit yes')
        if not isinstance(body, str) or not isinstance(subject, str):
            raise ValueError('Draft body and subject must be strings')
        check_content(subject + '\n' + body)
        checked = []
        for attachment in attachments:
            name, digest = attachment.get('name'), attachment.get('sha256')
            if not isinstance(name, str) or not name.strip() or not isinstance(digest, str):
                raise ValueError('Attachment requires name and sha256')
            check_content(name + '\n' + self.store.read_blob(digest).decode('utf-8', errors='replace'))
            checked.append(dict(name=name, sha256=digest))
        with self.db:
            self.db.execute('BEGIN IMMEDIATE')
            if not self.db.execute('SELECT 1 FROM messages WHERE thread_id=?', (thread_id,)).fetchone():
                raise ValueError('Thread is obsolete or empty; reload messages before drafting')
            if self.already_answered(thread_id):
                raise ValueError('Latest incoming message already has a sent reply; review before drafting')
            current = self.get_draft(thread_id)
            if expected_version is not None and expected_version != (current['version'] if current else 0):
                raise ValueError('Draft changed; reload before saving')
            version = self.db.execute('SELECT COALESCE(MAX(version),0)+1 FROM wf_draft_versions WHERE thread_id=?', (thread_id,)).fetchone()[0]
            cur = self.db.execute('INSERT INTO wf_draft_versions(thread_id,version,parent_id,subject,body,attachments_json) VALUES (?,?,?,?,?,?)', (thread_id, version, current['id'] if current else None, subject, body, json.dumps(checked)))
            self.db.execute('INSERT INTO wf_draft_heads(thread_id,version_id) VALUES (?,?) ON CONFLICT(thread_id) DO UPDATE SET version_id=excluded.version_id', (thread_id, cur.lastrowid))
            self._audit('workflow.draft.save:' + str(cur.lastrowid))
        return self.get_draft(thread_id)

    def draft_history(self, thread_id):
        return [self._draft(row) for row in self.db.execute('SELECT * FROM wf_draft_versions WHERE thread_id=? ORDER BY version DESC', (thread_id,))]

    def undo_draft(self, thread_id, expected_version=None):
        with self.db:
            self.db.execute('BEGIN IMMEDIATE')
            current = self.get_draft(thread_id)
            if expected_version is not None and expected_version != (current['version'] if current else 0):
                raise ValueError('Draft changed; reload before undo')
            if current:
                if current['parent_id']:
                    self.db.execute('UPDATE wf_draft_heads SET version_id=? WHERE thread_id=?', (current['parent_id'], thread_id))
                else:
                    self.db.execute('DELETE FROM wf_draft_heads WHERE thread_id=?', (thread_id,))
                self._audit('workflow.draft.undo:' + str(current['id']))
        return self.get_draft(thread_id)

    def spam_rescue(self, account, folder='[Gmail]/Spam'):
        """Section 6: messages of the spam folder that the owner's context says are wanted.

        Read only: nothing is moved at the provider. Tainted (read-guard) and redacted
        authentication messages stay where they are. Context: the owner wrote to the
        sender, the sender's domain is in an open case, or the case awaits their answer.
        """
        def address(value):
            return parseaddr(value)[1].lower()
        def domain(value):
            return address(value).rpartition('@')[2]
        others = 'SELECT message_id FROM message_locations WHERE account=? AND folder<>?'
        wrote_to = set()
        for (recipients,) in self.db.execute('SELECT recipients FROM messages WHERE account=? AND is_sent=1', (account,)):
            wrote_to.update(a.lower() for _, a in getaddresses([recipients]) if a)
        known = {address(r[0]) for r in self.db.execute('SELECT sender FROM messages WHERE account=? AND is_sent=0 AND tainted=0 AND sensitive=0 AND (id IN (' + others + ') OR id NOT IN (SELECT message_id FROM message_locations))', (account, account, folder))}
        open_domains, awaited = set(), set()
        for row in self.db.execute("""SELECT m.sender, EXISTS(SELECT 1 FROM wf_promises p WHERE p.case_id=m.case_id AND p.owner='them' AND p.status='open') AS awaited
            FROM messages m LEFT JOIN wf_case_state s ON s.case_id=m.case_id
            WHERE m.account=? AND m.case_id IS NOT NULL AND m.is_sent=0 AND COALESCE(s.state,'inbox')<>'closed'
              AND m.id NOT IN (SELECT message_id FROM message_locations WHERE account=? AND folder=?)""", (account, account, folder)):
            open_domains.add(domain(row['sender']))
            if row['awaited']:
                awaited.add(domain(row['sender']))
        public = {'gmail.com', 'googlemail.com', 'outlook.com', 'hotmail.com', 'live.com', 'yahoo.com', 'icloud.com', 'proton.me', 'protonmail.com', 'gmx.com', 'telenet.be', 'skynet.be'}
        rescued, unverified = [], []
        for row in self.db.execute('SELECT DISTINCT m.* FROM messages m JOIN message_locations l ON l.message_id=m.id WHERE m.account=? AND l.folder=? AND m.tainted=0 AND m.sensitive=0 AND m.is_sent=0 ORDER BY m.id', (account, folder)):
            sender, host = address(row['sender']), domain(row['sender'])
            reasons = []
            if sender in wrote_to:
                reasons.append('you_wrote_to_sender')
            if sender in known:
                reasons.append('known_correspondent')
            # A shared mailbox provider says nothing about the case: match whole domains only.
            if host and host not in public:
                if host in awaited:
                    reasons.append('awaited_reply')
                if host in open_domains:
                    reasons.append('open_case_domain')
            if reasons:
                item = dict(id=row['id'], thread_id=row['thread_id'], sender=row['sender'], subject=row['subject'], date=row['date'], reasons=reasons)
                # Spoofed From is the classic spam-folder phish: rescue only an authenticated sender.
                (rescued if sender_authenticated(self.store.read_blob(row['raw_sha256']), host) else unverified).append(item)
        report = 'rescued %d: ' % len(rescued) + '; '.join('%s — %s' % (r['sender'], r['subject']) for r in rescued) if rescued else 'rescued 0'
        with self.db:
            self._audit('workflow.spam_rescue:%d' % len(rescued))
        return dict(folder=folder, rescued=rescued, unverified=unverified, report=report)

    def briefing(self, now=None, accounts=None, lang='en', days=14, stale_days=7, spam_folder='[Gmail]/Spam'):
        """Morning briefing; see briefing.morning. Keeps the earlier keys (cases, promises, overdue, drafts)."""
        from briefing import morning
        now = _date(now or datetime.now(timezone.utc))
        cases = {state: [] for state in STATES}
        for row in self.db.execute("SELECT c.*,COALESCE(s.state,'inbox') AS state FROM cases c LEFT JOIN wf_case_state s ON s.case_id=c.id ORDER BY c.id"):
            if accounts is None or self.db.execute('SELECT 1 FROM messages WHERE case_id=? AND account IN (' + ','.join('?' for _ in accounts) + ')', [row['id'], *accounts]).fetchone():
                cases[row['state']].append(dict(row))
        result = morning(self, now, accounts=accounts, days=days, stale_days=stale_days, spam_folder=spam_folder, lang=lang)
        result['cases'] = cases
        return result

