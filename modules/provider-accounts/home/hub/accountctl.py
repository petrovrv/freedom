"""Provider accounts of the people of a home server, managed from a PC over SSH (Linux host with systemd).

  connect SLUG --want mail.read,drive.appfiles [--provider google] [--account A] [--env E]
                     (--client-json F | --client-credman TARGET)
        the person allows the capabilities that are still missing in their own browser (incremental: earlier grants
        stay); the refresh token goes straight into the encrypted systemd credential of SLUG on the host
        (/etc/credstore.encrypted/account.SLUG.PROVIDER.cred, name `account`), never into a file or keyring of this PC
  migrate SLUG --from PATH --from-name NAME --env E [--provider google]
        re-encrypt an older module-specific credential on the host as the shared one (the old file is kept)
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
    raw = ssh(args, f'sudo cat {RECORDS}/{slug}.json 2>/dev/null || true').strip()
    return json.loads(raw) if raw else {}


def write_record(args, slug, record):
    body = json.dumps(record, ensure_ascii=False, indent=1).encode()
    ssh(args, f'sudo install -d -m 0700 {RECORDS} && sudo tee {RECORDS}/{slug}.json >/dev/null && sudo chmod 0600 {RECORDS}/{slug}.json', body)


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
    url, state, verifier = prov.auth_request(client_id, redirect, lacking, login_hint=account)
    print(f'Open and allow ({", ".join(lacking)}) for {account}:\n' + url, flush=True)
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
    cred = json.dumps({'client_id': client_id, 'client_secret': client_secret, 'refresh_token': tok['refresh_token'],
                       'account': account, 'provider': prov.name, 'scopes': sorted(scopes)}).encode()
    path = cred_path(slug, prov.name)
    ssh(args, f'sudo install -d -m 0700 {CREDSTORE} && sudo systemd-creds encrypt --name={accounts.CREDENTIAL_NAME} - {path}.new'
              f' && sudo chmod 0600 {path}.new && sudo mv {path}.new {path}', cred)
    del cred, tok
    granted = prov.granted(scopes)
    record = {**record, 'slug': slug, 'env': env, 'account': record.get('account') or account,
              'providers': {**record.get('providers', {}), prov.name: {
                  'account': account, 'credential': path, 'scopes': sorted(scopes), 'capabilities': granted,
                  'updated': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}}}
    write_record(args, slug, record)
    out = {'slug': slug, 'provider': prov.name, 'account': account, 'credential': path, 'granted': granted,
           'refused': sorted(set(caps) - set(granted))}
    print(json.dumps(out))
    if on_saved:
        on_saved(out)
    return record


MIGRATE_PY = r'''
import json, os, subprocess, sys, time
src, name, dst, provider, expect = sys.argv[1:6]
if os.path.exists(dst):
    sys.exit("the shared credential already exists; nothing replaced")
raw = subprocess.run(["systemd-creds", "decrypt", "--name=" + name, src, "-"], check=True, capture_output=True).stdout
cred = json.loads(raw)
cred["provider"] = provider
for k in ("client_id", "client_secret", "refresh_token", "account"):
    if not cred.get(k):
        sys.exit("old credential incomplete: " + k)
if expect and cred["account"].strip().lower() != expect:
    sys.exit("the old credential belongs to another account than the record")
subprocess.run(["systemd-creds", "encrypt", "--name=account", "-", dst + ".new"], input=json.dumps(cred).encode(), check=True)
subprocess.run(["chmod", "0600", dst + ".new"], check=True)
subprocess.run(["mv", dst + ".new", dst], check=True)
print(json.dumps({"account": cred["account"].strip().lower(), "scopes": sorted(cred.get("scopes") or [])}))
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
    out = json.loads(ssh(args, 'sudo install -d -m 0700 ' + CREDSTORE + ' && sudo python3 -I -c ' + shlex.quote(MIGRATE_PY) + ' '
                         + ' '.join(shlex.quote(a) for a in (args.source, args.from_name, path, prov.name, expect))))
    record = {**record, 'slug': slug, 'env': args.env, 'account': record.get('account') or out['account'],
              'providers': {**record.get('providers', {}), prov.name: {
                  'account': out['account'], 'credential': path, 'scopes': out['scopes'],
                  'capabilities': prov.granted(out['scopes']), 'updated': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                  'migratedFrom': args.source}}}
    write_record(args, slug, record)
    print(json.dumps({'slug': slug, 'provider': prov.name, 'credential': path, 'capabilities': prov.granted(out['scopes'])}))


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
    s = sub.add_parser('status')
    s.add_argument('slug', nargs='?')
    args = p.parse_args()
    {'connect': connect, 'migrate': migrate, 'status': status}[args.cmd](args)


if __name__ == '__main__':
    main()
