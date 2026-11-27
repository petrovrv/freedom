"""F14b-4: real encryption and local worker authorization, no external service."""
import copy
import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock

import relayd
from relay_client import RelayClient, RelayError

SCHEMA = {'type': 'object', 'properties': {'query': {'type': 'string'}},
          'required': ['query'], 'additionalProperties': False}


class DispatchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.keys = relayd.Keys(Path(self.temp.name) / 'key.pem')
        self.client = RelayClient('test-env', 'test-app', self.keys.public)
        self.handler = Mock()
        self.handler.agent_actions = {'read': {'action': 'local_read', 'version': '1.0.0', 'args': copy.deepcopy(SCHEMA)}}
        self.handler.call.return_value = {'ok': True, 'result': {'echo': 'private'}}

    def packet(self, action='read', args=None, version='1.0.0'):
        req = self.client.seal(action, {'query': 'private'} if args is None else args, app_version=version)
        req['dispatch'] = {'kind': 'app-action-v1', 'actor': 'agent', 'env': 'test-env', 'module': 'test-app',
                           'jobId': 'JOB-test', 'action': 'read', 'version': '1.0.0', 'outward': False,
                           'schema': copy.deepcopy(SCHEMA), 'envelopeHash': hashlib.sha256(
                               '\n'.join(req[k] for k in ('id', 'pk', 'iv', 'ct')).encode()).hexdigest()}
        return req

    def answer(self, req, seen=None):
        return relayd.answer(self.keys, {'test-app': self.handler}, 'test-env', req, seen=seen)

    def refused(self, req):
        self.handler.call.reset_mock()
        with self.assertRaises(RelayError):
            self.client.open(req['id'], self.answer(req))
        self.handler.call.assert_not_called()
        self.client.discard(req['id'])

    def test_real_crypto_dispatches_only_registered_handler(self):
        req = self.packet()
        self.assertEqual(self.client.open(req['id'], self.answer(req)), {'echo': 'private'})
        self.handler.call.assert_called_once_with({'action': 'local_read', 'args': {'query': 'private'}})

    def test_encrypted_write_cannot_hide_behind_read(self):
        self.refused(self.packet(action='send'))

    def test_marker_and_dispatch_both_required(self):
        req = self.packet(); del req['dispatch']; self.refused(req)
        req = self.client.seal('read', {'query': 'private'})
        d = self.packet()['dispatch']
        d['envelopeHash'] = hashlib.sha256('\n'.join(req[k] for k in ('id', 'pk', 'iv', 'ct')).encode()).hexdigest()
        req['dispatch'] = d
        self.refused(req)

    def test_metadata_binding_and_local_opt_in(self):
        for key, value in [('kind', 'other'), ('actor', 'person'), ('env', 'other'), ('module', 'other'),
                           ('jobId', ''), ('action', 'send'), ('version', '2.0.0'), ('outward', True),
                           ('envelopeHash', 'f' * 64)]:
            with self.subTest(key=key):
                req = self.packet(); req['dispatch'][key] = value; self.refused(req)
        self.refused(self.packet(version='2.0.0'))
        self.handler.agent_actions = {}
        self.refused(self.packet())

    def test_both_schemas_checked_with_strict_types(self):
        self.refused(self.packet(args={'query': 'x', 'actor': 'person'}))
        self.refused(self.packet(args={'query': True}))
        self.refused(self.packet(args={}))
        req = self.packet(); req['dispatch']['schema']['properties']['query']['enum'] = ['other']; self.refused(req)
        self.handler.agent_actions['read']['args']['properties']['query']['enum'] = ['other']
        self.refused(self.packet())

    def test_no_replay(self):
        req = self.packet(); seen = relayd.Seen()
        self.client.open(req['id'], self.answer(req, seen))
        self.assertEqual(self.answer(req, seen)['error'], 'replay')
        self.handler.call.assert_called_once()

    def test_legacy_page_still_works(self):
        req = self.client.seal('page_operation', {'legacy': True})
        self.client.open(req['id'], self.answer(req))
        self.assertEqual(self.handler.call.call_args[0][0]['action'], 'page_operation')

    def test_schema_unknown_type_and_bool_are_not_integer(self):
        for kind in ['integer', 'number', 'unknown']:
            schema = {'type': 'object', 'properties': {'n': {'type': kind}}, 'additionalProperties': False}
            self.assertFalse(relayd.app_args_valid(schema, {'n': True}))
        self.assertFalse(relayd.app_args_valid({'properties': {}}, {}))


if __name__ == '__main__':
    unittest.main()
