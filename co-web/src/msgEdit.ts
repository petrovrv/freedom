// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.134.0: delete a chat message and "rewrite from here" (server: co-agent msgedit-lib.php, ops msg_delete,
// msg_rewrite, msg_restore). A hidden event keeps its row with data.deleted; the chat, the agent's context and the plan of
// the next turn leave it out. Pure helpers plus the hidden events of each loaded chat (for new sequence numbers, the
// session reset and the restore bar).
import type { ChatEvent } from './types.ts';

export const RESTORE_DAYS = 30;

export interface DeletedMark {
  utc: string;
  by: string;
  batch: string;
  mode: 'pair' | 'answer' | 'rewrite';
  at: number;
}

export function isHidden(e: ChatEvent): boolean {
  return !!(e as { deleted?: DeletedMark }).deleted;
}

export function markOf(e: ChatEvent): DeletedMark | undefined {
  return (e as { deleted?: DeletedMark }).deleted;
}

export function splitHidden(events: ChatEvent[]): { visible: ChatEvent[]; hidden: ChatEvent[] } {
  const visible: ChatEvent[] = [];
  const hidden: ChatEvent[] = [];
  for (const e of events) (isHidden(e) ? hidden : visible).push(e);
  return { visible, hidden };
}

// A provider session that saw a hidden message must not be resumed: sessions start after the newest deletion.
export function sessionsSince(movedUtc: string | undefined, hidden: ChatEvent[]): string | undefined {
  let t = movedUtc || '';
  for (const e of hidden) {
    const u = markOf(e)?.utc || '';
    if (u > t) t = u;
  }
  return t || undefined;
}

export function maxSeq(events: ChatEvent[]): number {
  return events.reduce((m, e) => Math.max(m, e.sequence), 0);
}

export interface DeletedBatch {
  batch: string;
  mode: DeletedMark['mode'];
  at: number;
  utc: string;
  by: string;
  count: number;
  messages: number;
}

// Deletions that can still be restored, newest first (one row per batch).
export function restorable(hidden: ChatEvent[], nowMs: number): DeletedBatch[] {
  const by = new Map<string, DeletedBatch>();
  for (const e of hidden) {
    const m = markOf(e);
    if (!m || nowMs - Date.parse(m.utc) > RESTORE_DAYS * 86400000) continue;
    const b = by.get(m.batch) || { batch: m.batch, mode: m.mode, at: m.at, utc: m.utc, by: m.by, count: 0, messages: 0 };
    b.count++;
    if (e.type === 'user_message' || e.type === 'assistant_message') b.messages++;
    by.set(m.batch, b);
  }
  return [...by.values()].sort((a, b) => b.utc.localeCompare(a.utc));
}

// Rule 4 on the client (the server decides): the chat's owner any message; others their own messages and the answers
// to them. `mine` = author of the user message of that turn is the signed-in person.
export function canHide(e: ChatEvent, events: ChatEvent[], me: string, owner: boolean): boolean {
  if (owner) return true;
  if (e.type === 'user_message') return !e.author || e.author === me;
  if (e.type === 'assistant_message' && e.jobId) {
    const u = events.find((x) => x.type === 'user_message' && x.jobId === e.jobId);
    return !!u && (!u.author || u.author === me);
  }
  return false;
}

// Rewrite from #seq forgets every later message too: allowed when all later user messages are the person's own.
export function canRewrite(e: ChatEvent, events: ChatEvent[], me: string, owner: boolean): boolean {
  if (e.type !== 'user_message') return false;
  if (owner) return true;
  return events.every((x) => x.sequence < e.sequence || x.type !== 'user_message' || !x.author || x.author === me);
}

// ---- hidden events of the chats loaded in this tab ----
const hiddenByChat = new Map<string, ChatEvent[]>();

export function setHidden(chatId: string, hidden: ChatEvent[]): void {
  hiddenByChat.set(chatId, hidden);
}

export function hiddenOf(chatId: string): ChatEvent[] {
  return hiddenByChat.get(chatId) || [];
}
