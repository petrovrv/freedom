# SPDX-License-Identifier: AGPL-3.0-or-later
import base64
import unittest
from unittest.mock import patch

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

import relayd
from relay_client import RelayClient, RelayError


class Echo:
    def __init__(self):
        self.calls = 0

    def call(self, request):
        self.calls += 1
        return {'ok': True, 'result': request['args']}


class ClientTests(unittest.TestCase):
    def setUp(self):
        # In-memory worker keys too: no test private key is written to disk.
        self.keys = object.__new__(relayd.Keys)
        self.keys.private = ec.generate_private_key(ec.SECP256R1())
        self.keys.public = base64.b64encode(self.keys.private.public_key().public_bytes(
            serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)).decode()
        self.client = RelayClient('enva', 'mail', self.keys.public)
        self.handler = Echo()
        self.handlers = {'mail': self.handler, 'calendar': self.handler}

    def answer(self, req, **kwargs):
        return relayd.answer(self.keys, self.handlers, 'enva', req, **kwargs)

    def test_roundtrip_unicode_and_multiple_pending(self):
        first = self.client.seal('list', {'text': '\u2603'})
        second = self.client.seal('list', {'text': 'second'})
        self.assertNotEqual(first['id'], second['id'])
        self.assertEqual(self.client.open(second['id'], self.answer(second)), {'text': 'second'})
        self.assertEqual(self.client.open(first['id'], self.answer(first)), {'text': '\u2603'})

    def test_request_binding_and_tamper(self):
        req = self.client.seal('list')
        for changed in ({**req, 'id': 'f' * 32}, {**req, 'module': 'calendar'},
                        {**req, 'ct': 'AAAA' + req['ct'][4:]}):
            self.assertEqual(self.answer(changed)['error'], 'bad_request')
        self.assertEqual(relayd.answer(self.keys, self.handlers, 'envb', req)['error'], 'bad_request')
        self.assertEqual(self.handler.calls, 0)

    def test_response_binding_and_tamper_do_not_consume(self):
        req = self.client.seal('list', {'ok': 1})
        answer = self.answer(req)
        for changed in ({**answer, 'id': 'f' * 32}, {**answer, 'iv': '!'},
                        {**answer, 'ct': 'AAAA' + answer['ct'][4:]}, req):
            with self.assertRaisesRegex(RelayError, 'bad_answer'):
                self.client.open(req['id'], changed)
        for env, module in [('envb', 'mail'), ('enva', 'calendar')]:
            changed = self.keys.seal(env, module, req['id'], base64.b64decode(req['pk']),
                                     {'ok': True, 'result': 'wrong binding'})
            with self.assertRaisesRegex(RelayError, 'bad_answer'):
                self.client.open(req['id'], changed)
        self.assertEqual(self.client.open(req['id'], answer), {'ok': 1})
        with self.assertRaisesRegex(RelayError, 'unknown_request'):
            self.client.open(req['id'], answer)

    def test_worker_replay_and_freshness_boundary(self):
        with patch('relay_client.time.time', return_value=1000):
            req = self.client.seal('list')
        seen = relayd.Seen()
        self.assertNotIn('error', self.answer(req, seen=seen, now=1120))
        self.assertEqual(self.answer(req, seen=seen, now=1120)['error'], 'replay')
        self.assertEqual(self.answer(req, now=1120.001)['error'], 'expired')
        self.assertEqual(self.answer(req, now=879.999)['error'], 'expired')
        self.assertEqual(self.handler.calls, 1)

    def test_pending_expiry_capacity_and_discard(self):
        with patch('relay_client.time.monotonic', return_value=100):
            requests = [self.client.seal('list') for _ in range(6)]
            with self.assertRaisesRegex(RelayError, 'busy'):
                self.client.seal('list')
            self.client.discard(requests[0]['id'])
            self.client.seal('list')
        with patch('relay_client.time.monotonic', return_value=220):
            with self.assertRaisesRegex(RelayError, 'unknown_request'):
                self.client.open(requests[1]['id'], self.answer(requests[1]))
            self.client.seal('list')

    def test_untrusted_transport_error_and_authenticated_worker_error(self):
        req = self.client.seal('list')
        with self.assertRaisesRegex(RelayError, '^transport_error$'):
            self.client.open(req['id'], {'id': req['id'], 'error': 'untrusted text'})
        answer = self.keys.seal('enva', 'mail', req['id'], base64.b64decode(req['pk']),
                                {'ok': False, 'error': 'untrusted text'})
        with self.assertRaisesRegex(RelayError, '^worker_error$'):
            self.client.open(req['id'], answer)
        with self.assertRaisesRegex(RelayError, 'unknown_request'):
            self.client.open(req['id'], answer)

    def test_bad_inputs(self):
        for env, mod in [('', 'mail'), ('env|mail', 'mail'), ('enva', '../mail')]:
            with self.assertRaises(RelayError):
                RelayClient(env, mod, self.keys.public)
        with self.assertRaises(RelayError):
            RelayClient('enva', 'mail', 'not a key')
        for action, args in [('', {}), ('list', []), (None, {})]:
            with self.assertRaises(RelayError):
                self.client.seal(action, args)


if __name__ == '__main__':
    unittest.main()
