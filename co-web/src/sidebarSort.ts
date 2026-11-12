// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.83.0: sort order of the sidebar (projects and the chats of the open project). One choice per browser
// (localStorage co.sortMode): last activity (default), creation date, title, or waiting for you first.
export type SortMode = 'activity' | 'created' | 'title' | 'waiting';

export const SORT_MODES: Array<{ id: SortMode; label: string; title: string }> = [
  { id: 'activity', label: 'Activity', title: 'Newest message first' },
  { id: 'created', label: 'Created', title: 'Newest created first' },
  { id: 'title', label: 'A–Z', title: 'By title, alphabetically' },
  { id: 'waiting', label: 'Waiting', title: 'Chats that wait for your answer first (questions, confirmations, failed jobs, unread answers), then by last activity' },
];

const KEY = 'co.sortMode';

export function readSortMode(): SortMode {
  try {
    const v = localStorage.getItem(KEY);
    if (SORT_MODES.some((m) => m.id === v)) return v as SortMode;
  } catch {
    /* storage unavailable */
  }
  return 'activity';
}

export function writeSortMode(mode: SortMode): void {
  try {
    localStorage.setItem(KEY, mode);
  } catch {
    /* storage unavailable or full */
  }
}

export function ts(s?: string): number {
  const t = s ? Date.parse(s) : NaN;
  return Number.isFinite(t) ? t : 0;
}

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/** Title for alphabetical order: leading emoji, symbols and spaces (⏰, 📖, ⤷ …) do not count. */
export function sortTitle(t: string): string {
  return (t || '').replace(/^[^\p{L}\p{N}]+/u, '');
}

export interface SortKeys {
  id: string;
  title: string;
  created: number;
  fresh: number; // last activity
  waiting: number; // 0 = nothing; higher waits more
}

/** Comparator for one mode; ties fall back to last activity, then id, so the order is stable. */
export function compareBy(mode: SortMode): (a: SortKeys, b: SortKeys) => number {
  const tail = (a: SortKeys, b: SortKeys) => b.fresh - a.fresh || a.id.localeCompare(b.id);
  switch (mode) {
    case 'created':
      return (a, b) => b.created - a.created || tail(a, b);
    case 'title':
      return (a, b) => collator.compare(sortTitle(a.title), sortTitle(b.title)) || tail(a, b);
    case 'waiting':
      return (a, b) => b.waiting - a.waiting || tail(a, b);
    default:
      return tail;
  }
}
