"""Provider accounts of the people of a home server, managed from a PC over SSH (Linux host with systemd).

  connect SLUG --want mail.read,drive.appfiles [--provider google] [--account A] [--env E]
                     (--client-json F | --client-credman TARGET)
        the person allows the capabilities that are still missing in their own browser (incremental: earlier grants
        stay); the refresh token goes straight into the encrypted systemd credential of SLUG on the host
        (/etc/credstore.encrypted/account.SLUG.PROVIDER.cred, name `account`), never into a file or keyring of this PC
  migrate SLUG --from PATH --from-name NAME --env E [--provider google]
        re-encrypt an older module-specific credential on the host as the shared one (the old file is kept)
  disconnect SLUG [--provider google] [--revoke]
        one switch for all modules of the person: the credential is removed (each module then reports that the sign-in
        is gone); with --revoke the grant is also withdrawn at the provider (the refresh token is sent to its revoke
        endpoint from the host, it never comes to this PC)
  status [SLUG]   who is connected, which capabilities are granted (no secrets)

Modules load the credential in their units: LoadCredentialEncrypted=account:/etc/credstore.encrypted/account.%i.google.cred
and mint narrowed access tokens with accounts.TokenSource(capabilities=[...]).
"""
from __future__ import annotations

import argparse
import http.server
import importlib.util
import json
import os
import re
import shlex
import subprocess
import sys
import threading
import time
import urllib.parse
import webbrowser
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import accounts  # noqa: E402

REPO = HERE.parents[3]
SLUG = re.compile(r'^[a-z][a-z0-9-]{0,20}$')
EMAIL = re.compile(r'^[^@\s]+@[^@\s]+\.[^@\s]+$')
RECORDS = '/etc/freedom-accounts'
CREDSTORE = '/etc/credstore.encrypted'


def ssh(args, cmd, data=None, check=True):
    base = ['ssh', '-o', 'BatchMode=yes', '-i', os.path.expanduser(args.key), f'{args.user}@{args.host}', cmd]
    proc = subprocess.run(base, input=data, capture_output=True)
    if check and proc.returncode:
        sys.exit(f'host command failed ({proc.returncode}): {proc.stderr.decode(errors="replace").strip()[:500]}')
    return proc.stdout.decode(errors='replace')


def slug_ok(slug):
    if not SLUG.match(slug or ''):
        sys.exit('slug: lowercase letters, digits, dash; starts with a letter; at most 21 characters')
    return slug


def cred_path(slug, provider):
    return f'{CREDSTORE}/account.{slug}.{provider}.cred'


def read_record(args, slug):
    """The record of a person, read under the person's lock (a change a crash left half done is settled first)."""
    return host(args, {'op': 'read', 'slug': slug})


def host(args, job):
    """One change of a person's account on the host (HOST_PY) under the person's lock; returns the new record."""
    return json.loads(ssh(args, 'sudo python3 -I -c ' + shlex.quote(HOST_PY), json.dumps(job).encode()))


def client(args):
    """client_id/client_secret of an installed-app OAuth client (a JSON file or a Windows Credential Manager entry)."""
    if args.client_json:
        data = json.loads(Path(args.client_json).read_text(encoding='utf-8'))
        data = data.get('installed') or data
    elif args.client_credman:
        script = REPO / 'tools' / 'gsheets-bridge' / 'gsheets-bridge.py'
        if not script.is_file():
            sys.exit('Credential Manager reader not found')
        spec = importlib.util.spec_from_file_location('gsb', script)
        gsb = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(gsb)
        data = json.loads(gsb.read_credential(args.client_credman))
    else:
        sys.exit('connect needs --client-json or --client-credman (an installed-app OAuth client)')
    if not data.get('client_id') or not data.get('client_secret'):
        sys.exit('OAuth client without client_id/client_secret')
    return data['client_id'], data['client_secret']


def consent(url, state, wait, open_browser=True):
    """Loopback redirect: wait for the browser to come back with ?code=...&state=..."""
    got = {}

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            if query.get('state', [''])[0] == state:
                got.update({k: v[0] for k, v in query.items()})
            self.send_response(200)
            self.send_header('Content-Type', 'text/plain; charset=utf-8')
            self.end_headers()
            self.wfile.write('Готово, окно можно закрыть. Done, you can close this window.'.encode())

        def log_message(self, *a):
            pass
    server = consent.server
    Handler.timeout = 10  # a connection that never sends its request is dropped after 10 s
    server.RequestHandlerClass = Handler
    if open_browser:
        webbrowser.open(url)
    deadline = time.time() + wait

    def loop():
        while 'code' not in got and 'error' not in got and time.time() < deadline:
            server.timeout = max(0.1, deadline - time.time())
            server.handle_request()
    # a daemon thread with an absolute deadline: a client that trickles bytes cannot hold the command past `wait`
    worker = threading.Thread(target=loop, daemon=True)
    worker.start()
    worker.join(max(0, deadline - time.time()) + 1)
    server.server_close()
    return got


def connect(args, want=None, on_saved=None):
    """Incremental consent for the capabilities still missing; returns the record written on the host."""
    slug = slug_ok(args.slug)
    prov = accounts.provider(args.provider)
    caps = sorted(set(want if want is not None else [c.strip() for c in (args.want or '').split(',') if c.strip()]))
    if not caps:
        sys.exit('--want: at least one capability, e.g. mail.read')
    prov.scopes(caps)  # unknown capability -> error before anything happens
    record = read_record(args, slug)
    rev = record.get('rev', 0)  # 0.1.2: any change of the account on the host meanwhile -> nothing saved
    have = record.get('providers', {}).get(prov.name, {})
    given_account = (getattr(args, 'account', None) or '').strip().lower()
    given_env = getattr(args, 'env', None) or ''
    known_account = (have.get('account') or record.get('account') or '').strip().lower()
    if known_account and given_account and given_account != known_account:
        sys.exit(f'{slug} is already {known_account}; refusing to connect {given_account} under the same slug')
    if record.get('env') and given_env and given_env != record['env']:
        sys.exit(f'{slug} belongs to environment {record["env"]}, not {given_env}')
    account = known_account or given_account
    env = record.get('env') or given_env
    if not EMAIL.match(account):
        sys.exit('--account: the e-mail address of the person (first connect)')
    if not env:
        sys.exit('--env: the AI Workbench environment of the person (first connect)')
    lacking = accounts.missing(have, caps, prov.name) if have else caps
    if not lacking:
        print(json.dumps({'slug': slug, 'provider': prov.name, 'account': account, 'granted': prov.granted(have.get('scopes', [])),
                          'note': 'already granted'}))
        return record
    client_id, client_secret = client(args)
    server = http.server.HTTPServer(('127.0.0.1', 0), http.server.BaseHTTPRequestHandler)
    consent.server = server
    redirect = f'http://127.0.0.1:{server.server_port}/'
    # 0.1.2: ask for the whole set (granted + new) - an installed-app client does not reliably return the union of grants
    request = sorted(set(prov.granted(have.get('scopes', []))) | set(lacking))
    url, state, verifier = prov.auth_request(client_id, redirect, request, login_hint=account)
    print(f'Open and allow ({", ".join(request)}) for {account}:\n' + url, flush=True)
    got = consent(url, state, args.wait, open_browser=not args.no_browser)
    if 'code' not in got:
        sys.exit('no consent received' + (f' ({got.get("error")})' if got.get('error') else ''))
    tok = prov.exchange(client_id, client_secret, redirect, got['code'], verifier)
    if not tok.get('refresh_token'):
        sys.exit('the provider gave no refresh token; nothing saved')
    email = prov.identity(tok['access_token'])
    if email != account:
        sys.exit(f'signed in as another account than {account}; nothing saved')
    scopes = set(tok.get('scope', '').split())
    if have.get('scopes') and not set(have['scopes']) <= scopes:
        # include_granted_scopes should return the union; if not, keep the old credential rather than lose grants
        sys.exit('the new grant does not include the earlier ones; nothing saved (connect again and allow all)')
    path = cred_path(slug, prov.name)
    granted = prov.granted(scopes)
    entry = {'account': account, 'credential': path, 'scopes': sorted(scopes), 'capabilities': granted,
             'updated': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
    job = {'op': 'save', 'slug': slug, 'provider': prov.name, 'env': env, 'account': account, 'rev': rev, 'entry': entry,
           'cred': {'client_id': client_id, 'client_secret': client_secret, 'refresh_token': tok['refresh_token'],
                    'account': account, 'provider': prov.name, 'scopes': sorted(scopes)}}
    del tok
    record = host(args, job)
    del job
    out = {'slug': slug, 'provider': prov.name, 'account': account, 'credential': path, 'granted': granted,
           'refused': sorted(set(caps) - set(granted))}
    print(json.dumps(out))
    if on_saved:
        on_saved(out)
    return record


# 0.1.2: every read and change of a person's account (read, save after a consent, migrate, drop on disconnect) runs on the
# host as one process under the person's lock /run/lock/freedom-account.<slug>.lock. The record carries a revision `rev`
# raised by each change; a save whose consent started at another revision saves nothing. A change is journaled: first the
# record with the raised revision and `pending` = the new provider entry + sha256 of the credential file it expects
# (null = no file), then the credential is replaced or removed, then the record without `pending`. A crash anywhere is
# repaired by the next locked call (also a read): the credential on disk decides whether the pending entry is applied or
# dropped; the revision stays raised either way, so a consent open meanwhile saves nothing. Staging files are removed.
HOST_PY = r'''
import fcntl, hashlib, json, os, subprocess, sys, time
job = json.loads(sys.stdin.buffer.read())
slug, op = job["slug"], job["op"]
RECORDS, CREDS = "/etc/freedom-accounts", "/etc/credstore.encrypted"
rec_path = RECORDS + "/" + slug + ".json"
staged = []
now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def cred_path(prov):
    return CREDS + "/account." + slug + "." + prov + ".cred"


def digest(path):
    try:
        with open(path, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()
    except FileNotFoundError:
        return None


def put_record(record):
    staged.append(rec_path + ".new")
    fd = os.open(rec_path + ".new", os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        json.dump(record, f, ensure_ascii=False, indent=1)
        f.flush()
        os.fsync(f.fileno())
    os.replace(rec_path + ".new", rec_path)
    staged.remove(rec_path + ".new")


def settle(record):
    """Finish or drop a change a crash left half done; the credential file on disk decides."""
    pend = record.get("pending")
    if not pend:
        return record
    providers = dict(record.get("providers", {}))
    if digest(cred_path(pend["provider"])) == pend["sha256"] and pend.get("entry") is not None:
        providers[pend["provider"]] = pend["entry"]
    record = {k: v for k, v in record.items() if k != "pending"}
    record["providers"] = providers
    put_record(record)
    return record


os.makedirs(CREDS, mode=0o700, exist_ok=True)
os.makedirs(RECORDS, mode=0o700, exist_ok=True)
removed = False
with open("/run/lock/freedom-account." + slug + ".lock", "w") as lock:
    fcntl.flock(lock, fcntl.LOCK_EX)
    try:
        record = settle(json.load(open(rec_path)) if os.path.exists(rec_path) else {})
        if op == "read":
            print(json.dumps(record))
            sys.exit(0)
        prov = job["provider"]
        path = cred_path(prov)
        have = record.get("providers", {}).get(prov) or {}
        known = (have.get("account") or record.get("account") or "").strip().lower()
        if job.get("env") and record.get("env") and record["env"] != job["env"]:
            sys.exit(slug + " belongs to another environment; nothing changed")
        if op in ("save", "migrate") and known and known != job["account"]:
            sys.exit(slug + " is " + known + ", not " + job["account"] + "; nothing changed")
        cred = None
        if op == "save":
            if record.get("rev", 0) != job["rev"]:
                sys.exit("the account of " + slug + " changed during this consent; nothing saved (connect again)")
            if not set(have.get("scopes") or []) <= set(job["entry"]["scopes"]):
                sys.exit("the new grant does not include the earlier ones; nothing saved (connect again and allow all)")
            cred, entry = job["cred"], job["entry"]
        elif op == "migrate":
            if os.path.exists(path):
                sys.exit("the shared credential already exists; nothing replaced")
            raw = subprocess.run(["systemd-creds", "decrypt", "--name=" + job["from_name"], job["source"], "-"],
                                 check=True, capture_output=True).stdout
            cred = json.loads(raw)
            del raw
            cred["provider"] = prov
            for k in ("client_id", "client_secret", "refresh_token", "account"):
                if not cred.get(k):
                    sys.exit("old credential incomplete: " + k)
            if cred["account"].strip().lower() != job["account"]:
                sys.exit("the old credential belongs to another account than the record")
            scopes = sorted(cred.get("scopes") or [])
            entry = {"account": job["account"], "credential": path, "scopes": scopes, "updated": now, "migratedFrom": job["source"],
                     "capabilities": sorted(c for c, s in job["capmap"].items() if s in scopes)}
        elif op == "drop":
            entry = {**have, "scopes": [], "capabilities": [], "revoked": bool(job.get("revoked")), "disconnected": now} if have else None
        else:
            sys.exit("unknown op")
        sha = None
        if cred is not None:
            staged.append(path + ".new")
            subprocess.run(["systemd-creds", "encrypt", "--name=account", "-", path + ".new"], input=json.dumps(cred).encode(), check=True)
            del cred
            os.chmod(path + ".new", 0o600)
            sha = digest(path + ".new")
        record = {**record, "slug": slug, "rev": record.get("rev", 0) + 1}
        if op != "drop":
            record["env"] = record.get("env") or job["env"]
            record["account"] = record.get("account") or job["account"]
        put_record({**record, "pending": {"provider": prov, "entry": entry, "sha256": sha}})
        if op == "drop":
            removed = os.path.exists(path)
            if removed:
                os.unlink(path)
        else:
            os.replace(path + ".new", path)
            staged.remove(path + ".new")
        if entry is not None:
            record["providers"] = {**record.get("providers", {}), prov: entry}
        put_record(record)
    finally:
        for f in staged:
            try:
                os.unlink(f)
            except FileNotFoundError:
                pass
print(json.dumps({**record, "removed": removed}))
'''


def migrate(args):
    slug = slug_ok(args.slug)
    prov = accounts.provider(args.provider)
    if not re.fullmatch(r'/etc/credstore\.encrypted/[A-Za-z0-9._-]+\.cred', args.source):
        sys.exit('--from: a file in /etc/credstore.encrypted')
    if not re.fullmatch(r'[a-z][a-z0-9_-]{0,31}', args.from_name):
        sys.exit('--from-name: the credential name the old file was encrypted with')
    path = cred_path(slug, prov.name)
    record = read_record(args, slug)
    if record.get('env') and record['env'] != args.env:
        sys.exit(f'{slug} belongs to environment {record["env"]}, not {args.env}')
    expect = (record.get('providers', {}).get(prov.name, {}).get('account') or record.get('account') or '').strip().lower()
    given = (getattr(args, 'account', None) or '').strip().lower()
    if expect and given and given != expect:
        sys.exit(f'{slug} is already {expect}, not {given}')
    expect = expect or given
    if not EMAIL.match(expect):
        sys.exit('--account: the e-mail address the old credential must belong to')
    record = host(args, {'op': 'migrate', 'slug': slug, 'provider': prov.name, 'env': args.env, 'account': expect,
                         'source': args.source, 'from_name': args.from_name, 'capmap': prov.capabilities})
    print(json.dumps({'slug': slug, 'provider': prov.name, 'credential': path,
                      'capabilities': record['providers'][prov.name]['capabilities']}))


REVOKE_PY = r'''
import json, subprocess, sys, urllib.error, urllib.parse, urllib.request
src, url = sys.argv[1:3]
cred = json.loads(subprocess.run(["systemd-creds", "decrypt", "--name=account", src, "-"], check=True, capture_output=True).stdout)
req = urllib.request.Request(url, data=urllib.parse.urlencode({"token": cred["refresh_token"]}).encode(),
                             headers={"Content-Type": "application/x-www-form-urlencoded"})
try:
    with urllib.request.urlopen(req, timeout=30) as r:
        print(json.dumps({"revoked": r.status == 200}))
except urllib.error.HTTPError as e:
    # only 400 invalid_token means "already revoked or expired at the provider"; any other refusal keeps the credential
    try:
        err = json.loads(e.read() or b"{}").get("error")
    except ValueError:
        err = None
    print(json.dumps({"revoked": e.code == 400 and err == "invalid_token", "status": e.code, "error": err}))
'''
REVOKE_URL = {'google': 'https://oauth2.googleapis.com/revoke'}


def disconnect(args):
    slug = slug_ok(args.slug)
    prov = accounts.provider(args.provider)
    path = cred_path(slug, prov.name)
    present = ssh(args, f'sudo test -f {path} && echo yes || echo no').strip() == 'yes'
    revoked = None
    if args.revoke and present:
        if prov.name not in REVOKE_URL:
            sys.exit(f'{prov.name}: revoke is not implemented')
        revoked = json.loads(ssh(args, 'sudo python3 -I -c ' + shlex.quote(REVOKE_PY) + ' ' + shlex.quote(path) + ' '
                                 + shlex.quote(REVOKE_URL[prov.name])))
        if not revoked.get('revoked'):
            sys.exit(f'the provider did not confirm the revoke ({revoked}); the credential is kept, try again')
    # credential removed and record changed in one locked step (revision raised: a consent open meanwhile saves nothing)
    present = host(args, {'op': 'drop', 'slug': slug, 'provider': prov.name, 'revoked': bool(revoked)})['removed'] or present
    # services already running (or a oneshot run in progress, state activating) hold the credential they loaded: stop
    # every such unit that loads this file; later timer runs fail without the file until the person connects again
    stopped = ssh(args, 'for u in $(systemctl list-units --state=active,activating,reloading --plain --no-legend --type=service | cut -d" " -f1); do'
                        # systemctl show prints this setting as [unprintable]: read the unit text, %i = its instance
                        ' i=${u#*@}; i=${i%.service}; case "$u" in *@*) ;; *) i=; esac;'
                        f' systemctl cat "$u" 2>/dev/null | sed "s/%i/$i/g" | grep -q "^LoadCredentialEncrypted=.*:"{shlex.quote(path)}'
                        ' && { sudo systemctl stop "$u" >/dev/null 2>&1; echo "$u"; }; done; true', check=False).split()
    # fail closed: an ssh/systemctl error or an empty answer counts as not stopped
    still = [u for u in stopped if ssh(args, f'systemctl show -p ActiveState --value {shlex.quote(u)}').strip()
             not in ('inactive', 'failed')]
    if still:
        sys.exit('credential removed, but these services did not stop: ' + ', '.join(still))
    print(json.dumps({'slug': slug, 'provider': prov.name, 'removed': present, 'revoked': bool(revoked), 'stopped': stopped}))


def status(args):
    names = [slug_ok(args.slug)] if args.slug else [n.removesuffix('.json') for n in
                                                     ssh(args, f'sudo ls {RECORDS} 2>/dev/null || true').split() if n.endswith('.json')]
    out = []
    for slug in names:
        record = read_record(args, slug)
        for name, p in sorted(record.get('providers', {}).items()):
            present = ssh(args, f'sudo test -f {shlex.quote(p.get("credential", ""))} && echo yes || echo no', check=False).strip()
            out.append({'slug': slug, 'env': record.get('env'), 'provider': name, 'account': p.get('account'),
                        'capabilities': p.get('capabilities'), 'credential': present, 'updated': p.get('updated')})
    print(json.dumps(out, indent=1, ensure_ascii=False))


def add_host_args(p):
    p.add_argument('--host', default=os.environ.get('HOMEHUB_HOST', 'homehub'))
    p.add_argument('--user', default=os.environ.get('HOMEHUB_USER', os.environ.get('USERNAME', 'root')).lower())
    p.add_argument('--key', default=os.environ.get('HOMEHUB_KEY', '~/.ssh/id_ed25519'))


def add_connect_args(c):
    c.add_argument('--provider', default='google', choices=sorted(accounts.PROVIDERS))
    c.add_argument('--client-json')
    c.add_argument('--client-credman')
    c.add_argument('--wait', type=int, default=600)
    c.add_argument('--no-browser', action='store_true')


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_host_args(p)
    sub = p.add_subparsers(dest='cmd', required=True)
    c = sub.add_parser('connect')
    c.add_argument('slug')
    c.add_argument('--want', required=True, help='capabilities, comma separated: ' + ', '.join(sorted(accounts.Google.capabilities)))
    c.add_argument('--account')
    c.add_argument('--env')
    add_connect_args(c)
    m = sub.add_parser('migrate')
    m.add_argument('slug')
    m.add_argument('--from', dest='source', required=True)
    m.add_argument('--from-name', required=True)
    m.add_argument('--env', required=True)
    m.add_argument('--account', help='the e-mail address the old credential must belong to (needed when the person has no record yet)')
    m.add_argument('--provider', default='google', choices=sorted(accounts.PROVIDERS))
    d = sub.add_parser('disconnect')
    d.add_argument('slug')
    d.add_argument('--provider', default='google', choices=sorted(accounts.PROVIDERS))
    d.add_argument('--revoke', action='store_true', help='also withdraw the grant at the provider')
    s = sub.add_parser('status')
    s.add_argument('slug', nargs='?')
    args = p.parse_args()
    {'connect': connect, 'migrate': migrate, 'disconnect': disconnect, 'status': status}[args.cmd](args)


if __name__ == '__main__':
    main()
