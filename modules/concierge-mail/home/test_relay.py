import base64
import io
import json
import tempfile
import time
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

    def test_threads_of_a_chat(self):
        chat, other = 'CHAT-20261125100000-AB12', 'CHAT-20261125100000-CD34'
        threads = self.ask('list', {'label': 'INBOX'})['result']['threads']
        heat = next(t['thread'] for t in threads if t['subject'] == 'Hello heat pump')
        self.assertEqual(self.ask('links', {'thread': heat})['result']['chats'], [])
        got = self.ask('link', {'thread': heat, 'chat': chat, 'title': '  Heat\npump   offer ', 'project': 'Home'})['result']
        self.assertEqual([(c['chat'], c['title'], c['project']) for c in got['chats']], [(chat, 'Heat pump offer', 'Home')])
        # linking again refreshes the title, no second row
        again = self.ask('link', {'thread': heat, 'chat': chat, 'title': 'Heat pump', 'project': 'Home'})['result']['chats']
        self.assertEqual([c['title'] for c in again], ['Heat pump'])
        self.ask('link', {'thread': heat, 'chat': other, 'title': 'Other', 'project': 'Home'})
        mine = self.ask('chat_threads', {'chat': chat})['result']['threads']
        self.assertEqual([t['subject'] for t in mine], ['Hello heat pump'])
        self.assertEqual({c['chat'] for c in mine[0]['chats']}, {chat, other})
        listed = {c['chat']: c for c in self.ask('chats')['result']['chats']}
        self.assertEqual((listed[chat]['threads'], listed[chat]['unread'], listed[chat]['title']), (1, 1, 'Heat pump'))
        self.ask('unlink', {'thread': heat, 'chat': other})
        self.assertEqual([c['chat'] for c in self.ask('links', {'thread': heat})['result']['chats']], [chat])
        self.assertEqual(self.ask('chat_threads', {'chat': other})['result']['threads'], [])
        # bad ids and unknown threads are refused, nothing is stored
        for bad in ({'thread': heat, 'chat': 'CHAT-1', 'project': 'Home'}, {'thread': heat, 'chat': chat, 'project': '../x'},
                    {'thread': 'nope', 'chat': chat, 'project': 'Home'}, {'thread': "x' OR 1=1", 'chat': chat, 'project': 'Home'}):
            self.assertFalse(self.ask('link', bad)['ok'], bad)
        self.assertFalse(self.ask('chat_threads', {'chat': "CHAT-' OR 1=1"})['ok'])
        self.assertEqual(self.store.db.execute('SELECT COUNT(*) FROM chat_links').fetchone()[0], 1)
        # links are kept per mailbox: another account of the same store sees none
        self.assertEqual(relay.MailApi(self.store, 'other@example.org').chats({})['chats'], [])

    def test_agent_reads_only_the_threads_of_its_chat(self):
        chat = 'CHAT-20261125100000-AB12'
        threads = self.ask('list', {'label': 'INBOX'})['result']['threads']
        heat = next(t['thread'] for t in threads if t['subject'] == 'Hello heat pump')
        other = next(t['thread'] for t in threads if t['subject'] == 'Html')
        self.ask('link', {'thread': heat, 'chat': chat, 'title': 'Heat', 'project': 'Home'})
        listed = relay.chat_mail(self.mail, chat)
        self.assertEqual([t['subject'] for t in listed['threads']], ['Hello heat pump'])
        got = relay.chat_mail(self.mail, chat, heat)
        self.assertEqual(got['messages'][0]['text'].strip(), 'Offer for the heat pump')
        self.assertNotIn('html', got['messages'][0])
        with self.assertRaises(LookupError):
            relay.chat_mail(self.mail, chat, other)  # a thread of no link to this chat
        with self.assertRaises(ValueError):
            relay.chat_mail(self.mail, 'CHAT-x')

    def test_links_per_thread_are_limited(self):
        heat = self.ask('list', {'label': 'INBOX'})['result']['threads'][0]['thread']
        for i in range(relay.CHATS_PER_THREAD):
            self.assertTrue(self.ask('link', {'thread': heat, 'chat': f'CHAT-20261125100000-A{i:03d}', 'title': str(i), 'project': 'P'})['ok'])
        self.assertFalse(self.ask('link', {'thread': heat, 'chat': 'CHAT-20261125100000-ZZZZ', 'title': 'x', 'project': 'P'})['ok'])
        self.assertTrue(self.ask('link', {'thread': heat, 'chat': 'CHAT-20261125100000-A000', 'title': 'again', 'project': 'P'})['ok'])

    def test_thread_flag_comes_from_any_message(self):
        db = self.store.db
        first = db.execute("SELECT thread_id FROM gmail_messages WHERE gmail_id='a1'").fetchone()['thread_id']
        db.execute("UPDATE gmail_messages SET thread_id=?, internal_date=1 WHERE gmail_id='a2'", (first,))
        db.execute("UPDATE gmail_messages SET internal_date=2 WHERE gmail_id='a1'")
        db.execute("UPDATE messages SET tainted=1 WHERE id=(SELECT message_id FROM gmail_messages WHERE gmail_id='a2')")
        db.commit()
        thread = next(t for t in self.ask('list', {'label': 'ALL'})['result']['threads'] if t['thread'] == first)
        self.assertEqual((thread['count'], thread['subject'], thread['tainted']), (2, 'Hello heat pump', True))
        db.execute("UPDATE gmail_messages SET gone=1 WHERE gmail_id='a2'")
        db.commit()
        thread = next(t for t in self.ask('list', {'label': 'ALL'})['result']['threads'] if t['thread'] == first)
        self.assertEqual((thread['count'], thread['tainted']), (1, False))

    def test_remote_images_come_from_the_stored_message(self):
        html = EmailMessage()
        html['From'], html['To'], html['Subject'], html['Message-ID'] = 'Shop <s@example.org>', 'me@example.org', 'Sale', '<a4@x>'
        html.set_content('plain')
        html.add_alternative('<p>Hi<img src="https://cdn.example/a.png"><img src="cid:none"></p>', subtype='html')
        self.api.add('a4', html.as_bytes(), labels=('INBOX',))
        cred = self.root / 'cred.json'
        gmailsync.sync_gmail(self.store, gmailsync.TokenSource(cred, request=self.api), request=self.api)
        asked = {}

        def fake(urls, cache=None):
            asked['urls'], asked['cache'] = urls, cache
            return {urls[0]: 'data:image/png;base64,AA'}, {}
        out = self.mail.images({'id': 'a4', 'url': 'http://192.168.86.1/'}, fetch_all=fake)
        self.assertEqual(asked['urls'], ['https://cdn.example/a.png'])  # never an address from the page
        self.assertEqual(asked['cache'], self.store.root / 'imgcache')
        self.assertEqual(out['images'], {'https://cdn.example/a.png': 'data:image/png;base64,AA'})
        self.assertEqual(self.ask('images', {'id': 'nope'})['error'], 'no message')
        self.assertEqual(self.ask('images', {'id': '../x'})['error'], 'bad message')

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

    def test_snippet_without_style_sheets(self):
        s = relay._snippet
        self.assertEqual(s('', '<html><head><title>Title</title><style>body{ width: 100%; }</style></head>'
                              '<body><!-- x --><p>Hi&nbsp;Alex &amp; co</p></body></html>'), 'Hi Alex & co')
        self.assertEqual(s('<style type="text/css"> html { height: 100%; } </style><p>Nieuw bericht</p>', ''), 'Nieuw bericht')
        self.assertEqual(s('Deal 96 * { font-family: Arial; } #outlook a { padding: 0; } [owa] .btn > td { x: 1; }'
                           ' @import url(https://f.example/css?a=1;b=2); @media screen { @font-face { font-family: X; } } Real', ''),
                         'Deal 96 Real')
        self.assertEqual(s('', '<style>table.data td {x:1} a[href^="tel"] {z:3}</style>Text table.data td {x:1} after'), 'Text after')
        self.assertEqual(s('', '<p>Je pakket wordt bezorgd</p><div>body{ height: 100% !important; }</div>'), 'Je pakket wordt bezorgd')
        self.assertEqual(s('', '<html><head><meta charset=utf-8><body><p>Hello Alex</p>'), 'Hello Alex')  # no </head>
        self.assertEqual(s('.editable { background: #EAEAEA } Dear Alex, Stain &amp; Odour&nbsp;Remover', ''), 'Dear Alex, Stain & Odour Remover')
        for html in ('<p>your code {id: 5} is ready</p>', '<p>Cost {Q3: up 5%} report</p>', '<p>Use {} as placeholder</p>'):
            self.assertEqual(s('', html), html[3:-4])  # braces in html text that are not CSS
        self.assertEqual(s('', '<head><meta charset=utf-8><p>No body tag here'), 'No body tag here')
        for text in ('Price {not css} and a: b', 'Meeting {time: 10:00} ok', 'Hello {} world', 'Price <50 EUR, delivery > 3 days',
                     'Reading <title of the book> now.', '?a=1&notify=true&copy=2', 'Total: 5 EUR. Fine', 'Your order #12345 {shipped: yes} today'):
            self.assertEqual(s(text, ''), text)  # a text part keeps its <, & and braces
        self.assertEqual(s('Hi\u200c\u034f\u2800 there \U0001F468\u200d\U0001F469', ''), 'Hi there \U0001F468\u200d\U0001F469')

    def test_snippet_time_stays_linear(self):
        for body, html in (('a' * 200000, ''), ('a ' * 100000, ''), ('/* ' * 60000 + '{', ''), ('@import url( ' * 15000 + '{', ''),
                           ('<' * 200000, ''), ('', '<' * 200000), ('{' * 100000 + '}' * 100000, ''), ('x{a:1;' * 30000 + '}' * 30000, ''),
                           ('.a ' * 60000 + '{x:1;}', ''), ('', '<head>' * 30000), ('', '<!--' * 50000),
                           ('a' + '\u200d' * 200000 + 'b', ''), ('x' * 400 + '{}' * 99000, ''),
                           (('{' + 'a:' * 1999 + '}') * 50, ''), ('@charset' + '\xa0' * 199980 + '{', ''),
                           (('@import ' + ' ' * 2000) * 95 + '{', '')):
            t = time.monotonic()
            relay._snippet(body, html)
            self.assertLess(time.monotonic() - t, 1.5)


if __name__ == '__main__':
    unittest.main()
