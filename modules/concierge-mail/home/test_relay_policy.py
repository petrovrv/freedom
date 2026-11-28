"""0.19.0: the policy the deploy registers at the home relay (home-relay 0.2.1+ denies every call not listed)."""
import ast
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import compose
import relay

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('hubctl_policy_test', HERE / 'hub' / 'hubctl.py')
hubctl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hubctl)


class RelayPolicyTests(unittest.TestCase):
    def test_page_actions_are_exactly_the_handler_actions(self):
        self.assertEqual(len(hubctl.PAGE_ACTIONS), len(set(hubctl.PAGE_ACTIONS)))
        self.assertEqual(set(hubctl.PAGE_ACTIONS), set(relay.READ_ACTIONS) | set(compose.ACTIONS))

    def test_handler_refuses_what_is_not_listed(self):
        api = relay.MailApi.__new__(relay.MailApi)
        api.composer = None
        with self.assertRaises(ValueError):
            api.handle({'action': 'compose_send', 'args': {}})  # no composer: writing actions are not reachable
        with self.assertRaises(ValueError):
            api.handle({'action': 'shell', 'args': {}})

    def test_deploy_registers_page_actions_only(self):
        tree = ast.parse((HERE / 'hub' / 'hubctl.py').read_text(encoding='utf-8'))
        calls = [n for n in ast.walk(tree) if isinstance(n, ast.Call) and getattr(n.func, 'attr', '') == 'register'
                 and n.args[1:2] and isinstance(n.args[1], ast.Constant) and n.args[1].value == 'concierge-mail']
        self.assertEqual(len(calls), 1)
        kw = {k.arg: k.value for k in calls[0].keywords}
        self.assertIsInstance(kw.get('page_actions'), ast.Name)
        self.assertEqual(kw['page_actions'].id, 'PAGE_ACTIONS')
        self.assertNotIn('agent_actions', kw)
        self.assertNotIn('outward_actions', kw)

    def test_relayctl_accepts_the_policy(self):
        relayctl = hubctl.relayctl
        with patch.object(relayctl, 'ssh', return_value='') as ssh, patch.object(relayctl, 'credentials_dropin'), \
                patch.object(relayctl, 'restart_running', return_value=[]):
            relayctl.register(None, 'concierge-mail', hubctl.HANDLER, page_actions=hubctl.PAGE_ACTIONS)
        got = json.loads(ssh.call_args_list[0].args[2])
        self.assertEqual(got['pageActions'], list(hubctl.PAGE_ACTIONS))
        self.assertEqual((got['agentActions'], got['outwardActions']), ({}, {}))


if __name__ == '__main__':
    unittest.main()
