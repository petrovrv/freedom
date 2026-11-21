import base64
import json
from pathlib import Path
import tempfile
import unittest
import urllib.parse
from unittest.mock import patch

from mailstore import Store
from test_mailstore import message
import gmailsync


class FakeGmail:
    """Gmail API stand-in: messages {gid: (raw, labels, thread)}, history records after a historyId."""

    def __init__(self, account='me@example.org'):
        self.account, self.messages, self.history, self.history_id = account, {}, [], 100
        self.calls, self.expired, self.fail_once = [], False, set()

    def add(self, gid, raw, labels=('INBOX',), record=True):
        self.messages[gid] = (raw, list(labels), 't' + gid)
        self.history_id += 1
        if record:
            self.history.append((self.history_id, {'id': str(self.history_id), 'messagesAdded': [{'message': {'id': gid, 'labelIds': list(labels)}}]}))

    def __call__(self, method, url, headers, body=None):
        self.calls.append((method, url))
        if url == gmailsync.TOKEN_URL:
            return 200, {'access_token': 'tok', 'expires_in': 3600}
        assert headers['Authorization'] == 'Bearer tok'
        parsed = urllib.parse.urlparse(url)
        path, query = parsed.path.split('/users/me/')[1], urllib.parse.parse_qs(parsed.query)
        if path in self.fail_once:
            self.fail_once.discard(path)
            return 429, {}
        if path == 'profile':
            return 200, {'emailAddress': self.account, 'historyId': str(self.history_id)}
        if path == 'messages':
            ids = sorted(self.messages)
            start = int(query.get('pageToken', ['0'])[0])
            page = ids[start:start + 2]
            data = {'messages': [{'id': i} for i in page]}
            if start + 2 < len(ids):
                data['nextPageToken'] = str(start + 2)
            return 200, data
        if path.startswith('messages/'):
            gid = path.split('/')[1]
            if gid not in self.messages:
                return 404, {}
            raw, labels, thread = self.messages[gid]
            return 200, {'id': gid, 'threadId': thread, 'labelIds': labels, 'internalDate': str(int(__import__('time').time() * 1000)),
                         'raw': base64.urlsafe_b64encode(raw).decode().rstrip('=')}
        if path == 'history':
            if self.expired:
                self.expired = False  # the historyId taken by the new full listing is fresh
                return 404, {}
            start = int(query['startHistoryId'][0])
            return 200, {'history': [r for h, r in self.history if h > start], 'historyId': str(self.history_id)}
        return 400, {}


class GmailSyncTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.store = Store(root / 'store')
        cred = root / 'cred.json'
        cred.write_text(json.dumps({'client_id': 'c', 'client_secret': 's', 'refresh_token': 'r', 'account': 'Me@Example.org'}))
        self.api = FakeGmail()
        self.token = gmailsync.TokenSource(cred, request=self.api)
        self.sync = lambda: gmailsync.sync_gmail(self.store, self.token, request=self.api)

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def raw(self, n, **kw):
        return message(subject=f'Subject {n}', body=f'Body {n}', mid=f'<m{n}@example.org>', **kw).as_bytes()

    def test_full_then_incremental(self):
        for n in range(5):
            self.api.add(f'g{n}', self.raw(n), record=False)
        self.api.add('s1', self.raw(9), labels=('SENT',), record=False)
        first = self.sync()
        self.assertEqual((first['mode'], first['stored']), ('full', 6))
        self.assertEqual(self.store.db.execute('SELECT is_sent FROM messages m JOIN gmail_messages g ON g.message_id=m.id WHERE g.gmail_id=?', ('s1',)).fetchone()[0], 1)
        self.api.add('g9', self.raw(20))
        self.api.history.append((self.api.history_id + 1, {'messagesDeleted': [{'message': {'id': 'g0'}}],
                                                          'labelsAdded': [{'message': {'id': 'g1', 'labelIds': ['INBOX', 'STARRED']}}]}))
        self.api.history_id += 1
        second = self.sync()
        self.assertEqual((second['mode'], second['stored'], second['deleted'], second['relabeled']), ('history', 1, 1, 1))
        state = gmailsync.status(self.store)[0]
        self.assertEqual((state['messages'], state['gone']), (6, 1))
        self.assertEqual(json.loads(self.store.db.execute("SELECT labels FROM gmail_messages WHERE gmail_id='g1'").fetchone()[0]), ['INBOX', 'STARRED'])
        third = self.sync()
        self.assertEqual(third['stored'], 0)

    def test_label_delta_without_full_set(self):
        self.api.add('g0', self.raw(0), labels=('INBOX', 'UNREAD'), record=False)
        self.sync()
        self.api.history_id += 1
        self.api.history.append((self.api.history_id, {'labelsRemoved': [{'message': {'id': 'g0'}, 'labelIds': ['UNREAD']}],
                                                       'labelsAdded': [{'message': {'id': 'g0'}, 'labelIds': ['STARRED']}]}))
        self.sync()
        self.assertEqual(json.loads(self.store.db.execute("SELECT labels FROM gmail_messages WHERE gmail_id='g0'").fetchone()[0]), ['INBOX', 'STARRED'])

    def test_gap_reconciles_deletions_and_labels(self):
        for n in range(3):
            self.api.add(f'g{n}', self.raw(n), record=False)
        self.sync()
        self.api.expired = True
        del self.api.messages['g0']                      # deleted while history was lost
        raw, _, thread = self.api.messages['g1']
        self.api.messages['g1'] = (raw, ['TRASH'], thread)  # moved meanwhile
        result = self.sync()
        self.assertEqual((result['deleted'], result['relabeled']), (1, 2))
        rows = dict(self.store.db.execute('SELECT gmail_id, gone FROM gmail_messages').fetchall())
        self.assertEqual(rows, {'g0': 1, 'g1': 0, 'g2': 0})
        self.assertEqual(json.loads(self.store.db.execute("SELECT labels FROM gmail_messages WHERE gmail_id='g1'").fetchone()[0]), ['TRASH'])

    def test_interrupted_recovery_still_reconciles(self):
        for n in range(2):
            self.api.add(f'g{n}', self.raw(n), record=False)
        self.sync()
        self.api.expired = True
        del self.api.messages['g0']
        original = self.api.__call__
        def broken(method, url, headers, body=None):
            if '/messages?' in url:
                return 500, {}
            return original(method, url, headers, body)
        with patch.object(gmailsync.Gmail.__init__, '__defaults__', (None, lambda seconds: None)), self.assertRaises(RuntimeError):
            gmailsync.sync_gmail(self.store, self.token, request=broken)  # listing fails after the history expired
        result = self.sync()
        self.assertEqual(result['deleted'], 1)

    def test_changes_during_first_sync_are_replayed(self):
        self.api.add('g0', self.raw(0), record=False)
        start = self.api.history_id
        original = self.api.__call__

        def arrives_during_listing(method, url, headers, body=None):
            if '/messages?' in url and 'g1' not in self.api.messages:
                self.api.add('g1', self.raw(1))  # new mail after the profile historyId was read
            return original(method, url, headers, body)
        result = gmailsync.sync_gmail(self.store, self.token, request=arrives_during_listing)
        self.assertGreaterEqual(int(result['history_id']), start)
        self.assertEqual(gmailsync.status(self.store)[0]['messages'], 2)

    def test_expired_history_relists_without_refetch(self):
        for n in range(3):
            self.api.add(f'g{n}', self.raw(n), record=False)
        self.sync()
        self.api.expired = True
        self.api.add('g7', self.raw(7), record=False)
        self.api.calls.clear()
        result = self.sync()
        self.assertEqual((result['mode'], result['stored']), ('full', 1))
        fetched = [u for _, u in self.api.calls if '/messages/' in u and 'format=raw' in u]
        self.assertEqual(len(fetched), 1)

    def test_drafts_and_codes_and_wrong_mailbox(self):
        self.api.add('d1', self.raw(1), labels=('DRAFT',), record=False)
        self.api.add('c1', message(subject='Your verification code', body='Code: 482913').as_bytes(), record=False)
        result = self.sync()
        self.assertEqual((result['stored'], result['redacted'], result.get('drafts_skipped')), (1, 1, 1))
        self.assertNotIn(b'482913', b''.join(p.read_bytes() for p in self.store.blobs.iterdir()))
        self.api.account = 'other@example.org'
        with self.assertRaises(PermissionError):
            self.sync()

    def test_rate_limit_retried_and_vanished_counted(self):
        self.api.add('g0', self.raw(0), record=False)
        self.api.fail_once.add('profile')
        sleeps = []
        gmail = gmailsync.Gmail(self.token, self.api, sleep=sleeps.append)
        self.assertEqual(gmail.profile()['emailAddress'], 'me@example.org')
        self.assertEqual(sleeps, [1])
        original = self.api.__call__

        def vanish(method, url, headers, body=None):
            if '/messages/g0' in url:
                return 404, {}
            return original(method, url, headers, body)
        result = gmailsync.sync_gmail(self.store, self.token, request=vanish)
        self.assertEqual((result['stored'], result['vanished']), (0, 1))

    def test_failed_ingest_kept_and_retried(self):
        self.api.add('g0', self.raw(0), record=False)
        real = self.store.ingest
        self.store.ingest = lambda *a, **k: (_ for _ in ()).throw(ValueError('Thread merge requires review'))
        result = self.sync()
        self.assertEqual(result.get('failed'), 1)
        self.store.ingest = real
        again = self.sync()
        self.assertEqual(again['stored'], 1)
        self.assertEqual(gmailsync.status(self.store)[0]['failed'], 0)

    def test_token_refresh_failure_is_clear(self):
        cred = Path(self.temp.name) / 'bad.json'
        cred.write_text(json.dumps({'client_id': 'c', 'client_secret': 's', 'refresh_token': 'r', 'account': 'a@b.c'}))
        token = gmailsync.TokenSource(cred, request=lambda *a, **k: (400, {'error': 'invalid_grant'}))
        with self.assertRaisesRegex(PermissionError, 'invalid_grant'):
            token()
        cred.write_text(json.dumps({'client_id': 'c', 'account': 'a@b.c'}))
        with self.assertRaises(ValueError):
            gmailsync.TokenSource(cred)


if __name__ == '__main__':
    unittest.main()
