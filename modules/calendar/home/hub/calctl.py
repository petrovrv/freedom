"""Calendar copy on the home server, managed from a PC over SSH (Linux host with systemd).

  deploy                      copy the module code (+ provider-accounts, read-guard) to /opt/calendar, install the unit
                              templates, register the Calendar page handler with the home relay (module home-relay)
  add SLUG --account A --env E   person user aiwb-SLUG (provider-accounts), /srv/data/calendar/SLUG (0700),
                              /etc/calendar/SLUG.env
  connect SLUG --client-json F | --client-credman TARGET   the person adds calendar.read to their shared account
                              (module provider-accounts: one sign-in for all modules, missing rights added incrementally)
  start SLUG / stop SLUG      enable/disable the sync timer (every 5 minutes)
  sync SLUG                   one sync now (as aiwb-SLUG with its credential); prints counters, no content
  status [SLUG]               timer, last run, credential present, size (no content)

Each person: own Unix user (aiwb-SLUG, shared by the modules of that person), own directory, own credential narrowed to
calendar.read; one person's service cannot read another's calendar (SPEC-calendar §3.2).
"""
from __future__ import annotations
import argparse
import io
import json
import os
import re
import sys
import tarfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
MODULE = HERE.parents[1]
REPO = HERE.parents[3]
CAPABILITIES = ['calendar.read']
sys.path.insert(0, str(MODULE.parent / 'provider-accounts' / 'home' / 'hub'))
sys.path.insert(0, str(MODULE.parent / 'home-relay' / 'home' / 'hub'))
import accountctl  # noqa: E402  (module provider-accounts)
import relayctl  # noqa: E402  (module home-relay)
from relayctl import slug_ok, ssh  # noqa: E402

UNITS = ['calendar-sync@.service', 'calendar-sync@.timer']
CODE = '/opt/calendar'
CLI = f'{CODE}/modules/calendar/home/calcli.py'
HANDLER = ['/usr/bin/python3', CLI, '--root', '/srv/data/calendar/{slug}', 'relay-handler']


def _tar():
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode='w:gz') as tar:
        def add(src, arc):
            for path in sorted(src.rglob('*.py')):
                if path.name.startswith('test_') or '__pycache__' in path.parts:
                    continue
                info = tar.gettarinfo(path, arc + '/' + path.relative_to(src).as_posix())
                info.uid = info.gid = 0
                info.mode = 0o644
                with path.open('rb') as fh:
                    tar.addfile(info, fh)
        add(MODULE / 'home', 'modules/calendar/home')
        add(MODULE.parent / 'provider-accounts' / 'home', 'modules/provider-accounts/home')
        add(REPO / 'tools' / 'read-guard', 'tools/read-guard')
    return buf.getvalue()


def people(args):
    return [n.removesuffix('.env') for n in ssh(args, 'ls /etc/calendar 2>/dev/null || true').split()
            if n.endswith('.env') and relayctl.SLUG.match(n.removesuffix('.env'))]


def deploy(args):
    ssh(args, f'set -e; t=$(mktemp -d); sudo tar -xzf - -C "$t"; sudo rm -rf {CODE}.new; sudo mkdir -p {CODE}.new;'
              f' sudo cp -a "$t"/. {CODE}.new/; sudo rm -rf "$t" {CODE}.old; [ -d {CODE} ] && sudo mv {CODE} {CODE}.old;'
              f' sudo mv {CODE}.new {CODE}; sudo chmod -R a+rX,go-w {CODE}; sudo mkdir -p /etc/calendar /srv/data/calendar;'
              ' sudo chmod 755 /etc/calendar; sudo chmod 711 /srv/data/calendar', _tar())
    for unit in UNITS:  # stored as name-at.ext: signed module files allow no '@'
        source = HERE / unit.replace('@', '-at')
        ssh(args, f'sudo tee /etc/systemd/system/{unit} >/dev/null', source.read_bytes().replace(b'\r\n', b'\n'))
    ssh(args, 'sudo systemctl daemon-reload')
    failed = relayctl.register(args, 'calendar', HANDLER, env_file='/etc/calendar/{slug}.env',
                               write=['/srv/data/calendar/{slug}'], only_if='/etc/calendar/{slug}.env')
    print(json.dumps({'deployed': CODE, 'units': UNITS, 'people': people(args), 'relayHandler': 'calendar',
                      'notStarted': sorted(set(failed or []))}))


def add(args):
    slug = slug_ok(args.slug)
    account = args.account.strip().lower()
    if not re.fullmatch(r'[^@\s]+@[^@\s]+\.[^@\s]+', account):
        sys.exit('account: an email address')
    if not relayctl.ENV_RE.match(args.env):
        sys.exit('env: an environment id')
    user = accountctl.person_user(slug)
    accountctl.ensure_person(args, slug)
    body = f'ACCOUNT={account}\nAIWB_ENV={args.env}\n'
    ssh(args, f'set -e; sudo install -d -m 0700 -o {user} -g {user} /srv/data/calendar/{slug};'
              f' sudo tee /etc/calendar/{slug}.env >/dev/null; sudo chown root:{user} /etc/calendar/{slug}.env;'
              f' sudo chmod 0640 /etc/calendar/{slug}.env', body.encode())
    # the worker serves the module only for people with an env file (only_if): pick it up now
    ssh(args, f'sudo systemctl try-restart home-relay@{slug}.service', check=False)
    print(json.dumps({'slug': slug, 'user': user, 'data': f'/srv/data/calendar/{slug}', 'env': {'ACCOUNT': account, 'AIWB_ENV': args.env}}))


def _env(args, slug):
    env = ssh(args, f'sudo cat /etc/calendar/{slug}.env')
    return (re.search(r'^ACCOUNT=(.+)$', env, re.M).group(1).strip(), re.search(r'^AIWB_ENV=(.+)$', env, re.M).group(1).strip())


def connect(args):
    slug = slug_ok(args.slug)
    args.account, args.env = _env(args, slug)
    args.provider = 'google'
    record = accountctl.connect(args, want=CAPABILITIES)
    granted = record.get('providers', {}).get('google', {}).get('capabilities', [])
    if 'calendar.read' not in granted:
        sys.exit('Calendar read access was not granted')
    if args.start:
        start(args)


def start(args, on=True):
    slug = slug_ok(args.slug)
    timer = f'calendar-sync@{slug}.timer'
    ssh(args, ('sudo systemctl enable --now ' if on else 'sudo systemctl disable --now ') + timer, check=on)
    print(json.dumps({'slug': slug, 'enabled' if on else 'disabled': [timer]}))


def sync_now(args):
    slug = slug_ok(args.slug)
    ssh(args, f'sudo systemctl start calendar-sync@{slug}.service')
    print(ssh(args, f'sudo journalctl -u calendar-sync@{slug}.service -n 3 -o cat --no-pager', check=False).strip())


def status(args):
    out = []
    for slug in ([slug_ok(args.slug)] if args.slug else people(args)):
        out.append({'slug': slug,
                    'timer': ssh(args, f'systemctl show -p ActiveState,LastTriggerUSec calendar-sync@{slug}.timer', check=False).split(),
                    'service': ssh(args, f'systemctl show -p Result,ExecMainExitTimestamp calendar-sync@{slug}.service', check=False).split(),
                    'credential': ssh(args, f'sudo test -f /etc/credstore.encrypted/account.{slug}.google.cred && echo yes || echo no', check=False).strip(),
                    'size': ssh(args, f'sudo du -sh /srv/data/calendar/{slug} 2>/dev/null | cut -f1', check=False).strip(),
                    'worker': ssh(args, f'systemctl is-active home-relay@{slug}.service', check=False).strip()})
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
    a.add_argument('--env', required=True)
    c = sub.add_parser('connect')
    c.add_argument('slug')
    accountctl.add_connect_args(c)
    c.add_argument('--start', action='store_true')
    for name in ('start', 'stop', 'sync'):
        sub.add_parser(name).add_argument('slug')
    s = sub.add_parser('status')
    s.add_argument('slug', nargs='?')
    args = p.parse_args()
    {'deploy': deploy, 'add': add, 'connect': connect, 'start': start, 'stop': lambda a: start(a, False),
     'sync': sync_now, 'status': status}[args.cmd](args)


if __name__ == '__main__':
    main()
