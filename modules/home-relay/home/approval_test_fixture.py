# SPDX-License-Identifier: AGPL-3.0-or-later
"""Local-only synthetic crypto/queue worker for app-relay-test.php, never a service."""
import json
import sys
import threading
import time
from pathlib import Path
from unittest.mock import Mock
import relayd
from approvals import Proposals
from relay_client import RelayClient

root = Path(sys.argv[1]); directory = root / 'q' / 'owner'
keys = relayd.Keys(root / 'approval-fixture-key.pem')
store = Proposals(root / 'approval-fixture.sqlite')
handler = Mock()
schema = {'type': 'object', 'properties': {'query': {'type': 'string'}}, 'required': ['query'], 'additionalProperties': False}
handler.outward_actions = {'send': {'action': 'deliver', 'version': '1.0.0', 'args': schema}}
handler.page_actions = []; handler.agent_actions = {}
handler.call.return_value = {'ok': True, 'result': {'receipt': 'private-receipt'}}
agent = RelayClient('owner', 'relay-test', keys.public)
page = RelayClient('owner', 'relay-test', keys.public)
pending = {}; stopped = threading.Event(); errors = []


def worker():
    seen = relayd.Seen()
    while not stopped.is_set():
        for path in directory.glob('*.req'):
            try:
                req = json.loads(path.read_text())
                if req.get('dispatch', {}).get('kind') != 'app-approval-v1': continue
                assert 'private-target' not in path.read_text()
                answer = relayd.answer(keys, {'relay-test': handler}, 'owner', req, seen=seen, proposals=store)
                temp = directory / (req['id'] + '.fixture-tmp')
                temp.write_text(json.dumps(answer)); temp.replace(directory / (req['id'] + '.res'))
                path.unlink()
            except Exception as exc:
                errors.append(type(exc).__name__); stopped.set(); break
        stopped.wait(0.01)


(directory / 'status.json').write_text(json.dumps({'seen': int(time.time()), 'pub': keys.public,
    'modules': {'relay-test': {'relayAppProtocol': 1, 'relayApprovalProtocol': 1}}}))
thread = threading.Thread(target=worker, daemon=True); thread.start()
print('ready', flush=True)
try:
    for line in sys.stdin:
        cmd = json.loads(line)
        if cmd['op'] == 'seal':
            phase = cmd['phase']; client = agent if phase in ('stage', 'result') else page
            approval = {'phase': phase, **cmd.get('binding', {})}
            env = client.seal('send', {'query': 'private-target'} if phase == 'stage' else {}, app_version='1.0.0', approval=approval)
            pending[env['id']] = client
            print(json.dumps({k: env[k] for k in ('id', 'pk', 'iv', 'ct')}), flush=True)
        elif cmd['op'] == 'open':
            print(json.dumps(pending.pop(cmd['id']).open(cmd['id'], cmd['envelope'])), flush=True)
        elif cmd['op'] == 'finish':
            assert not errors, errors
            handler.call.assert_called_once_with({'action': 'deliver', 'args': {'query': 'private-target'}})
            print('approval-fixture PASS', flush=True); break
finally:
    stopped.set(); thread.join(5)
