# SPDX-License-Identifier: AGPL-3.0-or-later
"""Freedom setup wizard without AI (SPEC Freedom 0.5.2 section 4.1, stage F2): plain checks and steps up to the moment
the user's own subscription works; from there the user's own agent continues (Google Cloud playbook, GOOGLE-CLOUD.md).

Every step prints what it found, what it did and the next action; a failed step stops and says how to get help:
`report` writes a log without secrets, e-mails, names, hosts or paths for the user's own claude.ai / chatgpt.com chat
or the forum (SPEC 4.3). The state of the steps is kept next to install.json (setup-state.json), so a new run goes on.

  python install/freedom_setup.py status                       steps done and the next one
  python install/freedom_setup.py preflight                    this computer: Python, git, Node/npm, paramiko, keyring, Drive
  python install/freedom_setup.py init [--site-url URL --owner-email E --owner-name N --owner-env ENV --drive-root DIR
                                        --ssh-host H --ssh-port P --ssh-user U --ssh-key FILE --site-home DIR
                                        --web-root DIR --db-name N --db-user U --member env:email:name ...] [--force]
                                                              write install.json (asks for what is missing when interactive)
  python install/freedom_setup.py subs [--install claude|codex|all] [--login claude|codex]
                                                              own subscriptions: official installers, sign-in status,
                                                              the official sign-in; the last action is always the user's
  python install/freedom_setup.py site-probe                   variant A: PHP, MariaDB, cron, Node, panel of the hosting
  python install/freedom_setup.py site-deploy [--diff|--apply] [--parts agent,auth,web,home,corporate,config,secrets,
                                        schema,cli] [--build] [--transport ssh|local --local-root DIR] [--no-bak]
                                                              plan (default) / read-only diff / apply with .bak copies
  python install/freedom_setup.py google [--import-web-client FILE] [--desktop-client FILE]
                                                              check the Google Cloud results (the steps: GOOGLE-CLOUD.md)
  python install/freedom_setup.py bridge [--no-start]           Bridge on this PC: config.json from install.json, own release
                                                              key, build from this source, logon task (bridge/install.ps1)
  python install/freedom_setup.py desktop [--installer FILE]     CO Desktop on this PC: checks the installer against the signed
                                                              release manifest next to it, runs it, finds what it needs
  python install/freedom_setup.py desktop-update --installer FILE
                                                              a newer CO Desktop of a release for all your computers: checks
                                                              its signature, gives it to the site and Bridge (update channel)
  python install/freedom_setup.py pc-access [--apply] [--check] [--off]
                                                              pc_* of this PC for the owner, household sandbox for
                                                              the other members (SPEC 3.3, step 6a)
  python install/freedom_setup.py verify [--no-tools]          site, sign-in redirect, db.php, then aiwb-smoke / aiwb-e2e
  python install/freedom_setup.py report [--out FILE]          log without secrets for help (also to the clipboard)

Secrets stay in the OS keyring (SPEC principle 2): aiwb/co-mysql (account = database user), aiwb/google-oauth-web
(default: JSON of the web OAuth client), aiwb/co-push (default: key of this PC for db.php), aiwb/site-ssh (account = ssh
user: passphrase of the key, if any), aiwb/site-claude-token (default: `claude setup-token` for the site agent).
"""
import argparse
import base64
import datetime as dt
import getpass
import hashlib
import json
import os
import platform
import re
import secrets
import shutil
import subprocess
import tempfile
import sys
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import aiwb_install  # noqa: E402

REPO = Path(__file__).resolve().parents[1]
MIN_PHP = (8, 1)        # the CLI PHP of the reference install is 8.1; the code needs match/mixed/readonly (8.1)
MIN_MARIADB = (10, 5)   # schema.sql uses ADD COLUMN / ADD INDEX IF NOT EXISTS (MariaDB only, not MySQL)
MIN_NODE = 20
STEPS = ['preflight', 'init', 'subs', 'site-probe', 'site-deploy', 'google', 'bridge', 'desktop', 'pc-access', 'verify']
CRON_LINES = ['* * * * * php {home}/co-agent/cron/drive-sync.php >/dev/null 2>&1',
              '*/5 * * * * php {home}/co-agent/cron/watch-cron.php >/dev/null 2>&1']


# ---------------------------------------------------------------- state, log, report

def state_path() -> Path:
    return aiwb_install.path().parent / 'setup-state.json'


def log_path() -> Path:
    return aiwb_install.path().parent / 'setup.log'


def load_state() -> dict:
    try:
        return json.loads(state_path().read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {'steps': {}}


def save_step(step: str, ok: bool, summary: str, data=None):
    st = load_state()
    st.setdefault('steps', {})[step] = {'ok': ok, 'utc': dt.datetime.now(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
                                        'summary': summary, **({'data': data} if data is not None else {})}
    state_path().parent.mkdir(parents=True, exist_ok=True)
    state_path().write_text(json.dumps(st, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def out(level: str, text: str):
    """One result line: ok / warn / FAIL / info / next. Also appended to setup.log."""
    line = f'{level:<5} {text}'
    print(line, flush=True)
    try:
        log_path().parent.mkdir(parents=True, exist_ok=True)
        with log_path().open('a', encoding='utf-8') as f:
            f.write(dt.datetime.now().strftime('%Y-%m-%d %H:%M:%S ') + line + '\n')
    except OSError:
        pass


def sanitize(text: str, extra=()) -> str:
    """What may leave the computer in a help request: no secrets, e-mails, names, hosts, paths or account numbers."""
    words = [w for w in extra if w and len(w) >= 3]
    try:
        d = aiwb_install.load()
        site = d.get('site') or {}
        words += [site.get('url', '').split('//')[-1], (site.get('ssh') or {}).get('host', ''),
                  (site.get('ssh') or {}).get('user', ''), site.get('home', ''), site.get('webRoot', ''),
                  (d.get('google') or {}).get('projectNumber', ''), (d.get('google') or {}).get('webClientId', ''),
                  (site.get('db') or {}).get('name', ''), (site.get('db') or {}).get('user', '')]
        for m in d.get('members') or []:
            words += [m.get('email', ''), m.get('name', ''), m.get('env', '')]
    except (FileNotFoundError, ValueError):
        pass
    words += [getpass.getuser(), platform.node()]
    for w in sorted({w for w in words if w and len(w) >= 3}, key=len, reverse=True):
        text = re.sub(re.escape(w), '<redacted>', text, flags=re.I)
    text = re.sub(r'[\w.+-]+@[\w-]+(\.[\w-]+)+', '<email>', text)
    text = re.sub(r'\b\d{1,3}(\.\d{1,3}){3}\b', '<ip>', text)
    text = re.sub(r'(?i)(password|passwd|secret|token|key|authorization)(["\']?\s*[:=]\s*)\S+', r'\1\2<secret>', text)
    text = re.sub(r'\b(sk|ghp|gho|xox[abp]|ya29|AIza)[-_.A-Za-z0-9]{10,}', '<secret>', text)
    text = re.sub(r'\b[A-Za-z0-9+/_-]{32,}={0,2}', '<secret>', text)
    text = re.sub(r'(?i)[a-z]:\\users\\[^\\\s"]+', r'C:\\Users\\<user>', text)
    text = re.sub(r'/(home|Users)/[^/\s"]+', r'/\1/<user>', text)
    text = re.sub(r'\bu\d{6,}\b', '<account>', text)
    return text


# ---------------------------------------------------------------- helpers

def sh(args, timeout=120, cwd=None, env=None):
    """Run a local program: (rc, stdout+stderr); rc 127 when it is not found."""
    exe = shutil.which(args[0])
    if not exe:
        return 127, f'{args[0]}: not found'
    try:
        p = subprocess.run([exe] + list(args[1:]), capture_output=True, text=True, timeout=timeout, cwd=cwd, env=env,
                           encoding='utf-8', errors='replace')
        return p.returncode, (p.stdout + p.stderr).strip()
    except subprocess.TimeoutExpired:
        return 124, f'{args[0]}: timeout {timeout} s'


def version_tuple(text: str):
    m = re.search(r'(\d+)\.(\d+)(?:\.(\d+))?', text or '')
    return tuple(int(x or 0) for x in m.groups()) if m else None


def keyring_get(service: str, account: str = 'default'):
    try:
        import keyring
        return keyring.get_password(service, account)
    except Exception:
        return None


def keyring_set(service: str, account: str, value: str):
    import keyring
    keyring.set_password(service, account, value)


def install_get(key, default=None):
    return aiwb_install.get(key, default)


# ---------------------------------------------------------------- preflight

def cmd_preflight(a) -> int:
    bad = 0
    out('info', f'{platform.system()} {platform.release()} {platform.machine()}, Python {platform.python_version()}')
    if sys.version_info < (3, 10):
        out('FAIL', 'Python 3.10+ is needed'); bad += 1
    rc, v = sh(['git', '--version'])
    out('ok' if rc == 0 else 'FAIL', f'git: {v}'); bad += rc != 0
    rc, v = sh(['node', '--version'])
    nv = version_tuple(v)
    if rc == 0 and nv and nv[0] >= MIN_NODE:
        out('ok', f'Node {v}')
    else:
        out('warn', f'Node {MIN_NODE}+ is needed to build CO Web and to install codex (found: {v}); '
                    'Windows: winget install OpenJS.NodeJS.LTS, macOS: brew install node')
    for mod, why in (('paramiko', 'SSH to the hosting (variant A)'), ('keyring', 'secrets in the OS keyring'),
                     ('cryptography', 'signatures of releases and modules')):
        try:
            __import__(mod)
            out('ok', f'python module {mod}')
        except ImportError:
            out('FAIL', f'python module {mod} is missing ({why}): python -m pip install {mod}'); bad += 1
    try:  # a real round trip: a fail backend would just return None
        import keyring
        probe = secrets.token_hex(8)
        keyring.set_password('aiwb/setup-probe', 'default', probe)
        good = keyring.get_password('aiwb/setup-probe', 'default') == probe
        keyring.delete_password('aiwb/setup-probe', 'default')
        out('ok' if good else 'FAIL', f'keyring backend: {type(keyring.get_keyring()).__name__}'
                                       + ('' if good else ' does not keep secrets'))
        bad += not good
    except Exception as e:
        out('FAIL', f'keyring does not work: {e}'); bad += 1
    root = Path(install_get('drive.root') or ('G:\\My Drive' if os.name == 'nt' else str(Path.home() / 'Google Drive')))
    if root.is_dir():
        out('ok', 'Google Drive for desktop: the drive root is mounted')
    else:
        out('warn', 'Google Drive for desktop is not mounted yet (install it and sign in with your Google account; '
                    'the root goes to install.json drive.root)')
    if os.name == 'nt':
        rc, _ = sh(['winget', '--version'])
        out('ok' if rc == 0 else 'warn', 'winget' if rc == 0 else 'winget is missing (App Installer from Microsoft Store)')
    save_step('preflight', bad == 0, f'{bad} problem(s)')
    return 1 if bad else 0


# ---------------------------------------------------------------- init (install.json)

def _merge(old, new):
    """new over old; empty values of new ('' / None) keep the old ones; lists are taken from new."""
    if not isinstance(old, dict) or not isinstance(new, dict):
        return new if new not in ('', None) else old
    res = dict(old)
    for k, v in new.items():
        res[k] = _merge(old.get(k), v) if k in old else v
    return res


def _ask(label: str, cur, interactive: bool):
    if cur or not interactive:
        return cur
    v = (_input(f'{label}: ') or '').strip()
    return v or None


def _input(prompt: str):
    """input() that gives None at the end of input (on Windows NUL looks like a terminal)."""
    try:
        return input(prompt)
    except EOFError:
        print()
        return None


def cmd_init(a) -> int:
    p = aiwb_install.path()
    if p.is_file() and not a.force:
        errs = aiwb_install.check(aiwb_install.load(fresh=True))
        for e in errs:
            out('FAIL', f'install.json: {e}')
        out('ok' if not errs else 'next', f'install.json exists: {p}' + ('' if not errs else ' - fix it or run init --force'))
        save_step('init', not errs, 'existing install.json' + (f', {len(errs)} error(s)' if errs else ''))
        return 1 if errs else 0
    ia = sys.stdin.isatty() and not a.no_input
    variant = (getattr(a, 'variant', None) or 'A').upper()
    url = _ask('Site address (https://co.example.org)', a.site_url, ia)
    email = _ask('Your Google e-mail (owner)', a.owner_email, ia)
    name = _ask('Your first name', a.owner_name, ia)
    env = a.owner_env or (re.sub(r'[^a-z0-9-]', '', (name or '').lower()) or 'owner')
    hosting = ia and variant == 'A'  # B/C: no SSH, folders or database host to ask (freedom_docker.py sets them)
    home = _ask('Home folder on the hosting (/home/<user>)', a.site_home, hosting)
    web = _ask('Web root of the site on the hosting (…/public_html)', a.web_root, hosting)
    d = {
        'schema': 1,
        'variant': variant,
        'site': {'url': (url or '').rstrip('/'),
                 'ssh': {'host': _ask('SSH host of the hosting', a.ssh_host, hosting), 'port': int(a.ssh_port or 22),
                         'user': _ask('SSH user', a.ssh_user, hosting), 'keyFile': _ask('SSH private key file', a.ssh_key, hosting)},
                 'home': (home or '').rstrip('/'), 'webRoot': (web or '').rstrip('/'),
                 'db': {'host': a.db_host or 'localhost', 'name': _ask('Database name', a.db_name, hosting) or ('co' if variant != 'A' else None),
                        'user': _ask('Database user', a.db_user, hosting) or ('co' if variant != 'A' else None)}},
        'google': {'webClientId': a.web_client_id or '', 'projectNumber': a.project_number or ''},
        'members': [{'env': env, 'email': email or '', 'name': name or '', 'role': 'owner'}],
        'drive': {'root': a.drive_root or ('G:\\My Drive' if os.name == 'nt' else str(Path.home() / 'Google Drive')),
                  'workbench': 'AI-Workbench'},
        'connectors': {'corporate': False},
        'hostId': 'home-main',
    }
    for m in a.member or []:
        parts = m.split(':')
        if len(parts) != 3:
            out('FAIL', f'--member {m!r}: env:email:name'); return 1
        d['members'].append({'env': parts[0], 'email': parts[1], 'name': parts[2]})
    if p.is_file():
        # --force: the answers win, everything else of the old file (Google ids, canvas, colors, flags) stays
        try:
            old = json.loads(p.read_text(encoding='utf-8-sig'))
        except ValueError:
            old = {}
        oldm = {m.get('env'): m for m in old.get('members') or []}
        d['members'] = [{**oldm.get(m['env'], {}), **{k: v for k, v in m.items() if v}} for m in d['members']]
        d = _merge(old, d)
    errs = aiwb_install.check(d)
    for e in errs:
        out('FAIL', f'install.json: {e}')
    if errs:
        save_step('init', False, f'{len(errs)} error(s)')
        return 1
    p.parent.mkdir(parents=True, exist_ok=True)
    if p.is_file():
        shutil.copy2(p, p.with_suffix('.json.bak'))
    p.write_text(json.dumps(d, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    aiwb_install.load(fresh=True)
    out('ok', f'install.json written: {p}')
    # B/C: freedom_docker.py up generates the database password into the stack's secrets
    if variant == 'A' and (d['site'].get('db') or {}).get('user') and not keyring_get('aiwb/co-mysql', d['site']['db']['user']):
        out('next', 'database password into the keyring: python "<tools>/secret-prompt/secret_prompt.py" co-mysql '
                    f'--account {d["site"]["db"]["user"]} (or: python -c "import keyring; keyring.set_password('
                    f'\'aiwb/co-mysql\', \'{d["site"]["db"]["user"]}\', input())")')
    save_step('init', True, 'install.json written')
    return 0


# ---------------------------------------------------------------- subscriptions (SPEC 4.1 step 3)

CLI_INSTALL = {
    'claude': {'nt': ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                      'irm https://claude.ai/install.ps1 | iex'],
               'posix': ['bash', '-c', 'curl -fsSL https://claude.ai/install.sh | bash']},
    'codex': {'nt': ['npm', 'install', '-g', '@openai/codex'], 'posix': ['npm', 'install', '-g', '@openai/codex']},
}
CLI_LOGIN = {'claude': ['claude', 'auth', 'login', '--claudeai'], 'codex': ['codex', 'login', '--device-auth']}


def cli_status(cli: str) -> dict:
    rc, v = sh([cli, '--version'], timeout=60)
    if rc == 127:
        return {'installed': False}
    st = {'installed': rc == 0, 'version': v.splitlines()[0] if v else ''}
    if cli == 'claude':
        rc, j = sh(['claude', 'auth', 'status', '--json'], timeout=60)
        try:
            data = json.loads(j[j.index('{'):]) if '{' in j else {}
        except ValueError:
            data = {}
        st['signedIn'] = bool(data.get('loggedIn') or data.get('email') or data.get('account'))
        st['plan'] = data.get('subscriptionType') or data.get('plan') or ''
    else:
        rc, t = sh(['codex', 'login', 'status'], timeout=60)
        st['signedIn'] = rc == 0 and 'not logged in' not in t.lower()
        st['plan'] = 'ChatGPT' if 'chatgpt' in t.lower() else ('API key' if 'api key' in t.lower() else '')
    return st


def cmd_subs(a) -> int:
    which = ['claude', 'codex'] if a.install in (None, 'all') else [a.install]
    if a.install:
        for cli in which:
            if cli_status(cli).get('installed'):
                out('ok', f'{cli} is already installed')
                continue
            cmd = CLI_INSTALL[cli]['nt' if os.name == 'nt' else 'posix']
            out('info', f'installing {cli} with the official installer: {" ".join(cmd)}')
            rc, o = sh(cmd, timeout=900)
            out('ok' if rc == 0 else 'FAIL', f'{cli} install: rc {rc} {o.splitlines()[-1] if o else ""}')
    if a.login:
        cmd = CLI_LOGIN[a.login]
        out('info', f'official sign-in: {" ".join(cmd)} - finish it yourself in your browser '
                    '(Claude: the captcha and "Authorize"; Codex: confirm the code in your ChatGPT)')
        try:
            subprocess.run([shutil.which(cmd[0]) or cmd[0]] + cmd[1:], timeout=900)
        except (OSError, subprocess.TimeoutExpired) as e:
            out('FAIL', f'{a.login} sign-in: {e}')
    any_ready = False
    rows = {}
    for cli in ('claude', 'codex'):
        st = cli_status(cli)
        rows[cli] = st
        if not st.get('installed'):
            out('warn', f'{cli}: not installed (subs --install {cli})')
        elif not st.get('signedIn'):
            out('next', f'{cli} {st.get("version", "")}: not signed in (subs --login {cli}, or CO Desktop -> Subscriptions)')
        else:
            any_ready = True
            out('ok', f'{cli} {st.get("version", "")}: signed in{(" (" + st["plan"] + ")") if st.get("plan") else ""}')
    if any_ready:
        out('next', 'your own agent can go on from here: open claude (or codex) in this folder and ask it to follow '
                    'install/GOOGLE-CLOUD.md and the next steps of this wizard')
    else:
        out('next', 'at least one own subscription (Claude Pro/Max or ChatGPT Plus/Pro) must be signed in; '
                    'stuck before that: python install/freedom_setup.py report -> paste it into your claude.ai / chatgpt.com chat')
    save_step('subs', any_ready, ', '.join(f'{k}: {"ready" if v.get("signedIn") else "no"}' for k, v in rows.items()))
    return 0 if any_ready else 1


# ---------------------------------------------------------------- transports for the hosting

class SshTarget:
    """The hosting over SSH (paramiko: works where the stock OpenSSH client is blocked)."""

    def __init__(self, host_key=None):
        try:
            self.c = aiwb_install.ssh_connect(accept_new=host_key or False)
        except ValueError as e:
            raise SystemExit(f'{e} (init)')
        except aiwb_install.HostKeyUnknown as e:
            # trust on first use, but only after the user compared the fingerprint with the panel of the hosting
            out('info', f'first connection to {e.host}: host key {e.fingerprint}')
            hint = f'compare it with the SSH page of your hosting panel, then repeat with --host-key {e.fingerprint}'
            answer = _input('Is this the key your hosting panel shows? (yes/no): ') if sys.stdin.isatty() else None
            if answer is None:
                raise SystemExit(hint)
            if answer.strip().lower() != 'yes':
                raise SystemExit('host key not accepted')
            self.c = aiwb_install.ssh_connect(accept_new=e.fingerprint)
            out('ok', f'host key saved: {aiwb_install.known_hosts_path()}')
        self.sftp = self.c.open_sftp()
        self.local = False

    def run(self, cmd: str, timeout=180, stdin: bytes = None):
        i, o, e = self.c.exec_command(cmd, timeout=timeout)
        if stdin is not None:
            i.write(stdin)
            i.channel.shutdown_write()
        text = o.read().decode('utf-8', 'replace') + e.read().decode('utf-8', 'replace')
        return o.channel.recv_exit_status(), text.strip()

    def shas(self, paths):
        res = {}
        for i in range(0, len(paths), 200):
            chunk = paths[i:i + 200]
            _, t = self.run('sha256sum -- ' + ' '.join(_q(p) for p in chunk) + ' 2>/dev/null; true')
            for line in t.splitlines():
                m = re.match(r'^([0-9a-f]{64})\s+\*?(.+)$', line)
                if m:
                    res[m.group(2)] = m.group(1)
        return res

    def exists(self, path):
        try:
            self.sftp.stat(path)
            return True
        except OSError:
            return False

    def read(self, path) -> bytes:
        with self.sftp.open(path, 'rb') as f:
            return f.read()

    def write(self, path, data: bytes, mode=None):
        """A secret (mode given) never exists with wider rights: its folder is made 0700 and the empty file is chmod-ed
        before the data goes in."""
        folder = path.rsplit('/', 1)[0]
        self.run(('mkdir -p -m 700 ' if mode else 'mkdir -p ') + _q(folder) + (f' && chmod 700 {_q(folder)}' if mode else ''))
        with self.sftp.open(path, 'wb') as f:
            if mode:
                f.chmod(mode)
            f.write(data)

    def replace(self, path, data: bytes, php: bool, bak: str, mode=None) -> str:
        """Write path.new, `php -l` for PHP, keep path.bak-<stamp>, then mv - never a broken live file; the live file
        keeps its permissions."""
        self.write(path + '.new', data, mode)
        lint = f'php -l {_q(path + ".new")} >/dev/null && ' if php else ''
        keep = f'{{ [ ! -e {_q(path)} ] || cp -p {_q(path)} {_q(path + ".bak-" + bak)}; }} && ' if bak else ''
        perm = f'{{ [ ! -e {_q(path)} ] || chmod --reference={_q(path)} {_q(path + ".new")}; }} && '
        rc, t = self.run(f'{lint}{keep}{perm}mv -f {_q(path + ".new")} {_q(path)}')
        if rc != 0:
            self.run('rm -f ' + _q(path + '.new'))
            raise RuntimeError(f'{path}: {t}')
        return 'replaced'

    def close(self):
        self.sftp.close()
        self.c.close()


class LocalTarget:
    """A folder that stands for the hosting (tests, and a dry run of a deploy): remote /x/y -> ROOT/x/y."""

    def __init__(self, root):
        self.root = Path(root)
        self.local = True

    def _p(self, path) -> Path:
        return self.root / path.lstrip('/')

    def run(self, cmd, timeout=180, stdin=None):
        return 127, 'commands do not run on a local target'

    def shas(self, paths):
        return {p: hashlib.sha256(self._p(p).read_bytes()).hexdigest() for p in paths if self._p(p).is_file()}

    def exists(self, path):
        return self._p(path).exists()

    def read(self, path):
        return self._p(path).read_bytes()

    def write(self, path, data, mode=None):
        f = self._p(path)
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_bytes(data)
        if mode:
            os.chmod(f, mode)

    def replace(self, path, data, php, bak, mode=None):
        f = self._p(path)
        if bak and f.exists():
            shutil.copy2(f, f.with_name(f.name + '.bak-' + bak))
        self.write(path + '.new', data, mode)
        os.replace(self._p(path + '.new'), f)
        return 'replaced'

    def close(self):
        pass


def _q(s: str) -> str:
    return "'" + s.replace("'", "'\\''") + "'"


def target(a):
    if getattr(a, 'transport', 'ssh') == 'local':
        if not a.local_root:
            raise SystemExit('--transport local needs --local-root DIR')
        return LocalTarget(a.local_root)
    return SshTarget(getattr(a, 'host_key', None))


# ---------------------------------------------------------------- site-probe (variant A, SPEC 4)

def detect_panel(t) -> str:
    rc, txt = t.run('ls -d /usr/local/cpanel /usr/local/psa /usr/local/directadmin /usr/local/hostinger '
                    '~/domains ~/public_html /var/www/vhosts 2>/dev/null; hostname -f 2>/dev/null; true')
    if '/usr/local/cpanel' in txt:
        return 'cPanel'
    if '/usr/local/psa' in txt or '/var/www/vhosts' in txt:
        return 'Plesk'
    if '/usr/local/directadmin' in txt:
        return 'DirectAdmin'
    if re.search(r'/domains\b', txt) and re.search(r'hostinger|hstgr', txt, re.I) or '/usr/local/hostinger' in txt:
        return 'hPanel'
    if re.search(r'/domains\b', txt):
        return 'domains/ layout (hPanel or DirectAdmin-like)'
    return 'unknown'


def find_node(t):
    rc, txt = t.run('for n in "$(command -v node)" ~/co-agent/bin/node /opt/alt/alt-nodejs*/root/usr/bin/node '
                    '~/.nvm/versions/node/*/bin/node /usr/local/bin/node; do [ -x "$n" ] && echo "$n $("$n" --version)"; '
                    'done 2>/dev/null; true')
    best = None
    for line in txt.splitlines():
        parts = line.split()
        if len(parts) == 2 and version_tuple(parts[1]):
            v = version_tuple(parts[1])
            if not best or v > best[1]:
                best = (parts[0], v)
    return best


def docker_variant() -> bool:
    """Freedom F3b: variants B (VPS/NAS) and C (only a computer) run the same server in Docker (install/freedom_docker.py)."""
    return str(install_get('variant') or 'A').upper() in ('B', 'C')


def cmd_site_probe(a) -> int:
    if docker_variant():
        import freedom_docker
        return freedom_docker.site_probe(a)
    t = target(a)
    bad, info = 0, {}
    try:
        home, web = install_get('site.home', ''), install_get('site.webRoot', '')
        rc, v = t.run('php -r "echo PHP_VERSION;"')
        pv = version_tuple(v) if rc == 0 else None
        info['php'] = v if rc == 0 else None
        if pv and pv[:2] >= MIN_PHP:
            out('ok', f'PHP {v} (CLI)')
        else:
            out('FAIL', f'PHP {".".join(map(str, MIN_PHP))}+ is needed in the shell of the hosting (found: {v or "none"})'); bad += 1
        rc, mods = t.run('php -m')
        missing = [m for m in ('mysqli', 'openssl', 'curl', 'mbstring', 'json', 'zlib') if m not in mods.lower().split()]
        out('ok' if not missing else 'FAIL', 'PHP modules' + (f': missing {", ".join(missing)}' if missing else ''))
        bad += bool(missing)
        rc, v = t.run('mysql --version || mariadb --version')
        mv = version_tuple(v.split('from')[-1]) if 'mariadb' in v.lower() else None
        info['db'] = v
        if mv and mv[:2] >= MIN_MARIADB:
            out('ok', f'MariaDB client {".".join(map(str, mv))}')
        elif rc == 0:
            out('FAIL', f'MariaDB {".".join(map(str, MIN_MARIADB))}+ is needed (schema.sql uses IF NOT EXISTS of MariaDB); '
                        f'found: {v}'); bad += 1
        else:
            out('FAIL', 'no mysql/mariadb client in the shell (needed for the schema and the nightly backup)'); bad += 1
        user = install_get('site.db.user')
        pw = keyring_get('aiwb/co-mysql', user) if user else None
        if user and pw:
            tmp = f'{home}/co-agent/secrets/.probe-{secrets.token_hex(4)}.cnf'
            t.write(tmp, my_cnf(user, pw, install_get('site.db.host', 'localhost')).encode(), 0o600)
            try:
                rc, v = t.run(f'mysql --defaults-extra-file={_q(tmp)} -N -e "SELECT VERSION()" '
                              f'{_q(install_get("site.db.name", ""))}')
            finally:
                t.run('rm -f ' + _q(tmp))
            out('ok' if rc == 0 else 'FAIL', f'database sign-in: {v.splitlines()[-1] if v else rc}')
            bad += rc != 0
        else:
            out('next', 'database: create a MariaDB database and user in the panel, put the names into install.json '
                        'site.db and the password into keyring aiwb/co-mysql (account = database user)')
        rc, v = t.run('command -v crontab && crontab -l 2>&1 | head -3; true')
        info['crontab'] = 'crontab' in v and 'not found' not in v
        out('ok' if info['crontab'] else 'info',
            'crontab available' if info['crontab'] else 'no crontab in the shell: the two cron lines go into the panel '
                                                        '(hPanel: Advanced -> Cron Jobs; cPanel: Cron Jobs)')
        node = find_node(t)
        info['node'] = node[0] if node else None
        if node and node[1][0] >= MIN_NODE:
            out('ok', f'Node {".".join(map(str, node[1]))} for the site agent: {node[0]}')
        else:
            out('warn', f'no Node {MIN_NODE}+ on the hosting: the site agent (AI on the server, SPEC 3.1) will not run; '
                        'the site still stores chats and the queue, jobs run on your PC')
        info['panel'] = detect_panel(t)
        out('info', f'panel: {info["panel"]}')
        for label, path in (('home', home), ('web root', web)):
            ok = bool(path) and t.exists(path)
            out('ok' if ok else 'FAIL', f'{label} folder exists' if ok else f'{label} folder not found: {path or "(empty)"}')
            bad += not ok
        if web and home and not (web.startswith(home + '/public_html') or web.startswith(home + '/domains/')):
            out('warn', 'the web root is neither ~/public_html nor ~/domains/<site>/public_html: set web_dir in '
                        'config.php (done by site-deploy) and env CO_AGENT_HOME for PHP')
        rc, v = t.run('ulimit -u; df -Pk ~ | tail -1')
        info['limits'] = v.replace('\n', '; ')
        out('info', f'process limit and disk: {info["limits"]}')
    finally:
        t.close()
    save_step('site-probe', bad == 0, f'{bad} problem(s)', info)
    return 1 if bad else 0


# ---------------------------------------------------------------- site-deploy

def _files(base: Path, skip=()):
    for f in sorted(base.rglob('*')):
        if f.is_file() and not any(part in ('__pycache__', 'node_modules', 'tests') for part in f.relative_to(base).parts) \
                and f.name not in ('desktop.ini',) and not f.name.endswith(('.bak', '.pyc')) \
                and not any(f.match(s) for s in skip):
            yield f


def _norm(b: bytes) -> bytes:
    return re.sub(rb'^[ \t]*(//|#) SPDX-License-Identifier:[^\n]*\n', b'', b.replace(b'\r\n', b'\n'), flags=re.M)


def layout():
    """repo file -> remote path, by part. One map for every install (SPEC 4, variant A)."""
    home, web = install_get('site.home', '').rstrip('/'), install_get('site.webRoot', '').rstrip('/')
    if not home or not web:
        raise SystemExit('install.json: site.home and site.webRoot are needed (init)')
    parts = {'agent': [], 'auth': [], 'web': [], 'home': [], 'corporate': []}
    for f in _files(REPO / 'co-agent' / 'web' / 'agent'):
        parts['agent'].append((f, f'{web}/agent/{f.relative_to(REPO / "co-agent/web/agent").as_posix()}'))
    for f in _files(REPO / 'co-web-auth' / 'auth'):
        parts['auth'].append((f, f'{web}/auth/{f.name}'))
    dist = REPO / 'co-web' / 'dist'
    if dist.is_dir():
        for f in _files(dist):
            parts['web'].append((f, f'{web}/{f.relative_to(dist).as_posix()}'))
    for sub in ('cron', 'db', 'executor'):
        for f in _files(REPO / 'co-agent' / sub, skip=('logs/*', '*.py')):
            parts['home'].append((f, f'{home}/co-agent/{sub}/{f.relative_to(REPO / "co-agent" / sub).as_posix()}'))
    man = REPO / 'modules' / 'corporate' / 'connector-manifest.json'
    if install_get('connectors.corporate') is True and man.is_file():
        parts['corporate'].append((man, f'{home}/co-agent/connectors/corporate/manifest.json'))
    return home, web, parts


def build_web() -> int:
    cw = REPO / 'co-web'
    env = dict(os.environ, AIWB_INSTALL=str(aiwb_install.path()))
    for cmd in (['npm', 'ci'] if not (cw / 'node_modules').exists() else None, ['npm', 'run', 'build']):
        if not cmd:
            continue
        out('info', f'co-web: {" ".join(cmd)}')
        rc, o = sh(cmd, timeout=1200, cwd=str(cw), env=env)
        if rc != 0:
            out('FAIL', f'co-web {" ".join(cmd)}: {o[-800:]}')
            return rc
    out('ok', 'co-web built with this install.json')
    return 0


def cmd_site_deploy(a) -> int:
    if docker_variant():
        import freedom_docker
        return freedom_docker.site_deploy(a)
    mode = 'apply' if a.apply else ('diff' if a.diff else 'plan')
    want = set((a.parts or 'agent,auth,web,home,corporate,config,secrets,schema').split(','))
    if a.build and 'web' in want and build_web() != 0:
        return 1
    home, web, parts = layout()
    if 'web' in want and not parts['web']:
        out('warn', 'co-web/dist is missing: add --build (needs Node and npm) or build co-web first')
    if mode == 'plan':
        for name in ('agent', 'auth', 'web', 'home', 'corporate'):
            if name in want:
                out('info', f'{name}: {len(parts[name])} file(s) -> {parts[name][0][1].rsplit("/", 1)[0] if parts[name] else "-"}')
        out('info', f'config: {home}/co-agent/config.php (from install.json), {home}/co-auth/config.php (once)')
        out('info', f'secrets: {home}/co-agent/secrets/db.json, my.cnf, push-notify-key (once, 0600)')
        out('info', f'schema: db/schema.sql (idempotent) and the members of install.json')
        for line in CRON_LINES:
            out('info', 'cron: ' + line.format(home=home))
        out('next', 'site-deploy --diff (read only), then site-deploy --apply')
        return 0
    t = target(a)
    stamp = dt.datetime.now().strftime('%Y%m%d-%H%M%S')
    bak = '' if a.no_bak else stamp
    counts = {'same': 0, 'header': 0, 'new': 0, 'changed': 0}
    try:
        items = [(f.read_bytes(), r, f.suffix == '.php') for name in ('agent', 'auth', 'web', 'home', 'corporate')
                 if name in want for f, r in parts[name]]
        if 'config' in want:
            items.append((aiwb_install.server_config().encode(), f'{home}/co-agent/config.php', True))
        remote = t.shas([r for _, r, _ in items])
        todo = []
        for data, r, php in items:
            h = hashlib.sha256(data).hexdigest()
            st = 'same' if remote.get(r) == h else ('changed' if r in remote else 'new')
            if st == 'changed' and mode == 'diff' and _norm(t.read(r)) == _norm(data):
                st = 'header'  # only the SPDX line or line ends differ
            if st == 'changed' and r == f'{home}/co-agent/config.php' and not a.rewrite_config:
                # a live config.php may hold keys install.json does not know (site_name, corporate_manifest, ...)
                out('info', 'config.php on the site differs from install.json: kept (--rewrite-config replaces it)')
                continue
            counts[st] += 1
            if st != 'same':
                todo.append((data, r, php, st))
                if (mode == 'diff' and st != 'header') or a.verbose:
                    out('info', f'{st:<7} {r}')
        out('ok', f'files: {counts["same"]} same, {counts["changed"]} changed, {counts["new"]} new'
                  + (f', {counts["header"]} differ only in the license line or line ends' if counts['header'] else ''))
        if mode == 'diff':
            for name, path in (('auth config', f'{home}/co-auth/config.php'), ('db.json', f'{home}/co-agent/secrets/db.json'),
                               ('push key', f'{home}/co-agent/secrets/push-notify-key')):
                out('ok' if t.exists(path) else 'info', f'{name}: {"present" if t.exists(path) else "missing (made by --apply)"}')
            save_step('site-deploy', True, f'diff: {counts}')
            return 0
        for data, r, php, st in todo:
            t.replace(r, data, php and not t.local, bak)
        if todo:
            out('ok', f'{len(todo)} file(s) written' + (f', old copies *.bak-{bak}' if bak else ''))
        bad = 0
        if 'config' in want:
            bad += deploy_auth_config(t, home)
        if 'secrets' in want:
            bad += deploy_secrets(t, home)
        if not t.local:
            # the site agent's start redirects into executor/logs before run.sh can make it (fresh installs)
            t.run(f'chmod +x {_q(home)}/co-agent/executor/run.sh; mkdir -p {_q(home)}/co-agent/executor/logs; true')
            node = find_node(t)
            if node:
                t.run(f'mkdir -p {_q(home)}/co-agent/bin && ln -sfn {_q(node[0])} {_q(home)}/co-agent/bin/node')
            if 'schema' in want:
                bad += deploy_schema(t, home)
            if 'cli' in want:
                bad += deploy_cli(t, home, node)
        rc, cron = t.run('crontab -l 2>/dev/null') if not t.local else (1, '')
        for line in CRON_LINES:
            l = line.format(home=home)
            script = l.split()[6]
            out('ok' if script in cron else 'next', ('cron set: ' if script in cron else 'add to the cron of the panel: ') + l)
        save_step('site-deploy', bad == 0, f'apply: {counts}, {bad} problem(s)')
        return 1 if bad else 0
    finally:
        t.close()


def deploy_auth_config(t, home) -> int:
    path = f'{home}/co-auth/config.php'
    if t.exists(path):
        out('ok', 'auth config present (kept)')
        return 0
    raw = keyring_get('aiwb/google-oauth-web')
    try:
        client = json.loads(raw) if raw else {}
    except ValueError:
        client = {}
    owner = aiwb_install.owner().get('email', '')
    if not client.get('client_id') or not client.get('client_secret'):
        out('next', 'auth config: the web OAuth client is not in the keyring yet (google --import-web-client FILE, '
                    'GOOGLE-CLOUD.md); run site-deploy --apply --parts config again after that')
        return 0
    php = ("<?php\n// ~/co-auth/config.php - written by install/freedom_setup.py; secrets stay on the server.\nreturn [\n"
           f"    'origin' => {aiwb_install._php(install_get('site.url'))},\n"
           f"    'client_id' => {aiwb_install._php(client['client_id'])},\n"
           f"    'client_secret' => {aiwb_install._php(client['client_secret'])},\n"
           f"    'cookie_key' => {aiwb_install._php(base64.b64encode(secrets.token_bytes(32)).decode())},\n"
           "    'scopes' => 'openid email https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/pubsub',\n"
           f"    'allowlist' => [{aiwb_install._php(owner)}],\n];\n")
    t.write(path, php.encode(), 0o600)
    out('ok', 'auth config written (0600)')
    return 0


def deploy_secrets(t, home) -> int:
    bad = 0
    sec = f'{home}/co-agent/secrets'
    user = install_get('site.db.user')
    pw = keyring_get('aiwb/co-mysql', user) if user else None
    host, name = install_get('site.db.host', 'localhost'), install_get('site.db.name', '')
    for fname, make in (('db.json', lambda: json.dumps({'host': host, 'db': name, 'user': user, 'password': pw})),
                        ('my.cnf', lambda: my_cnf(user, pw, host))):
        if t.exists(f'{sec}/{fname}'):
            out('ok', f'{fname} present (kept)')
        elif user and pw:
            t.write(f'{sec}/{fname}', make().encode(), 0o600)
            out('ok', f'{fname} written (0600)')
        else:
            out('FAIL', f'{fname}: no database user in install.json site.db or no password in keyring aiwb/co-mysql')
            bad += 1
    key = keyring_get('aiwb/co-push')
    if t.exists(f'{sec}/push-notify-key'):
        remote = t.read(f'{sec}/push-notify-key').decode().strip()
        if not key:
            keyring_set('aiwb/co-push', 'default', remote)
            out('ok', 'push key of the site copied into keyring aiwb/co-push')
        elif key != remote:
            out('warn', 'keyring aiwb/co-push differs from the key of the site: Bridge and tools of this PC will get 403')
        else:
            out('ok', 'push key: site and keyring agree')
    else:
        key = key or secrets.token_urlsafe(32)
        t.write(f'{sec}/push-notify-key', (key + '\n').encode(), 0o600)
        keyring_set('aiwb/co-push', 'default', key)
        out('ok', 'push key made: on the site (0600) and in keyring aiwb/co-push')
    tok = keyring_get('aiwb/site-claude-token')
    if tok and not t.exists(f'{sec}/claude_oauth_token'):
        t.write(f'{sec}/claude_oauth_token', (tok.strip() + '\n').encode(), 0o600)
        out('ok', 'token of the site agent (claude setup-token) written')
    elif not t.exists(f'{sec}/claude_oauth_token'):
        out('info', 'site agent: no Claude token yet (on this PC: claude setup-token, then save it with secret-prompt as '
                    'aiwb/site-claude-token and run --parts secrets again); without it AI runs on the PC only')
    return bad


def my_cnf(user: str, pw: str, host: str) -> str:
    """Option file of mysql and mysqldump (db-backup.php): [client] only has options both know; values quoted, so a
    '#' or a trailing space in the password survives."""
    esc = lambda v: '"' + str(v).replace('\\', '\\\\').replace('"', '\\"') + '"'
    return f'[client]\nuser={esc(user)}\npassword={esc(pw)}\nhost={esc(host)}\n'


def deploy_schema(t, home) -> int:
    name = install_get('site.db.name', '')
    if not name:
        out('FAIL', 'schema: install.json site.db.name is empty')
        return 1
    rc, o = t.run(f'mysql --defaults-extra-file={_q(home)}/co-agent/secrets/my.cnf {_q(name)} '
                  f'< {_q(home)}/co-agent/db/schema.sql', timeout=600)
    out('ok' if rc == 0 else 'FAIL', 'schema applied' if rc == 0 else f'schema: {o[-600:]}')
    if rc != 0:
        return 1
    # only members the database does not have yet: `set` would reset color and site agent of the existing ones
    rc, o = t.run(f'php {_q(home)}/co-agent/db/members.php list')
    try:
        rows = json.loads(o[o.index('['):] if '[' in o else o) if rc == 0 else None
    except ValueError:
        rows = None
    if rows is None:
        out('FAIL', f'members: list failed: {o[-300:]}')
        return 1
    have = {m.get('env') for m in (rows.values() if isinstance(rows, dict) else rows) if isinstance(m, dict)}
    bad = 0
    for cmd in aiwb_install.members_commands():
        env = cmd.split()[3].strip("'")
        if env in have:
            out('ok', f'member {env}: already in the database (kept)')
            continue
        rc, o = t.run(cmd.replace('~/', home + '/'))
        bad += rc != 0
        out('ok' if rc == 0 else 'FAIL', f'member {env}: {"added" if rc == 0 else o[-300:]}')
    return bad


def deploy_cli(t, home, node) -> int:
    if not node:
        out('warn', 'no Node on the hosting: CLIs of the site agent not installed')
        return 0
    nd = node[0].rsplit('/', 1)[0]
    rc, o = t.run(f'mkdir -p {_q(home)}/co-agent/cli && cd {_q(home)}/co-agent/cli && [ -f package.json ] || echo {{}} > package.json; '
                  f'PATH={_q(nd)}:$PATH UV_THREADPOOL_SIZE=1 npm install --no-audit --no-fund @anthropic-ai/claude-code @openai/codex',
                  timeout=900)
    out('ok' if rc == 0 else 'FAIL', 'CLIs of the site agent: ' + (o.splitlines()[-1] if o else str(rc)))
    return rc != 0


# ---------------------------------------------------------------- google (SPEC 4.1 step 5)

def cmd_google(a) -> int:
    bad = 0
    if a.import_web_client:
        j = json.loads(Path(a.import_web_client).read_text(encoding='utf-8'))
        w = j.get('web') or {}
        if not w.get('client_id') or not w.get('client_secret'):
            out('FAIL', 'not a web OAuth client file (no "web" section)'); return 1
        keyring_set('aiwb/google-oauth-web', 'default', json.dumps({'client_id': w['client_id'], 'client_secret': w['client_secret']}))
        d = aiwb_install.load(fresh=True)
        d.setdefault('google', {})['webClientId'] = w['client_id']
        d['google']['projectNumber'] = w['client_id'].split('-', 1)[0]
        aiwb_install.path().write_text(json.dumps(d, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        aiwb_install.load(fresh=True)
        out('ok', 'web OAuth client: secret in keyring aiwb/google-oauth-web, id and project number in install.json; '
                  'delete the downloaded file now')
    cid, num = install_get('google.webClientId') or '', str(install_get('google.projectNumber') or '')
    if re.match(r'^\d+-[a-z0-9]+\.apps\.googleusercontent\.com$', cid or ''):
        out('ok', 'install.json google.webClientId')
    else:
        out('next', 'web OAuth client: GOOGLE-CLOUD.md step 4, then google --import-web-client FILE'); bad += 1
    if num and cid.startswith(num + '-'):
        out('ok', 'project number matches the client id')
    elif cid:
        out('FAIL', 'google.projectNumber is not the number at the start of the client id'); bad += 1
    raw = keyring_get('aiwb/google-oauth-web')
    out('ok' if raw else 'next', 'web client secret in the keyring' if raw else 'web client secret: google --import-web-client FILE')
    desk = Path(a.desktop_client) if a.desktop_client else aiwb_install.path().parent / 'oauth-client-desktop.json'
    if desk.is_file():
        try:
            inst = json.loads(desk.read_text(encoding='utf-8')).get('installed') or {}
        except ValueError:
            inst = {}
        good = inst.get('client_id', '').startswith(num + '-') if num else bool(inst.get('client_id'))
        out('ok' if good else 'FAIL', 'desktop OAuth client file for Bridge' + ('' if good else ': no "installed" section or another project'))
        bad += not good
    else:
        out('next', f'desktop OAuth client (GOOGLE-CLOUD.md step 5): save it as {desk}; Bridge -> tray -> Connect Google uses it')
    url = install_get('site.url', '')
    if url and cid:
        loc = _redirect(url + '/auth/login.php')
        if loc and 'accounts.google.com' in loc and cid in loc:
            out('ok', 'sign-in of the site goes to Google with this client')
            if (url + '/auth/callback.php').replace(':', '%3A').replace('/', '%2F') not in loc and url + '/auth/callback.php' not in loc:
                out('warn', 'redirect_uri of the sign-in is not <site>/auth/callback.php')
        else:
            out('info', 'sign-in of the site is not ready yet (site-deploy --apply after this step)')
    save_step('google', bad == 0, f'{bad} open item(s)')
    return 1 if bad else 0


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kw):
        return None


def _redirect(url):
    try:
        urllib.request.build_opener(_NoRedirect).open(urllib.request.Request(url, headers={'User-Agent': 'freedom-setup'}), timeout=20)
    except urllib.error.HTTPError as e:
        return e.headers.get('Location') if 300 <= e.code < 400 else None
    except (urllib.error.URLError, OSError):
        return None
    return None


def _status(url):
    try:
        r = urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'freedom-setup'}), timeout=20)
        return r.status, r.read(4000).decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.read(4000).decode('utf-8', 'replace')
    except (urllib.error.URLError, OSError) as e:
        return 0, str(e)


# ---------------------------------------------------------------- Bridge and CO Desktop on this PC (Freedom 0.4.0)

def release_check(f: Path) -> str:
    """'' when f is listed with its size and sha256 in a freedom-manifest.json next to it that carries enough trusted
    maintainer signatures (install/maintainers.json); else what is wrong. Other files of the release may be missing."""
    import freedom_release
    man = f.parent / freedom_release.MANIFEST_NAME
    if not man.is_file():
        return f'no {freedom_release.MANIFEST_NAME} next to {f.name} (download it from the same release page)'
    m = json.loads(man.read_text(encoding='utf-8'))
    if not any(x.get('path') == f.name for x in m.get('files', [])):
        return f'{f.name} is not in the manifest of release {m.get("version")}'
    probs = [p for p in freedom_release.verify(f.parent, m, freedom_release.load_trust(freedom_release.DEFAULT_TRUST))
             if not p.startswith('missing ')]
    return '; '.join(probs)


def bridge_config() -> dict:
    try:
        return json.loads(bridge_config_path().read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        return {}


def cmd_bridge(a) -> int:
    if os.name != 'nt':
        out('info', 'Bridge for macOS/Linux: bridge/net8 (README there); this step is for Windows')
        save_step('bridge', False, 'not Windows')
        return 1
    errs = aiwb_install.check(aiwb_install.load(fresh=True))
    if errs:
        out('FAIL', 'install.json: ' + '; '.join(errs) + ' (init)')
        return 1
    args = ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(REPO / 'bridge' / 'install.ps1'),
            '-Python', sys.executable]
    if a.no_start:
        args.append('-NoStart')
    rc, o = sh(args, timeout=900)
    for line in o.splitlines()[-12:]:
        out('info', line)
    if rc != 0:
        out('FAIL', f'bridge/install.ps1 exit {rc}')
        save_step('bridge', False, f'install.ps1 exit {rc}')
        return 1
    cfg = bridge_config()
    patch = aiwb_install.bridge_patch(cfg) if cfg else {'config.json': 'missing'}
    out('ok' if not patch else 'FAIL', 'Bridge config.json' + ('' if not patch else f' lacks {sorted(patch)}'))
    desk = aiwb_install.path().parent / 'oauth-client-desktop.json'
    out('next', 'Bridge tray icon -> Connect Google with your Google account (desktop client: '
                + ('oauth-client-desktop.json next to install.json' if desk.is_file() else 'GOOGLE-CLOUD.md step 5 first') + ')')
    save_step('bridge', not patch, 'installed' if not patch else 'config incomplete')
    return 0 if not patch else 1


def desktop_installed():
    """Folder of the installed CO Desktop (per-user installer: HKCU Software\\CODesktop InstallDir) or None."""
    if os.name != 'nt':
        return None
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\CODesktop') as k:
            d = Path(winreg.QueryValueEx(k, 'InstallDir')[0])
    except OSError:
        return None
    return d if (d / 'CO.Desktop.exe').is_file() else None


def cmd_desktop(a) -> int:
    if os.name != 'nt':
        out('info', 'macOS: CO Resident (co-desktop/src/CO.Mac); this step is for Windows')
        save_step('desktop', False, 'not Windows')
        return 1
    bad = 0
    if a.installer:
        src = Path(a.installer)
        with tempfile.TemporaryDirectory(prefix='freedom-setup-') as tmp:
            # the checked copy is the one that runs (the downloaded file may change after the check)
            f = Path(tmp) / src.name
            why = '' if src.is_file() else f'{src}: not found'
            if not why:
                shutil.copy2(src, f)
                man = src.parent / 'freedom-manifest.json'
                if man.is_file():
                    shutil.copy2(man, Path(tmp) / man.name)
                why = release_check(f)
            if why:
                out('FAIL', f'installer not verified: {why}')
                save_step('desktop', False, 'installer not verified')
                return 1
            out('ok', f'{f.name}: signed by the Freedom maintainers, hash matches')
            try:
                rc = subprocess.run([str(f), '/S'], timeout=600).returncode
            except (OSError, subprocess.SubprocessError) as e:
                rc = f'{type(e).__name__}: {e}'
        out('ok' if rc == 0 else 'FAIL', f'installer: {"done" if rc == 0 else rc}')
        bad += rc != 0
    where = desktop_installed()
    if where:
        out('ok', 'CO Desktop installed')
    else:
        out('next', 'CO Desktop: download CO-Desktop-Setup-<version>.exe and freedom-manifest.json from the release page '
                    'into one folder, then: freedom_setup.py desktop --installer <the .exe>')
        bad += 1
    if aiwb_install.path().is_file():
        out('ok', 'install.json for CO Desktop (it reads the site and the members there)')
    else:
        out('next', 'init first: CO Desktop takes the site from install.json')
        bad += 1
    desk = aiwb_install.path().parent / 'oauth-client-desktop.json'
    if desk.is_file() or (where and (where / 'oauth-client.json').is_file()):
        out('ok', 'desktop OAuth client for CO Desktop')
    else:
        out('next', 'desktop OAuth client (GOOGLE-CLOUD.md step 5) as oauth-client-desktop.json next to install.json, '
                    'or choose the file in the first-start window of CO Desktop')
        bad += 1
    if where and not bad:
        out('next', 'start CO Desktop (Start menu) and sign in with Google; it connects to the Bridge of this PC')
    save_step('desktop', bad == 0, f'{bad} open item(s)')
    return 1 if bad else 0


def desktop_updates_dir() -> Path:
    """Where Bridge looks for CO Desktop releases (config desktopUpdatesDir; its default without the key)."""
    raw = bridge_config().get('desktopUpdatesDir') or ''
    return Path(os.path.expandvars(raw)) if raw else bridge_config_path().parent / 'desktop-updates'


def cmd_desktop_update(a) -> int:
    f = Path(a.installer)
    m = re.match(r'^CO-Desktop-Setup-(\d+\.\d+\.\d+)\.exe$', f.name)
    if not m:
        out('FAIL', 'the installer must be named CO-Desktop-Setup-x.y.z.exe')
        return 1
    why = release_check(f) if f.is_file() else f'{f}: not found'
    if why:
        out('FAIL', f'installer not verified: {why}')
        return 1
    data = f.read_bytes()
    # the bytes that go out are the ones of the manifest (the file may have changed after the check)
    entry = next(x for x in json.loads((f.parent / 'freedom-manifest.json').read_text(encoding='utf-8'))['files']
                 if x.get('path') == f.name)
    if hashlib.sha256(data).hexdigest() != entry.get('sha256'):
        out('FAIL', f'{f.name} changed while it was checked')
        return 1
    out('ok', f'{f.name}: signed by the Freedom maintainers, hash matches')
    # the site first: Bridge offers a release to the devices as soon as it sees the file, and they download it there
    latest = json.dumps({'version': m.group(1), 'fileName': f.name, 'sha256': hashlib.sha256(data).hexdigest(),
                         'size': len(data), 'publishedUtc': dt.datetime.now(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')},
                        indent=2) + '\n'
    if docker_variant():
        import freedom_docker
        h = freedom_docker.Host(freedom_docker.prepare_install())
        cid = h.cid('app')
        if not cid:
            out('FAIL', 'the app container is not running: freedom_docker.py up')
            return 1
        t, home = freedom_docker.DockerTarget(h, cid), freedom_docker.HOME
    else:
        home = (install_get('site.home') or '').rstrip('/')
        if not home:
            out('FAIL', 'install.json site.home (init)')
            return 1
        t = target(a)
    try:
        t.write(f'{home}/co-desktop/{f.name}', data)
        t.write(f'{home}/co-desktop/latest.json', latest.encode())
    finally:
        t.close()
    out('ok', f'site: {f.name} and latest.json in co-desktop/ of the site home')
    dest = desktop_updates_dir() / 'setup'
    dest.mkdir(parents=True, exist_ok=True)
    (dest / f.name).write_bytes(data)
    out('ok', f'Bridge: {f.name} offered to the paired computers within ~10 min; they install it when no agent call runs')
    save_step('desktop-update', True, m.group(1))
    return 0


# ---------------------------------------------------------------- PC access and household sandbox (SPEC 3.3, 4.1 step 6a)

SANDBOX = 'aiwb-family'
MIN_WSL = (2, 6)        # wsl --install --name/--location (wsl-family-sandbox create)
LINUX_USER = re.compile(r'^[a-z][a-z0-9_-]{1,30}$')   # rule of wsl-family-sandbox (stricter than Bridge's ValidLinuxUser)
# accounts every Ubuntu has (and the sandbox service user): a member with such an env would rebind a system account
SYSTEM_USERS = {'root', 'daemon', 'bin', 'sys', 'sync', 'games', 'man', 'lp', 'mail', 'news', 'uucp', 'proxy',
                'www-data', 'backup', 'list', 'irc', 'gnats', 'nobody', 'messagebus', 'syslog', 'ubuntu', 'polkitd',
                'systemd-network', 'systemd-resolve', 'systemd-timesync', 'tss', 'uuidd', 'tcpdump', 'landscape',
                'pollinate', 'sshd', 'usbmux', 'dnsmasq', 'aiwbsvc', 'aiwbfam'}


def bridge_config_path() -> Path:
    """config.json of the installed Bridge (BridgeAgent.ConfigPath): %LOCALAPPDATA% on Windows, the state root elsewhere."""
    if os.name == 'nt':
        return Path(os.environ.get('LOCALAPPDATA') or Path.home() / 'AppData' / 'Local') / 'AIWorkbenchBridge' / 'config.json'
    if sys.platform == 'darwin':
        return Path.home() / 'Library' / 'Application Support' / 'AIWorkbenchBridge' / 'config.json'
    return Path(os.environ.get('XDG_STATE_HOME') or Path.home() / '.local' / 'state') / 'AIWorkbenchBridge' / 'config.json'


def household_users(known_envs=None):
    """Members of install.json other than the owner -> ({env: Linux user}, [(env, why not)]). known_envs: the Bridge's
    chatProjectEnvironments; when not empty, Bridge ignores pcFamily entries of other environments."""
    users, bad = {}, []
    for m in install_get('members') or []:
        env = (m.get('env') or '').strip()
        if not env or m.get('role') == 'owner':
            continue
        if not LINUX_USER.match(env):
            bad.append((env, 'its environment id cannot be a Linux user ([a-z][a-z0-9_-], 2-31 characters)'))
        elif env in SYSTEM_USERS:
            bad.append((env, 'its environment id is a system account of Linux'))
        elif env in users:
            bad.append((env, 'listed twice in install.json'))
        elif known_envs and env not in known_envs:
            bad.append((env, 'Bridge does not know this environment yet (chatProjectEnvironments of its config)'))
        else:
            users[env] = env
    return users, bad


def sandbox_tool():
    for root in (REPO / 'tools', aiwb_install.workbench_root() / 'tools'):
        script = root / 'wsl-family-sandbox' / 'wsl-family-sandbox.py'
        if script.is_file():
            return script
    return None


def wsl_lines(args, timeout=60):
    """wsl.exe output (its own messages are UTF-16): (rc, [lines]); rc 127 when there is no wsl.exe at all."""
    exe = shutil.which('wsl.exe') or shutil.which('wsl')
    if not exe:
        return 127, []
    try:
        p = subprocess.run([exe] + list(args), capture_output=True, timeout=timeout)
    except (OSError, subprocess.SubprocessError):
        return 1, []
    raw = p.stdout
    text = raw.decode('utf-16-le', 'replace') if raw[1:2] == b'\x00' else raw.decode('utf-8', 'replace')
    return p.returncode, [l.strip().strip('\x00') for l in text.splitlines() if l.strip().strip('\x00')]


def wsl_version():
    """Version of the installed WSL as a tuple, None when WSL is not installed (Windows has a wsl.exe stub even then;
    only the installed WSL answers --version, with a localized first line 'WSL version: 2.6.1.0')."""
    rc, lines = wsl_lines(['--version'], timeout=30)
    m = re.search(r'(\d+)\.(\d+)\.\d+', lines[0]) if rc == 0 and lines else None
    return (int(m.group(1)), int(m.group(2))) if m else None


def cmd_pc_access(a) -> int:
    """Owner: pc_* of h-<hostId> (the whole PC with the rights of a Home job). Other members: only their own user in the
    WSL sandbox aiwb-family (pc_* of h-<hostId>-<user>). Plan by default; --apply sets it up (and switches it on again
    after --off), --check runs the sandbox self-test; --off switches both off in the Bridge config (the distro stays)."""
    cfgp = bridge_config_path()
    try:
        cfg = json.loads(cfgp.read_text(encoding='utf-8-sig')) if cfgp.is_file() else None
    except (OSError, ValueError) as e:
        out('FAIL', f'Bridge config {cfgp.name}: {e}')
        return 1
    if cfg is None:
        out('FAIL', 'Bridge is not installed on this computer yet (no config.json): install CO Desktop / Bridge, then '
                    'run pc-access again')
        save_step('pc-access', False, 'no Bridge')
        return 1
    dev = dict(cfg.get('devices') or {})
    want = dict(dev)
    bad = 0
    if a.off:
        want['pcTools'] = False
        want['pcFamilyTools'] = False
        out('info', 'switching off pc_* of the owner and the household sandbox (the WSL distro stays; remove it with '
                    'wsl-family-sandbox.py destroy --yes; pc-access --apply switches both on again)')
    else:
        if dev.get('pcTools') is False:
            want['pcTools'] = True
            out('info', 'pc_* of the owner is off in the Bridge config: --apply switches it on (--off to keep it off)')
        else:
            out('ok', "pc_* for the owner: on (the owner's agent jobs reach this PC from Site or Home through the bridge)")
        users, wrong = household_users(cfg.get('chatProjectEnvironments') or None)
        for env, why in wrong:
            out('warn', f'member "{env}": {why}; no sandbox seat')
        tool = sandbox_tool()
        ver = wsl_version() if users and os.name == 'nt' else None
        if not users:
            out('info', 'no other household members in install.json: no sandbox needed (add them with init --member, '
                        'then run pc-access again)')
        elif os.name != 'nt':
            out('info', 'household sandbox: only on Windows (WSL) in this version, skipped; members use their own devices')
        elif ver is None:
            out('warn', 'WSL is not installed: household sandbox skipped. To have it: `wsl --install --no-distribution` '
                        'as administrator, restart, then pc-access --apply')
        elif ver < MIN_WSL:
            out('warn', f'WSL {ver[0]}.{ver[1]} is older than {MIN_WSL[0]}.{MIN_WSL[1]}: household sandbox skipped. '
                        '`wsl --update`, then pc-access --apply')
        elif not tool:
            out('FAIL', 'tool wsl-family-sandbox not found (tools/ of the distribution or the tool library)')
            bad += 1
        else:
            _, distros = wsl_lines(['-l', '-q'])
            present = any(d.lower() == SANDBOX for d in distros)
            have, ready = set(), False
            if present:
                rc, rows = wsl_lines(['-d', SANDBOX, '-u', 'root', '--', 'cut', '-d', ' ', '-f1', '/etc/aiwb-family/users'])
                ready = rc == 0
                have = {re.split(r'[\s:]', r)[0] for r in rows} if ready else set()
            missing = [u for u in users.values() if u not in have]
            if not present:
                out('info', f'sandbox {SANDBOX}: not there yet, to be created for {len(users)} member(s) '
                            '(Ubuntu 24.04, compilers, quotas; 15-30 min and internet)')
            elif not ready:
                out('info', f'sandbox {SANDBOX}: the distro is there but not set up (an interrupted create?): create runs '
                            'again')
            elif missing:
                out('info', f'sandbox {SANDBOX}: {len(missing)} member(s) without a user yet')
            else:
                out('ok', f'sandbox {SANDBOX}: every member has a user')
            if a.apply and not (ready and not missing):
                if not ready:   # create is idempotent on an existing distro; uids of a fresh one from 3001
                    rc, o = sh([sys.executable, str(tool), 'create', '--users'] +
                               [f'{u}:{3001 + i}' for i, u in enumerate(users.values())], timeout=3600)
                else:           # add-user takes the next free uid of 3001..3998 itself
                    rc, o = 0, ''
                    for u in missing:
                        rc, o = sh([sys.executable, str(tool), 'add-user', u], timeout=900)
                        if rc:
                            break
                out('ok' if rc == 0 else 'FAIL', f'wsl-family-sandbox: {o.splitlines()[-1] if o else rc}' +
                    ('' if rc == 0 else ' (details: wsl-family-sandbox.py status; then pc-access --apply again)'))
                bad += rc != 0
                ready = rc == 0
            if not bad:
                fam = dict(dev.get('pcFamily') or {})
                fam.update(users)
                want['pcFamily'] = fam
                if dev.get('pcFamilyTools') is False:
                    want['pcFamilyTools'] = True
            if a.check and not bad:
                if not ready:
                    out('info', 'sandbox check: create the sandbox first (pc-access --apply)')
                for u in (users.values() if ready else []):
                    rc, o = sh([sys.executable, str(tool), 'check', '--user', u], timeout=1800)
                    out('ok' if rc == 0 else 'FAIL', f'sandbox check of one member: {o.splitlines()[-1] if o else rc}')
                    bad += rc != 0
    if want != dev:
        changed = ', '.join(sorted(k for k in set(want) | set(dev) if want.get(k) != dev.get(k)))
        if a.apply or a.off:
            cfg['devices'] = want
            bak = cfgp.with_name(cfgp.name + '.bak-pc-access')
            if not bak.exists():    # the config as it was before the wizard first touched it
                shutil.copy2(cfgp, bak)
            tmp = cfgp.with_name(cfgp.name + '.tmp')
            tmp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding='utf-8')
            os.replace(tmp, cfgp)
            out('ok', f'Bridge config: devices.{changed} written (first copy .bak-pc-access); restart Bridge from its '
                      'tray menu')
        else:
            out('info', f'Bridge config: devices.{changed} would change')
    if not (a.apply or a.check or a.off):
        out('next', 'python install/freedom_setup.py pc-access --apply  (then --check)')
    save_step('pc-access', bad == 0, ('off' if a.off else 'apply' if a.apply else 'plan') + f', {bad} problem(s)')
    return 1 if bad else 0


# ---------------------------------------------------------------- verify (SPEC 4.1 step 8)

def cmd_verify(a) -> int:
    if docker_variant():
        import freedom_docker
        if freedom_docker.site_verify(a) != 0:
            return 1
    url = install_get('site.url', '')
    if not url:
        out('FAIL', 'install.json: site.url'); return 1
    bad = 0
    code, body = _status(url + '/')
    good = code == 200 and 'id="root"' in body
    out('ok' if good else 'FAIL', f'CO Web: HTTP {code}' + ('' if good else ' (site-deploy --build --apply)')); bad += not good
    code, body = _status(url + '/agent/db.php?op=stats')
    good = code in (200, 400, 401, 403, 405) and body.lstrip().startswith('{')
    out('ok' if good else 'FAIL', f'db.php answers: HTTP {code}' + ('' if good else f' {body[:200]}')); bad += not good
    loc = _redirect(url + '/auth/login.php')
    good = bool(loc and 'accounts.google.com' in loc)
    out('ok' if good else 'FAIL', 'sign-in goes to Google' if good else 'sign-in: no redirect to Google (auth config)'); bad += not good
    if not a.no_tools and not bad:
        for name, args in (('aiwb-smoke', ['queue']), ('aiwb-e2e', ['run'])):
            # the tools of this release first (a new install has no tool library in Drive yet), then the library
            script = next((r / name / f'{name}.py' for r in (REPO / 'tools', aiwb_install.workbench_root() / 'tools')
                           if (r / name / f'{name}.py').is_file()), REPO / 'tools' / name / f'{name}.py')
            if not script.is_file():
                out('info', f'{name}: not in the tool library yet ({script})')
                continue
            rc, o = sh([sys.executable, str(script)] + args, timeout=900)
            out('ok' if rc == 0 else 'FAIL', f'{name}: {o.splitlines()[-1] if o else rc}')
            bad += rc != 0
    out('next' if bad else 'ok', 'fix the failed checks, then verify again' if bad else
        'done: open the site, sign in with your Google account; the light in the header shows Site and your PC')
    save_step('verify', bad == 0, f'{bad} problem(s)')
    return 1 if bad else 0


# ---------------------------------------------------------------- status, report

def cmd_status(a) -> int:
    st = load_state().get('steps', {})
    nxt = None
    for s in STEPS:
        r = st.get(s)
        out('ok' if r and r.get('ok') else ('FAIL' if r else '-'), f'{s:<12} {r["utc"] + "  " + r["summary"] if r else ""}')
        if nxt is None and not (r and r.get('ok')):
            nxt = s
    out('next', f'python install/freedom_setup.py {nxt}' if nxt else 'all steps done')
    return 0


def cmd_report(a) -> int:
    lines = []
    try:
        lines = log_path().read_text(encoding='utf-8').splitlines()[-200:]
    except OSError:
        pass
    st = load_state()
    text = ('Freedom setup report (no secrets, names or hosts; made by install/freedom_setup.py report)\n'
            f'system: {platform.system()} {platform.release()} {platform.machine()}, Python {platform.python_version()}\n'
            f'steps: {json.dumps({k: {"ok": v.get("ok"), "summary": v.get("summary")} for k, v in st.get("steps", {}).items()}, ensure_ascii=False)}\n'
            '--- last log lines ---\n' + '\n'.join(lines) + '\n'
            '--- question ---\nI am installing Freedom (a self-hosted AI Workbench). Which step failed and what should I do?\n')
    text = sanitize(text)
    dest = Path(a.out) if a.out else aiwb_install.path().parent / 'setup-report.txt'
    dest.write_text(text, encoding='utf-8')
    copied = False
    if os.name == 'nt':
        try:
            subprocess.run(['clip'], input=('\ufeff' + text).encode('utf-16-le'), check=True, timeout=10)
            copied = True
        except (OSError, subprocess.SubprocessError):
            pass
    elif shutil.which('pbcopy'):
        copied = subprocess.run(['pbcopy'], input=text.encode(), timeout=10).returncode == 0
    out('ok', f'report: {dest}' + (' (also in the clipboard)' if copied else ''))
    out('next', 'read it, then paste it into your own claude.ai or chatgpt.com chat, or post it to the Freedom forum')
    return 0


# ---------------------------------------------------------------- CLI

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog='freedom_setup.py', description='Freedom setup wizard without AI (SPEC 4.1)')
    sp = ap.add_subparsers(dest='cmd')
    sp.add_parser('status')
    sp.add_parser('preflight')
    p = sp.add_parser('init')
    for k in ('site-url', 'owner-email', 'owner-name', 'owner-env', 'drive-root', 'ssh-host', 'ssh-port', 'ssh-user',
              'ssh-key', 'site-home', 'web-root', 'db-host', 'db-name', 'db-user', 'web-client-id', 'project-number'):
        p.add_argument('--' + k)
    p.add_argument('--member', action='append')
    p.add_argument('--variant', choices=['A', 'B', 'C', 'a', 'b', 'c'], help='A shared hosting (default), B VPS/NAS with '
                   'Docker, C only this computer (Docker, Tailscale) - B/C: install/freedom_docker.py')
    p.add_argument('--force', action='store_true')
    p.add_argument('--no-input', action='store_true')
    p = sp.add_parser('subs')
    p.add_argument('--install', choices=['claude', 'codex', 'all'])
    p.add_argument('--login', choices=['claude', 'codex'])
    for name in ('site-probe', 'site-deploy'):
        p = sp.add_parser(name)
        p.add_argument('--transport', choices=['ssh', 'local'], default='ssh')
        p.add_argument('--local-root')
        p.add_argument('--host-key', help='SHA256:... fingerprint of the hosting you compared with its panel')
        if name == 'site-deploy':
            p.add_argument('--diff', action='store_true')
            p.add_argument('--apply', action='store_true')
            p.add_argument('--parts')
            p.add_argument('--build', action='store_true')
            p.add_argument('--no-bak', action='store_true')
            p.add_argument('--rewrite-config', action='store_true')
            p.add_argument('--verbose', action='store_true')
    p = sp.add_parser('google')
    p.add_argument('--import-web-client')
    p.add_argument('--desktop-client')
    p = sp.add_parser('bridge')
    p.add_argument('--no-start', action='store_true')
    p = sp.add_parser('desktop')
    p.add_argument('--installer')
    p = sp.add_parser('desktop-update')
    p.add_argument('--installer', required=True)
    p.add_argument('--transport', choices=['ssh', 'local'], default='ssh')
    p.add_argument('--local-root')
    p.add_argument('--host-key')
    p = sp.add_parser('pc-access')
    p.add_argument('--apply', action='store_true')
    p.add_argument('--check', action='store_true')
    p.add_argument('--off', action='store_true')
    p = sp.add_parser('verify')
    p.add_argument('--no-tools', action='store_true')
    p = sp.add_parser('report')
    p.add_argument('--out')
    a = ap.parse_args(argv)
    fn = {'status': cmd_status, 'preflight': cmd_preflight, 'init': cmd_init, 'subs': cmd_subs,
          'site-probe': cmd_site_probe, 'site-deploy': cmd_site_deploy, 'google': cmd_google,
          'bridge': cmd_bridge, 'desktop': cmd_desktop, 'desktop-update': cmd_desktop_update,
          'pc-access': cmd_pc_access, 'verify': cmd_verify,
          'report': cmd_report}.get(a.cmd)
    if not fn:
        print(__doc__)
        return 1
    try:
        return fn(a)
    except SystemExit:
        raise
    except Exception as e:  # a failed step: the message, and the way to help
        out('FAIL', f'{a.cmd}: {type(e).__name__}: {e}')
        out('next', 'python install/freedom_setup.py report -> your own claude.ai / chatgpt.com chat or the forum')
        save_step(a.cmd, False, f'{type(e).__name__}')
        return 1


if __name__ == '__main__':
    sys.exit(main())
