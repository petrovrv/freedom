# SPDX-License-Identifier: AGPL-3.0-or-later
"""install.json - every name of one AI Workbench install in one file (Freedom F1, SPEC section 3).

The code holds no domains, e-mails, paths or project numbers of a particular install; they live in install.json:
  AIWB_INSTALL (path), else %LOCALAPPDATA%/AIWorkbench/install.json (Windows), else ~/.config/aiwb/install.json.
Template: install.example.json next to this file. The file has no secrets (those stay in the OS keyring).

Library:  from aiwb_install import load, get, owner      get('site.url'), get('members'), owner()['env']
          aiwb_install.ssh_connect()                        paramiko client to the hosting (site.ssh; Freedom F2)
CLI:      python aiwb_install.py check                      validate the file
          python aiwb_install.py get site.url               one value (JSON for objects)
          python aiwb_install.py server-config              text of ~/co-agent/config.php on the server
          python aiwb_install.py members-commands           members.php lines that put the members into the database
          python aiwb_install.py bridge-patch [CONFIG]      what Bridge config.json lacks (siteUrl, push.ownerEnv, environments)
          python aiwb_install.py bridge-config OUT [--force] [--posix|--windows]
                                                            a new Bridge config.json from bridge/config.json.example and
                                                            install.json (Freedom 0.4.0 installer; an existing OUT is kept
                                                            unless --force; Linux/macOS form by default off Windows)
          python aiwb_install.py ext-config OUT              site.js of the browser extension (the CO site; OUT = <extension folder>/site.js)
          python aiwb_install.py client-json OUT            the part apps embed at build time (site, members, Google project);
                                                            no file -> OUT is removed (the build gets empty defaults)
          python aiwb_install.py corporate-manifest [DEST]  legacy: copy modules/corporate/connector-manifest.json of this tree
                                                            where Bridge reads a gateway before its module is installed
                                                            (%LOCALAPPDATA%/AIWorkbench/connectors/corporate/manifest.json);
                                                            no such file in the tree -> DEST removed
"""
import json
import os
import re
import shlex
import sys
from pathlib import Path

_cache = None


def path() -> Path:
    if os.environ.get('AIWB_INSTALL'):
        return Path(os.environ['AIWB_INSTALL'])
    if os.name == 'nt' and os.environ.get('LOCALAPPDATA'):
        return Path(os.environ['LOCALAPPDATA']) / 'AIWorkbench' / 'install.json'
    return Path.home() / '.config' / 'aiwb' / 'install.json'


def load(fresh: bool = False) -> dict:
    global _cache
    if _cache is None or fresh:
        p = path()
        if not p.is_file():
            raise FileNotFoundError(f'install.json not found: {p} (copy install/install.example.json there and fill it, '
                                    'or set AIWB_INSTALL)')
        _cache = json.loads(p.read_text(encoding='utf-8-sig'))
    return _cache


_missing_warned = False


def get(key: str, default=None):
    """One value; without install.json the default (a warning once on stderr), so older setups keep starting."""
    global _missing_warned
    try:
        cur = load()
    except FileNotFoundError as e:
        if not _missing_warned:
            _missing_warned = True
            print(f'aiwb_install: {e}; using defaults', file=sys.stderr)
        return default
    for part in key.split('.'):
        if not isinstance(cur, dict) or part not in cur:
            return default
        cur = cur[part]
    return cur


def workbench_root() -> Path:
    """The AI-Workbench folder in Drive: AIWB_ROOT, else drive.root / drive.workbench; the default root is the mount of
    Google Drive for desktop."""
    if os.environ.get('AIWB_ROOT'):
        return Path(os.environ['AIWB_ROOT'])
    return Path(get('drive.root') or 'G:\\My Drive') / (get('drive.workbench') or 'AI-Workbench')


def owner() -> dict:
    return next((m for m in get('members', []) if m.get('role') == 'owner'), {})


def check(d: dict) -> list:
    errs = []
    url = (d.get('site') or {}).get('url', '')
    variant = str(d.get('variant') or 'A').upper()
    if variant not in ('A', 'B', 'C'):
        errs.append('variant must be "A" (shared hosting), "B" (VPS/NAS with Docker) or "C" (only a computer)')
    # Freedom F3b: variant C without Tailscale may live on http://localhost:<port> (Google accepts plain-HTTP OAuth
    # redirects and browsers keep Secure / __Host- cookies only for the name localhost, not for 127.0.0.1)
    local_ok = variant == 'C' and re.match(r'^http://localhost:\d+$', url or '')
    if not re.match(r'^https://[a-z0-9.-]+(:\d+)?$', url or '') and not local_ok:
        errs.append('site.url must be https://host without a trailing slash'
                    + (' (variant C also: http://localhost:PORT, not 127.0.0.1)' if variant == 'C' else ''))
    ms = d.get('members') or []
    if sum(1 for m in ms if m.get('role') == 'owner') != 1:
        errs.append('members: exactly one role "owner"')
    seen = set()
    for m in ms:
        if not re.match(r'^[a-z0-9-]{1,40}$', m.get('env', '')):
            errs.append(f"members: bad env {m.get('env')!r}")
        if '@' not in m.get('email', ''):
            errs.append(f"members: bad email of {m.get('env')}")
        if not m.get('name'):
            errs.append(f"members: no name for {m.get('env')}")
        if m.get('env') in seen:
            errs.append(f"members: env {m.get('env')} twice")
        seen.add(m.get('env'))
    if not (d.get('drive') or {}).get('root'):
        errs.append('drive.root is required')
    return errs


class HostKeyUnknown(Exception):
    """The hosting answered with a key that is in neither known_hosts; .fingerprint = SHA256:... to compare."""

    def __init__(self, host: str, fingerprint: str):
        super().__init__(f'unknown SSH host key of {host}: {fingerprint}')
        self.host, self.fingerprint = host, fingerprint


def known_hosts_path() -> Path:
    return path().parent / 'ssh_known_hosts'


def ssh_connect(accept_new=False):
    """paramiko SSHClient to the hosting of install.json site.ssh {host, port, user, keyFile} (Freedom F2; paramiko,
    because the stock OpenSSH client is blocked on some PCs). Passphrase of the key: keyring aiwb/site-ssh, account = user.
    Host keys: the system known_hosts and ssh_known_hosts next to install.json; an unknown key raises HostKeyUnknown
    unless accept_new is True or the SHA256 fingerprint string the user confirmed - then it is saved (trust on first use).
    ValueError when site.ssh is incomplete."""
    import base64
    import hashlib
    import paramiko
    ssh = get('site.ssh') or {}
    if not ssh.get('host') or not ssh.get('user'):
        raise ValueError('install.json: site.ssh.host and site.ssh.user are needed')
    try:
        import keyring
        passphrase = keyring.get_password('aiwb/site-ssh', ssh['user'])
    except Exception:
        passphrase = None
    kh = known_hosts_path()

    class _FirstUse(paramiko.MissingHostKeyPolicy):
        def missing_host_key(self, client, hostname, key):
            fp = 'SHA256:' + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('=')
            if accept_new is not True and accept_new != fp:
                raise HostKeyUnknown(hostname, fp)
            client.get_host_keys().add(hostname, key.get_name(), key)
            kh.parent.mkdir(parents=True, exist_ok=True)
            client.save_host_keys(str(kh))

    c = paramiko.SSHClient()
    c.load_system_host_keys()
    if kh.is_file():
        c.load_host_keys(str(kh))
    c.set_missing_host_key_policy(_FirstUse())
    c.connect(hostname=ssh['host'], port=int(ssh.get('port') or 22), username=ssh['user'],
              key_filename=ssh.get('keyFile') or None, passphrase=passphrase, timeout=25, banner_timeout=25,
              auth_timeout=25, look_for_keys=not ssh.get('keyFile'), allow_agent=not ssh.get('keyFile'))
    return c


def _php(v) -> str:
    return "'" + str(v).replace('\\', '\\\\').replace("'", "\\'") + "'"


def server_config() -> str:
    lines = ['<?php', '// Server part of install.json (Freedom F1), written by install/aiwb_install.py server-config.', 'return [']
    lines.append(f"    'web_dir' => {_php(get('site.webRoot', '').rstrip('/') + '/agent')},")
    if get('site.url'):
        lines.append(f"    'site_url' => {_php(get('site.url'))},")
    if get('drive.root'):
        lines.append(f"    'drive_root' => {_php(get('drive.root'))},")
    if get('canvas.publicUrl'):
        lines.append(f"    'canvas_url' => {_php(get('canvas.publicUrl').rstrip('/'))},")
    lines.append('];')
    return '\n'.join(lines) + '\n'


def members_commands() -> list:
    out = []
    for i, m in enumerate(get('members', [])):
        args = ['php', '~/co-agent/db/members.php', 'set', m['env'], m['email'], m['name'],
                '--owner' if m.get('role') == 'owner' else '--member', f'--sort={i}']
        if m.get('siteAgent'):
            args.append('--site-agent')
        if m.get('color'):
            args.append(f"--color={m['color']}")
        out.append(' '.join(a if a.startswith('~/') else shlex.quote(a) for a in args))
    return out


def bridge_patch(cfg: dict) -> dict:
    """Keys Bridge config.json lacks compared with install.json (nothing is removed or overwritten)."""
    patch = {}
    if not cfg.get('siteUrl'):
        patch['siteUrl'] = get('site.url')
    own = owner().get('env')
    if own and not (cfg.get('push') or {}).get('ownerEnv'):
        patch['push.ownerEnv'] = own
    envs = cfg.get('chatProjectEnvironments') or {}
    missing = [m['env'] for m in get('members', []) if m['env'] not in envs]
    if missing:
        patch['chatProjectEnvironments+'] = missing
    return patch


def posix_root() -> str:
    """Workbench root of a Linux/macOS Bridge (bridge/install.sh): AIWB_ROOT or drive.root/drive.workbench when they are
    absolute POSIX paths, else ~/AIWorkbench (a server without Google Drive; install.json copied from Windows)."""
    for cand in (os.environ.get('AIWB_ROOT') or '',
                 (get('drive.root') or '').rstrip('/') + '/' + (get('drive.workbench') or 'AI-Workbench') if get('drive.root') else ''):
        if cand.startswith('/'):
            return cand
    return Path.home().as_posix() + '/AIWorkbench'


def bridge_config(posix: bool = None) -> dict:
    """A Bridge config.json of this install: bridge/config.json.example with the names of install.json (site, drive,
    members, Google project); the one test project of the template for the owner, no extra company mail domains. No install.json: FileNotFoundError.
    posix (default: not Windows): the .NET 8 Bridge of Linux/macOS - agent executables without .exe, the workbench root
    from posix_root() as driveRoot and driveReadyPath, the Drive context root only when drive.root is a POSIX path."""
    if posix is None:
        posix = os.name != 'nt'
    if posix:
        return _posix_bridge_config(bridge_config(False))
    d = load()
    tpl = Path(__file__).resolve().parents[1] / 'bridge' / 'config.json.example'
    cfg = json.loads(tpl.read_text(encoding='utf-8-sig'))
    site = (get('site.url') or '').rstrip('/')
    drive = get('drive.root') or ''
    cfg['hostId'] = d.get('hostId') or 'home-main'
    cfg['driveRoot'] = str(workbench_root())
    cfg['driveReadyPath'] = drive
    cfg['siteUrl'] = site
    cfg['push'] = {**cfg.get('push', {}), 'url': '', 'ownerEnv': owner().get('env', '')}
    cfg['dbQueue'] = {'enabled': True, 'url': '', 'pollSeconds': 10}   # urls from siteUrl (Bridge NormalizeSite)
    cfg['desktopUpdatesDir'] = '%LOCALAPPDATA%\\AIWorkbenchBridge\\desktop-updates'   # freedom_setup.py desktop-update
    tpl_env = next(iter((cfg.get('chatProjectEnvironments') or {}).values()), {'allowedAgents': ['claude', 'codex'], 'autoExport': True})
    cfg['chatProjectEnvironments'] = {m['env']: dict(tpl_env) for m in get('members', []) if m.get('env')}
    roots = cfg.get('contextRoots') or {}
    if 'gdrive' in roots:
        roots['gdrive']['path'] = drive
    cfg['devices'] = {**cfg.get('devices', {}), 'corporateDomains': []}
    # Bridge needs one project profile: the template's test project, for the owner, in this workbench
    own = owner().get('env', '')
    cfg['projects'] = {pid: {**prof, 'environmentId': own, 'workspace': str(workbench_root() / 'projects' / pid / 'workspace')}
                       for pid, prof in list((cfg.get('projects') or {}).items())[:1]}
    cfg['google'] = {**cfg.get('google', {}), 'projectId': str(get('google.projectNumber') or '')}
    return cfg


def _posix_bridge_config(cfg: dict) -> dict:
    root = posix_root()
    drive = get('drive.root') or ''
    cfg['driveRoot'] = root
    cfg['driveReadyPath'] = root
    for agent in (cfg.get('agents') or {}).values():
        exe = agent.get('executable') or ''
        if exe.lower().endswith('.exe') and '\\' not in exe and '/' not in exe:
            agent['executable'] = exe[:-4]
    roots = cfg.get('contextRoots') or {}
    if 'gdrive' in roots:
        if drive.startswith('/'):
            roots['gdrive']['path'] = drive
        else:
            del roots['gdrive']
    if not drive.startswith('/'):
        cfg['chatFolderWaitSeconds'] = 0   # a local root: no chat folder comes through Drive for desktop (Bridge 1.19.0)
    cfg['projects'] = {pid: {**prof, 'workspace': f'{root}/projects/{pid}/workspace'}
                       for pid, prof in (cfg.get('projects') or {}).items()}
    return cfg


def client_json() -> dict:
    """What CO Desktop and CO Resident embed: no ssh, paths or server layout. No install.json: FileNotFoundError."""
    load()
    return {'schema': 1, 'site': {'url': get('site.url', '')}, 'google': {'projectNumber': get('google.projectNumber', '')},
            'members': [{k: m[k] for k in ('env', 'email', 'name', 'role') if k in m} for m in get('members', [])]}


def main(argv):
    cmd = argv[1] if len(argv) > 1 else 'check'
    if cmd == 'check':
        errs = check(load())
        print('\n'.join(errs) if errs else f'ok: {path()}')
        return 1 if errs else 0
    if cmd == 'get' and len(argv) == 3:
        v = get(argv[2])
        print(v if isinstance(v, str) else json.dumps(v, ensure_ascii=False))
        return 0 if v is not None else 1
    if cmd == 'server-config':
        sys.stdout.write(server_config())
        return 0
    if cmd == 'members-commands':
        print('\n'.join(members_commands()))
        return 0
    if cmd == 'ext-config' and len(argv) == 3:
        Path(argv[2]).write_text('// The CO site of this install (generated by install/aiwb_install.py ext-config).\n'
                                 f"self.CO_SITE = {json.dumps(get('site.url', ''))};\n", encoding='utf-8')
        return 0
    if cmd == 'client-json' and len(argv) == 3:
        out = Path(argv[2])
        try:
            data = json.dumps(client_json(), ensure_ascii=False, indent=2) + '\n'
        except FileNotFoundError as e:
            # no install.json: drop the output of an earlier build, so no other install's names get embedded
            print(f'client-json: {e}', file=sys.stderr)
            out.unlink(missing_ok=True)
            return 0
        out.parent.mkdir(parents=True, exist_ok=True)
        if not out.is_file() or out.read_text(encoding='utf-8') != data:
            out.write_text(data, encoding='utf-8')
        return 0
    if cmd == 'corporate-manifest':
        # Freedom F1 5b: the corporate connector's manifest next to install.json (Bridge 1.13+ reads it there)
        src = Path(__file__).resolve().parents[1] / 'modules' / 'corporate' / 'connector-manifest.json'
        dest = Path(argv[2]) if len(argv) > 2 else path().parent / 'connectors' / 'corporate' / 'manifest.json'
        if not src.is_file():
            dest.unlink(missing_ok=True)
            print(f'no corporate connector in this tree: {dest} removed')
            return 0
        json.loads(src.read_text(encoding='utf-8-sig'))
        dest.parent.mkdir(parents=True, exist_ok=True)
        data = src.read_bytes()
        if not dest.is_file() or dest.read_bytes() != data:
            dest.write_bytes(data)
        print(f'ok: {dest}')
        return 0
    if cmd == 'bridge-config' and len(argv) >= 3:
        out = Path(argv[2])
        if out.is_file() and '--force' not in argv:
            print(f'kept: {out} exists (bridge-patch shows what it lacks; --force writes a new one)')
            return 0
        cfg = bridge_config(True if '--posix' in argv else False if '--windows' in argv else None)
        out.parent.mkdir(parents=True, exist_ok=True)
        if out.is_file():
            out.with_suffix('.json.bak').write_bytes(out.read_bytes())
        out.write_text(json.dumps(cfg, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(f'ok: {out}')
        return 0
    if cmd == 'bridge-patch':
        p = Path(argv[2]) if len(argv) > 2 else Path(os.environ.get('LOCALAPPDATA', '')) / 'AIWorkbenchBridge' / 'config.json'
        print(json.dumps(bridge_patch(json.loads(p.read_text(encoding='utf-8-sig'))), ensure_ascii=False, indent=2))
        return 0
    print(__doc__)
    return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
