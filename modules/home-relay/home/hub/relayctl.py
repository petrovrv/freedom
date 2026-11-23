"""Relay worker of each person on the home server (module home-relay), managed from a PC over SSH (Linux host with systemd).

  deploy                      copy the worker to /opt/home-relay, install the unit template home-relay@.service, restart
                              the workers that run
  pair SLUG [--site URL] [--env E]   the pages of SLUG's environment reach this home server: the site (push key of this PC)
                              issues the worker token, it goes straight into the encrypted credential home-relay.SLUG.cred;
                              AIWB_ENV (from the person's record of provider-accounts, /etc/freedom-accounts/SLUG.json) and
                              RELAY_URL go to /etc/home-relay/SLUG.env, the worker home-relay@SLUG starts
  unpair SLUG [--site URL]    stop the worker and revoke its token on the site
  status [SLUG]               worker state and the modules it serves (no content)

Modules plug in from their own deploy with register(args, module, exec, ...): a root-owned
/etc/home-relay/modules.d/<module>.json names the handler command ({slug} is filled in per person) and a drop-in of the
unit template gives the handler the folders it may write. The worker runs as the person user aiwb-SLUG (provider-accounts),
so one person's handler can never open another person's data.
"""
from __future__ import annotations
import argparse
import io
import json
import os
import re
import shlex
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
MODULE = HERE.parents[1]
sys.path.insert(0, str(MODULE.parent / 'provider-accounts' / 'home' / 'hub'))
import accountctl  # noqa: E402  (module provider-accounts: the person user and record)

SLUG = re.compile(r'^[a-z][a-z0-9-]{0,20}$')
MODULE_RE = re.compile(r'^[a-z][a-z0-9-]{1,39}$')
ENV_RE = re.compile(r'^[a-z0-9][a-z0-9_-]{0,39}$')
UNIT = 'home-relay@.service'
CONF = '/etc/home-relay'


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


def install_json():
    if os.environ.get('AIWB_INSTALL'):
        path = Path(os.environ['AIWB_INSTALL'])
    elif os.name == 'nt' and os.environ.get('LOCALAPPDATA'):
        path = Path(os.environ['LOCALAPPDATA']) / 'AIWorkbench' / 'install.json'
    else:
        path = Path.home() / '.config' / 'aiwb' / 'install.json'
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        return {}


def site_call(url, payload):
    """push.php of the own site with the push key of this PC (keyring aiwb/co-push)."""
    import keyring
    key = keyring.get_password('aiwb/co-push', 'default')
    if not url or not key:
        sys.exit('needs install.json site.url (or --site) and the push key in keyring aiwb/co-push')
    req = urllib.request.Request(url + '/agent/push.php', data=json.dumps(payload).encode(), method='POST',
                                 headers={'Content-Type': 'application/json', 'X-CO-Push-Key': key, 'User-Agent': 'freedom-home-relay/0.1'})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read().decode('utf-8') or '{}')


def site_url(args):
    return (args.site or str((install_json().get('site') or {}).get('url') or '')).rstrip('/')


def people(args):
    return [n.removesuffix('.env') for n in ssh(args, f'ls {CONF} 2>/dev/null || true').split()
            if n.endswith('.env') and SLUG.match(n.removesuffix('.env'))]


def restart_running(args):
    """Workers that run pick up a new worker, unit or handler registration; returns the units that are not active again
    (a failed listing ends the command: nothing may be taken for a success that was not checked)."""
    units = ssh(args, 'systemctl list-units --plain --no-legend --state=active,activating "home-relay@*.service" | cut -d" " -f1').split()
    failed = []
    for u in units:
        if ssh(args, f'sudo systemctl restart {shlex.quote(u)} && sleep 2 && systemctl is-active -q {shlex.quote(u)} && echo up || echo down',
               check=False).strip() != 'up':
            failed.append(u)
    return failed


def serving(args, slug):
    """True only when the worker of the person is set up and running (an unclear answer is False)."""
    return ssh(args, f'sudo test -f {CONF}/{slug_ok(slug)}.env && systemctl is-active -q home-relay@{slug}.service && echo up || echo down',
               check=False).strip() == 'up'


def deploy(args):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode='w:gz') as tar:
        src = MODULE / 'home'
        for path in sorted(src.glob('*.py')):
            if path.name.startswith('test_'):
                continue
            info = tar.gettarinfo(path, 'modules/home-relay/home/' + path.name)
            info.uid = info.gid = 0
            info.mode = 0o644
            with path.open('rb') as fh:
                tar.addfile(info, fh)
    ssh(args, 'set -e; t=$(mktemp -d); sudo tar -xzf - -C "$t"; sudo rm -rf /opt/home-relay.new; sudo mkdir -p /opt/home-relay.new;'
              ' sudo cp -a "$t"/. /opt/home-relay.new/; sudo rm -rf "$t" /opt/home-relay.old;'
              ' [ -d /opt/home-relay ] && sudo mv /opt/home-relay /opt/home-relay.old; sudo mv /opt/home-relay.new /opt/home-relay;'
              f' sudo chmod -R a+rX,go-w /opt/home-relay; sudo mkdir -p {CONF}/modules.d /etc/credstore.encrypted;'
              f' sudo chmod 755 {CONF} {CONF}/modules.d', buf.getvalue())
    ssh(args, f'sudo tee /etc/systemd/system/{UNIT} >/dev/null', (HERE / UNIT.replace('@', '-at')).read_bytes().replace(b'\r\n', b'\n'))
    ssh(args, 'sudo systemctl daemon-reload')
    failed = restart_running(args)
    out = {'deployed': '/opt/home-relay', 'unit': UNIT, 'modules': registered(args)}
    if failed:
        sys.exit(json.dumps(out | {'notStarted': failed}))
    print(json.dumps(out))


def registered(args):
    return sorted(n.removesuffix('.json') for n in ssh(args, f'ls {CONF}/modules.d 2>/dev/null || true').split() if n.endswith('.json'))


def register(args, module, exec_argv, env_file=None, write=(), only_if=None):
    """A module's handler for every person's worker (called from the module's own deploy). exec_argv / env_file / write /
    only_if may contain {slug}; write: folders the handler may write (a drop-in of the unit template, '-' = may be missing);
    only_if: a path that must exist for a person to be served (e.g. the module's own config of that person)."""
    if not MODULE_RE.match(module) or not exec_argv or not str(exec_argv[0]).startswith('/'):
        sys.exit(f'register: bad module {module!r} or command')
    spec = {'exec': [str(a) for a in exec_argv]}
    if env_file:
        spec['envFile'] = env_file
    if only_if:
        spec['if'] = only_if
    paths = []
    for p in write:
        p = str(p).replace('{slug}', '%i')
        if not re.fullmatch(r'/[A-Za-z0-9_./%-]+', p) or '..' in p:
            sys.exit(f'register: bad path {p!r}')
        paths.append('-' + p)
    ssh(args, f'set -e; sudo mkdir -p {CONF}/modules.d /etc/systemd/system/{UNIT}.d;'
              f' sudo tee {CONF}/modules.d/{module}.json.new >/dev/null; sudo chmod 0644 {CONF}/modules.d/{module}.json.new;'
              f' sudo mv {CONF}/modules.d/{module}.json.new {CONF}/modules.d/{module}.json', json.dumps(spec, indent=1).encode())
    drop = f'/etc/systemd/system/{UNIT}.d/{module}.conf'
    if paths:
        ssh(args, f'sudo tee {drop} >/dev/null', ('[Service]\nReadWritePaths=' + ' '.join(paths) + '\n').encode())
    else:
        ssh(args, f'sudo rm -f {drop}')
    ssh(args, 'sudo systemctl daemon-reload')
    return restart_running(args)


def unregister(args, module):
    if not MODULE_RE.match(module):
        sys.exit(f'unregister: bad module {module!r}')
    ssh(args, f'sudo rm -f {CONF}/modules.d/{module}.json /etc/systemd/system/{UNIT}.d/{module}.conf; sudo systemctl daemon-reload')
    return restart_running(args)


def person_env(args, slug):
    """The environment of a person: their record of provider-accounts (the home server's own binding)."""
    record = accountctl.read_record(args, slug) or {}
    env = str(record.get('env') or '')
    given = getattr(args, 'env', None) or ''
    if env and given and env != given:
        sys.exit(f'refused: person {slug} belongs to environment {env}, not {given}')
    env = env or given
    if not ENV_RE.match(env):
        sys.exit(f'person {slug} has no environment yet: connect an account first (provider-accounts) or give --env')
    return env


def pair(args):
    slug = slug_ok(args.slug)
    env = person_env(args, slug)
    user = accountctl.person_user(slug)
    # the person user is made (or an older module user renamed) by the modules of the person, never here: pairing first
    # would leave an older user beside a new one
    if accountctl.ensure_person(args, slug, check=True) != 'exists':
        sys.exit(f'person user {user} is not set up yet: deploy the modules of {slug} first (they create or rename it)')
    url = site_url(args)
    got = site_call(url, {'op': 'relay_worker_token', 'env': env})
    token = got.get('token') if isinstance(got, dict) else None
    if not token:
        sys.exit('the site gave no worker token: ' + str((got or {}).get('error', 'no answer')))
    worker = url + '/agent/module.php?m=home-relay&r=worker'
    cred = f'/etc/credstore.encrypted/home-relay.{slug}.cred'
    ssh(args, f'sudo systemd-creds encrypt --name=relay - {cred}.new && sudo chmod 0600 {cred}.new && sudo mv {cred}.new {cred}', token.encode())
    del token, got
    body = f'AIWB_ENV={env}\nRELAY_URL={worker}\n'
    ssh(args, f'set -e; f={CONF}/{slug}.env; sudo mkdir -p {CONF}; sudo tee $f.new >/dev/null; sudo chown root:{user} $f.new;'
              f' sudo chmod 0640 $f.new; sudo mv $f.new $f', body.encode())
    ssh(args, f'sudo systemctl enable home-relay@{slug}.service && sudo systemctl restart home-relay@{slug}.service')
    print(json.dumps({'slug': slug, 'env': env, 'relay': worker, 'credential': cred, 'worker': f'home-relay@{slug}', 'modules': registered(args)}))


def unpair(args):
    slug = slug_ok(args.slug)
    env = re.search(r'^AIWB_ENV=(.+)$', ssh(args, f'sudo cat {CONF}/{slug}.env'), re.M).group(1).strip()
    ssh(args, f'sudo systemctl disable --now home-relay@{slug}.service', check=False)
    got = site_call(site_url(args), {'op': 'relay_worker_token', 'env': env, 'revoke': True})
    ssh(args, f'sudo rm -f /etc/credstore.encrypted/home-relay.{slug}.cred {CONF}/{slug}.env')
    print(json.dumps({'slug': slug, 'env': env, 'revoked': bool(got.get('ok'))}))


def status(args):
    out = []
    for slug in ([slug_ok(args.slug)] if args.slug else people(args)):
        state = ssh(args, f'systemctl is-active home-relay@{slug}.service', check=False).strip()
        log = ssh(args, f'sudo journalctl -u home-relay@{slug}.service -n 3 --no-pager -o cat', check=False).strip().splitlines()
        out.append({'slug': slug, 'worker': state, 'log': log[-3:]})
    print(json.dumps({'modules': registered(args), 'people': out}, indent=1, ensure_ascii=False))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    accountctl.add_host_args(p)
    sub = p.add_subparsers(dest='cmd', required=True)
    sub.add_parser('deploy')
    pr = sub.add_parser('pair')
    pr.add_argument('slug')
    pr.add_argument('--site', help='site URL (default: install.json site.url)')
    pr.add_argument('--env', help='environment of the person when provider-accounts has no record of them yet')
    up = sub.add_parser('unpair')
    up.add_argument('slug')
    up.add_argument('--site')
    s = sub.add_parser('status')
    s.add_argument('slug', nargs='?')
    args = p.parse_args()
    {'deploy': deploy, 'pair': pair, 'unpair': unpair, 'status': status}[args.cmd](args)


if __name__ == '__main__':
    main()
