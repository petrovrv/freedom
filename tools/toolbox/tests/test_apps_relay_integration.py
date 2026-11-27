"""Opt-in real crypto/worker integration: set AIWB_MODULES_DIR to tested modules.

No network, credentials, on-disk keys or external actions. The site boundary is
represented by its documented queue/response shape, never by a plaintext fallback.
"""
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import apps

MODULES = Path(os.environ.get('AIWB_MODULES_DIR', 'not-installed'))
WORKER = MODULES / 'home-relay' / 'home' / 'relayd.py'


@unittest.skipUnless(WORKER.is_file(), 'set AIWB_MODULES_DIR to tested home-relay installation')
class RelayIntegration(unittest.TestCase):
    def setUp(self):
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric import ec
        spec = importlib.util.spec_from_file_location('tested_relay_worker', WORKER)
        self.worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.worker)
        self.keys = object.__new__(self.worker.Keys)
        self.keys.private = ec.generate_private_key(ec.SECP256R1())
        self.keys.public = base64.b64encode(self.keys.private.public_key().public_bytes(
            serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)).decode()
        self.schema = {'type': 'object', 'properties': {'query': {'type': 'string'}},
                       'required': ['query'], 'additionalProperties': False}
        self.calls = []
        parent = self

        class Handler:
            agent_actions = {'lookup': {'action': 'search', 'version': '1.0.0', 'args': parent.schema}}

            def call(self, request):
                parent.calls.append(request)
                return {'ok': True, 'result': {'value': request['args']['query']}}

        self.handler = Handler()
        self.wire = []
        self.tamper = False

    def route(self, op, params):
        self.wire.append(json.dumps([op, params], ensure_ascii=False))
        if op == 'app_list':
            return {'apps': [{'app': 'sample-app', 'actions': [
                {'op': 'lookup', 'via': 'home-relay', 'outward': False}]}]}, None
        if op == 'app_relay_status':
            return {'ok': True, 'via': 'home-relay', 'env': 'env-a', 'app': 'sample-app',
                    'action': 'lookup', 'version': '1.0.0', 'outward': False,
                    'relay': {'pub': self.keys.public, 'online': True, 'served': True}, 'schema': self.schema}, None
        self.assertEqual(op, 'app_relay_call')
        self.assertEqual(set(params['envelope']), {'id', 'pk', 'iv', 'ct'})
        request = dict(params['envelope'], module='sample-app')
        request['dispatch'] = {'kind': 'app-action-v1', 'actor': 'agent', 'env': 'env-a',
            'module': 'sample-app', 'jobId': 'JOB-TEST', 'action': 'write' if self.tamper else params['action'],
            'version': params['version'], 'outward': False, 'schema': self.schema,
            'envelopeHash': hashlib.sha256('\n'.join(request[k] for k in ('id', 'pk', 'iv', 'ct')).encode()).hexdigest()}
        answer = self.worker.answer(self.keys, {'sample-app': self.handler}, 'env-a', request)
        # Production server removes the redundant id from the worker answer.
        response = {k: answer[k] for k in ('iv', 'ct')} if 'ct' in answer else {'error': answer['error']}
        self.wire.append(json.dumps(response))
        return {'ok': True, 'envelope': response}, None

    def test_real_crypto_roundtrip_has_no_plaintext_on_site(self):
        with patch.object(apps, '_call', side_effect=self.route):
            text, failed = apps.app_action({'app': 'sample-app', 'action': 'lookup',
                                           'args': {'query': 'PRIVATE snowman \u2603'}})
        self.assertFalse(failed, text)
        self.assertEqual(json.loads(text), {'value': 'PRIVATE snowman \u2603'})
        self.assertEqual(self.calls, [{'action': 'search', 'args': {'query': 'PRIVATE snowman \u2603'}}])
        self.assertNotIn('PRIVATE', '\n'.join(self.wire))

    def test_wrong_dispatch_cannot_call_handler(self):
        self.tamper = True
        with patch.object(apps, '_call', side_effect=self.route):
            text, failed = apps.app_action({'app': 'sample-app', 'action': 'lookup', 'args': {'query': 'PRIVATE'}})
        self.assertTrue(failed)
        self.assertEqual(self.calls, [])
        self.assertNotIn('PRIVATE', text + '\n'.join(self.wire))


if __name__ == '__main__':
    unittest.main()
