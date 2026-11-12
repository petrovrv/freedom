#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Upload AI Workbench JSON state from the Google Drive folder to the CO database API (agent/db.php).

Step 1 of the Drive -> MariaDB migration: Drive stays the source of truth; re-running is safe (unchanged records
are counted as 'same'). --verify compares table counts and per-chat event shas with the local files.
"""
import argparse
import base64
import gzip
import hashlib
import json
import sys
import time
from pathlib import Path

import keyring
import requests

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'install'))
import aiwb_install  # noqa: E402  Freedom F1: the site of this install

DEFAULT_URL = aiwb_install.get('site.url', '') + '/agent/db.php'
DEFAULT_ROOT = str(aiwb_install.workbench_root())
JOB_STATES = ('inbox', 'claimed', 'completed', 'failed', 'rejected', 'cancel')
HOST_KV = ('owner', 'limits', 'cloud-connectors')
KINDS = ('projects', 'chats', 'events', 'jobs', 'kv')
BAD = object()  # sentinel for unreadable / invalid JSON


class HttpFail(Exception):
    """Unrecoverable HTTP / transport failure."""


def sha(obj):
    # Same canonical form as PHP json_encode(JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)
    s = json.dumps(obj, ensure_ascii=False, separators=(',', ':'))
    return hashlib.sha256(s.encode()).hexdigest()


def subdirs(p):
    return sorted(d for d in p.iterdir() if d.is_dir()) if p.is_dir() else []


def seq_key(item):
    # Sort by numeric 'sequence' (missing last), then file name
    seq, name = item[0], item[1]
    try:
        return (0, float(seq), name)
    except (TypeError, ValueError):
        return (1, 0.0, name)


class Loader:
    def __init__(self):
        self.bad = []

    def read(self, p):
        if p.name.lower() == 'desktop.ini':
            return BAD
        try:
            return json.loads(p.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError) as e:
            self.bad.append((str(p), str(e)))
            return BAD


def collect(root, ld):
    projects, chats, kv, jobs = [], [], [], []
    events = {}  # chatId -> [(eventId, obj)]

    for pd in subdirs(root / 'projects'):
        pid = pd.name
        pf = pd / 'project.json'
        if pf.is_file():
            obj = ld.read(pf)
            if obj is not BAD:
                projects.append(obj)
                if isinstance(obj, dict) and obj.get('projectId'):
                    pid = str(obj['projectId'])
        sf = pd / 'schedules.json'
        if sf.is_file():
            data = ld.read(sf)
            if data is not BAD:
                kv.append({'scope': 'project:' + pid, 'name': 'schedules', 'data': data})

        for cd in subdirs(pd / 'chats'):
            cid = cd.name
            cf = cd / 'chat.json'
            if cf.is_file():
                obj = ld.read(cf)
                if obj is not BAD:
                    chats.append(obj)
                    if isinstance(obj, dict) and obj.get('chatId'):
                        cid = str(obj['chatId'])
            items = []
            ed = cd / 'events'
            if ed.is_dir():
                for f in ed.glob('*.json'):
                    if not f.is_file():
                        continue
                    obj = ld.read(f)
                    if obj is BAD:
                        continue
                    seq = obj.get('sequence') if isinstance(obj, dict) else None
                    eid = obj.get('eventId') if isinstance(obj, dict) else None
                    items.append((seq, f.name, str(eid or f.stem), obj))
            items.sort(key=seq_key)
            if items:
                events.setdefault(cid, []).extend((i[2], i[3]) for i in items)

    for hd in subdirs(root / 'transport' / 'hosts'):
        host = hd.name
        for stem in HOST_KV:
            f = hd / f'{stem}.json'
            if f.is_file():
                data = ld.read(f)
                if data is not BAD:
                    kv.append({'scope': 'host:' + host, 'name': stem, 'data': data})
        for state in JOB_STATES:
            for jd in subdirs(hd / state):
                rf = jd / 'request.json'
                if not rf.is_file():
                    continue
                req = ld.read(rf)
                if req is BAD:
                    continue
                res = None
                rs = jd / 'result.json'
                if rs.is_file():
                    r = ld.read(rs)
                    res = None if r is BAD else r
                jobs.append({'hostId': host, 'state': state, 'request': req, 'result': res})

    return projects, chats, events, jobs, kv


class Api:
    def __init__(self, url, key, timeout=60):
        self.url, self.timeout = url, timeout
        self.s = requests.Session()
        self.s.headers.update({'X-CO-Push-Key': key,
                               'Content-Type': 'application/json; charset=utf-8'})

    def call(self, payload):
        # gzip + base64 wrapper: the hosting WAF rejects plain bodies with code/SQL snippets in chat text
        raw = json.dumps(payload, ensure_ascii=False).encode('utf-8')
        body = json.dumps({'gz': base64.b64encode(gzip.compress(raw)).decode('ascii')}).encode('ascii')
        err = 'unknown error'
        for attempt in range(3):
            try:
                r = self.s.post(self.url, data=body, timeout=self.timeout)
            except (requests.ConnectionError, requests.Timeout) as e:
                err = f'{type(e).__name__}: {e}'
            else:
                if r.status_code >= 500:
                    err = f'HTTP {r.status_code}: {r.text[:500]}'
                elif r.status_code >= 400:
                    raise HttpFail(f'HTTP {r.status_code}: {r.text[:1000]}')
                else:
                    try:
                        return r.json()
                    except ValueError:
                        raise HttpFail(f'Invalid JSON response: {r.text[:500]}')
            if attempt < 2:
                time.sleep(2 * (attempt + 1))  # 2 s, then 4 s
        raise HttpFail(f'{payload.get("op")} failed after 3 attempts: {err}')


def put(api, kind, items, totals, rejected):
    if not items:
        return
    res = api.call({'op': 'put', kind: items})
    for k in ('inserted', 'updated', 'same'):
        totals[k] += int(res.get(k) or 0)
    for rj in res.get('rejected') or []:
        rejected.append(rj)
        print(f"REJECTED {rj.get('kind')} {rj.get('id')}: {rj.get('reason')}")


def verify(api, local_counts, events):
    problems = 0
    stats = api.call({'op': 'stats'})
    print('Verify counts (local / server):')
    for k in KINDS:
        srv = (stats.get(k) or {}).get('count')
        flag = ''
        if srv is None or int(srv) < local_counts[k]:
            flag, problems = '  <-- MISSING', problems + 1
        print(f'  {k:9} {local_counts[k]:>7} / {srv}{flag}')
    for cid, items in events.items():
        srv = api.call({'op': 'shas', 'kind': 'events', 'chatId': cid}).get('shas') or {}
        loc = {eid: sha(obj) for eid, obj in items}
        missing = sorted(set(loc) - set(srv))
        extra = sorted(set(srv) - set(loc))
        mism = sorted(e for e in loc if e in srv and srv[e] != loc[e])
        if missing or extra or mism:
            print(f'  chat {cid}: missing={len(missing)} extra={len(extra)} mismatch={len(mism)}')
            for label, ids in (('missing', missing), ('extra', extra), ('mismatch', mism)):
                if ids:
                    print(f'    {label}: {", ".join(ids[:10])}{" ..." if len(ids) > 10 else ""}')
            problems += len(missing) + len(mism)
    return problems


def main():
    sys.stdout.reconfigure(errors='replace')
    ap = argparse.ArgumentParser(description='Upload Drive JSON files to the CO database API.')
    ap.add_argument('--url', default=DEFAULT_URL)
    ap.add_argument('--root', default=DEFAULT_ROOT)
    ap.add_argument('--batch', type=int, default=200, help='events per call (max 500)')
    ap.add_argument('--dry-run', action='store_true', help='only count local records')
    ap.add_argument('--verify', action='store_true', help='compare counts and event shas after upload')
    args = ap.parse_args()

    root = Path(args.root)
    if not root.is_dir():
        print(f'Root not found: {root}')
        return 1

    ld = Loader()
    projects, chats, events, jobs, kv = collect(root, ld)
    counts = {'projects': len(projects), 'chats': len(chats),
              'events': sum(len(v) for v in events.values()),
              'jobs': len(jobs), 'kv': len(kv)}
    for path, err in ld.bad:
        print(f'BAD FILE {path}: {err}')
    print('Local: ' + ', '.join(f'{k}={v}' for k, v in counts.items())
          + f', bad_files={len(ld.bad)}')
    if args.dry_run:
        return 1 if ld.bad else 0

    try:
        key = (keyring.get_password('aiwb/co-push', 'default') or '').replace('\0', '').strip()
    except Exception as e:  # keyring backend errors
        print(f'Keyring error: {e}')
        key = ''
    if not key:
        print('Missing push key in keyring (aiwb/co-push, default).')
        return 1

    api = Api(args.url, key)
    totals = {'inserted': 0, 'updated': 0, 'same': 0}
    rejected = []
    batch = max(1, min(args.batch, 500))
    problems = 0
    try:
        put(api, 'projects', projects, totals, rejected)
        put(api, 'chats', chats, totals, rejected)
        put(api, 'kv', kv, totals, rejected)
        for n, (cid, items) in enumerate(events.items(), 1):
            objs = [obj for _, obj in items]
            for i in range(0, len(objs), batch):
                put(api, 'events', objs[i:i + batch], totals, rejected)
            if n % 10 == 0:
                print(f'  events: {n}/{len(events)} chats', file=sys.stderr)
        for i in range(0, len(jobs), 50):
            put(api, 'jobs', jobs[i:i + 50], totals, rejected)
        if args.verify:
            problems = verify(api, counts, events)
    except HttpFail as e:
        print(f'HTTP FAILURE: {e}')
        return 2

    print(f"Summary: inserted={totals['inserted']} updated={totals['updated']} "
          f"same={totals['same']} rejected={len(rejected)} bad_files={len(ld.bad)}"
          + (f' verify_problems={problems}' if args.verify else ''))
    return 1 if (rejected or ld.bad or problems) else 0


if __name__ == '__main__':
    sys.exit(main())
