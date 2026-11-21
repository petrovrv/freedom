"""Mail store on the home server, managed from a PC over SSH (Linux host with systemd).

  deploy                      copy the module code and read-guard to /opt/concierge-mail, install the unit templates
  add SLUG --account A --env E [--backup-public KEY]   system user cmail-SLUG, /srv/data/mail/SLUG (0700), /etc/concierge-mail/SLUG.env
  connect SLUG --client-json F | --client-credman TARGET   the person grants gmail.readonly + drive.file in their browser;
                              the token goes straight into an encrypted systemd credential of SLUG on the host
                              (never into a file or keyring of this PC)
  start SLUG / stop SLUG      enable/disable the sync timer (and the backup timer when a backup key is set)
  status [SLUG]               timers, last run, per-mailbox counters (no message content)
  run SLUG -- ARGS            mailcli.py as cmail-SLUG with its credential (diagnostics: gmail-status, search ...)

Each person: own Unix user, own directory, own credential; one person's service cannot read another's mail.
"""
from __future__ import annotations
import argparse
import base64
import hashlib
import http.server
import json
import os
import re
import secrets
import shlex
import subprocess
import sys
import threading
import urllib.parse
import urllib.request
import webbrowser
from pathlib import Path

HERE = Path(__file__).resolve().parent
MODULE = HERE.parents[1]
REPO = HERE.parents[3]
SCOPES = 'openid email https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/drive.file'
SLUG = re.compile(r'^[a-z][a-z0-9-]{0,20}$')
UNITS = ['concierge-mail-sync@.service', 'concierge-mail-sync@.timer', 'concierge-mail-backup@.service', 'concierge-mail-backup@.timer']


def ssh(args, cmd, data=None, check=True):
    base = ['ssh', '-o', 'BatchMode=yes', '-i', os.path.expanduser(args.key), f'{args.user}@{args.host}', cmd]
    proc = subprocess.run(base, input=data, capture_output=True)
    if check and proc.returncode:
        sys.exit(f'host command failed ({proc.returncode}): {proc.stderr.decode(errors="replace").strip()[:500]}')
    return proc.stdout.decode(errors='replace')


def slug_ok(slug):
    if not SLUG.match(slug):
        sys.exit('slug: lowercase letters, digits, dash; starts with a letter; at most 21 characters')
    return slug


def deploy(args):
    import io
    import tarfile
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode='w:gz') as tar:
        def add(src, arc):
            for path in sorted(src.rglob('*.py')):
                if 'test_' in path.name or '__pycache__' in path.parts:
                    continue
                info = tar.gettarinfo(path, arc + '/' + path.relative_to(src).as_posix())
                info.uid = info.gid = 0
                info.mode = 0o644
                with path.open('rb') as fh:
                    tar.addfile(info, fh)
        add(MODULE / 'home', 'modules/concierge-mail/home')
        add(REPO / 'tools' / 'read-guard', 'tools/read-guard')
    ssh(args, 'set -e; t=$(mktemp -d); sudo tar -xzf - -C "$t"; sudo rm -rf /opt/concierge-mail.new; sudo mkdir -p /opt/concierge-mail.new;'
              ' sudo cp -a "$t"/. /opt/concierge-mail.new/; sudo rm -rf "$t" /opt/concierge-mail.old;'
              ' [ -d /opt/concierge-mail ] && sudo mv /opt/concierge-mail /opt/concierge-mail.old; sudo mv /opt/concierge-mail.new /opt/concierge-mail;'
              ' sudo chmod -R a+rX,go-w /opt/concierge-mail; sudo mkdir -p /etc/concierge-mail /etc/credstore.encrypted /srv/data/mail;'
              ' sudo chmod 755 /etc/concierge-mail; sudo chmod 711 /srv/data/mail', buf.getvalue())
    for unit in UNITS:  # stored as name-at.ext: signed module files allow no '@'
        source = HERE / unit.replace('@', '-at')
        ssh(args, f'sudo tee /etc/systemd/system/{unit} >/dev/null', source.read_bytes().replace(b'\r\n', b'\n'))
    ssh(args, 'sudo systemctl daemon-reload')
    print(json.dumps({'deployed': '/opt/concierge-mail', 'units': UNITS}))


def add(args):
    slug = slug_ok(args.slug)
    account = args.account.strip().lower()
    if not re.fullmatch(r'[^@\s]+@[^@\s]+\.[^@\s]+', account):
        sys.exit('account: an email address')
    env = {'ACCOUNT': account, 'AIWB_ENV': args.env}
    if args.backup_public:
        base64.b64decode(args.backup_public, validate=True)
        env['BACKUP_PUBLIC_KEY'] = args.backup_public
    body = ''.join(f'{k}={v}\n' for k, v in env.items())
    user = 'cmail-' + slug
    ssh(args, f'set -e; id -u {user} >/dev/null 2>&1 || sudo useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin {user};'
              f' sudo install -d -m 0700 -o {user} -g {user} /srv/data/mail/{slug}; sudo tee /etc/concierge-mail/{slug}.env >/dev/null;'
              f' sudo chown root:{user} /etc/concierge-mail/{slug}.env; sudo chmod 0640 /etc/concierge-mail/{slug}.env', body.encode())
    print(json.dumps({'slug': slug, 'user': user, 'data': f'/srv/data/mail/{slug}', 'env': env | ({'BACKUP_PUBLIC_KEY': 'set'} if args.backup_public else {})}))


def _client(args):
    if args.client_json:
        data = json.loads(Path(args.client_json).read_text(encoding='utf-8'))
        data = data.get('installed') or data
    elif args.client_credman:
        sys.path.insert(0, str(REPO / 'tools' / 'gsheets-bridge'))
        import importlib.util
        spec = importlib.util.spec_from_file_location('gsb', REPO / 'tools' / 'gsheets-bridge' / 'gsheets-bridge.py')
        if not spec.loader or not (REPO / 'tools' / 'gsheets-bridge' / 'gsheets-bridge.py').is_file():
            sys.exit('Credential Manager reader not found')
        gsb = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(gsb)
        data = json.loads(gsb.read_credential(args.client_credman))
    else:
        sys.exit('connect needs --client-json or --client-credman (an installed-app OAuth client)')
    if not data.get('client_id') or not data.get('client_secret'):
        sys.exit('OAuth client without client_id/client_secret')
    return data['client_id'], data['client_secret']


def connect(args):
    """Installed-app OAuth with PKCE and a loopback redirect; the refresh token is sealed on the host."""
    slug = slug_ok(args.slug)
    env = ssh(args, f'sudo cat /etc/concierge-mail/{slug}.env')
    account = re.search(r'^ACCOUNT=(.+)$', env, re.M).group(1).strip()
    client_id, client_secret = _client(args)
    verifier = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b'=').decode()
    state = secrets.token_urlsafe(16)
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
    server = http.server.HTTPServer(('127.0.0.1', 0), Handler)
    redirect = f'http://127.0.0.1:{server.server_port}/'
    url = 'https://accounts.google.com/o/oauth2/v2/auth?' + urllib.parse.urlencode({
        'response_type': 'code', 'client_id': client_id, 'redirect_uri': redirect, 'scope': SCOPES,
        'access_type': 'offline', 'prompt': 'consent', 'state': state, 'login_hint': account,
        'code_challenge': base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode(),
        'code_challenge_method': 'S256'})
    print('Open and allow (read-only Gmail + files this app creates in Drive):\n' + url, flush=True)
    if not args.no_browser:
        webbrowser.open(url)
    server.timeout = args.wait
    thread = threading.Thread(target=server.handle_request)
    thread.start()
    thread.join(args.wait + 5)
    server.server_close()
    if 'code' not in got:
        sys.exit('no consent received' + (f' ({got.get("error")})' if got.get('error') else ''))
    body = urllib.parse.urlencode({'code': got['code'], 'client_id': client_id, 'client_secret': client_secret,
                                   'redirect_uri': redirect, 'grant_type': 'authorization_code', 'code_verifier': verifier}).encode()
    with urllib.request.urlopen(urllib.request.Request('https://oauth2.googleapis.com/token', data=body), timeout=30) as resp:
        tok = json.loads(resp.read())
    granted = set(tok.get('scope', '').split())
    if 'https://www.googleapis.com/auth/gmail.readonly' not in granted or not tok.get('refresh_token'):
        sys.exit('Gmail read access was not granted')
    req = urllib.request.Request('https://gmail.googleapis.com/gmail/v1/users/me/profile', headers={'Authorization': 'Bearer ' + tok['access_token']})
    with urllib.request.urlopen(req, timeout=30) as resp:
        email = json.loads(resp.read())['emailAddress'].lower()
    if email != account:
        sys.exit(f'signed in as another mailbox than {account}; nothing saved')
    cred = json.dumps({'client_id': client_id, 'client_secret': client_secret, 'refresh_token': tok['refresh_token'],
                       'account': account, 'scopes': sorted(granted)}).encode()
    ssh(args, f'sudo systemd-creds encrypt --name=gmail - /etc/credstore.encrypted/concierge-mail.{slug}.cred'
              f' && sudo chmod 0600 /etc/credstore.encrypted/concierge-mail.{slug}.cred', cred)
    del cred, tok
    print(json.dumps({'slug': slug, 'account': account, 'credential': f'/etc/credstore.encrypted/concierge-mail.{slug}.cred',
                      'drive_backup': 'https://www.googleapis.com/auth/drive.file' in granted}))
    if args.start:
        start(args)


def start(args, on=True):
    slug = slug_ok(args.slug)
    env = ssh(args, f'sudo cat /etc/concierge-mail/{slug}.env')
    timers = [f'concierge-mail-sync@{slug}.timer'] + ([f'concierge-mail-backup@{slug}.timer'] if 'BACKUP_PUBLIC_KEY=' in env else [])
    if not on:
        timers = [f'concierge-mail-sync@{slug}.timer', f'concierge-mail-backup@{slug}.timer']
    ssh(args, ('sudo systemctl enable --now ' if on else 'sudo systemctl disable --now ') + ' '.join(timers), check=on)
    print(json.dumps({'slug': slug, 'enabled' if on else 'disabled': timers}))


def run(args):
    slug = slug_ok(args.slug)
    # An agent job reads only its own environment's mailbox. The binding check on the host comes with the relay (stage 2);
    # until then household members' jobs have no SSH identity for the hub.
    env = os.environ.get('AIWB_ENV', '')
    if os.environ.get('AIWB_JOB_ROLE') == 'member':
        sys.exit('refused: household members read their mail through their own environment, not over SSH')
    if env:
        owner = re.search(r'^AIWB_ENV=(.+)$', ssh(args, f'sudo cat /etc/concierge-mail/{slug}.env'), re.M)
        if not owner or owner.group(1).strip() != env:
            sys.exit(f'refused: mailbox {slug} does not belong to environment {env}')
    rest = [a for a in args.rest if a != '--']
    if any(a in ('--root',) for a in rest):
        sys.exit('--root is fixed to the person directory')
    cmd = ('sudo systemd-run --quiet --wait --pipe --collect'
           f' -p User=cmail-{slug} -p Group=cmail-{slug} -p EnvironmentFile=/etc/concierge-mail/{slug}.env'
           f' -p LoadCredentialEncrypted=gmail:/etc/credstore.encrypted/concierge-mail.{slug}.cred'
           f' -p ProtectSystem=strict -p ReadWritePaths=/srv/data/mail/{slug} -p ProtectHome=yes -p PrivateTmp=yes'
           f' /usr/bin/python3 /opt/concierge-mail/modules/concierge-mail/home/mailcli.py --root /srv/data/mail/{slug} '
           + ' '.join(shlex.quote(a) for a in rest))
    sys.stdout.write(ssh(args, cmd))


def status(args):
    slugs = [slug_ok(args.slug)] if args.slug else [s for s in ssh(args, 'ls /etc/concierge-mail 2>/dev/null').split() if s.endswith('.env')]
    out = []
    for name in slugs:
        slug = name.removesuffix('.env')
        timers = ssh(args, f'systemctl show -p ActiveState,LastTriggerUSec concierge-mail-sync@{slug}.timer concierge-mail-backup@{slug}.timer', check=False)
        result = ssh(args, f'systemctl show -p Result,ExecMainExitTimestamp concierge-mail-sync@{slug}.service concierge-mail-backup@{slug}.service', check=False)
        size = ssh(args, f'sudo du -sh /srv/data/mail/{slug} 2>/dev/null | cut -f1', check=False).strip()
        cred = ssh(args, f'sudo test -f /etc/credstore.encrypted/concierge-mail.{slug}.cred && echo yes || echo no', check=False).strip()
        out.append({'slug': slug, 'credential': cred, 'size': size, 'timers': timers.split(), 'services': result.split()})
    print(json.dumps(out, indent=1, ensure_ascii=False))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--host', default=os.environ.get('HOMEHUB_HOST', 'homehub'))
    p.add_argument('--user', default=os.environ.get('HOMEHUB_USER', os.environ.get('USERNAME', 'root')).lower())
    p.add_argument('--key', default=os.environ.get('HOMEHUB_KEY', '~/.ssh/id_ed25519'))
    sub = p.add_subparsers(dest='cmd', required=True)
    sub.add_parser('deploy')
    a = sub.add_parser('add')
    a.add_argument('slug')
    a.add_argument('--account', required=True)
    a.add_argument('--env', required=True, help='AI Workbench environment id of the person')
    a.add_argument('--backup-public', help='public key from `mailcli.py backup-keygen` (the private key stays with the person)')
    c = sub.add_parser('connect')
    c.add_argument('slug')
    c.add_argument('--client-json')
    c.add_argument('--client-credman')
    c.add_argument('--wait', type=int, default=600)
    c.add_argument('--no-browser', action='store_true')
    c.add_argument('--start', action='store_true', help='enable the timers right after the credential is saved')
    for name in ('start', 'stop'):
        sub.add_parser(name).add_argument('slug')
    s = sub.add_parser('status')
    s.add_argument('slug', nargs='?')
    r = sub.add_parser('run')
    r.add_argument('slug')
    r.add_argument('rest', nargs=argparse.REMAINDER)
    args = p.parse_args()
    {'deploy': deploy, 'add': add, 'connect': connect, 'start': start, 'stop': lambda a: start(a, False),
     'status': status, 'run': run}[args.cmd](args)


if __name__ == '__main__':
    main()
