import hashlib
import io
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import archive
import hubbackup
from hubbackup import Drive, backup, restore
from mailstore import Store
import seal
from test_mailstore import message


class FakeDrive:
    def __init__(self):
        self.files = {}
        self.next_id = 0
        self.uploads = []
        self.fail = None

    def folder(self, name):
        return 'folder'

    def list(self, folder_id):
        return [{'id': key, 'name': name, 'size': len(data)} for key, (name, data) in self.files.items()]

    def upload(self, path, name, folder_id):
        if self.fail and name.startswith(self.fail):
            raise RuntimeError('Simulated upload failure')
        self.next_id += 1
        key = str(self.next_id)
        self.files[key] = (name, Path(path).read_bytes())
        self.uploads.append(name)
        return key

    def download(self, file_id, dst_path):
        Path(dst_path).write_bytes(self.files[file_id][1])

    def delete(self, file_id):
        del self.files[file_id]


class SealTests(unittest.TestCase):
    def setUp(self):
        self.private, self.public = seal.generate_identity()

    def encode(self, data):
        out = io.BytesIO()
        seal.seal_stream(io.BytesIO(data), out, self.public)
        return out.getvalue()

    def decode(self, data, private=None):
        out = io.BytesIO()
        seal.open_stream(io.BytesIO(data), out, private or self.private)
        return out.getvalue()

    def test_roundtrip(self):
        self.assertEqual(seal.public_from_private(self.private), self.public)
        for data in (b'', b'hello', b'a' * seal.CHUNK, b'abc' * seal.CHUNK):
            with self.subTest(size=len(data)):
                self.assertEqual(self.decode(self.encode(data)), data)

    def test_reject_corruption(self):
        wire = self.encode(b'a' * seal.CHUNK + b'b' * seal.CHUNK + b'end')
        header, chunks, pos = wire[:38], [], 38
        while pos < len(wire):
            length = int.from_bytes(wire[pos:pos+4], 'big')
            chunks.append(wire[pos:pos+4+length])
            pos += 4 + length
        flipped = bytearray(wire)
        flipped[100] ^= 1
        for damaged in (bytes(flipped), header + b''.join(chunks[:-1]),
                        header + chunks[1] + chunks[0] + chunks[2], wire + b'x', wire[:-1], wire[:20]):
            with self.subTest(size=len(damaged)):
                with self.assertRaises(ValueError):
                    self.decode(damaged)
        with self.assertRaises(ValueError):
            self.decode(wire, seal.generate_identity()[0])

    def test_atomic_file_failure_and_short_reads(self):
        class Short(io.BytesIO):
            def read(self, size=-1):
                return super().read(min(size, 13) if size >= 0 else 13)
        out = io.BytesIO()
        seal.seal_stream(Short(b'payload' * 100), out, self.public)
        plain = io.BytesIO()
        seal.open_stream(Short(out.getvalue()), plain, self.private)
        self.assertEqual(plain.getvalue(), b'payload' * 100)
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            root = Path(directory)
            (root/'in').write_bytes(out.getvalue()[:-1])
            (root/'out').write_bytes(b'unchanged')
            with self.assertRaises(ValueError):
                seal.open_file(root/'in', root/'out', self.private)
            self.assertEqual((root/'out').read_bytes(), b'unchanged')
            self.assertEqual({p.name for p in root.iterdir()}, {'in', 'out'})


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.root = Path(self.temp.name)
        self.store = Store(self.root/'store')
        self.private, self.public = seal.generate_identity()
        self.drive = FakeDrive()
        self.state = self.root/'state.json'
        self.now = datetime(2026, 1, 1, 12, tzinfo=timezone.utc)
        for number in range(3):
            mail = message(mid='<%s@example.org>' % number, body='Engineering discussion %s' % number)
            if number == 0:
                mail.add_attachment(b'attachment bytes', maintype='application', subtype='octet-stream', filename='data.bin')
            self.store.ingest('owner', mail.as_bytes())

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def run_backup(self, day=0):
        return backup(self.store, self.public, self.drive, self.state, now=self.now + timedelta(days=day))

    def test_roundtrip_and_incremental(self):
        first = self.run_backup()
        self.assertEqual(first['new_blobs'], 4)
        self.assertEqual(first['new_packs'], 1)
        self.store.ingest('owner', message(mid='<new@example.org>', body='New meeting notes').as_bytes())
        second = self.run_backup(1)
        self.assertEqual((second['new_blobs'], second['new_packs']), (1, 1))
        third = self.run_backup(2)
        self.assertEqual((third['new_blobs'], third['uploaded_files']), (0, 2))
        result = restore(self.drive, self.private, self.root/'restored')
        self.assertEqual(result['messages'], 4)
        self.assertTrue(archive.verify_checkpoint(self.root/'restored')['verified'])
        for source in self.store.blobs.iterdir():
            self.assertEqual(source.read_bytes(), (self.root/'restored'/'blobs'/source.name).read_bytes())
        old = restore(self.drive, self.private, self.root/'old', manifest_name=first['manifest'])
        self.assertEqual(old['messages'], 3)
        with self.assertRaises(ValueError):
            restore(self.drive, self.private, self.root/'restored')
        with self.assertRaises(ValueError):
            restore(self.drive, seal.generate_identity()[0], self.root/'bad')
        self.assertFalse((self.root/'bad').exists())

    def test_missing_pack_and_failed_upload(self):
        self.run_backup()
        for key, (name, _) in list(self.drive.files.items()):
            if name.startswith('blobs-'):
                self.drive.delete(key)
        self.assertEqual(self.run_backup(1)['new_blobs'], 4)
        before = self.state.read_bytes()
        self.drive.fail = 'manifest-'
        with self.assertRaises(RuntimeError):
            self.run_backup(2)
        self.assertEqual(self.state.read_bytes(), before)
        self.assertFalse(list(self.root.glob('.mail-backup-*')))
        self.drive.fail = None
        self.assertTrue(restore(self.drive, self.private, self.root/'restored')['verified'])

    def test_rotation_sixty_days(self):
        for day in range(60):
            self.run_backup(day)
        # March 1: daily Feb 23..Mar 1; weekly Feb 8,15,22; monthly Jan 31.
        dates = {'20260223', '20260224', '20260225', '20260226', '20260227',
                 '20260228', '20260301', '20260208', '20260215', '20260222', '20260131'}
        manifests = {name[9:17] for name, _ in self.drive.files.values() if name.startswith('manifest-')}
        databases = {name[3:11] for name, _ in self.drive.files.values() if name.startswith('db-')}
        self.assertEqual(manifests, dates)
        self.assertEqual(databases, dates)
        self.assertEqual(sum(name.startswith('blobs-') for name, _ in self.drive.files.values()), 1)
        self.assertTrue(restore(self.drive, self.private, self.root/'restored')['verified'])

    def test_calendar_retention_crosses_year(self):
        now = datetime(2026, 1, 5, 12, tzinfo=timezone.utc)
        stamps = [(now - timedelta(days=n)).strftime(hubbackup.STAMP) for n in range(500)]
        kept = hubbackup._keep(stamps, now)
        self.assertIn('20250228T120000Z', kept)
        self.assertNotIn('20250131T120000Z', kept)
        self.assertIn('20251221T120000Z', kept)
        self.assertNotIn('20251214T120000Z', kept)
        # Only the newest snapshot on a day occupies its daily slot.
        kept = hubbackup._keep(stamps + ['20260105T110000Z'], now)
        self.assertNotIn('20260105T110000Z', kept)

    def test_pack_split(self):
        with patch.object(hubbackup, 'PACK_LIMIT', 10240):
            for n in range(3):
                self.store._blob(bytes([n]) * 6000)
            result = self.run_backup()
        self.assertGreater(result['new_packs'], 1)
        self.assertTrue(restore(self.drive, self.private, self.root/'restored')['verified'])

    def change_pack(self, member_name, payload):
        self.run_backup()
        pack_id = next(k for k, (n, _) in self.drive.files.items() if n.startswith('blobs-'))
        manifest_id = next(k for k, (n, _) in self.drive.files.items() if n.startswith('manifest-'))
        out = io.BytesIO()
        seal.open_stream(io.BytesIO(self.drive.files[manifest_id][1]), out, self.private)
        manifest = json.loads(out.getvalue())
        tar_bytes = io.BytesIO()
        with tarfile.open(fileobj=tar_bytes, mode='w') as tar:
            info = tarfile.TarInfo(member_name)
            info.size = len(payload)
            tar.addfile(info, io.BytesIO(payload))
        encrypted = io.BytesIO()
        seal.seal_stream(io.BytesIO(tar_bytes.getvalue()), encrypted, self.public)
        wire = encrypted.getvalue()
        self.drive.files[pack_id] = (self.drive.files[pack_id][0], wire)
        manifest['packs'][0]['sha256'] = hashlib.sha256(wire).hexdigest()
        if hubbackup.DIGEST.fullmatch(member_name):
            manifest['packs'][0]['digests'] = [member_name]
        encrypted = io.BytesIO()
        seal.seal_stream(io.BytesIO(json.dumps(manifest).encode()), encrypted, self.public)
        self.drive.files[manifest_id] = (self.drive.files[manifest_id][0], encrypted.getvalue())

    def test_wrong_blob_digest(self):
        self.change_pack('a' * 64, b'wrong content')
        with self.assertRaisesRegex(ValueError, 'Blob checksum'):
            restore(self.drive, self.private, self.root/'bad')
        self.assertFalse((self.root/'bad').exists())

    def test_tar_path_traversal(self):
        self.change_pack('../escape', b'unsafe')
        with self.assertRaisesRegex(ValueError, 'Unsafe'):
            restore(self.drive, self.private, self.root/'bad')
        self.assertFalse((self.root/'escape').exists())


class DriveTests(unittest.TestCase):
    def test_resumable_retry_and_partial_range(self):
        drive = Drive(lambda: 'fake-token')
        drive.sleep = lambda seconds: None
        received, calls = bytearray(), []
        failed = False
        def request(method, url, headers, body):
            nonlocal failed
            self.assertEqual(headers['Authorization'], 'Bearer fake-token')
            calls.append((method, headers.get('Content-Range')))
            if method == 'POST':
                return 200, {'Location': 'https://www.googleapis.com/upload/session'}, b''
            if not failed:
                received.extend(body[:3])
                failed = True
                return 503, {}, b''
            if headers['Content-Range'].startswith('bytes */'):
                return 308, {'Range': 'bytes=0-%s' % (len(received) - 1)}, b''
            start = int(headers['Content-Range'].split()[1].split('-')[0])
            self.assertEqual(start, len(received))
            received.extend(body)
            return 200, {}, b'{"id":"uploaded"}'
        drive.request = request
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            path = Path(directory)/'file'
            path.write_bytes(b'0123456789')
            self.assertEqual(drive.upload(path, 'name', 'folder'), 'uploaded')
        self.assertEqual(received, b'0123456789')
        self.assertEqual(len(calls), 4)

    def test_upload_chunks_and_rate_limit(self):
        drive = Drive(lambda: 'fake-token')
        sleeps, ranges = [], []
        drive.sleep = sleeps.append
        attempts = 0
        def request(method, url, headers, body):
            nonlocal attempts
            if method == 'POST':
                attempts += 1
                if attempts == 1:
                    return 429, {}, b''
                return 200, {'Location': 'https://www.googleapis.com/upload/session'}, b''
            ranges.append(headers['Content-Range'])
            self.assertLessEqual(len(body), 4)
            if len(ranges) < 3:
                return 308, {'Range': 'bytes=0-%s' % (len(ranges) * 4 - 1)}, b''
            return 201, {}, b'{"id":"complete"}'
        drive.request = request
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            path = Path(directory)/'file'
            path.write_bytes(b'0123456789')
            with patch.object(hubbackup, 'BLOCK', 4):
                self.assertEqual(drive.upload(path, 'name', 'folder'), 'complete')
        self.assertEqual(ranges, ['bytes 0-3/10', 'bytes 4-7/10', 'bytes 8-9/10'])
        self.assertEqual(sleeps, [1])

    def test_folder_pagination_and_download(self):
        drive = Drive(lambda: 'fake-token')
        calls = []
        def request(method, url, headers, body):
            calls.append(url)
            if 'alt=media' in url:
                return 206, {'Content-Range': 'bytes 0-2/3'}, b'abc'
            if 'pageToken=' in url:
                return 200, {}, b'{"files":[{"id":"folder","name":"test"}]}'
            return 200, {}, b'{"files":[],"nextPageToken":"next"}'
        drive.request = request
        self.assertEqual(drive.folder('test'), 'folder')
        self.assertIn('appProperties', calls[0])
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            path = Path(directory)/'file'
            drive.download('abc', path)
            self.assertEqual(path.read_bytes(), b'abc')

    def test_errors_hide_token(self):
        drive = Drive(lambda: 'fake-token')
        drive.sleep = lambda seconds: None
        def request(*args):
            raise RuntimeError('fake-token')
        drive.request = request
        with self.assertRaises(RuntimeError) as exc:
            drive.folder('test')
        self.assertNotIn('fake-token', str(exc.exception))


if __name__ == '__main__':
    unittest.main()
