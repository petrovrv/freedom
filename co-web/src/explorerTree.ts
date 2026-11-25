// SPDX-License-Identifier: AGPL-3.0-or-later
// F15.1 (SPEC §3.6b п. 1): the Explorer view of the left panel over today's projects and chats. Projects and chats are
// folders (child chats are nested folders), a path bar leads back up, Ctrl+K jumps to any project or chat by its title.
// Pure logic here (Explorer.tsx draws it). 0.128.0: the default view of the panel; the tree is a switch in ☰ (co.explorer).
import type { Chat, Project } from './types';
import { compareBy, sortTitle, ts, type SortMode } from './sidebarSort.ts';
import { placeParent } from './chatTree.ts';

/** Where the Explorer is: the root (projects), a project (its top chats) or a chat with child chats (chain of chat ids).
 * 0.128.2: AGENTS after a chat id is that chat's "Agent talks" folder (the subtasks its agent started). */
export interface ExplorerPath {
  projectId?: string;
  chatIds: string[];
}

/** Path step of the "Agent talks" folder of the chat before it; never a chat id (those start with CHAT-). */
export const AGENTS = '@agents';
export const AGENTS_TITLE = 'Agent talks';

export const ROOT_PATH: ExplorerPath = { chatIds: [] };

export type ExplorerItem =
  | { kind: 'project'; key: string; project: Project; title: string }
  | { kind: 'chat'; key: string; chat: Chat; title: string; folder: boolean; count: number }
  // the chat whose folder is open: its own conversation, first in the folder
  | { kind: 'self'; key: string; chat: Chat; title: string }
  // 0.128.2: the "Agent talks" folder of the open chat (`chat`): the subtasks its agent started, out of the person's way
  | { kind: 'agents'; key: string; chat: Chat; title: string; count: number };

export interface Crumb {
  label: string;
  path: ExplorerPath;
}

export type ExplorerView = 'icons' | 'list';

const ON_KEY = 'co.explorer';
const VIEW_KEY = 'co.explorer.view';

function readKey(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}
function writeKey(key: string, v: string): void {
  try {
    localStorage.setItem(key, v);
  } catch {
    /* storage unavailable or full */
  }
}

/** The Explorer view is off unless switched on in this browser. */
/** 0.128.0: the Explorer is the default view; a stored '0' (the tree chosen in ☰ by hand) is kept. */
export const explorerOnFrom = (stored: string | null): boolean => stored !== '0';
export const readExplorerOn = (): boolean => explorerOnFrom(readKey(ON_KEY));
export const writeExplorerOn = (on: boolean): void => writeKey(ON_KEY, on ? '1' : '0');
export const readExplorerView = (): ExplorerView => (readKey(VIEW_KEY) === 'list' ? 'list' : 'icons');
export const writeExplorerView = (v: ExplorerView): void => writeKey(VIEW_KEY, v);

const SUBTASK_PREFIX = '⤷ ';
/** Shown title: no ⏰ of scheduled chats, no ⤷ of subtasks (the folder already says where it belongs). */
export function itemTitle(title: string): string {
  let t = (title || '').replace(/^⏰\s*/u, '');
  if (t.startsWith(SUBTASK_PREFIX)) t = t.slice(SUBTASK_PREFIX.length);
  return t || '(untitled)';
}

/**
 * 0.128.2: a chat the agent started to serve its parent chat (aiwb-subtask spawn: kind subtask, title "⤷ …") and that
 * nobody moved by hand. It sits in the parent's "Agent talks" folder, not next to the person's own chats. Chats made
 * subchats of a former project's hub (kind subtask without ⤷) are the person's chats and stay in view.
 */
export function isAgentTalk(c: Chat): boolean {
  return c.parent?.kind === 'subtask' && typeof c.folder !== 'string' && (c.title || '').startsWith(SUBTASK_PREFIX);
}

/** The chat whose folder the path shows (the last chat id), or undefined at a project's top or the root. */
export function folderChatId(p: ExplorerPath): string | undefined {
  for (let i = p.chatIds.length - 1; i >= 0; i--) if (p.chatIds[i] !== AGENTS) return p.chatIds[i];
  return undefined;
}

/** The path shows the "Agent talks" folder of its last chat. */
export const inAgents = (p: ExplorerPath): boolean => p.chatIds[p.chatIds.length - 1] === AGENTS;

/** Parent chat of each chat, only when the parent is in the list; a chat that is its own ancestor stays on top. */
export function chatParents(chats: Chat[]): Map<string, string> {
  const byId = new Map<string, Chat>();
  for (const c of chats) if (c?.chatId && !byId.has(c.chatId)) byId.set(c.chatId, c);
  const raw = (c: Chat): string | undefined => {
    const pid = placeParent(c);
    return pid && pid !== c.chatId && byId.has(pid) ? pid : undefined;
  };
  const out = new Map<string, string>();
  for (const c of byId.values()) {
    const pid = raw(c);
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
      cur = next ? raw(next) : undefined;
    }
    if (!cyclic) out.set(c.chatId, pid);
  }
  return out;
}

/** The folder a chat lives in: its project and the chain of its parent chats (top first). */
export function pathOfChat(projectId: string, chatId: string, chats: Chat[]): ExplorerPath {
  const live = chats.filter((c) => !c.archived);
  const parents = chatParents(live);
  const byId = new Map(live.map((c) => [c.chatId, c]));
  // an agent talk is reached through the "Agent talks" folder of its parent
  const talk = (id: string) => parents.has(id) && !!byId.get(id) && isAgentTalk(byId.get(id)!);
  const chain: string[] = talk(chatId) ? [AGENTS] : [];
  const seen = new Set<string>();
  for (let cur = parents.get(chatId); cur && !seen.has(cur); cur = parents.get(cur)) {
    seen.add(cur);
    chain.unshift(...(talk(cur) ? [AGENTS, cur] : [cur]));
  }
  return { projectId, chatIds: chain };
}

export function samePath(a: ExplorerPath, b: ExplorerPath): boolean {
  return (a.projectId || '') === (b.projectId || '') && a.chatIds.join('/') === b.chatIds.join('/');
}

export function parentPath(p: ExplorerPath): ExplorerPath {
  if (p.chatIds.length) return { projectId: p.projectId, chatIds: p.chatIds.slice(0, -1) };
  return ROOT_PATH;
}

/** The folder of a chat that has children; a chat without children is opened, not entered. */
export function childPath(p: ExplorerPath, item: ExplorerItem): ExplorerPath | null {
  if (item.kind === 'project') return { projectId: item.project.projectId, chatIds: [] };
  if (item.kind === 'chat' && item.folder && p.projectId) return { projectId: p.projectId, chatIds: [...p.chatIds, item.chat.chatId] };
  if (item.kind === 'agents' && p.projectId) return { projectId: p.projectId, chatIds: [...p.chatIds, AGENTS] };
  return null;
}

/**
 * The path that still exists: a project that is gone leads to the root, a chat folder that is gone or has no children
 * any more leads to the nearest folder above it. `chats` are the chats of the path's project (null: not loaded yet,
 * the path is kept).
 */
export function validPath(p: ExplorerPath, projects: Project[], chats: Chat[] | null): ExplorerPath {
  if (!p.projectId) return ROOT_PATH;
  if (!projects.some((x) => x.projectId === p.projectId)) return ROOT_PATH;
  if (!chats) return p;
  const live = chats.filter((c) => !c.archived);
  const parents = chatParents(live);
  const kids = new Set(parents.values());
  const ids = new Set(live.map((c) => c.chatId));
  const byId = new Map(live.map((c) => [c.chatId, c]));
  const keep: string[] = [];
  for (const id of p.chatIds) {
    const last = keep.length ? keep[keep.length - 1] : undefined;
    if (id === AGENTS) {
      // only right after a chat that still has agent talks
      if (!last || last === AGENTS || !live.some((c) => parents.get(c.chatId) === last && isAgentTalk(c))) break;
      keep.push(id);
      continue;
    }
    const above = last === AGENTS ? keep[keep.length - 2] : last;
    const c = byId.get(id);
    if (!c || !ids.has(id) || !kids.has(id) || parents.get(id) !== above) break;
    // an agent talk is inside the "Agent talks" folder, the person's chats are not
    if (above && isAgentTalk(c) !== (last === AGENTS)) break;
    keep.push(id);
  }
  return keep.length === p.chatIds.length ? p : { projectId: p.projectId, chatIds: keep };
}

export interface ItemOpts {
  sort: SortMode;
  filter?: string;
  /** newest activity of a project (search.projectFreshness) */
  projectFresh?: (p: Project) => string;
  /** waiting rank of a project or a chat (higher waits more), for the 'waiting' sort */
  projectWait?: (p: Project) => number;
  chatWait?: (c: Chat) => number;
}

const fresh = (c: Chat): number => ts(c.updatedUtc) || ts(c.createdUtc);
const norm = (s: string) => sortTitle(s || '').toLowerCase();

/**
 * Items of one folder in display order. Root: projects (no family room, no archived ones). A project: its top chats.
 * A chat folder: the chat itself first, then its child chats. Archived chats are not shown (F15.2: the archive).
 * A filter keeps the items whose title contains it.
 */
export function explorerItems(p: ExplorerPath, projects: Project[], chats: Chat[] | null, o: ItemOpts): ExplorerItem[] {
  const f = (o.filter || '').trim().toLowerCase();
  const match = (title: string, extra = '') => !f || title.toLowerCase().includes(f) || extra.toLowerCase().includes(f);
  const cmp = compareBy(o.sort);
  if (!p.projectId) {
    return projects
      .filter((x) => !x.family && !x.archived && match(x.title, x.projectId))
      .map((x) => ({
        x,
        k: {
          id: x.projectId,
          title: x.title,
          created: ts(x.createdUtc),
          fresh: ts(o.projectFresh ? o.projectFresh(x) : x.updatedUtc),
          waiting: o.projectWait?.(x) ?? 0,
        },
      }))
      .sort((a, b) => cmp(a.k, b.k))
      .map(({ x }): ExplorerItem => ({ kind: 'project', key: 'p:' + x.projectId, project: x, title: x.title || x.projectId }));
  }
  if (!chats) return [];
  const live = chats.filter((c) => !c.archived);
  const parents = chatParents(live);
  const kids = new Map<string, Chat[]>();
  for (const c of live) {
    const pid = parents.get(c.chatId);
    if (!pid) continue;
    const arr = kids.get(pid);
    if (arr) arr.push(c);
    else kids.set(pid, [c]);
  }
  // subtree freshness and waiting, so an active subchat lifts its folder
  const treeFresh = new Map<string, number>();
  const treeWait = new Map<string, number>();
  const size = new Map<string, number>();
  const visit = (c: Chat, depth: number): void => {
    let fr = fresh(c);
    let w = o.chatWait?.(c) ?? 0;
    let n = 0;
    if (depth < 64) {
      for (const k of kids.get(c.chatId) ?? []) {
        visit(k, depth + 1);
        fr = Math.max(fr, treeFresh.get(k.chatId) ?? 0);
        w = Math.max(w, treeWait.get(k.chatId) ?? 0);
        n += 1 + (size.get(k.chatId) ?? 0);
      }
    }
    treeFresh.set(c.chatId, fr);
    treeWait.set(c.chatId, w);
    size.set(c.chatId, n);
  };
  const here = folderChatId(p);
  const agentsHere = !!here && inAgents(p);
  const inside = here ? kids.get(here) ?? [] : [];
  // a chat folder: the person's chats here, the agent talks in their own folder
  const talks = inside.filter(isAgentTalk);
  const list = here ? inside.filter((c) => isAgentTalk(c) === agentsHere) : live.filter((c) => !parents.has(c.chatId));
  list.forEach((c) => visit(c, 0));
  const sorted = list
    .filter((c) => match(itemTitle(c.title)))
    .map((c) => ({
      c,
      k: { id: c.chatId, title: c.title || '', created: ts(c.createdUtc), fresh: treeFresh.get(c.chatId) ?? 0, waiting: treeWait.get(c.chatId) ?? 0 },
    }))
    .sort((a, b) => cmp(a.k, b.k))
    .map(({ c }): ExplorerItem => {
      const n = (kids.get(c.chatId) ?? []).length;
      return { kind: 'chat', key: 'c:' + c.chatId, chat: c, title: itemTitle(c.title), folder: n > 0, count: size.get(c.chatId) ?? 0 };
    });
  const self = here ? live.find((c) => c.chatId === here) : undefined;
  if (!self || agentsHere) return sorted;
  const head: ExplorerItem[] = [{ kind: 'self', key: 's:' + self.chatId, chat: self, title: itemTitle(self.title) }];
  if (talks.length && (!f || AGENTS_TITLE.toLowerCase().includes(f) || talks.some((c) => match(itemTitle(c.title))))) {
    talks.forEach((c) => visit(c, 0));
    const n = talks.reduce((a, c) => a + 1 + (size.get(c.chatId) ?? 0), 0);
    head.push({ kind: 'agents', key: 'a:' + self.chatId, chat: self, title: AGENTS_TITLE, count: n });
  }
  return [...head, ...sorted];
}

/** Path bar: the environment, then the project, then each chat folder; the last one is where the Explorer is. */
export function crumbs(p: ExplorerPath, rootLabel: string, projects: Project[], chats: Chat[] | null): Crumb[] {
  const out: Crumb[] = [{ label: rootLabel, path: ROOT_PATH }];
  if (!p.projectId) return out;
  const proj = projects.find((x) => x.projectId === p.projectId);
  out.push({ label: proj?.title || p.projectId, path: { projectId: p.projectId, chatIds: [] } });
  p.chatIds.forEach((id, i) => {
    const c = id === AGENTS ? undefined : chats?.find((x) => x.chatId === id);
    const label = id === AGENTS ? '🤖 ' + AGENTS_TITLE : c ? itemTitle(c.title) : '…';
    out.push({ label, path: { projectId: p.projectId, chatIds: p.chatIds.slice(0, i + 1) } });
  });
  return out;
}

export interface JumpHit {
  kind: 'project' | 'chat';
  projectId: string;
  chatId?: string;
  title: string;
  where: string; // project title of a chat, '' for a project
  score: number;
}

// Russian / Ukrainian keys of the same place as the Latin ones: "ghjtrn" finds "проект" and back
const LAT = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
const CYR = 'йцукенгшщзхъфывапролджэячсмитьбюё';
function swapLayout(s: string): string {
  let out = '';
  for (const ch of s) {
    const i = LAT.indexOf(ch);
    const j = CYR.indexOf(ch);
    out += i >= 0 ? CYR[i] : j >= 0 ? LAT[j] : ch;
  }
  return out;
}

/** 3 = title starts with the query, 2 = a word starts with it, 1 = contains it, 0 = no match. */
function matchScore(title: string, q: string): number {
  const t = norm(title);
  if (!q || !t) return 0;
  if (t.startsWith(q)) return 3;
  if (t.split(/[^\p{L}\p{N}]+/u).some((w) => w.startsWith(q))) return 2;
  return t.includes(q) ? 1 : 0;
}

/**
 * Ctrl+K: projects and chats whose title matches the query (also typed in the other keyboard layout), best first:
 * start of the title, start of a word, anywhere; projects before chats of the same rank, then the newest.
 * Archived projects and chats and the family room are left out.
 */
export function quickJump(
  query: string,
  projects: Project[],
  chatsOf: (projectId: string) => Chat[] | null | undefined,
  limit = 20,
): JumpHit[] {
  const q = norm(query.trim());
  if (!q) return [];
  const alt = swapLayout(q);
  const score = (title: string) => Math.max(matchScore(title, q), alt !== q ? matchScore(title, alt) - 0.5 : 0);
  const hits: Array<JumpHit & { fresh: number }> = [];
  for (const p of projects) {
    if (p.family || p.archived) continue;
    const s = Math.max(score(p.title), norm(p.projectId) === q ? 3 : 0);
    if (s > 0) hits.push({ kind: 'project', projectId: p.projectId, title: p.title || p.projectId, where: '', score: s + 0.25, fresh: ts(p.updatedUtc) });
    for (const c of chatsOf(p.projectId) || []) {
      if (c.archived) continue;
      const t = itemTitle(c.title);
      const cs = score(t);
      if (cs > 0) hits.push({ kind: 'chat', projectId: p.projectId, chatId: c.chatId, title: t, where: p.title || p.projectId, score: cs, fresh: fresh(c) });
    }
  }
  hits.sort((a, b) => b.score - a.score || b.fresh - a.fresh || a.title.localeCompare(b.title));
  return hits.slice(0, limit).map(({ fresh: _f, ...h }) => h);
}

/** Next focused index of a grid/list for a key, or null when the key does not move the focus. */
export function moveFocus(key: string, index: number, count: number, columns: number): number | null {
  if (count <= 0) return null;
  const cols = Math.max(1, columns);
  const clamp = (i: number) => Math.min(count - 1, Math.max(0, i));
  switch (key) {
    case 'ArrowRight':
      return clamp(index + 1);
    case 'ArrowLeft':
      return clamp(index - 1);
    case 'ArrowDown':
      return clamp(index + cols);
    case 'ArrowUp':
      return clamp(index - cols);
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    default:
      return null;
  }
}

/** Short date of the list view: the time today, day and month this year, else the date with the year. */
export function shortDate(utc: string | undefined | null, now = new Date()): string {
  const d = utc ? new Date(utc) : null;
  if (!d || isNaN(d.getTime())) return '';
  if (d.toDateString() === now.toDateString()) return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

// ---------- F15.2: moving a chat (drag and drop, "Move to…") ----------

/** Where a chat goes: a project and the chat it will sit under there ('' = top of the project). */
export interface MoveDest {
  projectId: string;
  folder: string;
}

/** The place a dropped item stands for: a project (its top), a chat or the open folder's own chat (inside it). */
export function destOfItem(p: ExplorerPath, item: ExplorerItem): MoveDest | null {
  if (item.kind === 'project') return { projectId: item.project.projectId, folder: '' };
  // the "Agent talks" folder takes no chats: a chat moved by hand is the person's own
  if (item.kind === 'agents') return null;
  return p.projectId ? { projectId: p.projectId, folder: item.chat.chatId } : null;
}

/** The place a path-bar folder stands for; the root (the list of projects) is no place for a chat. */
export function destOfPath(p: ExplorerPath): MoveDest | null {
  // the "Agent talks" folder takes no chats (see destOfItem)
  if (inAgents(p)) return null;
  return p.projectId ? { projectId: p.projectId, folder: folderChatId(p) ?? '' } : null;
}

/** The chat and every chat under it (archived ones too), the chat first; they move to another project together. */
export function subtreeOf(chat: Chat, chats: Chat[]): Chat[] {
  const kids = new Map<string, Chat[]>();
  for (const c of chats) {
    const pid = placeParent(c);
    if (!pid || pid === c.chatId) continue;
    const arr = kids.get(pid);
    if (arr) arr.push(c);
    else kids.set(pid, [c]);
  }
  const seen = new Set<string>([chat.chatId]);
  const out: Chat[] = [chat];
  const stack = [...(kids.get(chat.chatId) ?? [])];
  while (stack.length) {
    const c = stack.pop()!;
    if (seen.has(c.chatId)) continue;
    seen.add(c.chatId);
    out.push(c);
    stack.push(...(kids.get(c.chatId) ?? []));
  }
  return out;
}

/**
 * Why the chat cannot go there, or null when it can. Only chats of the person's own projects move (a shared chat
 * stays where its owner keeps it), never into the family room, an archived project, itself or a chat under it.
 * `destChats` are the chats of the target project (null: not loaded - a target chat cannot be checked yet).
 */
export function moveBlock(chat: Chat, dest: MoveDest, projects: Project[], destChats: Chat[] | null): string | null {
  if (chat.draft) return 'Send the first message first.';
  if (chat.shareRole) return 'A shared chat stays where its owner keeps it.';
  const from = projects.find((x) => x.projectId === chat.projectId);
  const to = projects.find((x) => x.projectId === dest.projectId);
  if (!from || from.shareRole) return 'Only chats of your own projects move.';
  if (!to || to.shareRole || to.family || to.archived) return 'Only into your own projects.';
  if (dest.folder) {
    if (dest.folder === chat.chatId) return 'A chat cannot go inside itself.';
    if (!destChats) return 'Open that project first.';
    const target = destChats.find((c) => c.chatId === dest.folder);
    if (!target || target.archived || target.draft) return 'That chat is not there any more.';
    if (dest.projectId === chat.projectId && subtreeOf(chat, destChats).some((c) => c.chatId === dest.folder)) {
      return 'A chat cannot go inside a chat under it.';
    }
  }
  // a talk taken out of "Agent talks" into its own chat's folder becomes the person's chat there (folder is set)
  if (isAgentTalk(chat) && dest.projectId === chat.projectId && dest.folder === placeParent(chat)) return null;
  if (dest.projectId === chat.projectId && (placeParent(chat) ?? '') === dest.folder) return 'It is already there.';
  return null;
}

export interface MoveTarget {
  dest: MoveDest;
  title: string;
  depth: number; // 0 = a project, 1+ = a chat under it
}

/** "Move to…": each own project and, when its chats are loaded, its chats as a tree; the ones it cannot go to left out. */
export function moveTargets(chat: Chat, projects: Project[], chatsOf: (projectId: string) => Chat[] | null | undefined): MoveTarget[] {
  const out: MoveTarget[] = [];
  for (const p of projects) {
    if (p.family || p.archived || p.shareRole) continue;
    const list = chatsOf(p.projectId) ?? null;
    const top: MoveDest = { projectId: p.projectId, folder: '' };
    if (!moveBlock(chat, top, projects, list)) out.push({ dest: top, title: p.title || p.projectId, depth: 0 });
    if (!list) continue;
    const live = list.filter((c) => !c.archived && !c.draft);
    const parents = chatParents(live);
    const kids = new Map<string, Chat[]>();
    for (const c of live) {
      const k = parents.get(c.chatId) ?? '';
      const arr = kids.get(k);
      if (arr) arr.push(c);
      else kids.set(k, [c]);
    }
    const walk = (under: string, depth: number) => {
      if (depth > 32) return;
      const arr = [...(kids.get(under) ?? [])].sort((a, b) => norm(itemTitle(a.title)).localeCompare(norm(itemTitle(b.title))));
      for (const c of arr) {
        const dest = { projectId: p.projectId, folder: c.chatId };
        if (!moveBlock(chat, dest, projects, list)) out.push({ dest, title: itemTitle(c.title), depth });
        if (c.chatId !== chat.chatId) walk(c.chatId, depth + 1);
      }
    };
    walk('', 1);
  }
  return out;
}
