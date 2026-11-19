import hashlib
import imaplib
from email.message import EmailMessage
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from mailstore import Store, SensitiveMessageError, sync_imap


def message(subject='Engineering meeting', body='Discuss ventilation tomorrow', mid='<one@example.org>'):
    m = EmailMessage()
    m['From'] = 'Alice <alice@example.org>'
    m['To'] = 'bob@example.org'
    m['Subject'] = subject
    m['Message-ID'] = mid
    m.set_content(body)
    return m


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root)

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def test_exact_mime_and_attachment_storage(self):
        m = message('Réunion — проект', 'Текст письма')
        m.add_alternative('<p>Текст письма</p>', subtype='html')
        payload = b'\x00\xff\x01attachment'
        m.add_attachment(payload, maintype='application', subtype='octet-stream', filename='../../escape.bin')
        raw = m.as_bytes()
        row = self.store.ingest('owner', raw)
        self.assertEqual(self.store.read_blob(row['raw_sha256']), raw)
        self.assertEqual(row['subject'], 'Réunion — проект')
        self.assertIn('Текст письма', row['body'])
        self.assertIn('<p>', row['html'])
        self.assertEqual(len(row['attachments']), 1)
        self.assertEqual(self.store.read_blob(row['attachments'][0]['sha256']), payload)
        self.assertFalse((self.root.parent / 'escape.bin').exists())
        self.assertTrue(all(len(p.name) == 64 for p in (self.root / 'blobs').iterdir()))

    def test_idempotency_and_uid_validity(self):
        raw = message().as_bytes()
        first = self.store.ingest('owner', raw, 'INBOX', '1', '10')
        again = self.store.ingest('owner', raw, 'Archive', '2', '10')
        self.assertEqual(first['id'], again['id'])
        self.assertEqual(len(self.store.list_messages()), 1)
        self.assertEqual(len(self.store.audit()), 1)
        changed = message(body='Changed content').as_bytes()
        with self.assertRaises(ValueError):
            self.store.ingest('owner', changed, 'INBOX', '1', '10')
        self.assertEqual(len(self.store.list_messages()), 1)
        new = self.store.ingest('owner', changed, 'INBOX', '1', '11')
        self.assertNotEqual(first['id'], new['id'])
        other = self.store.ingest('other', raw)
        self.assertNotEqual(first['id'], other['id'])

    def test_threads_search_cases_and_audit(self):
        root = self.store.ingest('owner', message().as_bytes())
        reply = message('Re: Engineering meeting', 'Thermal analysis', '<two@example.org>')
        reply['References'] = '<one@example.org>'
        row = self.store.ingest('owner', reply.as_bytes())
        self.assertEqual(root['thread_id'], row['thread_id'])
        self.assertEqual(self.store.search('Thermal: analysis!')[0]['id'], row['id'])
        case = self.store.assign_case(row['id'], 'Project A')
        self.assertEqual(self.store.get_message(row['id'])['case_id'], case['id'])
        self.assertEqual(self.store.list_cases()[0]['message_count'], 1)
        self.assertEqual(self.store.audit()[0]['action'], 'assign_case')
        self.assertEqual(self.store.search('" OR *'), [])

    def test_authentication_message_never_persisted(self):
        raw = message('Your verification code', 'SECRET-846291').as_bytes()
        with self.assertRaises(SensitiveMessageError):
            self.store.ingest('owner', raw)
        self.assertEqual(self.store.list_messages(), [])
        self.assertEqual(list((self.root / 'blobs').iterdir()), [])
        self.assertNotIn(b'SECRET-846291', (self.root / 'mail.sqlite3').read_bytes())

    def test_blob_integrity_is_checked(self):
        raw = message().as_bytes()
        row = self.store.ingest('owner', raw)
        (self.root / 'blobs' / row['raw_sha256']).write_bytes(b'corrupt')
        with self.assertRaises(ValueError):
            self.store.read_blob(row['raw_sha256'])
        with self.assertRaises(ValueError):
            self.store.ingest('owner', raw)
        with self.assertRaises(ValueError):
            self.store.read_blob('../mail.sqlite3')

    def test_readonly_sync_and_sensitive_skip(self):
        raw = message().as_bytes()
        sensitive = message('Login code', '123456').as_bytes()
        class Client:
            def __init__(self):
                self.calls = []
            def login(self, account, password):
                self.calls.append(('login', account))
                return 'OK', []
            def select(self, folder, readonly=False):
                self.calls.append(('select', folder, readonly))
                return 'OK', [b'2']
            def response(self, key):
                return key, [b'42']
            def uid(self, command, *args):
                self.calls.append((command, *args))
                if command == 'search':
                    return 'OK', [b'1 2']
                if command == 'fetch':
                    return 'OK', [(b'BODY[]', raw if args[0] == b'1' else sensitive), b')']
                raise AssertionError('Mutation attempted')
            def logout(self):
                self.calls.append(('logout',))
        client = Client()
        secret = 'test-password-do-not-persist'
        keyring = types.SimpleNamespace(get_password=lambda service, account: secret)
        with patch.dict(sys.modules, keyring=keyring), patch('mailstore.imaplib.IMAP4_SSL', return_value=client):
            result = sync_imap(self.store, 'owner')
            sync_imap(self.store, 'owner')
        self.assertEqual(result, {'fetched': 2, 'stored': 1, 'skipped_sensitive': 1})
        self.assertIn(('select', '"INBOX"', True), client.calls)
        self.assertTrue(all(c[2] == '(BODY.PEEK[])' for c in client.calls if c[0] == 'fetch'))
        self.assertEqual(len(self.store.list_messages()), 1)
        for path in self.root.rglob('*'):
            if path.is_file():
                self.assertNotIn(secret.encode(), path.read_bytes())

    def test_mailbox_names_on_actual_imap_command_wire(self):
        # Exercise imaplib.select and _command; replace only transport/response.
        class WireClient(imaplib.IMAP4):
            def __init__(self):
                self.state = 'AUTH'
                self.tagged_commands = {}
                self.untagged_responses = {}
                self.tagpre = b'T'
                self.tagnum = 0
                self.literal = None
                self.debug = 0
                self._cmd_log = {}
                self._cmd_log_idx = 0
                self._cmd_log_len = 10
                self._encoding = 'ascii'
                self.wire = []
            def send(self, data):
                self.wire.append(data)
            def _command_complete(self, name, tag):
                return 'OK', [b'0']
            def login(self, *args):
                return 'OK', []
            def response(self, key):
                return key, [b'42']
            def uid(self, *args):
                return 'OK', [b'']
            def logout(self):
                pass
        keyring = types.SimpleNamespace(get_password=lambda *args: 'test-only')
        for folder, expected in [('台北/日本語', b'T0 EXAMINE "&U,BTFw-/&ZeVnLIqe-"\r\n'),
                                 ('A&B', b'T0 EXAMINE "A&-B"\r\n'),
                                 ('[Gmail]/Sent Mail', b'T0 EXAMINE "[Gmail]/Sent Mail"\r\n'),
                                 ('A"B\\C', b'T0 EXAMINE "A\\"B\\\\C"\r\n')]:
            client = WireClient()
            with patch.dict(sys.modules, keyring=keyring), patch('mailstore.imaplib.IMAP4_SSL', return_value=client):
                sync_imap(self.store, 'owner', folder=folder)
            self.assertEqual(client.wire, [expected])
        with patch('mailstore.imaplib.IMAP4_SSL') as connect:
            with self.assertRaises(ValueError):
                sync_imap(self.store, 'owner', folder='INBOX\r\nEXPUNGE')
            connect.assert_not_called()

    def test_login_error_is_sanitized_and_logout_runs(self):
        from unittest.mock import MagicMock
        client = MagicMock()
        client.login.side_effect = imaplib.IMAP4.error('secret-password')
        keyring = types.SimpleNamespace(get_password=lambda *args: 'secret-password')
        with patch.dict(sys.modules, keyring=keyring), patch('mailstore.imaplib.IMAP4_SSL', return_value=client):
            with self.assertRaises(RuntimeError) as error:
                sync_imap(self.store, 'owner')
        self.assertNotIn('secret-password', str(error.exception))
        client.logout.assert_called_once()

    def test_missing_credential_never_connects(self):
        keyring = types.SimpleNamespace(get_password=lambda *args: None)
        with patch.dict(sys.modules, keyring=keyring), patch('mailstore.imaplib.IMAP4_SSL') as connect:
            with self.assertRaises(ValueError):
                sync_imap(self.store, 'owner')
        connect.assert_not_called()


if __name__ == '__main__':
    unittest.main()
