// SPDX-License-Identifier: AGPL-3.0-or-later
// S6 (CO Web 0.100.0): self-check of the chat's model list (run: npm test, or node src/modelPick.selftest.ts).
import type { ModelResource } from './dbapi.ts';
import type { OwnerInfo } from './types.ts';
import { currentLead, helperGroup, helperResources, leadEntries } from './modelPick.ts';

const failures: string[] = [];
const eq = (name: string, got: unknown, want: unknown) => {
  if (JSON.stringify(got) !== JSON.stringify(want)) failures.push(`${name}: got ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
};

const res = (id: string, p: Partial<ModelResource> = {}): ModelResource => ({
  id, env: 'anna', kind: 'subscription', title: id, host: id.split('/')[0], agent: id.split('/')[1] || 'claude', models: [],
  defaultModel: null, card: {}, slaveAllowed: false, confidential: false, source: 'auto', builtin: true, online: true,
  own: true, callable: true, ...p,
});
const owner = (agents: Record<string, unknown>) => ({ hostId: 'x', agents }) as unknown as OwnerInfo;
const agents = [{ id: 'claude', displayName: 'Claude' }, { id: 'codex', displayName: 'GPT' }];
const health = () => true;

const resources = [
  res('home-main/claude', { slaveAllowed: true }), res('home-main/codex'), res('site/claude', { kind: 'site' }),
  res('home-main/gwbot', { kind: 'gateway', confidential: true }),
  res('home-main/oldgw', { kind: 'corporate', confidential: true }), // legacy wire kind
  res('boris/claude-mac', { env: 'boris', host: '', own: false, slaveAllowed: true, callable: false }),
  res('anna/codex-mac', { host: '', source: 'device', builtin: false }),
];
const owners = {
  'home-main': owner({ claude: { models: [{ id: 'opus' }], defaultModel: 'opus' }, codex: {}, gwbot: {}, oldgw: {} }),
  site: owner({ claude: { displayName: 'Claude (site)' } }),
};
const lead = leadEntries({ hosts: ['home-main', 'site'], current: 'home-main', owners, agents, resources, health });
eq('lead ids', lead.map((l) => l.id), ['home-main/claude', 'home-main/codex', 'site/claude']);
eq('lead carries the resource flag', lead[0].res?.slaveAllowed, true);
eq('lead models from owner.json', [lead[0].models.length, lead[0].defaultModel], [1, 'opus']);
eq('site display name', lead[2].name, 'Claude (site)');
eq('helpers', helperResources(resources, lead).map((r) => r.id), ['home-main/gwbot', 'home-main/oldgw', 'boris/claude-mac', 'anna/codex-mac']);
eq('groups', helperResources(resources, lead).map(helperGroup), ['Confidential', 'Confidential', 'Family: boris', 'Your devices']);
eq('current: pinned site', currentLead(lead, 'claude', 'site', 'home-main')?.id, 'site/claude');
eq('current: no pin', currentLead(lead, 'claude', null, 'home-main')?.id, 'home-main/claude');
eq('current: agent only at home while on site', currentLead(lead, 'codex', null, 'site')?.id, 'home-main/codex');

// no models_list (old server, signed out): the list still comes from owner.json
const bare = leadEntries({ hosts: ['home-main', 'site'], current: 'home-main', owners, agents, resources: null, health });
eq('fallback without resources', bare.map((l) => [l.id, l.res]), [['home-main/claude', null], ['home-main/codex', null], ['site/claude', null]]);
// no owner.json at all: Claude and GPT on the current host, as before S6
const none = leadEntries({ hosts: ['home-main', 'site'], current: 'site', owners: {}, agents, resources: null, health });
eq('no owner.json', none.map((l) => l.id), ['site/claude', 'site/codex']);

if (failures.length) throw new Error(`modelPick self-test:\n${failures.join('\n')}`);
console.log('modelPick self-test: 11/11 ok');
