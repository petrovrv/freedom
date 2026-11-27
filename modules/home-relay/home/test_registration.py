"""Registration policy survives module redeploy; all remote operations are mocked."""
import importlib.util
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / 'hub'))
import relayctl


class RegistrationTests(unittest.TestCase):
    def register(self, **policies):
        with patch.object(relayctl, 'ssh', return_value='') as ssh, \
                patch.object(relayctl, 'credentials_dropin'), \
                patch.object(relayctl, 'restart_running', return_value=[]):
            relayctl.register(None, 'test-app', ['/usr/bin/handler'], **policies)
        return json.loads(ssh.call_args_list[0].args[2])

    def test_policy_repeated_deploy_and_removal(self):
        for _ in range(2):
            spec = self.register(page_actions=['overview'])
            self.assertEqual(spec['pageActions'], ['overview'])
            self.assertEqual(spec['agentActions'], {})
            self.assertEqual(spec['outwardActions'], {})
        self.assertEqual(self.register()['pageActions'], [])

    def test_invalid_policy_has_no_remote_effect(self):
        rule = {'action': 'send', 'version': '1.0', 'args':
                {'type': 'object', 'properties': {}, 'additionalProperties': False}}
        cases = [{'page_actions': 'overview'}, {'page_actions': ['*']},
                 {'agent_actions': []}, {'outward_actions': {'send': {}}},
                 {'page_actions': ['send'], 'outward_actions': {'send': rule}},
                 {'agent_actions': {'alias': rule}, 'outward_actions': {'send': rule}}]
        for policies in cases:
            with self.subTest(policies=policies), patch.object(relayctl, 'ssh') as ssh:
                with self.assertRaises(SystemExit):
                    relayctl.register(None, 'test-app', ['/usr/bin/handler'], **policies)
                ssh.assert_not_called()

    def test_explicit_action_maps_preserved(self):
        rule = {'action': 'deliver', 'version': '1.0', 'args':
                {'type': 'object', 'properties': {}, 'additionalProperties': False}}
        self.assertEqual(self.register(outward_actions={'send': rule})['outwardActions'], {'send': rule})

    def test_calendar_deploy_registers_only_page_operations(self):
        path = HERE.parents[1] / 'calendar' / 'home' / 'hub' / 'calctl.py'
        spec = importlib.util.spec_from_file_location('calendar_policy_test', path)
        calctl = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(calctl)
        with patch.object(calctl, 'ssh', return_value=''), patch.object(calctl, '_tar', return_value=b''), \
                patch.object(calctl, 'people', return_value=[]), patch.object(relayctl, 'register', return_value=[]) as register:
            calctl.deploy(None)
        policies = register.call_args.kwargs
        self.assertEqual(set(policies['page_actions']), {'overview', 'range', 'event', 'upcoming', 'calendar_set'})
        self.assertNotIn('agent_actions', policies)
        self.assertNotIn('outward_actions', policies)
        self.assertEqual(self.register(page_actions=policies['page_actions'])['agentActions'], {})


if __name__ == '__main__':
    unittest.main()
