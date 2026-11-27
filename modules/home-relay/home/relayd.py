"""Relay worker of one person on the home server (module home-relay): answers the pages of that person's modules
(mail, calendar, ...) through the site.

The site is only the postman (server/ops.php): the worker long-polls its route `worker` with its own token, takes the
sealed requests of its environment, hands each to the handler of the module it names and returns the sealed answers.

Envelope (ui/relay.js is the other side): ECDH P-256 between the page's session key (`pk`, raw uncompressed point) and
the worker's key (relay-key.pem in the worker's state directory, its public half published in the status); per request
HKDF-SHA256(shared, salt = "<env>|<module>|<id>", info = "freedom-home-relay/1 req|res") -> AES-256-GCM key, 12-byte
nonce, AAD = id. A request the worker cannot open gets the plain error `bad_request`; content never leaves in clear.

Binding: the home server decides. The worker serves one person (unit instance = slug) of one environment (AIWB_ENV of
/etc/home-relay/<slug>.env, written by relayctl pair); the module handlers are what the root-owned files
/etc/home-relay/modules.d/<module>.json name (written by each module's own deploy), started with the person's slug
filled in. A request picks only the module; slugs, paths and commands never come from the page or the site.

Handler protocol (one long-lived child process per module, JSON lines on stdin/stdout, logs on stderr):
  {"op": "status"}                                -> {"status": {<small flat values for the tray>}}
  {"op": "call", "action": "...", "args": {...}}  -> {"ok": true, "result": ...} | {"ok": false, "error": "<code>"}
The environment of the handler carries HOME_RELAY_ENV, HOME_RELAY_SLUG and HOME_RELAY_MODULE, so the module can check
its own binding (e.g. that its store belongs to that environment) before it answers.
"""
from __future__ import annotations

import hashlib
import math
import base64
import json
import os
import queue
import re
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

INFO = b'freedom-home-relay/1 '
ID_RE = re.compile(r'^[a-f0-9]{32}$')
MODULE_RE = re.compile(r'^[a-z][a-z0-9-]{1,39}$')
SLUG_RE = re.compile(r'^[a-z][a-z0-9-]{0,20}$')
REPLY_MAX = 8 * 1024 * 1024 - 4096   # ciphertext (base64) of one answer: the site's CO_RELAY_RES_MAX
BATCH_MAX = 12 * 1024 * 1024        # answers per worker call: under the site's 16 MB body limit
FRESH = 120                         # seconds a sealed request stays valid (clock of the page vs the worker)
CALL_TIMEOUT = 30                   # seconds a handler may take for one call (then it is restarted)
STATUS_TIMEOUT = 5
CONFIG_DIR = '/etc/home-relay'


def b64e(data):
    return base64.b64encode(data).decode()


def b64d(text, limit=None):
    if not isinstance(text, str) or (limit and len(text) > limit):
        raise ValueError('bad base64')
    return base64.b64decode(text, validate=True)


class Keys:
    """The worker's static P-256 key, created on first start (0600 in the worker's state directory)."""

    def __init__(self, path):
        path = Path(path)
        if path.is_file():
            self.private = serialization.load_pem_private_key(path.read_bytes(), None)
        else:
            self.private = ec.generate_private_key(ec.SECP256R1())
            pem = self.private.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                             serialization.NoEncryption())
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, 'wb') as fh:
                fh.write(pem)
        self.public = b64e(self.private.public_key().public_bytes(serialization.Encoding.X962,
                                                                  serialization.PublicFormat.UncompressedPoint))

    def _key(self, env, module, rid, page_pub, direction):
        peer = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), page_pub)
        shared = self.private.exchange(ec.ECDH(), peer)
        return HKDF(hashes.SHA256(), 32, f'{env}|{module}|{rid}'.encode(), INFO + direction).derive(shared)

    def open(self, env, module, req):
        rid = req.get('id', '')
        if not ID_RE.match(rid):
            raise ValueError('bad id')
        pub = b64d(req.get('pk'), 200)
        plain = AESGCM(self._key(env, module, rid, pub, b'req')).decrypt(b64d(req.get('iv'), 40), b64d(req.get('ct')), rid.encode())
        return pub, json.loads(plain)

    def seal(self, env, module, rid, page_pub, value):
        iv = os.urandom(12)
        data = json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode()
        return {'id': rid, 'iv': b64e(iv),
                'ct': b64e(AESGCM(self._key(env, module, rid, page_pub, b'res')).encrypt(iv, data, rid.encode()))}


class Seen:
    """Ids of requests already accepted (kept for twice the freshness window): a copied request is not answered again,
    also after a restart of the worker (with a path, the ids are on disk before the request is handled)."""

    def __init__(self, path=None):
        self.path, self.ids = (Path(path) if path else None), {}
        if self.path and self.path.is_file():
            try:
                data = json.loads(self.path.read_text(encoding='utf-8'))
                self.ids = {str(k): float(v) for k, v in data.items()} if isinstance(data, dict) else {}
            except (OSError, ValueError, TypeError, AttributeError):
                self.ids = {}

    def first(self, rid, now):
        for k in [k for k, t in self.ids.items() if t < now - 2 * FRESH]:
            del self.ids[k]
        if rid in self.ids:
            return False
        self.ids[rid] = now
        if self.path:
            tmp = self.path.with_suffix('.tmp')
            with open(tmp, 'w', encoding='utf-8') as fh:
                json.dump(self.ids, fh)
                fh.flush()
                os.fsync(fh.fileno())
            os.replace(tmp, self.path)
        return True


class Handler:
    """One module's handler: a child process started on first use, restarted after it dies or hangs."""

    def __init__(self, module, argv, env, agent_actions=None, outward_actions=None, page_actions=None):
        self.module, self.argv, self.env = module, argv, env
        self.agent_actions = agent_actions if isinstance(agent_actions, dict) else {}
        self.outward_actions = outward_actions if isinstance(outward_actions, dict) else {}
        self.page_actions = page_actions if isinstance(page_actions, list) else []
        self.proc, self.lines = None, None

    def _start(self):
        self.proc = subprocess.Popen(self.argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=self.env, bufsize=0)
        self.lines = queue.Queue()
        proc, lines = self.proc, self.lines

        def read():
            for line in iter(proc.stdout.readline, b''):
                lines.put(line)
            lines.put(None)
        threading.Thread(target=read, daemon=True).start()

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.kill()
            try:
                self.proc.wait(5)
            except subprocess.TimeoutExpired:
                pass
        self.proc = None

    def ask(self, obj, timeout):
        if not self.proc or self.proc.poll() is not None:
            self._start()
        deadline = time.monotonic() + timeout
        data, proc, failed = json.dumps(obj, separators=(',', ':')).encode() + b'\n', self.proc, []

        def write():  # a handler that stops reading its stdin must not block the worker: one deadline for write + read
            try:
                proc.stdin.write(data)
                proc.stdin.flush()
            except OSError:
                failed.append(1)
        writer = threading.Thread(target=write, daemon=True)
        writer.start()
        writer.join(timeout)
        try:
            if writer.is_alive() or failed:
                raise OSError('write')
            line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
        except (OSError, queue.Empty):
            self.stop()
            raise RuntimeError('handler_down')
        if line is None:
            self.stop()
            raise RuntimeError('handler_down')
        value = json.loads(line)
        if not isinstance(value, dict):
            raise RuntimeError('handler_error')
        return value

    def status(self):
        try:
            st = self.ask({'op': 'status'}, STATUS_TIMEOUT).get('status')
            return st if isinstance(st, dict) else {}
        except Exception:
            return {'down': True}

    def call(self, request):
        action, args = request.get('action'), request.get('args') or {}
        if not isinstance(action, str) or not isinstance(args, dict):
            return {'ok': False, 'error': 'bad_request'}
        try:
            out = self.ask({'op': 'call', 'action': action, 'args': args}, CALL_TIMEOUT)
        except Exception as exc:
            return {'ok': False, 'error': str(exc) if str(exc) in ('handler_down', 'handler_error') else 'worker_error'}
        if out.get('ok') is True:
            return {'ok': True, 'result': out.get('result')}
        return {'ok': False, 'error': re.sub(r'[^A-Za-z0-9_ .:-]', '', str(out.get('error') or 'worker_error'))[:100]}


def _env_file(path):
    out = {}
    for line in Path(path).read_text(encoding='utf-8').splitlines():
        m = re.match(r'^([A-Z][A-Z0-9_]{0,63})=(.*)$', line.strip())
        if m:
            out[m[1]] = m[2]
    return out


def _own_credentials(module, names, source):
    """A folder with copies of only the credentials this module declared (the unit loads them per person: relayctl
    drop-in credentials.conf), never the worker's own `relay` token. All handlers of a person run as that person's
    user, so this keeps modules from reading each other's credentials by mistake; the user is the hard boundary."""
    target = Path(tempfile.mkdtemp(prefix=f'cred-{module}-'))
    os.chmod(target, 0o700)
    for name in names:
        src = Path(source) / str(name)
        if re.fullmatch(r'[a-z][a-z0-9_-]{0,30}', str(name)) and name != 'relay' and src.is_file():
            dst = target / str(name)
            dst.write_bytes(src.read_bytes())
            os.chmod(dst, 0o400)
    return str(target)


def load_handlers(config_dir, slug, env, log=None):
    """{module: Handler} from <config_dir>/modules.d/<module>.json: {"exec": [...], "envFile": "...", "if": "<path>",
    "credentials": {name: path}} with {slug} filled in; a module whose "if" path does not exist for this person is not
    served; a module with "credentials" gets the unit's CREDENTIALS_DIRECTORY (e.g. the person's account credential)."""
    if not SLUG_RE.match(slug):
        raise ValueError('bad slug')
    out = {}
    for path in sorted((Path(config_dir) / 'modules.d').glob('*.json')):
        module = path.stem
        try:
            spec = json.loads(path.read_text(encoding='utf-8'))
            argv = [str(a).replace('{slug}', slug) for a in spec['exec']]
            if not MODULE_RE.match(module) or not argv or not os.path.isabs(argv[0]):
                raise ValueError('module name or exec')
            if spec.get('if') and not Path(str(spec['if']).replace('{slug}', slug)).exists():
                continue
            child = {'PATH': '/usr/bin:/bin', 'LANG': 'C.UTF-8', 'PYTHONDONTWRITEBYTECODE': '1',
                     'HOME_RELAY_ENV': env, 'HOME_RELAY_SLUG': slug, 'HOME_RELAY_MODULE': module}
            if os.name == 'nt':  # the tests on a Windows PC: Python needs these to start
                child.update({k: os.environ[k] for k in ('SYSTEMROOT', 'PATH') if k in os.environ})
            if spec.get('envFile'):
                child = {**_env_file(str(spec['envFile']).replace('{slug}', slug)), **child}
            if spec.get('credentials') and os.environ.get('CREDENTIALS_DIRECTORY'):
                child['CREDENTIALS_DIRECTORY'] = _own_credentials(module, spec['credentials'], os.environ['CREDENTIALS_DIRECTORY'])
            out[module] = Handler(module, argv, child, spec.get('agentActions'), spec.get('outwardActions'), spec.get('pageActions'))
        except Exception as exc:  # one broken registration does not stop the others
            if log:
                log(f'handler {path.name} skipped: {type(exc).__name__}: {exc}')
    return out



def app_args_valid(schema, args):
    """The closed flat schema used by core apps; strict JSON types, no Python bool/int aliasing."""
    if not isinstance(schema, dict) or not isinstance(args, dict) or schema.get('type') != 'object' or schema.get('additionalProperties') is not False:
        return False
    props, required = schema.get('properties'), schema.get('required', [])
    if not isinstance(props, dict) or not isinstance(required, list):
        return False
    if any(not isinstance(k, str) or not isinstance(p, dict) or p.get('type') not in ('string', 'integer', 'number', 'boolean') for k, p in props.items()):
        return False
    if any(not isinstance(k, str) or k not in props or k not in args for k in required):
        return False
    for key, value in args.items():
        prop = props.get(key)
        if not isinstance(prop, dict):
            return False
        kind = prop.get('type')
        if kind == 'string':
            limit = prop.get('maxLength', 2000)
            good = type(limit) is int and 0 < limit <= 65536 and isinstance(value, str) and len(value) <= limit
        elif kind == 'integer':
            good = type(value) is int and -(2**63) <= value < 2**63
        elif kind == 'number':
            good = type(value) in (int, float) and math.isfinite(value)
        elif kind == 'boolean':
            good = type(value) is bool
        else:
            good = False
        if not good:
            return False
        if 'enum' in prop and (not isinstance(prop['enum'], list) or not any(
                type(value) is type(v) and value == v for v in prop['enum'])):
            return False
    return True


def outward_targets(handler):
    actions = getattr(handler, 'outward_actions', {})
    if not isinstance(actions, dict):
        return set()
    return {x['action'] for x in actions.values() if isinstance(x, dict) and isinstance(x.get('action'), str)}


def app_dispatch(handler, env, module, req, request):
    """Return only an allowlisted handler call. Queue authority comes from the site, content from AEAD.

    Local root-owned registration opts in each action and pins its version and schema. Default deny;
    no arbitrary handler method or authority field can be selected by the encrypted caller.
    """
    d = req.get('dispatch')
    marker = request.get('_app')
    if not isinstance(d, dict) or not isinstance(marker, dict) or set(marker) != {'version'}:
        raise ValueError('app_dispatch_required')
    if (d.get('kind') != 'app-action-v1' or d.get('actor') != 'agent' or d.get('outward') is not False
            or d.get('env') != env or d.get('module') != module
            or not isinstance(d.get('jobId'), str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,79}', d['jobId'])):
        raise ValueError('bad_app_dispatch')
    digest = hashlib.sha256('\n'.join(str(req.get(k, '')) for k in ('id', 'pk', 'iv', 'ct')).encode('ascii')).hexdigest()
    if d.get('envelopeHash') != digest:
        raise ValueError('app_envelope_mismatch')
    action = request.get('action')
    if not isinstance(action, str) or action != d.get('action'):
        raise ValueError('app_action_mismatch')
    local = getattr(handler, 'agent_actions', {}).get(action)
    if not isinstance(local, dict):
        raise ValueError('app_action_not_enabled')
    version = d.get('version')
    if (not isinstance(version, str) or not version or version != marker.get('version')
            or version != local.get('version')):
        raise ValueError('app_version_mismatch')
    target = local.get('action')
    if isinstance(target, str) and target in outward_targets(handler):
        raise ValueError('outward_alias_forbidden')
    if not isinstance(target, str) or not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{0,79}', target):
        raise ValueError('app_action_not_enabled')
    args = request.get('args')
    if not app_args_valid(d.get('schema'), args) or not app_args_valid(local.get('args'), args):
        raise ValueError('app_args_invalid')
    # Only these two fields reach the handler. In particular no actor/grant/env/root can be smuggled.
    return {'action': target, 'args': args}


def answer(keys, handlers, env, req, seen=None, now=None, proposals=None):
    """One sealed request -> one sealed answer (or a plain error code without content)."""
    rid = str(req.get('id') or '')
    module = str(req.get('module') or '')
    now = time.time() if now is None else now
    if module not in handlers:
        return {'id': rid, 'error': 'not_served'}
    try:
        page_pub, request = keys.open(env, module, req)
    except Exception:
        return {'id': rid, 'error': 'bad_request'}
    # the time is inside the sealed part: the site can neither refresh an old request nor replay a fresh one twice
    ts = request.get('ts') if isinstance(request, dict) else None
    if type(ts) not in (int, float) or not -1e20 < ts < 1e20 or abs(now - ts / 1000) > FRESH:
        return {'id': rid, 'error': 'expired'}
    if seen is not None and not seen.first(rid, now):
        return {'id': rid, 'error': 'replay'}
    dispatch = req.get('dispatch')
    if '_approval' in request or (isinstance(dispatch, dict) and dispatch.get('kind') == 'app-approval-v1'):
        try:
            if proposals is None:
                raise ValueError('approvals_disabled')
            result = proposals.handle(handlers[module], env, module, req, request, now, app_args_valid)
        except Exception:
            result = {'ok': False, 'error': 'approval_refused'}
        sealed = keys.seal(env, module, rid, page_pub, result)
        return sealed if len(sealed['ct']) <= REPLY_MAX else {'id': rid, 'error': 'too_large'}
    if 'dispatch' in req or '_app' in request:
        try:
            request = app_dispatch(handlers[module], env, module, req, request)
        except (ValueError, TypeError, OverflowError):
            return keys.seal(env, module, rid, page_pub, {'ok': False, 'error': 'app_dispatch_refused'})
    else:
        handler = handlers[module]
        outward = outward_targets(handler)
        action = request.get('action')
        if not isinstance(action, str) or action not in getattr(handler, 'page_actions', []) or action in outward:
            return keys.seal(env, module, rid, page_pub, {'ok': False, 'error': 'page_action_not_enabled'})
    result = handlers[module].call(request)
    sealed = keys.seal(env, module, rid, page_pub, result)
    return sealed if len(sealed['ct']) <= REPLY_MAX else {'id': rid, 'error': 'too_large'}


def batches(replies):
    """Split answers so that one worker call stays under the site's body limit."""
    out, cur, size = [], [], 0
    for r in replies:
        n = len(r.get('ct', '')) + 200
        if cur and size + n > BATCH_MAX:
            out.append(cur)
            cur, size = [], 0
        cur.append(r)
        size += n
    if cur:
        out.append(cur)
    return out


def serve(keys, handlers, env, url, token, request=None, stop=None, log=None, seen=None, proposals=None):
    """Long-poll loop; returns when `stop()` is true (tests) or never."""
    post = request or _post
    seen, waiting, delay = seen or Seen(), [], 1
    while not (stop and stop()):
        status = {'pub': keys.public, 'modules': {m: {'relayAppProtocol': 1, 'relayApprovalProtocol': 1 if proposals else 0, **{k: v for k, v in h.status().items() if k not in ('relayAppProtocol', 'relayApprovalProtocol')}} for m, h in handlers.items()}}
        parts = batches(waiting) or [[]]
        batch, rest = parts[0], [r for p in parts[1:] for r in p]
        payload = {'env': env, 'token': token, 'status': status, 'replies': batch}
        if rest:
            payload['nowait'] = 1  # more answers to hand in: do not wait for new requests yet
        try:
            code, body = post(url, payload)
        except Exception as exc:  # network: keep the answers, try again later
            code, body = 0, {'error': type(exc).__name__}
        if code in (400, 413) and batch:
            if any('ct' in r for r in batch):
                # the site refuses this batch for good (size): tell the pages it failed instead of retrying forever
                waiting = [{'id': r['id'], 'error': 'too_large'} for r in batch] + rest
            else:
                # even the plain error answers are refused: drop them (the pages time out) and back off
                waiting = rest
                time.sleep(delay)
                delay = min(delay * 2, 60)
            continue
        if code != 200:
            if log:
                log(f'worker call failed: {code} {body.get("error") if isinstance(body, dict) else ""}')
            if code == 403:
                raise PermissionError('the site refused the worker token: pair again (relayctl pair)')
            time.sleep(delay)
            delay = min(delay * 2, 60)
            continue
        delay, waiting = 1, rest
        for req in (body.get('requests') or [])[:20]:
            if isinstance(req, dict):
                waiting.append(answer(keys, handlers, env, req, seen, proposals=proposals))


def _post(url, payload):
    req = urllib.request.Request(url, data=json.dumps(payload).encode(), method='POST',
                                 headers={'Content-Type': 'application/json', 'User-Agent': 'freedom-home-relay/0.1'})
    try:
        with urllib.request.urlopen(req, timeout=40) as resp:
            return resp.status, json.loads(resp.read() or b'{}')
    except urllib.error.HTTPError as err:
        try:
            return err.code, json.loads(err.read() or b'{}')
        except ValueError:
            return err.code, {}


def main():
    """Entry of the unit home-relay@<slug> (state directory, credential `relay`, AIWB_ENV and RELAY_URL from systemd)."""
    slug = sys.argv[1] if len(sys.argv) > 1 else ''
    cred = os.environ.get('CREDENTIALS_DIRECTORY')
    token = (Path(cred) / 'relay').read_text(encoding='utf-8').strip() if cred and (Path(cred) / 'relay').is_file() else ''
    url, env, state = os.environ.get('RELAY_URL', ''), os.environ.get('AIWB_ENV', ''), os.environ.get('STATE_DIRECTORY', '')
    if not (SLUG_RE.match(slug) and token and url.startswith('https://') and env and state):
        raise SystemExit('relayd needs SLUG, the relay credential, RELAY_URL (https), AIWB_ENV and STATE_DIRECTORY')
    log = lambda m: print(m, file=sys.stderr, flush=True)  # noqa: E731
    handlers = load_handlers(os.environ.get('HOME_RELAY_CONFIG', CONFIG_DIR), slug, env, log)
    log(f'serving {sorted(handlers)} for {slug}')
    try:
        home = Path(state.split(':')[0])
        import importlib.util
        spec = importlib.util.spec_from_file_location('relay_approvals', Path(__file__).with_name('approvals.py'))
        approvals = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(approvals)
        serve(Keys(home / 'relay-key.pem'), handlers, env, url, token, log=log, seen=Seen(home / 'seen.json'), proposals=approvals.Proposals(home / 'proposals.sqlite'))
    finally:
        for h in handlers.values():
            h.stop()


if __name__ == '__main__':
    main()
