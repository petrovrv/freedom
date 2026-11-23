import base64
import json
import tempfile
import threading
import unittest
import urllib.parse
from email import policy
from email.message import EmailMessage
from email.parser import BytesParser
from pathlib import Path

import compose
import gmailsync
import relay
from mailstore import Store
from test_gmailsync import FakeGmail


class FakeWrite:
    """Gmail write calls: drafts, send, thread modify/trash, sendAs."""

    def __init__(self):
        self.drafts, self.sent, self.calls, self.n = {}, [], [], 0
        self.fail_send = None

    def __call__(self, method, url, headers, body=None):
        assert headers['Authorization'] == 'Bearer tok'
        path = urllib.parse.urlparse(url).path.split('/users/me/')[1]
        data = json.loads(body) if body else None
        self.calls.append((method, path, data))
        if path == 'settings/sendAs':
            return 200, {'sendAs': [{'sendAsEmail': 'me@example.org', 'displayName': 'Me Person',
                                     'signature': '<div>Me<br><a href="https://me.example">me.example</a></div>'}]}
        if path == 'drafts' and method == 'POST':
            self.n += 1
            did = 'd%d' % self.n
            self.drafts[did] = data['message']
            return 200, {'id': did, 'message': {'id': 'm' + did, 'threadId': data['message'].get('threadId', 't' + did)}}
        if path.startswith('drafts/') and method == 'PUT':
            did = path.split('/')[1]
            if did not in self.drafts:
                return 404, {}
            self.drafts[did] = data['message']
            return 200, {'id': did, 'message': {'id': 'm' + did, 'threadId': data['message'].get('threadId', 't' + did)}}
        if path.startswith('drafts/') and method == 'DELETE':
            self.drafts.pop(path.split('/')[1], None)
            return 204, {}
        if path == 'drafts/send':
            if self.fail_send:
                return self.fail_send, {}
            self.sent.append(self.drafts.pop(data['id']))
            return 200, {'id': 'sent1', 'threadId': 't'}
        if path.startswith('threads/'):
            return 200, {}
        if path.startswith('drafts'):
            return 200, {'drafts': [{'id': k, 'message': {'id': 'm' + k}} for k in self.drafts]}
        return 400, {}


def raw_of(draft):
    return BytesParser(policy=policy.default).parsebytes(base64.urlsafe_b64decode(draft['raw']))


class ComposeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root / 'store')
        cred = self.root / 'cred.json'
        cred.write_text(json.dumps({'client_id': 'c', 'client_secret': 's', 'refresh_token': 'r', 'account': 'me@example.org'}))
        api = FakeGmail()
        m = EmailMessage()
        m['From'], m['To'], m['Cc'] = 'Alice <alice@example.org>', 'me@example.org, bob@example.org', 'carol@example.org'
        m['Subject'], m['Message-ID'], m['References'] = 'Pump offer', '<a1@x>', '<a0@x>'
        m.set_content('Can you check the pump?\nThanks')
        m.add_attachment(b'%PDF-1.4 tiny', maintype='application', subtype='pdf', filename='offer.pdf')
        api.add('a1', m.as_bytes(), labels=('INBOX', 'UNREAD'))
        gmailsync.sync_gmail(self.store, gmailsync.TokenSource(cred, request=api), request=api)
        self.now = [1000.0]
        self.write = FakeWrite()
        self.c = compose.Composer(self.store, 'me@example.org', token=lambda: 'tok', request=self.write,
                                  clock=lambda: self.now[0], sleep=lambda s: None)

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def test_reply_all_builds_threaded_draft_with_signature_and_quote(self):
        start = self.c.handle('compose_start', {'kind': 'replyall', 'of': 'a1'})
        f = start['fields']
        self.assertEqual(f['subject'], 'Re: Pump offer')
        self.assertEqual(f['to'], 'Alice <alice@example.org>')
        self.assertEqual(f['cc'], 'bob@example.org, carol@example.org')  # never the account itself
        out = self.c.handle('compose_save', {'key': start['key'], 'fields': {**f, 'text': 'Checked: OK https://x.example/a'}})
        self.assertEqual(out['draft'], 'd1')
        draft = self.write.drafts['d1']
        self.assertEqual(draft['threadId'], 'ta1')
        mail = raw_of(draft)
        self.assertEqual(mail['In-Reply-To'], '<a1@x>')
        self.assertEqual(mail['References'], '<a0@x> <a1@x>')
        self.assertEqual(mail['From'], 'Me Person <me@example.org>')
        text = mail.get_body(('plain',)).get_content()
        self.assertIn('Checked: OK', text)
        self.assertIn('> Can you check the pump?', text)
        html = mail.get_body(('html',)).get_content()
        self.assertIn('<a href="https://x.example/a">', html)
        self.assertIn('https://me.example', html)  # the Gmail signature keeps its link

    def test_forward_keeps_attachments_and_upload_adds_one(self):
        start = self.c.handle('compose_start', {'kind': 'forward', 'of': 'a1'})
        self.assertEqual(start['fields']['subject'], 'Fwd: Pump offer')
        self.assertEqual(start['fields']['to'], '')
        key = start['key']
        self.c.handle('compose_save', {'key': key, 'fields': {**start['fields'], 'to': 'dan@example.org'}, 'pending': start['pending']})
        up = 'ab' * 8
        part1 = self.c.handle('compose_upload', {'key': key, 'upload': up, 'name': 'a/b.txt', 'type': 'text/plain', 'offset': 0,
                                                 'data': base64.b64encode(b'hello ').decode()})
        self.assertEqual(part1['received'], 6)
        with self.assertRaises(ValueError):
            self.c.handle('compose_upload', {'key': key, 'upload': up, 'offset': 0, 'data': base64.b64encode(b'x').decode()})
        done = self.c.handle('compose_upload', {'key': key, 'upload': up, 'offset': 6, 'last': True,
                                                'data': base64.b64encode(b'world').decode()})
        self.assertEqual([a['name'] for a in done['attachments']], ['offer.pdf', 'a_b.txt'])
        mail = raw_of(self.write.drafts['d1'])
        names = sorted(p.get_filename() for p in mail.iter_attachments())
        self.assertEqual(names, ['a_b.txt', 'offer.pdf'])
        self.assertIn('Forwarded message', mail.get_body(('plain',)).get_content())
        sha = next(a['sha'] for a in done['attachments'] if a['name'] == 'a_b.txt')
        after = self.c.handle('compose_detach', {'key': key, 'sha': sha})
        self.assertEqual([a['name'] for a in after['attachments']], ['offer.pdf'])

    def test_send_waits_for_undo_then_sends_once(self):
        start = self.c.handle('compose_start', {'kind': 'new'})
        key = start['key']
        with self.assertRaises(ValueError):
            self.c.handle('compose_send', {'key': key, 'fields': {'kind': 'new', 'subject': 'x', 'text': 'y'}})  # no recipients
        q = self.c.handle('compose_send', {'key': key, 'fields': {'kind': 'new', 'to': 'dan@example.org', 'subject': 'Hi', 'text': 'Yo'}})
        self.assertEqual((q['state'], q['undo']), ('wait', 10))
        with self.assertRaises(ValueError):
            self.c.handle('compose_save', {'key': key, 'fields': {'text': 'changed'}})
        self.c.send_due(self.store.db)
        self.assertEqual(self.write.sent, [])  # not yet due
        self.now[0] += 11
        self.c.send_due(self.store.db)
        self.c.send_due(self.store.db)
        self.assertEqual(len(self.write.sent), 1)
        self.assertEqual(self.c.handle('compose_unsend', {'key': key})['cancelled'], False)
        self.assertEqual(self.c.handle('compose_outbox', {})['items'][0]['state'], 'sent')

    def test_unsend_in_time_keeps_the_draft(self):
        key = self.c.handle('compose_start', {'kind': 'new'})['key']
        self.c.handle('compose_send', {'key': key, 'fields': {'kind': 'new', 'to': 'dan@example.org', 'subject': 'Hi', 'text': 'Yo'}})
        self.assertTrue(self.c.handle('compose_unsend', {'key': key})['cancelled'])
        self.now[0] += 60
        self.c.send_due(self.store.db)
        self.assertEqual(self.write.sent, [])
        self.assertIn('d1', self.write.drafts)
        self.assertEqual([d['key'] for d in self.c.handle('compose_drafts', {})['drafts']], [key])
        # sending again after a cancel works
        self.c.handle('compose_send', {'key': key})
        self.now[0] += 11
        self.c.send_due(self.store.db)
        self.assertEqual(len(self.write.sent), 1)

    def test_failed_send_is_reported_and_draft_stays(self):
        self.write.fail_send = 400
        key = self.c.handle('compose_start', {'kind': 'new'})['key']
        self.c.handle('compose_send', {'key': key, 'fields': {'kind': 'new', 'to': 'dan@example.org', 'subject': 'Hi', 'text': 'Yo'}})
        self.now[0] += 11
        self.c.send_due(self.store.db)
        item = self.c.handle('compose_outbox', {})['items'][0]
        self.assertEqual(item['state'], 'failed')
        self.assertIn('d1', self.write.drafts)

    def test_header_injection_and_bad_addresses_refused(self):
        key = self.c.handle('compose_start', {'kind': 'new'})['key']
        for fields in ({'to': 'a@example.org\r\nBcc: evil@example.org'}, {'subject': 'x\nBcc: e@x.org'}, {'to': 'not an address'}):
            with self.assertRaises(ValueError):
                self.c.handle('compose_save', {'key': key, 'fields': {'kind': 'new', **fields}})
        with self.assertRaises(ValueError):
            self.c.handle('compose_save', {'key': '../../x', 'fields': {}})

    def test_mark_changes_provider_and_store(self):
        self.c.handle('mark', {'thread': 'ta1', 'mark': 'read'})
        self.c.handle('mark', {'thread': 'ta1', 'mark': 'archive'})
        labels = json.loads(self.store.db.execute("SELECT labels FROM gmail_messages WHERE gmail_id='a1'").fetchone()[0])
        self.assertNotIn('UNREAD', labels)
        self.assertNotIn('INBOX', labels)
        self.assertIn(('POST', 'threads/ta1/modify', {'addLabelIds': [], 'removeLabelIds': ['UNREAD']}), self.write.calls)
        self.c.handle('mark', {'thread': 'ta1', 'mark': 'trash'})
        self.assertIn(('POST', 'threads/ta1/trash', None), self.write.calls)
        with self.assertRaises(ValueError):
            self.c.handle('mark', {'thread': 'ta1', 'mark': 'delete-forever'})

    def test_without_consent_writes_answer_needs_consent(self):
        c = compose.Composer(self.store, 'me@example.org')
        key = c.handle('compose_start', {'kind': 'new'})['key']
        with self.assertRaisesRegex(ValueError, 'needs_consent'):
            c.handle('compose_save', {'key': key, 'fields': {'kind': 'new', 'to': 'dan@example.org'}})

        def refused():
            raise PermissionError('Not granted yet: mail.compose')
        c = compose.Composer(self.store, 'me@example.org', token=refused, request=self.write)
        with self.assertRaisesRegex(ValueError, 'needs_consent'):
            c.handle('mark', {'thread': 'ta1', 'mark': 'read'})

    def test_relay_dispatches_compose_actions(self):
        api = relay.MailApi(self.store, 'me@example.org', composer=self.c)
        out = relay.handle_line(api, True, json.dumps({'op': 'call', 'action': 'compose_start', 'args': {'kind': 'new'}}).encode())
        self.assertTrue(out['ok'])
        api = relay.MailApi(self.store, 'me@example.org')
        out = relay.handle_line(api, True, json.dumps({'op': 'call', 'action': 'compose_start', 'args': {}}).encode())
        self.assertEqual(out['error'], 'unknown action')

    def test_sender_thread_runs_and_stops(self):
        stop = threading.Event()
        t = threading.Thread(target=self.c.run_sender, args=(self.store.root / 'mail.sqlite3', stop))
        t.start()
        stop.set()
        self.c._wake.set()
        t.join(5)
        self.assertFalse(t.is_alive())


if __name__ == '__main__':
    unittest.main()
