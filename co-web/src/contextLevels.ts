// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.36.0: context levels for new agent sessions (1 Instincts, 2 Reflexes, 3 Heuristics, 4 Experience, 5 Memory),
// context modes (full / reference / clean) and the live protocol: a new session gets this chat's protocol (without
// quotes) plus the messages after it instead of the whole transcript.
import type { ChatEvent } from './types';
import type { ChatProtocol, ProtocolEntry } from './dbapi';
import { stripTitleMarker } from './chatTitle';
import { drivePath } from './members';

export type ContextMode = 'full' | 'reference' | 'clean';

export const CONTEXT_MODE_LABEL: Record<ContextMode, string> = {
  full: 'Full context',
  reference: 'Reference',
  clean: 'Clean view',
};

export const CONTEXT_MODE_HINT: Record<ContextMode, string> = {
  full: 'Everything the agent knows about you and the project is included.',
  reference:
    'No project memory, other chats or project instructions are included, which suits reference and teaching questions.',
  clean: 'No profile, project memory or project context; only the messages of this chat itself, for an unbiased answer.',
};

export const INSTINCTS = [
  "- Never send emails or messages on the user's behalf; prepare drafts only.",
  '- Secrets live only in the keyring, never in files, code or chat.',
  '- Ask before any irreversible or outward-facing action.',
  '- Report outcomes faithfully, including failed tests and skipped steps.',
  "- If the request hinges on an allusion, comparison, name or reference you cannot place, look for it first in the chat, protocol and dossier; if it stays unclear, ask one short question and do only the parts that do not depend on it. Never build advice on a guessed meaning.",
  '- Claims about specific people, companies and events (what happened, who did what, when, how much) must come from a source you read in this job (chat, protocol, dossier, mail, files, web); otherwise leave them out or label them as your assumption. General knowledge and your own calculations need no source.',
  "- A missing tool or blocker is a subtask, not a stop: first try legitimate workarounds (other installed tools, browser, APIs, a quick script, manual steps); if none fits, build or extend the tool in the tool library, then finish the original task.",
  "- Stop and ask only when every path needs the user's decision or credentials (or the request itself is unclear, see above), or would break another Instinct; then report which paths you tried and why each failed. Workarounds and new tools obey every other Instinct.",
  '- When levels conflict, the lower-numbered level wins.',
].join('\n');

const LEVELS_FRAME =
  '[Context levels: 1 Instincts, 2 Reflexes, 3 Heuristics, 4 Experience, 5 Memory. A lower number wins when they conflict.]';
const REFERENCE_NOTE =
  '[Reference mode: project memory, project instructions and other chats are deliberately withheld. Answer as a reference or a teacher for this question.]';
const CLEAN_REFLEX =
  "Clean view: you know nothing about the user, their projects or earlier chats. Answer as a neutral expert, in the language of the user's message.";
const OMITTED_MARKER = '[… earlier turns omitted …]';
const TRUNCATED_MARKER = '[… truncated …]';

export interface LevelsInput {
  mode: ContextMode;
  envProfile: string;
  projectInstructions: string;
  projectMemory: string;
  // 0.76.0: the work-plan line (workPlan.ts), in every mode; empty where the executor cannot keep the plan file
  workPlan?: string;
  // F10 (ext/1 chat.mode): rules of the chat's modes that are on, or their "turned off" notes (moduleModes.ts), in every mode
  modes?: string;
  // F10 (ext/1 context): instructions of enabled modules, in every mode
  moduleContext?: string;
  // 0.85.0: the dossier block (db.php dossier_context; people only in shared projects), level 4 of the full mode
  dossier?: string;
}

export function buildLevels(i: LevelsInput): string {
  const lines: string[] = [LEVELS_FRAME];
  const push = (header: string, content: string): void => {
    const c = content.trim();
    if (c) lines.push(header, c);
  };

  push('[1. Instincts]', INSTINCTS);

  const reflexes = i.mode === 'clean' ? CLEAN_REFLEX : (i.envProfile ?? '');
  push('[2. Reflexes]', reflexes);
  if (i.mode === 'reference') lines.push(REFERENCE_NOTE);

  const heuristics = [i.mode === 'full' ? (i.projectInstructions ?? '').trim() : '', (i.workPlan ?? '').trim(), (i.modes ?? '').trim(), (i.moduleContext ?? '').trim()];
  push('[3. Heuristics]', heuristics.filter((x) => x).join('\n'));
  if (i.mode === 'full') push('[4. Experience]', [(i.dossier ?? '').trim(), (i.projectMemory ?? '').trim()].filter((x) => x).join('\n\n'));

  return lines.join('\n');
}

const SECTION_ORDER: ReadonlyArray<[ProtocolEntry['section'], string]> = [
  ['decision', 'Decisions'],
  ['constraint', 'Constraints'],
  ['question', 'Open questions'],
  ['agreement', 'Agreements (who: what [due date])'],
  ['fact', 'Facts'],
  ['artifact', 'Artifacts'],
  ['answer', 'Answers (pointers; the full text is in the message)'],
];

export function formatProtocolMemory(p: ChatProtocol, title: string): string {
  const active = p.entries.filter((e) => e.status === 'active');
  if (active.length === 0) return '';

  const groups: string[] = [];
  for (const [section, heading] of SECTION_ORDER) {
    const items = active
      .filter((e) => e.section === section)
      .sort((a, b) => a.no - b.no)
      // Quotes are intentionally never included: they are for checking in the Protocol pane only
      .map((e) => `- #${e.no} (msg ${e.msg}): ${e.text.trim()}`);
    if (items.length > 0) groups.push(`${heading}:\n${items.join('\n')}`);
  }
  if (groups.length === 0) return '';

  const seq = p.state.seq ?? '?';
  const head = [`[Protocol of this chat (kept by the secretary) up to message #${seq}]`];
  const t = title.trim();
  if (t) head.push(`Chat title: ${t}`);

  return [head.join('\n'), ...groups, '[End of protocol]'].join('\n\n');
}

// The secretary also advances its cursor for chats it does not keep (GPT chats, skipped agents) without entries.
export function protocolUsable(p: ChatProtocol | null): p is ChatProtocol {
  if (p === null || p.state.seq === null || p.state.seq <= 0 || p.entries.length === 0) return false;
  const err = p.state.error || '';
  return !err.startsWith('GPT chats') && !err.startsWith('skipped');
}

export interface MemoryPack {
  text: string;
  usedProtocol: boolean;
}

type Message = ChatEvent & { type: 'user_message' | 'assistant_message' };

function isMessage(e: ChatEvent): e is Message {
  return e.type === 'user_message' || e.type === 'assistant_message';
}

function formatItem(m: Message): string {
  if (m.type === 'user_message') {
    let s = `#${m.sequence} User:\n${m.text ?? ''}`;
    const names = (m.attachments ?? []).map((a) => a.displayName).filter((n) => n.trim() !== '');
    if (names.length > 0) s += `\n(attachments: ${names.join(', ')})`;
    return s;
  }
  const body = stripTitleMarker(m.text ?? '') || m.error || '';
  return `#${m.sequence} Agent (${m.agent || 'agent'}):\n${body}`;
}

// Keeps the newest whole items within maxChars; the newest item is always kept (truncated if needed).
function fitTranscript(messages: Message[], maxChars: number): string {
  const items = messages.map(formatItem);
  if (items.length === 0) return '';

  const sep = 2;
  const budget = Math.max(0, maxChars);
  const kept: string[] = [];
  let used = 0;

  for (let k = items.length - 1; k >= 0; k--) {
    const item = items[k];
    const cost = item.length + (kept.length > 0 ? sep : 0);
    if (used + cost > budget) break;
    kept.unshift(item);
    used += cost;
  }

  if (kept.length === 0) {
    const newest = items[items.length - 1];
    const room = Math.max(0, budget - TRUNCATED_MARKER.length - 1);
    kept.push(`${newest.slice(0, room)}\n${TRUNCATED_MARKER}`);
  }

  if (kept.length < items.length) kept.unshift(OMITTED_MARKER);
  return kept.join('\n\n');
}

export function buildMemory(
  events: ChatEvent[],
  p: ChatProtocol | null,
  opts: {
    title: string;
    chatId: string;
    minTail: number;
    maxChars: number;
    retrievalHint: boolean;
    parentProtocol?: string;
  },
): MemoryPack {
  // Event order is kept: inherited parent messages and this chat's messages both start at #1.
  const messages = events.filter(isMessage);
  const parent = (opts.parentProtocol ?? '').trim();

  if (messages.length === 0) return { text: parent, usedProtocol: false };

  const sections: string[] = ['[5. Memory]'];
  if (parent) sections.push(parent);

  if (protocolUsable(p)) {
    const seq = p.state.seq as number;
    let tail = messages.filter((m) => m.sequence > seq);
    const minTail = Math.min(Math.max(0, opts.minTail), messages.length);
    if (tail.length < minTail) tail = messages.slice(messages.length - minTail);

    const protocol = formatProtocolMemory(p, opts.title);
    if (protocol) sections.push(protocol);
    sections.push('[Messages after the protocol, verbatim]');
    const transcript = fitTranscript(tail, opts.maxChars);
    if (transcript) sections.push(transcript);
    sections.push('[End of messages]');

    if (opts.retrievalHint) {
      sections.push(
        `Older messages are referenced by #number. To read one: python "${drivePath('AI-Workbench/tools/aiwb-chat/aiwb-chat.py')}" get ${opts.chatId} <seq> (search: ... search ${opts.chatId} <words>). Read the source message before relying on exact numbers or wording.`,
      );
    }
    return { text: sections.join('\n\n'), usedProtocol: true };
  }

  sections.push('[CO context: earlier conversation in this chat; you are starting a new session]');
  const transcript = fitTranscript(messages, opts.maxChars);
  if (transcript) sections.push(transcript);
  sections.push('[End of CO context]');

  return { text: sections.join('\n\n'), usedProtocol: false };
}
