// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.31.0: activity icons. ProjectRing — spinning ring with the number of working chats of a project;
// ChatStatusIcon — running / queued / waiting for your answer / unread / scheduled. Read tracking per browser
// (localStorage co.seen); chats updated after the first run of this feature and never opened count as unread.
import { useSyncExternalStore } from 'react';
import { gateway } from './gateway';

/* ---------- Project ring ---------- */

export function ProjectRing({ count }: { count: number }) {
  if (!count || count <= 0) return null;
  const label = count > 9 ? '9+' : String(count);
  const r = 7;
  const c = 2 * Math.PI * r;
  return (
    <span className="project-ring" title={`${count} chat(s) working`}>
      <svg width={18} height={18} viewBox="0 0 18 18" aria-hidden="true">
        <circle cx={9} cy={9} r={r} fill="none" stroke="rgba(127,127,127,0.25)" strokeWidth={2} />
        <circle
          className="ring-spin"
          cx={9}
          cy={9}
          r={r}
          fill="none"
          stroke="var(--accent, #2563eb)"
          strokeWidth={2}
          strokeLinecap="round"
          strokeDasharray={`${c * 0.28} ${c}`}
        />
        <text
          x={9}
          y={9}
          textAnchor="middle"
          dominantBaseline="central"
          fontSize={9}
          fontWeight={700}
          fill="currentColor"
        >
          {label}
        </text>
      </svg>
    </span>
  );
}

/* ---------- Chat status icon ---------- */

// 0.52.0: 'gateway' = stopped because the gateway model was unavailable
export type ChatStatus = 'running' | 'queued' | 'gateway' | 'unread' | 'asking' | 'scheduled' | 'idle';

const STATUS_TITLES: Record<ChatStatus, string> = {
  running: 'Running',
  queued: 'Queued',
  gateway: 'Stopped: the gateway model was unavailable', // named at runtime in ChatStatusIcon
  unread: 'Unread',
  asking: 'Waiting for your answer',
  scheduled: 'Scheduled',
  idle: '',
};

export function ChatStatusIcon({ status }: { status: ChatStatus }) {
  const title = status === 'gateway' ? `Stopped: ${gateway().title} was unavailable` : STATUS_TITLES[status] || undefined;

  switch (status) {
    case 'running': {
      const r = 5;
      const c = 2 * Math.PI * r;
      return (
        <span className="status-icon" title={title}>
          <svg width={14} height={14} viewBox="0 0 14 14" aria-hidden="true">
            <circle cx={7} cy={7} r={r} fill="none" stroke="rgba(127,127,127,0.25)" strokeWidth={2} />
            <circle
              className="ring-spin"
              cx={7}
              cy={7}
              r={r}
              fill="none"
              stroke="var(--accent, #2563eb)"
              strokeWidth={2}
              strokeLinecap="round"
              strokeDasharray={`${c * 0.3} ${c}`}
            />
          </svg>
        </span>
      );
    }
    case 'queued':
      return (
        <span className="status-icon" title={title}>
          <svg width={14} height={14} viewBox="0 0 14 14" aria-hidden="true">
            <circle
              cx={7}
              cy={7}
              r={5}
              fill="none"
              stroke="var(--muted, #9ca3af)"
              strokeWidth={1.5}
              strokeDasharray="2 2"
            />
          </svg>
        </span>
      );
    case 'unread':
      return (
        <span className="status-icon" title={title}>
          <svg width={14} height={14} viewBox="0 0 14 14" aria-hidden="true">
            <circle cx={7} cy={7} r={4} fill="var(--accent, #2563eb)" />
          </svg>
        </span>
      );
    // 0.52.0: stopped while the gateway model was unavailable (the first letter of its name)
    case 'gateway':
      return (
        <span className="status-icon" title={title}>
          <svg width={14} height={14} viewBox="0 0 14 14" aria-hidden="true">
            <circle cx={7} cy={7} r={6.5} fill="#7c3aed" />
            <text x={7} y={7.5} textAnchor="middle" dominantBaseline="central" fontSize={9} fontWeight={700} fill="#fff">{gateway().title.slice(0, 1).toUpperCase()}</text>
          </svg>
        </span>
      );
    case 'asking':
      return (
        <span className="status-icon" title={title}>
          <svg width={14} height={14} viewBox="0 0 14 14" aria-hidden="true">
            <circle cx={7} cy={7} r={6.5} fill="#d97706" />
            <text
              x={7}
              y={7.5}
              textAnchor="middle"
              dominantBaseline="central"
              fontSize={10}
              fontWeight={700}
              fill="#fff"
            >
              ?
            </text>
          </svg>
        </span>
      );
    case 'scheduled':
      return (
        <span className="status-icon" title={title} style={{ fontSize: 11, lineHeight: 1 }}>
          <span aria-hidden="true">⏰</span>
        </span>
      );
    default:
      return <span className="status-icon" aria-hidden="true" />;
  }
}

/* ---------- Status priority ---------- */

export function chatStatus(o: {
  running?: 'queued' | 'running';
  gateway?: boolean;
  asking: boolean;
  unread: boolean;
  scheduled: boolean;
}): ChatStatus {
  if (o.running === 'running') return 'running';
  if (o.running === 'queued') return 'queued';
  // 0.52.0: gateway-stopped after queued, before asking
  if (o.gateway) return 'gateway';
  if (o.asking) return 'asking';
  if (o.unread) return 'unread';
  if (o.scheduled) return 'scheduled';
  return 'idle';
}

/* ---------- Read tracking ---------- */

const SEEN_KEY = 'co.seen';
const SEEN_SINCE_KEY = 'co.seenSince';
const SEEN_CAP = 500;

let seenCache: Record<string, string> | null = null;
let seenVersion = 0;
const seenListeners = new Set<() => void>();

function ts(iso: string): number {
  const t = Date.parse(iso);
  return Number.isNaN(t) ? 0 : t;
}

// Start of read tracking in this browser: older chats never opened are not "unread".
function seenSince(): number {
  try {
    const v = localStorage.getItem(SEEN_SINCE_KEY);
    if (v) return Number(v) || 0;
    const now = Date.now();
    localStorage.setItem(SEEN_SINCE_KEY, String(now));
    return now;
  } catch {
    return Date.now();
  }
}

function loadSeen(): Record<string, string> {
  if (seenCache) return seenCache;
  try {
    const raw = localStorage.getItem(SEEN_KEY);
    const parsed = raw ? JSON.parse(raw) : {};
    seenCache = parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  } catch {
    seenCache = {};
  }
  return seenCache!;
}

function saveSeen(map: Record<string, string>): Record<string, string> {
  let entries = Object.entries(map);
  if (entries.length > SEEN_CAP) {
    entries = entries.sort((a, b) => ts(b[1]) - ts(a[1])).slice(0, SEEN_CAP);
    map = Object.fromEntries(entries);
  }
  try {
    localStorage.setItem(SEEN_KEY, JSON.stringify(map));
  } catch {
    /* storage full or unavailable */
  }
  return map;
}

function notifySeen() {
  seenVersion++;
  seenListeners.forEach((cb) => {
    try {
      cb();
    } catch {
      /* ignore listener errors */
    }
  });
}

export function markSeen(chatId: string, updatedUtc: string): void {
  if (!chatId || !updatedUtc) return;
  const map = { ...loadSeen() };
  const prev = map[chatId];
  if (prev && ts(prev) >= ts(updatedUtc)) return;
  map[chatId] = updatedUtc;
  seenCache = saveSeen(map);
  notifySeen();
}

export function isUnread(chatId: string, updatedUtc: string): boolean {
  if (!chatId || !updatedUtc) return false;
  const stored = loadSeen()[chatId];
  if (!stored) return ts(updatedUtc) > seenSince();
  return ts(stored) < ts(updatedUtc);
}

export function subscribeSeen(cb: () => void): () => void {
  seenListeners.add(cb);
  return () => {
    seenListeners.delete(cb);
  };
}

export function getSeenVersion(): number {
  return seenVersion;
}

export function useSeenVersion(): number {
  return useSyncExternalStore(subscribeSeen, getSeenVersion, getSeenVersion);
}

if (typeof window !== 'undefined') {
  window.addEventListener('storage', (e) => {
    if (e.key === SEEN_KEY) {
      seenCache = null;
      notifySeen();
    }
  });
}
