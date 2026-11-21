"""Incremental encrypted Drive backups; credentials are supplied by the caller."""
from datetime import datetime, timedelta, timezone
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import tarfile
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, quote, urlsplit
from urllib.request import Request, urlopen

import archive
from seal import seal_file, open_file

API = 'https://www.googleapis.com/drive/v3/files'
UPLOAD = 'https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable'
BLOCK = 8 * 1024 * 1024
PACK_LIMIT = 512 * 1024 * 1024
STAMP = '%Y%m%dT%H%M%SZ'
DIGEST = re.compile(r'[0-9a-f]{64}')
BACKUP_NAME = re.compile(r'(db|manifest)-(\d{8}T\d{6}Z)\.(sqlite\.gz|json)\.sealed')


def _request(method, url, headers, body):
    try:
        with urlopen(Request(url, data=body, headers=headers, method=method), timeout=120) as reply:
            return reply.status, dict(reply.headers), reply.read()
    except HTTPError as exc:
        return exc.code, dict(exc.headers), b''
    except (URLError, OSError):
        raise RuntimeError('Drive transport failed') from None


class Drive:
    """drive.file scope. request returns (status, headers, bytes); replace in tests."""
    def __init__(self, get_token):
        self.get_token = get_token
        self.request = _request
        self.sleep = time.sleep

    def _call(self, method, url, headers=None, body=None, retry=True):
        for attempt in range(7):
            try:
                status, response_headers, data = self.request(method, url,
                    {**(headers or {}), 'Authorization': 'Bearer ' + self.get_token()}, body)
            except Exception:
                # Do not expose transport errors that might contain credentials.
                status, response_headers, data = 503, {}, b''
            response_headers = {k.lower(): v for k, v in response_headers.items()}
            if retry and (status == 429 or 500 <= status < 600) and attempt < 6:
                self.sleep(2 ** attempt)
                continue
            return status, response_headers, data

    def _json(self, method, url, payload=None):
        body = None if payload is None else json.dumps(payload).encode()
        status, _, data = self._call(method, url, {'Content-Type': 'application/json'}, body)
        if not 200 <= status < 300:
            raise RuntimeError('Drive request failed (%s)' % status)
        return json.loads(data) if data else {}

    def _list(self, query):
        result, token = [], None
        while True:
            params = {'q': query, 'fields': 'nextPageToken,files(id,name,size)', 'pageSize': 1000}
            if token:
                params['pageToken'] = token
            data = self._json('GET', API + '?' + urlencode(params))
            result.extend(data.get('files', []))
            token = data.get('nextPageToken')
            if not token:
                return result

    def folder(self, name):
        escaped = name.replace('\\', '\\\\').replace("'", "\\'")
        found = self._list("trashed=false and 'root' in parents and mimeType='application/vnd.google-apps.folder' "
                           "and name='%s' and appProperties has { key='freedomMailBackup' and value='1' }" % escaped)
        if found:
            return sorted(found, key=lambda f: f['id'])[0]['id']
        return self._json('POST', API, {'name': name, 'mimeType': 'application/vnd.google-apps.folder',
            'parents': ['root'], 'appProperties': {'freedomMailBackup': '1'}})['id']

    def list(self, folder_id):
        escaped = folder_id.replace('\\', '\\\\').replace("'", "\\'")
        return self._list("trashed=false and '%s' in parents" % escaped)

    def upload(self, path, name, folder_id):
        size = Path(path).stat().st_size
        status, headers, _ = self._call('POST', UPLOAD,
            {'Content-Type': 'application/json', 'X-Upload-Content-Type': 'application/octet-stream',
             'X-Upload-Content-Length': str(size)}, json.dumps({'name': name, 'parents': [folder_id]}).encode())
        if not 200 <= status < 300 or 'location' not in headers:
            raise RuntimeError('Drive upload initiation failed')
        url = headers['location']
        parsed = urlsplit(url)
        if parsed.scheme != 'https' or parsed.hostname not in ('www.googleapis.com', 'upload.googleapis.com') or parsed.username:
            raise ValueError('Unsafe upload location')
        offset, failures = 0, 0
        with open(path, 'rb') as stream:
            while True:
                stream.seek(offset)
                chunk = stream.read(BLOCK)
                content_range = 'bytes %s-%s/%s' % (offset, offset + len(chunk) - 1, size) if chunk else 'bytes */%s' % size
                status, headers, data = self._call('PUT', url,
                    {'Content-Length': str(len(chunk)), 'Content-Range': content_range}, chunk, retry=False)
                if status == 429 or 500 <= status < 600:
                    failures += 1
                    if failures > 6:
                        raise RuntimeError('Drive upload retry limit')
                    self.sleep(2 ** (failures - 1))
                    status, headers, data = self._call('PUT', url,
                        {'Content-Length': '0', 'Content-Range': 'bytes */%s' % size}, b'')
                if status in (200, 201):
                    return json.loads(data)['id']
                if status != 308:
                    raise RuntimeError('Drive upload failed (%s)' % status)
                match = re.fullmatch(r'bytes=0-(\d+)', headers.get('range', ''))
                next_offset = int(match[1]) + 1 if match else 0
                if not offset <= next_offset <= min(offset + len(chunk), size):
                    raise ValueError('Invalid Drive upload range')
                if next_offset == offset:
                    failures += 1
                    if failures > 6:
                        raise RuntimeError('Drive upload made no progress')
                else:
                    failures = 0
                offset = next_offset

    def download(self, file_id, dst_path):
        url = API + '/' + quote(file_id, safe='') + '?alt=media'
        # Range requests bound memory even for large packs.
        offset = 0
        with open(dst_path, 'wb') as dst:
            while True:
                status, headers, data = self._call('GET', url, {'Range': 'bytes=%s-%s' % (offset, offset + BLOCK - 1)})
                if status == 200 and offset == 0:
                    dst.write(data)
                    return
                match = re.fullmatch(r'bytes (\d+)-(\d+)/(\d+)', headers.get('content-range', ''))
                if status != 206 or not match or int(match[1]) != offset or int(match[2]) + 1 - offset != len(data) or not data:
                    raise RuntimeError('Invalid Drive download response')
                dst.write(data)
                offset += len(data)
                if offset == int(match[3]):
                    return

    def delete(self, file_id):
        self._json('DELETE', API + '/' + quote(file_id, safe=''))


def _hash(path):
    with open(path, 'rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def _atomic_json(path, data):
    path = Path(path)
    fd, temporary = tempfile.mkstemp(prefix='.backup-state-', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(data, stream, sort_keys=True)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def _keep(stamps, now):
    """Seven newest days, plus calendar weeks/months including the current one."""
    daily, weekly, monthly = {}, {}, {}
    week_start = now.date() - timedelta(days=now.weekday())
    month_index = now.year * 12 + now.month
    for stamp in sorted(set(stamps), reverse=True):
        dt = datetime.strptime(stamp, STAMP).replace(tzinfo=timezone.utc)
        daily.setdefault(dt.date(), stamp)
        age_weeks = (week_start - (dt.date() - timedelta(days=dt.weekday()))).days // 7
        if 0 <= age_weeks < 4:
            weekly.setdefault(dt.isocalendar()[:2], stamp)
        if 0 <= month_index - (dt.year * 12 + dt.month) < 12:
            monthly.setdefault((dt.year, dt.month), stamp)
    return set(list(daily.values())[:7]) | set(weekly.values()) | set(monthly.values())


def backup(store, public_b64, drive, state_path, folder='Freedom mail backup', now=None):
    now = now or datetime.now(timezone.utc)
    if now.tzinfo is None:
        now = now.replace(tzinfo=timezone.utc)
    now = now.astimezone(timezone.utc)
    stamp = now.strftime(STAMP)
    folder_id = drive.folder(folder)
    listing = drive.list(folder_id)
    names = {f['name'] for f in listing}
    db_name = 'db-%s.sqlite.gz.sealed' % stamp
    manifest_name = 'manifest-%s.json.sealed' % stamp
    if db_name in names or manifest_name in names:
        raise ValueError('Backup timestamp already exists; retry in a later second')
    state_path = Path(state_path)
    state = json.loads(state_path.read_text(encoding='utf-8')) if state_path.exists() else {}
    if state and state.get('public_key') != public_b64:
        raise ValueError('Backup recipient changed; use a new state and folder')
    packs = [p for p in state.get('packs', []) if p['name'] in names]
    known = {d for p in packs for d in p['digests']}
    counts = {'uploaded_files': 0, 'uploaded_bytes': 0, 'new_blobs': 0, 'new_packs': 0, 'deleted_files': 0,
              'manifest': manifest_name}
    # Never remove packs: old encrypted manifests cannot be inspected by this server.
    with tempfile.TemporaryDirectory(prefix='.mail-backup-', dir=state_path.parent) as temporary:
        tmp = Path(temporary)
        os.chmod(tmp, 0o700)
        def upload(path, name):
            drive.upload(path, name, folder_id)
            counts['uploaded_files'] += 1
            counts['uploaded_bytes'] += path.stat().st_size
        snapshot = tmp/'mail.sqlite3'
        db = sqlite3.connect(snapshot)
        try:
            store.db.backup(db)
        finally:
            db.close()
        with open(snapshot, 'rb') as src, gzip.open(tmp/'db.gz', 'wb') as dst:
            shutil.copyfileobj(src, dst)
        seal_file(tmp/'db.gz', tmp/'db.sealed', public_b64)
        upload(tmp/'db.sealed', db_name)
        pending = sorted(p for p in store.blobs.iterdir() if DIGEST.fullmatch(p.name) and p.name not in known)
        groups, group, size = [], [], 0
        for path in pending:
            if path.is_symlink() or not path.is_file():
                raise ValueError('Unsafe blob')
            member_size = 512 + ((path.stat().st_size + 511) // 512) * 512
            # tarfile pads the end to a 10 KiB record, including two zero blocks.
            if ((member_size + 1024 + 10239) // 10240) * 10240 > PACK_LIMIT:
                raise ValueError('Blob exceeds pack limit')
            if group and ((size + member_size + 1024 + 10239) // 10240) * 10240 > PACK_LIMIT:
                groups.append(group)
                group, size = [], 0
            group.append(path)
            size += member_size
        if group:
            groups.append(group)
        for number, group in enumerate(groups, 1):
            name = 'blobs-%s-%s.tar.sealed' % (stamp, number)
            if name in names:
                raise ValueError('Pack timestamp already exists')
            with tarfile.open(tmp/'pack.tar', 'w', format=tarfile.USTAR_FORMAT) as tar:
                for path in group:
                    if _hash(path) != path.name:
                        raise ValueError('Blob checksum mismatch')
                    info = tarfile.TarInfo(path.name)
                    info.size = path.stat().st_size
                    info.mode = 0o600
                    with path.open('rb') as src:
                        tar.addfile(info, src)
            seal_file(tmp/'pack.tar', tmp/'pack.sealed', public_b64)
            upload(tmp/'pack.sealed', name)
            packs.append({'name': name, 'sha256': _hash(tmp/'pack.sealed'), 'digests': [p.name for p in group]})
            counts['new_blobs'] += len(group)
            counts['new_packs'] += 1
        manifest = {'format': 'freedom-mail-backup-1', 'created': stamp,
                    'db': {'name': db_name, 'sha256': _hash(tmp/'db.gz')}, 'packs': packs}
        (tmp/'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        seal_file(tmp/'manifest.json', tmp/'manifest.sealed', public_b64)
        upload(tmp/'manifest.sealed', manifest_name)
        _atomic_json(state_path, {'public_key': public_b64, 'packs': packs, 'latest': manifest_name})
    listing = drive.list(folder_id)
    stamps = [m[2] for f in listing if (m := BACKUP_NAME.fullmatch(f['name'])) and m[1] == 'manifest']
    keep = _keep(stamps, now)
    for item in listing:
        match = BACKUP_NAME.fullmatch(item['name'])
        if match and match[2] not in keep:
            drive.delete(item['id'])
            counts['deleted_files'] += 1
    return counts


def restore(drive, private_b64, destination, folder='Freedom mail backup', manifest_name=None):
    if os.path.lexists(destination):
        raise ValueError('Destination must not exist')
    def build(stage):
        os.chmod(stage, 0o700)
        listing = drive.list(drive.folder(folder))
        by_name = {}
        for item in listing:
            if item['name'] in by_name:
                raise ValueError('Ambiguous duplicate Drive filename')
            by_name[item['name']] = item['id']
        candidates = sorted(n for n in by_name if re.fullmatch(r'manifest-\d{8}T\d{6}Z\.json\.sealed', n))
        selected = manifest_name or (candidates[-1] if candidates else None)
        if selected not in candidates:
            raise ValueError('Backup manifest not found')
        with tempfile.TemporaryDirectory(prefix='.restore-', dir=stage) as temporary:
            tmp = Path(temporary)
            os.chmod(tmp, 0o700)
            def fetch(name, output, digest=None):
                if name not in by_name:
                    raise ValueError('Backup file missing')
                drive.download(by_name[name], tmp/'sealed')
                if digest and _hash(tmp/'sealed') != digest:
                    raise ValueError('Pack checksum mismatch')
                open_file(tmp/'sealed', output, private_b64)
            fetch(selected, tmp/'manifest.json')
            manifest = json.loads((tmp/'manifest.json').read_text(encoding='utf-8'))
            if manifest.get('format') != 'freedom-mail-backup-1':
                raise ValueError('Unsupported backup format')
            fetch(manifest['db']['name'], tmp/'db.gz')
            if _hash(tmp/'db.gz') != manifest['db']['sha256']:
                raise ValueError('Database checksum mismatch')
            with gzip.open(tmp/'db.gz', 'rb') as src, open(stage/'mail.sqlite3', 'wb') as dst:
                shutil.copyfileobj(src, dst)
            (stage/'blobs').mkdir(mode=0o700)
            files = {'mail.sqlite3': _hash(stage/'mail.sqlite3')}
            for pack in manifest['packs']:
                expected = set(pack['digests'])
                if len(expected) != len(pack['digests']) or any(not DIGEST.fullmatch(d) for d in expected):
                    raise ValueError('Invalid pack digest list')
                fetch(pack['name'], tmp/'pack.tar', pack['sha256'])
                seen = set()
                with tarfile.open(tmp/'pack.tar', 'r:') as tar:
                    for member in tar:
                        if member.type not in (tarfile.REGTYPE, tarfile.AREGTYPE) or member.sparse is not None or not DIGEST.fullmatch(member.name) or member.name not in expected or member.name in seen:
                            raise ValueError('Unsafe or unexpected tar member')
                        target = stage/'blobs'/member.name
                        with tar.extractfile(member) as src, target.open('wb') as dst:
                            shutil.copyfileobj(src, dst)
                        if _hash(target) != member.name:
                            raise ValueError('Blob checksum mismatch')
                        seen.add(member.name)
                        files['blobs/' + member.name] = member.name
                if seen != expected:
                    raise ValueError('Pack blobs missing')
        (stage/'manifest.json').write_text(json.dumps({'format': 'freedom-mail-checkpoint-1', 'files': files}), encoding='utf-8')
        return {**archive.verify_checkpoint(stage), 'manifest': selected}
    return archive._publish(destination, build)
