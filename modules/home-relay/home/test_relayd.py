import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import unittest.mock
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

import relayd

UI = Path(__file__).resolve().parents[1] / 'ui' / 'relay.js'

# a module handler for the tests: echoes calls, reports a status; "hang" / "die" misbehave on purpose
ECHO = r'''
import json, os, sys, time
for line in iter(sys.stdin.buffer.readline, b''):
    req = json.loads(line)
    if req['op'] == 'status':
        out = {'status': {'bound': True, 'env': os.environ.get('HOME_RELAY_ENV'), 'slug': os.environ.get('HOME_RELAY_SLUG'),
                          'extra': os.environ.get('EXTRA', '')}}
    elif req['action'] == 'hang':
        time.sleep(60)
    elif req['action'] == 'die':
        sys.exit(3)
    elif req['action'] == 'fail':
        out = {'ok': False, 'error': 'no such <thing>'}
    else:
        out = {'ok': True, 'result': {'action': req['action'], 'args': req['args'], 'pid': os.getpid()}}
    sys.stdout.buffer.write(json.dumps(out).encode() + b'\n')
    sys.stdout.buffer.flush()
'''


class Page:
    """The page side of the envelope in Python (the JS one is checked against node below)."""

    def __init__(self, env, module, worker_pub):
        self.env, self.module, self.key = env, module, ec.generate_private_key(ec.SECP256R1())
        self.pub = base64.b64encode(self.key.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)).decode()
        self.peer = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), base64.b64decode(worker_pub))

    def _k(self, rid, d):
        return HKDF(hashes.SHA256(), 32, f'{self.env}|{self.module}|{rid}'.encode(), b'freedom-home-relay/1 ' + d).derive(
            self.key.exchange(ec.ECDH(), self.peer))

    def seal(self, rid, value, module=None):
        value = {'ts': time.time() * 1000, **value}
        iv = os.urandom(12)
        return {'id': rid, 'module': module or self.module, 'pk': self.pub, 'iv': base64.b64encode(iv).decode(),
                'ct': base64.b64encode(AESGCM(self._k(rid, b'req')).encrypt(iv, json.dumps(value).encode(), rid.encode())).decode()}

    def open(self, rid, ans):
        return json.loads(AESGCM(self._k(rid, b'res')).decrypt(base64.b64decode(ans['iv']), base64.b64decode(ans['ct']), rid.encode()))


class RelaydTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / 'echo.py').write_text(ECHO, encoding='utf-8')
        conf = self.root / 'conf' / 'modules.d'
        conf.mkdir(parents=True)
        (self.root / 'conf' / 'alice.extra').write_text('EXTRA=from-env-file\nBAD LINE\n', encoding='utf-8')
        argv = [sys.executable, str(self.root / 'echo.py'), '--who', '{slug}']
        (conf / 'mail.json').write_text(json.dumps({'exec': argv, 'envFile': str(self.root / 'conf' / '{slug}.extra')}))
        (conf / 'calendar.json').write_text(json.dumps({'exec': argv, 'if': str(self.root / 'conf' / '{slug}.calendar')}))
        (conf / 'Bad_Name.json').write_text(json.dumps({'exec': argv}))
        (conf / 'relative.json').write_text(json.dumps({'exec': ['python', 'x.py']}))
        self.logs = []
        self.handlers = relayd.load_handlers(self.root / 'conf', 'alice', 'enva', self.logs.append)
        self.keys = relayd.Keys(self.root / 'relay-key.pem')

    def tearDown(self):
        for h in self.handlers.values():
            h.stop()
        self.temp.cleanup()

    def ask(self, action, args=None, rid='0' * 32, module='mail'):
        page = Page('enva', module, self.keys.public)
        ans = relayd.answer(self.keys, self.handlers, 'enva', page.seal(rid, {'action': action, 'args': args or {}}))
        self.assertNotIn('error', ans)
        return page.open(rid, ans)

    def test_handlers_come_from_the_home_server_config_only(self):
        self.assertEqual(sorted(self.handlers), ['mail'])  # calendar: its "if" path is missing for this person
        self.assertEqual(len(self.logs), 2)  # bad name, relative command
        mail = self.handlers['mail']
        self.assertEqual(mail.argv[-1], 'alice')
        self.assertEqual((mail.env['HOME_RELAY_ENV'], mail.env['HOME_RELAY_SLUG'], mail.env['HOME_RELAY_MODULE']), ('enva', 'alice', 'mail'))
        self.assertEqual(mail.env['EXTRA'], 'from-env-file')
        (self.root / 'conf' / 'alice.calendar').write_text('')
        self.assertEqual(sorted(relayd.load_handlers(self.root / 'conf', 'alice', 'enva')), ['calendar', 'mail'])
        with self.assertRaises(ValueError):
            relayd.load_handlers(self.root / 'conf', '../x', 'enva')
        self.assertNotIn('CREDENTIALS_DIRECTORY', mail.env)

    def test_credentials_only_for_a_module_that_asks(self):
        conf = self.root / 'conf2'
        (conf / 'modules.d').mkdir(parents=True)
        exe = os.path.abspath(sys.executable)
        (conf / 'modules.d' / 'mail.json').write_text(json.dumps({'exec': [exe, '-c', 'pass'], 'credentials': {'account': '/etc/credstore.encrypted/account.{slug}.google.cred'}}))
        (conf / 'modules.d' / 'notes.json').write_text(json.dumps({'exec': [exe, '-c', 'pass']}))
        with unittest.mock.patch.dict(os.environ, {'CREDENTIALS_DIRECTORY': '/run/credentials/home-relay@alice.service'}):
            got = relayd.load_handlers(conf, 'alice', 'enva')
        self.assertEqual(got['mail'].env['CREDENTIALS_DIRECTORY'], '/run/credentials/home-relay@alice.service')
        self.assertNotIn('CREDENTIALS_DIRECTORY', got['notes'].env)

    def test_key_is_kept_private_and_reused(self):
        path = self.root / 'relay-key.pem'
        if os.name == 'posix':
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
        self.assertEqual(relayd.Keys(path).public, self.keys.public)

    def test_call_and_status_reach_the_handler(self):
        out = self.ask('list', {'label': 'INBOX'})
        self.assertEqual(out, {'ok': True, 'result': {'action': 'list', 'args': {'label': 'INBOX'}, 'pid': out['result']['pid']}})
        self.assertEqual(self.handlers['mail'].status(), {'bound': True, 'env': 'enva', 'slug': 'alice', 'extra': 'from-env-file'})
        self.assertEqual(self.ask('fail'), {'ok': False, 'error': 'no such thing'})

    def test_handler_that_dies_or_hangs_is_restarted(self):
        first = self.ask('x')['result']['pid']
        self.assertEqual(self.ask('die'), {'ok': False, 'error': 'handler_down'})
        self.assertNotEqual(self.ask('x')['result']['pid'], first)
        with unittest.mock.patch.object(relayd, 'CALL_TIMEOUT', 1):
            self.assertEqual(self.ask('hang'), {'ok': False, 'error': 'handler_down'})
        self.assertTrue(self.ask('x')['ok'])

    def test_bad_input_gives_errors_not_content(self):
        page = Page('enva', 'mail', self.keys.public)
        sealed = page.seal('1' * 32, {'action': 'overview'})
        # sealed for another environment, another id or another module: cannot be opened
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'envb', sealed), {'id': '1' * 32, 'error': 'bad_request'})
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'enva', {**sealed, 'id': '2' * 32})['error'], 'bad_request')
        (self.root / 'conf' / 'alice.calendar').write_text('')
        both = relayd.load_handlers(self.root / 'conf', 'alice', 'enva')
        try:
            self.assertEqual(relayd.answer(self.keys, both, 'enva', {**sealed, 'module': 'calendar'})['error'], 'bad_request')
        finally:
            for h in both.values():
                h.stop()
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'enva', {**sealed, 'module': 'calendar'})['error'], 'not_served')
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'enva', {**sealed, 'module': '../mail'})['error'], 'not_served')

    def test_expired_and_replayed_requests_get_no_answer(self):
        page, seen = Page('enva', 'mail', self.keys.public), relayd.Seen()
        old = page.seal('3' * 32, {'action': 'overview', 'ts': (time.time() - 600) * 1000})
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'enva', old, seen)['error'], 'expired')
        fresh = page.seal('4' * 32, {'action': 'overview'})
        self.assertNotIn('error', relayd.answer(self.keys, self.handlers, 'enva', fresh, seen))
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'enva', fresh, seen)['error'], 'replay')

    def test_replay_is_refused_after_a_restart(self):
        page, path = Page('enva', 'mail', self.keys.public), self.root / 'seen.json'
        req = page.seal('8' * 32, {'action': 'x'})
        self.assertNotIn('error', relayd.answer(self.keys, self.handlers, 'enva', req, relayd.Seen(path)))
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'enva', req, relayd.Seen(path))['error'], 'replay')
        path.write_text('not json')
        self.assertNotIn('error', relayd.answer(self.keys, self.handlers, 'enva', page.seal('9' * 32, {'action': 'x'}), relayd.Seen(path)))

    def test_handler_that_stops_reading_does_not_block(self):
        with unittest.mock.patch.object(relayd, 'CALL_TIMEOUT', 2):
            t0 = time.time()
            self.assertEqual(self.ask('hang'), {'ok': False, 'error': 'handler_down'})  # child busy, not reading stdin
            self.assertLess(time.time() - t0, 10)
        h = self.handlers['mail']
        h._start()
        h.proc.stdin.close()  # a write that cannot succeed is an error, not a hang
        with self.assertRaises(RuntimeError):
            h.ask({'op': 'call', 'action': 'x', 'args': {'big': 'y' * 10_000_000}}, 2)

    def test_big_answers_are_refused_and_batches_split(self):
        page = Page('enva', 'mail', self.keys.public)
        with unittest.mock.patch.object(relayd, 'REPLY_MAX', 50):
            self.assertEqual(relayd.answer(self.keys, self.handlers, 'enva', page.seal('5' * 32, {'action': 'overview'}))['error'], 'too_large')
        with unittest.mock.patch.object(relayd, 'BATCH_MAX', 1000):
            parts = relayd.batches([{'id': str(i), 'ct': 'x' * 400} for i in range(5)])
        self.assertEqual([len(p) for p in parts], [1, 1, 1, 1, 1])
        self.assertEqual(relayd.batches([]), [])

    def serve(self, post, until):
        calls = []

        def wrapped(url, payload):
            calls.append(payload)
            return post(len(calls), payload)
        with unittest.mock.patch.object(relayd.time, 'sleep'):
            relayd.serve(self.keys, self.handlers, 'enva', 'https://x/', 'tok', request=wrapped, stop=lambda: len(calls) >= until)
        return calls

    def test_site_refusing_a_batch_turns_it_into_errors(self):
        page = Page('enva', 'mail', self.keys.public)
        calls = self.serve(lambda n, p: (200, {'requests': [page.seal('6' * 32, {'action': 'x'})]}) if n == 1 else
                           ((413, {}) if n == 2 else (200, {'requests': []})), 3)
        self.assertIn('ct', calls[1]['replies'][0])
        self.assertEqual(calls[2]['replies'], [{'id': '6' * 32, 'error': 'too_large'}])

    def test_refused_error_batch_is_dropped(self):
        page = Page('enva', 'mail', self.keys.public)
        calls = self.serve(lambda n, p: (200, {'requests': [page.seal('7' * 32, {'action': 'x'})]}) if n == 1 else
                           ((413, {}) if n <= 3 else (200, {'requests': []})), 4)
        self.assertEqual(calls[2]['replies'], [{'id': '7' * 32, 'error': 'too_large'}])
        self.assertEqual(calls[3]['replies'], [])

    def test_serve_loop_answers_and_reports_status(self):
        page, rid = Page('enva', 'mail', self.keys.public), 'a' * 32
        calls = self.serve(lambda n, p: (200, {'requests': [page.seal(rid, {'action': 'x'})]}) if n == 1 else (200, {'requests': []}), 2)
        self.assertEqual(calls[0]['status']['pub'], self.keys.public)
        self.assertEqual(calls[0]['status']['modules']['mail']['bound'], True)
        self.assertEqual(calls[1]['replies'][0]['id'], rid)
        self.assertTrue(page.open(rid, calls[1]['replies'][0])['ok'])

    def test_refused_token_stops_the_worker(self):
        with self.assertRaises(PermissionError):
            relayd.serve(self.keys, self.handlers, 'enva', 'https://x/', 'tok', request=lambda u, p: (403, {'error': 'forbidden'}))

    @unittest.skipUnless(shutil.which('node'), 'node not installed')
    def test_browser_envelope_interoperates(self):
        rid = 'b' * 32
        script = f'''
const HomeRelay = require({json.dumps(str(UI))});
const fs = require('fs');
(async () => {{
  const r = HomeRelay(null, {{env: 'enva', module: 'mail', op: 'mail_call'}});
  const req = await r._seal({json.dumps(rid)}, {{action: 'overview', args: {{}}, ts: Date.now()}}, {json.dumps(self.keys.public)});
  process.stdout.write(JSON.stringify(req) + '\\n');
  const ans = JSON.parse(fs.readFileSync(0, 'utf8'));
  const out = await r._open({json.dumps(rid)}, ans);
  process.stdout.write(JSON.stringify(out));
}})().catch(e => {{ console.error(e); process.exit(1); }});
'''
        proc = subprocess.Popen(['node', '-e', script], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        req = json.loads(proc.stdout.readline())
        ans = relayd.answer(self.keys, self.handlers, 'enva', {**req, 'module': 'mail'})
        out, _ = proc.communicate(json.dumps(ans), timeout=30)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual(json.loads(out)['result']['action'], 'overview')


if __name__ == '__main__':
    unittest.main()
