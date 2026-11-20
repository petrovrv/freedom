"""Delivery of the morning briefing (section 13) to the channels the person chose.

The core knows where a push can go (push.php op=channels): the subscribed devices of CO and every notify hook of an
enabled module (a messenger, a speaker...). This module names none of them: it lists what the workbench has, suggests
the ready ones and keeps the person's choice in delivery.json next to the archive. Nothing goes anywhere until the
person chose; `tick` (schedule of the module, every few minutes) sends once a day after the chosen time.
"""
import json
import os
import urllib.request
from datetime import datetime, timedelta
from pathlib import Path

CONFIG = 'delivery.json'
TAG = 'concierge-mail-briefing'
BODY_MAX = 400  # push.php keeps 400 characters of a body
LINE_MAX = 160
LATE = timedelta(hours=3)  # a PC woken up later than this skips the day instead of a stale briefing
TITLE = {'en': 'Mail this morning', 'ru': 'Почта утром'}


def install_path():
    if os.environ.get('AIWB_INSTALL'):
        return Path(os.environ['AIWB_INSTALL'])
    if os.name == 'nt' and os.environ.get('LOCALAPPDATA'):
        return Path(os.environ['LOCALAPPDATA']) / 'AIWorkbench' / 'install.json'
    return Path.home() / '.config' / 'aiwb' / 'install.json'


def load_install():
    try:
        return json.loads(install_path().read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {}


def default_root():
    return install_path().parent / 'concierge-mail' / 'archive'


def default_env(install=None):
    """The env of the job that runs this, else the owner of the install."""
    if os.environ.get('AIWB_JOB_ENV'):
        return os.environ['AIWB_JOB_ENV']
    install = load_install() if install is None else install
    return next((m.get('env', '') for m in install.get('members', []) if isinstance(m, dict) and m.get('role') == 'owner'), '')


class Site:
    """push.php of the own site with the push key of this PC (keyring aiwb/co-push)."""

    def __init__(self, url=None, key=None, timeout=20):
        self.url = (url if url is not None else str((load_install().get('site') or {}).get('url') or '')).rstrip('/')
        self.key = key
        self.timeout = timeout

    def call(self, payload):
        if not self.url:
            raise RuntimeError('install.json: site.url is needed')
        if self.key is None:
            import keyring
            self.key = keyring.get_password('aiwb/co-push', 'default')
        if not self.key:
            raise RuntimeError('no key in keyring aiwb/co-push (account default)')
        req = urllib.request.Request(self.url + '/agent/push.php', data=json.dumps(payload, ensure_ascii=False).encode('utf-8'),
                                     method='POST', headers={'Content-Type': 'application/json', 'X-CO-Push-Key': self.key,
                                                             'User-Agent': 'freedom-concierge-mail/1'})
        with urllib.request.urlopen(req, timeout=self.timeout) as r:
            return json.loads(r.read().decode('utf-8') or '{}')

    def channels(self, env):
        return self.call({'op': 'channels', 'env': env}).get('channels', [])

    def notify(self, env, title, body, channels):
        return self.call({'op': 'notify', 'env': env, 'title': title, 'body': body, 'url': '/', 'tag': TAG, 'channels': channels})


def load(root):
    try:
        return json.loads((Path(root) / CONFIG).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {}


def save(root, cfg):
    path = Path(root) / CONFIG
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix('.tmp')
    tmp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding='utf-8')
    tmp.replace(path)
    return cfg


def channels(site, env):
    """What the workbench offers, with `suggested` = the ready ones (a person picks; nothing is chosen for them)."""
    found = site.channels(env)
    return {'env': env, 'channels': found, 'suggested': [c['id'] for c in found if c.get('ready')]}


def configure(root, site, env, chosen=None, at=None, lang=None, accounts=None, off=False):
    """Store the person's choice; channels are checked against what the workbench has now."""
    cfg = load(root)
    cfg['env'] = env or cfg.get('env') or ''
    if not cfg['env']:
        raise ValueError('no env: pass --env')
    warnings = []
    if chosen is not None:
        known = {c['id']: c for c in site.channels(cfg['env'])}
        unknown = [c for c in chosen if c not in known]
        if unknown:
            raise ValueError('unknown channels: ' + ', '.join(unknown) + '; known: ' + ', '.join(known))
        for c in chosen:
            if known[c].get('ready') is False:
                warnings.append(f"{c}: not ready ({known[c].get('note') or 'no subscribed device'})")
        cfg['channels'] = list(dict.fromkeys(chosen))
    if at is not None:
        datetime.strptime(at, '%H:%M')
        cfg['at'] = at
    if lang is not None:
        cfg['lang'] = lang
    if accounts is not None:
        cfg['accounts'] = accounts
    cfg['enabled'] = not off and bool(cfg.get('channels'))
    cfg.setdefault('at', '07:30')
    cfg.setdefault('lang', 'en')
    save(root, cfg)
    return {'config': cfg, 'warnings': warnings}


def body(lines, limit=BODY_MAX):
    """The briefing lines in at most `limit` characters, most urgent first; what does not fit as '… +N'."""
    lines = [ln if len(ln) <= LINE_MAX else ln[:LINE_MAX - 1] + '…' for ln in lines]
    for n in range(len(lines), -1, -1):
        rest = len(lines) - n
        text = '\n'.join(lines[:n] + ([f'… +{rest}'] if rest else []))
        if len(text) <= limit:
            return text
    return ''


def send(root, site, briefing, sync=None, dry_run=False, now=None):
    """Sync (when accounts are set), build the briefing and push it to the chosen channels."""
    cfg = load(root)
    if not cfg.get('channels'):
        return {'sent': False, 'reason': 'no channels chosen: deliver channels, then deliver set --channels ...'}
    synced = []
    if sync is not None:
        for account in cfg.get('accounts') or []:
            try:
                sync(account)
                synced.append(account)
            except Exception as e:  # noqa: BLE001 - a failed sync still gives yesterday's briefing, and says so
                synced.append(f'{account}: sync failed ({type(e).__name__})')
    lines = briefing(cfg.get('accounts') or None, cfg.get('lang', 'en'))
    lang = cfg.get('lang', 'en')
    text = body(lines)
    result = {'channels': cfg['channels'], 'title': TITLE.get(lang, TITLE['en']), 'body': text, 'synced': synced}
    if dry_run:
        return result | {'sent': False, 'reason': 'dry run'}
    result['push'] = site.notify(cfg['env'], result['title'], text, cfg['channels'])
    cfg['lastSent'] = (now or datetime.now()).strftime('%Y-%m-%d')
    save(root, cfg)
    return result | {'sent': True}


def due(cfg, now):
    """Once a day, from the chosen time until LATE after it."""
    if not cfg.get('enabled') or not cfg.get('channels'):
        return False
    at = datetime.combine(now.date(), datetime.strptime(cfg.get('at', '07:30'), '%H:%M').time())
    return cfg.get('lastSent') != now.strftime('%Y-%m-%d') and at <= now < at + LATE


def tick(root, site, briefing, sync=None, now=None):
    now = now or datetime.now()
    cfg = load(root)
    if not due(cfg, now):
        return {'sent': False, 'reason': 'not due'}
    return send(root, site, briefing, sync=sync, now=now)
