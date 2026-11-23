"""Calendar providers: one interface, one file per provider (google now; graph and ics later, SPEC-calendar §3.1).

A provider gives
  calendars()                      -> [{id, title, color, role, primary, tz}]
  sync(cal_id, token)              -> (changes, next_token); changes are provider events already normalised by
                                      `event(...)` of the provider; raises Gone when the token is no longer valid
  capabilities()                   -> {'read': True, 'write': False, ...}
Writes (respond, insert, move, delete) come with stage 2 and are called only from a person's click.
"""


class Gone(Exception):
    """The sync token is no longer valid: read the calendar again from the start (only the provider copy is rebuilt)."""


class NotAllowed(Exception):
    """The account has not granted what is needed (revoked, or the capability was never given)."""


def provider(name, token, request=None):
    if name == 'google':
        from providers import google
        return google.Google(token, request=request)
    raise ValueError('unknown provider: ' + str(name))
