"""Local verified checkpoints and lossless Maildir export. No cloud upload.

Checkpoint directories contain private plaintext: deploy only on protected local
storage. They are not the encrypted off-device backup required for production.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import tempfile


def _publish(destination, build):
    destination = Path(destination).resolve()
    if destination.exists():
        raise ValueError('Destination must not exist')
    destination.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='.mail-stage-', dir=destination.parent))
    try:
        result = build(stage)
        # Windows rename refuses an existing destination; never overwrite data.
        if destination.exists():
            raise ValueError('Destination appeared during export')
        os.rename(stage, destination)
        return result
    finally:
        if stage.exists():
            shutil.rmtree(stage)


def verify_checkpoint(root):
    root = Path(root).resolve()
    manifest = json.loads((root / 'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('format') != 'freedom-mail-checkpoint-1':
        raise ValueError('Unsupported checkpoint format')
    files = manifest.get('files', {})
    if 'mail.sqlite3' not in files:
        raise ValueError('Checkpoint database missing')
    for name, digest in files.items():
        if name != 'mail.sqlite3' and not re.fullmatch(r'blobs/[0-9a-f]{64}', name):
            raise ValueError('Invalid checkpoint path')
        path = root / name
        if path.is_symlink() or not path.is_file() or root not in path.resolve().parents:
            raise ValueError('Checkpoint file missing or unsafe')
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError('Checkpoint checksum mismatch')
    db = sqlite3.connect((root/'mail.sqlite3').as_uri() + '?mode=ro', uri=True)
    try:
        if db.execute('PRAGMA integrity_check').fetchall() != [('ok',)]:
            raise ValueError('Checkpoint database integrity failure')
        if db.execute('PRAGMA foreign_key_check').fetchall():
            raise ValueError('Checkpoint foreign key failure')
        # Redacted authentication stubs have no raw blob by design (section 11).
        sensitive = 'sensitive' in {r[1] for r in db.execute('PRAGMA table_info(messages)')}
        refs = db.execute('SELECT raw_sha256 FROM messages' + (' WHERE sensitive=0' if sensitive else '') + ' UNION SELECT sha256 FROM attachments').fetchall()
        if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='wf_draft_versions'").fetchone():
            for (payload,) in db.execute('SELECT attachments_json FROM wf_draft_versions'):
                refs.extend((item['sha256'],) for item in json.loads(payload))
        for (digest,) in refs:
            if f'blobs/{digest}' not in files or files[f'blobs/{digest}'] != digest:
                raise ValueError('Checkpoint blob missing')
        return {'verified': True, 'files': len(files), 'messages': db.execute('SELECT count(*) FROM messages').fetchone()[0]}
    finally:
        db.close()


def checkpoint(store, destination):
    def build(stage):
        db = sqlite3.connect(stage/'mail.sqlite3')
        try:
            store.db.backup(db)
        finally:
            db.close()
        (stage/'blobs').mkdir()
        # Immutable blobs: concurrent ingestion can only add files, never change them.
        for source in store.blobs.iterdir():
            if re.fullmatch('[0-9a-f]{64}', source.name):
                data = store.read_blob(source.name)
                (stage/'blobs'/source.name).write_bytes(data)
        files = {p.relative_to(stage).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                 for p in stage.rglob('*') if p.is_file()}
        (stage/'manifest.json').write_text(json.dumps({'format': 'freedom-mail-checkpoint-1', 'files': files}, indent=2), encoding='utf-8')
        return verify_checkpoint(stage)
    return _publish(destination, build)


def restore_checkpoint(source, destination):
    source = Path(source).resolve()
    verify_checkpoint(source)
    def build(stage):
        files = json.loads((source/'manifest.json').read_text(encoding='utf-8'))['files']
        for name in files:
            if name != 'mail.sqlite3' and not re.fullmatch(r'blobs/[0-9a-f]{64}', name):
                raise ValueError('Invalid checkpoint path')
        for name in [*files, 'manifest.json']:
            if (source/name).is_symlink() or source not in (source/name).resolve().parents:
                raise ValueError('Unsafe checkpoint path')
            target = stage/name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source/name, target)
        # Verify the copy too, before publishing, in case the source changed.
        return verify_checkpoint(stage)
    return _publish(destination, build)


def export_maildir(store, destination, account):
    def build(stage):
        for name in ('cur', 'new', 'tmp'):
            (stage/name).mkdir()
        rows = store.db.execute('SELECT id,raw_sha256 FROM messages WHERE account=? AND sensitive=0 ORDER BY id', (account,)).fetchall()
        for row in rows:
            (stage/'new'/f'{row[0]}.{row[1]}').write_bytes(store.read_blob(row[1]))
        redacted = store.db.execute('SELECT count(*) FROM messages WHERE account=? AND sensitive=1', (account,)).fetchone()[0]
        return {'exported': len(rows), 'skipped_redacted': redacted, 'format': 'Maildir'}
    return _publish(destination, build)
