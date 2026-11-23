"""Remote images of one stored message, fetched by the home server for the Mail page ("Show images").

The page never loads anything from the internet itself (its CSP forbids it, and a tracking pixel would see the
person's device). On the person's click the handler takes the image addresses from the stored message (never from the
page, so the home server is not an open proxy), fetches them without cookies or referer, and answers data: URIs.

Guards: http/https only, public addresses only (every resolved address of the host, re-checked on each redirect, and
the connection goes to the checked address: no DNS rebinding into the home network), raster types checked by their
magic bytes (no SVG), limits on count, size and time. Images that came are cached for CACHE_DAYS in the person's
mailbox folder (imgcache/), so a second click is instant and shows what was too late the first time.
"""
from __future__ import annotations

import base64
import hashlib
import html as htmllib
from html.entities import html5
from html.parser import HTMLParser
import http.client
import ipaddress
import os
import re
import socket
import ssl
import tempfile
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed, TimeoutError as FuturesTimeout
from urllib.parse import urljoin, urlsplit

MAX_IMAGES = 40
IMAGE_MAX = 1536 * 1024       # bytes of one image
TOTAL_MAX = 3 * 1024 * 1024   # bytes of all images of one answer (base64 twice must stay under the site's 8 MB)
TIMEOUT = 5                   # seconds per connection step
DEADLINE = 7                  # seconds for the whole answer (the site lets a page call wait 10 s)
LATE = 20                     # seconds late images may still load into the cache
WORKERS = 6
CACHE_DAYS = 14
CACHE_MAX = 300 * 1024 * 1024  # bytes of the cache of one mailbox
REDIRECTS = 3
UA = 'Mozilla/5.0 (compatible; FreedomMail/1.0; image proxy)'
MAGIC = [(b'\x89PNG\r\n\x1a\n', 'image/png'), (b'\xff\xd8\xff', 'image/jpeg'), (b'GIF87a', 'image/gif'),
         (b'GIF89a', 'image/gif')]


def _attr(value):
    """Character references of an attribute value as a browser decodes them (HTML spec, attribute state): numeric ones
    with or without ';'; named ones with ';'; a legacy name without ';' (&amp, &copy) only when the next character is
    not '=' — so '&notit=2' of a query string stays as it is, like in the page's DOM."""
    def one(m):
        text, name, semi = m.group(0), m.group(2), m.group(3)
        if m.group(1):
            return htmllib.unescape(text if semi else text + ';')
        if semi:
            return htmllib.unescape(text) if name + ';' in html5 else text
        nxt = value[m.end():m.end() + 1]
        return htmllib.unescape(text) if name in html5 and nxt != '=' else text
    return re.sub(r'&(?:(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6})|([A-Za-z][A-Za-z0-9]{0,31}))(;?)', one, value)


# containers whose content the page never shows (mail.js drops them with everything inside)
# (not head: browsers move what follows an unclosed head into the body; not option: closed implicitly, inside select)
DROPPED = {'script', 'style', 'iframe', 'frameset', 'object', 'form', 'button', 'textarea', 'select', 'svg',
           'math', 'template', 'noscript', 'video', 'audio', 'title'}
ATTR_RE = re.compile(r'''[\s/]*([^\s"'>/=]+)(?:\s*=\s*("([^"]*)"|'([^']*)'|([^\s>]*)))?''')


def _src(tag_text):
    """The raw value of the first src attribute of one start tag's text (attributes tokenized in order, so 'src=' inside
    another attribute's quoted value never counts), or None."""
    pos = len(re.match(r'<[A-Za-z0-9]+', tag_text).group(0)) if re.match(r'<[A-Za-z0-9]+', tag_text) else 1
    while pos < len(tag_text):
        m = ATTR_RE.match(tag_text, pos)
        if not m or m.end() == pos:
            break
        pos = m.end()
        if m.group(1).lower() == 'src':
            return next((g for g in m.group(3, 4, 5) if g is not None), '')
    return None


class _Images(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.depth, self.raw = 0, []

    def handle_starttag(self, tag, attrs):
        if tag in DROPPED:
            self.depth += 1
        elif tag == 'img' and not self.depth:
            value = _src(self.get_starttag_text() or '')
            if value is not None:
                self.raw.append(value)

    def handle_startendtag(self, tag, attrs):  # <x/>: never opens a container
        if tag == 'img' and not self.depth:
            value = _src(self.get_starttag_text() or '')
            if value is not None:
                self.raw.append(value)

    def handle_endtag(self, tag):
        if tag in DROPPED and self.depth:
            self.depth -= 1


def image_urls(html):
    """Remote <img src> addresses of a message's html as the page sees them (real <img> elements only — not comments,
    not inside dropped containers; entities decoded like a browser), in order, unique."""
    parser = _Images()
    try:
        parser.feed(html or '')
        parser.close()
    except Exception:  # a parser surprise: no pictures rather than a broken handler
        return []
    out = []
    for value in parser.raw:
        raw = _attr(value).strip()
        if re.match(r'(?i)^(https?:)?//', raw) and raw not in out:
            out.append(raw)
        if len(out) >= MAX_IMAGES:
            break
    return out


def _kind(data):
    for magic, kind in MAGIC:
        if data.startswith(magic):
            return kind
    if data[:4] == b'RIFF' and data[8:12] == b'WEBP':
        return 'image/webp'
    return None


def _public_ip(host, port):
    """The first resolved address of host if every resolved address is public, else None."""
    try:
        infos = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
    except (OSError, UnicodeError):
        return None
    addrs = []
    for info in infos:
        ip = ipaddress.ip_address(info[4][0].split('%')[0])
        if getattr(ip, 'ipv4_mapped', None):
            ip = ip.ipv4_mapped
        if not ip.is_global or ip.is_multicast:
            return None
        addrs.append(str(ip))
    return addrs[0] if addrs else None


class _Pinned(http.client.HTTPConnection):
    def __init__(self, host, port, ip, timeout):
        super().__init__(host, port, timeout=timeout)
        self._ip = ip

    def connect(self):
        self.sock = self.transport = socket.create_connection((self._ip, self.port), self.timeout)


class _PinnedTLS(http.client.HTTPSConnection):
    def __init__(self, host, port, ip, timeout):
        super().__init__(host, port, timeout=timeout, context=ssl.create_default_context())
        self._ip = ip

    def connect(self):
        # `transport` stays reachable for the watchdog: getresponse() of a 'Connection: close' answer drops self.sock
        sock = socket.create_connection((self._ip, self.port), self.timeout)
        self.sock = self.transport = self._context.wrap_socket(sock, server_hostname=self.host)


def fetch(url, limit=IMAGE_MAX, deadline=None, opener=None):
    """(content_type, bytes) of one remote image, or (None, reason)."""
    deadline = deadline or time.monotonic() + DEADLINE
    if url.startswith('//'):
        url = 'https:' + url
    for _ in range(REDIRECTS + 1):
        if time.monotonic() > deadline:
            return None, 'time'
        parts = urlsplit(url)
        if parts.scheme not in ('http', 'https') or not parts.hostname or parts.username or parts.password:
            return None, 'address'
        try:
            port = parts.port or (443 if parts.scheme == 'https' else 80)
        except ValueError:
            return None, 'address'
        ip = _public_ip(parts.hostname, port)
        if not ip:
            return None, 'address'
        cls = _PinnedTLS if parts.scheme == 'https' else _Pinned
        conn = (opener or cls)(parts.hostname, port, ip, max(0.1, min(TIMEOUT, deadline - time.monotonic())))
        # an absolute limit for connect, TLS, headers and body: the socket is shut at the deadline (a server that
        # trickles bytes never keeps a worker longer); DNS stays bounded by the resolver's own timeouts
        watchdog = threading.Timer(max(0.0, deadline - time.monotonic()), _shut, (conn,))
        watchdog.daemon = True
        watchdog.start()
        responses = []
        try:
            path = (parts.path or '/') + ('?' + parts.query if parts.query else '')
            conn.request('GET', path, headers={'User-Agent': UA, 'Accept': 'image/png,image/jpeg,image/gif,image/webp'})
            resp = conn.getresponse()
            responses.append(resp)
            if resp.status in (301, 302, 303, 307, 308) and resp.getheader('Location'):
                url = urljoin(url, resp.getheader('Location'))
                continue
            if resp.status != 200:
                return None, 'status'
            length = resp.getheader('Content-Length')
            if length and length.isdigit() and int(length) > limit:
                return None, 'size'
            data = b''
            while len(data) <= limit:
                if time.monotonic() > deadline:
                    return None, 'time'
                chunk = resp.read1(65536) if hasattr(resp, 'read1') else resp.read(65536)
                if not chunk:
                    break
                data += chunk
            if len(data) > limit:
                return None, 'size'
            kind = _kind(data)
            return (kind, data) if kind else (None, 'type')
        except (OSError, http.client.HTTPException, ssl.SSLError):
            return None, 'network'
        finally:
            watchdog.cancel()
            for resp in responses:
                resp.close()
            conn.close()
    return None, 'redirects'


def _shut(conn):
    sock = getattr(conn, 'transport', None) or getattr(conn, 'sock', None)
    if sock is not None:
        try:
            sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass


def _cached(cache, url):
    if not cache:
        return None
    path = cache / (hashlib.sha256(url.encode()).hexdigest() + '.img')
    try:
        if time.time() - path.stat().st_mtime > CACHE_DAYS * 86400:
            return None
        data = path.read_bytes()
    except OSError:
        return None
    kind = _kind(data)
    return (kind, data) if kind else None


def _store(cache, url, data):
    if not cache:
        return
    try:
        cache.mkdir(mode=0o700, exist_ok=True)
        fd, tmp = tempfile.mkstemp(prefix='.part-', dir=cache)  # one file per writer: a parallel fetch never mixes in
        try:
            with os.fdopen(fd, 'wb') as fh:
                fh.write(data)
            os.replace(tmp, cache / (hashlib.sha256(url.encode()).hexdigest() + '.img'))
        finally:
            if os.path.exists(tmp):
                os.unlink(tmp)
    except OSError:
        pass


_pruned = [0.0]


def prune(cache, force=False):
    """Drop cached images older than CACHE_DAYS (and leftovers of interrupted writes), then the oldest beyond CACHE_MAX
    bytes; at most once an hour unless forced."""
    try:
        if not cache or not cache.is_dir():
            return
    except OSError:
        return
    if (not force and time.monotonic() - _pruned[0] < 3600 and _pruned[0]):
        return
    _pruned[0] = time.monotonic()
    limit, files = time.time() - CACHE_DAYS * 86400, []
    try:
        entries = list(cache.iterdir())
    except OSError:  # cache upkeep is best effort: never stops the mail handler
        return
    for path in entries:
        try:
            st = path.stat()
            if st.st_mtime < limit or (path.name.startswith('.part-') and st.st_mtime < time.time() - 600):
                path.unlink()
            elif path.suffix == '.img':
                files.append((st.st_mtime, st.st_size, path))
        except OSError:
            pass
    total = sum(f[1] for f in files)
    for _, size, path in sorted(files):
        if total <= CACHE_MAX:
            break
        try:
            path.unlink()
            total -= size
        except OSError:
            pass


_POOL = []


def _pool():
    """One bounded pool for the handler process: repeated clicks never add threads; queued fetches past their
    deadline return at once."""
    if not _POOL:
        _POOL.append(ThreadPoolExecutor(max_workers=WORKERS, thread_name_prefix='remoteimg'))
    return _POOL[0]


def fetch_all(urls, fetcher=fetch, cache=None):
    """{url: data URI} of the images that came, {url: reason} of the rest. Fetches in parallel within DEADLINE (the
    site waits ~10 s for an answer); images that came are kept in `cache`, so a second click shows what was late."""
    urls = urls[:MAX_IMAGES]
    got, skipped = {}, {}
    todo = []
    for url in urls:
        hit = _cached(cache, url)
        if hit:
            got[url] = hit
        else:
            todo.append(url)
    deadline = time.monotonic() + DEADLINE

    def job(url):
        # late images go on loading a little longer and land in the cache for the next click
        kind, data = fetcher(url, IMAGE_MAX, deadline + LATE)
        if kind:
            _store(cache, url, data)
        return kind, data
    prune(cache)
    if todo:
        pool = _pool()
        futures = {pool.submit(job, url): url for url in todo}
        try:
            for fut in as_completed(futures, timeout=max(0.05, deadline - time.monotonic())):
                url = futures[fut]
                try:
                    kind, data = fut.result()
                except Exception:
                    kind, data = None, 'network'
                if kind is None:
                    skipped[url] = data
                else:
                    got[url] = (kind, data)
        except FuturesTimeout:
            pass
        for url in todo:
            if url not in got and url not in skipped:
                skipped[url] = 'time'
    images, total = {}, 0
    for url in urls:  # message order, so the first pictures win the size limit
        if url in got:
            kind, data = got[url]
            if total + len(data) > TOTAL_MAX:
                skipped[url] = 'size'
                continue
            total += len(data)
            images[url] = f'data:{kind};base64,' + base64.b64encode(data).decode()
    return images, skipped
