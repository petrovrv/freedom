// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.40.0: the chat list as a tree. Child chats (chat.parent: branch, follow-up, side question, subtask) are
// listed under their parent; a chat whose parent is not in the list (archived, other project, filtered out) is a root.
import type { Chat } from './types';
import { compareBy, ts, type SortMode } from './sidebarSort.ts';

/** F15.2: the chat a chat sits under (its folder), else its lineage parent; undefined = top of the project. */
export function placeParent(c: Chat): string | undefined {
  if (typeof c.folder === 'string') return c.folder || undefined;
  return c.parent?.chatId || undefined;
}

export interface ChatTreeRow {
  chat: Chat;
  depth: number;
  hasChildren: boolean;
  open: boolean;
}

const OPEN_KEY = 'co.treeOpen.';
const SUBTASK_PREFIX = '⤷ '; // subtask kind prefix

/** Stored open state for a parent row; undefined when never set. */
export function readTreeOpen(chatId: string): boolean | undefined {
  try {
    const v = localStorage.getItem(OPEN_KEY + chatId);
    if (v === '1') return true;
    if (v === '0') return false;
  } catch {
    /* storage unavailable */
  }
  return undefined;
}

export function writeTreeOpen(chatId: string, open: boolean): void {
  try {
    localStorage.setItem(OPEN_KEY + chatId, open ? '1' : '0');
  } catch {
    /* storage unavailable or full */
  }
}

/** Strips the subtask prefix from child titles only; other prefixes stay. */
export function treeTitle(title: string, depth: number): string {
  if (depth > 0 && title && title.startsWith(SUBTASK_PREFIX)) return title.slice(SUBTASK_PREFIX.length);
  return title;
}

const fresh = (c: Chat): number => ts(c.updatedUtc) || ts(c.createdUtc);

/** Flattened visible rows in display order; descendants of collapsed nodes are omitted. */
export function buildChatTree(
  chats: Chat[],
  opts: {
    isWorking: (chatId: string) => boolean;
    selectedChatId?: string;
    isOpen?: (chatId: string) => boolean | undefined;
    // 0.83.0: order of the roots (sidebarSort.ts); 'waiting' uses waitRank of the chat and its subtree
    sort?: SortMode;
    waitRank?: (chat: Chat) => number;
  },
): ChatTreeRow[] {
  const isOpen = opts.isOpen ?? readTreeOpen;

  // Dedupe by chatId (first occurrence wins)
  const byId = new Map<string, Chat>();
  for (const c of chats) if (c?.chatId && !byId.has(c.chatId)) byId.set(c.chatId, c);
  const list = [...byId.values()];

  // Parent only counts when present in the given list
  const rawParent = (c: Chat): string | undefined => {
    const pid = placeParent(c);
    return pid && pid !== c.chatId && byId.has(pid) ? pid : undefined;
  };

  // Effective parent; any chat that is its own ancestor becomes a root
  const parentOf = new Map<string, string>();
  for (const c of list) {
    const pid = rawParent(c);
    if (!pid) continue;
    const seen = new Set<string>([c.chatId]);
    let cur: string | undefined = pid;
    let cyclic = false;
    while (cur) {
      if (seen.has(cur)) {
        cyclic = cur === c.chatId;
        break;
      }
      seen.add(cur);
      const next = byId.get(cur);
      cur = next ? rawParent(next) : undefined;
    }
    if (!cyclic) parentOf.set(c.chatId, pid);
  }

  // Group children; children ordered by creation ascending
  const kids = new Map<string, Chat[]>();
  const roots: Chat[] = [];
  for (const c of list) {
    const pid = parentOf.get(c.chatId);
    if (!pid) {
      roots.push(c);
      continue;
    }
    const arr = kids.get(pid);
    if (arr) arr.push(c);
    else kids.set(pid, [c]);
  }
  for (const arr of kids.values()) {
    arr.sort((a, b) => ts(a.createdUtc) - ts(b.createdUtc) || a.chatId.localeCompare(b.chatId));
  }

  // Subtree freshness and "any descendant working" (graph is acyclic now)
  const treeFresh = new Map<string, number>();
  const busyBelow = new Map<string, boolean>();
  const treeWait = new Map<string, number>();
  const waitRank = opts.waitRank ?? (() => 0);
  const visit = (c: Chat): void => {
    let f = fresh(c);
    let busy = false;
    let w = waitRank(c);
    for (const k of kids.get(c.chatId) ?? []) {
      visit(k);
      f = Math.max(f, treeFresh.get(k.chatId) ?? 0);
      busy = busy || opts.isWorking(k.chatId) || !!busyBelow.get(k.chatId);
      w = Math.max(w, treeWait.get(k.chatId) ?? 0);
    }
    treeFresh.set(c.chatId, f);
    busyBelow.set(c.chatId, busy);
    treeWait.set(c.chatId, w);
  };
  roots.forEach(visit);

  // Ancestors of the selected chat default to expanded
  const selAnc = new Set<string>();
  for (let cur = opts.selectedChatId ? parentOf.get(opts.selectedChatId) : undefined; cur; cur = parentOf.get(cur)) {
    selAnc.add(cur);
  }

  // Roots by the chosen mode; default: subtree freshness desc (active tree rises)
  const sort = opts.sort ?? 'activity';
  if (sort === 'activity') {
    roots.sort(
      (a, b) =>
        (treeFresh.get(b.chatId) ?? 0) - (treeFresh.get(a.chatId) ?? 0) ||
        fresh(b) - fresh(a) ||
        a.chatId.localeCompare(b.chatId),
    );
  } else {
    const cmp = compareBy(sort);
    const key = (c: Chat) => ({
      id: c.chatId,
      title: c.title || '',
      created: ts(c.createdUtc),
      fresh: treeFresh.get(c.chatId) ?? 0,
      waiting: treeWait.get(c.chatId) ?? 0,
    });
    const keys = new Map(roots.map((c) => [c.chatId, key(c)] as const));
    roots.sort((a, b) => cmp(keys.get(a.chatId)!, keys.get(b.chatId)!));
  }

  // Iterative DFS flatten
  const rows: ChatTreeRow[] = [];
  const stack: Array<{ chat: Chat; depth: number }> = roots
    .slice()
    .reverse()
    .map((chat) => ({ chat, depth: 0 }));
  while (stack.length) {
    const { chat, depth } = stack.pop()!;
    const children = kids.get(chat.chatId) ?? [];
    const hasChildren = children.length > 0;
    const open =
      hasChildren && (isOpen(chat.chatId) ?? (!!busyBelow.get(chat.chatId) || selAnc.has(chat.chatId)));
    rows.push({ chat, depth, hasChildren, open });
    if (open) for (let i = children.length - 1; i >= 0; i--) stack.push({ chat: children[i], depth: depth + 1 });
  }
  return rows;
}

/**
 * 0.70.0: chats whose archived flag changes together with `chatId`. Archive takes the chat and its descendants;
 * restore takes the chat, its descendants and its archived ancestors, so the restored chat is back under its parent.
 * Only chats whose flag actually changes are returned, the given chat first.
 */
export function archiveScope(chats: Chat[], chatId: string, archive: boolean): Chat[] {
  const byId = new Map<string, Chat>();
  for (const c of chats) if (c?.chatId && !byId.has(c.chatId)) byId.set(c.chatId, c);
  const self = byId.get(chatId);
  if (!self) return [];
  const kids = new Map<string, Chat[]>();
  for (const c of byId.values()) {
    const pid = placeParent(c);
    if (!pid || pid === c.chatId || !byId.has(pid)) continue;
    const arr = kids.get(pid);
    if (arr) arr.push(c);
    else kids.set(pid, [c]);
  }
  const seen = new Set<string>([chatId]);
  const out: Chat[] = [self];
  const stack = [...(kids.get(chatId) ?? [])];
  while (stack.length) {
    const c = stack.pop()!;
    if (seen.has(c.chatId)) continue;
    seen.add(c.chatId);
    out.push(c);
    stack.push(...(kids.get(c.chatId) ?? []));
  }
  if (!archive) {
    for (let cur = byId.get(placeParent(self) || ''); cur && !seen.has(cur.chatId); cur = byId.get(placeParent(cur) || '')) {
      seen.add(cur.chatId);
      if (cur.archived) out.push(cur);
    }
  }
  return out.filter((c) => !!c.archived !== archive);
}
