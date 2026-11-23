"""Calendar of one person on the home server: sync from the provider, the handler of the Calendar page, diagnostics.

  calcli.py --root DIR sync [--cred F]        read the provider into DIR/calendar.db (systemd timer calendar-sync@SLUG)
  calcli.py --root DIR status                 flat status (what the tray gets)
  calcli.py --root DIR range FROM TO          occurrences between two UTC ISO times (diagnostics)
  calcli.py --root DIR relay-handler          JSON lines for the home-relay worker (SPEC-calendar §5)

Environment (from /etc/calendar/SLUG.env): ACCOUNT, AIWB_ENV; the token comes from the shared account credential of
module provider-accounts (systemd credential `account`), narrowed to calendar.read.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[1] / 'provider-accounts' / 'home'))

import calapi  # noqa: E402
from calstore import CalStore  # noqa: E402

CAPABILITIES = ['calendar.read']


def token_source(cred=None):
    import accounts
    return accounts.TokenSource(accounts.credential_path(cred), capabilities=CAPABILITIES)


def sync(store, account, cred=None, provider_name='google', request=None, token=None):
    """One pass over every calendar of the account. Returns a summary (no event content)."""
    from providers import Gone, NotAllowed, provider
    try:
        prov = provider(provider_name, token or token_source(cred), request=request)
        store.put_calendars(provider_name, account, prov.calendars())
    except (NotAllowed, PermissionError, ValueError) as exc:
        store.set_meta('auth_error', str(exc)[:200])
        store.db.commit()
        return {'ok': False, 'error': 'not_allowed', 'detail': str(exc)[:200]}
    store.set_meta('auth_error', '')
    store.db.commit()
    out = {'ok': True, 'calendars': 0, 'events': 0, 'rebuilt': [], 'failed': []}
    for cal in store.calendars():
        if cal['provider'] != provider_name or cal['account'] != account or cal['role'] == 'freeBusyReader':
            continue
        out['calendars'] += 1
        token_ = cal['sync_token'] or None
        try:
            try:
                events, nxt = prov.sync(cal['id'], token_, cal['tz'])
            except Gone:
                store.forget_token(provider_name, account, cal['id'])
                out['rebuilt'].append(cal['id'])
                token_ = None
                events, nxt = prov.sync(cal['id'], None, cal['tz'])
            store.apply(provider_name, account, cal['id'], events, nxt, full=token_ is None)
            out['events'] += len(events)
        except NotAllowed as exc:
            store.set_error(provider_name, account, cal['id'], exc)
            out['failed'].append(cal['id'])
        except Exception as exc:  # one calendar failing does not stop the others
            store.set_error(provider_name, account, cal['id'], type(exc).__name__ + ': ' + str(exc)[:150])
            out['failed'].append(cal['id'])
    store.set_meta('synced', calapi.iso(calapi.now_utc()))
    store.db.commit()
    return out


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--root', type=Path, required=True)
    sub = p.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('sync')
    s.add_argument('--cred')
    sub.add_parser('status')
    r = sub.add_parser('range')
    r.add_argument('start')
    r.add_argument('end')
    sub.add_parser('relay-handler')
    a = p.parse_args(argv)
    store = CalStore(a.root)
    account = os.environ.get('ACCOUNT', '').strip().lower()
    if a.cmd == 'sync':
        if not account:
            sys.exit('ACCOUNT is not set (env file of the person)')
        print(json.dumps(sync(store, account, a.cred)))
    elif a.cmd == 'status':
        print(json.dumps(calapi.CalApi(store, account).status()))
    elif a.cmd == 'range':
        print(json.dumps(calapi.CalApi(store, account).handle({'action': 'range', 'args': {'from': a.start, 'to': a.end}}),
                         ensure_ascii=False, indent=1))
    else:
        calapi.handler_main(store)


if __name__ == '__main__':
    main()
