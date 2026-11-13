// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F12 (SPEC §3.1a): the list of executors comes from the server (db.php op executors) instead of the constants
// Home and Site. applyExecutors() fills HOSTS of config.ts in place and the titles of routing.ts, so their readers stay
// as they were. 'home-main' and 'site' are always in the list (the first two executors, also without a server row).
// The last list is kept in localStorage, so a chosen executor survives a reload before the server answers.
import { HOSTS } from './config.ts';
import { HOME, SITE, setHostTitles } from './routing.ts';
import type { ExecutorInfo } from './dbapi.ts';

const STORAGE_KEY = 'co.executors';
let list: ExecutorInfo[] = [];

export const HOST_ID_RE = /^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/;

const DEFAULTS: Record<string, { label: string; title: string }> = {
  [HOME]: { label: 'Home', title: 'Home computer (BridgeAgent)' },
  [SITE]: { label: 'Site', title: 'Server agent on the CO site (works when the computer is off)' },
};

const OS_NAME: Record<string, string> = { windows: 'Windows', macos: 'macOS', linux: 'Linux' };

export function executorTitle(e: ExecutorInfo): string {
  const os = OS_NAME[e.os] ? `, ${OS_NAME[e.os]}` : '';
  return e.kind === 'site' ? DEFAULTS[SITE].title : `${e.title || e.id} (BridgeAgent${os}${e.ownerEnv ? `, ${e.ownerEnv}` : ''})`;
}

export function applyExecutors(next: ExecutorInfo[], persist = true): void {
  list = next.filter((e) => e && HOST_ID_RE.test(e.id));
  const ids = [HOME, SITE, ...list.map((e) => e.id).filter((id) => id !== HOME && id !== SITE)];
  const byId = new Map(list.map((e) => [e.id, e]));
  HOSTS.splice(0, HOSTS.length, ...ids
    .filter((id) => id === HOME || id === SITE || (byId.get(id)?.enabled !== false))
    .map((id) => {
      const e = byId.get(id);
      if (DEFAULTS[id] && (!e || !e.registered)) return { id, ...DEFAULTS[id] };
      return { id, label: id === HOME ? 'Home' : id === SITE ? 'Site' : (e?.title || id), title: e ? executorTitle(e) : id };
    }));
  const titles: Record<string, string> = {};
  for (const e of list) if (e.id !== HOME && e.id !== SITE && e.title) titles[e.id] = e.title;
  setHostTitles(titles);
  if (persist && typeof localStorage !== 'undefined') {
    try {
      // without the owner.json copies: paths and devices stay out of localStorage
      localStorage.setItem(STORAGE_KEY, JSON.stringify(list.map(({ owner: _o, machine: _m, ...rest }) => rest)));
    } catch {
      /* private mode */
    }
  }
}

export function loadCachedExecutors(): void {
  if (typeof localStorage === 'undefined') return;
  try {
    const v = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
    if (Array.isArray(v)) applyExecutors(v as ExecutorInfo[], false);
  } catch {
    /* ignore */
  }
}

export function executors(): ExecutorInfo[] {
  return list;
}

export function executorOf(id: string): ExecutorInfo | undefined {
  return list.find((e) => e.id === id);
}

// Hosts in the order auto should try them for this environment (the server's rank: own executor of the environment,
// Home, Site, then the others), only those that serve it and are switched on. Without a server list: Home, Site.
export function autoOrder(): string[] {
  const l = list.filter((e) => e.servesMe !== false && e.enabled !== false).sort((a, b) => a.rank - b.rank || a.id.localeCompare(b.id));
  const ids = l.map((e) => e.id);
  for (const id of [HOME, SITE]) if (!ids.includes(id)) ids.push(id);
  return ids;
}
