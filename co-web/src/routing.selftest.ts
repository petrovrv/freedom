// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.29.0: self-check of routeHost (run: npm test, or node src/routing.selftest.ts).
import type { OwnerInfo } from './types.ts';
import { setGateway } from './gateway.ts';
import { autoHost, hostHealth, hostLight, hostStateText, routeHost, HOME, SITE } from './routing.ts';

export function routeSelfTest(): string[] {
  const failures: string[] = [];
  const now = Date.now();
  const ONLINE = 0;
  const OFFLINE = 2 * 60 * 60 * 1000;

  setGateway({ module: 'acme-gw', agents: [{ id: 'gwbot' }] }); // Freedom F10: gateway agents come from modules_list
  const homeAgents = { claude: {}, codex: {}, gwbot: {} };
  const siteAgents = { claude: {}, codex: {} };

  const owner = (hostId: string, ageMs: number, agents: Record<string, unknown>): OwnerInfo =>
    ({
      hostId,
      capabilities: hostId === HOME ? ['devices'] : [],
      agents,
      protocolMinor: 1,
      updatedUtc: new Date(now - ageMs).toISOString(),
    }) as unknown as OwnerInfo;

  const owners = (homeAge: number, siteAge: number): Record<string, OwnerInfo | null> => ({
    [HOME]: owner(HOME, homeAge, homeAgents),
    [SITE]: owner(SITE, siteAge, siteAgents),
  });

  const check = (
    name: string,
    actual: { hostId: string; note: string | null },
    hostId: string,
    note: string | null,
  ) => {
    if (actual.hostId !== hostId || actual.note !== note) {
      failures.push(
        `${name}: expected ${hostId} / ${JSON.stringify(note)}, got ${actual.hostId} / ${JSON.stringify(actual.note)}`,
      );
    }
  };

  // S8: Claude reaches the devices from the Site through the relay; GPT and browser devices still need Home
  check('devices from site (relay)',
    routeHost({ selected: SITE, agent: 'claude', wantsDevices: true, owners: owners(ONLINE, ONLINE) }),
    SITE, null);
  check('codex devices from site',
    routeHost({ selected: SITE, agent: 'codex', wantsDevices: true, owners: owners(ONLINE, ONLINE) }),
    HOME, 'Sent to Home: device tools');
  check('browser devices from site',
    routeHost({ selected: SITE, agent: 'claude', wantsDevices: false, webDevices: true, owners: owners(ONLINE, ONLINE) }),
    HOME, 'Sent to Home: browser devices');
  check('devices, home offline: claude failover',
    routeHost({ selected: HOME, agent: 'claude', wantsDevices: true, owners: owners(OFFLINE, ONLINE) }),
    SITE, 'Sent to Site: Home is offline');
  check('codex devices, home offline: waits',
    routeHost({ selected: HOME, agent: 'codex', wantsDevices: true, owners: { [HOME]: owner(HOME, OFFLINE, homeAgents), [SITE]: owner(SITE, ONLINE, homeAgents) } }),
    HOME, null);
  check('gwbot from site',
    routeHost({ selected: SITE, agent: 'gwbot', wantsDevices: false, owners: owners(ONLINE, ONLINE) }),
    HOME, 'Sent to Home: gwbot runs only at home');
  check('home offline, claude failover',
    routeHost({ selected: HOME, agent: 'claude', wantsDevices: false, owners: owners(OFFLINE, ONLINE) }),
    SITE, 'Sent to Site: Home is offline');
  check('home offline, gwbot waits',
    routeHost({ selected: SITE, agent: 'gwbot', wantsDevices: false, owners: owners(OFFLINE, ONLINE) }),
    HOME, 'Sent to Home: gwbot runs only at home (Home is offline — the job waits in its queue)');
  check('both online',
    routeHost({ selected: SITE, agent: 'codex', wantsDevices: false, owners: owners(ONLINE, ONLINE) }),
    SITE, null);
  check('site offline failover',
    routeHost({ selected: SITE, agent: 'codex', wantsDevices: false, owners: owners(ONLINE, OFFLINE) }),
    HOME, 'Sent to Home: Site is offline');

  // 0.44.0: Bridge 1.4.2 state in owner.json
  const fresh = new Date(now).toISOString();
  const updating = { hostId: HOME, updatedUtc: fresh, state: 'updating', updateTo: '1.4.2', runningJobs: 2 } as OwnerInfo;
  const expectEq = (name: string, actual: string, expected: string) => {
    if (actual !== expected) failures.push(`${name}: expected ${expected}, got ${actual}`);
  };
  expectEq('updating health', hostHealth(updating), 'updating');
  expectEq('updating text', hostStateText(updating, 'updating'), 'updating to 1.4.2, waiting for 2 jobs');
  expectEq('paused health', hostHealth({ hostId: HOME, updatedUtc: fresh, state: 'paused' } as OwnerInfo), 'paused');
  expectEq('old updating is stale',
    hostHealth({ ...updating, updatedUtc: new Date(now - 300 * 1000).toISOString() }), 'stale');
  // 0.79.0: an updating / paused home hands the job to an online site; device jobs still wait for home
  check('updating home: failover to site',
    routeHost({ selected: HOME, agent: 'claude', wantsDevices: false, owners: { [HOME]: { ...updating, agents: homeAgents }, [SITE]: owner(SITE, ONLINE, siteAgents) } }),
    SITE, 'Sent to Site: Home is updating');
  check('paused home: failover to site',
    routeHost({ selected: HOME, agent: 'claude', wantsDevices: false, owners: { [HOME]: { hostId: HOME, updatedUtc: fresh, state: 'paused', agents: homeAgents } as OwnerInfo, [SITE]: owner(SITE, ONLINE, siteAgents) } }),
    SITE, 'Sent to Site: Home is paused');
  check('updating home, device tools: site (relay)',
    routeHost({ selected: HOME, agent: 'claude', wantsDevices: true, owners: { [HOME]: { ...updating, agents: homeAgents }, [SITE]: owner(SITE, ONLINE, siteAgents) } }),
    SITE, 'Sent to Site: Home is updating');

  // S2: auto executor and the header traffic light
  // S8: the nearest live executor - own Bridge for CO Desktop on the Bridge PC, else the Site, Home as the fallback
  const pc = (ageMs: number): OwnerInfo => ({ ...owner(HOME, ageMs, homeAgents), machineName: 'HOME-PC',
    devices: [{ deviceId: 'd-pc', name: 'HOME-PC' }, { deviceId: 'd-lap', name: 'LAPTOP' }] }) as unknown as OwnerInfo;
  const withPc = (homeAge: number, siteAge: number) => ({ [HOME]: pc(homeAge), [SITE]: owner(SITE, siteAge, siteAgents) });
  expectEq('auto: browser/phone, both online -> home', autoHost(withPc(ONLINE, ONLINE)), HOME);
  expectEq('auto: CO Desktop on the Bridge PC -> home', autoHost(withPc(ONLINE, ONLINE), { deviceId: 'd-pc', name: 'HOME-PC' }), HOME);
  expectEq('auto: laptop without Bridge -> home', autoHost(withPc(ONLINE, ONLINE), { deviceId: 'd-lap', name: 'LAPTOP' }), HOME);
  expectEq('auto: Bridge PC, home offline -> site', autoHost(withPc(OFFLINE, ONLINE), { deviceId: 'd-pc', name: 'HOME-PC' }), SITE);
  expectEq('auto: Bridge PC, home updating -> site', autoHost({ [HOME]: { ...pc(ONLINE), state: 'updating' }, [SITE]: owner(SITE, ONLINE, siteAgents) }, { deviceId: 'd-pc' }), SITE);
  expectEq('auto: site offline -> home', autoHost(owners(ONLINE, OFFLINE)), HOME);
  expectEq('auto: both offline -> site (the server can start it)', autoHost(owners(OFFLINE, OFFLINE)), SITE);
  expectEq('auto: site never seen, home offline -> home queue', autoHost({ [HOME]: owner(HOME, OFFLINE, homeAgents) }), HOME);
  expectEq('light green', hostLight(owners(ONLINE, ONLINE)), 'green');
  expectEq('light blue (site only)', hostLight(owners(OFFLINE, ONLINE)), 'blue');
  expectEq('light green while home updates', hostLight({ [HOME]: updating, [SITE]: owner(SITE, ONLINE, siteAgents) }), 'green');
  expectEq('light red', hostLight(owners(OFFLINE, OFFLINE)), 'red');
  expectEq('light grey', hostLight({}), 'grey');

  // Freedom F12: three and more executors (a Linux server 'lnx', a family member's Mac 'sam-mac')
  const L = 'lnx';
  const M = 'sam-mac';
  const lnx = (ageMs: number): OwnerInfo => ({ ...owner(L, ageMs, siteAgents), machineName: 'srv1',
    devices: [{ deviceId: 'd-srv', name: 'srv1' }] }) as unknown as OwnerInfo;
  const three = (homeAge: number, siteAge: number, lnxAge: number): Record<string, OwnerInfo | null> =>
    ({ ...owners(homeAge, siteAge), [L]: lnx(lnxAge) });
  const order3 = [HOME, SITE, L];
  expectEq('auto 3: Home first', autoHost(three(ONLINE, ONLINE, ONLINE), null, order3), HOME);
  expectEq('auto 3: Home and Site off -> lnx', autoHost(three(OFFLINE, OFFLINE, ONLINE), null, order3), L);
  expectEq('auto 3: CO Desktop on the lnx machine -> lnx', autoHost(three(ONLINE, ONLINE, ONLINE), { deviceId: 'd-srv', name: 'srv1' }, order3), L);
  expectEq("auto 3: sam's own Mac first", autoHost({ ...three(ONLINE, ONLINE, ONLINE), [M]: owner(M, ONLINE, siteAgents) }, null, [M, HOME, SITE, L]), M);
  check('lnx selected, online', routeHost({ selected: L, agent: 'claude', wantsDevices: false, owners: three(ONLINE, ONLINE, ONLINE), order: order3 }), L, null);
  check('lnx offline: failover to Home', routeHost({ selected: L, agent: 'claude', wantsDevices: false, owners: three(ONLINE, ONLINE, OFFLINE), order: order3 }),
    HOME, 'Sent to Home: lnx is offline');
  check('home offline, site offline: failover to lnx', routeHost({ selected: HOME, agent: 'codex', wantsDevices: false, owners: three(OFFLINE, OFFLINE, ONLINE), order: order3 }),
    L, 'Sent to lnx: Home is offline');
  check('home offline, device job: never to another Bridge', routeHost({ selected: HOME, agent: 'codex', wantsDevices: true, owners: three(OFFLINE, OFFLINE, ONLINE), order: order3 }),
    HOME, null);
  check('lnx, codex with devices: to Home (its devices)', routeHost({ selected: L, agent: 'codex', wantsDevices: true, owners: three(ONLINE, ONLINE, ONLINE), order: order3 }),
    HOME, 'Sent to Home: device tools');
  check('lnx with gwbot keeps it', routeHost({ selected: L, agent: 'gwbot', wantsDevices: false, owners: { ...three(ONLINE, ONLINE, ONLINE), [L]: owner(L, ONLINE, homeAgents) }, order: order3 }),
    L, null);
  check('lnx without gwbot: gateway agent to Home', routeHost({ selected: L, agent: 'gwbot', wantsDevices: false, owners: three(ONLINE, ONLINE, ONLINE), order: order3 }),
    HOME, 'Sent to Home: gwbot runs only at home');
  expectEq('light 3: site + lnx = green', hostLight(three(OFFLINE, ONLINE, ONLINE), order3), 'green');
  expectEq('light 3: only lnx = blue', hostLight(three(OFFLINE, OFFLINE, ONLINE), order3), 'blue');
  expectEq('light 3: none = red', hostLight(three(OFFLINE, OFFLINE, OFFLINE), order3), 'red');

  return failures;
}

const failures = routeSelfTest();
console.log(failures.length ? failures.join('\n') : 'routing self-test: ok');
