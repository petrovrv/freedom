# SPDX-License-Identifier: AGPL-3.0-or-later
"""Private immutable proposals and durable, at-most-once dispatch claims.

The database belongs to the environment's worker, never the public site. A claim
is committed with FULL synchronous durability before calling an external handler.
An interrupted claim remains executing; neither restart nor polling replays it.
"""
import hashlib
import json
import re
import sqlite3
import os
import secrets
from contextlib import contextmanager


class Proposals:
    def __init__(self, path):
        self.path = str(path)
        with self.connect() as db:
            db.execute('CREATE TABLE IF NOT EXISTS proposals (id TEXT PRIMARY KEY, binding TEXT NOT NULL, '
                       'payload TEXT NOT NULL, expires INTEGER NOT NULL, state TEXT NOT NULL, result TEXT, preview TEXT)')

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.path, timeout=15)
        try:
            os.chmod(self.path, 0o600)
            db.execute('PRAGMA synchronous=FULL')
            with db:
                yield db
        finally:
            db.close()

    def handle(self, handler, env, module, req, request, now, valid_args):
        d, marker = req.get('dispatch'), request.get('_approval')
        if not isinstance(d, dict) or not isinstance(marker, dict):
            raise ValueError('approval_required')
        phase = d.get('phase')
        if (d.get('kind') != 'app-approval-v1' or d.get('env') != env or d.get('module') != module
                or d.get('outward') is not True or phase not in ('stage', 'preview', 'run', 'result')
                or marker.get('phase') != phase or request.get('_app') != {'version': d.get('version')}
                or request.get('action') != d.get('action')):
            raise ValueError('approval_binding')
        if d.get('actor') not in (('agent', 'person') if phase == 'result' else (('agent',) if phase == 'stage' else ('person',))):
            raise ValueError('approval_actor')
        digest = hashlib.sha256('\n'.join(str(req.get(k, '')) for k in ('id', 'pk', 'iv', 'ct')).encode('ascii')).hexdigest()
        if d.get('envelopeHash') != digest:
            raise ValueError('approval_envelope')
        rid, revision = d.get('requestId'), d.get('revision')
        if not isinstance(rid, str) or not re.fullmatch('[0-9a-f]{16}', rid) or not isinstance(revision, str) or not re.fullmatch('[0-9a-f]{32}', revision):
            raise ValueError('approval_id')
        if phase != 'stage' and (marker.get('requestId') != rid or marker.get('revision') != revision):
            raise ValueError('approval_revision')
        local = getattr(handler, 'outward_actions', {}).get(d.get('action'))
        if (not isinstance(local, dict) or local.get('version') != d.get('version')
                or not isinstance(local.get('action'), str) or not re.fullmatch('[A-Za-z][A-Za-z0-9_]{0,79}', local['action'])):
            raise ValueError('approval_not_enabled')
        binding = json.dumps({k: d[k] for k in ('env', 'module', 'action', 'version', 'requestId', 'revision')}, sort_keys=True)
        info = {'requestId': rid, 'revision': revision}
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            if phase == 'stage':
                # Keep only binding/state tombstones after 30 days; never permit ID reuse.
                db.execute("UPDATE proposals SET payload='{}',result=NULL WHERE expires < ?", (int(now) - 30 * 86400,))
                args = request.get('args')
                if not valid_args(local.get('args'), args) or not valid_args(d.get('schema'), args):
                    raise ValueError('approval_args')
                expiry = d.get('expires')
                if type(expiry) is not int or expiry <= now or expiry > now + 1800:
                    raise ValueError('approval_expiry')
                payload = json.dumps({'action': local['action'], 'args': args, 'schema': local['args']}, sort_keys=True)
                db.execute('INSERT INTO proposals VALUES (?,?,?,?,?,NULL,NULL)', (rid, binding, payload, expiry, 'pending'))
                return {'ok': True, 'result': {**info, 'expires': expiry}}
            row = db.execute('SELECT binding,payload,expires,state,result,preview FROM proposals WHERE id=?', (rid,)).fetchone()
            if not row or row[0] != binding:
                raise ValueError('approval_not_found')
            payload = json.loads(row[1])
            if not payload:
                raise ValueError('approval_retired')
            if payload['action'] != local['action'] or payload['schema'] != local.get('args'):
                raise ValueError('approval_changed')
            if phase == 'result':
                state = 'expired' if row[3] == 'pending' and row[2] <= now else row[3]
                return {'ok': True, 'result': {**info, 'state': state, 'result': json.loads(row[4]) if row[4] else None}}
            if row[2] <= now or row[3] != 'pending':
                raise ValueError('approval_unavailable')
            if phase == 'preview':
                token = secrets.token_hex(32)
                proof = hashlib.sha256((token + '|' + req['pk']).encode()).hexdigest()
                proofs = json.loads(row[5]) if row[5] else []
                if len(proofs) >= 32:
                    raise ValueError('approval_preview_limit')
                db.execute('UPDATE proposals SET preview=? WHERE id=?', (json.dumps([*proofs, proof]), rid))
                return {'ok': True, 'result': {**info, 'previewToken': token, 'action': d['action'], 'version': d['version'], 'args': payload['args']}}
            token = marker.get('previewToken')
            if not isinstance(token, str) or not row[5] or not any(secrets.compare_digest(proof, hashlib.sha256((token + '|' + req['pk']).encode()).hexdigest()) for proof in json.loads(row[5])):
                raise ValueError('approval_preview_required')
            # Short-lived grant is minted only by authenticated user Run. The durable proposal
            # state is the grant's one-use ledger, shared by all worker processes.
            if type(d.get('grantExpires')) is not int or not now < d['grantExpires'] <= now + 60:
                raise ValueError('approval_grant_expired')
            db.execute("UPDATE proposals SET state='executing' WHERE id=?", (rid,))
        result = handler.call({'action': payload['action'], 'args': payload['args']})
        # A handler timeout is ambiguous: keep executing forever, requiring manual reconciliation.
        if result.get('error') not in ('handler_down', 'handler_error', 'worker_error'):
            with self.connect() as db:
                db.execute('UPDATE proposals SET state=?,result=? WHERE id=?',
                           ('done' if result.get('ok') is True else 'failed', json.dumps(result), rid))
        with self.connect() as db:
            row = db.execute('SELECT state,result FROM proposals WHERE id=?', (rid,)).fetchone()
        return {'ok': True, 'result': {**info, 'state': row[0], 'result': json.loads(row[1]) if row[1] else None}}
