// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F1 (schema 21): the people of this workbench come from the server table members (db.php op members), not from
// constants. applyMembers() fills ENVIRONMENTS and ALLOWLIST of config.ts in place, so their readers stay as they were,
// and sets the --env color of each .env-<id>. The last list is kept in localStorage: the app starts with it and a
// database outage at sign-in does not lock anybody out.

import { ALLOWLIST, ENVIRONMENTS } from './config.ts';

export interface Member {
  env: string;
  email: string;
  name: string;
  color: string;
  role: 'owner' | 'member';
}

const STORAGE_KEY = 'co.members';
const DRIVE_KEY = 'co.driveRoot';
let members: Member[] = [];
// Where Google Drive is mounted on the home host (install.json drive.root, sent with op members); the default is the
// mount of Google Drive for desktop.
let driveRoot = 'G:\\My Drive';

function valid(x: unknown): x is Member {
  const m = x as Member;
  return !!m && typeof m.env === 'string' && /^[a-z0-9-]{1,40}$/.test(m.env) && typeof m.email === 'string'
    && typeof m.name === 'string' && (m.role === 'owner' || m.role === 'member');
}

export function applyMembers(list: Member[]): void {
  members = list.filter(valid).map((m) => ({ ...m, email: m.email.toLowerCase(), color: typeof m.color === 'string' ? m.color : '' }));
  ENVIRONMENTS.splice(0, ENVIRONMENTS.length, ...members.map((m) => ({ id: m.env, displayName: m.name })));
  for (const k of Object.keys(ALLOWLIST)) delete ALLOWLIST[k];
  for (const m of members) ALLOWLIST[m.email] = { env: m.env, owner: m.role === 'owner' };
  if (typeof document !== 'undefined') {
    let style = document.getElementById('co-member-colors') as HTMLStyleElement | null;
    if (!style) {
      style = document.createElement('style');
      style.id = 'co-member-colors';
      document.head.appendChild(style);
    }
    style.textContent = members
      .filter((m) => /^#[0-9a-fA-F]{3,8}$|^[a-z]{3,16}$/.test(m.color))
      .map((m) => `.env-${m.env} { --env: ${m.color}; }`)
      .join('\n');
  }
}

export function memberColor(env: string | undefined): string | undefined {
  return members.find((m) => m.env === env)?.color || undefined;
}

export function ownerEnv(): string {
  return members.find((m) => m.role === 'owner')?.env ?? '';
}

export function ownerName(): string {
  return members.find((m) => m.role === 'owner')?.name ?? 'the owner';
}

/** "Anna, Boris and Vera" - everybody of the workbench, for texts about the family chat. */
export function memberNames(): string {
  const n = members.map((m) => m.name);
  return n.length <= 1 ? n.join('') : `${n.slice(0, -1).join(', ')} and ${n[n.length - 1]}`;
}

/** Windows path on the home host of a Drive path: drivePath('AI-Workbench/tools') -> G:\My Drive\AI-Workbench\tools. */
export function drivePath(rel = ''): string {
  const r = rel.replace(/\//g, '\\').replace(/^\\+/, '');
  return r ? `${driveRoot}\\${r}` : driveRoot;
}

function applyDriveRoot(v: unknown): void {
  // "H:\" arrives from the server as "H:" (trailing separators are cut there too)
  if (typeof v === 'string' && /^[A-Za-z]:(\\[^"<>|]*)?$/.test(v)) driveRoot = v.replace(/\\+$/, '');
}

/** Loads the list from the server (session cookie or the given access token). False: kept the cached list. */
export async function loadMembers(accessToken?: string): Promise<boolean> {
  try {
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
    const r = await fetch('/agent/db.php', {
      method: 'POST', headers, body: JSON.stringify({ op: 'members' }), credentials: 'same-origin', cache: 'no-store',
    });
    if (!r.ok) return false;
    const j = await r.json();
    if (!Array.isArray(j.members) || !j.members.some(valid)) return false;
    applyMembers(j.members);
    applyDriveRoot(j.install?.driveRoot);
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(members));
      localStorage.setItem(DRIVE_KEY, driveRoot);
    } catch {
      /* private mode */
    }
    return true;
  } catch {
    return false;
  }
}

try {
  const cached = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
  if (Array.isArray(cached)) applyMembers(cached);
  applyDriveRoot(localStorage.getItem(DRIVE_KEY));
} catch {
  /* no cache yet */
}
