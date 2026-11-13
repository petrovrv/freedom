// SPDX-License-Identifier: AGPL-3.0-or-later
// S6 (CO Web 0.100.0): the chat's model choice from the one list of model resources (S5, db.php models_list).
// "Lead" entries are what this chat can run as its leading agent: every agent an executor (home Bridge, site agent)
// announces in its owner.json, enriched with its resource (card, slave allowed). Everything else the environment may
// call - subscriptions registered by devices, confidential gateway models, the family's slaves - is a "helper": the leading agent
// calls it as a subagent (aiwb-subtask spawn --resource). Pure module (no browser APIs): modelPick.selftest.ts.
import type { ModelResource } from './dbapi.ts';
import type { CatalogModel, OwnerInfo } from './types.ts';
import { gateway, isGatewayKind } from './gateway.ts';

export interface LeadEntry {
  id: string; // <host>/<agent>, the id of the built-in resource
  host: string;
  agent: string;
  name: string; // agent display name
  models: CatalogModel[];
  defaultModel: string | null;
  online: boolean | null;
  res: ModelResource | null;
}

const DEFAULT_AGENTS = ['claude', 'codex'];

export function leadEntries(o: {
  hosts: string[];
  current: string;
  owners: Record<string, OwnerInfo | null>;
  agents: { id: string; displayName: string }[]; // pickable agents the project allows
  resources: ModelResource[] | null;
  health: (owner: OwnerInfo | null) => boolean | null;
}): LeadEntry[] {
  const byId = new Map((o.resources || []).map((r) => [r.id, r]));
  const out: LeadEntry[] = [];
  for (const host of o.hosts) {
    const owner = o.owners[host] ?? null;
    // a host that never announced itself is offered only when it is the current one (old behaviour: Claude and GPT)
    if (!owner?.agents && host !== o.current) continue;
    for (const a of o.agents) {
      const cat = owner?.agents ? owner.agents[a.id] : DEFAULT_AGENTS.includes(a.id) ? {} : undefined;
      if (!cat) continue;
      const id = `${host}/${a.id}`;
      out.push({
        id, host, agent: a.id, name: cat.displayName || a.displayName,
        models: cat.models || [], defaultModel: cat.defaultModel ?? null,
        online: owner ? o.health(owner) : null, res: byId.get(id) ?? null,
      });
    }
  }
  return out;
}

/** Resources that are not a lead entry of this chat: the subagents the leading agent may call. */
export function helperResources(resources: ModelResource[] | null, lead: LeadEntry[]): ModelResource[] {
  const ids = new Set(lead.map((l) => l.id));
  return (resources || []).filter((r) => !ids.has(r.id));
}

/** The entry the composer's selection points at: the pinned host, else the current one, else any host with the agent. */
export function currentLead(lead: LeadEntry[], agent: string, pinned: string | null | undefined, current: string): LeadEntry | null {
  return lead.find((l) => l.agent === agent && l.host === (pinned || current))
    ?? lead.find((l) => l.agent === agent) ?? null;
}

/** Group label of a helper (own subscription on a device, a confidential gateway, the family's). */
export function helperGroup(r: ModelResource): string {
  if (!r.own) return `Family: ${r.env}`;
  if (isGatewayKind(r.kind)) return gateway().module ? gateway().title : 'Confidential';
  if (r.kind === 'site') return 'Site';
  return 'Your devices';
}
