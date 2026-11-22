import json
import os
import sys
import tempfile
import unittest
import urllib.parse
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import accounts  # noqa: E402

GMAIL = 'https://www.googleapis.com/auth/gmail.readonly'
DRIVE = 'https://www.googleapis.com/auth/drive.file'
CAL = 'https://www.googleapis.com/auth/calendar.readonly'
CRED = {'client_id': 'c', 'client_secret': 's', 'refresh_token': 'r', 'account': 'a@b.c',
        'scopes': ['openid', 'email', GMAIL, DRIVE]}


class Recorder:
    def __init__(self):
        self.forms = []

    def __call__(self, method, url, headers, body=None):
        self.forms.append(dict(urllib.parse.parse_qsl(body.decode())))
        return 200, {'access_token': 'tok%d' % len(self.forms), 'expires_in': 3600}


class AccountsTest(unittest.TestCase):
    def test_capabilities_map_to_scopes_and_back(self):
        g = accounts.provider('google')
        self.assertEqual(g.scopes(['mail.read']), sorted(['openid', 'email', GMAIL]))
        self.assertEqual(g.granted(CRED['scopes']), ['drive.appfiles', 'mail.read'])
        with self.assertRaisesRegex(ValueError, 'unknown capabilities'):
            g.scopes(['mail.read', 'telepathy'])
        with self.assertRaises(ValueError):
            accounts.provider('yahoo')

    def test_missing_lists_only_new_capabilities(self):
        self.assertEqual(accounts.missing(CRED, ['mail.read', 'calendar.read', 'calendar.write']), ['calendar.read', 'calendar.write'])
        self.assertEqual(accounts.missing(CRED, ['mail.read']), [])
        self.assertEqual(accounts.missing({}, ['mail.read']), ['mail.read'])

    def test_merged_keeps_earlier_grants(self):
        out = accounts.merged(CRED, ['openid', CAL], 'google')
        self.assertEqual(out['scopes'], sorted(set(CRED['scopes']) | {CAL}))
        self.assertEqual(out['provider'], 'google')

    def test_module_token_is_narrowed_to_its_capabilities(self):
        rec = Recorder()
        mail = accounts.TokenSource(cred=CRED, request=rec, capabilities=['mail.read'])
        self.assertEqual(mail(), 'tok1')
        self.assertEqual(mail(), 'tok1')  # cached
        self.assertEqual(rec.forms[0]['scope'], GMAIL)
        accounts.TokenSource(cred=CRED, request=rec, capabilities=['drive.appfiles'])()
        self.assertEqual(rec.forms[1]['scope'], DRIVE)

    def test_capability_not_granted_is_refused_before_any_request(self):
        rec = Recorder()
        with self.assertRaisesRegex(PermissionError, 'calendar.read'):
            accounts.TokenSource(cred=CRED, request=rec, capabilities=['calendar.read'])
        self.assertEqual(rec.forms, [])

    def test_credential_without_scope_record_refreshes_unnarrowed(self):
        rec = Recorder()
        cred = {k: v for k, v in CRED.items() if k != 'scopes'}
        accounts.TokenSource(cred=cred, request=rec, capabilities=['mail.read'])()
        self.assertNotIn('scope', rec.forms[0])

    def test_refresh_failure_is_clear(self):
        t = accounts.TokenSource(cred=CRED, request=lambda *a, **k: (400, {'error': 'invalid_grant'}))
        with self.assertRaisesRegex(PermissionError, 'invalid_grant'):
            t()

    def test_credential_path_prefers_shared_then_legacy(self):
        with tempfile.TemporaryDirectory() as d:
            with mock.patch.dict(os.environ, {'CREDENTIALS_DIRECTORY': d}):
                with self.assertRaises(ValueError):
                    accounts.credential_path()
                (Path(d) / 'gmail').write_text('{}')
                self.assertEqual(accounts.credential_path().name, 'gmail')
                (Path(d) / 'account').write_text('{}')
                self.assertEqual(accounts.credential_path().name, 'account')
            self.assertEqual(accounts.credential_path('x.json'), Path('x.json'))

    def test_file_credential_needs_account(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'c.json'
            p.write_text(json.dumps({k: v for k, v in CRED.items() if k != 'account'}))
            with self.assertRaisesRegex(ValueError, 'account'):
                accounts.TokenSource(p)

    def test_google_consent_is_incremental_with_pkce(self):
        url, state, verifier = accounts.provider('google').auth_request('cid', 'http://127.0.0.1:1/', ['calendar.read'], 'a@b.c')
        q = dict(urllib.parse.parse_qsl(urllib.parse.urlparse(url).query))
        self.assertEqual(q['include_granted_scopes'], 'true')
        self.assertEqual(q['access_type'], 'offline')
        self.assertEqual(q['code_challenge_method'], 'S256')
        self.assertEqual(q['state'], state)
        self.assertEqual(set(q['scope'].split()), {'openid', 'email', CAL})
        self.assertTrue(verifier)

    def test_microsoft_is_named_but_not_connectable(self):
        ms = accounts.provider('microsoft')
        self.assertIn('Mail.Read', ms.scopes(['mail.read']))
        with self.assertRaises(NotImplementedError):
            ms.auth_request('cid', 'http://127.0.0.1:1/', ['mail.read'])


if __name__ == '__main__':
    unittest.main()
