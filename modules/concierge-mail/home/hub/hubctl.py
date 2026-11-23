"""Mail store on the home server, managed from a PC over SSH (Linux host with systemd).

  deploy                      copy the module code and read-guard to /opt/concierge-mail, install the unit templates,
                              register the Mail page handler with the home relay (module home-relay)
  add SLUG --account A --env E [--backup-public KEY]   person user aiwb-SLUG (provider-accounts), /srv/data/mail/SLUG (0700), /etc/concierge-mail/SLUG.env
  connect SLUG --client-json F | --client-credman TARGET   the person allows mail.read + drive.appfiles of their shared
                              account (module provider-accounts: one sign-in for all modules of the person, missing rights are
                              added incrementally); the token goes straight into the encrypted systemd credential
                              /etc/credstore.encrypted/account.SLUG.google.cred (never into a file or keyring of this PC)
  migrate SLUG                re-encrypt the older concierge-mail.SLUG.cred as the shared account credential
  (the Mail page reaches a mailbox through the person's home-relay worker: modules/home-relay/home/hub/relayctl.py pair SLUG)
  start SLUG / stop SLUG      enable/disable the sync timer (and the backup timer when a backup key is set)
  status [SLUG]               timers, last run, per-mailbox counters (no message content)
  run SLUG -- ARGS            mailcli.py as aiwb-SLUG with its credential (diagnostics: gmail-status, search ...)

Each person: own Unix user (aiwb-SLUG, shared by the modules of that person), own directory, own credential; one person's
service cannot read another's mail. deploy renames the older user cmail-SLUG to aiwb-SLUG (same uid, units restarted).
"""
from __future__ import annotations
import argparse
import base64
import json
import os
import re
import shlex
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
MODULE = HERE.parents[1]
REPO = HERE.parents[3]
CAPABILITIES = ['mail.read', 'drive.appfiles']
sys.path.insert(0, str(MODULE.parent / 'provider-accounts' / 'home' / 'hub'))
sys.path.insert(0, str(MODULE.parent / 'home-relay' / 'home' / 'hub'))
import accountctl  # noqa: E402  (module provider-accounts)
import relayctl  # noqa: E402  (module home-relay)
SLUG = re.compile(r'^[a-z][a-z0-9-]{0,20}$')
LEGACY_USER = 'cmail-'  # concierge-mail 0.7-0.8 ran as its own user; 0.9 runs as the person user aiwb-SLUG
UNITS = ['concierge-mail-sync@.service', 'concierge-mail-sync@.timer', 'concierge-mail-backup@.service', 'concierge-mail-backup@.timer']
OLD_RELAY = 'concierge-mail-relay@'  # 0.8-0.9 had its own page worker; 0.10 is a handler of the person's home-relay worker
HANDLER = ['/usr/bin/python3', '/opt/concierge-mail/modules/concierge-mail/home/mailcli.py', '--root', '/srv/data/mail/{slug}', 'relay-handler']


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
    # 0.8-0.9 -> 0.10: the old Mail page worker goes away (the new code has no relay-serve). A person paired with it (its
    # credential is there) must first run as aiwb-SLUG (0.9 renames cmail-SLUG) and be paired with the home relay, whose
    # worker runs. Checked before anything changes; an unclear answer counts as "not ready".
    blocked = {}
    for slug in slugs(args):
        got = ssh(args, f'if sudo test -f /etc/credstore.encrypted/{OLD_RELAY[:-1]}.{slug}.cred; then echo old; elif sudo true; then echo none; fi',
                  check=False).strip()
        if got == 'none':
            continue
        if got != 'old':  # no clear answer: change nothing
            sys.exit(f'deploy: could not check the old worker of {slug}; nothing was changed')
        if ssh(args, f'id -u {LEGACY_USER + slug} >/dev/null 2>&1 && echo legacy || echo moved', check=False).strip() != 'moved':
            blocked[slug] = 'deploy concierge-mail 0.9.0 first (it moves the person to aiwb-' + slug + ')'
        elif not relayctl.serving(args, slug):
            blocked[slug] = f'relayctl.py deploy; relayctl.py pair {slug}'
    if blocked:
        sys.exit(json.dumps({'refused': "these persons' Mail page still runs on the old worker; nothing was changed", 'do': blocked}))
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
        add(MODULE.parent / 'provider-accounts' / 'home', 'modules/provider-accounts/home')
        add(REPO / 'tools' / 'read-guard', 'tools/read-guard')
    ssh(args, 'set -e; t=$(mktemp -d); sudo tar -xzf - -C "$t"; sudo rm -rf /opt/concierge-mail.new; sudo mkdir -p /opt/concierge-mail.new;'
              ' sudo cp -a "$t"/. /opt/concierge-mail.new/; sudo rm -rf "$t" /opt/concierge-mail.old;'
              ' [ -d /opt/concierge-mail ] && sudo mv /opt/concierge-mail /opt/concierge-mail.old; sudo mv /opt/concierge-mail.new /opt/concierge-mail;'
              ' sudo chmod -R a+rX,go-w /opt/concierge-mail; sudo mkdir -p /etc/concierge-mail /etc/credstore.encrypted /srv/data/mail;'
              ' sudo chmod 755 /etc/concierge-mail; sudo chmod 711 /srv/data/mail', buf.getvalue())
    # 0.7 -> 0.8: the units load the shared account credential; move each person's old one first, so that no timer run
    # meets a unit that wants a credential which is not there yet
    for name in ssh(args, 'ls /etc/concierge-mail 2>/dev/null || true').split():
        slug = name.removesuffix('.env')
        if name.endswith('.env') and SLUG.match(slug) and ssh(args, f'sudo test -f /etc/credstore.encrypted/concierge-mail.{slug}.cred'
                                                              f' && ! sudo test -f /etc/credstore.encrypted/account.{slug}.google.cred'
                                                              ' && echo move || true').strip() == 'move':
            args.slug = slug
            migrate(args)
    people = slugs(args)
    # one Unix user per person (provider-accounts): every clash is found before any unit is stopped
    for slug in people:
        accountctl.ensure_person(args, slug, adopt=[LEGACY_USER + slug], check=True)
    users, was_active, failed = {}, [], []
    legacy = list(people)  # until its rename is done a person keeps the user it has (drop-in below)
    try:
        for slug in people:
            if ssh(args, f'id -u {LEGACY_USER + slug} >/dev/null 2>&1 && echo yes || echo no').strip() == 'yes':
                # also what runs without being enabled (started by hand) comes back below
                was_active += ssh(args, 'for u in ' + ' '.join(unit_names(slug)[:2]) + '; do'
                                        ' case $(systemctl is-active "$u") in active|activating|reloading) echo "$u";; esac; done').split()
                ssh(args, 'sudo systemctl stop ' + ' '.join(unit_names(slug)))  # a rename is refused while the old user runs
                ssh(args, f'sudo systemctl stop {OLD_RELAY}{slug}.service', check=False)  # the 0.8-0.9 worker, removed below
            try:
                users[slug] = accountctl.ensure_person(args, slug, adopt=[LEGACY_USER + slug])
                legacy.remove(slug)
            except SystemExit as e:
                users[slug] = f'kept: {e}'
    finally:
        _units(args, people, legacy)
        # what a person has enabled runs again (also when an earlier deploy stopped it and failed half way), and what ran before
        for slug in people:
            failed += ssh(args, 'for u in ' + ' '.join(unit_names(slug)[:2]) + '; do'
                                ' if systemctl is-enabled -q "$u"; then sudo systemctl start "$u" || echo "$u"; fi; done', check=False).split()
        if was_active:
            failed += ssh(args, 'for u in ' + ' '.join(was_active) + '; do sudo systemctl start "$u" || echo "$u"; done', check=False).split()
    # the Mail page handler of the home relay (every person whose mailbox is set up here); the older own worker goes
    failed += relayctl.register(args, 'concierge-mail', HANDLER, env_file='/etc/concierge-mail/{slug}.env',
                                write=['/srv/data/mail/{slug}'], only_if='/etc/concierge-mail/{slug}.env')
    # the old worker of a person goes only when that person's relay worker is verified running with the new handler
    removed = []
    for slug in people:
        if ssh(args, f'systemctl cat {OLD_RELAY}{slug}.service >/dev/null 2>&1 || sudo test -f /etc/credstore.encrypted/{OLD_RELAY[:-1]}.{slug}.cred'
                     ' && echo old || echo none', check=False).strip() != 'old':
            continue
        if f'home-relay@{slug}.service' in failed or not relayctl.serving(args, slug) or not handler_bound(args, slug):
            failed.append(f'{OLD_RELAY}{slug}.service (kept: home-relay@{slug} not running)')
            continue
        ssh(args, f'sudo systemctl disable --now {OLD_RELAY}{slug}.service; sudo rm -rf /etc/systemd/system/{OLD_RELAY}{slug}.service.d'
                  f' /etc/credstore.encrypted/{OLD_RELAY[:-1]}.{slug}.cred; sudo sed -i "/^RELAY_URL=/d" /etc/concierge-mail/{slug}.env', check=False)
        removed.append(slug)
    if ssh(args, f"if sudo sh -c 'ls /etc/credstore.encrypted/{OLD_RELAY[:-1]}.*.cred' >/dev/null 2>&1; then echo some; elif sudo true; then echo none; fi",
           check=False).strip() == 'none':
        ssh(args, f'sudo rm -f /etc/systemd/system/{OLD_RELAY}.service; sudo systemctl daemon-reload', check=False)
    if failed or legacy:
        sys.exit(json.dumps({'deployed': '/opt/concierge-mail', 'users': users, 'notStarted': sorted(set(failed)),
                             'hint': 'persons in "kept" still run as their old user; fix the reason and deploy again'}, ensure_ascii=False))
    print(json.dumps({'deployed': '/opt/concierge-mail', 'units': UNITS, 'users': users, 'relayHandler': 'concierge-mail',
                      'oldWorkerRemoved': removed}))


def handler_bound(args, slug):
    """The Mail handler answers for this person exactly as the relay worker starts it (person user, the mailbox's env file,
    the worker's environment) and reports a mailbox bound to that environment; anything else is False."""
    env = ssh(args, f"sudo sed -n 's/^AIWB_ENV=//p' /etc/home-relay/{slug}.env", check=False).strip()
    if not re.fullmatch(r'[a-z0-9][a-z0-9_-]{0,39}', env):
        return False
    cmd = ('sudo systemd-run --quiet --wait --pipe --collect'
           f' -p User={accountctl.person_user(slug)} -p Group={accountctl.person_user(slug)} -p EnvironmentFile=/etc/concierge-mail/{slug}.env'
           f' --setenv=HOME_RELAY_ENV={env} -p ProtectSystem=strict -p ReadWritePaths=/srv/data/mail/{slug} -p ProtectHome=yes -p PrivateTmp=yes'
           ' ' + ' '.join(a.replace('{slug}', slug) for a in HANDLER))
    try:
        out = json.loads(ssh(args, cmd, b'{"op": "status"}\n', check=False).strip().splitlines()[0])
    except (ValueError, IndexError):
        return False
    return isinstance(out, dict) and isinstance(out.get('status'), dict) and out['status'].get('bound') is True


def _units(args, people, legacy):
    """Unit templates (User=aiwb-%i) and, for a person not moved yet, a drop-in with the user and primary group that
    person has now (cmail-SLUG, or aiwb-SLUG with the older group after an interrupted rename)."""
    for unit in UNITS:  # stored as name-at.ext: signed module files allow no '@'
        source = HERE / unit.replace('@', '-at')
        ssh(args, f'sudo tee /etc/systemd/system/{unit} >/dev/null', source.read_bytes().replace(b'\r\n', b'\n'))
    for slug in people:
        for name in unit_names(slug)[2:]:  # the services of the person
            d = f'/etc/systemd/system/{name}.d'
            if slug in legacy:
                ssh(args, f'set -e; u={LEGACY_USER + slug}; id -u $u >/dev/null 2>&1 || u={accountctl.person_user(slug)};'
                          f' g=$(id -gn $u); sudo mkdir -p {d};'
                          f' printf "[Service]\\nUser=%s\\nGroup=%s\\n" "$u" "$g" | sudo tee {d}/10-person-user.conf >/dev/null')
            else:
                ssh(args, f'sudo rm -f {d}/10-person-user.conf')
    ssh(args, 'sudo systemctl daemon-reload')


def slugs(args):
    return [n.removesuffix('.env') for n in ssh(args, 'ls /etc/concierge-mail 2>/dev/null || true').split()
            if n.endswith('.env') and SLUG.match(n.removesuffix('.env'))]


def unit_names(slug):
    """The units of a person: timers (what may be enabled), then the oneshot services."""
    return [f'concierge-mail-sync@{slug}.timer', f'concierge-mail-backup@{slug}.timer',
            f'concierge-mail-sync@{slug}.service', f'concierge-mail-backup@{slug}.service']


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
    user = accountctl.person_user(slug)
    accountctl.ensure_person(args, slug, adopt=[LEGACY_USER + slug])
    ssh(args, f'set -e; sudo install -d -m 0700 -o {user} -g {user} /srv/data/mail/{slug}; sudo tee /etc/concierge-mail/{slug}.env >/dev/null;'
              f' sudo chown root:{user} /etc/concierge-mail/{slug}.env; sudo chmod 0640 /etc/concierge-mail/{slug}.env', body.encode())
    print(json.dumps({'slug': slug, 'user': user, 'data': f'/srv/data/mail/{slug}', 'env': env | ({'BACKUP_PUBLIC_KEY': 'set'} if args.backup_public else {})}))


def connect(args):
    """The shared account of the person (module provider-accounts) with the capabilities this module needs."""
    slug = slug_ok(args.slug)
    env = ssh(args, f'sudo cat /etc/concierge-mail/{slug}.env')
    args.account = re.search(r'^ACCOUNT=(.+)$', env, re.M).group(1).strip()
    args.env = re.search(r'^AIWB_ENV=(.+)$', env, re.M).group(1).strip()
    args.provider = 'google'
    record = accountctl.connect(args, want=CAPABILITIES)
    granted = record.get('providers', {}).get('google', {}).get('capabilities', [])
    if 'mail.read' not in granted:
        sys.exit('Gmail read access was not granted')
    if args.start:
        start(args)


def migrate(args):
    slug = slug_ok(args.slug)
    env = ssh(args, f'sudo cat /etc/concierge-mail/{slug}.env')
    args.env = re.search(r'^AIWB_ENV=(.+)$', env, re.M).group(1).strip()
    args.account = re.search(r'^ACCOUNT=(.+)$', env, re.M).group(1).strip()
    args.source, args.from_name, args.provider = f'/etc/credstore.encrypted/concierge-mail.{slug}.cred', 'gmail', 'google'
    accountctl.migrate(args)


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
    user = accountctl.person_user(slug)  # a person a deploy could not move yet still runs as the older user
    if ssh(args, f'id -u {user} >/dev/null 2>&1 && echo yes || echo no').strip() != 'yes':
        user = LEGACY_USER + slug
    cmd = ('sudo systemd-run --quiet --wait --pipe --collect'
           f' -p User={user} -p Group={user} -p EnvironmentFile=/etc/concierge-mail/{slug}.env'
           f' -p LoadCredentialEncrypted=account:/etc/credstore.encrypted/account.{slug}.google.cred'
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
        worker = ssh(args, f'systemctl is-active home-relay@{slug}.service', check=False).strip()
        size = ssh(args, f'sudo du -sh /srv/data/mail/{slug} 2>/dev/null | cut -f1', check=False).strip()
        cred = ssh(args, f'sudo test -f /etc/credstore.encrypted/account.{slug}.google.cred && echo yes || echo no', check=False).strip()
        out.append({'slug': slug, 'credential': cred, 'size': size, 'timers': timers.split(), 'services': result.split(), 'worker': worker})
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
    accountctl.add_connect_args(c)
    c.add_argument('--start', action='store_true', help='enable the timers right after the credential is saved')
    sub.add_parser('migrate').add_argument('slug')
    for name in ('start', 'stop'):
        sub.add_parser(name).add_argument('slug')
    s = sub.add_parser('status')
    s.add_argument('slug', nargs='?')
    r = sub.add_parser('run')
    r.add_argument('slug')
    r.add_argument('rest', nargs=argparse.REMAINDER)
    args = p.parse_args()
    {'deploy': deploy, 'add': add, 'connect': connect, 'migrate': migrate, 'start': start, 'stop': lambda a: start(a, False),
     'status': status, 'run': run}[args.cmd](args)


if __name__ == '__main__':
    main()
