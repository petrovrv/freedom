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

export function label(hostId: string): string {
  if (hostId === HOME) return 'Home';
  if (hostId === SITE) return 'Site';
  return hostId;
}

// S8: devices of the environment reach a Claude job on the Site through the server relay (S1, schema 22),
// so device tools alone no longer pull a job to Home. Still Home: gateway agents, agents the Site lacks, device tools
// for another agent (the relay serves Claude only) and browser devices (w- ids are not relayed).
export function routeHost(o: {
  selected: string;
  agent: string;
  wantsDevices: boolean;
  owners: Record<string, OwnerInfo | null>;
  webDevices?: boolean;
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
    if (selected === HOME) return { hostId: HOME, note: null };
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
  const other = selected === HOME ? SITE : HOME;
  const selHealth = healthOf(owners, selected);
  const otherFits = hasAgent(owners[other] ?? null, agent);
  if (
    (selHealth === 'offline' || selHealth === 'updating' || selHealth === 'paused') &&
    healthOf(owners, other) === 'online' &&
    otherFits
  ) {
    return { hostId: other, note: `Sent to ${label(other)}: ${label(selected)} is ${selHealth}` };
  }

  // 3. Default
  return { hostId: selected, note: null };
}

// S2 (CO Web 0.98.0): executor preference 'auto' (default) or a fixed Home / Site set in Host and devices.
// S8 (11.11) sent every device except the Bridge PC to the Site; replaced 12.11 (owner): auto = Home while its
// Bridge is online, from any device (phone, browser, Mac); the Site while Home is not online. routeHost then applies
// the needs of the job, and the server moves a job no executor took within ~60 s (finalize-lib co_queue_handover).
export type HostPref = 'auto' | typeof HOME | typeof SITE;

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
): string {
  const home = healthOf(owners, HOME);
  const site = healthOf(owners, SITE);
  // 12.11 (owner): Home first from every device. A Site job reaches the PC only through the relay and runs on
  // Hostinger (~80 threads per account: Claude + relay MCP servers die with EAGAIN / exit 128), so from the phone the
  // code, builds, files and the browser of the PC felt like a seam. The Site is the fallback while Home is not online.
  void local;
  if (home === 'online') return HOME;
  if (site === 'online') return SITE;
  // nobody online: the server can start its agent (S7), the PC may be off
  return site === 'unknown' && home !== 'unknown' ? HOME : SITE;
}

// S2: header traffic light. green — Site and Bridge alive; blue — one of them (usually Site only; that is normal);
// red — none; grey — nothing announced yet.
export type Light = 'green' | 'blue' | 'red' | 'grey';

export function hostLight(owners: Record<string, OwnerInfo | null>): Light {
  const h = healthOf(owners, HOME);
  const s = healthOf(owners, SITE);
  if (h === 'unknown' && s === 'unknown') return 'grey';
  const n = (hostAlive(h) ? 1 : 0) + (hostAlive(s) ? 1 : 0);
  return n === 2 ? 'green' : n === 1 ? 'blue' : 'red';
}
