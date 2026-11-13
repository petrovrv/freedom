// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/gateway.selftest.ts - the gateway description from db.php modules_list (Freedom F10) and its legacy wire names.
import {
  confidentialPatch, gateway, gatewayBlocked, gatewayBlockedFields, gatewayVersion, gatewayErrorRe, gatewayState, gatewayTokens, gatewayTools, isGatewayAgent,
  isGatewayFailureKind, isGatewayKind, onGatewayChange, pausedAgents, projectConfidential, setGateway, setGateways,
} from './gateway.ts';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}

// neutral before modules_list answers
ok('neutral: no module', gateway().module === '' && gateway().agents.length === 0 && !gateway().paused);
ok('neutral: generic title', gateway().title === 'Employer' && gateway().portalTitle === 'the employer portal');
ok('neutral: no ops', !gateway().ops.resume && !gateway().ops.waiting);
ok('neutral: core error pattern', gatewayErrorRe().test('sign_in_needed') && !gatewayErrorRe().test('Employer call failed'));
ok('legacy Bridge error text', gatewayErrorRe().test('Corporate call failed: no gateway tool'));
const v0 = gatewayVersion();

let changes = 0;
const off = onGatewayChange(() => changes++);
const desc = {
  module: 'acme-gw', enabled: true, paused: false, title: 'Acme', employerTitle: 'Acme (Employer)', portalTitle: 'acme.ai',
  signinTitle: 'Acme Verify', domains: ['acme.example'], errorPattern: 'Acme gateway failed|(', usageSource: 'acme-savings',
  resumeText: 'Back.', blockedMarkers: ['[[gw-blocked]]'],
  agents: [
    { id: 'acmebot', kind: 'gateway-llm', title: 'Acme (Employer)' },
    { id: 'disp', kind: 'gateway-dispatcher', title: 'Dispatcher (Acme)', family: 'claude', needsDevice: true, job: true },
  ],
  tools: [{ name: 'acme_chat', kind: 'gateway-llm' }, { name: 'acme_fetch', kind: 'fetch' }, { name: 'acme_mail', kind: '' }, { bad: 1 }],
  ops: { resume: 'gw_resume', waiting: 'gw_waiting', bogus: 'x', evil: 'DROP TABLE' },
};
setGateways([desc, { module: 'second', title: 'Second' }]);
ok('first gateway counts', gateway().module === 'acme-gw' && gateway().title === 'Acme');
ok('listener ran once', changes === 1);
ok('version grows on change', gatewayVersion() === v0 + 1);
setGateways([desc]);
ok('same description: no listener call', changes === 1);
ok('bad errorPattern dropped', gateway().errorPattern === '');
ok('title call failed matches', gatewayErrorRe().test('Acme call failed: 502'));
ok('agents', isGatewayAgent('acmebot') && isGatewayAgent('disp') && !isGatewayAgent('claude'));
ok('dispatcher kind and family', gateway().agents[1].kind === 'gateway-dispatcher' && gateway().agents[1].family === 'claude');
ok('tools normalized', gatewayTools('gateway-llm').join() === 'acme_chat' && gatewayTools('gateway-fetch').join() === 'acme_fetch' && gatewayTools().length === 3);
ok('ops kept, unknown dropped', gateway().ops.resume === 'gw_resume' && gateway().ops.waiting === 'gw_waiting' && Object.keys(gateway().ops).length === 2);
ok('enabled: nothing paused', pausedAgents().size === 0);

setGateways([{ ...desc, enabled: false, paused: true }]);
ok('disabled = paused', gateway().paused && !gateway().enabled);
ok('paused agents', [...pausedAgents()].sort().join() === 'acmebot,disp');
ok('listener ran on change', changes === 2);

setGateways([]);
ok('empty list: neutral again', gateway().module === '' && pausedAgents().size === 0);
setGateways(undefined);
ok('missing field: neutral', gateway().module === '');
off();
setGateway(desc);
ok('unsubscribed listener not called', changes === 3);

// legacy wire names
ok('blocked: new name first', gatewayBlocked({ gatewayBlocked: 'off', corporateBlocked: 'signin' }) === 'off');
ok('blocked: legacy names', gatewayBlocked({ corporateBlocked: ' signin ' }) === 'signin' && gatewayBlocked({ engieBlocked: 'x' }) === 'x');
ok('blocked fields: all names written', JSON.stringify(gatewayBlockedFields('off')) === '{"gatewayBlocked":"off","corporateBlocked":"off","engieBlocked":"off"}' && Object.keys(gatewayBlockedFields('')).length === 0);
ok('blocked: none', gatewayBlocked({}) === '' && gatewayBlocked(null) === '');
ok('state: new field', gatewayState({ gateway: { state: 'online' }, corp: { state: 'off' } }) === 'online');
ok('state: legacy fields', gatewayState({ corp: { state: 'signin' } }) === 'signin' && gatewayState({ engie: 'offline' }) === 'offline');
ok('state: unknown value', gatewayState({ gateway: { state: 'weird' } }) === null);
ok('project flag: new name wins', projectConfidential({ confidential: false, corporate: true }) === false);
ok('project flag: legacy name', projectConfidential({ corporate: true }) && !projectConfidential({}));
ok('failure kinds', isGatewayFailureKind('gateway') && isGatewayFailureKind('engie') && isGatewayFailureKind('corporate') && !isGatewayFailureKind('timeout'));
ok('tokens: new name first', gatewayTokens({ gatewayTokens: 5, corpTokens: 7 }) === 5);
ok('tokens: legacy names', gatewayTokens({ corpTokens: 7 }) === 7 && gatewayTokens({ engieTokens: 0 }) === 0 && gatewayTokens({}) === null);
ok('flag patch drops the legacy name', JSON.stringify({ corporate: true, ...confidentialPatch(false) }) === '{"confidential":false}');
ok('model kinds', isGatewayKind('gateway') && isGatewayKind('corporate') && !isGatewayKind('site'));

if (fail) throw new Error(`gateway selftest: ${fail} failed`);
console.log('gateway selftest OK');
