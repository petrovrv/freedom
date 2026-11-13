// SPDX-License-Identifier: AGPL-3.0-or-later
// Sign-in "once and done" (TZ-CO-WEB §5.1, v1.2).
// Token sources, in order:
//   1. CO Desktop (window.coDesktop.getAccessToken): the desktop app holds the long-lived Google token.
//   2. <CO site>/auth/token.php: the refresh token sits encrypted in an HttpOnly cookie on this browser.
// JavaScript only ever sees short-lived access tokens, kept in this module's memory.

import { ALLOWLIST, type EnvironmentId } from './config';
import { inExtension, openInTab } from './extLink';
import { loadMembers } from './members';

export type AuthState =
  | { status: 'signed_out'; error?: string; checking?: boolean }
  | { status: 'signed_in'; email: string; homeEnv: EnvironmentId; owner: boolean; expiresAt: number }
  | { status: 'expired'; email: string; homeEnv: EnvironmentId; owner: boolean };

type Listener = (s: AuthState) => void;
type TokenReply = { access_token: string; expires_in: number; email: string };

interface CoDesktop {
  getAccessToken?: () => Promise<TokenReply>;
  signIn?: () => Promise<void>;
}

let token: string | null = null;
let state: AuthState = { status: 'signed_out', checking: true };
const listeners = new Set<Listener>();
let refreshTimer: number | undefined;
let inflight: Promise<boolean> | null = null;

function desktop(): CoDesktop | null {
  const d = (window as unknown as { coDesktop?: CoDesktop }).coDesktop;
  return d && typeof d.getAccessToken === 'function' ? d : null;
}

function emit(next: AuthState) {
  state = next;
  listeners.forEach((l) => l(state));
}

export function onAuth(l: Listener): () => void {
  listeners.add(l);
  l(state);
  return () => listeners.delete(l);
}

export function getAuth(): AuthState {
  return state;
}

export function getToken(): string | null {
  return token;
}

async function fetchToken(): Promise<TokenReply | null> {
  const d = desktop();
  if (d) {
    try {
      return await d.getAccessToken!();
    } catch {
      /* this desktop has no web token (participant device): use the browser session */
    }
  }
  const r = await fetch('/auth/token.php', {
    method: 'POST',
    headers: { 'X-CO-Auth': '1' },
    credentials: 'same-origin',
    cache: 'no-store',
  });
  if (r.status === 401) return null;
  if (!r.ok) throw new Error(`Sign-in service: HTTP ${r.status}`);
  return (await r.json()) as TokenReply;
}

async function accept(reply: TokenReply): Promise<boolean> {
  const email = String(reply.email || '').toLowerCase();
  // Freedom F1: who may use CO and their environments come from the server (members.ts); offline - the cached list.
  await loadMembers(reply.access_token);
  const entry = ALLOWLIST[email];
  if (!entry) {
    token = null;
    emit({ status: 'signed_out', error: `${email || 'This account'} is not allowed to use CO.` });
    return false;
  }
  token = reply.access_token;
  const lifetime = Math.max(60, Number(reply.expires_in) || 3599);
  window.clearTimeout(refreshTimer);
  // Renew five minutes before expiry; a failed renewal retries on the next API 401.
  refreshTimer = window.setTimeout(() => void refresh(), Math.max(30, lifetime - 300) * 1000);
  emit({ status: 'signed_in', email, homeEnv: entry.env, owner: entry.owner, expiresAt: Date.now() + lifetime * 1000 });
  return true;
}

// Silent renewal. Resolves true when a new token is in place.
export function refresh(): Promise<boolean> {
  if (inflight) return inflight;
  inflight = (async () => {
    try {
      const reply = await fetchToken();
      if (reply) return await accept(reply);
      token = null;
      if (state.status === 'signed_in' || state.status === 'expired') {
        emit({ status: 'expired', email: state.email, homeEnv: state.homeEnv, owner: state.owner });
      } else {
        emit({ status: 'signed_out' });
      }
      return false;
    } catch (e) {
      // Network or server trouble: keep the account, mark the token stale, retry on the next call.
      token = null;
      if (state.status === 'signed_in') {
        emit({ status: 'expired', email: state.email, homeEnv: state.homeEnv, owner: state.owner });
      } else if (state.status === 'signed_out') {
        emit({ status: 'signed_out', error: (e as Error).message });
      }
      return false;
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

// Called by API clients on HTTP 401.
export function markExpired() {
  token = null;
  void refresh();
}

// Interactive sign-in: CO Desktop runs its own Google flow; the browser goes through /auth/login.php.
export async function signIn(): Promise<void> {
  const d = desktop();
  if (d?.signIn) {
    await d.signIn();
    if (await refresh()) return;
  }
  const hint = state.status !== 'signed_out' ? `?hint=${encodeURIComponent(state.email)}` : '';
  if (inExtension()) {
    // 0.62.0: Google sign-in refuses to run in a frame: sign in in a tab, the panel shares the cookie. The Strict
    // session cookie does reach /auth/token.php from the panel frame: Chrome treats a site framed in an extension page
    // as same-site when the extension has host permission for it (cookie-test.mjs of the extension's tests).
    openInTab(`/auth/login.php${hint}`);
    for (let i = 0; i < 100; i++) {
      await new Promise((r) => setTimeout(r, 3000));
      if (await refresh()) return;
    }
    return;
  }
  window.location.assign(`/auth/login.php${hint}`);
  await new Promise(() => undefined); // the page navigates away
}

export async function signOut() {
  window.clearTimeout(refreshTimer);
  token = null;
  if (!desktop()) {
    try {
      await fetch('/auth/logout.php', { method: 'POST', headers: { 'X-CO-Auth': '1' }, credentials: 'same-origin' });
    } catch {
      /* the cookie is dropped by the server when reachable */
    }
  }
  emit({ status: 'signed_out' });
}

void refresh();
