"""Shared sign-in of a person's provider account for the modules of their environment (module `provider-accounts`).

One consent per person and provider; a module asks for *capabilities* (mail.read, calendar.read, ...), never for raw
scopes. Capabilities a person has not granted yet are added later by an incremental consent, which keeps the earlier
ones (Google `include_granted_scopes`). On the home server the refresh token lives in one encrypted systemd credential
per person and provider (`/etc/credstore.encrypted/account.<slug>.<provider>.cred`, credential name `account`) that the
units of that person's modules load; the non-secret record of what was granted is `/etc/freedom-accounts/<slug>.json`.

A provider is a replaceable layer: `google` is implemented; `microsoft` (Graph) only maps capabilities so that a module
can already be written against the same names; connecting it is not implemented yet.

Access tokens are minted per module with only that module's capabilities (refresh with a narrower `scope`), so a mail
process never holds a calendar token, although the refresh token is shared.
"""
from __future__ import annotations

import base64
import hashlib
import json
import os
import secrets
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

CREDENTIAL_NAME = 'account'


class Provider:
    name = ''
    auth_url = ''
    token_url = ''
    base = ()                 # scopes of the sign-in itself (identity, offline access)
    capabilities = {}         # capability -> scope

    def scopes(self, caps):
        unknown = sorted(set(caps) - set(self.capabilities))
        if unknown:
            raise ValueError(f'{self.name}: unknown capabilities: ' + ', '.join(unknown))
        return sorted(set(self.base) | {self.capabilities[c] for c in caps})

    def granted(self, scopes):
        """Capabilities covered by a set of granted scopes."""
        have = set(scopes)
        return sorted(c for c, s in self.capabilities.items() if s in have)

    def auth_request(self, client_id, redirect, caps, login_hint=''):
        raise NotImplementedError(f'{self.name}: connecting is not implemented yet')

    def exchange(self, client_id, client_secret, redirect, code, verifier):
        raise NotImplementedError(f'{self.name}: connecting is not implemented yet')

    def identity(self, access_token):
        raise NotImplementedError(f'{self.name}: connecting is not implemented yet')


class Google(Provider):
    name = 'google'
    auth_url = 'https://accounts.google.com/o/oauth2/v2/auth'
    token_url = 'https://oauth2.googleapis.com/token'
    base = ('openid', 'email')
    capabilities = {
        'mail.read': 'https://www.googleapis.com/auth/gmail.readonly',
        'mail.modify': 'https://www.googleapis.com/auth/gmail.modify',
        'mail.compose': 'https://www.googleapis.com/auth/gmail.compose',
        'mail.send': 'https://www.googleapis.com/auth/gmail.send',
        'calendar.read': 'https://www.googleapis.com/auth/calendar.readonly',
        'calendar.write': 'https://www.googleapis.com/auth/calendar.events',
        'drive.appfiles': 'https://www.googleapis.com/auth/drive.file',
        'contacts.read': 'https://www.googleapis.com/auth/contacts.readonly',
    }

    def auth_request(self, client_id, redirect, caps, login_hint=''):
        """(url, state, verifier): installed-app OAuth with PKCE and a loopback redirect; earlier grants are kept."""
        verifier = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b'=').decode()
        state = secrets.token_urlsafe(16)
        query = {'response_type': 'code', 'client_id': client_id, 'redirect_uri': redirect,
                 'scope': ' '.join(self.scopes(caps)), 'access_type': 'offline', 'prompt': 'consent',
                 'include_granted_scopes': 'true', 'state': state,
                 'code_challenge': base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode(),
                 'code_challenge_method': 'S256'}
        if login_hint:
            query['login_hint'] = login_hint
        return self.auth_url + '?' + urllib.parse.urlencode(query), state, verifier

    def exchange(self, client_id, client_secret, redirect, code, verifier, request=None):
        body = urllib.parse.urlencode({'code': code, 'client_id': client_id, 'client_secret': client_secret,
                                       'redirect_uri': redirect, 'grant_type': 'authorization_code',
                                       'code_verifier': verifier}).encode()
        status, data = (request or http)('POST', self.token_url, {'Content-Type': 'application/x-www-form-urlencoded'}, body)
        if status != 200 or not isinstance(data, dict) or not data.get('access_token'):
            raise PermissionError(f'token exchange failed ({status})')
        return data

    def identity(self, access_token, request=None):
        status, data = (request or http)('GET', 'https://openidconnect.googleapis.com/v1/userinfo',
                                         {'Authorization': 'Bearer ' + access_token})
        if status != 200 or not isinstance(data, dict) or not data.get('email'):
            raise PermissionError(f'identity check failed ({status})')
        if data.get('email_verified') is False:
            raise PermissionError('the account e-mail is not verified')
        return data['email'].strip().lower()


class Microsoft(Provider):
    """Graph names only (not connectable yet): lets a module declare the same capabilities for a later Outlook."""
    name = 'microsoft'
    auth_url = 'https://login.microsoftonline.com/common/oauth2/v2.0/authorize'
    token_url = 'https://login.microsoftonline.com/common/oauth2/v2.0/token'
    base = ('openid', 'email', 'offline_access')
    capabilities = {
        'mail.read': 'Mail.Read',
        'mail.modify': 'Mail.ReadWrite',
        'mail.compose': 'Mail.ReadWrite',
        'mail.send': 'Mail.Send',
        'calendar.read': 'Calendars.Read',
        'calendar.write': 'Calendars.ReadWrite',
        'drive.appfiles': 'Files.ReadWrite.AppFolder',
        'contacts.read': 'Contacts.Read',
    }


PROVIDERS = {p.name: p for p in (Google(), Microsoft())}


def provider(name):
    try:
        return PROVIDERS[name]
    except KeyError:
        raise ValueError('unknown provider: ' + str(name)) from None


def merged(old, new_scopes, provider_name):
    """Credential after a consent: the newest refresh token, scopes = union of what was granted before and now."""
    return {**old, 'provider': provider_name, 'scopes': sorted(set(old.get('scopes') or ()) | set(new_scopes))}


def missing(cred_or_record, caps, provider_name='google'):
    """Capabilities from `caps` the person has not granted yet."""
    have = set(provider(provider_name).granted((cred_or_record or {}).get('scopes') or ()))
    return sorted(set(caps) - have)


def credential_path(explicit=None, legacy=('gmail',)):
    """--cred, else the systemd credential `account`, else an older module-specific one (e.g. `gmail`)."""
    if explicit:
        return Path(explicit)
    directory = os.environ.get('CREDENTIALS_DIRECTORY')
    if directory:
        for name in (CREDENTIAL_NAME, *legacy):
            if (Path(directory) / name).is_file():
                return Path(directory) / name
    raise ValueError('No account credential: pass --cred or run under the hub service')


class TokenSource:
    """Access tokens for one module: only the scopes of its capabilities (narrowed refresh), cached until expiry."""

    def __init__(self, path=None, request=None, cred=None, capabilities=None, require_account=None):
        self._request = request or http
        self._token, self._until = None, 0.0
        self._lock = threading.Lock()
        cred = cred if cred is not None else json.loads(Path(path).read_text(encoding='utf-8'))
        require_account = bool(path) if require_account is None else require_account
        needed = ('client_id', 'client_secret', 'refresh_token') + (('account',) if require_account else ())
        absent = [k for k in needed if not cred.get(k)]
        if absent:
            raise ValueError('Credential file incomplete: ' + ', '.join(absent))
        self.account = str(cred.get('account') or '').strip().lower()
        self.provider = provider(cred.get('provider') or 'google')
        self._cred = cred
        self._scope = ''
        if capabilities:
            lacking = missing(cred, capabilities, self.provider.name) if cred.get('scopes') else []
            if lacking:
                raise PermissionError('Not granted yet: ' + ', '.join(lacking) + '; the person must allow it (accounts connect)')
            # only the module's own scopes (a narrowed refresh must be a subset of what was granted; checked 22.11.2026)
            self.provider.scopes(capabilities)
            self._scope = ' '.join(sorted({self.provider.capabilities[c] for c in capabilities})) if cred.get('scopes') else ''

    def invalidate(self, rejected_token):
        with self._lock:
            if self._token == rejected_token:
                self._token, self._until = None, 0.0

    def __call__(self):
        with self._lock:
            return self._access_token()

    def _refresh(self, scope):
        form = {'client_id': self._cred['client_id'], 'client_secret': self._cred['client_secret'],
                'refresh_token': self._cred['refresh_token'], 'grant_type': 'refresh_token'}
        if scope:
            form['scope'] = scope
        return self._request('POST', self.provider.token_url, {'Content-Type': 'application/x-www-form-urlencoded'},
                             urllib.parse.urlencode(form).encode())

    def _access_token(self):
        if self._token and time.time() < self._until - 120:
            return self._token
        status, data = self._refresh(self._scope)
        if status != 200 or not isinstance(data, dict) or not data.get('access_token'):
            # invalid_grant: revoked, password change or a Testing-mode OAuth app (7-day refresh tokens)
            reason = data.get('error') if isinstance(data, dict) else ''
            raise PermissionError(f'Token refresh failed ({status} {reason}); the person must connect again')
        self._token, self._until = data['access_token'], time.time() + int(data.get('expires_in', 3600))
        return self._token


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
