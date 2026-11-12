// SPDX-License-Identifier: AGPL-3.0-or-later
// S5 (CO Web 0.99.0, schema 23): one list of model resources — subscriptions of the hosts, the site, subscriptions
// registered by devices, corporate models — with a card of what each can and cannot do and the flag "slave allowed":
// a leading agent of any member may then call it as a subagent (aiwb-subtask spawn --resource), without limits or
// confirmations. Own resources are always callable by own agents; confidential ones never leave their environment.
import { Fragment, useEffect, useState } from 'react';
import { dbModelDelete, dbModels, dbModelSet, type ModelCard, type ModelResource } from './dbapi';
import { refreshModelResources } from './ModelPicker';

const KIND_LABEL: Record<string, string> = { subscription: 'subscription', site: 'site', corporate: 'corporate', free: 'free' };
const CARD_FIELDS: [keyof ModelCard, string][] = [
  ['summary', 'Summary'],
  ['strengths', 'Good at'],
  ['limits', 'Limits'],
  ['dataStaysIn', 'Data stays in'],
  ['notes', 'Notes'],
];

export function ModelsSection() {
  const [list, setList] = useState<ModelResource[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [edit, setEdit] = useState<string | null>(null);
  const [draft, setDraft] = useState<ModelCard>({});
  const [busy, setBusy] = useState(false);

  async function load() {
    try {
      setList(await dbModels());
      setErr(null);
      void refreshModelResources(); // S6: the composer's model list (ModelPicker) shows the same flags
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }
  useEffect(() => {
    void load();
  }, []);

  async function save(id: string, patch: Parameters<typeof dbModelSet>[1]) {
    setBusy(true);
    try {
      const r = await dbModelSet(id, patch);
      if (r) setList((l) => (l ? l.map((x) => (x.id === id ? { ...x, ...r, own: x.own, callable: x.callable } : x)) : l));
      void load(); // callable and visibility follow the flags
      setErr(null);
      return true;
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function reset(id: string) {
    if (!window.confirm('Reset this model to its defaults (or remove it if it is not built in)?')) return;
    setBusy(true);
    try {
      await dbModelDelete(id);
      await load();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  if (list === null) return <p className="muted small">{err ? `Models: ${err}` : 'Loading models…'}</p>;
  return (
    <div className="models">
      <p className="muted small">
        Any leading agent may call these models as subagents (tool aiwb-subtask: <code>models</code>, <code>spawn --resource</code>).
        Your own models are always available to your own agents; <b>Slave allowed</b> lets the agents of the other members use
        them too, without limits or confirmations. Confidential (corporate) models never serve another environment.
      </p>
      {err && <p className="error-text small">{err}</p>}
      {list.length === 0 && <p className="muted small">No models yet: the hosts have not announced their agents.</p>}
      {list.map((r) => {
        const mine = r.own;
        const state = r.online === true ? 'online' : r.online === false ? 'offline' : 'unknown';
        return (
          <div key={r.id} className="model-res">
            <div className="model-head">
              <span className={`host-pill ${state}`} title={`Host ${r.host || '—'}: ${state}`}>{state}</span>{' '}
              <b>{r.title}</b>{' '}
              <span className="muted small">
                {r.agent}{r.host ? ` on ${r.host}` : ''} · {KIND_LABEL[r.kind] ?? r.kind}
                {!mine && ` · of ${r.env}`}
                {r.confidential && ' · confidential'}
              </span>
            </div>
            {edit === r.id ? (
              <div className="model-card-edit">
                {CARD_FIELDS.map(([k, label]) => (
                  <label key={k} className="small">
                    {label}
                    <textarea rows={k === 'summary' || k === 'dataStaysIn' ? 1 : 2} value={draft[k] ?? ''}
                      onChange={(e) => setDraft((d) => ({ ...d, [k]: e.target.value }))} />
                  </label>
                ))}
                <div>
                  <button className="btn small primary" disabled={busy}
                    onClick={async () => { if (await save(r.id, { card: draft })) setEdit(null); }}>Save</button>{' '}
                  <button className="btn small ghost" disabled={busy} onClick={() => setEdit(null)}>Cancel</button>
                </div>
              </div>
            ) : (
              <dl className="kv small">
                {CARD_FIELDS.filter(([k]) => r.card?.[k]).map(([k, label]) => (
                  <Fragment key={k}><dt>{label}</dt><dd>{r.card[k]}</dd></Fragment>
                ))}
                {r.models.length > 0 && (
                  <><dt>Models</dt><dd>{r.models.map((m) => m.label || m.id).join(', ')}</dd></>
                )}
                <dt>Call as</dt>
                <dd><code>--resource {r.id}</code>{!r.callable && <span className="muted"> (no job queue for it yet)</span>}</dd>
              </dl>
            )}
            {mine && edit !== r.id && (
              <div className="model-actions small">
                <label title="Leading agents of the other members may call this model as a subagent">
                  <input type="checkbox" checked={r.slaveAllowed} disabled={busy || r.confidential}
                    onChange={(e) => void save(r.id, { slaveAllowed: e.target.checked })} /> Slave allowed
                </label>{' '}
                <label title="Corporate: never serves another environment, even with Slave allowed">
                  <input type="checkbox" checked={r.confidential} disabled={busy}
                    onChange={(e) => void save(r.id, { confidential: e.target.checked })} /> Confidential
                </label>{' '}
                <button className="btn tiny ghost" disabled={busy} onClick={() => { setDraft({ ...r.card }); setEdit(r.id); }}>Edit card</button>
                <button className="btn tiny ghost" disabled={busy} title={r.builtin ? 'Back to the built-in card and flags' : 'Remove this model'}
                  onClick={() => void reset(r.id)}>{r.builtin ? 'Reset' : 'Remove'}</button>
              </div>
            )}
            {!mine && r.slaveAllowed && <div className="muted small">Shared with you as a slave by {r.env}.</div>}
          </div>
        );
      })}
    </div>
  );
}
