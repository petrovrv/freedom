// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6): the confidential model gateway - a model gateway reached through a device tool, plus an
// optional dispatcher agent that delegates to it. CO Web knows no gateway by name: an installed module declares one
// (module.json provides.gateway) and db.php modules_list hands its normalized description in the field "gateways"
// (dbapi.ts dbModules calls setGateways). Until it answers CO Web is neutral: no gateway agents or tools, generic texts.
// Installed but disabled = paused: its agents are known (old messages show their names) but never offered, old
// messages addressed to them go to another agent, its device tools are denied.
// Legacy wire still read (deployed Bridges, CO Desktops and old rows send them), all of it only in this file: the two
// older names of the connector (LEGACY_NAMES below) as model kind, job_failures kind, project.json flag, device heartbeat
// field and Bridge error text; result/event fields corporateBlocked / engieBlocked (new: gatewayBlocked); device
// heartbeat corp.state (new: gateway.state); savings tokens corpTokens / engieTokens (new: gatewayTokens).
// Pure module (no browser APIs) so the node self-tests can import it.

export interface GatewayAgent {
  id: string;
  kind: string; // gateway-llm | gateway-dispatcher
  title: string;
  family: string; // CLI it runs on ('claude' for a dispatcher), for the diagnosis rules
  needsDevice: boolean;
  defaultModel: string;
  job: boolean;
}

export interface GatewayOps {
  resume?: string; // user op of the module: resume chats the gateway blocked
  waiting?: string; // user op of the module: chats waiting for the gateway
}

export interface Gateway {
  module: string; // id of the module that provides it; '' = none
  enabled: boolean;
  paused: boolean; // module disabled: agents not offered, tools denied
  title: string; // short name, e.g. of the employer
  employerTitle: string;
  portalTitle: string;
  signinTitle: string;
  domains: string[];
  errorPattern: string;
  usageSource: string;
  resumeText: string;
  blockedMarkers: string[];
  agents: GatewayAgent[];
  tools: { name: string; kind: string }[];
  ops: GatewayOps;
}

const NEUTRAL: Gateway = {
  module: '', enabled: false, paused: false, title: 'Employer', employerTitle: 'Employer', portalTitle: 'the employer portal',
  signinTitle: '', domains: [], errorPattern: '', usageSource: '', resumeText: '', blockedMarkers: [], agents: [], tools: [], ops: {},
};

const OP = /^[a-z][a-z0-9_]{2,40}$/;

// The connector's names before Freedom F10 (F1 5b) and before F1 5b: legacy wire only.
const LEGACY_NAME = "corporate";
const OLDER_NAME = "engie";

function toolKind(k: unknown): string {
  if (k === 'llm' || k === 'gateway-llm') return 'gateway-llm';
  if (k === 'fetch' || k === 'gateway-fetch') return 'gateway-fetch';
  return '';
}

export function parseGateway(j: unknown): Gateway {
  if (!j || typeof j !== 'object' || Array.isArray(j)) return { ...NEUTRAL };
  const o = j as Record<string, unknown>;
  const str = (k: string): string | undefined => (typeof o[k] === 'string' && o[k] !== '' ? (o[k] as string) : undefined);
  const strs = (k: string): string[] => (Array.isArray(o[k]) ? (o[k] as unknown[]).filter((x): x is string => typeof x === 'string' && x !== '') : []);
  const title = str('title') ?? NEUTRAL.title;
  let errorPattern = str('errorPattern') ?? '';
  try { new RegExp(errorPattern); } catch { errorPattern = ''; }
  const enabled = o.enabled !== false && o.paused !== true;
  const ops: GatewayOps = {};
  const rawOps = o.ops && typeof o.ops === 'object' ? (o.ops as Record<string, unknown>) : {};
  for (const k of ['resume', 'waiting'] as const) {
    const v = rawOps[k];
    if (typeof v === 'string' && OP.test(v)) ops[k] = v;
  }
  return {
    module: str('module') ?? str('id') ?? 'gateway',
    enabled,
    paused: !enabled,
    title,
    employerTitle: str('employerTitle') ?? title,
    portalTitle: str('portalTitle') ?? NEUTRAL.portalTitle,
    signinTitle: str('signinTitle') ?? '',
    domains: strs('domains'),
    errorPattern,
    usageSource: str('usageSource') ?? '',
    resumeText: str('resumeText') ?? '',
    blockedMarkers: strs('blockedMarkers'),
    agents: (Array.isArray(o.agents) ? o.agents : [])
      .filter((a): a is Record<string, unknown> => !!a && typeof a === 'object' && typeof (a as Record<string, unknown>).id === 'string')
      .map((a) => ({
        id: String(a.id),
        kind: a.kind === 'gateway-dispatcher' || a.kind === 'dispatcher' ? 'gateway-dispatcher' : 'gateway-llm',
        title: typeof a.title === 'string' && a.title ? a.title : String(a.id),
        family: typeof a.family === 'string' ? a.family : '',
        needsDevice: a.needsDevice === true,
        defaultModel: typeof a.defaultModel === 'string' ? a.defaultModel : '',
        job: a.job === true,
      })),
    tools: (Array.isArray(o.tools) ? o.tools : [])
      .filter((t): t is Record<string, unknown> => !!t && typeof t === 'object' && typeof (t as Record<string, unknown>).name === 'string')
      .map((t) => ({ name: String(t.name), kind: toolKind(t.kind) })),
    ops,
  };
}

let current: Gateway = { ...NEUTRAL };
let currentKey = '';
let version = 0;
const listeners = new Set<() => void>();

export function gateway(): Gateway {
  return current;
}

/** Sets the description (self-tests; null = neutral). Listeners run only when it really changed. */
export function setGateway(j: unknown): void {
  const next = parseGateway(j);
  const key = JSON.stringify(next);
  current = next;
  if (key === currentKey) return;
  currentKey = key;
  version++;
  for (const f of listeners) {
    try { f(); } catch { /* a listener never breaks the others */ }
  }
}

/** Grows on every change of the description: a dependency for memos built from it (agent lists). */
export function gatewayVersion(): number {
  return version;
}

/** The "gateways" field of db.php modules_list: the first one counts; absent or empty = neutral. */
export function setGateways(list: unknown): void {
  setGateway(Array.isArray(list) && list.length ? list[0] : null);
}

/** Called after every change of the description (config.ts refills its agent lists). Returns the unsubscribe. */
export function onGatewayChange(f: () => void): () => void {
  listeners.add(f);
  return () => { listeners.delete(f); };
}

export function gatewayAgent(id: string | null | undefined): GatewayAgent | null {
  return (id && current.agents.find((a) => a.id === id)) || null;
}

export function isGatewayAgent(id: string | null | undefined): boolean {
  return gatewayAgent(id) !== null;
}

// Agents of a paused gateway: never offered, old messages addressed to them go to another agent.
export function pausedAgents(): ReadonlySet<string> {
  return new Set(current.paused ? current.agents.map((a) => a.id) : []);
}

export function gatewayTools(kind?: string): string[] {
  return current.tools.filter((t) => !kind || t.kind === kind).map((t) => t.name);
}

const escapeRe = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Core part of "the gateway model was unreachable" plus the module's own messages (failures-lib.php the same).
export function gatewayErrorRe(): RegExp {
  // LEGACY_NAME call failed: legacy wire text of Bridges before F10 without a gateway title
  const core = ['gateway call failed', `${LEGACY_NAME} call failed`, 'sign_in_needed', 'device_unreachable'];
  if (current.module) core.push(`${escapeRe(current.title)} call failed`);
  if (current.errorPattern) core.push(current.errorPattern);
  return new RegExp(core.join('|'), 'i');
}

// Legacy wire field names of gatewayBlocked (Bridge / server before Freedom F10 and before F1 5b).
const LEGACY_BLOCKED = ['corporateBlocked', 'engieBlocked'] as const;

/** Why a job could not reach the gateway model: gatewayBlocked, then the legacy wire names. */
export function gatewayBlocked(x: object | null | undefined): string {
  if (!x) return '';
  const o = x as Record<string, unknown>;
  for (const k of ['gatewayBlocked', ...LEGACY_BLOCKED]) {
    const v = o[k];
    if (typeof v === 'string' && v.trim()) return v.trim();
  }
  return '';
}

/** Fields an event written by CO Web carries for a blocked job: the new name and the legacy wire names, so that
 * servers and CO Web copies that read only the old ones (drive-sync, older clients) still see it. */
export function gatewayBlockedFields(reason: string): Record<string, string> {
  return reason ? { gatewayBlocked: reason, corporateBlocked: reason, engieBlocked: reason } : {};
}

export type GatewayState = 'online' | 'signin' | 'offline' | 'unknown' | 'off';
const STATES: ReadonlyArray<GatewayState> = ['online', 'signin', 'offline', 'unknown', 'off'];

/** Session state of the gateway on a device: gateway.state, legacy wire corp.state (F1 5b) and OLDER_NAME (before it). */
export function gatewayState(d: object | null | undefined): GatewayState | null {
  if (!d) return null;
  const o = d as Record<string, unknown>;
  const st = (v: unknown) => (v && typeof v === 'object' ? (v as Record<string, unknown>).state : undefined);
  const s = st(o.gateway) ?? st(o.corp) ?? o[OLDER_NAME];
  return STATES.includes(s as GatewayState) ? (s as GatewayState) : null;
}

/** Project flag "holds confidential data": confidential, legacy project.json field LEGACY_NAME (CO Web 0.28.0 .. F10). */
export function projectConfidential(p: object | null | undefined): boolean {
  if (!p) return false;
  const o = p as Record<string, unknown>;
  return typeof o.confidential === 'boolean' ? o.confidential : o[LEGACY_NAME] === true;
}

/** Patch of saveProjectMeta for the flag: writes the new name and drops the legacy one from project.json. */
export function confidentialPatch(on: boolean): { confidential: boolean } {
  return { confidential: on, [LEGACY_NAME]: undefined } as { confidential: boolean };
}

/** Model resource kinds of a gateway (models-lib.php): gateway, legacy wire LEGACY_NAME. */
export function isGatewayKind(kind: string | null | undefined): boolean {
  return kind === 'gateway' || kind === LEGACY_NAME;
}

/** Diagnosis kinds of a gateway failure (failures-lib.php job_failures): gateway, legacy rows of both older names. */
export function isGatewayFailureKind(kind: string | null | undefined): boolean {
  return kind === 'gateway' || kind === LEGACY_NAME || kind === OLDER_NAME;
}

/** Output tokens of the gateway models in a savings window: gatewayTokens, then the legacy wire names. */
export function gatewayTokens(w: object | null | undefined): number | null {
  if (!w) return null;
  const o = w as Record<string, unknown>;
  for (const k of ['gatewayTokens', 'corpTokens', 'engieTokens']) if (typeof o[k] === 'number') return o[k] as number;
  return null;
}
