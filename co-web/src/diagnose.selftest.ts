// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.77.0: self-check of the failure diagnosis against the cases shared with the server (run: npm test, or
// node src/diagnose.selftest.ts). The server port is checked on the same file by co-agent/tests/failures-test.php.
import { diagnose } from './diagnose.ts';
import data from './diagnose.cases.json' with { type: 'json' };
import { setCorporate } from './corporate.ts';

declare const process: { exitCode?: number };
interface Case { name: string; agent: string; error: string; text?: string; kind: string }
const failures: string[] = [];
for (const c of (data as { cases: Case[] }).cases) {
  const d = diagnose({ error: c.error, text: c.text ?? '', agent: c.agent, status: 'failed', jobId: 'JOB-TEST' });
  if (d?.kind !== c.kind) failures.push(`${c.name}: ${d?.kind ?? 'null'} (want ${c.kind})`);
}
// Freedom F1 5b: the corporate rule takes the connector's messages and the dispatcher's CLI family from its manifest;
// the result field corporateBlocked and the legacy wire engieBlocked both count.
const corpKind = (i: Parameters<typeof diagnose>[0]) => diagnose({ status: 'failed', ...i })?.kind ?? 'null';
setCorporate({ title: 'Acme', errorPattern: 'Acme gateway failed|acme\\.ai', agents: [{ id: 'corpbot', family: 'claude' }] });
const msg = 'Acme gateway failed: no session';
const disp = 'corpbot';
const corpChecks: Array<[string, string, string]> = [
  ['manifest message', corpKind({ error: msg, agent: 'claude' }), 'corporate'],
  ['legacy engieBlocked', corpKind({ error: '', agent: 'claude', engieBlocked: 'signin' }), 'corporate'],
  ['corporateBlocked', corpKind({ error: '', agent: 'claude', corporateBlocked: 'off' }), 'corporate'],
  ['dispatcher is claude', corpKind({ error: 'hit your weekly limit', agent: disp }), 'claude_limit'],
];
setCorporate(null);
corpChecks.push(['no manifest: no connector message', corpKind({ error: msg, agent: 'claude' }), 'fallback']);
corpChecks.push(['no manifest: core message', corpKind({ error: 'sign_in_needed', agent: 'claude' }), 'corporate']);
for (const [name, got, want] of corpChecks) if (got !== want) failures.push(`${name}: ${got} (want ${want})`);
if (failures.length) {
  console.error('diagnose selftest FAILED:\n  ' + failures.join('\n  '));
  process.exitCode = 1;
} else {
  console.log(`diagnose selftest: ${(data as { cases: Case[] }).cases.length} cases OK`);
}
