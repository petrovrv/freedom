# SPDX-License-Identifier: AGPL-3.0-or-later
"""Browser test peer. In-memory crypto keys, disposable private SQLite, fake action only."""
import base64
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import time
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

home = Path(sys.argv[1]) / 'home-relay' / 'home'
sys.path.insert(0, str(home))
import relayd
from relay_client import RelayClient
from approvals import Proposals

keys = object.__new__(relayd.Keys)
keys.private = ec.generate_private_key(ec.SECP256R1())
keys.public = base64.b64encode(keys.private.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)).decode()
schema = {'type': 'object', 'properties': {'text': {'type': 'string'}}, 'required': ['text'], 'additionalProperties': False}
rid, rev = 'b' * 16, 'c' * 32
calls = 0

class Fake:
    outward_actions = {'send': {'action': 'fake_send', 'version': '1.0.0', 'args': schema}}
    agent_actions = {}
    page_actions = []
    def call(self, request):
        global calls
        calls += 1
        return {'ok': True, 'result': {'text': request['args']['text'], 'calls': calls}}

handler = Fake()
with tempfile.TemporaryDirectory() as folder:
    store = Proposals(Path(folder) / 'private.sqlite')
    def dispatch(phase, envelope):
        request = dict(envelope, module='sample-app')
        request['dispatch'] = {'kind': 'app-approval-v1', 'actor': 'agent' if phase == 'stage' else 'person',
            'env': 'enva', 'module': 'sample-app', 'action': 'send', 'version': '1.0.0', 'outward': True,
            'requestId': rid, 'revision': rev, 'phase': phase, 'expires': int(time.time()) + 1200,
            'grantExpires': int(time.time()) + 45, 'schema': schema,
            'envelopeHash': hashlib.sha256('\n'.join(request[k] for k in ('id', 'pk', 'iv', 'ct')).encode()).hexdigest()}
        return relayd.answer(keys, {'sample-app': handler}, 'enva', request, proposals=store)
    client = RelayClient('enva', 'sample-app', keys.public)
    stage = client.seal('send', {'text': 'PRIVATE <img src=x onerror=alert(1)>'}, app_version='1.0.0', approval={'phase': 'stage'})
    client.open(stage['id'], dispatch('stage', stage))
    for line in sys.stdin:
        data = json.loads(line)
        if data['phase'] == 'status':
            answer = {'ok': True, 'env': 'enva', 'app': 'sample-app', 'action': 'send', 'version': '1.0.0',
                      'requestId': rid, 'revision': rev, 'pub': keys.public}
        elif data['phase'] == 'count':
            answer = {'count': calls}
        else:
            encrypted = dispatch(data['phase'], data['envelope'])
            answer = {'ok': True, 'envelope': encrypted}
        print(json.dumps(answer), flush=True)
