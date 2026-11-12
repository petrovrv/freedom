# SPDX-License-Identifier: AGPL-3.0-or-later
"""Share and clean copy of a canvas document (stage M5, SPEC.md in chat CHAT-20261028152233-8P34).

- clean_docx: the docx without the agent's comment threads (a thread whose first message starts with @agent, or with a
  message of "CO Agent"); other comments and tracked changes stay. Plain text edits of the XML parts, so everything
  else in the file stays byte for byte.
- to_pdf: ONLYOFFICE conversion service (/converter on 127.0.0.1:8088, JWT); it downloads the source from a one-time
  URL of canvas-bridge.
- upload: the prepared file goes to the site in chunks (db.php op canvas_share_put, push key); CO Web then makes the
  Gmail draft with the user's own Google token (op canvas_share_draft). The PC never sees a Google token.
"""
from __future__ import annotations

import base64
import hashlib
import io
import json
import re
import zipfile

AGENT = 'CO Agent'
MARK = re.compile(r'^\s*@agent\b', re.I)
MAX_ATTACH = 25 * 1024 * 1024  # Gmail attachment limit
CHUNK = 4_000_000  # raw bytes per db.php call (its body limit is 8 MB after base64 and gzip)
MIME = {
    'docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    'pptx': 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
    'pdf': 'application/pdf',
}

_COMMENT = re.compile(r'<w:comment\b([^>]*)>(.*?)</w:comment>', re.S)
_ATTR = re.compile(r'([\w:]+)="([^"]*)"')
_TEXT = re.compile(r'<w:t(?:\s[^>]*)?>([^<]*)</w:t>')
_PARA_ID = re.compile(r'w14:paraId="([0-9A-Fa-f]+)"')


def _attrs(s: str) -> dict:
    return dict(_ATTR.findall(s))


def _unxml(s: str) -> str:
    return s.replace('&lt;', '<').replace('&gt;', '>').replace('&quot;', '"').replace('&apos;', "'").replace('&amp;', '&')


def agent_comment_ids(parts: dict[str, str]) -> tuple[set[str], set[str]]:
    """Ids (w:id) and paragraph ids (w14:paraId) of the comments in agent threads."""
    comments = []  # (id, author, text, paraIds)
    for m in _COMMENT.finditer(parts.get('word/comments.xml', '')):
        a = _attrs(m.group(1))
        comments.append((a.get('w:id', ''), a.get('w:author', ''), _unxml(''.join(_TEXT.findall(m.group(2)))),
                         _PARA_ID.findall(m.group(2))))
    parent = {}  # paraId -> paraIdParent (replies of a thread, commentsExtended.xml)
    for m in re.finditer(r'<w15:commentEx\b([^>]*)/?>', parts.get('word/commentsExtended.xml', '')):
        a = _attrs(m.group(1))
        if a.get('w15:paraIdParent'):
            parent[a.get('w15:paraId', '').upper()] = a['w15:paraIdParent'].upper()
    by_para = {}
    for c in comments:
        if c[3]:
            by_para[c[3][-1].upper()] = c  # a comment is known by the paraId of its last paragraph

    def root(c):
        seen = set()
        while c[3] and c[3][-1].upper() in parent and c[3][-1].upper() not in seen:
            seen.add(c[3][-1].upper())
            nxt = by_para.get(parent[c[3][-1].upper()])
            if not nxt:
                break
            c = nxt
        return c[0]

    threads: dict[str, list] = {}
    for c in comments:
        threads.setdefault(root(c), []).append(c)
    ids, paras = set(), set()
    for rid, members in threads.items():
        first = next((c for c in members if c[0] == rid), members[0])
        if MARK.match(first[2]) or any(c[1] == AGENT for c in members):
            for c in members:
                ids.add(c[0])
                paras.update(p.upper() for p in c[3])
    return ids, paras


def clean_docx(data: bytes) -> tuple[bytes, int]:
    """The docx without the agent's comment threads; returns (bytes, number of comments removed)."""
    zin = zipfile.ZipFile(io.BytesIO(data))
    names = zin.namelist()
    parts = {n: zin.read(n).decode('utf-8') for n in names
             if n in ('word/comments.xml', 'word/commentsExtended.xml', 'word/commentsIds.xml', 'word/commentsExtensible.xml')}
    ids, paras = agent_comment_ids(parts)
    if not ids:
        return data, 0
    idre = '|'.join(re.escape(i) for i in ids)
    marks = re.compile(r'<w:(?:commentRangeStart|commentRangeEnd|commentReference)\b[^>]*\bw:id="(?:%s)"[^>]*/>' % idre)
    durable = set()
    for m in re.finditer(r'<w16cid:commentId\b([^>]*)/>', parts.get('word/commentsIds.xml', '')):
        a = _attrs(m.group(1))
        if a.get('w16cid:paraId', '').upper() in paras:
            durable.add(a.get('w16cid:durableId', '').upper())

    def edit(name: str, text: str) -> str:
        if name == 'word/comments.xml':
            return _COMMENT.sub(lambda m: '' if _attrs(m.group(1)).get('w:id') in ids else m.group(0), text)
        if name == 'word/commentsExtended.xml':
            return re.sub(r'<w15:commentEx\b([^>]*)/>',
                          lambda m: '' if _attrs(m.group(1)).get('w15:paraId', '').upper() in paras else m.group(0), text)
        if name == 'word/commentsIds.xml':
            return re.sub(r'<w16cid:commentId\b([^>]*)/>',
                          lambda m: '' if _attrs(m.group(1)).get('w16cid:paraId', '').upper() in paras else m.group(0), text)
        if name == 'word/commentsExtensible.xml':
            return re.sub(r'<w16cex:commentExtensible\b([^>]*?)(?:/>|>.*?</w16cex:commentExtensible>)',
                          lambda m: '' if _attrs(m.group(1)).get('w16cex:durableId', '').upper() in durable else m.group(0),
                          text, flags=re.S)
        # the document, headers, footers, foot- and endnotes: anchors and reference marks of the removed comments
        return marks.sub('', text)

    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w') as zout:
        for info in zin.infolist():
            b = zin.read(info.filename)
            n = info.filename
            if n.startswith('word/') and n.endswith('.xml'):
                text = b.decode('utf-8')
                new = edit(n, text)
                if new != text:
                    b = new.encode('utf-8')
            zout.writestr(info, b, compress_type=info.compress_type)
    return out.getvalue(), len(ids)


def sha256(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def chunks(data: bytes):
    """(seq, total, base64) pieces for db.php."""
    total = max(1, -(-len(data) // CHUNK))
    for i in range(total):
        yield i, total, base64.b64encode(data[i * CHUNK:(i + 1) * CHUNK]).decode('ascii')


def converter_request(ds_internal: str, key: str, src_url: str, filetype: str, title: str, jwt_encode, ds_key: str):
    """urllib Request for the ONLYOFFICE conversion service (synchronous, result as a download URL)."""
    import urllib.request
    payload = {'async': False, 'filetype': filetype, 'outputtype': 'pdf', 'key': key, 'title': title, 'url': src_url}
    tok = jwt_encode(payload, ds_key)
    return urllib.request.Request(ds_internal + '/converter', data=json.dumps({**payload, 'token': tok}).encode(),
                                  method='POST', headers={'Content-Type': 'application/json', 'Accept': 'application/json',
                                                          'Authorization': 'Bearer ' + tok})
