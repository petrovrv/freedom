"""Calendar store of one person on the home server (SQLite, /srv/data/calendar/<slug>/calendar.db).

Two layers (SPEC-calendar §3.2): the copy of the provider (calendars, events; written only by sync) and our own layer
(proposals, holds, decisions, links; empty in 0.1, never sent to the provider without a person's click).
A series is kept as its rule + exceptions and expanded here for the window of a view (local wall time of the series'
time zone, so a 10:00 meeting stays at 10:00 across a DST change).
"""
from __future__ import annotations

import json
import re
import sqlite3
import sys
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

from dateutil import rrule

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'tools' / 'read-guard'))
try:
    import read_guard
except ImportError:  # the hub deploy ships tools/read-guard next to the module; a store without it marks nothing
    read_guard = None

SCHEMA = """
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS calendars (
  provider TEXT NOT NULL, account TEXT NOT NULL, id TEXT NOT NULL,
  title TEXT NOT NULL DEFAULT '', color TEXT NOT NULL DEFAULT '', role TEXT NOT NULL DEFAULT '',
  is_primary INTEGER NOT NULL DEFAULT 0, tz TEXT NOT NULL DEFAULT '',
  show INTEGER NOT NULL DEFAULT 1, busy INTEGER NOT NULL DEFAULT 1, gone INTEGER NOT NULL DEFAULT 0,
  sync_token TEXT NOT NULL DEFAULT '', synced TEXT NOT NULL DEFAULT '', error TEXT NOT NULL DEFAULT '',
  PRIMARY KEY (provider, account, id));
CREATE TABLE IF NOT EXISTS events (
  provider TEXT NOT NULL, account TEXT NOT NULL, cal_id TEXT NOT NULL, id TEXT NOT NULL,
  ical_uid TEXT NOT NULL DEFAULT '', etag TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'confirmed',
  summary TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '',
  start_utc TEXT NOT NULL DEFAULT '', end_utc TEXT NOT NULL DEFAULT '',
  start_date TEXT NOT NULL DEFAULT '', end_date TEXT NOT NULL DEFAULT '', tz TEXT NOT NULL DEFAULT '',
  all_day INTEGER NOT NULL DEFAULT 0, recurrence TEXT NOT NULL DEFAULT '[]', recurring_id TEXT NOT NULL DEFAULT '',
  original_start TEXT NOT NULL DEFAULT '', transparency TEXT NOT NULL DEFAULT 'opaque',
  organizer TEXT NOT NULL DEFAULT '', organizer_self INTEGER NOT NULL DEFAULT 0, attendees TEXT NOT NULL DEFAULT '[]',
  my_response TEXT NOT NULL DEFAULT '', video TEXT NOT NULL DEFAULT '', html_link TEXT NOT NULL DEFAULT '',
  updated TEXT NOT NULL DEFAULT '', kind TEXT NOT NULL DEFAULT 'default',
  tainted INTEGER NOT NULL DEFAULT 0, guard TEXT NOT NULL DEFAULT '', seen INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (provider, account, cal_id, id));
CREATE INDEX IF NOT EXISTS events_series ON events (provider, account, cal_id, recurring_id);
CREATE INDEX IF NOT EXISTS events_uid ON events (ical_uid);
CREATE TABLE IF NOT EXISTS proposals (id INTEGER PRIMARY KEY, created TEXT NOT NULL, source TEXT NOT NULL DEFAULT '',
  data TEXT NOT NULL DEFAULT '{}', state TEXT NOT NULL DEFAULT 'open');
CREATE TABLE IF NOT EXISTS holds (id INTEGER PRIMARY KEY, created TEXT NOT NULL, start_utc TEXT NOT NULL,
  end_utc TEXT NOT NULL, reason TEXT NOT NULL DEFAULT '', until TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'open');
CREATE TABLE IF NOT EXISTS decisions (id INTEGER PRIMARY KEY, at TEXT NOT NULL, what TEXT NOT NULL,
  sent TEXT NOT NULL DEFAULT '', answer TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS links (id INTEGER PRIMARY KEY, ical_uid TEXT NOT NULL, kind TEXT NOT NULL, ref TEXT NOT NULL);
"""

EVENT_FIELDS = ['id', 'ical_uid', 'etag', 'status', 'summary', 'location', 'description', 'start_utc', 'end_utc',
                'start_date', 'end_date', 'tz', 'all_day', 'recurrence', 'recurring_id', 'original_start',
                'transparency', 'organizer', 'organizer_self', 'attendees', 'my_response', 'video', 'html_link',
                'updated', 'kind']
MAX_INSTANCES = 1000   # of one series in one window (a daily series over a year is 366)
UTC = timezone.utc


def now_utc():
    return datetime.now(UTC)


def iso(dt):
    return dt.astimezone(UTC).strftime('%Y-%m-%dT%H:%M:%SZ')


def parse_utc(text):
    return datetime.strptime(text, '%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=UTC)


def zone(name):
    try:
        return ZoneInfo(name) if name else UTC
    except Exception:
        return UTC


def guard(texts):
    """read-guard on the external text of an event -> sorted rule names ([] = clean)."""
    if read_guard is None:
        return []
    reasons = set()
    for text in texts:
        if text:
            result = read_guard.scan(text)
            if result.suspicious:
                reasons |= {s['rule'] for s in result.signals} or {'suspicious'}
    return sorted(reasons)


class CalStore:
    def __init__(self, root):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(str(self.root / 'calendar.db'), timeout=30)
        self.db.row_factory = sqlite3.Row
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.executescript(SCHEMA)

    # --- meta ---------------------------------------------------------------------------------------------------
    def meta(self, key, default=''):
        row = self.db.execute('SELECT value FROM meta WHERE key=?', (key,)).fetchone()
        return row[0] if row else default

    def set_meta(self, key, value):
        self.db.execute('INSERT INTO meta(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value',
                        (key, str(value)))

    # --- provider copy ------------------------------------------------------------------------------------------
    def put_calendars(self, provider, account, cals):
        """Calendar list from the provider: new ones shown and busy by default (hidden at Google -> not shown)."""
        seen = set()
        with self.db:
            for c in cals:
                seen.add(c['id'])
                self.db.execute(
                    'INSERT INTO calendars(provider,account,id,title,color,role,is_primary,tz,show,busy,gone)'
                    ' VALUES(?,?,?,?,?,?,?,?,?,?,0) ON CONFLICT(provider,account,id) DO UPDATE SET title=excluded.title,'
                    ' color=excluded.color, role=excluded.role, is_primary=excluded.is_primary, tz=excluded.tz, gone=0',
                    (provider, account, c['id'], c.get('title', ''), c.get('color', ''), c.get('role', ''),
                     c.get('primary', 0), c.get('tz', ''), 0 if c.get('hidden') else 1,
                     1 if c.get('role') in ('owner', 'writer') and not c.get('hidden') else 0))
            for row in self.db.execute('SELECT id FROM calendars WHERE provider=? AND account=?', (provider, account)).fetchall():
                if row[0] not in seen:
                    self.db.execute('UPDATE calendars SET gone=1 WHERE provider=? AND account=? AND id=?', (provider, account, row[0]))

    def calendars(self, all_=False):
        q = 'SELECT * FROM calendars' + ('' if all_ else ' WHERE gone=0') + ' ORDER BY is_primary DESC, title'
        return [dict(r) for r in self.db.execute(q)]

    def set_calendar(self, provider, account, cal_id, show=None, busy=None):
        with self.db:
            if show is not None:
                self.db.execute('UPDATE calendars SET show=? WHERE provider=? AND account=? AND id=?', (1 if show else 0, provider, account, cal_id))
            if busy is not None:
                self.db.execute('UPDATE calendars SET busy=? WHERE provider=? AND account=? AND id=?', (1 if busy else 0, provider, account, cal_id))

    def apply(self, provider, account, cal_id, events, token, full=False):
        """Changes of one sync and the new token in one transaction (the token only after the last page).
        full=True (first read, or after 410): rows the provider did not list are marked cancelled, never deleted."""
        with self.db:
            if full:
                self.db.execute('UPDATE events SET seen=0 WHERE provider=? AND account=? AND cal_id=?', (provider, account, cal_id))
            for e in events:
                reasons = guard([e.get('summary'), e.get('location'), e.get('description')]
                                + [a.get('name', '') for a in e.get('attendees') or []])
                row = {k: e.get(k, '') for k in EVENT_FIELDS}
                row['recurrence'] = json.dumps(e.get('recurrence') or [])
                row['attendees'] = json.dumps(e.get('attendees') or [], ensure_ascii=False)
                row['all_day'] = int(e.get('all_day') or 0)
                row['organizer_self'] = int(e.get('organizer_self') or 0)
                old = self.db.execute('SELECT * FROM events WHERE provider=? AND account=? AND cal_id=? AND id=?',
                                      (provider, account, cal_id, row['id'])).fetchone()
                if row['status'] == 'cancelled' and old is not None and not row['start_utc'] and not row['start_date']:
                    # a deleted event comes back with its id only: keep what we knew, mark it cancelled
                    self.db.execute('UPDATE events SET status=?, etag=?, updated=?, seen=1 WHERE provider=? AND account=? AND cal_id=? AND id=?',
                                    ('cancelled', row['etag'], row['updated'], provider, account, cal_id, row['id']))
                    continue
                cols = ['provider', 'account', 'cal_id'] + EVENT_FIELDS + ['tainted', 'guard', 'seen']
                vals = [provider, account, cal_id] + [row[k] for k in EVENT_FIELDS] + [1 if reasons else 0, ','.join(reasons), 1]
                self.db.execute(f'INSERT OR REPLACE INTO events({",".join(cols)}) VALUES({",".join("?" * len(cols))})', vals)
            if full:
                self.db.execute("UPDATE events SET status='cancelled', seen=1 WHERE provider=? AND account=? AND cal_id=? AND seen=0",
                                (provider, account, cal_id))
            self.db.execute('UPDATE calendars SET sync_token=?, synced=?, error=? WHERE provider=? AND account=? AND id=?',
                            (token, iso(now_utc()), '', provider, account, cal_id))

    def forget_token(self, provider, account, cal_id):
        """410 Gone: only the provider copy is read again (rows stay; our own layer is untouched)."""
        with self.db:
            self.db.execute("UPDATE calendars SET sync_token='' WHERE provider=? AND account=? AND id=?", (provider, account, cal_id))

    def set_error(self, provider, account, cal_id, error):
        with self.db:
            self.db.execute('UPDATE calendars SET error=? WHERE provider=? AND account=? AND id=?', (str(error)[:200], provider, account, cal_id))

    # --- views --------------------------------------------------------------------------------------------------
    def instances(self, start, end, only_shown=True):
        """Every occurrence that overlaps [start, end) (aware datetimes), series expanded, cancelled left out.
        -> list of dicts: key, cal, start/end (UTC ISO) or date/end_date, all_day, summary, ..."""
        cals = {(c['provider'], c['account'], c['id']): c for c in self.calendars()}
        out = []
        rows = self.db.execute("SELECT * FROM events WHERE status != 'cancelled' AND recurring_id = ''").fetchall()
        exceptions = {}
        for r in self.db.execute("SELECT * FROM events WHERE recurring_id != ''"):
            exceptions.setdefault((r['provider'], r['account'], r['cal_id'], r['recurring_id']), []).append(r)
        for r in rows:
            cal = cals.get((r['provider'], r['account'], r['cal_id']))
            if cal is None or (only_shown and not cal['show']):
                continue
            rules = json.loads(r['recurrence'] or '[]')
            if not rules:
                inst = _instance(r, cal)
                if inst and _overlaps(inst, start, end):
                    out.append(inst)
                continue
            exc = exceptions.get((r['provider'], r['account'], r['cal_id'], r['id']), [])
            moved = {e['original_start'] for e in exc}
            for occ_start in _occurrences(r, rules, start, end):
                okey = iso(occ_start) if isinstance(occ_start, datetime) else occ_start.isoformat()
                if okey in moved:
                    continue
                inst = _instance(r, cal, occ_start)
                if inst and _overlaps(inst, start, end):
                    out.append(inst)
            for e in exc:
                if e['status'] == 'cancelled':
                    continue
                inst = _instance(e, cal)
                if inst and _overlaps(inst, start, end):
                    out.append(inst)
        # exceptions whose series we do not have (e.g. an invitation to one occurrence only)
        series_ids = {(r['provider'], r['account'], r['cal_id'], r['id']) for r in rows}
        for skey, exc in exceptions.items():
            if skey in series_ids:
                continue
            cal = cals.get(skey[:3])
            if cal is None or (only_shown and not cal['show']):
                continue
            for e in exc:
                if e['status'] != 'cancelled':
                    inst = _instance(e, cal)
                    if inst and _overlaps(inst, start, end):
                        out.append(inst)
        out.sort(key=lambda i: (i['sort'], i['summary']))
        mark_conflicts(out)
        return out

    def event(self, key):
        provider, account, cal_id, eid = _split_key(key)
        r = self.db.execute('SELECT * FROM events WHERE provider=? AND account=? AND cal_id=? AND id=?',
                            (provider, account, cal_id, eid)).fetchone()
        return dict(r) if r else None


def _split_key(key):
    parts = str(key).split('|', 3)
    if len(parts) != 4:
        raise LookupError('bad_key')
    return parts


def _key(r):
    return '|'.join([r['provider'], r['account'], r['cal_id'], r['id']])


def _overlaps(inst, start, end):
    s, e = inst['_s'], inst['_e']
    return s < end and e > start


def _instance(r, cal, occ=None):
    """One occurrence -> view dict. occ: aware datetime (timed series) or date (all-day series) or None (the row)."""
    attendees = json.loads(r['attendees'] or '[]')
    base = {'key': _key(r), 'cal': cal['id'], 'calTitle': cal['title'], 'color': cal['color'],
            'busyCal': bool(cal['busy']), 'summary': r['summary'], 'location': r['location'],
            'status': r['status'], 'transparency': r['transparency'], 'my': r['my_response'],
            'video': r['video'], 'tz': r['tz'], 'tainted': bool(r['tainted']), 'attendees': len(attendees),
            'series': bool(json.loads(r['recurrence'] or '[]')) or bool(r['recurring_id'])}
    if r['all_day']:
        if not r['start_date']:
            return None
        d0 = date.fromisoformat(r['start_date'])
        d1 = date.fromisoformat(r['end_date']) if r['end_date'] else d0 + timedelta(days=1)
        if isinstance(occ, date) and not isinstance(occ, datetime):
            d1, d0 = occ + (d1 - d0), occ
        elif isinstance(occ, datetime):
            d1, d0 = occ.date() + (d1 - d0), occ.date()
        base.update({'allDay': True, 'date': d0.isoformat(), 'endDate': d1.isoformat(),
                     # all-day spans the person's day; the view compares it in UTC days (good enough for overlap)
                     '_s': datetime(d0.year, d0.month, d0.day, tzinfo=UTC), '_e': datetime(d1.year, d1.month, d1.day, tzinfo=UTC),
                     'sort': d0.isoformat()})
        if occ is not None:
            base['key'] += '~' + d0.isoformat()
        return base
    if not r['start_utc']:
        return None
    s = parse_utc(r['start_utc'])
    e = parse_utc(r['end_utc']) if r['end_utc'] else s
    if occ is not None:
        s, e = occ.astimezone(UTC), occ.astimezone(UTC) + (e - s)
        base['key'] += '~' + iso(s)
    base.update({'allDay': False, 'start': iso(s), 'end': iso(e), '_s': s, '_e': e, 'sort': iso(s)})
    return base


_PARAM = re.compile(r'^(RDATE|EXDATE)((?:;[^:]*)?):(.*)$', re.I)


def _dates(line, tz, all_day):
    m = _PARAM.match(line.strip())
    if not m:
        return None, []
    params = {k.upper(): v for k, _, v in (p.partition('=') for p in m.group(2).split(';') if p)}
    z = zone(params.get('TZID', '')) if params.get('TZID') else tz
    out = []
    for v in m.group(3).split(','):
        v = v.strip()
        if not v:
            continue
        if len(v) == 8:
            d = date(int(v[:4]), int(v[4:6]), int(v[6:8]))
            out.append(d if all_day else datetime(d.year, d.month, d.day, tzinfo=z))
        else:
            dt = datetime.strptime(v.rstrip('Z'), '%Y%m%dT%H%M%S')
            dt = dt.replace(tzinfo=UTC) if v.endswith('Z') else dt.replace(tzinfo=z)
            out.append(dt.date() if all_day else dt.astimezone(tz))
    return m.group(1).upper(), out


def _occurrences(r, rules, start, end):
    """Start of every occurrence of a series that may overlap [start, end).
    Timed: aware datetimes in the series' zone (wall time kept over DST); all-day: dates."""
    all_day = bool(r['all_day'])
    tz = zone(r['tz'])
    if all_day:
        if not r['start_date']:
            return []
        d0 = date.fromisoformat(r['start_date'])
        dtstart = datetime(d0.year, d0.month, d0.day)
        span = (date.fromisoformat(r['end_date']) - d0) if r['end_date'] else timedelta(days=1)
        lo = start.astimezone(UTC).replace(tzinfo=None) - span - timedelta(days=1)
        hi = end.astimezone(UTC).replace(tzinfo=None) + timedelta(days=1)
    else:
        if not r['start_utc']:
            return []
        s = parse_utc(r['start_utc'])
        dtstart = s.astimezone(tz)
        span = (parse_utc(r['end_utc']) - s) if r['end_utc'] else timedelta(0)
        lo, hi = start - span - timedelta(seconds=1), end
    rs = rrule.rruleset()
    for line in rules:
        up = line.strip().upper()
        if up.startswith('RRULE:'):
            text = _until_fix(line.strip()[6:], tz, all_day)
            try:
                rs.rrule(rrule.rrulestr(text, dtstart=dtstart))
            except (ValueError, TypeError):
                continue
        elif up.startswith(('RDATE', 'EXDATE')):
            kind, values = _dates(line, tz, all_day)
            for v in values:
                v = datetime(v.year, v.month, v.day) if all_day else v
                (rs.rdate if kind == 'RDATE' else rs.exdate)(v)
    rs.rdate(dtstart)  # DTSTART is always an occurrence (RFC 5545), also when the rule would skip it
    found = []
    for occ in rs.xafter(lo, count=MAX_INSTANCES, inc=True):
        if occ >= hi:
            break
        found.append(occ)
    if all_day:
        return [o.date() for o in found]
    return found


def _until_fix(rule, tz, all_day):
    """UNTIL must match DTSTART: aware (UTC) for a timed series, a plain date-time for an all-day one."""
    m = re.search(r'UNTIL=(\d{8})(T\d{6}Z?)?', rule, re.I)
    if not m:
        return rule
    d = m.group(1)
    if all_day:
        return rule[:m.start()] + f'UNTIL={d}T235959' + rule[m.end():]
    if m.group(2) and m.group(2).upper().endswith('Z'):
        return rule
    if m.group(2):
        local = datetime.strptime(d + m.group(2), '%Y%m%dT%H%M%S').replace(tzinfo=tz)
    else:
        local = datetime(int(d[:4]), int(d[4:6]), int(d[6:8]), 23, 59, 59, tzinfo=tz)
    return rule[:m.start()] + 'UNTIL=' + local.astimezone(UTC).strftime('%Y%m%dT%H%M%SZ') + rule[m.end():]


def counts_busy(i):
    return (i['busyCal'] and i['transparency'] != 'transparent' and i['status'] != 'cancelled'
            and i['my'] != 'declined')


def mark_conflicts(items):
    """Red frame on both of two overlapping busy occurrences; all-day ones only mark 'soft' (SPEC-calendar §5)."""
    timed = [i for i in items if counts_busy(i) and not i['allDay']]
    timed.sort(key=lambda i: i['_s'])
    for i in items:
        i['conflict'] = False
    for n, a in enumerate(timed):
        for b in timed[n + 1:]:
            if b['_s'] >= a['_e']:
                break
            if b['_e'] > a['_s']:
                a['conflict'] = b['conflict'] = True
    days = [i for i in items if counts_busy(i) and i['allDay']]
    for i in timed:
        if any(d['_s'] < i['_e'] and d['_e'] > i['_s'] for d in days):
            i['softConflict'] = True
    return items


def public(items):
    """Drop the private sort fields before an answer leaves the handler."""
    return [{k: v for k, v in i.items() if not k.startswith('_') and k != 'sort'} for i in items]
