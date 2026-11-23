"""Calendar handler of one person for the home relay (module home-relay): answers the person's Calendar page and tray.

The worker home-relay@<slug> opens the sealed requests of the person's pages and hands the ones for this module to this
handler (`calcli.py relay-handler`, one long-lived process, JSON lines on stdin/stdout, see
modules/home-relay/home/relayd.py). Binding: the handler serves this calendar only when it belongs to the environment
the worker serves (AIWB_ENV of /etc/calendar/<slug>.env == HOME_RELAY_ENV); otherwise every call gets `not_bound`.

Read only towards the provider (calendar 0.1). The only thing a call may change is our own view setting of a
calendar (shown / counts as busy), which never goes to the provider.
"""
from __future__ import annotations

import html
import json
import os
import re
import sys
from datetime import timedelta

from calstore import UTC, iso, mark_conflicts, now_utc, parse_utc, public  # noqa: F401  (re-exported for calcli)

RANGE_MAX_DAYS = 45      # one view (a week, a month of agenda) per call; the relay answer must stay well under 8 MB
DESCRIPTION_MAX = 20000
LINK = re.compile(r'''(?i)href\s*=\s*["']((?:https|mailto):[^"'\s<>]+)["']''')


def text_of(description):
    """Event description (Google allows some HTML) -> plain text + its https/mailto links; the page shows only text."""
    raw = description or ''
    links = []
    for u in LINK.findall(raw):
        u = html.unescape(u)
        if u not in links:
            links.append(u)
    t = re.sub(r'(?i)<br\s*/?>|</p\s*>|</div\s*>|</li\s*>', '\n', raw)
    t = re.sub(r'<[^>]*>', '', t)
    t = html.unescape(t)
    t = re.sub(r'[ \t\r\f\v]+', ' ', t)
    t = re.sub(r'\n\s*\n\s*\n+', '\n\n', t).strip()
    for u in re.findall(r'(?:https|mailto):[^\s<>"\')\]]+', t):
        if u not in links:
            links.append(u)
    return t[:DESCRIPTION_MAX], links[:50]


def _when(args, name):
    v = str(args.get(name) or '')
    try:
        return parse_utc(v)
    except ValueError:
        raise ValueError('bad_' + name)


class CalApi:
    def __init__(self, store, account):
        self.store, self.account = store, account

    # --- tray ---------------------------------------------------------------------------------------------------
    def status(self):
        """At most 20 flat values, no event content: the site sees them (home-relay status is not sealed)."""
        now = now_utc()
        auth = self.store.meta('auth_error')
        items = self.store.instances(now - timedelta(hours=12), now + timedelta(days=7))
        nxt = next((i for i in items if not i['allDay'] and i['_e'] > now and i['my'] != 'declined'
                    and i['_s'] < now + timedelta(hours=24)), None)
        conflicts = sum(1 for i in items if i['conflict'] and i['_e'] > now)
        return {'bound': True, 'account': self.account, 'synced': self.store.meta('synced'),
                'auth': 'needs_consent' if auth else ('ok' if self.store.meta('synced') else 'never'),
                'next_start': nxt['start'] if nxt else '', 'next_end': nxt['end'] if nxt else '',
                'conflicts': conflicts, 'calendars': len(self.store.calendars())}

    # --- page ---------------------------------------------------------------------------------------------------
    def handle(self, request):
        action, args = request.get('action'), request.get('args') or {}
        if not isinstance(args, dict):
            raise ValueError('bad_args')
        if action == 'overview':
            return self.overview()
        if action == 'range':
            return self.range(args)
        if action == 'event':
            return self.event(args)
        if action == 'upcoming':
            return self.upcoming(args)
        if action == 'calendar_set':
            return self.calendar_set(args)
        raise ValueError('unknown_action')

    def overview(self):
        cals = [{'provider': c['provider'], 'account': c['account'], 'id': c['id'], 'title': c['title'],
                 'color': c['color'], 'role': c['role'], 'primary': bool(c['is_primary']), 'tz': c['tz'],
                 'show': bool(c['show']), 'busy': bool(c['busy']), 'synced': c['synced'], 'error': c['error']}
                for c in self.store.calendars()]
        return {'account': self.account, 'synced': self.store.meta('synced'), 'authError': self.store.meta('auth_error'),
                'calendars': cals, 'capabilities': {'read': True, 'write': False}}

    def range(self, args):
        start, end = _when(args, 'from'), _when(args, 'to')
        if end <= start or end - start > timedelta(days=RANGE_MAX_DAYS):
            raise ValueError('bad_range')
        return {'from': iso(start), 'to': iso(end), 'synced': self.store.meta('synced'),
                'items': public(self.store.instances(start, end))}

    def upcoming(self, args):
        """The tray's popover: next 3 meetings today/tomorrow and the conflicts of the next 7 days (titles: sealed)."""
        now = now_utc()
        items = self.store.instances(now - timedelta(hours=12), now + timedelta(days=7))
        nxt = [i for i in items if not i['allDay'] and i['_e'] > now and i['my'] != 'declined'][:3]
        conflicts = [i for i in items if i['conflict'] and i['_e'] > now][:10]
        return {'next': public(nxt), 'conflicts': public(conflicts), 'synced': self.store.meta('synced')}

    def event(self, args):
        key = str(args.get('key') or '')
        base, _, occurrence = key.rpartition('~') if '~' in key else (key, '', '')
        row = self.store.event(base)
        if row is None:
            raise LookupError('not_found')
        text, links = text_of(row['description'])
        attendees = json.loads(row['attendees'] or '[]')
        return {'key': key, 'summary': row['summary'], 'location': row['location'], 'description': text, 'links': links,
                'status': row['status'], 'allDay': bool(row['all_day']), 'start': row['start_utc'], 'end': row['end_utc'],
                'date': row['start_date'], 'endDate': row['end_date'], 'occurrence': occurrence, 'tz': row['tz'],
                'organizer': row['organizer'], 'organizerSelf': bool(row['organizer_self']), 'attendees': attendees[:200],
                'my': row['my_response'], 'video': row['video'] if row['video'].startswith('https:') else '',
                'htmlLink': row['html_link'] if row['html_link'].startswith('https://www.google.com/calendar/') else '',
                'transparency': row['transparency'], 'series': bool(json.loads(row['recurrence'] or '[]')) or bool(row['recurring_id']),
                'tainted': bool(row['tainted']), 'guard': row['guard'], 'updated': row['updated']}

    def calendar_set(self, args):
        """Our own view of a calendar (never sent to the provider): shown, counts as busy."""
        cal = next((c for c in self.store.calendars() if c['id'] == args.get('id')
                    and c['provider'] == args.get('provider', 'google') and c['account'] == self.account), None)
        if cal is None:
            raise LookupError('not_found')
        self.store.set_calendar(cal['provider'], cal['account'], cal['id'],
                                show=args.get('show') if isinstance(args.get('show'), bool) else None,
                                busy=args.get('busy') if isinstance(args.get('busy'), bool) else None)
        return self.overview()


def handle_line(api, bound, line):
    """One JSON line of the relay worker -> one answer dict."""
    try:
        req = json.loads(line)
    except ValueError:
        return {'ok': False, 'error': 'bad_request'}
    op = req.get('op') if isinstance(req, dict) else None
    if op == 'status':
        if not bound:
            return {'status': {'bound': False}}
        try:
            return {'status': api.status()}
        except Exception:
            return {'status': {'bound': True, 'error': 'status_failed'}}
    if op != 'call':
        return {'ok': False, 'error': 'bad_request'}
    if not bound:
        return {'ok': False, 'error': 'not_bound'}
    try:
        return {'ok': True, 'result': api.handle({'action': req.get('action'), 'args': req.get('args') or {}})}
    except (ValueError, LookupError) as exc:
        return {'ok': False, 'error': str(exc)[:100]}
    except Exception:
        return {'ok': False, 'error': 'worker_error'}


def handler_main(store, stdin=None, stdout=None, environ=None):
    """Entry of `calcli.py relay-handler` (started by the home-relay worker with the person's calendar env file)."""
    environ = os.environ if environ is None else environ
    stdin, stdout = stdin or sys.stdin.buffer, stdout or sys.stdout.buffer
    account = environ.get('ACCOUNT', '').strip().lower()
    bound = bool(account) and bool(environ.get('AIWB_ENV')) and environ.get('AIWB_ENV') == environ.get('HOME_RELAY_ENV')
    if not bound:
        print('relay-handler: this calendar does not belong to the environment of the worker', file=sys.stderr, flush=True)
    store.db.execute('PRAGMA busy_timeout=10000')
    api = CalApi(store, account)
    for line in iter(stdin.readline, b''):
        out = handle_line(api, bound, line)
        stdout.write(json.dumps(out, ensure_ascii=False, separators=(',', ':')).encode() + b'\n')
        stdout.flush()
