"""Full mailbox synchronization through the Gmail API (read only).

First run: every message of the last N years (spam and trash included) is fetched as raw
RFC822 into the Store; then the mailbox historyId taken BEFORE the listing is kept and
every next run applies only the changes since it (history.list). An expired historyId
(HTTP 404) falls back to a full listing that skips messages already archived.
Scope gmail.readonly: nothing is ever modified, sent or deleted at the provider.
Credentials come from one JSON file per person ({client_id, client_secret, refresh_token,
account}), never from the owner's keyring and never from arguments.
"""
from __future__ import annotations
import base64
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

API = 'https://gmail.googleapis.com/gmail/v1/users/me/'
TOKEN_URL = 'https://oauth2.googleapis.com/token'
SCOPE_READ = 'https://www.googleapis.com/auth/gmail.readonly'
WORKERS = 8
PAGE = 500


class TokenSource:
    """Access tokens from a per-person credential file (systemd LoadCredentialEncrypted on the hub)."""

    def __init__(self, path=None, request=None, cred=None):
        self._request = request or _http
        self._token, self._until = None, 0.0
        cred = cred if cred is not None else json.loads(Path(path).read_text(encoding='utf-8'))
        # `account` is required for a mailbox; a bare Drive token (restore on the person's device) passes cred= with account ''
        needed = ('client_id', 'client_secret', 'refresh_token') + (('account',) if path else ())
        missing = [k for k in needed if not cred.get(k)]
        if missing:
            raise ValueError('Credential file incomplete: ' + ', '.join(missing))
        self.account = str(cred.get('account') or '').strip().lower()
        self._cred = cred

    def __call__(self):
        if self._token and time.time() < self._until - 120:
            return self._token
        body = urllib.parse.urlencode({'client_id': self._cred['client_id'], 'client_secret': self._cred['client_secret'],
                                       'refresh_token': self._cred['refresh_token'], 'grant_type': 'refresh_token'}).encode()
        status, data = self._request('POST', TOKEN_URL, {'Content-Type': 'application/x-www-form-urlencoded'}, body)
        if status != 200 or not isinstance(data, dict) or not data.get('access_token'):
            # invalid_grant: revoked, password change or a Testing-mode OAuth app (7-day refresh tokens)
            reason = data.get('error') if isinstance(data, dict) else ''
            raise PermissionError(f'Gmail token refresh failed ({status} {reason}); the person must connect again')
        self._token, self._until = data['access_token'], time.time() + int(data.get('expires_in', 3600))
        return self._token


def _http(method, url, headers, body=None, timeout=60):
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            status = resp.status
    except urllib.error.HTTPError as err:
        raw, status = err.read(), err.code
    try:
        return status, json.loads(raw) if raw else {}
    except ValueError:
        return status, {}


class Gmail:
    def __init__(self, token, request=None, sleep=time.sleep):
        self.token, self._request, self._sleep = token, request or _http, sleep

    def get(self, path, **params):
        url = API + path + ('?' + urllib.parse.urlencode(params, doseq=True) if params else '')
        for attempt in range(7):
            status, data = self._request('GET', url, {'Authorization': 'Bearer ' + self.token()})
            if status == 200:
                return data
            if status == 404:
                raise LookupError(path.split('?')[0])
            if status in (429, 500, 502, 503, 504) or (status == 403 and _rate_limited(data)):
                self._sleep(min(64, 2 ** attempt))
                continue
            raise RuntimeError(f'Gmail API {status} on {path.split("/")[0]}')
        raise RuntimeError('Gmail API keeps refusing (rate limit)')

    def profile(self):
        return self.get('profile')

    def message_ids(self, query):
        token = None
        while True:
            params = {'q': query, 'includeSpamTrash': 'true', 'maxResults': PAGE}
            if token:
                params['pageToken'] = token
            data = self.get('messages', **params)
            for item in data.get('messages', []):
                yield item['id']
            token = data.get('nextPageToken')
            if not token:
                return

    def raw(self, gid):
        data = self.get('messages/' + urllib.parse.quote(gid, safe=''), format='raw')
        data['raw'] = base64.urlsafe_b64decode(data['raw'] + '=' * (-len(data['raw']) % 4))
        return data

    def labels(self, gid):
        try:
            return self.get('messages/' + urllib.parse.quote(gid, safe=''), format='minimal').get('labelIds', [])
        except LookupError:
            return None

    def history(self, start):
        token, latest = None, start
        while True:
            params = {'startHistoryId': start, 'maxResults': PAGE,
                      'historyTypes': ['messageAdded', 'messageDeleted', 'labelAdded', 'labelRemoved']}
            if token:
                params['pageToken'] = token
            data = self.get('history', **params)
            latest = data.get('historyId', latest)
            for record in data.get('history', []):
                yield record, None
            token = data.get('nextPageToken')
            if not token:
                yield None, latest
                return


def _rate_limited(data):
    errors = (data.get('error') or {}).get('errors') or [] if isinstance(data, dict) else []
    return any(e.get('reason') in ('rateLimitExceeded', 'userRateLimitExceeded') for e in errors)


def _schema(store):
    store.db.executescript('''
    CREATE TABLE IF NOT EXISTS gmail_state(account TEXT PRIMARY KEY, history_id TEXT, pending_history_id TEXT,
        since TEXT, full_done TEXT, last_sync TEXT, last_error TEXT);
    CREATE TABLE IF NOT EXISTS gmail_messages(account TEXT NOT NULL, gmail_id TEXT NOT NULL, thread_id TEXT NOT NULL,
        message_id INTEGER NOT NULL REFERENCES messages(id), labels TEXT NOT NULL DEFAULT '[]', internal_date INTEGER,
        gone INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(account, gmail_id));
    CREATE INDEX IF NOT EXISTS gmail_messages_msg ON gmail_messages(message_id);
    CREATE INDEX IF NOT EXISTS gmail_messages_thread ON gmail_messages(account, thread_id);
    CREATE TABLE IF NOT EXISTS gmail_failed(account TEXT NOT NULL, gmail_id TEXT NOT NULL, reason TEXT NOT NULL,
        tries INTEGER NOT NULL DEFAULT 1, PRIMARY KEY(account, gmail_id));
    ''')
    if 'reconcile' not in {r[1] for r in store.db.execute('PRAGMA table_info(gmail_state)')}:
        store.db.execute('ALTER TABLE gmail_state ADD COLUMN reconcile INTEGER NOT NULL DEFAULT 0')
    store.db.commit()


def _state(store, account):
    row = store.db.execute('SELECT * FROM gmail_state WHERE account=?', (account,)).fetchone()
    return dict(row) if row else {}


def _set_state(store, account, **values):
    with store.db:
        store.db.execute('INSERT OR IGNORE INTO gmail_state(account) VALUES (?)', (account,))
        for key, value in values.items():
            store.db.execute(f'UPDATE gmail_state SET {key}=? WHERE account=?', (value, account))


def _known(store, account):
    return {r[0] for r in store.db.execute('SELECT gmail_id FROM gmail_messages WHERE account=?', (account,))}


def _fetch_and_store(store, gmail, account, ids, result, progress=None):
    """Raw downloads run in parallel; SQLite writes stay on this thread."""
    ids = list(ids)
    with ThreadPoolExecutor(WORKERS) as pool:
        for start in range(0, len(ids), WORKERS * 25):
            chunk = ids[start:start + WORKERS * 25]
            for gid, data in zip(chunk, pool.map(_safe_raw(gmail), chunk)):
                if data is None:
                    result['vanished'] += 1  # deleted between listing and download
                    continue
                labels = data.get('labelIds', [])
                if 'DRAFT' in labels:
                    result['drafts_skipped'] = result.get('drafts_skipped', 0) + 1
                    continue
                try:
                    row = store.ingest(account, data['raw'], sent='SENT' in labels)
                except ValueError as err:
                    # e.g. a thread merge that needs review: kept aside and retried on every run, the rest goes on
                    with store.db:
                        store.db.execute('INSERT INTO gmail_failed(account,gmail_id,reason) VALUES (?,?,?) ON CONFLICT DO UPDATE SET tries=tries+1, reason=excluded.reason',
                                         (account, gid, str(err)[:200]))
                    result['failed'] = result.get('failed', 0) + 1
                    continue
                with store.db:
                    store.db.execute('DELETE FROM gmail_failed WHERE account=? AND gmail_id=?', (account, gid))
                    store.db.execute('INSERT OR REPLACE INTO gmail_messages(account,gmail_id,thread_id,message_id,labels,internal_date,gone) VALUES (?,?,?,?,?,?,0)',
                                     (account, gid, data.get('threadId', ''), row['id'], json.dumps(sorted(labels)), int(data.get('internalDate') or 0)))
                result['stored'] += 1
                result['redacted'] += row['sensitive']
                result['tainted'] += row['tainted']
            if progress:
                progress(result)


def _safe_raw(gmail):
    def fetch(gid):
        try:
            return gmail.raw(gid)
        except LookupError:
            return None
    return fetch


def sync_gmail(store, token, years=2, request=None, progress=None, now=None):
    """One synchronization run for the account of the token. Returns counters."""
    _schema(store)
    account = token.account
    gmail = Gmail(token, request)
    result = {'account': account, 'mode': '', 'stored': 0, 'redacted': 0, 'tainted': 0, 'vanished': 0,
              'deleted': 0, 'relabeled': 0}
    profile = gmail.profile()
    if profile.get('emailAddress', '').lower() != account:
        raise PermissionError('Token belongs to another mailbox')
    state = _state(store, account)
    if state.get('history_id'):
        try:
            _incremental(store, gmail, account, state['history_id'], result, progress)
            return result
        except LookupError:
            # historyId too old (about a week without sync): list everything again, skip what is archived
            # kept in the database until the reconciliation has run: an interrupted recovery resumes it
            _set_state(store, account, history_id=None, pending_history_id=None, reconcile=1)
            state = _state(store, account)
    # The historyId is taken before listing: changes made during a long first sync are replayed after it.
    pending = state.get('pending_history_id') or profile['historyId']
    since = state.get('since') or ((now or datetime.now(timezone.utc)) - timedelta(days=365 * years)).strftime('%Y/%m/%d')
    _set_state(store, account, pending_history_id=pending, since=since)
    result['mode'] = 'full'
    known = _known(store, account)
    listed = list(gmail.message_ids('after:' + since + ' -in:drafts'))
    ids = [gid for gid in listed if gid not in known]
    result['listed'] = len(listed)
    _fetch_and_store(store, gmail, account, ids, result, progress)
    if state.get('reconcile'):
        _reconcile(store, gmail, account, set(listed), known, since, result)
    stamp = datetime.now(timezone.utc).isoformat(timespec='seconds')
    _set_state(store, account, history_id=pending, pending_history_id=None, full_done=stamp, last_sync=stamp, last_error=None, reconcile=0)
    _incremental(store, gmail, account, pending, result, progress, retry=False)
    result['mode'] = 'full'
    return result


def _reconcile(store, gmail, account, listed, known, since, result):
    """After a history gap: deletions and label changes made meanwhile are taken from the mailbox itself."""
    floor = int(datetime.strptime(since, '%Y/%m/%d').replace(tzinfo=timezone.utc).timestamp() * 1000)
    window = {r[0] for r in store.db.execute('SELECT gmail_id FROM gmail_messages WHERE account=? AND gone=0 AND internal_date>=?', (account, floor))}
    # Older backfilled mail is outside the listing window: its absence proves nothing, so it is asked one by one.
    older = {r[0] for r in store.db.execute('SELECT gmail_id FROM gmail_messages WHERE account=? AND gone=0', (account,))} - window - listed
    present = sorted((known & listed) | older)
    with ThreadPoolExecutor(WORKERS) as pool:
        fresh = dict(zip(present, pool.map(gmail.labels, present)))
    with store.db:
        for gid in (window - listed) | {g for g, ids in fresh.items() if ids is None}:
            store.db.execute('UPDATE gmail_messages SET gone=1 WHERE account=? AND gmail_id=?', (account, gid))
            result['deleted'] += 1
        for gid, ids in fresh.items():
            if ids is not None:
                store.db.execute('UPDATE gmail_messages SET labels=?, gone=0 WHERE account=? AND gmail_id=?', (json.dumps(sorted(ids)), account, gid))
                result['relabeled'] += 1


def _incremental(store, gmail, account, start, result, progress, retry=True):
    result['mode'] = result['mode'] or 'history'
    added, labels, deleted, latest = {}, {}, set(), start
    for record, last in gmail.history(start):
        if record is None:
            latest = last
            break
        for item in record.get('messagesAdded', []):
            msg = item['message']
            added[msg['id']] = msg
            deleted.discard(msg['id'])
        for item in record.get('messagesDeleted', []):
            deleted.add(item['message']['id'])
            added.pop(item['message']['id'], None)
        for key in ('labelsAdded', 'labelsRemoved'):
            for item in record.get(key, []):
                # message.labelIds (when present) is the full current set; else apply the event's own delta in order
                msg = item['message']
                op = ('set', msg['labelIds']) if 'labelIds' in msg else (key, item.get('labelIds', []))
                labels.setdefault(msg['id'], []).append(op)
    known = _known(store, account)
    # Drafts are the person's unsent text that changes on every keystroke: synced in stage 4, not archived here.
    new = [gid for gid, msg in added.items() if gid not in known and 'DRAFT' not in msg.get('labelIds', [])]
    if retry:  # not right after a full listing that just tried them
        new += [r[0] for r in store.db.execute('SELECT gmail_id FROM gmail_failed WHERE account=?', (account,))
                if r[0] not in added and r[0] not in deleted]
    _fetch_and_store(store, gmail, account, new, result, progress)
    with store.db:
        for gid in deleted & known:
            store.db.execute('UPDATE gmail_messages SET gone=1 WHERE account=? AND gmail_id=?', (account, gid))
            result['deleted'] += 1
        for gid, ops in labels.items():
            if gid in known and gid not in deleted:
                current = set(json.loads(store.db.execute('SELECT labels FROM gmail_messages WHERE account=? AND gmail_id=?', (account, gid)).fetchone()[0]))
                for kind, ids in ops:
                    current = set(ids) if kind == 'set' else current | set(ids) if kind == 'labelsAdded' else current - set(ids)
                store.db.execute('UPDATE gmail_messages SET labels=? WHERE account=? AND gmail_id=?', (json.dumps(sorted(current)), account, gid))
                result['relabeled'] += 1
    stamp = datetime.now(timezone.utc).isoformat(timespec='seconds')
    _set_state(store, account, history_id=str(latest), last_sync=stamp, last_error=None)
    result['history_id'] = str(latest)


def backfill(store, token, before=None, query=None, request=None, progress=None):
    """Older mail on request: everything before a date (YYYY-MM-DD) or matching a Gmail query."""
    _schema(store)
    if not before and not query:
        raise ValueError('backfill needs --before or --query')
    gmail = Gmail(token, request)
    if gmail.profile().get('emailAddress', '').lower() != token.account:
        raise PermissionError('Token belongs to another mailbox')
    q = query or ''
    if before:
        q = (q + ' before:' + datetime.strptime(before, '%Y-%m-%d').strftime('%Y/%m/%d')).strip()
    result = {'account': token.account, 'mode': 'backfill', 'stored': 0, 'redacted': 0, 'tainted': 0, 'vanished': 0}
    known = _known(store, token.account)
    ids = [gid for gid in gmail.message_ids(q) if gid not in known]
    result['listed'] = len(ids)
    _fetch_and_store(store, gmail, token.account, ids, result, progress)
    return result


def status(store, account=None):
    _schema(store)
    rows = store.db.execute('SELECT * FROM gmail_state' + (' WHERE account=?' if account else ''), (account,) if account else ()).fetchall()
    out = []
    for row in rows:
        item = dict(row)
        item['messages'] = store.db.execute('SELECT count(*) FROM gmail_messages WHERE account=? AND gone=0', (row['account'],)).fetchone()[0]
        item['failed'] = store.db.execute('SELECT count(*) FROM gmail_failed WHERE account=?', (row['account'],)).fetchone()[0]
        item['gone'] = store.db.execute('SELECT count(*) FROM gmail_messages WHERE account=? AND gone=1', (row['account'],)).fetchone()[0]
        out.append(item)
    return out


def credential_path(explicit=None):
    """--cred, else the systemd credential `gmail`, else none."""
    if explicit:
        return Path(explicit)
    directory = os.environ.get('CREDENTIALS_DIRECTORY')
    if directory and (Path(directory) / 'gmail').is_file():
        return Path(directory) / 'gmail'
    raise ValueError('No Gmail credential: pass --cred or run under the hub service')
