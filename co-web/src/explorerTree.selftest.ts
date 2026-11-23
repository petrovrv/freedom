// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/explorerTree.selftest.ts - the Explorer view: folders, path bar, Ctrl+K jump (F15.1).
import { childPath, crumbs, shortDate, explorerItems, itemTitle, moveFocus, parentPath, pathOfChat, quickJump, ROOT_PATH, samePath, validPath, type ExplorerPath } from './explorerTree.ts';
import type { Chat, Project } from './types';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}

const proj = (projectId: string, title: string, extra: Partial<Project> = {}): Project =>
  ({ projectId, title, folderId: 'F' + projectId, environmentId: 'env-a', archived: false, createdUtc: '2026-01-01T00:00:00Z', updatedUtc: '2026-01-01T00:00:00Z', ...extra }) as Project;
const chat = (chatId: string, title: string, updated: string, parent?: string, extra: Partial<Chat> = {}): Chat =>
  ({ chatId, title, folderId: 'F' + chatId, projectId: 'alpha', createdUtc: updated, updatedUtc: updated, ...(parent ? { parent: { chatId: parent, kind: 'subtask' } } : {}), ...extra }) as unknown as Chat;

const projects = [
  proj('alpha', 'Alpha garden', { updatedUtc: '2026-03-01T00:00:00Z' }),
  proj('beta', 'Beta notes', { updatedUtc: '2026-04-01T00:00:00Z' }),
  proj('old', 'Old stuff', { archived: true }),
  proj('fam', 'Family room', { family: true }),
];
const chats = [
  chat('c1', 'Plan the beds', '2026-03-01T00:00:00Z'),
  chat('c2', '⏰ Watering reminder', '2026-03-05T00:00:00Z'),
  chat('c3', '⤷ Seeds order', '2026-03-02T00:00:00Z', 'c1'),
  chat('c4', 'Soil test', '2026-03-10T00:00:00Z', 'c3'),
  chat('c5', 'Old idea', '2026-02-01T00:00:00Z', undefined, { archived: true }),
  chat('x1', 'Loop a', '2026-01-01T00:00:00Z', 'x2'),
  chat('x2', 'Loop b', '2026-01-02T00:00:00Z', 'x1'),
];

// root: projects only, no archived, no family; activity sort uses the given freshness
const root = explorerItems(ROOT_PATH, projects, null, { sort: 'activity' });
ok('root lists live projects', root.map((i) => i.key).join() === 'p:beta,p:alpha');
ok('root A-Z', explorerItems(ROOT_PATH, projects, null, { sort: 'title' }).map((i) => i.key).join() === 'p:alpha,p:beta');
ok('root filter', explorerItems(ROOT_PATH, projects, null, { sort: 'title', filter: 'NOTES' }).map((i) => i.key).join() === 'p:beta');

// project folder: top chats; c1 is a folder because of c3, its subtree freshness (c4, 03-10) lifts it first
const top = explorerItems({ projectId: 'alpha', chatIds: [] }, projects, chats, { sort: 'activity' });
ok('project lists top chats, no archived', top.map((i) => i.key).join() === 'c:c1,c:c2,c:x2,c:x1');
const c1 = top[0];
ok('folder flag and subtree count', c1.kind === 'chat' && c1.folder && c1.count === 2);
ok('title without ⏰', top[1].title === 'Watering reminder');
ok('chats not loaded yet', explorerItems({ projectId: 'alpha', chatIds: [] }, projects, null, { sort: 'activity' }).length === 0);

// chat folder: the chat itself first, then its children; ⤷ stripped
const inC1: ExplorerPath = { projectId: 'alpha', chatIds: ['c1'] };
const sub = explorerItems(inC1, projects, chats, { sort: 'activity' });
ok('chat folder: self then children', sub.map((i) => i.key).join() === 's:c1,c:c3');
ok('subtask title without ⤷', sub[1].title === 'Seeds order');
ok('filter keeps self', explorerItems(inC1, projects, chats, { sort: 'activity', filter: 'zzz' }).map((i) => i.key).join() === 's:c1');

// navigation
ok('enter project', samePath(childPath(ROOT_PATH, root[1])!, { projectId: 'alpha', chatIds: [] }));
ok('enter chat folder', samePath(childPath({ projectId: 'alpha', chatIds: [] }, c1)!, inC1));
ok('leaf chat is not a folder', childPath({ projectId: 'alpha', chatIds: [] }, top[1]) === null);
ok('up from chat folder', samePath(parentPath({ projectId: 'alpha', chatIds: ['c1', 'c3'] }), inC1));
ok('up from project', samePath(parentPath({ projectId: 'alpha', chatIds: [] }), ROOT_PATH));
ok('up from root stays', samePath(parentPath(ROOT_PATH), ROOT_PATH));
ok('path of a deep chat', samePath(pathOfChat('alpha', 'c4', chats), { projectId: 'alpha', chatIds: ['c1', 'c3'] }));
ok('path of a top chat', samePath(pathOfChat('alpha', 'c2', chats), { projectId: 'alpha', chatIds: [] }));
ok('cycle: own ancestor is on top', samePath(pathOfChat('alpha', 'x1', chats), { projectId: 'alpha', chatIds: [] }));

// crumbs
const cr = crumbs({ projectId: 'alpha', chatIds: ['c1', 'c3'] }, 'Home', projects, chats);
ok('crumbs labels', cr.map((c) => c.label).join(' > ') === 'Home > Alpha garden > Plan the beds > Seeds order');
ok('crumb leads to its folder', samePath(cr[2].path, inC1) && samePath(cr[0].path, ROOT_PATH));
ok('crumbs of unknown chat', crumbs({ projectId: 'alpha', chatIds: ['zz'] }, 'Home', projects, null)[2].label === '…');

// validPath
ok('gone project -> root', samePath(validPath({ projectId: 'nope', chatIds: [] }, projects, null), ROOT_PATH));
ok('not loaded keeps path', samePath(validPath({ projectId: 'alpha', chatIds: ['c1', 'zz'] }, projects, null), { projectId: 'alpha', chatIds: ['c1', 'zz'] }));
ok('gone chat folder -> nearest above', samePath(validPath({ projectId: 'alpha', chatIds: ['c1', 'zz'] }, projects, chats), inC1));
ok('chat without children is not a folder', samePath(validPath({ projectId: 'alpha', chatIds: ['c2'] }, projects, chats), { projectId: 'alpha', chatIds: [] }));
ok('wrong chain cut', samePath(validPath({ projectId: 'alpha', chatIds: ['c3'] }, projects, chats), { projectId: 'alpha', chatIds: [] }));
ok('good path is the same object', (() => { const p = { projectId: 'alpha', chatIds: ['c1', 'c3'] }; return validPath(p, projects, chats) === p; })());

// quick jump
const chatsOf = (id: string) => (id === 'alpha' ? chats : null);
const j1 = quickJump('seed', projects, chatsOf);
ok('jump finds a subchat', j1.length === 1 && j1[0].chatId === 'c3' && j1[0].where === 'Alpha garden');
const j2 = quickJump('be', projects, chatsOf);
ok('jump: title start first, project before chat', j2[0].projectId === 'beta' && j2[0].kind === 'project' && j2.some((h) => h.chatId === 'c1'));
ok('jump skips archived and family', quickJump('old', projects, chatsOf).length === 0 && quickJump('family', projects, chatsOf).length === 0);
ok('jump other layout', quickJump('ыщшд', projects, chatsOf).some((h) => h.chatId === 'c4'));
ok('jump empty query', quickJump('  ', projects, chatsOf).length === 0);
ok('jump limit', quickJump('o', projects, chatsOf, 2).length === 2);

// keyboard
ok('grid down', moveFocus('ArrowDown', 1, 10, 4) === 5);
ok('grid clamps', moveFocus('ArrowDown', 8, 10, 4) === 9 && moveFocus('ArrowUp', 1, 10, 4) === 0);
ok('home/end', moveFocus('Home', 5, 10, 4) === 0 && moveFocus('End', 0, 10, 4) === 9);
ok('other keys', moveFocus('a', 0, 10, 4) === null && moveFocus('ArrowDown', 0, 0, 4) === null);
const now = new Date(2026, 10, 23, 15, 0);
ok('short date today is a time', /\d{1,2}.\d{2}/.test(shortDate(new Date(2026, 10, 23, 9, 5).toISOString(), now)) && !shortDate(new Date(2026, 10, 23, 9, 5).toISOString(), now).includes('2026'));
ok('short date other year has the year', shortDate(new Date(2024, 1, 2).toISOString(), now).includes('2024'));
ok('short date bad', shortDate('x', now) === '' && shortDate(null, now) === '');
ok('itemTitle empty', itemTitle('') === '(untitled)');

if (fail) throw new Error(`explorerTree selftest: ${fail} failed`);
console.log('explorerTree selftest OK');
