"""Local case, promise and draft workflow. No network or sending capabilities."""
import json
import re
from mailstore import check_content
from datetime import datetime, timezone

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
        latest = self.db.execute("SELECT * FROM messages WHERE thread_id=? AND is_sent=0 ORDER BY id DESC LIMIT 1", (thread_id,)).fetchone()
        return bool(latest and self.db.execute('SELECT 1 FROM sent_replies WHERE account=? AND parent_mid=?', (latest['account'], latest['message_id'])).fetchone())

    def _draft(self, row):
        if row is None:
            return None
        result = dict(row)
        result['attachments'] = json.loads(result.pop('attachments_json'))
        result['findings'] = critic(result['body'], result['subject'], result['attachments'])
        if self.already_answered(result['thread_id']):
            result['findings'].append(dict(code='already_answered', severity='block', message='A sent reply exists; review this draft before use.'))
        return result

    def get_draft(self, thread_id):
        return self._draft(self.db.execute('SELECT v.* FROM wf_draft_versions v JOIN wf_draft_heads h ON h.version_id=v.id WHERE h.thread_id=?', (thread_id,)).fetchone())

    def save_draft(self, thread_id, body, subject='', attachments=(), expected_version=None):
        self._exists('threads', thread_id)
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

    def briefing(self, now=None):
        now = _date(now or datetime.now(timezone.utc))
        cases = {state: [] for state in STATES}
        for row in self.db.execute("SELECT c.*,COALESCE(s.state,'inbox') AS state FROM cases c LEFT JOIN wf_case_state s ON s.case_id=c.id ORDER BY c.id"):
            cases[row['state']].append(dict(row))
        promises = self.list_promises()
        for promise in promises:
            promise['overdue'] = bool(promise['due_at'] and datetime.fromisoformat(promise['due_at']) < datetime.fromisoformat(now))
        drafts = [self._draft(row) for row in self.db.execute('SELECT v.* FROM wf_draft_versions v JOIN wf_draft_heads h ON h.version_id=v.id ORDER BY v.id')]
        return dict(as_of=now, cases=cases, promises=promises, overdue=[p for p in promises if p['overdue']], drafts=drafts)

