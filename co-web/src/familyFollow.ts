// SPDX-License-Identifier: AGPL-3.0-or-later
import type { ChatEvent } from './types';

// 0.107.0: once called, @co follows the conversation for a while: the next few messages without @co also start a turn,
// in which the agent joins only when the message continues its topic (else [[silent]]). The window counts from its last
// spoken answer, so silent turns do not extend it.
export const FOLLOW_MESSAGES = 6;
export const FOLLOW_MINUTES = 30;
export function followsTopic(events: ChatEvent[], nowMs = Date.now()): boolean {
  let people = 0;
  for (let i = events.length - 1; i >= 0; i--) {
    const e = events[i];
    if (e.type === 'assistant_message') {
      if ((e.text || '').trim() === '[[silent]]' || e.status !== 'completed') continue;
      const t = Date.parse(e.completedUtc || e.createdUtc || '');
      return !Number.isNaN(t) && nowMs - t < FOLLOW_MINUTES * 60000;
    }
    if (e.type === 'user_message' && e.family && !e.coFor && ++people > FOLLOW_MESSAGES) return false;
  }
  return false;
}
