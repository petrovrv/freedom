import base64
import json
import os
import shutil
import subprocess
import tempfile
import time
import unittest
import unittest.mock
from email.message import EmailMessage
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

import gmailsync
import relay
from mailstore import Store
from test_gmailsync import FakeGmail
from test_mailstore import message

UI = Path(__file__).resolve().parents[1] / 'ui' / 'relay.js'


class Page:
    """The page side of the envelope in Python (the JS one is checked against node below)."""

    def __init__(self, env, worker_pub):
        self.env, self.key = env, ec.generate_private_key(ec.SECP256R1())
        self.pub = base64.b64encode(self.key.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)).decode()
        self.peer = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), base64.b64decode(worker_pub))

    def _k(self, rid, d):
        return HKDF(hashes.SHA256(), 32, f'{self.env}|{rid}'.encode(), b'freedom-mail-relay/1 ' + d).derive(self.key.exchange(ec.ECDH(), self.peer))

    def seal(self, rid, value):
        value = {'ts': time.time() * 1000, **value}
        iv = os.urandom(12)
        return {'id': rid, 'pk': self.pub, 'iv': base64.b64encode(iv).decode(),
                'ct': base64.b64encode(AESGCM(self._k(rid, b'req')).encrypt(iv, json.dumps(value).encode(), rid.encode())).decode()}

    def open(self, rid, ans):
        return json.loads(AESGCM(self._k(rid, b'res')).decrypt(base64.b64decode(ans['iv']), base64.b64decode(ans['ct']), rid.encode()))


class RelayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root / 'store')
        cred = self.root / 'cred.json'
        cred.write_text(json.dumps({'client_id': 'c', 'client_secret': 's', 'refresh_token': 'r', 'account': 'me@example.org'}))
        self.api = FakeGmail()
        self.api.add('a1', message(subject='Hello heat pump', body='Offer for the heat pump', mid='<a1@x>').as_bytes(), labels=('INBOX', 'UNREAD'))
        self.api.add('a2', message(subject='Report', body='Weekly report', mid='<a2@x>').as_bytes(), labels=('SENT',))
        html = EmailMessage()
        html['From'], html['To'], html['Subject'], html['Message-ID'] = 'Eve <eve@example.org>', 'me@example.org', 'Html', '<a3@x>'
        html.set_content('plain')
        html.add_alternative('<p onclick="x()">Hi<script>alert(1)</script><img src="cid:logo"><a href="javascript:evil()">l</a></p>', subtype='html')
        html.get_payload()[1].add_related(b'\x89PNG fake', 'image', 'png', cid='<logo>')
        html.add_attachment(b'%PDF-1.4 tiny', maintype='application', subtype='pdf', filename='offer.pdf')
        self.api.add('a3', html.as_bytes(), labels=('INBOX',))
        gmailsync.sync_gmail(self.store, gmailsync.TokenSource(cred, request=self.api), request=self.api)
        self.keys = relay.Keys(self.root / 'relay-key.pem')
        self.mail = relay.MailApi(self.store, 'me@example.org')

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def ask(self, action, args=None, rid='0' * 32):
        page = Page('enva', self.keys.public)
        ans = relay.answer(self.keys, self.mail, 'enva', page.seal(rid, {'action': action, 'args': args or {}}))
        self.assertNotIn('error', ans)
        return page.open(rid, ans)

    def test_key_is_kept_private_and_reused(self):
        path = self.root / 'relay-key.pem'
        if os.name == 'posix':
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
        self.assertEqual(relay.Keys(path).public, self.keys.public)

    def test_overview_list_search_thread(self):
        ov = self.ask('overview')['result']
        inbox = next(l for l in ov['labels'] if l['id'] == 'INBOX')
        self.assertEqual((inbox['total'], inbox['unread'], ov['unread']), (2, 1, 1))
        threads = self.ask('list', {'label': 'INBOX'})['result']['threads']
        self.assertEqual(len(threads), 2)
        self.assertEqual(self.ask('list', {'label': 'SENT'})['result']['threads'][0]['subject'], 'Report')
        first = self.ask('list', {'label': 'INBOX', 'limit': 1})['result']
        second = self.ask('list', {'label': 'INBOX', 'limit': 1, 'before': first['next']})['result']
        self.assertEqual({first['threads'][0]['thread'], second['threads'][0]['thread']}, {t['thread'] for t in threads})
        self.assertFalse(self.ask('list', {'label': 'INBOX', 'before': "1:x' OR 1=1"})['ok'])
        found = self.ask('search', {'q': 'heat pump'})['result']['threads']
        self.assertEqual([t['subject'] for t in found], ['Hello heat pump'])
        html_thread = next(t for t in threads if t['subject'] == 'Html')
        self.assertEqual(html_thread['attachments'], 1)
        msg = self.ask('thread', {'thread': html_thread['thread']})['result']['messages'][0]
        self.assertNotIn('<script', msg['html'])
        self.assertNotIn('onclick', msg['html'])
        self.assertNotIn('javascript:', msg['html'])
        self.assertIn('src="data:image/png;base64,', msg['html'])
        att = msg['attachments'][0]
        got = self.ask('attachment', {'sha': att['sha']})['result']
        self.assertEqual(base64.b64decode(got['data']), b'%PDF-1.4 tiny')

    def test_bad_input_gives_errors_not_content(self):
        self.assertFalse(self.ask('drop_table')['ok'])
        self.assertFalse(self.ask('list', {'label': "INBOX%' OR 1=1 --"})['ok'])
        self.assertFalse(self.ask('attachment', {'sha': 'f' * 64})['ok'])
        page = Page('enva', self.keys.public)
        sealed = page.seal('1' * 32, {'action': 'overview'})
        # sealed for another environment or another id: cannot be opened
        self.assertEqual(relay.answer(self.keys, self.mail, 'envb', sealed), {'id': '1' * 32, 'error': 'bad_request'})
        self.assertEqual(relay.answer(self.keys, self.mail, 'enva', {**sealed, 'id': '2' * 32})['error'], 'bad_request')

    def test_expired_and_replayed_requests_get_no_answer(self):
        page, seen = Page('enva', self.keys.public), relay.Seen()
        old = page.seal('3' * 32, {'action': 'overview', 'ts': (time.time() - 600) * 1000})
        self.assertEqual(relay.answer(self.keys, self.mail, 'enva', old, seen)['error'], 'expired')
        fresh = page.seal('4' * 32, {'action': 'overview'})
        self.assertNotIn('error', relay.answer(self.keys, self.mail, 'enva', fresh, seen))
        self.assertEqual(relay.answer(self.keys, self.mail, 'enva', fresh, seen)['error'], 'replay')

    def test_big_answers_are_refused_and_batches_split(self):
        page = Page('enva', self.keys.public)
        with unittest.mock.patch.object(relay, 'REPLY_MAX', 50):
            self.assertEqual(relay.answer(self.keys, self.mail, 'enva', page.seal('5' * 32, {'action': 'overview'}))['error'], 'too_large')
        with unittest.mock.patch.object(relay, 'BATCH_MAX', 1000):
            parts = relay.batches([{'id': str(i), 'ct': 'x' * 400} for i in range(5)])
        self.assertEqual([len(p) for p in parts], [1, 1, 1, 1, 1])
        self.assertEqual(relay.batches([]), [])

    def test_site_refusing_a_batch_turns_it_into_errors(self):
        page, calls = Page('enva', self.keys.public), []

        def post(url, payload):
            calls.append(payload)
            if len(calls) == 1:
                return 200, {'requests': [page.seal('6' * 32, {'action': 'overview'})]}
            if len(calls) == 2:
                return 413, {}
            return 200, {'requests': []}
        relay.serve(self.store, self.root, 'enva', 'me@example.org', 'https://x/', 'tok', request=post, stop=lambda: len(calls) >= 3)
        self.assertIn('ct', calls[1]['replies'][0])
        self.assertEqual(calls[2]['replies'], [{'id': '6' * 32, 'error': 'too_large'}])

    def test_refused_error_batch_is_dropped(self):
        page, calls = Page('enva', self.keys.public), []

        def post(url, payload):
            calls.append(payload)
            if len(calls) == 1:
                return 200, {'requests': [page.seal('7' * 32, {'action': 'overview'})]}
            return (413, {}) if len(calls) <= 3 else (200, {'requests': []})
        with unittest.mock.patch.object(relay.time, 'sleep'):
            relay.serve(self.store, self.root, 'enva', 'me@example.org', 'https://x/', 'tok', request=post, stop=lambda: len(calls) >= 4)
        self.assertEqual(calls[2]['replies'], [{'id': '7' * 32, 'error': 'too_large'}])
        self.assertEqual(calls[3]['replies'], [])

    def test_serve_loop_answers_and_reports_status(self):
        page = Page('enva', self.keys.public)
        calls, rid = [], 'a' * 32

        def post(url, payload):
            calls.append(payload)
            if len(calls) == 1:
                return 200, {'requests': [page.seal(rid, {'action': 'overview'})]}
            return 200, {'requests': []}
        relay.serve(self.store, self.root, 'enva', 'me@example.org', 'https://x/', 'tok', request=post, stop=lambda: len(calls) >= 2)
        self.assertEqual(calls[0]['status']['pub'], self.keys.public)
        self.assertEqual(calls[0]['status']['unread'], 1)
        self.assertEqual(calls[1]['replies'][0]['id'], rid)
        self.assertTrue(page.open(rid, calls[1]['replies'][0])['ok'])

    def test_refused_token_stops_the_worker(self):
        with self.assertRaises(PermissionError):
            relay.serve(self.store, self.root, 'enva', 'me@example.org', 'https://x/', 'tok', request=lambda u, p: (403, {'error': 'forbidden'}))

    @unittest.skipUnless(shutil.which('node'), 'node not installed')
    def test_browser_envelope_interoperates(self):
        rid = 'b' * 32
        script = f'''
const MailRelay = require({json.dumps(str(UI))});
const fs = require('fs');
(async () => {{
  const r = MailRelay(null, 'enva');
  const req = await r._seal({json.dumps(rid)}, {{action: 'overview', args: {{}}, ts: Date.now()}}, {json.dumps(self.keys.public)});
  process.stdout.write(JSON.stringify(req) + '\\n');
  const ans = JSON.parse(fs.readFileSync(0, 'utf8'));
  const out = await r._open({json.dumps(rid)}, ans);
  process.stdout.write(JSON.stringify(out));
}})().catch(e => {{ console.error(e); process.exit(1); }});
'''
        proc = subprocess.Popen(['node', '-e', script], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        req = json.loads(proc.stdout.readline())
        ans = relay.answer(self.keys, self.mail, 'enva', req)
        out, _ = proc.communicate(json.dumps(ans), timeout=30)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual(json.loads(out)['result']['unread'], 1)


if __name__ == '__main__':
    unittest.main()
