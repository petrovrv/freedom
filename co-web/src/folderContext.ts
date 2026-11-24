// SPDX-License-Identifier: AGPL-3.0-or-later
// F15.2 part 4 (SPEC §3.6b п. 2): the context switch of a folder - what the chats inside a folder get from it in a new
// agent session: full, a summary, or nothing. A chat folder is a chat with chats under it (its place: chat.folder, else
// its lineage parent); a project folder is the project. "Automatic" (the default of a chat folder) is the rule of the
// deep chat: the nearest folder in full, the farther ones as a summary. The switch works on top of the chat's own
// context mode (contextLevels.ts): only "Full context" takes folders at all.
// A branch or subtask keeps the messages it started from (its lineage snapshot) whatever the switch: they are the
// beginning of the chat itself. Pure logic here; ChatView loads the protocols and messages and sends the block.
import type { Chat, Project } from './types';
import type { ProtocolEntry } from './dbapi';
import { placeParent } from './chatTree.ts';

export type FolderCtx = 'auto' | 'full' | 'summary' | 'off';
export type LevelMode = 'full' | 'summary' | 'off';
export type ProjectCtx = 'full' | 'summary' | 'off';

export const FOLDER_CTX_LABEL: Record<FolderCtx, string> = {
  auto: 'Automatic',
  full: 'Full',
  summary: 'Summary',
  off: 'Off',
};

export const FOLDER_CTX_HINT: Record<FolderCtx, string> = {
  auto: 'Chats right inside get this folder in full, chats deeper down get its summary.',
  full: 'Chats inside get the protocol of this chat and its newest messages.',
  summary: 'Chats inside get only its summary: the automatic summary and the decisions, constraints and open questions of its protocol.',
  off: 'Chats inside get nothing from this chat.',
};

export const PROJECT_CTX_HINT: Record<ProjectCtx, string> = {
  full: 'Chats of the project get its instructions, its memory and the summaries of its recent chats.',
  summary: 'Chats of the project get its instructions and a short memory (long entries cut, no recent chats).',
  off: 'Chats of the project get neither its instructions nor its memory (your profile stays).',
};

/** How many folders above a chat are looked at. */
export const MAX_LEVELS = 6;
/** Characters per folder: newest messages of a full one, the protocol of a full one, a summary. */
export const FULL_MESSAGES_CHARS = 4000;
export const FULL_PROTOCOL_CHARS = 5000;
export const SUMMARY_CHARS = 1500;
/** All folders together. */
export const TOTAL_CHARS = 14000;

export function folderCtxOf(c: Pick<Chat, 'folderContext'> | null | undefined): FolderCtx {
  const v = c?.folderContext;
  return v === 'full' || v === 'summary' || v === 'off' ? v : 'auto';
}

export function projectCtxOf(p: Pick<Project, 'folderContext'> | null | undefined): ProjectCtx {
  const v = p?.folderContext;
  return v === 'summary' || v === 'off' ? v : 'full';
}

export interface FolderLevel {
  chat: Chat;
  /** 1 = the folder the chat sits in */
  depth: number;
  /** what the chat gets from it */
  mode: LevelMode;
  /** the switch of that folder */
  set: FolderCtx;
  /** the chat's lineage parent: its messages come with the lineage snapshot, the folder adds nothing */
  lineage: boolean;
}

/** Folders above a chat, the nearest first, by its place (folder, else lineage parent). Stops at a loop, a chat not in
 * the list (another project, deleted) or MAX_LEVELS. */
export function folderLevels(chat: Pick<Chat, 'chatId' | 'folder' | 'parent'>, chats: Chat[]): FolderLevel[] {
  const byId = new Map(chats.map((c) => [c.chatId, c]));
  const out: FolderLevel[] = [];
  const seen = new Set<string>([chat.chatId]);
  let cur = placeParent(chat as Chat);
  for (let depth = 1; cur && depth <= MAX_LEVELS && !seen.has(cur); depth++) {
    const c = byId.get(cur);
    if (!c) break;
    seen.add(cur);
    const set = folderCtxOf(c);
    const mode: LevelMode = set === 'auto' ? (depth === 1 ? 'full' : 'summary') : set;
    out.push({ chat: c, depth, mode, set, lineage: chat.parent?.chatId === c.chatId });
    cur = placeParent(c);
  }
  return out;
}

/** The folders whose content is loaded and sent (not off, not the lineage parent). */
export function levelsToSend(levels: FolderLevel[]): FolderLevel[] {
  return levels.filter((l) => l.mode !== 'off' && !l.lineage);
}

/** One key of what the folders give, stored on the message: a change (move, switch) starts a new agent session. */
export function folderKey(levels: FolderLevel[], project: ProjectCtx): string {
  const parts = levelsToSend(levels).map((l) => `${l.chat.chatId}:${l.mode}`);
  return project === 'full' && !parts.length ? '' : [`p:${project}`, ...parts].join(',');
}

const KEEP_IN_SUMMARY = new Set<ProtocolEntry['section']>(['decision', 'constraint', 'question']);
const SECTION_HEAD: Partial<Record<ProtocolEntry['section'], string>> = {
  decision: 'Decisions',
  constraint: 'Constraints',
  question: 'Open questions',
  fact: 'Facts',
  artifact: 'Artifacts',
};

function cut(s: string, max: number): string {
  const t = s.trim();
  return t.length <= max ? t : `${t.slice(0, Math.max(0, max - 2)).trimEnd()} …`;
}

/** Protocol lines of a folder: all active sections in full mode, decisions/constraints/questions in a summary. */
export function protocolLines(entries: ProtocolEntry[] | null | undefined, mode: 'full' | 'summary'): string {
  const active = (entries || []).filter((e) => e.status === 'active' && (mode === 'full' ? e.section !== 'answer' : KEEP_IN_SUMMARY.has(e.section)));
  const groups: string[] = [];
  for (const sec of Object.keys(SECTION_HEAD) as ProtocolEntry['section'][]) {
    const items = active.filter((e) => e.section === sec).sort((a, b) => a.no - b.no).map((e) => `- ${e.text.trim()}`);
    if (items.length) groups.push(`${SECTION_HEAD[sec]}:\n${items.join('\n')}`);
  }
  return groups.join('\n');
}

export interface LevelContent {
  level: FolderLevel;
  protocol?: ProtocolEntry[] | null;
  /** newest messages of a full folder, already as text (oldest first) */
  messages?: string[];
}

/** The block of the folders above a chat for a new agent session ('' when there is nothing). */
export function buildFolderBlock(items: LevelContent[]): string {
  const parts: string[] = [];
  let used = 0;
  for (const it of items) {
    const { level } = it;
    if (level.mode === 'off' || level.lineage) continue;
    const title = (level.chat.title || level.chat.chatId).trim();
    const head = `## «${title}» (${level.depth === 1 ? 'the folder of this chat' : `${level.depth} folders up`}, ${level.mode}) — chat ${level.chat.chatId}`;
    const body: string[] = [];
    if (level.chat.summary?.trim()) body.push(`Summary: ${cut(level.chat.summary, SUMMARY_CHARS)}`);
    const proto = protocolLines(it.protocol, level.mode);
    if (proto) body.push(cut(proto, level.mode === 'full' ? FULL_PROTOCOL_CHARS : SUMMARY_CHARS));
    if (level.mode === 'full' && it.messages?.length) {
      const kept: string[] = [];
      let n = 0;
      for (let i = it.messages.length - 1; i >= 0; i--) {
        const m = it.messages[i];
        if (n + m.length > FULL_MESSAGES_CHARS) {
          if (!kept.length) kept.unshift(cut(m, FULL_MESSAGES_CHARS));
          break;
        }
        kept.unshift(m);
        n += m.length;
      }
      body.push(`Newest messages:\n${kept.join('\n\n')}`);
    }
    if (!body.length) continue;
    const text = `${head}\n${body.join('\n')}`;
    if (used + text.length > TOTAL_CHARS) {
      parts.push(`## «${title}» and the folders above it: left out (context budget)`);
      break;
    }
    parts.push(text);
    used += text.length;
  }
  if (!parts.length) return '';
  return [
    '[Folders above this chat (nearest first). This chat sits inside them; their content is background, this chat\'s own messages win.]',
    ...parts,
    '[End of folders above]',
  ].join('\n\n');
}

/** Project memory as a summary: the entry lines, each cut to 160 characters, the newest 40. */
export function memoryDigest(md: string): string {
  const lines = md.replace(/^﻿/, '').split(/\r?\n/).filter((l) => l.startsWith('- ') && l.slice(2).trim());
  const kept = lines.slice(-40).map((l) => cut(l, 160));
  if (!kept.length) return '';
  return `[Project memory, summary${lines.length > kept.length ? `: the newest ${kept.length} of ${lines.length} entries` : ''}; long entries cut]\n${kept.join('\n')}\n[End of project memory]`;
}
