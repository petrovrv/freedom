# SPDX-License-Identifier: AGPL-3.0-or-later
import copy
import hashlib
import tempfile
import time
import unittest
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import Mock

import relayd
from approvals import Proposals
from relay_client import RelayClient, RelayError

SCHEMA = {'type': 'object', 'properties': {'target': {'type': 'string'}}, 'required': ['target'], 'additionalProperties': False}


class ApprovalTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name)
        self.keys = relayd.Keys(self.path / 'key.pem')
        self.store = Proposals(self.path / 'proposals.sqlite')
        self.agent = RelayClient('env', 'app', self.keys.public)
        self.page = RelayClient('env', 'app', self.keys.public)
        self.handler = Mock()
        self.handler.agent_actions = {}
        self.handler.page_actions = ['safe']
        self.handler.outward_actions = {'send': {'action': 'deliver', 'version': '1', 'args': SCHEMA}}
        self.handler.call.return_value = {'ok': True, 'result': {'private': 'receipt'}}
        self.binding = {'requestId': 'a' * 16, 'revision': 'b' * 32}

    def packet(self, phase, client=None, token=None):
        client = client or (self.agent if phase in ('stage', 'result') else self.page)
        marker = {'phase': phase, **({} if phase == 'stage' else self.binding)}
        if token: marker['previewToken'] = token
        req = client.seal('send', {'target': 'private-target'} if phase == 'stage' else {}, app_version='1', approval=marker)
        req['dispatch'] = {'kind': 'app-approval-v1', 'phase': phase, 'actor': 'agent' if phase in ('stage', 'result') else 'person',
                           'env': 'env', 'module': 'app', 'action': 'send', 'version': '1', 'outward': True,
                           **self.binding, 'expires': int(time.time()) + 1800, 'schema': SCHEMA,
                           'grantExpires': int(time.time()) + 45,
                           'envelopeHash': hashlib.sha256('\n'.join(req[k] for k in ('id', 'pk', 'iv', 'ct')).encode()).hexdigest()}
        return client, req

    def execute(self, packet, store=None):
        client, req = packet
        answer = relayd.answer(self.keys, {'app': self.handler}, 'env', req, proposals=store or self.store)
        return client.open(req['id'], answer)

    def stage_preview(self):
        self.execute(self.packet('stage'))
        return self.execute(self.packet('preview'))['previewToken']

    def test_full_lifecycle_and_durable_restart(self):
        token = self.stage_preview()
        result = self.execute(self.packet('run', token=token))
        self.assertEqual(result['state'], 'done')
        self.assertEqual(result['result']['result'], {'private': 'receipt'})
        restarted = Proposals(self.path / 'proposals.sqlite')
        with self.assertRaises(RelayError): self.execute(self.packet('run', token=token), restarted)
        self.assertEqual(self.execute(self.packet('result'), restarted)['state'], 'done')
        self.handler.call.assert_called_once_with({'action': 'deliver', 'args': {'target': 'private-target'}})

    def test_concurrent_run_and_two_previews(self):
        token = self.stage_preview()
        other = RelayClient('env', 'app', self.keys.public)
        token2 = self.execute(self.packet('preview', client=other))['previewToken']
        packets = [self.packet('run', token=token), self.packet('run', client=other, token=token2)]
        def go(packet):
            try: return self.execute(packet)['state']
            except RelayError: return 'refused'
        with ThreadPoolExecutor(2) as pool: results = list(pool.map(go, packets))
        self.assertEqual(sorted(results), ['done', 'refused'])
        self.assertEqual(self.handler.call.call_count, 1)

    def test_lost_handler_result_is_never_retried(self):
        token = self.stage_preview()
        self.handler.call.side_effect = RuntimeError('crash after side effect')
        with self.assertRaises(RelayError): self.execute(self.packet('run', token=token))
        restarted = Proposals(self.path / 'proposals.sqlite')
        self.assertEqual(self.execute(self.packet('result'), restarted)['state'], 'executing')
        with self.assertRaises(RelayError): self.execute(self.packet('run', token=token), restarted)
        self.assertEqual(self.handler.call.call_count, 1)

    def test_no_preview_wrong_key_wrong_binding_and_stale_grant(self):
        self.execute(self.packet('stage'))
        with self.assertRaises(RelayError): self.execute(self.packet('run'))
        token = self.execute(self.packet('preview'))['previewToken']
        other = RelayClient('env', 'app', self.keys.public)
        with self.assertRaises(RelayError): self.execute(self.packet('run', client=other, token=token))
        for field, value in [('revision', 'c' * 32), ('action', 'other'), ('module', 'other'), ('env', 'other'),
                             ('actor', 'agent'), ('version', '2'), ('envelopeHash', '0' * 64), ('grantExpires', 1)]:
            packet = self.packet('run', token=token); packet[1]['dispatch'][field] = value
            with self.subTest(field=field), self.assertRaises(RelayError): self.execute(packet)
        self.handler.call.assert_not_called()

    def test_malformed_handler_response_is_ambiguous(self):
        token = self.stage_preview()
        self.handler.call.return_value = {'ok': False, 'error': 'handler_error'}
        self.assertEqual(self.execute(self.packet('run', token=token))['state'], 'executing')
        with self.assertRaises(RelayError): self.execute(self.packet('run', token=token))
        self.assertEqual(self.handler.call.call_count, 1)

    def test_immutable_staging_and_expiry(self):
        self.execute(self.packet('stage'))
        with self.assertRaises(RelayError): self.execute(self.packet('stage'))
        with self.store.connect() as db: db.execute('UPDATE proposals SET expires=1')
        self.assertEqual(self.execute(self.packet('result'))['state'], 'expired')
        with self.assertRaises(RelayError): self.execute(self.packet('preview'))
        self.handler.call.assert_not_called()

    def test_legacy_alias_cannot_execute(self):
        self.handler.page_actions += ['deliver']
        req = self.agent.seal('deliver', {'target': 'private'})
        with self.assertRaises(RelayError): self.execute((self.agent, req))
        marker = self.packet('stage'); del marker[1]['dispatch']
        with self.assertRaises(RelayError): self.execute(marker)
        self.handler.call.assert_not_called()

    def test_malformed_legacy_request_does_not_crash_worker(self):
        req = self.agent.seal('safe', {})
        req['dispatch'] = 'invalid'
        with self.assertRaises(RelayError): self.execute((self.agent, req))
        self.handler.call.assert_not_called()

    def test_local_schema_change_and_invalid_stage(self):
        packet = self.packet('stage'); packet[1]['dispatch']['schema'] = {'type': 'invalid'}
        with self.assertRaises(RelayError): self.execute(packet)
        token = self.stage_preview()
        self.handler.outward_actions['send']['args'] = copy.deepcopy(SCHEMA)
        self.handler.outward_actions['send']['args']['required'] = []
        with self.assertRaises(RelayError): self.execute(self.packet('run', token=token))
        self.handler.call.assert_not_called()


    def test_service_isolated_python_loads_durable_store(self):
        (self.path / 'relay').write_text('synthetic-local-test-token')
        config = self.path / 'config'; (config / 'modules.d').mkdir(parents=True)
        child = {**os.environ, 'CREDENTIALS_DIRECTORY': str(self.path), 'STATE_DIRECTORY': '.',
                 'HOME_RELAY_CONFIG': str(config), 'AIWB_ENV': 'env', 'RELAY_URL': 'https://example.invalid/unused'}
        code = "import runpy,sys\ng=runpy.run_path(sys.argv[1]); sys.argv=['relayd.py','test']\ndef local_only(*args, **kw):\n    assert kw['proposals'] is not None\n    print('isolated-start PASS')\ng['main'].__globals__['serve']=local_only\ng['main']()\n"
        done = subprocess.run([sys.executable, '-I', '-c', code, str(Path(relayd.__file__))], env=child, cwd=self.path, capture_output=True, text=True, timeout=15)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn('isolated-start PASS', done.stdout)


if __name__ == '__main__': unittest.main()
