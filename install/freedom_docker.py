# SPDX-License-Identifier: AGPL-3.0-or-later
"""Freedom variants B and C (SPEC Freedom section 4, stage F3b): the AI Workbench server in Docker.

  B  VPS or NAS with Docker: Caddy on 80/443 with automatic HTTPS for the domain of site.url.
  C  only a computer: the same stack with plain HTTP on 127.0.0.1:<port>; from outside through Tailscale
     (`tailscale serve` / `funnel` ends TLS with the *.ts.net name, which is then site.url).

The stack is the hosting of variant A in containers: app (Apache + PHP 8.3 + Node 24, user co, HOME /home/co, cPanel
layout), db (MariaDB 11), cron (the cron lines of the panel, same image) and caddy. `deploy` fills the volume /home/co
with the same repo -> server map (freedom_setup.layout()) and the same secret writers and schema step as variant A.

  python install/freedom_docker.py plan                       what would run where (no changes)
  python install/freedom_docker.py up [--no-build]            stack folder on the Docker host, secrets once (0600), compose up
  python install/freedom_docker.py deploy [--diff] [--build] [--parts agent,auth,web,home,config,secrets,
                                           schema,cron,cli] [--oauth-client FILE] [--rewrite-config] [--no-bak]
                                                              site files, config, secrets, schema, members, cron lines
  python install/freedom_docker.py verify [--php-tests] [--wait-cron SEC] [--agent]
                                                              HTTP of CO Web, sign-in, ping.php, db.php with the host key,
                                                              cron sidecar, site agent; PHP tests on a throwaway database
  python install/freedom_docker.py backup [--out FILE]        mysqldump of the db container (gzip) on the Docker host
  python install/freedom_docker.py down [--volumes] [--rmi]   stop; --volumes also deletes the data (asks no question)

install.json: "variant": "B" | "C" and an optional "docker" object (defaults in DEFAULTS): project (compose project
name), cli ("auto" | "docker" | "wsl": on Windows the Docker of WSL), stackDir (on the Docker host; default
~/.local/share/aiwb-freedom/<project>), bind, httpPort, httpsPort, tls ("auto" | "internal" | an e-mail for ACME),
acmeEmail, keyring (also keep the generated secrets in the OS keyring of this computer, as variant A does), cpus.

Secrets: generated once into <stackDir>/secrets (folder 0700, files 0600) on the Docker host: db_password,
db_root_password, push_key, and google_oauth_web.json when --oauth-client is given. With docker.keyring (default) the
database password, push key and web OAuth client are also read from / written to the keyring entries of variant A
(aiwb/co-mysql, aiwb/co-push, aiwb/google-oauth-web), so Bridge and the tools of this PC find them.
"""
import argparse
import datetime as dt
import hashlib
import http.client
import io
import json
import os
import re
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import tarfile
import time
import urllib.parse
from contextlib import contextmanager
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import aiwb_install  # noqa: E402
import freedom_setup as fs  # noqa: E402

REPO = fs.REPO
DOCKER_DIR = Path(__file__).resolve().parent / 'docker'
STACK_FILES = ('compose.yaml', 'compose.variant-b.yaml', 'compose.variant-c.yaml', 'Dockerfile', 'apache-site.conf',
               'php.ini', 'freedom-cron.php', 'run-php-tests.sh')
HOME = '/home/co'                       # the "hosting" account inside the image (cPanel layout)
WEB = HOME + '/public_html'
INTERNAL_URL = 'https://app'            # how the site agent reaches db.php inside the stack (config.php site_url)
NODE = '/usr/local/bin/node'
# the root password reaches the client in its environment (MYSQL_PWD), never on a command line (ps shows those)
ROOT_PWD = 'MYSQL_PWD="$(cat /run/secrets/db_root_password)" '
LOCAL_BINDS = ('127.0.0.1', 'localhost', '::1')
DEFAULTS = {'project': 'freedom', 'cli': 'auto', 'stackDir': '', 'bind': '', 'httpPort': 0, 'httpsPort': 0,
            'tls': 'auto', 'acmeEmail': '', 'keyring': True, 'cpus': 2}
PARTS = 'agent,auth,web,home,config,secrets,schema,cron'  # F10: the server reads model gateways from modules, no connector manifest
out = fs.out


# ---------------------------------------------------------------- settings

def variant() -> str:
    return str(aiwb_install.get('variant') or 'A').upper()


def settings() -> dict:
    """docker settings of install.json with defaults and the values that follow from the variant and site.url."""
    v = variant()
    d = {**DEFAULTS, **(aiwb_install.get('docker') or {})}
    if not re.match(r'^[a-z0-9][a-z0-9_-]{0,40}$', str(d['project'])):
        raise SystemExit(f'install.json docker.project {d["project"]!r}: lower-case letters, digits, - and _')
    url = aiwb_install.get('site.url', '') or ''
    u = urllib.parse.urlsplit(url)
    d['variant'] = v
    d['url'] = url.rstrip('/')
    d['host'] = u.hostname or 'localhost'
    d['scheme'] = u.scheme or 'https'
    d['bind'] = d['bind'] or ('0.0.0.0' if v == 'B' else '127.0.0.1')
    if v == 'C' and d['bind'] not in LOCAL_BINDS:
        raise SystemExit(f'install.json docker.bind {d["bind"]!r}: variant C publishes the site on this computer only '
                         f'({", ".join(LOCAL_BINDS)}); for other devices use Tailscale, or variant B with HTTPS')
    d['httpPort'] = int(d['httpPort'] or (80 if v == 'B' else 18080))
    d['httpsPort'] = int(d['httpsPort'] or 443)
    tls = str(d.get('tls') or 'auto')
    d['tls'] = tls
    d['dbName'] = aiwb_install.get('site.db.name') or 'co'
    d['dbUser'] = aiwb_install.get('site.db.user') or 'co'
    d['image'] = f'{d["project"]}-app:local'
    cpus = float(d.get('cpus') or 2)
    d['cpus'] = cpus
    if os.name == 'nt' and d.get('cli') == 'docker':
        raise SystemExit('install.json docker.cli "docker" does not work on Windows (the steps need sh and cat on the '
                         'Docker host): use "wsl" (Docker Engine in WSL, or Docker Desktop with WSL integration)')
    if d.get('keyring') and not keyring_usable():
        out('warn', 'no OS keyring backend on this computer: the secrets stay only in the stack folder (docker.keyring off)')
        d['keyring'] = False
    return d


def keyring_usable() -> bool:
    """False on headless hosts without a keyring backend (keyring's fail/null backends, or no module)."""
    try:
        import keyring
        kr = keyring.get_keyring()
    except Exception:
        return False
    name = f'{type(kr).__module__}.{type(kr).__name__}'.lower()
    if 'chainer' in name:
        return bool(getattr(kr, 'backends', None))
    return not any(x in name for x in ('fail', 'null'))


def keyring_put(fn, service: str, account: str, value: str) -> bool:
    """keyring_set of freedom_setup has no try/except: a keyring error only warns, the stack secret is kept."""
    try:
        fn(service, account, value)
        return True
    except Exception as e:
        out('warn', f'keyring {service}: not saved ({type(e).__name__}); the secret stays in the stack folder')
        return False


def prepare_install() -> dict:
    """install.json as the variant A code expects it, for the stack: site.home / webRoot / db.host point into the
    containers (only in memory; the file is not changed)."""
    cfg = aiwb_install.load()
    if variant() not in ('B', 'C'):
        raise SystemExit(f'install.json variant is {variant()!r}: freedom_docker.py is for "B" and "C" '
                         '(variant A: freedom_setup.py site-deploy)')
    errs = aiwb_install.check(cfg)
    if errs:
        raise SystemExit('install.json: ' + '; '.join(errs))
    site = cfg.setdefault('site', {})
    site['home'], site['webRoot'] = HOME, WEB
    db = site.setdefault('db', {})
    db['host'] = 'db'
    db.setdefault('name', 'co')
    db.setdefault('user', 'co')
    db['name'] = db['name'] or 'co'
    db['user'] = db['user'] or 'co'
    return settings()


def caddyfile(s: dict) -> str:
    proxy = ('\treverse_proxy https://app:443 {\n\t\ttransport http {\n\t\t\ttls_insecure_skip_verify\n\t\t}\n\t}\n')
    head = '# Generated by install/freedom_docker.py (variant ' + s['variant'] + ').\n'
    if s['variant'] == 'C':
        # plain HTTP on the published localhost port; Tailscale (or the browser on this computer) in front
        return head + '{\n\tauto_https off\n}\n\n:80 {\n' + proxy + '}\n'
    glob = []
    tls = ''
    if s['tls'] == 'internal':
        tls = '\ttls internal\n'
    elif '@' in s['tls']:
        glob.append(f'\temail {s["tls"]}')
    if s.get('acmeEmail'):
        glob.append(f'\temail {s["acmeEmail"]}')
    g = ('{\n' + '\n'.join(dict.fromkeys(glob)) + '\n}\n\n') if glob else ''
    return head + g + f'{s["host"]} {{\n' + tls + proxy + '}\n'


def env_text(s: dict, ncpu: int = 0) -> str:
    """ncpu: CPUs of the Docker host; no limit may exceed it (Docker refuses cpus above nproc)."""
    cap = (lambda x: min(x, float(ncpu))) if ncpu and ncpu > 0 else (lambda x: x)
    cpus = cap(s['cpus'])
    rows = {'COMPOSE_PROJECT_NAME': s['project'], 'APP_IMAGE': s['image'], 'DB_NAME': s['dbName'], 'DB_USER': s['dbUser'],
            'BIND': s['bind'], 'HTTP_PORT': s['httpPort'], 'HTTPS_PORT': s['httpsPort'],
            'APP_CPUS': cpus, 'DB_CPUS': cap(max(0.5, round(cpus / 2, 2))),
            'CRON_CPUS': cap(max(0.5, round(cpus / 2, 2))), 'CADDY_CPUS': cap(0.5)}
    return '# Generated by install/freedom_docker.py; no secrets here (they are in secrets/).\n' + \
        ''.join(f'{k}={v}\n' for k, v in rows.items())


# ---------------------------------------------------------------- the Docker host

class Host:
    """Runs commands where Docker runs: this computer, or WSL from Windows (cli "wsl")."""

    def __init__(self, s: dict):
        cli = s.get('cli') or 'auto'
        if cli == 'auto':
            cli = 'wsl' if os.name == 'nt' and shutil.which('wsl.exe') else 'docker'
        self.cli = cli
        self.prefix = ['wsl.exe', '-e'] if cli == 'wsl' else []
        self.s = s
        self._home = None

    def run(self, args, stdin: bytes = None, timeout=600):
        """(rc, bytes of stdout+stderr)."""
        try:
            p = subprocess.run(self.prefix + list(args), input=stdin, capture_output=True, timeout=timeout,
                               cwd='C:\\' if os.name == 'nt' else None)
            return p.returncode, p.stdout + p.stderr
        except FileNotFoundError as e:
            return 127, str(e).encode()
        except subprocess.TimeoutExpired:
            return 124, f'timeout {timeout} s: {" ".join(args[:4])}'.encode()

    def text(self, args, stdin: bytes = None, timeout=600):
        rc, o = self.run(args, stdin, timeout)
        return rc, o.decode('utf-8', 'replace').strip()

    def sh(self, script: str, *args, stdin: bytes = None, timeout=600):
        return self.text(['sh', '-c', script, 'sh', *args], stdin, timeout)

    def nproc(self) -> int:
        rc, n = self.sh('nproc 2>/dev/null || getconf _NPROCESSORS_ONLN')
        return int(n) if rc == 0 and n.strip().isdigit() else 0

    def home(self) -> str:
        if self._home is None:
            rc, h = self.sh('printf %s "$HOME"')
            if rc != 0 or not h.startswith('/'):
                raise SystemExit(f'Docker host: no home folder ({h})')
            self._home = h
        return self._home

    def stack(self) -> str:
        return (self.s.get('stackDir') or f'{self.home()}/.local/share/aiwb-freedom/{self.s["project"]}').rstrip('/')

    def write(self, path: str, data: bytes, mode: int = 0o644):
        rc, o = self.sh('umask 077; mkdir -p "$(dirname "$1")" && cat > "$1.tmp" && chmod "$2" "$1.tmp" && mv -f "$1.tmp" "$1"',
                        path, format(mode, 'o'), stdin=data)
        if rc != 0:
            raise RuntimeError(f'{path}: {o}')

    def read(self, path: str):
        rc, o = self.run(['cat', path])
        return o if rc == 0 else None

    def secret(self, name: str):
        v = self.read(f'{self.stack()}/secrets/{name}')
        return v.decode().strip() if v else None

    def set_secret(self, name: str, value: str):
        self.sh('mkdir -p -m 700 "$1" && chmod 700 "$1"', f'{self.stack()}/secrets')
        self.write(f'{self.stack()}/secrets/{name}', (value.strip() + '\n').encode(), 0o600)

    def compose(self, *args, timeout=1800):
        st = self.stack()
        files = ['-f', f'{st}/compose.yaml', '-f', f'{st}/compose.variant-{self.s["variant"].lower()}.yaml']
        return self.text(['docker', 'compose', '-p', self.s['project'], '--project-directory', st, *files, *args],
                         timeout=timeout)

    def cid(self, service: str) -> str:
        rc, o = self.compose('ps', '-q', service, timeout=60)
        ids = [x for x in o.split() if re.match(r'^[0-9a-f]{12,64}$', x)]
        return ids[0] if rc == 0 and ids else ''


# ---------------------------------------------------------------- the app container as a deploy target of freedom_setup

class DockerTarget:
    """freedom_setup's target interface (run, shas, exists, read, write, replace, close) on the app container, as the
    user co. put_many() writes a whole batch with one tar: .new files, `php -l`, .bak copy, mv - like SshTarget."""
    local = False

    def __init__(self, host: Host, container: str):
        self.h, self.c = host, container

    def run(self, cmd: str, timeout=600, stdin: bytes = None, user='co'):
        return self.h.text(['docker', 'exec', '-i', '-u', user, self.c, 'sh', '-c', cmd], stdin, timeout)

    def shas(self, paths):
        res = {}
        for i in range(0, len(paths), 200):
            chunk = paths[i:i + 200]
            _, t = self.run('sha256sum -- ' + ' '.join(fs._q(p) for p in chunk) + ' 2>/dev/null; true')
            for line in t.splitlines():
                m = re.match(r'^([0-9a-f]{64})\s+\*?(.+)$', line)
                if m:
                    res[m.group(2)] = m.group(1)
        return res

    def exists(self, path):
        return self.run('test -e ' + fs._q(path))[0] == 0

    def read(self, path) -> bytes:
        rc, o = self.h.run(['docker', 'exec', '-u', 'co', self.c, 'cat', path])
        if rc != 0:
            raise OSError(f'{path}: {o[:200]!r}')
        return o

    def write(self, path, data: bytes, mode=None):
        folder = path.rsplit('/', 1)[0]
        if mode:   # a secret: folder 0700, the empty file gets its mode before the data goes in
            cmd = (f'umask 077 && mkdir -p -m 700 {fs._q(folder)} && chmod 700 {fs._q(folder)} && : > {fs._q(path)} && '
                   f'chmod {mode:o} {fs._q(path)} && cat > {fs._q(path)}')
        else:
            cmd = f'mkdir -p {fs._q(folder)} && cat > {fs._q(path)}'
        rc, o = self.run(cmd, stdin=data)
        if rc != 0:
            raise RuntimeError(f'{path}: {o}')

    def replace(self, path, data: bytes, php: bool, bak: str, mode=None) -> str:
        bad = self.put_many([(path, data, php)], bak)
        if bad:
            raise RuntimeError('; '.join(bad))
        return 'replaced'

    def put_many(self, items, bak: str):
        """items: (remote path, bytes, php). Returns the failed paths with the reason; the others are live."""
        if not items:
            return []
        tag = secrets.token_hex(4)
        lst = f'/tmp/freedom-deploy-{tag}.list'
        buf = io.BytesIO()
        now = time.time()
        with tarfile.open(fileobj=buf, mode='w') as tar:
            for path, data, _ in items:
                ti = tarfile.TarInfo((path + '.new').lstrip('/'))
                ti.size, ti.mtime = len(data), now
                ti.mode = 0o755 if path.endswith('.sh') else 0o644
                tar.addfile(ti, io.BytesIO(data))
            listing = ''.join(f'{1 if php else 0} {path}\n' for path, _, php in items).encode()
            ti = tarfile.TarInfo(lst.lstrip('/'))
            ti.size, ti.mtime, ti.mode = len(listing), now, 0o600
            tar.addfile(ti, io.BytesIO(listing))
        keep = (f'[ ! -e "$p" ] || cp -p "$p" "$p.bak-{bak}" || {{ echo "FAILED $p bak"; continue; }}; ') if bak else ''
        script = ('tar -x -m -C / -f - || exit 9; '
                  'while read -r php p; do '
                  'if [ "$php" = 1 ] && ! err=$(php -l "$p.new" 2>&1); then rm -f "$p.new"; echo "FAILED $p php -l: $err"; continue; fi; '
                  + keep +
                  '[ ! -e "$p" ] || chmod --reference="$p" "$p.new"; '
                  'mv -f "$p.new" "$p" || echo "FAILED $p mv"; '
                  f'done < {lst}; rm -f {lst}')
        rc, o = self.run(script, stdin=buf.getvalue(), timeout=900)
        if rc == 9:
            raise RuntimeError(f'tar into the app container failed: {o[-400:]}')
        return [line[7:] for line in o.splitlines() if line.startswith('FAILED ')]

    def close(self):
        pass


@contextmanager
def secret_store(host: Host, s: dict):
    """freedom_setup reads and writes its secrets through keyring_get/keyring_set; for the stack they come from the
    secrets folder on the Docker host first, then (docker.keyring) from the OS keyring, and new ones go to both."""
    files = {('aiwb/co-mysql', s['dbUser']): 'db_password', ('aiwb/co-push', 'default'): 'push_key',
             ('aiwb/google-oauth-web', 'default'): 'google_oauth_web.json',
             ('aiwb/site-claude-token', 'default'): 'site_claude_token'}
    orig_get, orig_set = fs.keyring_get, fs.keyring_set

    def get(service, account='default'):
        name = files.get((service, account))
        v = host.secret(name) if name else None
        if v:
            return v
        return orig_get(service, account) if s.get('keyring') else None

    def put(service, account, value):
        name = files.get((service, account))
        if name:
            host.set_secret(name, value)
        if s.get('keyring'):
            keyring_put(orig_set, service, account, value)

    fs.keyring_get, fs.keyring_set = get, put
    try:
        yield
    finally:
        fs.keyring_get, fs.keyring_set = orig_get, orig_set


# ---------------------------------------------------------------- steps

def docker_ok(h: Host) -> bool:
    rc, v = h.text(['docker', 'version', '--format', '{{.Server.Version}}'], timeout=60)
    if rc != 0:
        if h.cli == 'wsl':
            out('FAIL', f'Docker in WSL: {v[-300:] or rc} - the default WSL distribution has no working docker (wsl -l -v '
                        'lists them; install Docker Engine there, enable WSL integration of Docker Desktop for it, or make '
                        'the distribution with Docker the default: wsl --set-default <name>)')
        else:
            out('FAIL', f'Docker: {v[-300:] or rc} - install Docker Engine (Linux: get.docker.com; Mac: Docker Desktop, '
                        'OrbStack or Colima; NAS: the Docker / Container Manager package)')
        return False
    rc2, c = h.text(['docker', 'compose', 'version', '--short'], timeout=60)
    if rc2 != 0:
        out('FAIL', 'docker compose v2 is missing (package docker-compose-plugin)')
        return False
    out('ok', f'Docker {v.splitlines()[-1]}, compose {c.splitlines()[-1]} ({"WSL" if h.cli == "wsl" else "local"})')
    return True


def urls(s: dict) -> dict:
    local = (f'https://{s["host"]}:{s["httpsPort"]}' if s['variant'] == 'B' else f'http://localhost:{s["httpPort"]}')
    return {'site': s['url'], 'local': local}


def cmd_plan(a) -> int:
    s = prepare_install()
    h = Host(s)
    home, web, parts = fs.layout()
    out('info', f'variant {s["variant"]}: {"VPS/NAS, Caddy with HTTPS for " + s["host"] if s["variant"] == "B" else "this computer, plain HTTP on " + s["bind"]}')
    out('info', f'Docker host: {"WSL of this PC" if h.cli == "wsl" else "this computer"}; compose project {s["project"]}; '
                f'stack folder {s["stackDir"] or "~/.local/share/aiwb-freedom/" + s["project"]}')
    ports = (f'{s["bind"]}:{s["httpPort"]}->80, {s["bind"]}:{s["httpsPort"]}->443 (tls {s["tls"]})' if s['variant'] == 'B'
             else f'{s["bind"]}:{s["httpPort"]}->80')
    out('info', f'services: db (MariaDB 11, volume db), app ({s["image"]}: Apache+PHP 8.3+Node 24, volume home), '
                f'cron (same image), caddy ({ports}); cpus app {s["cpus"]}')
    for name in ('agent', 'auth', 'web', 'home'):
        out('info', f'deploy {name}: {len(parts[name])} file(s) -> {parts[name][0][1].rsplit("/", 1)[0] if parts[name] else "-"}')
    if not parts['web']:
        out('warn', 'co-web/dist is missing: deploy --build (Node 20+ and npm on this computer) or build co-web first')
    out('info', 'secrets: <stack>/secrets/ db_password, db_root_password, push_key (0600, made once by up)'
                + (', also in the OS keyring (docker.keyring)' if s['keyring'] else ''))
    out('info', 'cron: ' + '; '.join(cron_lines(home)))
    out('info', f'site: {s["url"]}  local check: {urls(s)["local"]}')
    out('next', 'freedom_docker.py up, then deploy --build, then verify')
    return 0


def cron_lines(home=HOME) -> list:
    """The panel lines of variant A, output kept (the sidecar logs it)."""
    return [re.sub(r'\s*>\s*/dev/null\s+2>&1\s*$', '', l.format(home=home)) for l in fs.CRON_LINES]


def cmd_up(a) -> int:
    s = prepare_install()
    h = Host(s)
    if not docker_ok(h):
        return 1
    st = h.stack()
    for name in STACK_FILES:
        h.write(f'{st}/{name}', (DOCKER_DIR / name).read_bytes().replace(b'\r\n', b'\n'),
                0o755 if name.endswith('.sh') else 0o644)
    h.write(f'{st}/Caddyfile', caddyfile(s).encode(), 0o644)
    h.write(f'{st}/.env', env_text(s, h.nproc()).encode(), 0o600)
    made = []
    for name, keyring_key in (('db_password', ('aiwb/co-mysql', s['dbUser'])), ('db_root_password', None),
                              ('push_key', ('aiwb/co-push', 'default'))):
        if h.secret(name):
            continue
        val = fs.keyring_get(*keyring_key) if keyring_key and s['keyring'] else None
        val = val or secrets.token_urlsafe(32)
        h.set_secret(name, val)
        if keyring_key and s['keyring'] and not fs.keyring_get(*keyring_key):
            keyring_put(fs.keyring_set, *keyring_key, val)
        made.append(name)
    out('ok', f'stack folder {st}' + (f'; secrets made (0600): {", ".join(made)}' if made else '; secrets kept'))
    args = ['up', '-d', '--remove-orphans'] + ([] if a.no_build else ['--build'])
    out('info', f'docker compose -p {s["project"]} {" ".join(args)} (first build: a few minutes)')
    rc, o = h.compose(*args, timeout=3600)
    if rc != 0:
        out('FAIL', f'compose up: {o[-1500:]}')
        fs.save_step('site-deploy', False, f'docker up failed ({s["variant"]})')
        return 1
    if not wait_ready(h, 240):
        out('FAIL', 'the stack did not become ready in 4 min: docker compose -p ' + s['project'] + ' logs')
        return 1
    out('ok', f'stack {s["project"]} is up: {urls(s)["local"]}')
    out('next', 'freedom_docker.py deploy --build')
    return 0


def services(h: Host) -> dict:
    rc, o = h.compose('ps', '-a', '--format', 'json', timeout=60)
    res = {}
    for line in o.splitlines():
        line = line.strip()
        if not line.startswith(('{', '[')):
            continue
        try:
            j = json.loads(line)
        except ValueError:
            continue
        for r in (j if isinstance(j, list) else [j]):
            res[r.get('Service')] = {'state': r.get('State'), 'health': r.get('Health') or '', 'status': r.get('Status')}
    return res


def wait_ready(h: Host, secs: int) -> bool:
    end = time.time() + secs
    while time.time() < end:
        sv = services(h)
        if all(sv.get(x, {}).get('state') == 'running' for x in ('db', 'app', 'cron', 'caddy')) \
                and sv['db']['health'] == 'healthy':
            return True
        time.sleep(3)
    return False


def server_config_text() -> str:
    """config.php of variant A with site_url = the address the site agent can reach inside the stack."""
    text = aiwb_install.server_config()
    line = f"    'site_url' => {aiwb_install._php(INTERNAL_URL)}, // inside the stack (Freedom B/C); public: {aiwb_install.get('site.url', '')}"
    if "'site_url' =>" in text:
        return re.sub(r"^    'site_url' => .*$", lambda m: line, text, flags=re.M)
    return text.replace('return [\n', 'return [\n' + line + '\n', 1)


def cmd_deploy(a) -> int:
    s = prepare_install()
    h = Host(s)
    want = set((a.parts or PARTS).split(','))
    if a.oauth_client:
        j = json.loads(Path(a.oauth_client).read_text(encoding='utf-8'))
        w = j.get('web') or j
        if not w.get('client_id') or not w.get('client_secret'):
            out('FAIL', '--oauth-client: no client_id / client_secret (the "web" client JSON of Google Cloud)')
            return 1
        val = json.dumps({'client_id': w['client_id'], 'client_secret': w['client_secret']})
        h.set_secret('google_oauth_web.json', val)
        if s['keyring']:
            keyring_put(fs.keyring_set, 'aiwb/google-oauth-web', 'default', val)
        out('ok', 'web OAuth client saved with the stack secrets (0600)')
    if a.build and 'web' in want and fs.build_web() != 0:
        return 1
    home, web, parts = fs.layout()
    if 'web' in want and not parts['web']:
        out('warn', 'co-web/dist is missing: add --build (needs Node and npm) or build co-web first')
    cid = h.cid('app')
    if not cid:
        out('FAIL', 'the app container is not running: freedom_docker.py up')
        return 1
    t = DockerTarget(h, cid)
    items = [(f.read_bytes(), r, f.suffix == '.php') for name in ('agent', 'auth', 'web', 'home')
             if name in want for f, r in parts[name]]
    if 'config' in want:
        items.append((server_config_text().encode(), f'{home}/co-agent/config.php', True))
    remote = t.shas([r for _, r, _ in items])
    counts = {'same': 0, 'changed': 0, 'new': 0}
    todo = []
    for data, r, php in items:
        st = 'same' if remote.get(r) == hashlib.sha256(data).hexdigest() else ('changed' if r in remote else 'new')
        if st == 'changed' and r == f'{home}/co-agent/config.php' and not a.rewrite_config:
            out('info', 'config.php in the stack differs from install.json: kept (--rewrite-config replaces it)')
            continue
        counts[st] += 1
        if st != 'same':
            todo.append((r, data, php))
            if a.diff or a.verbose:
                out('info', f'{st:<7} {r}')
    out('ok', f'files: {counts["same"]} same, {counts["changed"]} changed, {counts["new"]} new')
    if a.diff:
        for name, path in (('auth config', f'{home}/co-auth/config.php'), ('db.json', f'{home}/co-agent/secrets/db.json'),
                           ('push key', f'{home}/co-agent/secrets/push-notify-key')):
            have = t.exists(path)
            out('ok' if have else 'info', f'{name}: {"present" if have else "missing (made by deploy)"}')
        fs.save_step('site-deploy', True, f'docker diff: {counts}')
        return 0
    bak = '' if a.no_bak else dt.datetime.now().strftime('%Y%m%d-%H%M%S')
    bad = 0
    failed = t.put_many(todo, bak)
    for f in failed:
        out('FAIL', f'not written: {f}')
    bad += len(failed)
    if todo:
        out('ok', f'{len(todo) - len(failed)} file(s) written into the volume' + (f', old copies *.bak-{bak}' if bak and counts['changed'] else ''))
    with secret_store(h, s):
        if 'config' in want:
            bad += fs.deploy_auth_config(t, home)
        if 'secrets' in want:
            bad += fs.deploy_secrets(t, home)
        rc, o = t.run(f'chmod +x {home}/co-agent/executor/run.sh 2>/dev/null; mkdir -p {home}/co-agent/executor/logs '
                      f'{home}/co-agent/bin '
                      f'&& ln -sfn {NODE} {home}/co-agent/bin/node && {home}/co-agent/bin/node --version')
        out('ok' if rc == 0 else 'FAIL', f'Node of the site agent: {o.splitlines()[-1] if o else rc}')
        bad += rc != 0
        if 'schema' in want:
            bad += fs.deploy_schema(t, home)
        if 'cron' in want:
            t.write(f'{home}/co-agent/crontab', ('# Freedom B/C: the cron lines of the hosting panel, run by the cron '
                                                 'sidecar (freedom-cron.php)\n' + '\n'.join(cron_lines(home)) + '\n').encode())
            out('ok', 'cron lines for the sidecar: ' + '; '.join(cron_lines(home)))
        if 'cli' in want:
            bad += fs.deploy_cli(t, home, (NODE, (24,)))
    fs.save_step('site-deploy', bad == 0, f'docker {s["variant"]} apply: {counts}, {bad} problem(s)')
    out('ok' if not bad else 'next', 'deploy done: freedom_docker.py verify' if not bad else 'fix the failures and deploy again')
    return 1 if bad else 0


# ---------------------------------------------------------------- verify

class _Https(http.client.HTTPSConnection):
    """HTTPS to a fixed address with the site name for SNI, Host and the certificate check."""

    def __init__(self, name, addr, port, context, timeout=20):
        super().__init__(name, port, context=context, timeout=timeout)
        self._addr = addr

    def connect(self):
        sock = socket.create_connection((self._addr, self.port), self.timeout)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


def check_addr(s: dict) -> str:
    """Where verify connects: the published address, or 127.0.0.1 when Caddy listens on all addresses."""
    b = s.get('bind') or ''
    return '127.0.0.1' if b in ('', '0.0.0.0', '::', '[::]', 'localhost') else b.strip('[]')


def http_req(s: dict, path: str, method='GET', body: bytes = None, headers=None, ca: str = None, limit=200000):
    """(status, headers, body) through the published port of Caddy, as the site name; limit=None reads it all."""
    headers = dict(headers or {})
    try:
        if s['variant'] == 'B':
            ctx = ssl.create_default_context(cadata=ca) if ca else ssl.create_default_context()
            conn = _Https(s['host'], check_addr(s), s['httpsPort'], ctx)
        else:
            conn = http.client.HTTPConnection(check_addr(s), s['httpPort'], timeout=20)
            headers.setdefault('Host', urllib.parse.urlsplit(s['url']).netloc or 'localhost')
        conn.request(method, path, body=body, headers=headers)
        r = conn.getresponse()
        data = r.read(limit) if limit else r.read()
        conn.close()
        return r.status, {k.lower(): v for k, v in r.getheaders()}, data.decode('utf-8', 'replace')
    except (OSError, http.client.HTTPException, ssl.SSLError) as e:
        return 0, {}, f'{type(e).__name__}: {e}'


def cmd_verify(a) -> int:
    s = prepare_install()
    h = Host(s)
    bad = 0

    def check(ok, text):
        nonlocal bad
        out('ok' if ok else 'FAIL', text)
        bad += not ok

    sv = services(h)
    check(all(sv.get(x, {}).get('state') == 'running' for x in ('db', 'app', 'cron', 'caddy')),
          'containers: ' + ', '.join(f'{k} {v["state"]}{"/" + v["health"] if v["health"] else ""}' for k, v in sorted(sv.items())))
    cid = h.cid('app')
    if not cid:
        out('FAIL', 'no app container: freedom_docker.py up')
        return 1
    t = DockerTarget(h, cid)
    ca = None
    if s['variant'] == 'B' and s['tls'] == 'internal':
        rc, ca = h.text(['docker', 'exec', h.cid('caddy'), 'cat', '/data/caddy/pki/authorities/local/root.crt'])
        ca = ca if rc == 0 and 'BEGIN CERTIFICATE' in ca else None
        out('ok' if ca else 'warn', 'Caddy internal CA ' + ('read: the TLS chain is checked against it' if ca else 'not found yet'))
    code, hd, body = http_req(s, '/', ca=ca)
    check(code == 200 and 'id="root"' in body, f'CO Web index: HTTP {code}' + ('' if code == 200 else f' {body[:200]}')
          + (f', TLS ok ({s["host"]})' if s['variant'] == 'B' and code else ''))
    code, hd, body = http_req(s, '/auth/login.php', ca=ca)
    loc = hd.get('location', '')
    if code in (301, 302, 303) and 'accounts.google.com' in loc:
        q = urllib.parse.parse_qs(urllib.parse.urlsplit(loc).query)
        redir = (q.get('redirect_uri') or [''])[0]
        check(redir == s['url'] + '/auth/callback.php', f'sign-in page: HTTP {code} -> Google, redirect_uri {redir}')
    else:
        check(False, f'sign-in page: HTTP {code} {loc or body[:200]} (deploy --oauth-client FILE, or the web client in keyring)')
    code, hd, body = http_req(s, '/agent/ping.php', 'POST', b'{}', {'X-CO-Auth': '1', 'Content-Type': 'application/json'}, ca=ca)
    check(code in (200, 403) and body.lstrip().startswith('{'), f'agent/ping.php answers: HTTP {code} {body[:120]}'
          + (' (expected without a Google sign-in)' if code == 403 else ''))
    key = h.secret('push_key') or ''
    code, hd, body = http_req(s, '/agent/db.php', 'POST', b'{"op":"stats"}',
                              {'X-CO-Push-Key': key, 'Content-Type': 'application/json'}, ca=ca)
    check(code == 200 and body.lstrip().startswith('{'), f'db.php with the host key: HTTP {code} {body[:160]}')
    code2, _, body2 = http_req(s, '/agent/db.php', 'POST', b'{"op":"stats"}', {'Content-Type': 'application/json'}, ca=ca)
    check(code2 == 403, f'db.php without the key: HTTP {code2} (403 expected)')
    rc, o = t.run(f'php {HOME}/co-agent/db/members.php list')
    try:
        rows = json.loads(o[o.index('['):] if '[' in o else o)
        envs = [m.get('env') for m in (rows.values() if isinstance(rows, dict) else rows)]
    except ValueError:
        envs = []
    want = [m['env'] for m in aiwb_install.get('members', [])]
    check(rc == 0 and all(e in envs for e in want), f'members in the database: {", ".join(map(str, envs)) or o[-200:]}')
    bad += verify_cron(t, a.wait_cron)
    if a.agent:
        bad += verify_agent(t)
    if a.php_tests:
        bad += php_tests(h, t)
    out('next' if bad else 'ok', f'{bad} check(s) failed' if bad else
        f'stack {s["project"]} is green; next: Google sign-in on {s["url"]} (README: OAuth redirect URI)')
    fs.save_step('verify', bad == 0, f'docker {s["variant"]}: {bad} problem(s)')
    return 1 if bad else 0


def verify_cron(t: DockerTarget, wait: int) -> int:
    end = time.time() + max(0, wait)
    while True:
        rc, o = t.run(f'cat {HOME}/co-agent/state/freedom-cron.json 2>/dev/null')
        try:
            st = json.loads(o) if rc == 0 and o else {}
        except ValueError:
            st = {}
        good = {k: v for k, v in st.items() if v.get('ok', 0) > 0}
        if good or time.time() >= end:
            break
        time.sleep(10)
    for cmd, v in sorted(st.items()):
        out('info', f'cron: {v.get("ok")}/{v.get("runs")} ok, last rc {v.get("lastRc")} at {v.get("lastUtc")}: {cmd}'
                    + (f' -> {v.get("lastOutput")[-160:]}' if v.get('lastRc') and v.get('lastOutput') else ''))
    out('ok' if good else 'FAIL', 'cron sidecar: ' + (f'{len(good)} script(s) ran without errors' if good else
                                                    'no script finished with rc 0 yet (docker compose logs cron)'))
    return 0 if good else 1


def verify_agent(t: DockerTarget) -> int:
    """Start the site agent like ping.php / cron do (co_site_agent_kick) and read its first log lines; inside the stack
    nobody is signed in, so the agent waits for the owner's Google session and the CLIs report no login."""
    php = ("require '" + WEB + "/agent/lib.php'; $r = co_site_agent_running(); "
           "echo json_encode(['running' => $r, 'started' => $r ? false : co_site_agent_kick(false)]);")
    rc, o = t.run('php -r ' + fs._q(php))
    out('info', f'site agent kick: {o[-200:]}')
    alive = False
    for _ in range(10):
        time.sleep(2)
        rc, o = t.run('php -r ' + fs._q("require '" + WEB + "/agent/lib.php'; echo co_site_agent_running() ? 'yes' : 'no';"))
        if o.strip().endswith('yes'):
            alive = True
            break
    _, log = t.run(f'tail -n 3 {HOME}/co-agent/executor/logs/$(date -u +%Y-%m-%d).log 2>/dev/null; '
                   f'tail -n 3 {HOME}/co-agent/executor/logs/daemon.out 2>/dev/null; true')
    _, sess = t.run(f'test -f {HOME}/co-agent/secrets/google-session.json && echo yes || echo no')
    _, pid = t.run(f'cat {HOME}/co-agent/executor/agent.pid 2>/dev/null')
    _, env = t.run(f'tr "\\0" "\\n" < /proc/{pid.strip() or 0}/environ 2>/dev/null | grep -E "^(HOME|CO_DB_URL|NODE_EXTRA_CA_CERTS)=" ; true')
    out('ok' if alive else 'FAIL', f'site agent process: {"running, pid " + pid.strip() if alive else "not running"}; '
                                   f'log: {" | ".join(log.splitlines()[-3:]) or "-"}')
    if env:
        out('info', 'site agent env: ' + ', '.join(env.splitlines()))
    out('info', 'site agent: Google session ' + ('present' if sess.strip() == 'yes' else
                                                 'missing - not signed in (expected until the owner signs in to CO Web)'))
    rc, c = t.run(f'cd {HOME} && PATH={HOME}/co-agent/cli/node_modules/.bin:$PATH; '
                  'if command -v claude >/dev/null; then claude auth status --json 2>&1 | head -c 400; else echo "claude CLI not installed (deploy --parts cli)"; fi')
    out('info', f'site agent subscription: {c.strip()[:300]}')
    return 0 if alive else 1


def php_tests(h: Host, t: DockerTarget) -> int:
    """co-agent/tests in the app container against a throwaway database (own user, dropped afterwards)."""
    dbc = h.cid('db')
    name, user, pw = 'co_phptest', 'phptest', secrets.token_hex(16)
    root = ROOT_PWD + 'mariadb -uroot'
    sql = (f"DROP USER IF EXISTS '{user}'@'%'; CREATE USER '{user}'@'%' IDENTIFIED BY '{pw}'; "
           f"GRANT ALL ON `{name}`.* TO '{user}'@'%';")
    rc, o = h.text(['docker', 'exec', '-i', dbc, 'sh', '-c', root], stdin=sql.encode())
    if rc != 0:
        out('FAIL', f'php tests: throwaway database user: {o[-300:]}')
        return 1
    work = '/tmp/freedom-phptest'
    try:
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode='w') as tar:
            for sub in ('co-agent', 'co-web-auth', 'modules'):
                for f in (REPO / sub).rglob('*'):
                    rel = f.relative_to(REPO).as_posix()
                    if f.is_file() and '/node_modules/' not in rel and '/logs/' not in rel and f.name != 'desktop.ini':
                        tar.add(str(f), arcname='repo/' + rel, recursive=False)
            cases = REPO / 'co-web' / 'src' / 'diagnose.cases.json'
            if cases.is_file():
                tar.add(str(cases), arcname='repo/co-web/src/diagnose.cases.json')
            data = json.dumps({'host': 'db', 'db': name, 'user': user, 'password': pw}).encode()
            ti = tarfile.TarInfo('db.json')
            ti.size, ti.mode, ti.mtime = len(data), 0o600, time.time()
            tar.addfile(ti, io.BytesIO(data))
        rc, o = t.run(f'rm -rf {work} && mkdir -m 700 -p {work} && tar -x -C {work} -f - && '
                      f'/usr/local/lib/freedom/run-php-tests.sh {work}', stdin=buf.getvalue(), timeout=1800)
        for line in o.splitlines():
            if line.strip():
                out('info', 'php: ' + line)
        last = (o.strip().splitlines() or [''])[-1]
        out('ok' if rc == 0 else 'FAIL', f'PHP tests in the container: {last}')
        return 0 if rc == 0 else 1
    finally:
        t.run(f'rm -rf {work}')
        h.text(['docker', 'exec', '-i', dbc, 'sh', '-c', root],
               stdin=f"DROP DATABASE IF EXISTS `{name}`; DROP USER IF EXISTS '{user}'@'%';".encode())


# ---------------------------------------------------------------- backup, down

def cmd_backup(a) -> int:
    s = prepare_install()
    h = Host(s)
    dest = a.out or f'{h.stack()}/backups/{s["dbName"]}-{dt.datetime.now().strftime("%Y%m%d-%H%M%S")}.sql.gz'
    dbc = h.cid('db')
    if not dbc:
        out('FAIL', 'the db container is not running')
        return 1
    rc, o = h.sh('umask 077; mkdir -p "$(dirname "$2")" && docker exec "$1" sh -c '
                 '\'MYSQL_PWD="$(cat /run/secrets/db_root_password)" mariadb-dump -uroot --single-transaction --routines '
                 '--triggers --default-character-set=utf8mb4 "$MARIADB_DATABASE"\' | gzip > "$2.tmp" && mv -f "$2.tmp" "$2" '
                 '&& ls -l "$2"', dbc, dest, timeout=3600)
    out('ok' if rc == 0 else 'FAIL', f'backup: {o.splitlines()[-1] if o else rc}')
    return 0 if rc == 0 else 1


def cmd_down(a) -> int:
    s = prepare_install()
    h = Host(s)
    args = ['down', '--remove-orphans'] + (['-v'] if a.volumes else [])
    rc, o = h.compose(*args, timeout=600)
    out('ok' if rc == 0 else 'FAIL', f'compose {" ".join(args)}: {o.splitlines()[-1] if o else rc}')
    if a.rmi:
        rc2, o2 = h.text(['docker', 'image', 'rm', s['image']])
        out('ok' if rc2 == 0 else 'warn', f'image {s["image"]}: {o2.splitlines()[-1] if o2 else rc2}')
    if a.volumes:
        out('info', f'data volumes deleted; the stack folder with the secrets stays: {h.stack()}')
    return 0 if rc == 0 else 1


# ---------------------------------------------------------------- freedom_setup hook (variant B/C)

def site_deploy(a) -> int:
    """Entry for `freedom_setup.py site-deploy` when install.json variant is B or C (install/docker/freedom_setup-hook.patch):
    plan -> plan, --diff -> deploy --diff, --apply -> up (when not running) + deploy."""
    if not (a.apply or a.diff):
        return cmd_plan(a)
    ns = argparse.Namespace(parts=getattr(a, 'parts', None), build=getattr(a, 'build', False), diff=bool(a.diff),
                            oauth_client=None, rewrite_config=getattr(a, 'rewrite_config', False),
                            no_bak=getattr(a, 'no_bak', False), verbose=getattr(a, 'verbose', False))
    if a.apply:
        s = prepare_install()
        if not Host(s).cid('app') and cmd_up(argparse.Namespace(no_build=False)) != 0:
            return 1
    return cmd_deploy(ns)


def site_probe(a) -> int:
    """Entry for `freedom_setup.py site-probe` (B/C): Docker and compose on the Docker host, the state of the stack."""
    s = prepare_install()
    h = Host(s)
    good = docker_ok(h)
    if good:
        sv = services(h)
        out('info', f'stack {s["project"]}: ' + (', '.join(f'{k} {v["state"]}' for k, v in sorted(sv.items())) or 'not created yet (up)'))
    fs.save_step('site-probe', good, f'docker {s["variant"]}: ' + ('ok' if good else 'no Docker'))
    return 0 if good else 1


def site_verify(a) -> int:
    return cmd_verify(argparse.Namespace(php_tests=False, wait_cron=0, agent=False))


# ---------------------------------------------------------------- CLI

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog='freedom_docker.py', description='Freedom variants B/C: the server in Docker')
    sp = ap.add_subparsers(dest='cmd')
    sp.add_parser('plan')
    p = sp.add_parser('up')
    p.add_argument('--no-build', action='store_true')
    p = sp.add_parser('deploy')
    p.add_argument('--diff', action='store_true', help='read only: what would change')
    p.add_argument('--build', action='store_true', help='build co-web/dist first (Node 20+, npm)')
    p.add_argument('--parts', help=f'default {PARTS}; add cli for the CLIs of the site agent')
    p.add_argument('--oauth-client', help='web OAuth client JSON of Google Cloud (saved with the stack secrets)')
    p.add_argument('--rewrite-config', action='store_true')
    p.add_argument('--no-bak', action='store_true')
    p.add_argument('--verbose', action='store_true')
    p = sp.add_parser('verify')
    p.add_argument('--php-tests', action='store_true', help='co-agent/tests on a throwaway database in the stack')
    p.add_argument('--wait-cron', type=int, default=330, help='seconds to wait for a cron run with rc 0 (watch-cron.php runs '
                   'every 5 min; drive-sync.php fails until the owner signed in to Google)')
    p.add_argument('--agent', action='store_true', help='start the site agent and check that it runs')
    p = sp.add_parser('backup')
    p.add_argument('--out', help='file on the Docker host (default <stack>/backups/<db>-<time>.sql.gz)')
    p = sp.add_parser('down')
    p.add_argument('--volumes', action='store_true', help='also delete the database and site volumes')
    p.add_argument('--rmi', action='store_true', help='also delete the built app image')
    a = ap.parse_args(argv)
    fn = {'plan': cmd_plan, 'up': cmd_up, 'deploy': cmd_deploy, 'verify': cmd_verify, 'backup': cmd_backup,
          'down': cmd_down}.get(a.cmd)
    if not fn:
        print(__doc__)
        return 1
    try:
        return fn(a)
    except SystemExit:
        raise
    except Exception as e:
        out('FAIL', f'{a.cmd}: {type(e).__name__}: {e}')
        out('next', 'python install/freedom_setup.py report -> your own claude.ai / chatgpt.com chat or the forum')
        return 1


if __name__ == '__main__':
    sys.exit(main())
