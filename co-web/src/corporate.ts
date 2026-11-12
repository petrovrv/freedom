// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F1 step 5b: what CO Web knows about the corporate connector comes from its manifest
// (connectors/corporate/manifest.json), embedded at build time by vite.config.ts when install.json has
// connectors.corporate = true; none = neutral texts, no corporate agents or tools. The core never compares agent or
// tool names of the connector itself: it asks this module (isCorporateAgent, corporateTools, ...).
// Legacy wire: Bridge and devices still send engieBlocked / engie / engieTokens; read both (new name first).
// Pure module (no browser APIs) so the node self-tests can import it.

export interface CorporateAgent {
  id: string;
  kind: string; // corporate-llm | corporate-dispatcher
  title: string;
  family: string; // CLI it runs on ('claude' for a dispatcher), for the diagnosis rules
}

export interface CorporateManifest {
  id: string;
  title: string; // short name, e.g. of the employer
  employerTitle: string;
  portalTitle: string;
  signinTitle: string;
  paused: boolean; // the owner stopped using it: agents not offered, tools denied
  agents: CorporateAgent[];
  tools: { name: string; kind: string }[];
  errorPattern: string;
  usageSource: string;
}

declare const __CO_CORPORATE__: unknown;

const NEUTRAL: CorporateManifest = {
  id: 'corporate', title: 'Corporate', employerTitle: 'Corporate', portalTitle: 'the corporate portal', signinTitle: '',
  paused: false, agents: [], tools: [], errorPattern: '', usageSource: '',
};

export function parseCorporate(j: unknown): CorporateManifest {
  const o = j && typeof j === 'object' ? (j as Record<string, unknown>) : {};
  const str = (k: keyof CorporateManifest): string | undefined => (typeof o[k] === 'string' && o[k] !== '' ? (o[k] as string) : undefined);
  const title = str('title') ?? NEUTRAL.title;
  let errorPattern = str('errorPattern') ?? '';
  try { new RegExp(errorPattern); } catch { errorPattern = ''; }
  return {
    id: str('id') ?? NEUTRAL.id,
    title,
    employerTitle: str('employerTitle') ?? title,
    portalTitle: str('portalTitle') ?? NEUTRAL.portalTitle,
    signinTitle: str('signinTitle') ?? '',
    paused: o.paused === true,
    agents: (Array.isArray(o.agents) ? o.agents : [])
      .filter((a): a is Record<string, unknown> => !!a && typeof a === 'object' && typeof (a as Record<string, unknown>).id === 'string')
      .map((a) => ({
        id: String(a.id), kind: typeof a.kind === 'string' ? a.kind : 'corporate-llm',
        title: typeof a.title === 'string' ? a.title : String(a.id), family: typeof a.family === 'string' ? a.family : '',
      })),
    tools: (Array.isArray(o.tools) ? o.tools : [])
      .filter((t): t is Record<string, unknown> => !!t && typeof t === 'object' && typeof (t as Record<string, unknown>).name === 'string')
      .map((t) => ({ name: String(t.name), kind: typeof t.kind === 'string' ? t.kind : '' })),
    errorPattern,
    usageSource: str('usageSource') ?? '',
  };
}

let current: CorporateManifest = parseCorporate(typeof __CO_CORPORATE__ !== 'undefined' ? __CO_CORPORATE__ : null);

export function corporate(): CorporateManifest {
  return current;
}

// Self-tests set the manifest they test with.
export function setCorporate(j: unknown): void {
  current = parseCorporate(j);
}

export function corporateAgent(id: string | null | undefined): CorporateAgent | null {
  return (id && current.agents.find((a) => a.id === id)) || null;
}

export function isCorporateAgent(id: string | null | undefined): boolean {
  return corporateAgent(id) !== null;
}

// Agents of a paused connector: never offered, old messages addressed to them go to another agent.
export function pausedAgents(): ReadonlySet<string> {
  return new Set(current.paused ? current.agents.map((a) => a.id) : []);
}

export function corporateTools(kind?: string): string[] {
  return current.tools.filter((t) => !kind || t.kind === kind).map((t) => t.name);
}

// Core part of "the corporate model was unreachable" plus the connector's own messages (failures-lib.php the same).
export function corporateErrorRe(): RegExp {
  const core = 'corporate call failed|sign_in_needed|device_unreachable';
  return new RegExp(current.errorPattern ? `${core}|${current.errorPattern}` : core, 'i');
}

// Why a job could not reach the corporate model: corporateBlocked, legacy wire engieBlocked.
export function corporateBlocked(x: { corporateBlocked?: string | null; engieBlocked?: string | null } | null | undefined): string {
  return (x?.corporateBlocked || x?.engieBlocked || '').trim();
}

export type CorpState = 'online' | 'signin' | 'offline' | 'unknown' | 'off';
const STATES: ReadonlyArray<CorpState> = ['online', 'signin', 'offline', 'unknown', 'off'];

// Session state of the connector on a device: corp.state, legacy wire engie (CO Desktop / Bridge before 5b).
export function corpState(d: { corp?: { state?: unknown } | null; engie?: unknown } | null | undefined): CorpState | null {
  const s = d?.corp?.state ?? d?.engie;
  return STATES.includes(s as CorpState) ? (s as CorpState) : null;
}
