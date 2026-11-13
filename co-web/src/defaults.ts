// SPDX-License-Identifier: AGPL-3.0-or-later
// Agent and model for the first message of a chat (CO Web 0.12.0).
// The subscription (Claude or GPT) with the most headroom left in limits.json. 0.66.0: the owner no longer starts on the
// gateway dispatcher (its module is disabled).

import type { AgentId } from './config';
import { windowState } from './Limits';
import type { LimitsFile } from './types';

const SUBSCRIPTIONS: AgentId[] = ['claude', 'codex'];

// Percent left in the tightest of the session and week windows; -1 when Bridge has not reported the agent yet.
export function headroom(limits: LimitsFile | null, agent: AgentId): number {
  const windows = (limits?.agents?.[agent]?.windows || []).filter((w) => w.id === 'session' || w.id === 'week');
  if (!windows.length) return -1;
  return 100 - Math.max(...windows.map((w) => windowState(w).pct));
}

export function defaultSelection(
  allowed: AgentId[], limits: LimitsFile | null, fallback: AgentId,
): { agent: AgentId; model: string | null } {
  const subs = SUBSCRIPTIONS.filter((a) => allowed.includes(a));
  if (subs.length) {
    const best = [...subs].sort((a, b) => headroom(limits, b) - headroom(limits, a))[0];
    if (headroom(limits, best) >= 0) return { agent: best, model: null };
  }
  return { agent: allowed.includes(fallback) ? fallback : allowed[0] || 'claude', model: null };
}
