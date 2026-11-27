# SPDX-License-Identifier: AGPL-3.0-or-later
"""In-memory client for home-relay/1. No transport, credentials or file access.

Create one client per environment/module/worker public key. Pass seal()'s envelope
through the module's authorised transport, then open(request_id, answer). Recreate
on worker key rotation; existing requests remain bound to their original key.
This encryption layer grants no authority to execute an action.
"""
from __future__ import annotations

import base64
import json
import os
import re
import secrets
import threading
import time

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF


class RelayError(ValueError):
    """A local validation failure, unauthenticated transport error, or worker error."""


def _decode(value, limit):
    if not isinstance(value, str) or len(value) > limit:
        raise ValueError('bad base64')
    return base64.b64decode(value, validate=True)


class RelayClient:
    """Thread-safe envelope client; up to six pending requests, valid for 120 s.

    Worker public keys must come from the caller's trusted status channel. Plain
    relay errors are NOT authenticated: they raise transport_error, never success.
    """

    def __init__(self, env, module, worker_pub):
        if (not isinstance(env, str) or not env or '|' in env or len(env) > 200
                or not isinstance(module, str) or not re.fullmatch(r'[a-z][a-z0-9-]{1,39}', module)):
            raise RelayError('bad_relay_options')
        self._env, self._module = env, module
        try:
            peer = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), _decode(worker_pub, 200))
        except (ValueError, TypeError) as exc:
            raise RelayError('bad_worker_key') from exc
        private = ec.generate_private_key(ec.SECP256R1())
        self._public = base64.b64encode(private.public_key().public_bytes(
            serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)).decode('ascii')
        self._shared = private.exchange(ec.ECDH(), peer)
        self._pending = {}
        self._lock = threading.Lock()

    def _key(self, rid, direction):
        return HKDF(hashes.SHA256(), 32, f'{self._env}|{self._module}|{rid}'.encode(),
                    b'freedom-home-relay/1 ' + direction).derive(self._shared)

    def _prune(self):
        now = time.monotonic()
        for rid in [rid for rid, expiry in self._pending.items() if expiry <= now]:
            del self._pending[rid]

    def seal(self, action, args=None):
        """Return {id,module,pk,iv,ct}; timestamp and random id are client-owned."""
        if not isinstance(action, str) or not action or (args is not None and not isinstance(args, dict)):
            raise RelayError('bad_request')
        with self._lock:
            self._prune()
            if len(self._pending) >= 6:
                raise RelayError('busy')
            rid = secrets.token_hex(16)
            while rid in self._pending:
                rid = secrets.token_hex(16)
            data = json.dumps({'action': action, 'args': args if args is not None else {},
                               'ts': time.time() * 1000}, ensure_ascii=False, allow_nan=False,
                              separators=(',', ':')).encode()
            iv = os.urandom(12)
            ct = AESGCM(self._key(rid, b'req')).encrypt(iv, data, rid.encode())
            self._pending[rid] = time.monotonic() + 120
            return {'id': rid, 'module': self._module, 'pk': self._public,
                    'iv': base64.b64encode(iv).decode('ascii'), 'ct': base64.b64encode(ct).decode('ascii')}

    def discard(self, rid):
        """Forget an abandoned request (e.g. transport timeout); no automatic retry."""
        with self._lock:
            self._pending.pop(rid, None)

    def open(self, rid, answer):
        """Authenticate a pending answer once and return its result.

        Invalid answers do not consume the request. Authenticated worker failures
        do consume it and raise worker_error; their untrusted text is not exposed.
        """
        with self._lock:
            self._prune()
            if not isinstance(rid, str) or rid not in self._pending:
                raise RelayError('unknown_request')
            if not isinstance(answer, dict) or answer.get('id') != rid:
                raise RelayError('bad_answer')
            if 'error' in answer:
                raise RelayError('transport_error')
            try:
                iv = _decode(answer.get('iv'), 40)
                if len(iv) != 12:
                    raise ValueError('bad nonce')
                data = AESGCM(self._key(rid, b'res')).decrypt(
                    iv, _decode(answer.get('ct'), 8 * 1024 * 1024), rid.encode())
                value = json.loads(data)
                if not isinstance(value, dict) or type(value.get('ok')) is not bool:
                    raise ValueError('bad result')
            except Exception as exc:
                raise RelayError('bad_answer') from exc
            del self._pending[rid]
            if not value['ok']:
                raise RelayError('worker_error')
            return value.get('result')
