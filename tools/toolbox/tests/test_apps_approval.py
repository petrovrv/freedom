"""Private approval routing: no raw arguments, authenticated pending, fresh result key."""
import json
import unittest
from unittest.mock import patch
import test_apps as baseline

apps = baseline.apps


class ApprovalTest(unittest.TestCase):
    def setUp(self):
        baseline.RelayRouteTest.setUp(self)
        self.descriptor['outward'] = True
        self.status.update(outward=True, approvalProtocol=1)
        self.rid, self.rev = 'b' * 16, 'c' * 32
        self.pending = {'requestId': self.rid, 'revision': self.rev, 'expires': 1795788000}
        self.response = dict(self.pending, ok=True, pending=True, envelope={'iv': 'iv', 'ct': 'ct'})

    def run_action(self):
        return baseline.RelayRouteTest.run_action(self)

    def test_staging_only_after_protocol_check(self):
        for version in (None, 0, True, '1'):
            with self.subTest(version=version), patch.object(apps, '_call', side_effect=[
                    (self.listing, None), (dict(self.status, approvalProtocol=version), None)]) as call:
                self.assertTrue(self.run_action()[1])
                self.assertEqual(call.call_count, 2)
                self.assertNotIn('private-test-value', str(call.call_args_list))

    def test_pending_authenticated_and_no_plaintext(self):
        with patch.object(apps, '_call', side_effect=[(self.listing, None), (self.status, None),
                          (self.response, None)]) as call, patch.object(apps, '_relay_client') as factory:
            client = factory.return_value.return_value
            client.seal.return_value = self.envelope
            client.open.return_value = self.pending
            text, failed = self.run_action()
            self.assertFalse(failed, text)
            self.assertEqual(json.loads(text)['requestId'], self.rid)
            self.assertEqual(json.loads(text)['expiresUtc'], '2026-11-27T14:00:00Z')
            self.assertEqual(call.call_args.args[0], 'app_relay_propose')
            self.assertNotIn('private-test-value', str(call.call_args_list))
            client.seal.assert_called_once_with('lookup', self.params, app_version='1.0.0', approval={'phase': 'stage'})
            client.discard.assert_called_once_with(self.envelope['id'])

    def test_failed_or_mismatched_stage_is_not_pending(self):
        for result in (None, {}, dict(self.pending, requestId='other'), dict(self.pending, revision='other')):
            with self.subTest(result=result), patch.object(apps, '_call', side_effect=[
                    (self.listing, None), (self.status, None), (self.response, None)]), \
                    patch.object(apps, '_relay_client') as factory:
                client = factory.return_value.return_value
                client.seal.return_value = self.envelope
                client.open.return_value = result
                self.assertTrue(self.run_action()[1])
                client.discard.assert_called_once()

    def result_status(self):
        return dict(self.status, requestId=self.rid, revision=self.rev, status='running')

    def test_result_fresh_key_no_approval_resubmit(self):
        result = dict(self.pending, state='done', result={'private': 'private-result'})
        with patch.object(apps, '_call', side_effect=[(self.result_status(), None),
                          ({'ok': True, 'envelope': {'iv': 'iv', 'ct': 'ct'}}, None)]) as call, \
                patch.object(apps, '_relay_client') as factory:
            client = factory.return_value.return_value
            client.seal.return_value = self.envelope
            client.open.return_value = result
            text, failed = apps.app_action_status({'requestId': self.rid})
            self.assertFalse(failed, text)
            self.assertEqual(json.loads(text)['result'], result['result'])
            client.seal.assert_called_once_with('lookup', {}, app_version='1.0.0',
                approval={'phase': 'result', 'requestId': self.rid, 'revision': self.rev})
            self.assertEqual(call.call_args.args[0], 'app_relay_result')
            self.assertNotIn('private-result', str(call.call_args_list))

    def test_result_mismatch_or_error_is_sanitized(self):
        for result in ({}, dict(self.pending, state='done', revision='other')):
            with self.subTest(result=result), patch.object(apps, '_call', side_effect=[
                    (self.result_status(), None), ({'ok': True, 'envelope': {}}, None)]), \
                    patch.object(apps, '_relay_client') as factory:
                client = factory.return_value.return_value
                client.seal.return_value = self.envelope
                client.open.return_value = result
                self.assertTrue(apps.app_action_status({'requestId': self.rid})[1])

    def test_result_timeout_never_executes(self):
        with patch.object(apps, '_call', side_effect=[(self.result_status(), None),
                          (None, 'private-result')]) as call, patch.object(apps, '_relay_client') as factory:
            factory.return_value.return_value.seal.return_value = self.envelope
            text, failed = apps.app_action_status({'requestId': self.rid})
            self.assertTrue(failed)
            self.assertNotIn('private-result', text)
            self.assertEqual(call.call_count, 2)
            self.assertEqual(call.call_args.args[0], 'app_relay_result')

    def test_denied_and_expired_are_metadata_only(self):
        for state in ('denied', 'expired'):
            with self.subTest(state=state), patch.object(apps, '_call', return_value=(
                    dict(self.result_status(), status=state), None)) as call:
                text, failed = apps.app_action_status({'requestId': self.rid})
                self.assertFalse(failed)
                self.assertEqual(json.loads(text)['status'], state)
                self.assertEqual(call.call_count, 1)


if __name__ == '__main__':
    unittest.main()
