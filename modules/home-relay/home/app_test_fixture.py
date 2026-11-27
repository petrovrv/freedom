"""Local-only test child used by co-agent/tests/app-relay-test.php. Never deploy/run as service."""
import json
import sys
import time
from pathlib import Path
from unittest.mock import Mock
import relayd
from relay_client import RelayClient

root = Path(sys.argv[1])
env, module = 'owner', 'relay-test'
directory = root / 'q' / env
directory.mkdir(parents=True, exist_ok=True)
keys = relayd.Keys(root / 'fixture-key.pem')
schema = {'type': 'object', 'properties': {'query': {'type': 'string'}}, 'required': ['query'], 'additionalProperties': False}
handler = Mock()
handler.agent_actions = {'read': {'action': 'local_read', 'version': '1.0.0', 'args': schema}}
handler.call.return_value = {'ok': True, 'result': {'echo': 'private-fixture-data'}}
client = RelayClient(env, module, keys.public)
envelope = client.seal('read', {'query': 'private-fixture-data'}, app_version='1.0.0')
(directory / 'status.json').write_text(json.dumps({'seen': int(time.time()), 'pub': keys.public, 'modules': {module: {'relayAppProtocol': 1}}}))
print(json.dumps(envelope), flush=True)
deadline = time.time() + 20
while time.time() < deadline:
    path = directory / (envelope['id'] + '.req')
    if path.exists():
        req = json.loads(path.read_text())
        answer = relayd.answer(keys, {module: handler}, env, req, seen=relayd.Seen())
        handler.call.assert_called_once_with({'action': 'local_read', 'args': {'query': 'private-fixture-data'}})
        assert client.open(envelope['id'], answer) == {'echo': 'private-fixture-data'}
        assert 'private-fixture-data' not in path.read_text()
        (directory / (envelope['id'] + '.res')).write_text(json.dumps(answer))
        path.unlink()
        print('fixture PASS', flush=True)
        sys.exit(0)
    time.sleep(0.02)
raise TimeoutError('fixture queue never received request')
