"""Google Calendar provider (read only in calendar 0.1).

Sync protocol (SPEC-calendar §3.1): the first read lists everything without timeMin/timeMax (syncToken does not go
with them), singleEvents=false (a series is one rule + its exceptions), showDeleted=true, all pages until
nextSyncToken; later reads pass the syncToken. 410 Gone -> Gone (the caller rebuilds only its copy of the provider).
"""
from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

from providers import Gone, NotAllowed

API = 'https://www.googleapis.com/calendar/v3'
FIELDS = ('nextPageToken,nextSyncToken,timeZone,items(id,iCalUID,etag,status,summary,location,description,start,end,'
          'recurrence,recurringEventId,originalStartTime,transparency,organizer,attendees,hangoutLink,htmlLink,updated,'
          'eventType,conferenceData/entryPoints)')


def http(method, url, headers, body=None, timeout=60):
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw, status = resp.read(), resp.status
    except urllib.error.HTTPError as err:
        raw, status = err.read(), err.code
    try:
        return status, json.loads(raw) if raw else {}
    except ValueError:
        return status, {}


def _time(value):
    """Google start/end/originalStartTime -> (utc_iso or '', date or '', tz)."""
    value = value or {}
    if value.get('date'):
        return '', value['date'], value.get('timeZone') or ''
    dt = value.get('dateTime') or ''
    if not dt:
        return '', '', value.get('timeZone') or ''
    parsed = datetime.fromisoformat(dt.replace('Z', '+00:00'))
    return parsed.astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'), '', value.get('timeZone') or ''


def event(item, cal_tz=''):
    """One Google event -> the row the store keeps (copy of the provider; never edited by us)."""
    s_utc, s_date, s_tz = _time(item.get('start'))
    e_utc, e_date, _ = _time(item.get('end'))
    o_utc, o_date, _ = _time(item.get('originalStartTime'))
    attendees, mine = [], ''
    for a in item.get('attendees') or []:
        attendees.append({'email': a.get('email', ''), 'name': a.get('displayName', ''),
                          'response': a.get('responseStatus', ''), 'organizer': bool(a.get('organizer')),
                          'optional': bool(a.get('optional')), 'resource': bool(a.get('resource'))})
        if a.get('self'):
            mine = a.get('responseStatus', '')
    video = item.get('hangoutLink') or ''
    if not video:
        for ep in (item.get('conferenceData') or {}).get('entryPoints') or []:
            if ep.get('entryPointType') == 'video' and str(ep.get('uri', '')).startswith('https:'):
                video = ep['uri']
                break
    org = item.get('organizer') or {}
    return {
        'id': item.get('id', ''), 'ical_uid': item.get('iCalUID', ''), 'etag': item.get('etag', ''),
        'status': item.get('status', 'confirmed'), 'summary': item.get('summary', ''),
        'location': item.get('location', ''), 'description': item.get('description', ''),
        'start_utc': s_utc, 'end_utc': e_utc, 'start_date': s_date, 'end_date': e_date,
        'tz': s_tz or cal_tz, 'all_day': 1 if s_date else 0,
        'recurrence': item.get('recurrence') or [], 'recurring_id': item.get('recurringEventId', ''),
        'original_start': o_utc or o_date, 'transparency': item.get('transparency', 'opaque'),
        'organizer': org.get('email', ''), 'organizer_self': 1 if org.get('self') else 0,
        'attendees': attendees, 'my_response': mine, 'video': video, 'html_link': item.get('htmlLink', ''),
        'updated': item.get('updated', ''), 'kind': item.get('eventType', 'default'),
    }


class Google:
    name = 'google'

    def __init__(self, token, request=None, sleep=time.sleep):
        self.token, self._request, self._sleep = token, request or http, sleep

    def capabilities(self):
        return {'read': True, 'write': False, 'rsvp': False, 'proposeTime': False}

    def get(self, path, **params):
        url = API + path + ('?' + urllib.parse.urlencode(params, doseq=True) if params else '')
        auth_retried = False
        for attempt in range(6):
            access = self.token()
            status, data = self._request('GET', url, {'Authorization': 'Bearer ' + access})
            if status == 200:
                return data
            if status == 401 and not auth_retried:
                auth_retried = True
                getattr(self.token, 'invalidate', lambda t: None)(access)
                continue
            if status == 410:
                raise Gone(path)
            if status in (401, 403) and not _rate_limited(data):
                raise NotAllowed(f'{status} {_reason(data)}')
            if status in (403, 429, 500, 502, 503, 504):
                self._sleep(min(2 ** attempt, 30))
                continue
            raise RuntimeError(f'Google Calendar {status} {_reason(data)}')
        raise RuntimeError('Google Calendar: too many retries')

    def calendars(self):
        out, page = [], None
        while True:
            params = {'maxResults': 250, 'showHidden': 'true'}
            if page:
                params['pageToken'] = page
            data = self.get('/users/me/calendarList', **params)
            for c in data.get('items') or []:
                out.append({'id': c.get('id', ''), 'title': c.get('summaryOverride') or c.get('summary', ''),
                            'color': c.get('backgroundColor', ''), 'role': c.get('accessRole', ''),
                            'primary': 1 if c.get('primary') else 0, 'tz': c.get('timeZone', ''),
                            'hidden': 1 if c.get('hidden') else 0, 'selected': 1 if c.get('selected') else 0})
            page = data.get('nextPageToken')
            if not page:
                return out

    def sync(self, cal_id, token=None, cal_tz=''):
        """All changes since `token` (everything when None) -> (events, next_token). Raises Gone on 410."""
        out, page = [], None
        path = '/calendars/' + urllib.parse.quote(cal_id, safe='') + '/events'
        while True:
            params = {'maxResults': 2500, 'singleEvents': 'false', 'showDeleted': 'true', 'fields': FIELDS}
            if token:
                params['syncToken'] = token
            if page:
                params['pageToken'] = page
            data = self.get(path, **params)
            tz = data.get('timeZone') or cal_tz
            out.extend(event(i, tz) for i in data.get('items') or [])
            page = data.get('nextPageToken')
            if not page:
                return out, data.get('nextSyncToken') or ''


def _reason(data):
    err = (data or {}).get('error') if isinstance(data, dict) else None
    if isinstance(err, dict):
        errs = err.get('errors') or [{}]
        return str(errs[0].get('reason') or err.get('status') or err.get('message') or '')[:80]
    return str(err or '')[:80]


def _rate_limited(data):
    return _reason(data) in ('rateLimitExceeded', 'userRateLimitExceeded', 'quotaExceeded')
