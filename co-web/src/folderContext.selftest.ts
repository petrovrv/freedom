// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/folderContext.selftest.ts - the context switch of folders (F15.2 part 4): levels above a chat, automatic
// near/far rule, off, the lineage parent, loops, the key of a session, the block and the project memory summary.
import { buildFolderBlock, folderCtxOf, folderKey, folderLevels, levelsToSend, memoryDigest, projectCtxOf, protocolLines, MAX_LEVELS, TOTAL_CHARS } from './folderContext.ts';
import type { Chat } from './types';
import type { ProtocolEntry } from './dbapi';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}

const chat = (chatId: string, extra: Partial<Chat> = {}): Chat =>
  ({ chatId, title: 'T ' + chatId, folderId: 'F' + chatId, projectId: 'alpha', createdUtc: '2026-01-01T00:00:00Z', updatedUtc: '2026-01-01T00:00:00Z', ...extra }) as unknown as Chat;
const sub = (parent: string) => ({ parent: { chatId: parent, kind: 'subtask' as const, seq: 1 } }) as Partial<Chat>;

// a ← b ← c ← d (lineage); e moved under c (folder), f moved to the top
const chats = [
  chat('a'),
  chat('b', sub('a')),
  chat('c', sub('b')),
  chat('d', sub('c')),
  chat('e', { folder: 'c' }),
  chat('f', { ...sub('d'), folder: '' }),
];
const by = (id: string) => chats.find((c) => c.chatId === id)!;

const lv = folderLevels(by('d'), chats);
ok('levels: nearest first by place', lv.map((l) => l.chat.chatId).join() === 'c,b,a');
ok('levels: automatic = nearest full, farther summary', lv.map((l) => l.mode).join() === 'full,summary,summary');
ok('levels: the lineage parent is marked and not sent', lv[0].lineage && levelsToSend(lv).map((l) => l.chat.chatId).join() === 'b,a');
const le = folderLevels(by('e'), chats);
ok('moved chat: its folder counts, sent in full', le[0].chat.chatId === 'c' && le[0].mode === 'full' && !le[0].lineage);
ok('moved to the top: no folders', folderLevels(by('f'), chats).length === 0);

const withSet = chats.map((c) => (c.chatId === 'c' ? { ...c, folderContext: 'off' as const } : c.chatId === 'a' ? { ...c, folderContext: 'full' as const } : c));
const ls = folderLevels(withSet.find((c) => c.chatId === 'e')!, withSet);
ok('switch: off and full override automatic', ls.map((l) => `${l.chat.chatId}:${l.mode}`).join() === 'c:off,b:summary,a:full');
ok('switch: off is not sent', levelsToSend(ls).map((l) => l.chat.chatId).join() === 'b,a');
ok('switch: unknown value reads as automatic', folderCtxOf({ folderContext: 'x' as never }) === 'auto' && projectCtxOf({ folderContext: 'x' as never }) === 'full');

const loop = [chat('x', { folder: 'y' }), chat('y', { folder: 'x' })];
ok('levels: a loop stops', folderLevels(loop[0], loop).map((l) => l.chat.chatId).join() === 'y');
const deep = Array.from({ length: 12 }, (_, i) => chat('n' + i, i ? { folder: 'n' + (i - 1) } : {}));
ok('levels: at most MAX_LEVELS', folderLevels(deep[11], deep).length === MAX_LEVELS);
ok('levels: a missing folder stops', folderLevels(chat('z', { folder: 'gone' }), chats).length === 0);

ok('key: empty with nothing above and the full project', folderKey([], 'full') === '' && folderKey(lv.slice(0, 1), 'full') === '');
ok('key: changes with a switch or a move', folderKey(le, 'full') !== folderKey(ls, 'full') && folderKey([], 'summary') === 'p:summary');

const entry = (no: number, section: ProtocolEntry['section'], text: string, status: ProtocolEntry['status'] = 'active') =>
  ({ no, section, text, status, quote: null, msg: 1, replacedBy: null, closedSeq: null, author: 'a', model: null, createdUtc: '' }) as ProtocolEntry;
const proto = [entry(1, 'decision', 'Use clay pots'), entry(2, 'fact', 'Soil is acid'), entry(3, 'question', 'Which seeds?'), entry(4, 'decision', 'Old', 'superseded'), entry(5, 'answer', 'see #3')];
ok('protocol: summary keeps decisions, constraints, questions', protocolLines(proto, 'summary') === 'Decisions:\n- Use clay pots\nOpen questions:\n- Which seeds?');
ok('protocol: full keeps facts, drops answers and replaced', /Soil is acid/.test(protocolLines(proto, 'full')) && !/see #3|Old/.test(protocolLines(proto, 'full')));

const block = buildFolderBlock([
  { level: { ...le[0] }, protocol: proto, messages: ['#1 User: hi', '#2 Claude: hello'] },
  { level: { ...le[1], chat: { ...le[1].chat, summary: 'About beds' } }, protocol: proto },
]);
ok('block: frame, nearest first, messages only in full', block.startsWith('[Folders above this chat') && block.indexOf('T c') < block.indexOf('T b') && /Newest messages:\n#1 User: hi/.test(block) && (block.match(/Newest messages/g) || []).length === 1);
ok('block: summary of a far folder', /Summary: About beds/.test(block) && /2 folders up, summary/.test(block));
ok('block: off and lineage give nothing', buildFolderBlock([{ level: lv[0] }, { level: { ...ls[0] }, protocol: proto }]) === '');
const big = buildFolderBlock(Array.from({ length: 6 }, (_, i) => ({ level: { ...le[0], depth: i + 1, chat: chat('k' + i) }, protocol: proto, messages: ['x'.repeat(3900)] })));
ok('block: the total budget holds', big.length < TOTAL_CHARS + 2000 && /left out \(context budget\)/.test(big));

const md = ['# Memory', '- short one', '- ' + 'y'.repeat(300), 'not an entry', ...Array.from({ length: 45 }, (_, i) => `- e${i}`)].join('\n');
const dg = memoryDigest(md);
ok('memory summary: entries only, cut, newest 40', !/short one|not an entry/.test(dg) && /- e44/.test(dg) && /newest 40 of 47/.test(dg) && memoryDigest('') === '');

if (fail) throw new Error(`folderContext selftest: ${fail} failed`);
console.log('folderContext selftest OK');
