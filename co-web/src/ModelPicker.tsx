// SPDX-License-Identifier: AGPL-3.0-or-later
// S6 (CO Web 0.100.0): model choice of the composer from the one list of model resources (S5, Models.tsx) instead of
// the two agent/model selects. One button shows the leading agent · model · executor; its list has the agents this
// chat can lead with (per executor, with the card and a Slave allowed switch for own resources) and the helpers the
// leading agent may call as subagents (devices, a confidential gateway, the family's slaves). The selection stays {agent, model}
// (+ an optional pinned host): routeHost, templates, retries and old events work as before.
import { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { dbModels, dbModelSet, type ModelResource } from './dbapi';
import { openInspectorTab } from './chatPanel';
import { HOSTS } from './config';
import { hostAlive, hostHealth, label as hostLabel } from './routing';
import { currentLead, helperGroup, helperResources, leadEntries, type LeadEntry } from './modelPick';
import type { OwnerInfo } from './types';

// ---- one shared copy of models_list for every composer; Models.tsx refreshes it after a change ----
let cache: ModelResource[] | null = null;
let loading: Promise<void> | null = null;
let loadedAt = 0;
let failed = false;
let seq = 0;
const subs = new Set<() => void>();
const emit = () => subs.forEach((f) => f());
const subscribe = (f: () => void) => { subs.add(f); return () => { subs.delete(f); }; };
const snapshot = () => cache;

export function refreshModelResources(): Promise<void> {
  const my = ++seq; // a later call wins: an older answer never overwrites a newer flag
  const p = dbModels()
    .then((l) => { if (my === seq) { cache = l; failed = false; } })
    .catch(() => { if (my === seq) failed = true; /* old server or signed out: the list comes from owner.json alone */ })
    .finally(() => { if (my === seq) { loadedAt = Date.now(); loading = null; emit(); } });
  loading = p;
  return p;
}

function useModelResources(env: string): ModelResource[] | null {
  const list = useSyncExternalStore(subscribe, snapshot);
  useEffect(() => {
    // the server answers for the signed-in member: reload when the environment changes, after 5 min, or 30 s after a failure
    if (!loading && (env !== loadedEnv || Date.now() - loadedAt > (failed ? 30_000 : 5 * 60_000))) {
      loadedEnv = env;
      void refreshModelResources();
    }
  }, [env]);
  return list;
}
let loadedEnv = '';

export function openModelsSettings() {
  openInspectorTab('host');
  setTimeout(() => document.getElementById('models-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 250);
}

function SlaveSwitch({ r, busy, onSet }: { r: ModelResource; busy: boolean; onSet: (v: boolean) => void }) {
  if (r.kind === 'api') return <span className="mp-badge slave">coding helper · owner only</span>;
  if (!r.own) return <span className="mp-badge slave" title={`${r.env} lets your leading agent call this model as a helper`}>slave · {r.env}</span>;
  return (
    <label className={`mp-slave ${r.slaveAllowed ? 'on' : ''}`}
      title={r.confidential
        ? 'Confidential: never serves another environment'
        : 'Slave allowed: the leading agents of the other members may call this model as a subagent, without limits or confirmations'}
      onClick={(e) => e.stopPropagation()}>
      <input type="checkbox" checked={r.slaveAllowed} disabled={busy || r.confidential} onChange={(e) => onSet(e.target.checked)} />
      Slave allowed
    </label>
  );
}

export function ModelPicker({ sel, env, hostId, owners, agents, disabled, onPick }: {
  sel: { agent: string; model: string | null; host?: string | null };
  env: string;
  hostId: string; // the chat's executor now (Shell: auto or chosen)
  owners: Record<string, OwnerInfo | null>;
  agents: { id: string; displayName: string }[];
  disabled?: boolean;
  onPick: (p: { agent: string; model: string | null; host: string | null }) => void;
}) {
  const resources = useModelResources(env);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [more, setMore] = useState<string | null>(null);

  const lead = useMemo(() => leadEntries({
    hosts: HOSTS.map((h) => h.id), current: hostId, owners, agents, resources,
    health: (o) => (o ? hostAlive(hostHealth(o)) : null),
  }), [hostId, owners, agents, resources]);
  const helpers = useMemo(() => helperResources(resources, lead), [resources, lead]);
  const cur = currentLead(lead, sel.agent, sel.host, hostId);
  const curModel = cur?.models.find((m) => m.id === sel.model);
  const defLabel = cur?.defaultModel ? cur.models.find((m) => m.id === cur.defaultModel)?.label || cur.defaultModel : null;
  const shown = `${cur?.name || agents.find((a) => a.id === sel.agent)?.displayName || sel.agent} · ${curModel ? curModel.label || curModel.id : defLabel ? `${defLabel}` : 'default'}`;

  async function setSlave(id: string, v: boolean) {
    setBusy(true);
    try {
      await dbModelSet(id, { slaveAllowed: v });
      await refreshModelResources();
      setErr(null);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  function pick(l: LeadEntry, model: string | null) {
    onPick({ agent: l.agent, model, host: l.host }); // an explicit pin: it holds while auto flips the executor
    setOpen(false);
  }

  const card = (r: ModelResource | null) => r && (r.card?.summary || r.card?.strengths) ? (
    <div className="mp-card muted small">
      {r.card.summary}
      {more === r.id ? (
        <dl className="kv small">
          {r.card.strengths && <><dt>Good at</dt><dd>{r.card.strengths}</dd></>}
          {r.card.limits && <><dt>Limits</dt><dd>{r.card.limits}</dd></>}
          {r.card.dataStaysIn && <><dt>Data stays in</dt><dd>{r.card.dataStaysIn}</dd></>}
          {r.card.notes && <><dt>Notes</dt><dd>{r.card.notes}</dd></>}
        </dl>
      ) : (r.card.strengths || r.card.limits) && (
        <> <button type="button" className="link tiny" onClick={() => setMore(r.id)}>more</button></>
      )}
    </div>
  ) : null;

  const groups = new Map<string, ModelResource[]>();
  for (const r of helpers) groups.set(helperGroup(r), [...(groups.get(helperGroup(r)) || []), r]);

  return (
    <div className="mp-wrap">
      <button type="button" className="mp-btn" disabled={disabled} aria-expanded={open} aria-label="Model"
        title={`Leading agent and model${cur ? ` on ${hostLabel(cur.host)}` : ''}. Click to choose; helpers the agent may call are listed there too.`}
        onClick={() => setOpen((v) => !v)}>
        <span className={`hl-dot ${cur?.online === false ? 'red' : cur?.online ? 'green' : ''}`} />
        <span className="mp-name">{shown}</span>
        <span className="muted"> · {hostLabel(cur?.host || sel.host || hostId)}</span>
        {cur?.res?.slaveAllowed && <span className="mp-badge slave" title="Slave allowed: other members' agents may call it">slave</span>}
        {helpers.length > 0 && <span className="mp-badge" title="Helpers the leading agent may call as subagents">+{helpers.length}</span>}
        <span aria-hidden> ▾</span>
      </button>
      {open && (
        <>
          <div className="app-menu-scrim" onClick={() => setOpen(false)} />
          <div className="mp-pop" role="dialog" aria-label="Choose a model">
            <div className="mp-h">Leading agent of this chat</div>
            {lead.length === 0 && <div className="muted small">No agent announced yet: the executors are offline.</div>}
            {lead.map((l) => {
              const active = cur?.id === l.id;
              return (
                <div key={l.id} className={`mp-row ${active ? 'active' : ''}`} data-agent={l.agent} data-host={l.host} data-here={l.host === hostId ? '1' : undefined}>
                  <div className="mp-head">
                    <span className={`hl-dot ${l.online === false ? 'red' : l.online ? 'green' : ''}`} title={l.online === false ? 'offline: jobs wait or go to the other executor' : 'online'} />
                    <button type="button" className="link mp-title" data-pick="default" onClick={() => pick(l, null)}>
                      <b>{l.res?.title || `${l.name} · ${hostLabel(l.host)}`}</b>
                    </button>
                    <span className="muted tiny">{l.res?.kind || (l.host === 'site' ? 'site' : 'subscription')}{l.res?.confidential ? ' · confidential' : ''}</span>
                    <span className="mp-spacer" />
                    {l.res && <SlaveSwitch r={l.res} busy={busy} onSet={(v) => void setSlave(l.res!.id, v)} />}
                  </div>
                  {card(l.res)}
                  {l.models.length > 0 && (
                    <div className="mp-models">
                      <button type="button" className={`chip-btn ${active && !sel.model ? 'on' : ''}`} onClick={() => pick(l, null)}
                        title="The executor's default model">
                        {l.defaultModel ? `Default (${l.models.find((m) => m.id === l.defaultModel)?.label || l.defaultModel})` : 'Default'}
                      </button>
                      {l.models.map((m) => (
                        <button key={m.id} type="button" data-model={m.id} className={`chip-btn ${active && sel.model === m.id ? 'on' : ''}`}
                          title={m.description || m.id} onClick={() => pick(l, m.id)}>{m.label || m.id}</button>
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
            {helpers.length > 0 && (
              <>
                <div className="mp-h">Helpers your agent may call</div>
                <div className="muted tiny">The leading agent calls them itself as subagents (aiwb-subtask <code>spawn --resource</code>): according to each resource’s access rules and pricing.</div>
                {[...groups].map(([g, list]) => (
                  <div key={g}>
                    <div className="mp-g muted tiny">{g}</div>
                    {list.map((r) => (
                      <div key={r.id} className="mp-row helper">
                        <div className="mp-head">
                          <span className={`hl-dot ${r.online === false ? 'red' : r.online ? 'green' : ''}`} />
                          <b>{r.title}</b>
                          <span className="muted tiny">{r.kind}{r.confidential ? ' · confidential' : ''}{!r.callable ? ' · no job queue yet' : ''}</span>
                          <span className="mp-spacer" />
                          <SlaveSwitch r={r} busy={busy} onSet={(v) => void setSlave(r.id, v)} />
                        </div>
                        {card(r)}
                      </div>
                    ))}
                  </div>
                ))}
              </>
            )}
            {err && <div className="error-text small">{err}</div>}
            {resources === null && <div className="muted tiny">Cards and flags are not loaded (server list unavailable); the agents come from the executors.</div>}
            <button type="button" className="link small mp-foot" onClick={() => { setOpen(false); openModelsSettings(); }}>
              Models: cards and flags in Host and devices →
            </button>
          </div>
        </>
      )}
    </div>
  );
}
