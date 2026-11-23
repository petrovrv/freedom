// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/appEvents.selftest.ts - refresh of app pages after an agent's action (F14b-1).
import { pageWantsRefresh, takeAppEvents, trayWantsRefresh } from './appEvents.ts';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}

const ev = (seq: number, app: string, action = 'home_state') => ({ seq, app, action });
let r = takeAppEvents(-1, { seq: 7, events: [ev(7, 'smart-home')] });
ok('first answer only sets the cursor', r.cursor === 7 && r.refresh.length === 0);
r = takeAppEvents(7, { seq: 7, events: [] });
ok('nothing new', r.cursor === 7 && r.refresh.length === 0);
r = takeAppEvents(7, { seq: 10, events: [ev(7, 'old'), ev(8, 'smart-home'), ev(9, 'smart-home', 'home_control'), ev(10, 'calendar', 'cal_list')] });
ok('new events grouped by app', r.cursor === 10 && JSON.stringify(r.refresh) === JSON.stringify([
  { module: 'smart-home', actions: ['home_state', 'home_control'] }, { module: 'calendar', actions: ['cal_list'] }]));
r = takeAppEvents(10, { seq: 2, events: [ev(2, 'smart-home')] });
ok('a reset record restarts the cursor without refresh', r.cursor === 2 && r.refresh.length === 0);
r = takeAppEvents(3, { seq: 4, events: [ev(4, '../x'), { seq: 4, app: 'ok-app', action: 'BAD OP' }, null] });
ok('bad app ids dropped, bad action names ignored', JSON.stringify(r.refresh) === JSON.stringify([{ module: 'ok-app', actions: [] }]));
ok('garbage keeps the cursor', takeAppEvents(5, null).cursor === 5 && takeAppEvents(5, { seq: 'x' }).refresh.length === 0);

// F14b-2: the tray of a module with actions keeps the poll on without an open app
const mod = (enabled: boolean, actions: unknown, slots: string[]) => ({ enabled, actions, ui: slots.map((slot) => ({ slot })) });
ok('tray of a module with actions listens', trayWantsRefresh([mod(true, ['home_state'], ['ui.page', 'ui.header'])]));
ok('no tray, no actions or switched off: no poll', !trayWantsRefresh([mod(true, ['home_state'], ['ui.page']), mod(true, [], ['ui.header']),
  mod(false, ['x'], ['ui.header']), mod(true, undefined, ['ui.header'])]));

// F14e.2: the bare page of an app (a desktop window) polls itself when its module has actions
ok('bare page of a module with actions listens', pageWantsRefresh({ enabled: true, actions: ['home_state'] }));
ok('bare page without actions or switched off: no poll', !pageWantsRefresh({ enabled: true, actions: [] })
  && !pageWantsRefresh({ enabled: false, actions: ['x'] }) && !pageWantsRefresh({ enabled: true }) && !pageWantsRefresh(null));

if (fail) throw new Error(`appEvents selftest: ${fail} failed`);
console.log('appEvents selftest OK');
