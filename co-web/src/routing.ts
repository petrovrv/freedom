// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.29.0: capability routing and failover between the home PC (BridgeAgent) and the site agent.
// The gateway agents (gateway.ts) run only at home (S8: device tools of a Claude job also reach the Site); when the selected executor is offline and the other one is
// online and has the agent, the job goes there. Pure module (no browser APIs) so routing.selftest.ts runs in node.
import type { OwnerInfo } from './types';
import { isGatewayAgent } from './gateway.ts';

export const HOME = 'home-main';
export const SITE = 'site';

// 0.44.0: 'updating' and 'paused' — Bridge 1.4.2 is alive but takes no new jobs (owner.json state)
export type HostHealth = 'online' | 'updating' | 'paused' | 'stale' | 'offline' | 'unknown';

export function hostHealth(owner: OwnerInfo | null): HostHealth {
  if (!owner?.updatedUtc) return 'unknown';
  const age = (Date.now() - new Date(owner.updatedUtc).getTime()) / 1000;
  // owner.json is refreshed every hostLeaseSeconds/3 (100 s with the default 300 s lease),
  // then Drive sync adds latency.
  if (age < 240) {
    if (owner.state === 'updating') return 'updating';
    if (owner.state === 'paused') return 'paused';
    return 'online';
  }
  if (age < 900) return 'stale';
  return 'offline';
}

// 0.44.0: true when the host process is alive (it may still take no new jobs)
export function hostAlive(health: HostHealth): boolean {
  return health === 'online' || health === 'updating' || health === 'paused' || health === 'stale';
}

// 0.44.0: one line on why the host takes no new jobs, e.g. "updating to 1.4.2, waiting for 2 jobs"
export function hostStateText(owner: OwnerInfo | null, health: HostHealth): string {
  if (health === 'updating') {
    const n = owner?.runningJobs ?? 0;
    const to = owner?.updateTo ? ` to ${owner.updateTo}` : '';
    return `updating${to}, waiting for ${n} job${n === 1 ? '' : 's'}`;
  }
  if (health === 'paused') return 'paused';
  return health;
}

const DEFAULT_AGENTS = new Set(['claude', 'codex']);

function hasAgent(owner: OwnerInfo | null | undefined, agent: string): boolean {
  const agents = owner?.agents;
  if (agents) return Object.prototype.hasOwnProperty.call(agents, agent);
  return DEFAULT_AGENTS.has(agent);
}

function healthOf(owners: Record<string, OwnerInfo | null>, hostId: string): HostHealth {
  return hostHealth(owners[hostId] ?? null);
}

// Freedom F12: titles of the registered executors (executors.ts fills them from db.php op executors)
const TITLES = new Map<string, string>();
export function setHostTitles(t: Record<string, string>): void {
  TITLES.clear();
  for (const [k, v] of Object.entries(t)) if (typeof v === 'string' && v) TITLES.set(k, v);
}

export function label(hostId: string): string {
  if (hostId === HOME) return 'Home';
  if (hostId === SITE) return 'Site';
  return TITLES.get(hostId) || hostId;
}

// S8: devices of the environment reach a Claude job on the Site through the server relay (S1, schema 22),
// so device tools alone no longer pull a job to Home. Still Home: gateway agents, agents the Site lacks, device tools
// for another agent (the relay serves Claude only) and browser devices (w- ids are not relayed).
// Freedom F12: any Bridge (not the site) keeps the jobs it was chosen for like Home does; failover tries the executors of
// `order` (executors.ts autoOrder: the environment's own one, Home, Site, the others), a device job only Home or the Site.
export function routeHost(o: {
  selected: string;
  agent: string;
  wantsDevices: boolean;
  owners: Record<string, OwnerInfo | null>;
  webDevices?: boolean;
  order?: string[];
}): { hostId: string; note: string | null } {
  const { selected, agent, wantsDevices, owners } = o;
  const selectedOwner = owners[selected] ?? null;
  const homeOwner = owners[HOME] ?? null;

  // 1. Needs home
  const homeOnlyAgent = isGatewayAgent(agent);
  const missingOnSelected =
    selected !== HOME && !hasAgent(selectedOwner, agent) && hasAgent(homeOwner, agent);
  const devicesNeedHome = (wantsDevices && agent !== 'claude') || !!o.webDevices;

  if (devicesNeedHome || homeOnlyAgent || missingOnSelected) {
    if (selected !== SITE && !missingOnSelected) return { hostId: selected, note: null };
    const reason = o.webDevices && !homeOnlyAgent && !missingOnSelected ? 'browser devices'
      : devicesNeedHome && !homeOnlyAgent && !missingOnSelected ? 'device tools' : `${agent} runs only at home`;
    let note = `Sent to Home: ${reason}`;
    if (healthOf(owners, HOME) === 'offline') {
      note += ' (Home is offline — the job waits in its queue)';
    }
    return { hostId: HOME, note };
  }

  // 2. Failover. 0.79.0: also when the selected host is alive but takes no new jobs (updating / paused): 03.11 a phone
  // question waited 17 min behind a Bridge update while the site was online. S8: a device job of Claude may go from
  // Home to the Site (relay); other device jobs stay at Home.
  const selHealth = healthOf(owners, selected);
  if (selHealth === 'offline' || selHealth === 'updating' || selHealth === 'paused') {
    const order = o.order && o.order.length ? o.order : [HOME, SITE];
    const other = order.find((h) =>
      h !== selected && healthOf(owners, h) === 'online' && hasAgent(owners[h] ?? null, agent)
      // a device job goes to the Site (relay) or Home (its devices), never to another Bridge
      && (!wantsDevices || h === HOME || h === SITE));
    if (other) return { hostId: other, note: `Sent to ${label(other)}: ${label(selected)} is ${selHealth}` };
  }

  // 3. Default
  return { hostId: selected, note: null };
}

// S2 (CO Web 0.98.0): executor preference 'auto' (default) or a fixed Home / Site set in Host and devices.
// S8 (11.11) sent every device except the Bridge PC to the Site; replaced 12.11 (owner): auto = Home while its
// Bridge is online, from any device (phone, browser, Mac); the Site while Home is not online. routeHost then applies
// the needs of the job, and the server moves a job no executor took within ~60 s (finalize-lib co_queue_handover).
// Freedom F12: or any executor id (executors.ts).
export type HostPref = 'auto' | typeof HOME | typeof SITE | string;

// True when this CO Desktop runs on the machine of the home Bridge (owner.json machineName = the device's name there).
export function onBridgeHost(owner: OwnerInfo | null | undefined, local: { deviceId: string; name?: string } | null | undefined): boolean {
  if (!owner || !local?.deviceId || !owner.machineName) return false;
  const machine = owner.machineName.trim().toLowerCase();
  const d = (owner.devices || []).find((x) => x && x.deviceId === local.deviceId);
  const name = (d?.name || local.name || '').trim().toLowerCase();
  return !!name && name === machine;
}

export function autoHost(
  owners: Record<string, OwnerInfo | null>,
  local?: { deviceId: string; name?: string } | null,
  order?: string[],
): string {
  const home = healthOf(owners, HOME);
  const site = healthOf(owners, SITE);
  const ids = order && order.length ? order : [HOME, SITE];
  // F12 (SPEC §3.1a): CO Desktop on the machine of a Bridge — that Bridge.
  for (const id of ids) {
    if (id !== SITE && healthOf(owners, id) === 'online' && onBridgeHost(owners[id], local)) return id;
  }
  // 12.11 (owner): Home first from every device. A Site job reaches the PC only through the relay and runs on
  // Hostinger (~80 threads per account: Claude + relay MCP servers die with EAGAIN / exit 128), so from the phone the
  // code, builds, files and the browser of the PC felt like a seam. The Site is the fallback while Home is not online.
  // F12: the order comes from the server (the environment's own executor first, then Home, Site, the others).
  for (const id of ids) if (healthOf(owners, id) === 'online') return id;
  // nobody online: the server can start its agent (S7), the PC may be off
  return site === 'unknown' && home !== 'unknown' ? HOME : SITE;
}

// S2: header traffic light. green — Site and Bridge alive; blue — one of them (usually Site only; that is normal);
// red — none; grey — nothing announced yet.
export type Light = 'green' | 'blue' | 'red' | 'grey';

// F12: with more executors — green when the Site and at least one Bridge are alive.
export function hostLight(owners: Record<string, OwnerInfo | null>, ids: string[] = [HOME, SITE]): Light {
  const all = ids.includes(SITE) ? ids : [...ids, SITE];
  if (all.every((id) => healthOf(owners, id) === 'unknown')) return 'grey';
  const site = hostAlive(healthOf(owners, SITE));
  const bridge = all.some((id) => id !== SITE && hostAlive(healthOf(owners, id)));
  return site && bridge ? 'green' : site || bridge ? 'blue' : 'red';
}
