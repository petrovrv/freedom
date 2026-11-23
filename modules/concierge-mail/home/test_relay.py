import base64
import io
import json
import tempfile
import unittest
from email.message import EmailMessage
from pathlib import Path

import gmailsync
import relay
from mailstore import Store
from test_gmailsync import FakeGmail
from test_mailstore import message


class RelayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root / 'store')
        cred = self.root / 'cred.json'
        cred.write_text(json.dumps({'client_id': 'c', 'client_secret': 's', 'refresh_token': 'r', 'account': 'me@example.org'}))
        self.api = FakeGmail()
        self.api.add('a1', message(subject='Hello heat pump', body='Offer for the heat pump', mid='<a1@x>').as_bytes(), labels=('INBOX', 'UNREAD'))
        self.api.add('a2', message(subject='Report', body='Weekly report', mid='<a2@x>').as_bytes(), labels=('SENT',))
        html = EmailMessage()
        html['From'], html['To'], html['Subject'], html['Message-ID'] = 'Eve <eve@example.org>', 'me@example.org', 'Html', '<a3@x>'
        html.set_content('plain')
        html.add_alternative('<p onclick="x()">Hi<script>alert(1)</script><img src="cid:logo"><a href="javascript:evil()">l</a></p>', subtype='html')
        html.get_payload()[1].add_related(b'\x89PNG fake', 'image', 'png', cid='<logo>')
        html.add_attachment(b'%PDF-1.4 tiny', maintype='application', subtype='pdf', filename='offer.pdf')
        self.api.add('a3', html.as_bytes(), labels=('INBOX',))
        gmailsync.sync_gmail(self.store, gmailsync.TokenSource(cred, request=self.api), request=self.api)
        self.mail = relay.MailApi(self.store, 'me@example.org')

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def ask(self, action, args=None):
        return relay.handle_line(self.mail, True, json.dumps({'op': 'call', 'action': action, 'args': args or {}}).encode())

    def test_overview_list_search_thread(self):
        ov = self.ask('overview')['result']
        inbox = next(l for l in ov['labels'] if l['id'] == 'INBOX')
        self.assertEqual((inbox['total'], inbox['unread'], ov['unread']), (2, 1, 1))
        threads = self.ask('list', {'label': 'INBOX'})['result']['threads']
        self.assertEqual(len(threads), 2)
        self.assertEqual(self.ask('list', {'label': 'SENT'})['result']['threads'][0]['subject'], 'Report')
        first = self.ask('list', {'label': 'INBOX', 'limit': 1})['result']
        second = self.ask('list', {'label': 'INBOX', 'limit': 1, 'before': first['next']})['result']
        self.assertEqual({first['threads'][0]['thread'], second['threads'][0]['thread']}, {t['thread'] for t in threads})
        self.assertFalse(self.ask('list', {'label': 'INBOX', 'before': "1:x' OR 1=1"})['ok'])
        found = self.ask('search', {'q': 'heat pump'})['result']['threads']
        self.assertEqual([t['subject'] for t in found], ['Hello heat pump'])
        html_thread = next(t for t in threads if t['subject'] == 'Html')
        self.assertEqual(html_thread['attachments'], 1)
        msg = self.ask('thread', {'thread': html_thread['thread']})['result']['messages'][0]
        self.assertNotIn('<script', msg['html'])
        self.assertNotIn('onclick', msg['html'])
        self.assertNotIn('javascript:', msg['html'])
        self.assertIn('src="data:image/png;base64,', msg['html'])
        att = msg['attachments'][0]
        got = self.ask('attachment', {'sha': att['sha']})['result']
        self.assertEqual(base64.b64decode(got['data']), b'%PDF-1.4 tiny')

    def test_bad_input_gives_errors_not_content(self):
        self.assertFalse(self.ask('drop_table')['ok'])
        self.assertFalse(self.ask('list', {'label': "INBOX%' OR 1=1 --"})['ok'])
        self.assertFalse(self.ask('attachment', {'sha': 'f' * 64})['ok'])
        self.assertEqual(relay.handle_line(self.mail, True, b'not json'), {'ok': False, 'error': 'bad_request'})
        self.assertEqual(relay.handle_line(self.mail, True, b'{"op": "other"}'), {'ok': False, 'error': 'bad_request'})

    def test_status_for_the_tray(self):
        st = relay.handle_line(self.mail, True, b'{"op": "status"}')['status']
        self.assertEqual((st['bound'], st['unread'], st['account']), (True, 1, 'me@example.org'))

    def test_mailbox_of_another_environment_is_not_served(self):
        self.assertEqual(relay.handle_line(self.mail, False, b'{"op": "status"}'), {'status': {'bound': False}})
        self.assertEqual(relay.handle_line(self.mail, False, b'{"op": "call", "action": "overview", "args": {}}'),
                         {'ok': False, 'error': 'not_bound'})

    def test_handler_loop_binds_by_environment(self):
        lines = b'{"op": "status"}\n{"op": "call", "action": "list", "args": {"label": "SENT"}}\n'
        for env, bound in (({'ACCOUNT': 'Me@example.org', 'AIWB_ENV': 'enva', 'HOME_RELAY_ENV': 'enva'}, True),
                           ({'ACCOUNT': 'me@example.org', 'AIWB_ENV': 'enva', 'HOME_RELAY_ENV': 'envb'}, False),
                           ({'ACCOUNT': 'me@example.org', 'HOME_RELAY_ENV': ''}, False)):
            out = io.BytesIO()
            relay.handler_main(self.store, io.BytesIO(lines), out, env)
            status, answer = [json.loads(x) for x in out.getvalue().splitlines()]
            self.assertEqual(status['status']['bound'], bound)
            if bound:
                self.assertEqual(answer['result']['threads'][0]['subject'], 'Report')
            else:
                self.assertEqual(answer, {'ok': False, 'error': 'not_bound'})


if __name__ == '__main__':
    unittest.main()
