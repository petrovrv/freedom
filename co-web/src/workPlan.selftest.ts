// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.76.0: self-check of work plans (run: npm test, or node src/workPlan.selftest.ts).
import { brokenRun, planApplies, planHeuristic, planPath, resumeBlock, resumeUnreadBlock } from './workPlan.ts';
import type { ChatEvent } from './types.ts';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

let seq = 0;
const turn = (jobId: string, utc: string, status: string, error = ''): ChatEvent[] => [
  { type: 'user_message', jobId, sequence: ++seq, createdUtc: utc, text: 'do it' } as ChatEvent,
  { type: 'assistant_message', jobId, sequence: ++seq, createdUtc: utc, status, error } as ChatEvent,
];

const ok = turn('J1', '2026-11-01T20:00:00Z', 'completed');
assert.equal(brokenRun(ok, 'C'), null, 'completed: no run');

const failed = [...ok, ...turn('J2', '2026-11-01T22:00:00Z', 'failed', 'The pipe\n has been ended.'), ...turn('J3', '2026-11-01T22:05:00Z', 'cancelled')];
const run = brokenRun(failed, 'C')!;
assert.ok(run, 'broken run found');
assert.equal(run.jobId, 'J3', 'last broken job');
assert.equal(run.sinceUtc, '2026-11-01T22:00:00Z', 'since = first broken start');
assert.equal(brokenRun([...failed, ...turn('J4', '2026-11-01T22:10:00Z', 'completed')], 'C'), null, 'completed after: no run');
const other = failed.map((e) => ({ ...e, chatId: 'PARENT' }) as ChatEvent);
assert.equal(brokenRun(other, 'C'), null, 'inherited parent messages ignored');

const plan = 'Status: active\nGoal: x\n- [x] one - ok\n- [ ] two\n';
assert.ok(planApplies(plan, '2026-11-01T22:03:00Z', run), 'fresh active plan applies');
assert.ok(!planApplies(plan, '2026-11-01T19:00:00Z', run), 'stale plan ignored');
assert.ok(!planApplies(plan.replace('active', 'done'), '2026-11-01T22:03:00Z', run), 'done plan ignored');
assert.ok(!planApplies('Status: active\n- [x] one\n', '2026-11-01T22:03:00Z', run), 'all ticked ignored');

const block = resumeBlock(plan, run, planPath('P', 'C'));
assert.ok(block.includes('J3') && block.includes('was stopped'), 'block names job and status');
assert.ok(block.includes('- [ ] two') && block.endsWith('\n\n'), 'block carries plan');
assert.ok(planHeuristic(planPath('P', 'C')).includes('scratch\\.plan\\plan.md'), 'heuristic path');
assert.ok(planHeuristic('x').length < 450, 'heuristic stays short');

const unread = resumeUnreadBlock({ sinceUtc: '', jobId: 'J9', sequence: 9, status: 'failed', error: '' }, 'P:/plan.md');
assert.ok(unread.includes('J9') && unread.includes('P:/plan.md') && unread.endsWith('\n\n'), 'unread block');

if (failures.length) {
  console.error('workPlan selftest FAILED:\n' + failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log('workPlan selftest ok');
}
