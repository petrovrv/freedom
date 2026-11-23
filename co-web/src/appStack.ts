// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 shell, navigation F14e.1 (SPEC §3.6a п. 9): which apps stay alive while hidden and which are recent. An app
// opened in CO Web stays loaded (its iframe is hidden, not destroyed) so that switching back is instant and keeps its
// state; only the last few stay alive — 3 on a phone, 5 on a computer — and the oldest is unloaded (it loads again on
// the next open). The recent list (icons in the header and in the app bar) is kept per browser.
import { shellApps, type ModuleInfo, type ShellApp } from './extBridge.ts';

/** Event: ▦ of the app bar opens the launcher of the header. */
export const LAUNCHER_OPEN = 'co-launcher-open';

/** One app of the shell: a ui.page of a module in an environment. */
export interface AppRef { env: string; moduleId: string; pageId: string }

export const appKey = (a: AppRef): string => `${a.env}/${a.moduleId}/${a.pageId}`;

/** How many hidden apps stay alive: 3 on a phone (narrow screen), 5 on a computer. */
export function liveLimit(narrow: boolean): number {
  return narrow ? 3 : 5;
}

/** The app goes to the front of the list; the list keeps at most max entries (the oldest drop off the end). */
export function touchApp(list: AppRef[], a: AppRef, max: number): AppRef[] {
  const k = appKey(a);
  return [a, ...list.filter((x) => appKey(x) !== k)].slice(0, Math.max(1, max));
}

/** Drops the apps the predicate rejects (a module switched off or removed); returns the same list when nothing changed. */
export function pruneApps(list: AppRef[], keep: (a: AppRef) => boolean): AppRef[] {
  const out = list.filter(keep);
  return out.length === list.length ? list : out;
}

const okId = (v: unknown, re: RegExp): v is string => typeof v === 'string' && re.test(v);

/** A stored recent list as it was read (anything malformed is dropped). */
export function parseRecent(raw: string | null): AppRef[] {
  if (!raw) return [];
  let v: unknown;
  try { v = JSON.parse(raw); } catch { return []; }
  if (!Array.isArray(v)) return [];
  return v.flatMap((x) => {
    const r = x as Record<string, unknown>;
    return r && okId(r.env, /^[A-Za-z0-9_-]{1,40}$/) && okId(r.moduleId, /^[a-z0-9][a-z0-9-]{0,62}$/) && okId(r.pageId, /^[A-Za-z0-9._-]{1,40}$/)
      ? [{ env: r.env, moduleId: r.moduleId, pageId: r.pageId }] : [];
  }).slice(0, RECENT_MAX);
}

export const RECENT_MAX = 8;
const RECENT_KEY = 'co.recentApps';

export function loadRecent(): AppRef[] {
  try { return parseRecent(localStorage.getItem(RECENT_KEY)); } catch { return []; }
}

export function saveRecent(list: AppRef[]): void {
  try { localStorage.setItem(RECENT_KEY, JSON.stringify(list.slice(0, RECENT_MAX))); } catch { /* private mode */ }
}

/**
 * Where "← Chats" of an app goes: back in the history when the app was opened from a page of the shell (Back of the
 * browser and of Android lands on the same place), else to the last page of the shell, else the home of the
 * environment.
 */
export function exitTarget(openedFromShell: boolean, lastShellPath: string | null, env: string): -1 | string {
  if (openedFromShell) return -1;
  return lastShellPath || `/e/${env}`;
}

/** The app of a reference among the enabled modules (null: switched off, removed or not an app any more). */
export function findApp(modules: ModuleInfo[], a: AppRef): ShellApp | null {
  return shellApps(modules).find((x) => x.m.id === a.moduleId && x.ui.id === a.pageId) ?? null;
}

/** Any full-window page (ui.page) of an enabled module, an app of the launcher or not (a link of the side panel). */
export function findPage(modules: ModuleInfo[], a: AppRef): ShellApp | null {
  const app = findApp(modules, a);
  if (app) return app;
  const m = modules.find((x) => x.enabled && x.id === a.moduleId);
  const ui = m?.ui.find((u) => u.slot === 'ui.page' && u.id === a.pageId);
  if (!m || !ui) return null;
  const label = ui.label || ui.title || m.title || m.id;
  return { m, ui, label, icon: ui.icon || '' };
}

/**
 * The live pages in the order of the DOM: a moved iframe reloads (and a reloaded module page is closed), so the frames
 * keep the order in which they were first mounted; seq numbers the keys at first sight (and again after an unload).
 */
export function mountOrder(live: AppRef[], seq: Map<string, number>): AppRef[] {
  const keys = new Set(live.map(appKey));
  for (const k of [...seq.keys()]) if (!keys.has(k)) seq.delete(k);
  let next = Math.max(0, ...seq.values()) + 1;
  // the newest are at the front of live: number them from the back so that a batch keeps the order of first opening
  for (const a of [...live].reverse()) if (!seq.has(appKey(a))) seq.set(appKey(a), next++);
  return [...live].sort((x, y) => seq.get(appKey(x))! - seq.get(appKey(y))!);
}

/**
 * The shell address of a full-window module page /x/<module>/<page> (from a tray chip, a push, a bookmark or a link in
 * a message), or null to keep the bare page. Without the shell such a page has no app bar, no Back/Esc and no way back
 * to the chats (seen 23.11 with the mail). A query stays on the bare page (app pages of the shell take no parameters);
 * a desktop app's own window (F14e.2) is opened with ?window=1 for that reason.
 */
export function shellRoute(pathname: string, search: string, env: string): string | null {
  if (search && search !== '?') return null;
  if (!env) return null;
  const x = /^\/x\/([a-z0-9][a-z0-9-]{0,62})\/([A-Za-z0-9._-]{1,40})\/?$/.exec(pathname);
  return x ? `/e/${encodeURIComponent(env)}/m/${x[1]}/${x[2]}` : null;
}

/** The environment of a shell address /e/<env>/... ('' elsewhere). */
export function envOfPath(pathname: string): string {
  const m = /^\/e\/([^/]+)/.exec(pathname);
  return m ? decodeURIComponent(m[1]) : '';
}
