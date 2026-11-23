# calendar — the person's calendar app (📅)

The person looks at this calendar, not at Google Calendar: Google is only the transport. A copy of the calendars lives on
the home server, one store per person; the page reaches it through the module `home-relay` (the site carries only
end-to-end encrypted requests and answers). Design: `docs/SPEC-calendar.md`.

**0.1 is read only**: week, day and agenda views, event card, calendars to show or hide, conflicts (red frame), tray with
the next meeting of today and a red dot when the next 7 days have a conflict. Changes are made in Google Calendar; the
decision buttons, the agent and writing to Google come with 0.2 (only on a person's click).

```
Google Calendar API -> calcli.py sync (timer calendar-sync@SLUG, user aiwb-SLUG, token narrowed to calendar.read)
                    -> /srv/data/calendar/SLUG/calendar.db (provider copy + own layer)
page 📅 / tray -> cal_status, cal_call (server/ops.php) -> home-relay -> home-relay@SLUG -> calcli.py relay-handler
```

- **Provider layer** (`home/providers/`): one interface (`calendars`, `sync`, `capabilities`), Google now; Graph and ICS
  later. Sync without a time window (the sync token needs that), `singleEvents=false`, `showDeleted=true`; `410 Gone`
  rebuilds only the provider copy (deleted rows stay as `cancelled`, the own layer is untouched).
- **Series** are kept as rule + exceptions and expanded per view in the series' time zone (wall time kept over DST);
  `EXDATE`/`RDATE` with `TZID`, `UNTIL` as a date or local time are accepted.
- **External text** (title, place, description, attendee names) passes read-guard when stored (`tainted`); the page shows
  it only as text, links only `https:`/`mailto:` on a click.
- **Status through the site** is flat and holds no titles: `next_start`, `next_end`, `conflicts`, `synced`, `auth`,
  `account`, `bound`. Titles go only inside sealed answers.
- **Binding**: the handler answers only when `AIWB_ENV` of `/etc/calendar/SLUG.env` equals the worker's environment
  (`HOME_RELAY_ENV`), otherwise `not_bound`.

## On the home server

`python home/hub/calctl.py [--host H --user U --key K] ...`

1. `deploy` — code to `/opt/calendar` (+ provider-accounts, read-guard), units `calendar-sync@.service/.timer`, the
   handler registered with `home-relay`.
2. `add SLUG --account A --env E` — the person's data folder and env file.
3. `connect SLUG ...` — the person adds `calendar.read` to their shared Google sign-in (module `provider-accounts`;
   one consent screen for everything granted so far plus the calendar).
4. `start SLUG` — sync every 5 minutes; `sync SLUG` — one run now; `status [SLUG]`.

The person's `home-relay@SLUG` worker must be paired (`relayctl.py pair SLUG`).

Checks: `python -m pytest home` and `php tests/server.php` (PHP 8.2+, from the module folder).
