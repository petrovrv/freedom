import json
import tempfile
import unittest
from pathlib import Path
from archive import checkpoint, restore_checkpoint, verify_checkpoint, export_maildir
from mailstore import Store
from workflow import Workflow
from test_mailstore import message

class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root/'live')
    def tearDown(self):
        self.store.close()
        self.temp.cleanup()
    def test_checkpoint_restore_preserves_workflow_and_search(self):
        mail = message(body='Unique searchable text')
        mail.add_attachment(b'attachment content', maintype='application', subtype='octet-stream', filename='data.bin')
        row = self.store.ingest('owner', mail.as_bytes())
        flow = Workflow(self.store)
        flow.save_draft(row['thread_id'], 'Draft answer')
        checkpoint(self.store, self.root/'copy')
        restore_checkpoint(self.root/'copy', self.root/'restored')
        restored = Store(self.root/'restored')
        try:
            self.assertEqual(restored.search('searchable')[0]['id'], row['id'])
            self.assertEqual(Workflow(restored).get_draft(row['thread_id'])['body'], 'Draft answer')
            self.assertEqual(restored.read_blob(row['raw_sha256']), mail.as_bytes())
            self.assertEqual(restored.read_blob(row['attachments'][0]['sha256']), b'attachment content')
        finally:
            restored.close()
    def test_missing_attachment_in_undone_draft_blocks_checkpoint(self):
        row = self.store.ingest('owner', message().as_bytes())
        digest = self.store._blob(b'Draft-only attachment')
        flow = Workflow(self.store)
        flow.save_draft(row['thread_id'], 'Draft', attachments=[dict(name='draft.txt', sha256=digest)])
        flow.undo_draft(row['thread_id'])
        checkpoint(self.store, self.root/'good')
        manifest_path = self.root/'good'/'manifest.json'
        manifest = json.loads(manifest_path.read_text())
        del manifest['files']['blobs/'+digest]
        manifest_path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, 'blob missing'):
            verify_checkpoint(self.root/'good')
        (self.store.blobs/digest).unlink()
        with self.assertRaisesRegex(ValueError, 'blob missing'):
            checkpoint(self.store, self.root/'bad')
        self.assertFalse((self.root/'bad').exists())

    def test_corruption_never_publishes_restore(self):
        row = self.store.ingest('owner', message().as_bytes())
        checkpoint(self.store, self.root/'copy')
        (self.root/'copy'/'blobs'/row['raw_sha256']).write_bytes(b'corrupt')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            restore_checkpoint(self.root/'copy', self.root/'restored')
        self.assertFalse((self.root/'restored').exists())
    def test_missing_blob_and_path_traversal_rejected(self):
        row = self.store.ingest('owner', message().as_bytes())
        checkpoint(self.store, self.root/'copy')
        path = self.root/'copy'/'manifest.json'
        manifest = json.loads(path.read_text())
        del manifest['files']['blobs/'+row['raw_sha256']]
        path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, 'blob missing'):
            verify_checkpoint(self.root/'copy')
        manifest['files']['../outside'] = 'abc'
        path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, 'Invalid checkpoint path'):
            restore_checkpoint(self.root/'copy', self.root/'restored')
        self.assertFalse((self.root/'restored').exists())
    def test_export_account_isolation_exact_bytes_no_overwrite(self):
        raw = message().as_bytes()
        self.store.ingest('owner', raw)
        self.store.ingest('other', message(body='Other account').as_bytes())
        self.assertEqual(export_maildir(self.store, self.root/'maildir', 'owner')['exported'], 1)
        self.assertEqual(next((self.root/'maildir'/'new').iterdir()).read_bytes(), raw)
        with self.assertRaisesRegex(ValueError, 'must not exist'):
            export_maildir(self.store, self.root/'maildir', 'other')
        self.assertEqual(next((self.root/'maildir'/'new').iterdir()).read_bytes(), raw)

if __name__ == '__main__':
    unittest.main()
