# SPDX-License-Identifier: AGPL-3.0-or-later
"""Comment threads for the chat agent (canvas stage M3, SPEC.md in chat CHAT-20261028152233-8P34).

The CO Agent plugin in the editor sends threads marked @agent (20 s after the last change, or at once with "Send now"
and when the editor closes). canvas-bridge queues one job for the chat's agent through db.php (op canvas_job, a host
op of the module canvas since Freedom F10: its own agent session, a collapsed row in the chat), waits for the answer
and keeps it until a plugin of the same document claims it, applies the ops as tracked changes, writes the reply into
the thread and confirms.

  POST /cb/agent/<sid>/ask      {threads: [...], doc: "<text>"}   -> {accepted: [tkey...]}
  GET  /cb/agent/<sid>/poll                                      -> {answers: [...], status: {tkey: state}}
  POST /cb/agent/<sid>/applied  {aid, tkey}                      -> {ok}

Asks live in %LOCALAPPDATA%/AIWorkbenchCanvas/asks.json (14 days). The push key aiwb/co-push authorizes db.php.
"""
from __future__ import annotations

import base64
import gzip
import json
import logging
import os
import re
import sys
import secrets
import threading
import time
import urllib.request
from pathlib import Path

log = logging.getLogger('canvas-bridge')

# <repo>/modules/canvas/canvas-bridge -> <repo>/install (Freedom F10: canvas-bridge moved from <repo>/canvas-bridge)
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'install'))
import aiwb_install  # noqa: E402  Freedom F1: the site of this install

DB_API = os.environ.get('CANVAS_DB_API') or (os.environ.get('CANVAS_SITE') or aiwb_install.get('site.url', '')).rstrip('/') + '/agent/db.php'
AGENT_NAME = 'CO Agent'
KEEP_DAYS = 14
CLAIM_S = 90          # an answer claimed by one editor goes to another one if not applied within this time
POLL_S = 5            # job state checks
MAX_DOC = 300_000     # chars of the document text given to the agent
MAX_THREADS = 30
GIVE_UP_S = 45 * 60   # no answer this long after the ask (the job itself has 30 min): the threads get an error reply
MAX_TEXT = 20_000     # chars per thread message
OPS = ('replace', 'insert_after', 'delete')

_lock = threading.RLock()
_asks: list[dict] = []
_file: Path | None = None
_wake = threading.Event()


def init(state_dir: Path) -> None:
    global _file
    _file = state_dir / 'asks.json'
    try:
        _asks.extend(json.loads(_file.read_text(encoding='utf-8')))
    except FileNotFoundError:
        pass
    except Exception as e:
        log.warning('asks.json unreadable, starting empty: %s', e)
    threading.Thread(target=_worker, daemon=True, name='agent-threads').start()


def _save() -> None:
    with _lock:
        cut = time.time() - KEEP_DAYS * 86400
        _asks[:] = [a for a in _asks if a['created'] > cut]
        tmp = _file.with_suffix('.tmp')
        tmp.write_text(json.dumps(_asks, ensure_ascii=False, indent=1), encoding='utf-8')
        os.replace(tmp, _file)


# ---------- db.php ----------

def _db(payload: dict) -> dict:
    import keyring
    key = keyring.get_password('aiwb/co-push', 'default')
    if not key:
        raise RuntimeError('keyring aiwb/co-push is empty')
    raw = gzip.compress(json.dumps(payload, ensure_ascii=False).encode('utf-8'))
    # gzip+base64 body: the site firewall refuses bodies with code-like text (documents may contain any text)
    body = json.dumps({'gz': base64.b64encode(raw).decode('ascii')}).encode()
    req = urllib.request.Request(DB_API, data=body, method='POST', headers={
        'Content-Type': 'application/json', 'X-CO-Push-Key': key, 'User-Agent': 'canvas-bridge'})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read() or b'{}')
    except urllib.error.HTTPError as e:
        try:
            return json.loads(e.read() or b'{}') | {'httpStatus': e.code}
        except Exception:
            return {'error': f'http {e.code}', 'httpStatus': e.code}


# ---------- plugin requests ----------

def _clean_threads(raw) -> list[dict]:
    if not isinstance(raw, list) or not raw or len(raw) > MAX_THREADS:
        raise ValueError('threads: 1..%d' % MAX_THREADS)
    out = []
    for t in raw:
        msgs = t.get('messages') if isinstance(t, dict) else None
        if not isinstance(msgs, list) or not msgs or len(msgs) > 100:
            raise ValueError('thread messages')
        tkey, sig = str(t.get('tkey') or ''), str(t.get('sig') or '')
        if not re.fullmatch(r'[A-Za-z0-9_-]{6,64}', tkey) or not re.fullmatch(r'[A-Za-z0-9_-]{6,64}', sig):
            raise ValueError('tkey/sig')
        out.append({
            'tkey': tkey, 'sig': sig,
            'quote': str(t.get('quote') or '')[:2000],
            'messages': [{'author': str(m.get('author') or '')[:80], 'text': str(m.get('text') or '')[:MAX_TEXT]}
                         for m in msgs if isinstance(m, dict)],
        })
    return out


def ask(session: dict, body: dict) -> dict:
    """New threads from a plugin. A thread already asked with the same signature (same last message) is skipped."""
    if not session.get('edit'):
        raise PermissionError('view only')
    if not session.get('chatId'):
        raise ValueError('canvas agent works for chat files only')
    threads = _clean_threads(body.get('threads'))
    doc = str(body.get('doc') or '')[:MAX_DOC]
    rel = session['rel']
    with _lock:
        known = {(t['tkey'], t['sig']) for a in _asks if a['rel'] == rel and a['state'] != 'error' for t in a['threads']}
        new = [t for t in threads if (t['tkey'], t['sig']) not in known]
        if not new:
            return {'accepted': []}
        a = {'aid': secrets.token_hex(8), 'rel': rel, 'env': session['env'], 'created': time.time(),
             'state': 'queued', 'jobId': None, 'threads': new, 'doc': doc, 'answers': [], 'error': None,
             'tries': 0}
        _asks.append(a)
        _save()
    log.info('ask %s: %d thread(s) of %s from %s', a['aid'], len(new), rel, session['env'])
    _wake.set()
    return {'accepted': [t['tkey'] for t in new]}


def poll(session: dict, sid: str) -> dict:
    rel = session['rel']
    now = time.time()
    out, status = [], {}
    with _lock:
        for a in _asks:
            if a['rel'] != rel:
                continue
            for t in a['threads']:
                status[t['tkey']] = a['state'] if a['state'] != 'error' else 'error'
            if not session.get('edit'):
                continue
            for ans in a['answers']:
                if ans.get('applied'):
                    continue
                if ans.get('claimedBy') not in (None, sid) and now - ans.get('claimedAt', 0) < CLAIM_S:
                    continue
                ans['claimedBy'], ans['claimedAt'] = sid, now
                q = next((t['messages'][-1]['text'] for t in a['threads'] if t['tkey'] == ans['tkey']), '')
                out.append({'aid': a['aid'], 'tkey': ans['tkey'], 'reply': ans['reply'], 'ops': ans['ops'], 'question': q[:300]})
        if out:
            _save()
    return {'answers': out, 'status': status}


def applied(session: dict, sid: str, body: dict) -> dict:
    aid, tkey = str(body.get('aid') or ''), str(body.get('tkey') or '')
    with _lock:
        for a in _asks:
            if a['aid'] == aid and a['rel'] == session['rel']:
                for ans in a['answers']:
                    if ans['tkey'] == tkey:
                        ans['applied'] = time.time()
                        ans['appliedBy'] = sid[:6]
                        ans['result'] = body.get('result') if isinstance(body.get('result'), list) else None
                        _save()
                        log.info('answer %s/%s applied (%s)', aid, tkey, json.dumps(ans['result'])[:300])
                        return {'ok': True}
    return {'ok': False}


# ---------- the job ----------

def _prompt(a: dict) -> str:
    name = a['rel'].rsplit('/', 1)[-1]
    lines = [
        f'Canvas: comment threads in the document "{name}" (chat file {a["rel"].split("/scratch/", 1)[-1]}). '
        'The user marked them for you (@agent). Answer every thread.',
        '',
        'The document is open in the editor right now: do not change the file on disk. Your edits go into the editor '
        'as tracked changes (the user accepts or rejects them there), described as ops:',
        '- {"op": "replace", "find": "<exact current text inside one paragraph>", "with": "<new text>", "n": 1}',
        '- {"op": "insert_after", "find": "<exact text of the paragraph to insert after>", '
        '"paragraphs": ["<new paragraph>", ...], "style": "Heading 2" or null}',
        '- {"op": "delete", "find": "<exact text inside one paragraph>", "n": 1}',
        '"find" must match the current text exactly (copy it from the document below) and stay inside one paragraph; '
        'make it short but unique, or give "n" (1 = first occurrence). For a whole paragraph, replace its full text. '
        'No ops when the thread only asks a question.',
        '',
        'Finish your answer with exactly one JSON block and nothing after it:',
        '```json',
        '{"threads": [{"id": "<thread id>", "reply": "<what you did or your answer, short, in the language of the '
        'thread; when the thread is done, end with a question whether to close it>", "ops": [...]}]}',
        '```',
        '',
        f'Threads ({len(a["threads"])}):',
    ]
    for t in a['threads']:
        lines.append(f'[{t["tkey"]}] anchored to: "{t["quote"]}"' if t['quote'] else f'[{t["tkey"]}] (no anchor text)')
        for m in t['messages']:
            lines.append(f'  {m["author"]}: {m["text"]}')
    if a['doc']:
        lines += ['', '[Document text: one paragraph per line, headings as #; pending tracked changes show both the '
                      'deleted and the inserted text]', a['doc'], '[End of document]']
    else:
        lines += ['', f'The editor was closing and sent no text: read the document from {aiwb_install.workbench_root()}\\'
                      f'{a["rel"].replace("/", chr(92))} (saved on close); your ops are applied when it opens again.']
    return '\n'.join(lines)


def _summary(a: dict) -> str:
    first = a['threads'][0]['messages'][-1]['text'].strip().replace('\n', ' ')
    first = re.sub(r'^@agent\b\s*', '', first, flags=re.I)[:160]
    more = f' (+{len(a["threads"]) - 1} more)' if len(a['threads']) > 1 else ''
    return f'Canvas · {a["rel"].rsplit("/", 1)[-1]}: «{first}»{more}'


def parse_answer(text: str, tkeys: list[str]) -> list[dict]:
    """The last JSON block of the agent's answer -> [{tkey, reply, ops}] for the asked threads."""
    blocks = re.findall(r'```(?:json)?\s*(\{.*?\})\s*```', text or '', flags=re.S)
    cand = blocks[-1] if blocks else (text or '')[(text or '').find('{'):]
    try:
        data = json.loads(cand)
    except Exception:
        raise ValueError('no JSON block in the answer')
    items = data.get('threads') if isinstance(data, dict) else None
    if not isinstance(items, list):
        raise ValueError('answer without "threads"')
    out = []
    for it in items:
        if not isinstance(it, dict) or it.get('id') not in tkeys:
            continue
        ops = []
        for op in it.get('ops') or []:
            if isinstance(op, dict) and op.get('op') in OPS and isinstance(op.get('find'), str) and op['find']:
                ops.append({k: op[k] for k in ('op', 'find', 'with', 'paragraphs', 'style', 'n') if k in op})
        out.append({'tkey': it['id'], 'reply': str(it.get('reply') or '').strip()[:MAX_TEXT], 'ops': ops[:50]})
    return out


def _fail(a: dict, msg: str) -> None:
    a['state'], a['error'] = 'error', msg
    a['answers'] = [{'tkey': t['tkey'], 'reply': f'⚠ The agent could not answer: {msg}. Reply in this thread to ask again.',
                     'ops': []} for t in a['threads']]
    log.error('ask %s failed: %s', a['aid'], msg)


def _step(a: dict) -> None:
    late = time.time() - a['created'] > GIVE_UP_S
    gone = f'no answer within {GIVE_UP_S // 60} min' + (f' (job {a["jobId"]})' if a['jobId'] else '')
    if late and a['state'] == 'queued':
        return _fail(a, gone)
    if a['state'] == 'queued':
        r = _db({'op': 'canvas_job', 'path': a['rel'], 'env': a['env'], 'summary': _summary(a), 'prompt': _prompt(a)})
        if r.get('jobId'):
            a['state'], a['jobId'] = 'running', r['jobId']
            log.info('ask %s -> job %s (agent %s, resume %s)', a['aid'], r['jobId'], r.get('agent'), r.get('resume'))
        elif r.get('busy'):
            a['state_note'] = f'waits for {r["busy"]}'
        else:
            a['tries'] += 1
            if a['tries'] >= 5 or r.get('error') in ('forbidden', 'not_found', 'not_a_chat_file'):
                _fail(a, f'the job was not queued ({r.get("error") or r})')
            else:
                log.warning('ask %s: canvas_job %s (try %d)', a['aid'], r, a['tries'])
        return
    if a['state'] == 'running':
        # The job's result first: an answer that came while canvas-bridge was down (31.10) is still delivered.
        r = _db({'op': 'canvas_job_get', 'jobId': a['jobId']})
        st = r.get('state')
        if late and not (st == 'completed' and r.get('status') == 'completed'):
            return _fail(a, gone)
        if st in ('inbox', 'claimed', 'running'):
            return
        if st is None:
            # no state: the site or the network failed; the job itself may still be fine
            a['tries'] += 1
            if a['tries'] < 20:
                log.warning('ask %s: canvas_job_get %s (try %d)', a['aid'], r, a['tries'])
                return
        if st == 'completed' and r.get('status') == 'completed':
            try:
                ans = parse_answer(r.get('answer') or '', [t['tkey'] for t in a['threads']])
            except ValueError as e:
                return _fail(a, str(e))
            got = {x['tkey'] for x in ans}
            for t in a['threads']:
                if t['tkey'] not in got:
                    ans.append({'tkey': t['tkey'], 'reply': '⚠ The agent gave no answer for this thread. Reply here to ask again.', 'ops': []})
            a['answers'], a['state'] = ans, 'done'
            log.info('ask %s done: %d answer(s), %d op(s)', a['aid'], len(ans), sum(len(x['ops']) for x in ans))
            return
        _fail(a, (r.get('error') or r.get('status') or st or 'unknown error')[:300])


def _worker() -> None:
    while True:
        _wake.wait(POLL_S)
        _wake.clear()
        with _lock:
            todo = [a for a in _asks if a['state'] in ('queued', 'running')]
        for a in todo:
            try:
                _step(a)
            except Exception as e:  # network or site down: retried until GIVE_UP_S
                a['tries'] += 1
                log.warning('ask %s: %s (try %d)', a['aid'], e, a['tries'])
                if time.time() - a['created'] > GIVE_UP_S:
                    _fail(a, f'no answer within {GIVE_UP_S // 60} min (site unreachable: {e})'[:300])
            with _lock:
                _save()
