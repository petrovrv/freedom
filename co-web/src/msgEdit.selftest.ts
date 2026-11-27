// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.134.0: self-check of the message delete / rewrite helpers (run: npm test, or node src/msgEdit.selftest.ts).
import { canHide, canRewrite, maxSeq, restorable, sessionsSince, splitHidden } from './msgEdit.ts';
import type { ChatEvent } from './types.ts';

declare const process: { exitCode?: number };
const failures: string[] = [];
const equal = (a: unknown, b: unknown, what: string) => { if (JSON.stringify(a) !== JSON.stringify(b)) failures.push(what + ': ' + JSON.stringify(a)); };

const ev = (sequence: number, type: string, author: string, jobId: string, deleted?: object): ChatEvent =>
  ({ schemaVersion: 1, eventId: `E${sequence}`, sequence, type, author, jobId, createdUtc: '2026-11-26T10:00:00.000Z', chatId: 'C', branchId: 'B',
    ...(deleted ? { deleted } : {}) }) as unknown as ChatEvent;
const del = (batch: string, utc: string, mode = 'pair') => ({ utc, by: 'a@x', batch, mode, at: 3 });

const all = [ev(1, 'user_message', 'a@x', 'J1'), ev(2, 'assistant_message', '', 'J1'), ev(3, 'user_message', 'b@x', 'J2', del('DEL-1', '2026-11-26T11:00:00.000Z')),
  ev(4, 'assistant_message', '', 'J2', del('DEL-1', '2026-11-26T11:00:00.000Z')), ev(5, 'user_message', 'b@x', 'J3'), ev(6, 'assistant_message', '', 'J3')];
const { visible, hidden } = splitHidden(all);
equal(visible.map((e) => e.sequence), [1, 2, 5, 6], 'visible');
equal(hidden.map((e) => e.sequence), [3, 4], 'hidden');
equal(maxSeq([...visible, ...hidden]), 6, 'max seq counts hidden ones');
equal(sessionsSince(undefined, hidden), '2026-11-26T11:00:00.000Z', 'sessions after the deletion');
equal(sessionsSince('2026-11-27T00:00:00.000Z', hidden), '2026-11-27T00:00:00.000Z', 'a later move wins');
equal(sessionsSince(undefined, []), undefined, 'nothing hidden');
equal(restorable(hidden, Date.parse('2026-11-27T00:00:00.000Z')).map((b) => [b.batch, b.count, b.messages]), [['DEL-1', 2, 2]], 'one batch');
equal(restorable(hidden, Date.parse('2027-01-27T00:00:00.000Z')), [], 'expired after 30 days');
equal(canHide(visible[0], visible, 'a@x', false), true, 'own message');
equal(canHide(visible[2], visible, 'a@x', false), false, 'another person\'s message');
equal(canHide(visible[3], visible, 'b@x', false), true, 'answer to own message');
equal(canHide(visible[3], visible, 'a@x', true), true, 'owner any');
equal(canRewrite(visible[0], visible, 'a@x', false), false, 'rewrite over another person\'s later message');
equal(canRewrite(visible[2], visible, 'b@x', false), true, 'rewrite own last turn');
equal(canRewrite(visible[1], visible, 'a@x', true), false, 'rewrite only from a user message');

if (failures.length) {
  console.error('msgEdit selftest FAILED:\n' + failures.join('\n'));
  process.exitCode = 1;
} else console.log('msgEdit selftest ok');
