// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.77.0: self-check of the failure diagnosis against the cases shared with the server (run: npm test, or
// node src/diagnose.selftest.ts). The server port is checked on the same file by co-agent/tests/failures-test.php.
import { diagnose } from './diagnose.ts';
import data from './diagnose.cases.json' with { type: 'json' };
import { gatewayBlocked, setGateways } from './gateway.ts';

declare const process: { exitCode?: number };
interface Case { name: string; agent: string; error: string; text?: string; kind: string }
const failures: string[] = [];
for (const c of (data as { cases: Case[] }).cases) {
  const d = diagnose({ error: c.error, text: c.text ?? '', agent: c.agent, status: 'failed', jobId: 'JOB-TEST' });
  if (d?.kind !== c.kind) failures.push(`${c.name}: ${d?.kind ?? 'null'} (want ${c.kind})`);
}
// Freedom F10: the gateway rule takes the module's messages and the dispatcher's CLI family from the gateway
// description (db.php modules_list gateways); gatewayBlocked() reads the result field and its legacy wire names.
const gwKind = (i: Parameters<typeof diagnose>[0]) => diagnose({ status: 'failed', ...i })?.kind ?? 'null';
setGateways([{ module: 'acme-gw', title: 'Acme', errorPattern: 'Acme gateway failed|acme\\.ai', agents: [{ id: 'gwbot', family: 'claude', kind: 'gateway-dispatcher' }] }]);
const msg = 'Acme gateway failed: no session';
const disp = 'gwbot';
const gwChecks: Array<[string, string, string]> = [
  ['module message', gwKind({ error: msg, agent: 'claude' }), 'gateway'],
  ['title call failed', gwKind({ error: 'Acme call failed: 502', agent: 'claude' }), 'gateway'],
  ['gatewayBlocked', gwKind({ error: '', agent: 'claude', gatewayBlocked: 'off' }), 'gateway'],
  ['legacy blocked field (1)', gwKind({ error: '', agent: 'claude', gatewayBlocked: gatewayBlocked({ engieBlocked: 'signin' }) }), 'gateway'],
  ['legacy blocked field (2)', gwKind({ error: '', agent: 'claude', gatewayBlocked: gatewayBlocked({ corporateBlocked: 'off' }) }), 'gateway'],
  ['dispatcher is claude', gwKind({ error: 'hit your weekly limit', agent: disp }), 'claude_limit'],
];
setGateways([]);
gwChecks.push(['no gateway: no module message', gwKind({ error: msg, agent: 'claude' }), 'fallback']);
gwChecks.push(['no gateway: core message', gwKind({ error: 'sign_in_needed', agent: 'claude' }), 'gateway']);
for (const [name, got, want] of gwChecks) if (got !== want) failures.push(`${name}: ${got} (want ${want})`);
if (failures.length) {
  console.error('diagnose selftest FAILED:\n  ' + failures.join('\n  '));
  process.exitCode = 1;
} else {
  console.log(`diagnose selftest: ${(data as { cases: Case[] }).cases.length} cases OK`);
}
