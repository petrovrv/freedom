// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 shell (SPEC §3.6a): the launcher of the header. Every enabled module page that declares itself an app
// (ui.page app: true, icon) is one tile here, the same on a phone (a full-screen grid) and on a computer (a popover);
// the header itself keeps only the tray (small ui.header status pages). The last tile is the core app Settings (the
// control panel): the core settings panels grouped like an OS (this chat, project, system) and one tile per module
// with own settings (Host → Modules at that module). The ☰ menu keeps navigation only. The launcher knows no module
// by name.
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { openInspectorTab, type InspectorTab } from './chatPanel';
import { shellApps, shellOpen, shellSettings, type ModuleInfo } from './extBridge';
import { openModulesSettings } from './Modules';

type Core = [InspectorTab | 'modules', string, string, string];
// the control panel: core settings panels of the inspector, grouped
export const SETTINGS_GROUPS: [string, Core[]][] = [
  ['This chat', [
    ['chat', '⚙', 'Chat', 'Permissions, device, modes, protocol, rename, move, delete'],
    ['session', '🧾', 'Session', 'What the agent of this chat gets and spends'],
  ]],
  ['Project', [
    ['project', '🗂', 'Project', 'Instructions, memory, schedules, sharing'],
  ]],
  ['System', [
    ['host', '🖥', 'Host and devices', 'Bridge, agents, CO Desktop, browser devices, notifications'],
    ['modules', '🧩', 'Modules', 'Install, switch on and off, the switch about you (programs and features)'],
  ]],
];

export default function Launcher({ modules, env, current }: { modules: ModuleInfo[]; env: string; current?: { moduleId: string; pageId: string } | null }) {
  const [open, setOpen] = useState(false);
  const [view, setView] = useState<'apps' | 'settings'>('apps');
  const navigate = useNavigate();
  const apps = shellApps(modules);
  const settings = shellSettings(modules);
  useEffect(() => {
    if (!open) return;
    const f = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      if (view === 'settings') setView('apps'); else setOpen(false);
    };
    window.addEventListener('keydown', f);
    return () => window.removeEventListener('keydown', f);
  }, [open, view]);
  const desktop = !!(window as unknown as { coDesktop?: unknown }).coDesktop;
  const close = () => { setOpen(false); setView('apps'); };
  const go = (a: (typeof apps)[number]) => {
    close();
    const o = shellOpen(a, env, desktop);
    if (o.target === 'blank') window.open(o.href, '_blank', 'noopener');
    else navigate(o.href);
  };
  const core = (t: Core[0]) => { close(); if (t === 'modules') openModulesSettings(); else openInspectorTab(t); };
  const tile = (key: string, icon: string, label: string, title: string, onClick: () => void, here = false) => (
    <button key={key} type="button" className={`launcher-app ${here ? 'here' : ''}`} title={title} onClick={onClick}>
      <span className="launcher-icon" aria-hidden="true">{icon}</span>
      <span className="launcher-label">{label}</span>
    </button>
  );
  return (
    <div className="launcher">
      <button type="button" className={`launcher-btn ${open ? 'open' : ''}`} aria-haspopup="dialog" aria-expanded={open}
        title="Apps" aria-label="Apps" onClick={() => (open ? close() : setOpen(true))}>
        <span aria-hidden="true">▦</span>
      </button>
      {open && (
        <>
          <div className="app-menu-scrim" onClick={close} />
          <div className="launcher-pop" role="dialog" aria-label={view === 'apps' ? 'Apps' : 'Settings'}>
            {view === 'apps' ? (
              <>
                <div className="launcher-head">
                  <span>Apps</span>
                  <button type="button" className="launcher-close" aria-label="Close" onClick={close}>×</button>
                </div>
                <div className="launcher-grid">
                  {apps.map((a) => tile(`${a.m.id}/${a.ui.id}`, a.icon, a.label, a.ui.title || a.m.description || a.label, () => go(a),
                    current?.moduleId === a.m.id && current?.pageId === a.ui.id))}
                  {tile('core/settings', '⚙️', 'Settings', 'Settings of the chat, the project, the host and the modules', () => setView('settings'))}
                </div>
              </>
            ) : (
              <>
                <div className="launcher-head">
                  <button type="button" className="launcher-back" aria-label="Back to apps" onClick={() => setView('apps')}>‹</button>
                  <span className="launcher-title">Settings</span>
                  <button type="button" className="launcher-close" aria-label="Close" onClick={close}>×</button>
                </div>
                {SETTINGS_GROUPS.map(([group, items], i) => (
                  <div key={group}>
                    <div className={`launcher-sub ${i === 0 ? 'first' : ''}`}>{group}</div>
                    <div className="launcher-grid">{items.map(([t, icon, label, hint]) => tile(`core/${t}`, icon, label, hint, () => core(t)))}</div>
                  </div>
                ))}
                {settings.length > 0 && (
                  <>
                    <div className="launcher-sub">Modules</div>
                    <div className="launcher-grid">
                      {settings.map((x) => tile(`set/${x.m.id}`, x.icon, x.label, `Settings of ${x.label}`, () => { close(); openModulesSettings(x.m.id); }))}
                    </div>
                  </>
                )}
              </>
            )}
          </div>
        </>
      )}
    </div>
  );
}
