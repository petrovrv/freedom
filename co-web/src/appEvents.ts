// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 shell F14b-1 (SPEC §3.6a items 5 and 9): an agent ran an action of an app (db.php host op app_action); the
// pages of that app open in this CO Web get the ext/1 event "refresh" and load their data again. CO Web has no push
// channel from the site, so it asks the cheap user op app_events while an app is alive and the tab is visible; a
// module frame (the app page, also its tray chip) passes the event on to its page (ModuleFrame listens to APP_REFRESH).

export const APP_REFRESH = 'co-app-refresh';
export const APP_EVENTS_MS = 5000;

/**
 * F14b-2: a tray chip (ui.header) of an enabled module with actions is shown in the header all the time, so CO Web polls
 * app_events for it even when no app is open.
 */
export function trayWantsRefresh(modules: { enabled: boolean; actions?: unknown; ui: { slot: string }[] }[]): boolean {
  return modules.some((m) => m.enabled && Array.isArray(m.actions) && m.actions.length > 0 && m.ui.some((u) => u.slot === 'ui.header'));
}

export type AppEvent = { seq: number; app: string; action: string; utc?: string };
export type AppRefresh = { module: string; actions: string[] };

/**
 * The next cursor and the apps to refresh from one answer of app_events. The first answer (cursor -1) only sets the
 * cursor: actions from before the page was opened refresh nothing. A seq lower than the cursor (the record was reset)
 * restarts from it.
 */
export function takeAppEvents(cursor: number, r: unknown): { cursor: number; refresh: AppRefresh[] } {
  const o = (r && typeof r === 'object' ? r : {}) as { seq?: unknown; events?: unknown };
  const seq = typeof o.seq === 'number' && Number.isInteger(o.seq) && o.seq >= 0 ? o.seq : cursor;
  if (cursor < 0 || seq < cursor) return { cursor: seq, refresh: [] };
  const by = new Map<string, string[]>();
  for (const e of Array.isArray(o.events) ? o.events : []) {
    const ev = e as Partial<AppEvent>;
    if (!ev || typeof ev.app !== 'string' || !/^[a-z0-9][a-z0-9-]{0,62}$/.test(ev.app) || typeof ev.seq !== 'number' || ev.seq <= cursor) continue;
    const list = by.get(ev.app) ?? [];
    if (typeof ev.action === 'string' && /^[a-z][a-z0-9_]{2,40}$/.test(ev.action) && !list.includes(ev.action)) list.push(ev.action);
    by.set(ev.app, list);
  }
  return { cursor: seq, refresh: [...by].map(([module, actions]) => ({ module, actions })) };
}
