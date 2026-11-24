// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.33.0: chat lineage. A child chat (branch, follow-up, side question, subtask) gets a snapshot at creation:
// the inherited messages (the parent's own inherited snapshot + the parent's messages up to `seq`) and the parent's
// protocol at that point, written once to lineage-snapshot.json in the child's folder. Views and sends read only the
// snapshot, so deleting or changing the parent never breaks the child; the parent's agent session is never resumed.
import type { Chat, ChatEvent, Project, Mode } from './types';
import { dbProtocol, type ProtocolEntry } from './dbapi';
import { createChat, loadChatEvents, updateChatMeta } from './workbench';
import { createFile, getJson } from './drive';

export type ChatParentKind = 'branch' | 'followup' | 'side' | 'subtask';

export interface ChatParent {
  chatId: string;
  seq: number;
  kind: ChatParentKind;
  title?: string;
  snapshotFileId?: string;
}

export interface LineageSnapshot {
  version: 1;
  events: ChatEvent[];
  protocol: string;
  createdUtc: string;
}

export const KIND_PREFIX: Record<ChatParentKind, string> = {
  branch: '⑂ ',
  followup: '↪ ',
  side: 'btw: ',
  subtask: '⤷ ',
};

const SNAPSHOT_FILE = 'lineage-snapshot.json';

const SECTION_ORDER: ReadonlyArray<[ProtocolEntry['section'], string]> = [
  ['decision', 'Decisions'],
  ['constraint', 'Constraints'],
  ['fact', 'Facts'],
  ['question', 'Open questions'],
  ['agreement', 'Agreements'],
  ['artifact', 'Artifacts'],
  ['answer', 'Answers'],
];

function isMessage(e: ChatEvent): boolean {
  return e.type === 'user_message' || e.type === 'assistant_message';
}

export async function readSnapshot(parent: ChatParent): Promise<LineageSnapshot> {
  // A subtask (aiwb-subtask) starts from its own self-contained prompt: nothing is inherited.
  if (!parent.snapshotFileId && parent.kind === 'subtask' && parent.seq === 0) {
    return { version: 1, events: [], protocol: '', createdUtc: '' };
  }
  if (!parent.snapshotFileId) {
    throw new Error('This chat was created before lineage snapshots; branch it again from its parent.');
  }
  let data: unknown;
  try {
    data = await getJson<LineageSnapshot>(parent.snapshotFileId);
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    throw new Error(`Could not read the lineage snapshot: ${msg}`);
  }
  const snap = data as Partial<LineageSnapshot> | null;
  if (!snap || typeof snap !== 'object') {
    throw new Error('The lineage snapshot is missing or not valid JSON.');
  }
  if (snap.version !== 1) {
    throw new Error(`Unsupported lineage snapshot version: ${String(snap.version)}.`);
  }
  if (!Array.isArray(snap.events)) {
    throw new Error('The lineage snapshot is corrupted (events is not an array).');
  }
  if (typeof snap.protocol !== 'string') {
    throw new Error('The lineage snapshot is corrupted (protocol is not a string).');
  }
  return {
    version: 1,
    events: snap.events,
    protocol: snap.protocol,
    createdUtc: typeof snap.createdUtc === 'string' ? snap.createdUtc : '',
  };
}

function formatProtocol(entries: ProtocolEntry[], title: string, seq: number): string {
  const active = entries.filter(
    (e) => e.msg <= seq && (e.status === 'active' || (e.closedSeq !== null && e.closedSeq > seq)),
  );
  if (active.length === 0) return '';
  const blocks: string[] = [];
  for (const [section, heading] of SECTION_ORDER) {
    const items = active.filter((e) => e.section === section);
    if (items.length === 0) continue;
    blocks.push([`${heading}:`, ...items.map((e) => `- #${e.no}: ${e.text}`)].join('\n'));
  }
  return [
    `[Protocol of the parent chat "${title}" up to message #${seq}]`,
    blocks.join('\n\n'),
    '[End of parent protocol]',
  ].join('\n');
}

export async function buildSnapshot(parentChat: Chat, seq: number): Promise<LineageSnapshot> {
  const base: { events: ChatEvent[]; protocol: string } = parentChat.parent
    ? await readSnapshot(parentChat.parent)
    : { events: [], protocol: '' };

  const all = await loadChatEvents(parentChat);
  const ownRaw = all
    .filter((e) => isMessage(e) && e.sequence <= seq)
    .sort((a, b) => a.sequence - b.sequence);

  if (seq > 0 && !ownRaw.some((e) => e.sequence === seq)) {
    throw new Error(`The parent chat history is incomplete (message #${seq} not found); try again.`);
  }

  const own = ownRaw.map(
    (e) => ({ ...e, sessionId: null, inherited: true }) as unknown as ChatEvent,
  );

  const p = await dbProtocol(parentChat.chatId);
  if (p === null) {
    throw new Error('Could not load the parent protocol; try again.');
  }
  let block = formatProtocol(p.entries ?? [], parentChat.title, seq);
  // The secretary works asynchronously: say how far the protocol got (the inherited messages are complete anyway).
  const covered = (p as { state?: { seq?: number | null } }).state?.seq ?? null;
  if (seq > 0 && (covered === null || covered < seq)) {
    block = [block, covered === null
        ? '(The parent protocol was not processed yet; decisions are only in the inherited messages.)'
        : `(The parent protocol covers messages up to #${covered}; later decisions are only in the inherited messages.)`]
      .filter(Boolean).join('\n');
  }
  const protocol = [base.protocol, block].filter(Boolean).join('\n\n');

  return {
    version: 1,
    events: [...base.events, ...own],
    protocol,
    createdUtc: new Date().toISOString(),
  };
}

export async function createChildChat(
  project: Project,
  parentChat: Chat,
  seq: number,
  kind: ChatParentKind,
  mode: Mode,
): Promise<Chat> {
  if (parentChat.draft || !parentChat.chatId) {
    throw new Error('Save the chat (send a message) before branching it.');
  }
  const snapshot = await buildSnapshot(parentChat, seq);
  const title = (KIND_PREFIX[kind] + parentChat.title).slice(0, 80).trim();
  const chat = await createChat(project, title, mode);
  const file = await createFile(
    chat.folderId,
    SNAPSHOT_FILE,
    JSON.stringify(snapshot),
    'application/json',
  );
  const parent: ChatParent = {
    chatId: parentChat.chatId,
    seq,
    kind,
    title: parentChat.title,
    snapshotFileId: file.id,
  };
  return updateChatMeta(chat, { parent } as Partial<Chat>);
}

export function lastSequence(events: ChatEvent[]): number {
  let max = 0;
  for (const e of events) {
    if (isMessage(e) && e.sequence > max) max = e.sequence;
  }
  return max;
}
