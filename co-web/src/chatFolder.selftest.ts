// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/chatFolder.selftest.ts - the folder of a chat (0.129.0): which parts show, the point for more content,
// Documents / Products by who added a file, agreements of the protocol, the lines of the context.
import {
  agreementOf, agreements, CHAT_PARTS, chatPartContributions, contextCount, contextItems, dueState, fileSide, onChatPartsChanged, partCounts,
  registerChatPart, splitStreams, visibleParts, type ContextInput, type StreamLike,
} from './chatFolder.ts';
import type { ProtocolEntry } from './dbapi';
import type { FolderLevel } from './folderContext';
import type { Chat } from './types';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}

// ---- parts
ok('order: subchats, context, correspondence, events, watchers, documents, products',
  CHAT_PARTS.map((p) => p.id).join() === 'subchats,context,correspondence,events,watchers,documents,products');
ok('empty chat: documents and products only', visibleParts({}).map((p) => p.id).join() === 'documents,products');
ok('parts with something show in order', visibleParts({ events: 2, subchats: 1, context: 0 }).map((p) => p.id).join() === 'subchats,events,documents,products');

// ---- the point
const ref = { projectId: 'alpha', chatId: 'c1' };
let changed = 0;
const off = onChatPartsChanged(() => changed++);
const un = registerChatPart({ part: 'correspondence', key: 'test.letters', count: (c) => (c.chatId === 'c1' ? 3 : 0), render: () => null });
ok('register notifies', changed === 1);
ok('a contribution counts for its part', partCounts({ events: 1 }, ref).correspondence === 3 && partCounts({ events: 1 }, ref).events === 1);
ok('another chat: zero', (partCounts({}, { projectId: 'alpha', chatId: 'c2' }).correspondence ?? 0) === 0);
registerChatPart({ part: 'correspondence', key: 'test.letters', count: () => 1, render: () => null });
ok('same key replaces', chatPartContributions('correspondence').length === 1 && partCounts({}, ref).correspondence === 1);
const bad = registerChatPart({ part: 'watchers', key: 'test.bad', count: () => { throw new Error('x'); }, render: () => null });
ok('a failing count counts 0', partCounts({}, ref).watchers === 0);
bad();
un(); // the replaced one is gone already: no change
ok('unregister of a replaced one does nothing', chatPartContributions('correspondence').length === 1);
const before = changed;
chatPartContributions('correspondence').forEach(() => undefined);
off();
registerChatPart({ part: 'events', key: 'test.ev', count: () => 1, render: () => null })();
ok('listener removed', changed === before);

// ---- documents / products
const st = (id: number | null, origins: Array<'agent' | 'user'>, head: string | null = null, parent?: number): StreamLike & { name: string } => ({
  name: `f${id}`,
  versions: origins.map((origin) => ({ origin })),
  row: id === null ? null : { id, head: { origin: head }, extra: parent != null ? { parent: { id: parent } } : {} },
});
const up = st(1, ['user', 'agent']);
const made = st(2, ['agent', 'user']);
const rowOnlyUser = st(3, [], 'user');
const rowOnlyAgent = st(4, [], 'agent');
const branchOfMade = st(5, [], 'user', 2);
const branchOfUp = st(6, [], 'user', 1);
const noRow = st(null, ['user']);
const loop = st(7, [], 'agent', 7);
const all = [up, made, rowOnlyUser, rowOnlyAgent, branchOfMade, branchOfUp, noRow, loop];
const sp = splitStreams(all);
ok('upload first = document, even if the agent changed it', sp.documents.includes(up));
ok('artifact first = product, even if you uploaded a version', sp.products.includes(made));
ok('no message versions: the head origin', sp.documents.includes(rowOnlyUser) && sp.products.includes(rowOnlyAgent));
ok('a branch follows the file it came from', sp.products.includes(branchOfMade) && sp.documents.includes(branchOfUp));
ok('a file without a row', sp.documents.includes(noRow));
ok('a parent loop ends', fileSide(loop, new Map([[7, loop]])) === 'products');
ok('every file in one side', sp.documents.length + sp.products.length === all.length);

// ---- agreements
const e = (no: number, text: string, status: ProtocolEntry['status'] = 'active', section: ProtocolEntry['section'] = 'agreement'): ProtocolEntry =>
  ({ no, section, text, quote: null, msg: no * 10, status, replacedBy: null, closedSeq: null, author: 'secretary', model: null, createdUtc: '' });
const a1 = agreementOf(e(1, 'Sam: send the report [due 2026-12-01]'));
ok('who, what, due', a1.who === 'Sam' && a1.what === 'send the report' && a1.due === '2026-12-01' && a1.msg === 10);
const a2 = agreementOf(e(2, 'check the logs again'));
ok('plain text: what only', a2.who === '' && a2.what === 'check the logs again' && a2.due === null);
const a3 = agreementOf(e(3, 'Агент: проверить почту [due 2026-11-28]'));
ok('unicode who', a3.who === 'Агент' && a3.due === '2026-11-28');
ok('a colon far in the text is not a who', agreementOf(e(4, `${'x'.repeat(70)}: y`)).who === '');
const ag = agreements([e(1, 'A: one [due 2026-12-05]'), e(2, 'B: two'), e(3, 'C: three [due 2026-12-01]'), e(4, 'D: done', 'closed'), e(5, 'E: old', 'superseded'), e(6, 'not one', 'active', 'decision')]);
ok('open by due, undated last', ag.open.map((a) => a.no).join() === '3,1,2');
ok('done = closed only', ag.done.map((a) => a.no).join() === '4');
ok('due states', dueState('2026-11-20', '2026-11-24') === 'overdue' && dueState('2026-11-24', '2026-11-24') === 'today'
  && dueState('2026-11-27', '2026-11-24') === 'soon' && dueState('2026-12-24', '2026-11-24') === 'later' && dueState(null, '2026-11-24') === null);

// ---- context
const chat = (chatId: string, title: string): Chat => ({ chatId, title, folderId: 'F' + chatId, projectId: 'alpha' }) as unknown as Chat;
const lvl = (c: Chat, depth: number, mode: FolderLevel['mode'], lineage = false): FolderLevel => ({ chat: c, depth, mode, set: 'auto', lineage });
const base: ContextInput = { mode: 'full', projectCtx: 'full', projectTitle: 'Garden', protocolEntries: 4, lineageTitle: null, levels: [], contextFolders: 0, shared: false, family: false };
const keys = (i: ContextInput) => contextItems(i).map((x) => `${x.key}:${x.state}`).join();
ok('full: rules, profile, instructions, memory, dossier, protocol', keys(base) === 'rules:on,profile:on,instructions:on,memory:on,dossier:on,protocol:on');
ok('project summary: instructions on, memory summary', keys({ ...base, projectCtx: 'summary' }).includes('instructions:on,memory:summary'));
ok('project off', keys({ ...base, projectCtx: 'off' }).includes('instructions:off,memory:off'));
ok('reference: profile only of the person', keys({ ...base, mode: 'reference' }) === 'rules:on,profile:on,instructions:off,memory:off,dossier:off,protocol:on');
ok('clean: no profile', keys({ ...base, mode: 'clean' }).startsWith('rules:on,profile:off'));
ok('switches: project lines in the project, mode lines here', contextItems(base).find((x) => x.key === 'memory')?.switch === 'project'
  && contextItems({ ...base, mode: 'clean' }).find((x) => x.key === 'memory')?.switch === 'mode');
const p1 = chat('p1', 'Parent');
const p2 = chat('p2', 'Grand');
const withLevels = { ...base, lineageTitle: 'Parent', levels: [lvl(p1, 1, 'full', true), lvl(p2, 2, 'summary')] };
ok('a branch: lineage instead of protocol, the lineage folder not twice', keys(withLevels) === 'rules:on,profile:on,instructions:on,memory:on,dossier:on,lineage:on,folder:p2:summary');
ok('a folder line names its chat', contextItems(withLevels).find((x) => x.key === 'folder:p2')?.chatId === 'p2');
ok('context folders: off when shared', keys({ ...base, contextFolders: 2, shared: true }).endsWith('ctxfolders:off'));
ok('family: no dossier', keys({ ...base, family: true }).includes('dossier:off'));
ok('count: an empty new chat shows no Context', contextCount({ ...base, protocolEntries: 0 }) === 0 && contextCount({ ...base, protocolEntries: null }) === 0);
ok('count: protocol, branch, folders, context folders', contextCount(withLevels) === 3 && contextCount({ ...base, contextFolders: 1 }) === 2);
ok('count: folders above do not count outside full', contextCount({ ...withLevels, mode: 'reference', protocolEntries: 0 }) === 0);

if (fail) throw new Error(`chatFolder selftest: ${fail} failed`);
console.log('chatFolder selftest OK');
