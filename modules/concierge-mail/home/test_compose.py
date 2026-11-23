import base64
import hashlib
import json
import sqlite3
import tempfile
import threading
import unittest
import urllib.parse
from email import policy
from email.message import EmailMessage
from email.parser import BytesParser
from pathlib import Path
from unittest import mock

import compose
import gmailsync
import relay
from mailstore import Store
from test_gmailsync import FakeGmail


class FakeWrite:
    """Gmail write calls: drafts, send, thread modify/trash, sendAs."""

    def __init__(self):
        self.drafts, self.sent, self.calls, self.n = {}, [], [], 0
        self.fail_send, self.send_calls = None, 0

    def mid(self, did):  # Gmail gives a draft a new message id on every change
        return 'm' + did + hashlib.md5(self.drafts[did].get('raw', '').encode()).hexdigest()[:6]

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
            return 200, {'id': did, 'message': {'id': self.mid(did), 'threadId': data['message'].get('threadId', 't' + did)}}
        if path.startswith('drafts/') and method == 'PUT':
            did = path.split('/')[1]
            if did not in self.drafts:
                return 404, {}
            self.drafts[did] = data['message']
            return 200, {'id': did, 'message': {'id': self.mid(did), 'threadId': data['message'].get('threadId', 't' + did)}}
        if path.startswith('drafts/') and method == 'DELETE':
            self.drafts.pop(path.split('/')[1], None)
            return 204, {}
        if path == 'drafts/send':
            self.send_calls += 1
            if self.fail_send == 'lost':  # sent, but the answer never came
                self.sent.append(self.drafts.pop(data['id']))
                raise TimeoutError('read timed out')
            if self.fail_send:
                return self.fail_send, {}
            self.sent.append(self.drafts.pop(data['id']))
            return 200, {'id': 'sent1', 'threadId': 't'}
        if path == 'messages' and method == 'GET':
            q = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)['q'][0]
            mid = '<' + q.split('rfc822msgid:')[1] + '>'
            hits = [m for m in self.sent if raw_of(m)['Message-ID'] == mid]
            return 200, ({'messages': [{'id': 'x'}]} if hits else {})
        if path.startswith('threads/'):
            return 200, {}
        if path.startswith('drafts/') and method == 'GET':
            did = path.split('/')[1]
            if did not in self.drafts:
                return 404, {}
            m = self.drafts[did]
            return 200, {'id': did, 'message': {'id': self.mid(did), 'threadId': m.get('threadId', 't' + did), 'raw': m.get('raw', ''),
                                                'snippet': 'snip &amp; more', 'internalDate': '5',
                                                'payload': {'headers': [{'name': 'Subject', 'value': 'S ' + did}]}}}
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
        self.api = api = FakeGmail()
        self.cred = cred
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
        stored = self.store.db.execute('SELECT mid FROM compose WHERE key=?', (start['key'],)).fetchone()[0]
        self.assertEqual(stored, mail['Message-ID'])  # the reply's own id, never the original's
        self.assertNotEqual(stored, '<a1@x>')
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

    def queue(self):
        key = self.c.handle('compose_start', {'kind': 'new'})['key']
        self.c.handle('compose_send', {'key': key, 'fields': {'kind': 'new', 'to': 'dan@example.org', 'subject': 'Hi', 'text': 'Yo'}})
        return key

    def test_lost_answer_is_never_sent_twice(self):
        self.write.fail_send = 'lost'
        key = self.queue()
        self.now[0] += 11
        self.c.send_due(self.store.db)
        self.assertEqual(self.c.handle('compose_outbox', {})['items'][0]['state'], 'unknown')
        with self.assertRaises(ValueError):  # no new Send / save while the outcome is unknown
            self.c.handle('compose_send', {'key': key, 'fields': {'text': 'again'}})
        self.now[0] += compose.RECONCILE + 1
        self.c.reconcile(self.store.db)
        self.assertEqual(self.c.handle('compose_outbox', {})['items'][0]['state'], 'sent')
        self.assertEqual((self.write.send_calls, len(self.write.sent)), (1, 1))

    def test_5xx_then_draft_still_there_allows_sending_again(self):
        self.write.fail_send = 503
        key = self.queue()
        self.now[0] += 11
        self.c.send_due(self.store.db)
        self.assertEqual(self.write.send_calls, 1)  # a 5xx of a send is not retried
        self.now[0] += compose.RECONCILE + 1
        self.c.reconcile(self.store.db)
        self.assertEqual(self.c.handle('compose_outbox', {})['items'][0]['state'], 'unknown')  # not in Sent yet, within the grace
        with self.assertRaises(ValueError):  # too early to ask the person
            self.c.handle('compose_resolve', {'key': key, 'sent': False})
        self.now[0] += compose.GRACE
        self.c.reconcile(self.store.db)
        item = self.c.handle('compose_outbox', {})['items'][0]
        self.assertEqual((item['state'], item['ask']), ('unknown', True))  # time alone never settles it
        self.assertEqual(self.c.handle('compose_resolve', {'key': key, 'sent': False})['state'], 'failed')
        self.write.fail_send = None
        self.c.handle('compose_send', {'key': key})
        self.now[0] += 11
        self.c.send_due(self.store.db)
        self.assertEqual(len(self.write.sent), 1)

    def test_restart_mid_send_is_reconciled_not_resent(self):
        key = self.queue()
        self.store.db.execute("UPDATE outbox SET state='sending' WHERE key=?", (key,))
        self.store.db.commit()
        stop = threading.Event()
        stop.set()
        self.c.run_sender(self.store.root / 'mail.sqlite3', stop)
        self.assertEqual(self.store.db.execute('SELECT state FROM outbox WHERE key=?', (key,)).fetchone()[0], 'unknown')
        self.assertEqual(self.write.send_calls, 0)

    def test_cancel_and_send_again_restarts_the_undo_window(self):
        key = self.queue()
        self.now[0] += 11
        due_rows = self.store.db.execute("SELECT key FROM outbox WHERE state='wait'").fetchall()
        self.assertEqual(len(due_rows), 1)
        self.c.handle('compose_unsend', {'key': key})
        self.c.handle('compose_send', {'key': key})  # a new 10 s window from now
        self.c.send_due(self.store.db)
        self.assertEqual(self.write.send_calls, 0)

    def test_draft_written_elsewhere_keeps_its_html_until_the_text_changes(self):
        d = EmailMessage()
        d['From'], d['To'], d['Subject'], d['Message-ID'] = 'me@example.org', 'eve@example.org', 'Plan', '<dx@x>'
        d.set_content('See the plan')
        d.add_alternative('<p>See <a href="https://plan.example/p">the plan</a></p>', subtype='html')
        d.add_attachment(b'GIF89a...', maintype='image', subtype='gif', filename='p.gif')
        inner = EmailMessage(); inner['Subject'] = 'Inner'; inner.set_content('inner body')
        d.add_attachment(inner, filename='inner.eml')
        self.write.drafts['dx'] = {'raw': base64.urlsafe_b64encode(d.as_bytes()).decode()}
        listed = self.c.handle('compose_gmail_drafts', {})['drafts']
        self.assertEqual([(x['draft'], x['subject'], x['snippet'], x['key']) for x in listed], [('dx', 'S dx', 'snip & more', '')])
        opened = self.c.handle('compose_open', {'draft': 'dx'})
        self.assertNotIn('base', opened['fields'])  # the original stays on the home server
        self.assertEqual((opened['fields']['to'], opened['fields']['text'].strip()), ('eve@example.org', 'See the plan'))
        self.assertEqual([a['name'] for a in opened['attachments']], ['p.gif', 'inner.eml'])
        self.assertTrue(all(a['size'] > 0 for a in opened['attachments']))
        key = opened['key']
        self.assertEqual(self.c.handle('compose_open', {'draft': 'dx'})['key'], key)
        self.c.handle('compose_save', {'key': key, 'fields': {**opened['fields'], 'to': 'eve@example.org, dan@example.org'}})
        mail = raw_of(self.write.drafts['dx'])
        self.assertIn('href="https://plan.example/p"', mail.get_body(('html',)).get_content())
        self.assertEqual(mail['To'], 'eve@example.org, dan@example.org')
        self.assertNotEqual(mail['Message-ID'], '<dx@x>')
        self.c.handle('compose_save', {'key': key, 'fields': {**opened['fields'], 'text': 'New plan'}})
        mail = raw_of(self.write.drafts['dx'])
        self.assertIn('New plan', mail.get_body(('plain',)).get_content())
        self.assertNotIn('plan.example', mail.get_body(('html',)).get_content())
        self.assertEqual([p.get_filename() for p in mail.iter_attachments()], ['p.gif', 'inner.eml'])
        self.assertIn('inner body', [p for p in mail.iter_attachments()][1].get_content().get_body().get_content())
    def test_db_error_after_a_send_never_allows_a_resend(self):
        key = self.queue()
        self.now[0] += 11
        real = self.store.db.execute

        class Db:  # the sent mark fails once, after Gmail took the letter
            def __getattr__(s, name):
                return getattr(self.store.db, name)

            def execute(s, sql, *a):
                if sql.startswith("UPDATE outbox SET state='sent'"):
                    raise sqlite3.OperationalError('disk I/O error')
                return real(sql, *a)

            def __enter__(s):
                return self.store.db.__enter__()

            def __exit__(s, *e):
                return self.store.db.__exit__(*e)
        with self.assertRaises(sqlite3.OperationalError):
            self.c.send_due(Db())
        self.assertEqual(len(self.write.sent), 1)
        state = self.store.db.execute('SELECT state FROM outbox WHERE key=?', (key,)).fetchone()[0]
        self.assertEqual(state, 'sending')  # the loop turns it into unknown; never failed
        stop = threading.Event(); stop.set()
        self.c.run_sender(self.store.root / 'mail.sqlite3', stop)
        self.assertEqual(self.store.db.execute('SELECT state FROM outbox WHERE key=?', (key,)).fetchone()[0], 'unknown')
        with self.assertRaises(ValueError):
            self.c.handle('compose_send', {'key': key})

    def test_draft_changed_in_gmail_is_taken_again(self):
        d = EmailMessage()
        d['From'], d['To'], d['Subject'], d['In-Reply-To'], d['References'] = 'me@example.org', 'eve@example.org', 'Re: X', '<p@x>', '<o@x> <p@x>'
        d.set_content('first')
        self.write.drafts['dz'] = {'raw': base64.urlsafe_b64encode(d.as_bytes()).decode()}
        first = self.c.handle('compose_open', {'draft': 'dz'})
        self.c.handle('compose_save', {'key': first['key'], 'fields': {**first['fields'], 'text': 'edited here'}})
        mail = raw_of(self.write.drafts['dz'])
        self.assertEqual((mail['In-Reply-To'], mail['References']), ('<p@x>', '<o@x> <p@x>'))  # reply headers kept
        self.assertEqual(self.c.handle('compose_open', {'draft': 'dz'})['fields']['text'], 'edited here')  # unchanged: ours
        d.replace_header('To', 'zed@example.org')
        self.write.drafts['dz'] = {'raw': base64.urlsafe_b64encode(d.as_bytes()).decode()}
        self.write.n_msg = True
        again = self.c.handle('compose_open', {'draft': 'dz'})
        self.assertEqual(again['fields']['to'], 'zed@example.org')
        self.assertNotEqual(again['key'], first['key'])

    def test_sender_survives_a_database_error(self):
        key = self.queue()
        calls = {'n': 0}
        real = self.c.send_due

        def flaky(db):
            calls['n'] += 1
            if calls['n'] == 1:
                raise sqlite3.OperationalError('database is locked')
            real(db)
            stop.set()
        self.c.send_due = flaky
        stop = threading.Event()
        self.now[0] += 11
        with mock.patch.object(self.c._wake, 'wait', lambda t: None):
            self.c.run_sender(self.store.root / 'mail.sqlite3', stop)
        self.assertEqual((calls['n'], len(self.write.sent)), (2, 1))

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
