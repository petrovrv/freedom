// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.129.0 (SPEC §3.6b п. 1, «Папка чата»): the folder of a chat in the Explorer (and under the tree) has a fixed set of
// subfolders: Subchats · Context · Correspondence · Events · Watchers · Documents · Products. Documents and Products are
// always shown, the others only when they hold something (on a phone the same). Pure logic here, the view is
// FolderContents.tsx (ChatFolder) and ChatContext.tsx.
// - Documents / Products: the chat's files split by who added them (the first version from a message: an upload of the
//   person or an artifact of the agent; a branch follows the file it came from; else the server's head origin). No
//   schema change.
// - Context: what a new agent session of this chat gets (ChatView.sendToChat, folderContext.ts), one line per source.
// - Events: the time line of the chat; now the agreements of its protocol (secretary section "agreement": "<who>:
//   <what> [due YYYY-MM-DD]", co-agent/cron/protocol-apply.php).
// - One point for more content (registerChatPart): a part gets items from whoever registers for it, the core names no
//   source. Correspondence and Watchers have no core content, so they stay hidden until something registers.
import type { ContextMode } from './contextLevels';
import type { FolderLevel, ProjectCtx } from './folderContext';
import type { ProtocolEntry } from './dbapi';

export type ChatPartId = 'subchats' | 'context' | 'correspondence' | 'events' | 'watchers' | 'documents' | 'products';

export interface ChatPartDef {
  id: ChatPartId;
  icon: string;
  label: string;
  hint: string;
  /** shown even when empty */
  always: boolean;
}

export const CHAT_PARTS: readonly ChatPartDef[] = [
  { id: 'subchats', icon: '🗂️', label: 'Subchats', hint: 'Chats inside this chat (branches, subtasks, chats moved here)', always: false },
  { id: 'context', icon: '🧠', label: 'Context', hint: 'What the agent of this chat gets in a new session', always: false },
  { id: 'correspondence', icon: '✉️', label: 'Correspondence', hint: 'Mail and messages that belong to this chat', always: false },
  { id: 'events', icon: '🕑', label: 'Events', hint: 'The time line of this chat: agreements, meetings, calls, findings of watchers', always: false },
  { id: 'watchers', icon: '👁️', label: 'Watchers', hint: 'Watchers that work for this chat', always: false },
  { id: 'documents', icon: '📄', label: 'Documents', hint: 'Files you added to this chat, with their versions', always: true },
  { id: 'products', icon: '📦', label: 'Products', hint: 'Files the agent made in this chat, with their versions, and its scratch folder', always: true },
];

/** The parts to show, in their fixed order: the "always" ones and those with something inside. */
export function visibleParts(counts: Partial<Record<ChatPartId, number>>): ChatPartDef[] {
  return CHAT_PARTS.filter((p) => p.always || (counts[p.id] ?? 0) > 0);
}

// ---------- the point for more content of a part ----------

export interface ChatRef {
  projectId: string;
  chatId: string;
}

/** Content of a part from outside the core. `count` decides whether the part is shown; `render` gives its view
 * (a React node in the shell). Call chatPartsChanged() when a count changes. */
export interface ChatPartContribution<R = unknown> {
  part: ChatPartId;
  key: string;
  count: (chat: ChatRef) => number;
  render: (chat: ChatRef) => R;
}

let contributions: ChatPartContribution[] = [];
const listeners = new Set<() => void>();

export function chatPartsChanged(): void {
  listeners.forEach((f) => f());
}

/** Registers content for a part; the same key replaces the earlier one. Returns the unregister function. */
export function registerChatPart(c: ChatPartContribution): () => void {
  contributions = [...contributions.filter((x) => x.key !== c.key), c];
  chatPartsChanged();
  return () => {
    const before = contributions.length;
    contributions = contributions.filter((x) => x !== c);
    if (contributions.length !== before) chatPartsChanged();
  };
}

export function chatPartContributions(part?: ChatPartId): ChatPartContribution[] {
  return part ? contributions.filter((c) => c.part === part) : contributions;
}

export function onChatPartsChanged(f: () => void): () => void {
  listeners.add(f);
  return () => {
    listeners.delete(f);
  };
}

/** The core's counts plus those of the contributions (a failing count counts 0). */
export function partCounts(core: Partial<Record<ChatPartId, number>>, chat: ChatRef, list: ChatPartContribution[] = contributions): Partial<Record<ChatPartId, number>> {
  const out: Partial<Record<ChatPartId, number>> = { ...core };
  for (const c of list) {
    let n = 0;
    try {
      n = Math.max(0, Number(c.count(chat)) || 0);
    } catch {
      n = 0;
    }
    out[c.part] = (out[c.part] ?? 0) + n;
  }
  return out;
}

// ---------- Documents / Products ----------

export type FileSide = 'documents' | 'products';

/** What the split needs of a stream (chatFiles.ts ChatStream). */
export interface StreamLike {
  versions: { origin: 'agent' | 'user' }[];
  row: { id: number; head: { origin: string | null }; extra?: { parent?: { id: number } } } | null;
}

/** Who added the file: its first version from a message; a branch follows the file it came from; else the head origin
 * the server keeps (an upload of the person = Documents, anything else = Products). */
export function fileSide(s: StreamLike, byId: Map<number, StreamLike>): FileSide {
  let cur: StreamLike | undefined = s;
  for (let hops = 0; cur && hops < 10; hops++) {
    const first = cur.versions[0]?.origin;
    if (first) return first === 'user' ? 'documents' : 'products';
    const parent: number | undefined = cur.row?.extra?.parent?.id;
    const next: StreamLike | undefined = parent != null ? byId.get(parent) : undefined;
    if (!next || next === cur) break;
    cur = next;
  }
  return s.row?.head.origin === 'user' ? 'documents' : 'products';
}

export function splitStreams<T extends StreamLike>(streams: T[]): { documents: T[]; products: T[] } {
  const byId = new Map<number, StreamLike>();
  for (const s of streams) if (s.row) byId.set(s.row.id, s);
  const documents: T[] = [];
  const products: T[] = [];
  for (const s of streams) (fileSide(s, byId) === 'documents' ? documents : products).push(s);
  return { documents, products };
}

// ---------- Events: agreements of the protocol ----------

export interface Agreement {
  no: number;
  msg: number;
  who: string;
  what: string;
  due: string | null;
  status: ProtocolEntry['status'];
}

const DUE_RE = /\s*\[due (\d{4}-\d{2}-\d{2})\]\s*$/;

/** "<who>: <what> [due YYYY-MM-DD]" → its parts; a text without them is all "what". */
export function agreementOf(e: Pick<ProtocolEntry, 'no' | 'msg' | 'text' | 'status'>): Agreement {
  let t = e.text.trim();
  let due: string | null = null;
  const m = DUE_RE.exec(t);
  if (m) {
    due = m[1];
    t = t.slice(0, m.index).trim();
  }
  let who = '';
  const colon = t.indexOf(': ');
  if (colon > 0 && colon <= 60) {
    who = t.slice(0, colon).trim();
    t = t.slice(colon + 2).trim();
  }
  return { no: e.no, msg: e.msg, who, what: t, due, status: e.status };
}

/** Open agreements (active) by due date (none last), then by number; done ones (closed) newest first. */
export function agreements(entries: ProtocolEntry[] | null | undefined): { open: Agreement[]; done: Agreement[] } {
  const all = (entries || []).filter((e) => e.section === 'agreement').map(agreementOf);
  const open = all.filter((a) => a.status === 'active').sort((a, b) => (a.due && b.due ? a.due.localeCompare(b.due) : a.due ? -1 : b.due ? 1 : 0) || a.no - b.no);
  const done = all.filter((a) => a.status === 'closed').sort((a, b) => b.no - a.no);
  return { open, done };
}

export type DueState = 'overdue' | 'today' | 'soon' | 'later';

/** today = the local date YYYY-MM-DD; soon = within 3 days. */
export function dueState(due: string | null, today: string): DueState | null {
  if (!due) return null;
  if (due < today) return 'overdue';
  if (due === today) return 'today';
  const days = (Date.parse(due + 'T00:00:00Z') - Date.parse(today + 'T00:00:00Z')) / 86400000;
  return days <= 3 ? 'soon' : 'later';
}

export function localDate(d: Date = new Date()): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

// ---------- Context ----------

export type CtxState = 'on' | 'summary' | 'off';

export interface ContextItem {
  key: string;
  icon: string;
  label: string;
  state: CtxState;
  detail: string;
  /** where it is switched: 'mode' here (the chat's context mode), 'project' in the project's Properties, 'folder' in the
   * Properties of that folder chat, '' nowhere (fixed, or needs the server: written in SPEC §3.6b) */
  switch: 'mode' | 'project' | 'folder' | '';
  /** the folder chat of a 'folder' line */
  chatId?: string;
}

export interface ContextInput {
  mode: ContextMode;
  projectCtx: ProjectCtx;
  projectTitle: string;
  /** active entries of this chat's protocol (null = not loaded) */
  protocolEntries: number | null;
  /** a branch or subtask: starts from its parent's messages */
  lineageTitle: string | null;
  /** a chat with a lineage parent (a subtask from a task of its own has no lineageTitle) */
  child?: boolean;
  levels: FolderLevel[];
  contextFolders: number;
  shared: boolean;
  family: boolean;
}

const MODE_WORD: Record<ContextMode, string> = { full: 'full context', reference: 'reference mode', clean: 'clean view' };

/** One line per source of a new agent session of the chat, in the order the agent gets them. */
export function contextItems(i: ContextInput): ContextItem[] {
  const full = i.mode === 'full';
  const offWhy = `not in ${MODE_WORD[i.mode]}`;
  const out: ContextItem[] = [];
  out.push({ key: 'rules', icon: '⚖️', label: 'Rules of the workbench and its modules', state: 'on', detail: 'always: safety rules, the modes of this chat, instructions of enabled modules', switch: '' });
  out.push({ key: 'profile', icon: '👤', label: 'Your profile', state: i.mode === 'clean' ? 'off' : 'on', detail: i.mode === 'clean' ? offWhy : 'the profile of your environment (Settings)', switch: 'mode' });
  const pOn = full && i.projectCtx !== 'off';
  out.push({ key: 'instructions', icon: '📋', label: `Instructions of «${i.projectTitle}»`, state: pOn ? 'on' : 'off', detail: !full ? offWhy : pOn ? 'the project\'s instructions' : 'the project folder gives nothing', switch: full ? 'project' : 'mode' });
  const mState: CtxState = !full || i.projectCtx === 'off' ? 'off' : i.projectCtx === 'summary' ? 'summary' : 'on';
  out.push({ key: 'memory', icon: '🧾', label: 'Project memory', state: mState, detail: !full ? offWhy : mState === 'summary' ? 'short: long entries cut, no recent chats' : mState === 'on' ? 'entries (an index when the file is long) and the summaries of recent chats' : 'the project folder gives nothing', switch: full ? 'project' : 'mode' });
  out.push({ key: 'dossier', icon: '🪪', label: 'Dossier', state: full ? 'on' : 'off', detail: !full ? offWhy : 'facts about you and the people around you, those on the topic of the first message first', switch: full ? '' : 'mode' });
  if (i.lineageTitle !== null) {
    out.push({ key: 'lineage', icon: '⤴️', label: `Messages it started from («${i.lineageTitle}»)`, state: full ? 'on' : 'off', detail: full ? 'the start of this chat (its lineage snapshot) with that chat\'s protocol' : offWhy, switch: 'mode' });
  } else if (i.child) {
    out.push({ key: 'protocol', icon: '📝', label: 'Protocol of this chat', state: 'off', detail: 'a subtask started from a task of its own: its own messages, no protocol', switch: '' });
  } else {
    const n = i.protocolEntries;
    out.push({ key: 'protocol', icon: '📝', label: 'Protocol of this chat', state: n ? 'on' : 'off', detail: n === null ? 'not loaded' : n ? `${n} entr${n === 1 ? 'y' : 'ies'} and the newest messages instead of the whole history` : 'no entries yet: the messages themselves', switch: '' });
  }
  for (const l of i.levels) {
    if (l.lineage) continue;
    const st: CtxState = !full || l.mode === 'off' ? 'off' : l.mode === 'summary' ? 'summary' : 'on';
    const where = l.depth === 1 ? 'the folder of this chat' : `${l.depth} folders up`;
    out.push({ key: `folder:${l.chat.chatId}`, icon: '🗂️', label: `«${(l.chat.title || l.chat.chatId).trim()}»`, state: st, detail: `${where}: ${!full ? offWhy : st === 'on' ? 'protocol and newest messages' : st === 'summary' ? 'summary and decisions' : 'gives nothing'}`, switch: full ? 'folder' : 'mode', chatId: l.chat.chatId });
  }
  if (i.contextFolders > 0) {
    out.push({ key: 'ctxfolders', icon: '📁', label: `Context folders of the project (${i.contextFolders})`, state: i.shared ? 'off' : 'on', detail: i.shared ? 'not in a shared chat' : 'readable by the agent where its computer supports it', switch: 'project' });
  }
  return out;
}

/** What makes the Context part show: something of this chat itself (protocol entries, the start of a branch, folders
 * above that give something, context folders). Project instructions, memory and the profile alone do not: they are
 * the same for every chat and live in the project's and the environment's Properties. */
export function contextCount(i: ContextInput): number {
  let n = 0;
  if (i.protocolEntries) n++;
  if (i.lineageTitle !== null && i.mode === 'full') n++;
  if (i.mode === 'full') n += i.levels.filter((l) => !l.lineage && l.mode !== 'off').length;
  if (i.contextFolders > 0 && !i.shared) n++;
  return n;
}
