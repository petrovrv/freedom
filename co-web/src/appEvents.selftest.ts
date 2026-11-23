// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/appEvents.selftest.ts - refresh of app pages after an agent's action (F14b-1).
import { takeAppEvents } from './appEvents.ts';

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

if (fail) throw new Error(`appEvents selftest: ${fail} failed`);
console.log('appEvents selftest OK');
