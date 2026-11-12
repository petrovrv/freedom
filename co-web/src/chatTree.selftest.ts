// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.70.0: self-check of the chat tree and archive scope (run: npm test, or node src/chatTree.selftest.ts).
import { buildChatTree, archiveScope } from './chatTree.ts';
import type { Chat } from './types.ts';

declare const process: { exitCode?: number };

const failures: string[] = [];
let count = 0;
function eq(name: string, got: unknown, want: unknown) {
  count++;
  const g = JSON.stringify(got);
  const w = JSON.stringify(want);
  if (g !== w) failures.push(`${name}: got ${g}, want ${w}`);
}

const mk = (chatId: string, parent?: string, archived = false, createdUtc = '2026-10-01T00:00:00Z'): Chat =>
  ({ chatId, title: chatId, createdUtc, updatedUtc: createdUtc, archived, parent: parent ? { chatId: parent } : undefined }) as unknown as Chat;

// hub H > A > A1, hub H > B; X separate
const chats = [mk('H'), mk('A', 'H'), mk('A1', 'A'), mk('B', 'H'), mk('X')];
const ids = (cs: Chat[]) => cs.map((c) => c.chatId).sort();

eq('archive hub takes subtree', ids(archiveScope(chats, 'H', true)), ['A', 'A1', 'B', 'H']);
eq('archive child takes its subtree only', ids(archiveScope(chats, 'A', true)), ['A', 'A1']);
eq('archive unknown chat', archiveScope(chats, 'Z', true), []);

const arch = [mk('H', undefined, true), mk('A', 'H', true), mk('A1', 'A', true), mk('B', 'H', true), mk('X')];
eq('restore child brings archived ancestors', ids(archiveScope(arch, 'A1', false)), ['A', 'A1', 'H']);
eq('restore hub takes subtree', ids(archiveScope(arch, 'H', false)), ['A', 'A1', 'B', 'H']);
eq('restore skips visible chats', ids(archiveScope([mk('H'), mk('A', 'H', true)], 'A', false)), ['A']);
eq('first item is the chat itself', archiveScope(arch, 'A', false)[0].chatId, 'A');

const cyc = [mk('P', 'Q', true), mk('Q', 'P', true)];
eq('cycle terminates', ids(archiveScope(cyc, 'P', false)), ['P', 'Q']);

// archived list keeps the tree
const rows = buildChatTree(arch.filter((c) => c.archived), { isWorking: () => false, isOpen: () => true });
eq('archived tree rows', rows.map((r) => `${r.chat.chatId}:${r.depth}`), ['H:0', 'A:1', 'A1:2', 'B:1']);

// 0.83.0: sort modes of the roots; children stay by creation
const mk2 = (chatId: string, title: string, createdUtc: string, updatedUtc: string, parent?: string): Chat =>
  ({ chatId, title, createdUtc, updatedUtc, archived: false, parent: parent ? { chatId: parent } : undefined }) as unknown as Chat;
const S = [
  mk2('c1', 'Бета', '2026-11-01T00:00:00Z', '2026-11-05T00:00:00Z'),
  mk2('c2', '⏰ альфа', '2026-11-03T00:00:00Z', '2026-11-03T00:00:00Z'),
  mk2('c3', 'Chat 10', '2026-11-02T00:00:00Z', '2026-11-04T00:00:00Z'),
  mk2('c4', 'Chat 9', '2026-10-30T00:00:00Z', '2026-10-30T00:00:00Z'),
  mk2('k1', 'kid', '2026-11-04T00:00:00Z', '2026-11-06T00:00:00Z', 'c4'),
];
const order = (sort: 'activity' | 'created' | 'title' | 'waiting', waitRank?: (c: Chat) => number) =>
  buildChatTree(S, { isWorking: () => false, isOpen: () => false, sort, waitRank }).map((r) => r.chat.chatId);
eq('sort activity (subtree rises)', order('activity'), ['c4', 'c1', 'c3', 'c2']);
eq('sort created', order('created'), ['c2', 'c3', 'c1', 'c4']);
eq('sort title (emoji ignored, numeric)', order('title'), ['c4', 'c3', 'c2', 'c1']);
eq('sort waiting (child counts for parent)', order('waiting', (c) => (c.chatId === 'k1' ? 1 : c.chatId === 'c2' ? 2 : 0)), ['c2', 'c4', 'c1', 'c3']);
eq('sort waiting, nothing waits = activity', order('waiting'), order('activity'));

if (failures.length) {
  for (const f of failures) console.error('FAIL', f);
  process.exitCode = 1;
} else {
  console.log(`chatTree selftest: ${count} checks passed`);
}
