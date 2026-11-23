// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6): Host → Modules. The owner switches installed modules on and off (data stays) and sets the
// default of the person's switch for members who have not chosen yet; everybody sets the own switch of a module that
// keeps data about people (auto = show me without asking, ask, off). Installing, updating and removing are done by
// install/freedom_modules.py on the home PC (signature check, upload, migration); this section shows what it did.
import { useEffect, useState } from 'react';
import { dbAppNotifySet, dbModulePerson, dbModules, dbModuleSet } from './dbapi';
import type { ModuleInfo } from './extBridge';
import { deviceModules, ModulePages, MODULES_CHANGED } from './ModuleSlot';
import { ENVIRONMENTS } from './config';
import { openInspectorTab } from './chatPanel';

/** Host → Modules, scrolled to one module; CO Desktop calls it from its Connectors tab (window.coOpenModules). */
export function openModulesSettings(id?: string) {
  openInspectorTab('host');
  setTimeout(() => document.getElementById(id ? `module-${id}` : 'modules-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 250);
}
(window as unknown as { coOpenModules?: (id?: string) => void }).coOpenModules = (id?: string) => openModulesSettings(typeof id === 'string' ? id : undefined);

type DeviceState = { id: string; on: boolean; active: boolean; tools: string[]; problem: string; warning: string };

const MODES: { id: 'auto' | 'ask' | 'off'; label: string; hint: string }[] = [
  { id: 'auto', label: 'Automatic', hint: 'the module works with the defaults, without asking' },
  { id: 'ask', label: 'Ask me', hint: 'nothing about you is shown until you answer' },
  { id: 'off', label: 'Off', hint: 'the module shows nothing about you' },
];

function envName(env: string): string {
  return ENVIRONMENTS.find((e) => e.id === env)?.displayName ?? env;
}

export function ModulesSection({ me, owner }: { me?: string; owner: boolean }) {
  const [list, setList] = useState<ModuleInfo[] | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const load = async () => {
    try {
      setList(await dbModules());
      setErr('');
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  };
  useEffect(() => { void load(); }, []);
  // ext/1 device.tools: the per-device switch, only inside CO Desktop / CO Resident
  const dev = deviceModules();
  const [devState, setDevState] = useState<DeviceState[]>([]);
  const loadDev = async () => {
    if (!dev?.moduleDevice) return;
    try { setDevState(await dev.moduleDevice()); } catch { /* an older app: no switch shown */ }
  };
  useEffect(() => { void loadDev(); }, [list]); // eslint-disable-line react-hooks/exhaustive-deps

  const act = async (fn: () => Promise<void>) => {
    setBusy(true);
    setErr('');
    try {
      await fn();
      window.dispatchEvent(new Event(MODULES_CHANGED));
      await load();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  if (list === null) return <p className="small muted">{err || 'Loading…'}</p>;
  if (!list.length) {
    return <p className="small muted">No modules installed. The owner installs them on the home PC: <code>python install/freedom_modules.py install &lt;folder&gt;</code>.</p>;
  }
  return (
    <div className="modules-list">
      {list.map((m) => {
        const mine = m.personal ? m.personal.mode : null;
        return (
          <div key={m.id} id={`module-${m.id}`} className="module-row">
            <div className="module-row-head">
              <b>{m.title}</b>
              <span className="small muted">{m.version}{m.standard ? ' · standard' : ''}{m.envs?.length ? ` · ${m.envs.join(', ')}` : ''}</span>
              {owner && (
                <button className={`btn tiny ${m.enabled ? '' : 'primary'}`} type="button" disabled={busy}
                  onClick={() => void act(() => dbModuleSet(m.id, !m.enabled))}>{m.enabled ? 'Turn off' : 'Turn on'}</button>
              )}
            </div>
            <div className="small muted">{m.description}</div>
            {owner && m.permissions.length > 0 && <div className="small muted">Permissions: {m.permissions.join(', ')}</div>}
            {!m.enabled && <div className="small muted">Off — its data is kept.</div>}
            {m.enabled && m.device && dev?.moduleDeviceSet && (() => {
              const ds = devState.find((x) => x.id === m.id);
              if (!ds) return null;
              return (
                <div className="small">
                  <label title={m.device.tools.map((t) => `${t.name} [${t.class}]`).join(', ')}>
                    <input type="checkbox" checked={ds.on} disabled={busy} onChange={(e) => {
                      const on = e.target.checked;
                      if (on && ds.warning && !window.confirm(`${m.title}: ${ds.warning}`)) return;
                      void act(async () => { await dev.moduleDeviceSet!(m.id, on); await loadDev(); });
                    }} /> On this computer
                  </label>
                  {ds.on && ds.tools.length > 0 && <span className="muted"> · tools: {ds.tools.join(', ')}</span>}
                  {ds.problem && <div style={{ color: 'var(--warn)' }}>{ds.problem}</div>}
                </div>
              );
            })()}
            {m.enabled && m.ui.some((u) => u.slot === 'ui.settings') && (
              <div className="module-settings"><ModulePages slot="ui.settings" m={m} me={me} owner={owner} /></div>
            )}
            {m.enabled && m.notify && (
              <div className="small">
                <label title="Pushes of this app to your devices (at most 10 an hour). Off: the app keeps working, it just does not notify you.">
                  <input type="checkbox" checked={m.notify.on} disabled={busy}
                    onChange={(e) => { const on = e.target.checked; void act(() => dbAppNotifySet(m.id, on)); }} /> Notifications
                </label>
              </div>
            )}
            {m.personal && mine && (
              <div className="small">
                About you:{' '}
                {MODES.map((x) => (
                  <label key={x.id} title={x.hint} style={{ marginRight: 10 }}>
                    <input type="radio" name={`mod-${m.id}`} checked={mine === x.id} disabled={busy}
                      onChange={() => void act(() => dbModulePerson(m.id, x.id))} /> {x.label}
                  </label>
                ))}
              </div>
            )}
            {owner && m.personal && m.people && m.people.length > 0 && (
              <div className="module-people small">
                {m.people.filter((p) => p.env !== me).map((p) => (
                  <span key={p.env} style={{ display: 'contents' }}>
                    <span>{envName(p.env)}</span>
                    {p.own
                      ? <span className="muted">{MODES.find((x) => x.id === p.mode)?.label} (own choice)</span>
                      : (
                        <select value={p.mode} disabled={busy} aria-label={`default for ${envName(p.env)}`}
                          onChange={(e) => void act(() => dbModulePerson(m.id, e.target.value as 'auto' | 'ask' | 'off', p.env))}>
                          {MODES.map((x) => <option key={x.id} value={x.id}>{x.label} (default)</option>)}
                        </select>
                      )}
                  </span>
                ))}
              </div>
            )}
          </div>
        );
      })}
      {owner && <p className="small muted">A default you set here applies until the person chooses; their own choice is final. Install, update and remove: <code>install/freedom_modules.py</code> on the home PC.</p>}
      {err && <div className="small" style={{ color: 'var(--bad)' }}>{err}</div>}
    </div>
  );
}
