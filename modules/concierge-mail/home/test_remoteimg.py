import base64
import socket
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

import remoteimg

PNG = b'\x89PNG\r\n\x1a\n' + b'x' * 20


class RemoteImageTests(unittest.TestCase):
    def test_urls_from_html(self):
        html = ('<img src="https://a.example/1.png?x=1&amp;y=2"><IMG alt=x SRC=\'//b.example/2.gif\'>'
                '<img src=http://c.example/3.jpg><img src="cid:logo"><img src="data:image/png;base64,AA">'
                '<img src="https://a.example/1.png?x=1&amp;y=2"><img src="https://e.example/?a=1&notit=2&amp;b=3">')
        self.assertEqual(remoteimg.image_urls(html),
                         ['https://a.example/1.png?x=1&y=2', '//b.example/2.gif', 'http://c.example/3.jpg',
                          'https://e.example/?a=1&notit=2&b=3'])

    def test_attribute_references_like_a_browser(self):
        html = ('<img src="https://e.example/&#97.png"><img src="https://e.example/x?a=1&copy=2">'
                '<img src="https://e.example/y&copy.png"><img src="https://e.example/z&#x62;&lt;"><img src="https://e.example/?x=1&notit;=2">')
        self.assertEqual(remoteimg.image_urls(html), ['https://e.example/a.png', 'https://e.example/x?a=1&copy=2',
                                                      'https://e.example/y\u00a9.png', 'https://e.example/zb<',
                                                      'https://e.example/?x=1&notit;=2'])

    def test_watchdog_shuts_the_transport_even_without_conn_sock(self):
        a, b = socket.socketpair()
        try:
            conn = type('C', (), {})()
            conn.sock, conn.transport = None, a
            remoteimg._shut(conn)
            try:  # a blocked read returns at once: empty on Linux, an error on Windows
                self.assertEqual(a.recv(10), b'')
            except OSError:
                pass
        finally:
            a.close()
            b.close()

    def test_private_addresses_refused(self):
        for url in ['http://127.0.0.1/a.png', 'http://192.168.86.1/a.png', 'http://10.0.0.1/a.png', 'http://[::1]/a.png',
                    'http://169.254.169.254/latest', 'http://100.99.205.44/a.png', 'ftp://example.org/a.png',
                    'http://user:pw@example.org/a.png', 'file:///etc/passwd']:
            self.assertEqual(remoteimg.fetch(url), (None, 'address'), url)

    def test_hostname_resolving_inside_refused(self):
        infos = [(2, 1, 6, '', ('93.184.216.34', 80)), (2, 1, 6, '', ('192.168.1.5', 80))]
        with mock.patch('socket.getaddrinfo', return_value=infos):
            self.assertIsNone(remoteimg._public_ip('mixed.example', 80))
        with mock.patch('socket.getaddrinfo', return_value=infos[:1]):
            self.assertEqual(remoteimg._public_ip('ok.example', 80), '93.184.216.34')

    def test_redirect_to_inside_refused_and_type_checked(self):
        calls = []

        class Resp:
            def __init__(self, status, location=None, body=b''):
                self.status, self._loc, self._body = status, location, body

            def getheader(self, name):
                return self._loc if name == 'Location' else None

            def close(self):
                pass

            def read(self, n):
                out, self._body = self._body[:n], self._body[n:]
                return out

        class Conn:
            plan = {}

            def __init__(self, host, port, ip, timeout):
                self.host = host

            def request(self, method, path, headers):
                calls.append((self.host, path))

            def getresponse(self):
                return Conn.plan[self.host]

            def close(self):
                pass
        Conn.plan = {'a.example': Resp(302, 'http://127.0.0.1/x'), 'b.example': Resp(200, body=b'<svg/>'),
                     'c.example': Resp(200, body=PNG)}
        with mock.patch.object(remoteimg, '_public_ip', side_effect=lambda h, p: None if h == '127.0.0.1' else '93.184.216.34'):
            self.assertEqual(remoteimg.fetch('https://a.example/i', opener=Conn), (None, 'address'))
            self.assertEqual(remoteimg.fetch('https://b.example/i', opener=Conn), (None, 'type'))
            self.assertEqual(remoteimg.fetch('https://c.example/i?q=1', opener=Conn), ('image/png', PNG))
        self.assertIn(('c.example', '/i?q=1'), calls)

    def test_fetch_all_limits_order_and_cache(self):
        seen = []

        def fetcher(url, limit, deadline):
            seen.append(url)
            if 'bad' in url:
                return None, 'status'
            if 'slow' in url:
                time.sleep(0.3)
            return 'image/png', PNG
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(remoteimg, 'DEADLINE', 0.1):
            cache = Path(tmp) / 'imgcache'
            images, skipped = remoteimg.fetch_all(['https://x/1', 'https://x/bad', 'https://x/slow'], fetcher, cache)
            self.assertEqual(images, {'https://x/1': 'data:image/png;base64,' + base64.b64encode(PNG).decode()})
            self.assertEqual(skipped, {'https://x/bad': 'status', 'https://x/slow': 'time'})
            time.sleep(0.5)  # the late image lands in the cache
            seen.clear()
            images, skipped = remoteimg.fetch_all(['https://x/1', 'https://x/slow'], fetcher, cache)
            self.assertEqual(set(images), {'https://x/1', 'https://x/slow'})
            self.assertEqual(seen, [])
        with mock.patch.object(remoteimg, 'TOTAL_MAX', len(PNG) + 1):
            images, skipped = remoteimg.fetch_all(['https://x/a', 'https://x/b'], fetcher)
            self.assertEqual(list(images), ['https://x/a'])
            self.assertEqual(skipped, {'https://x/b': 'size'})


if __name__ == '__main__':
    unittest.main()
