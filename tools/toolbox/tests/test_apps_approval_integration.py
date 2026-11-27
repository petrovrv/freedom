"""Real crypto and durable proposal integration; all actions are harmless local fakes."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch
import test_apps_relay_integration as base


@unittest.skipUnless((base.WORKER.parent / 'approvals.py').is_file(), 'set AIWB_MODULES_DIR to approval module')
class ApprovalIntegration(unittest.TestCase):
    def setUp(self):
        base.RelayIntegration.setUp(self)
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        spec = importlib.util.spec_from_file_location('test_proposals', base.WORKER.parent / 'approvals.py')
        self.store_module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.store_module)
        self.store = self.store_module.Proposals(Path(self.tmp.name) / 'proposals.sqlite')
        self.handler.outward_actions = {'lookup': {'action': 'search', 'version': '1.0.0', 'args': self.schema}}
        self.handler.agent_actions = {}
        self.rid, self.rev = 'b' * 16, 'c' * 32
        self.expiry = int(time.time()) + 1200
        self.page_client = base.apps._relay_client()('env-a', 'sample-app', self.keys.public)
        self.preview_token = None

    def dispatch(self, envelope, phase, actor):
        request = dict(envelope, module='sample-app')
        request['dispatch'] = {'kind': 'app-approval-v1', 'actor': actor, 'env': 'env-a',
            'module': 'sample-app', 'jobId': 'JOB-TEST', 'action': 'lookup', 'version': '1.0.0',
            'outward': True, 'schema': self.schema, 'phase': phase, 'requestId': self.rid,
            'revision': self.rev, 'expires': self.expiry, 'grantExpires': int(time.time()) + 55,
            'envelopeHash': hashlib.sha256('\n'.join(request[k] for k in ('id', 'pk', 'iv', 'ct')).encode()).hexdigest()}
        answer = self.worker.answer(self.keys, {'sample-app': self.handler}, 'env-a', request, proposals=self.store)
        response = {k: answer[k] for k in ('iv', 'ct')} if 'ct' in answer else {'error': answer['error']}
        self.wire.append(json.dumps([request, response]))
        return response

    def route(self, op, params):
        if op == 'app_list':
            return {'apps': [{'app': 'sample-app', 'actions': [{'op': 'lookup', 'via': 'home-relay', 'outward': True}]}]}, None
        status = {'ok': True, 'via': 'home-relay', 'env': 'env-a', 'app': 'sample-app', 'action': 'lookup',
                  'version': '1.0.0', 'outward': True, 'approvalProtocol': 1,
                  'relay': {'pub': self.keys.public, 'online': True, 'served': True}}
        if op == 'app_relay_status':
            return status, None
        if op == 'app_action_status':
            return dict(status, requestId=self.rid, revision=self.rev, status='running'), None
        self.assertIn(op, ('app_relay_propose', 'app_relay_result'))
        phase = 'stage' if op == 'app_relay_propose' else 'result'
        response = self.dispatch(params['envelope'], phase, 'agent')
        return {'ok': True, 'envelope': response, 'requestId': self.rid, 'revision': self.rev, 'pending': True}, None

    def stage(self):
        with patch.object(base.apps, '_call', side_effect=self.route):
            text, failed = base.apps.app_action({'app': 'sample-app', 'action': 'lookup', 'args': {'query': 'PRIVATE-approval'}})
        self.assertFalse(failed, text)
        self.assertEqual(json.loads(text)['status'], 'pending')
        self.assertEqual(self.calls, [])

    def page(self, phase):
        client = self.page_client
        approval = {'phase': phase, 'requestId': self.rid, 'revision': self.rev}
        if phase == 'run' and self.preview_token is not None:
            approval['previewToken'] = self.preview_token
        envelope = client.seal('lookup', {}, app_version='1.0.0',
                               approval=approval)
        value = client.open(envelope['id'], self.dispatch(envelope, phase, 'person'))
        if phase == 'preview':
            self.preview_token = value['previewToken']
        return value

    def test_private_stage_preview_run_and_fresh_result(self):
        self.stage()
        preview = self.page('preview')
        self.assertEqual(preview['args'], {'query': 'PRIVATE-approval'})
        self.assertEqual(self.calls, [])
        result = self.page('run')
        self.assertEqual(result['state'], 'done')
        self.assertEqual(len(self.calls), 1)
        # Reopen durable store: completed action cannot be executed twice.
        self.store = self.store_module.Proposals(Path(self.tmp.name) / 'proposals.sqlite')
        with self.assertRaises(ValueError):
            self.page('run')
        with patch.object(base.apps, '_call', side_effect=self.route):
            text, failed = base.apps.app_action_status({'requestId': self.rid})
        self.assertFalse(failed, text)
        self.assertEqual(json.loads(text)['status'], 'done')
        self.assertEqual(len(self.calls), 1)
        self.assertNotIn('PRIVATE-approval', '\n'.join(self.wire))

    def test_run_requires_preview_in_same_browser_key(self):
        self.stage()
        with self.assertRaises(ValueError):
            self.page('run')
        self.assertEqual(self.calls, [])
        self.page('preview')
        self.page_client = base.apps._relay_client()('env-a', 'sample-app', self.keys.public)
        with self.assertRaises(ValueError):
            self.page('run')
        self.assertEqual(self.calls, [])

    def test_another_tab_preview_does_not_invalidate_first_tab(self):
        self.stage()
        self.page('preview')
        first_client, first_token = self.page_client, self.preview_token
        self.page_client = base.apps._relay_client()('env-a', 'sample-app', self.keys.public)
        self.page('preview')
        self.page_client, self.preview_token = first_client, first_token
        self.assertEqual(self.page('run')['state'], 'done')
        self.assertEqual(len(self.calls), 1)


if __name__ == '__main__':
    unittest.main()
