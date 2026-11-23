// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/appStack.selftest.ts - live and recent apps of the shell (F14e.1).
import { appKey, exitTarget, findApp, findPage, liveLimit, mountOrder, parseRecent, pruneApps, touchApp, type AppRef } from './appStack.ts';
import { parseExtMessage, type ModuleInfo } from './extBridge.ts';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}

const a = (moduleId: string, env = 'env-a'): AppRef => ({ env, moduleId, pageId: 'main' });
ok('3 alive on a phone, 5 on a computer', liveLimit(true) === 3 && liveLimit(false) === 5);
let l: AppRef[] = [];
for (const id of ['a', 'b', 'c', 'd']) l = touchApp(l, a(id), 3);
ok('the oldest is unloaded', l.map((x) => x.moduleId).join() === 'd,c,b');
l = touchApp(l, a('b'), 3);
ok('reopened goes to the front, no duplicate', l.map((x) => x.moduleId).join() === 'b,d,c');
ok('same app in another environment is another app', touchApp(l, a('b', 'env-b'), 5).length === 4);
ok('prune keeps the same list when nothing goes', pruneApps(l, () => true) === l);
ok('prune drops a switched-off module', pruneApps(l, (x) => x.moduleId !== 'd').map(appKey).join() === 'env-a/b/main,env-a/c/main');
ok('recent: malformed storage', parseRecent('{') .length === 0 && parseRecent('{"a":1}').length === 0 && parseRecent(null).length === 0);
ok('recent: bad ids dropped', parseRecent(JSON.stringify([a('ok'), { env: 'env-a', moduleId: '../x', pageId: 'main' }, { env: 'r', moduleId: 'm' }])).length === 1);
const seq = new Map<string, number>();
let live: AppRef[] = [];
const order = () => mountOrder(live, seq).map((x) => x.moduleId).join();
live = touchApp(live, a('a'), 3); order();
live = touchApp(live, a('b'), 3); order();
ok('first mount order', order() === 'a,b');
live = touchApp(live, a('a'), 3);
ok('switching back does not move a frame', order() === 'a,b');
live = touchApp(live, a('c'), 3); live = touchApp(live, a('d'), 3);
ok('unloaded oldest leaves, new one goes to the end', order() === 'a,c,d' && !seq.has('env-a/b/main'));
live = touchApp(live, a('b'), 3);
ok('reopened after unload is mounted at the end', order() === 'c,d,b');
ok('exit: back in history when opened from the shell', exitTarget(true, '/e/env-a/p/x', 'env-a') === -1);
ok('exit: last shell page when opened by link', exitTarget(false, '/e/env-a/p/x/c/y', 'env-a') === '/e/env-a/p/x/c/y');
ok('exit: home of the environment otherwise', exitTarget(false, null, 'env-b') === '/e/env-b');

const mk = (id: string, enabled: boolean, app: boolean): ModuleInfo => ({
  id, enabled, version: '1', title: id, description: '', permissions: [],
  ui: [{ slot: 'ui.page', id: 'main', entry: 'ui/m.html', title: id, app }],
} as unknown as ModuleInfo);
const mods = [mk('home', true, true), mk('off', false, true), mk('plain', true, false)];
ok('find an app', findApp(mods, a('home'))?.m.id === 'home');
ok('no app of a switched-off module', findApp(mods, a('off')) === null);
ok('a page without app: true is not an app', findApp(mods, a('plain')) === null);
ok('but it is a page the shell shows', findPage(mods, a('plain'))?.m.id === 'plain' && findPage(mods, a('off')) === null);

ok('ext/1 key: Esc only', parseExtMessage({ ext: 1, t: 'key', key: 'Escape' })?.t === 'key' && parseExtMessage({ ext: 1, t: 'key', key: 'Enter' }) === null);

if (fail) throw new Error(`appStack selftest: ${fail} failed`);
