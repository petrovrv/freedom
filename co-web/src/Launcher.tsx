// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 shell (SPEC §3.6a): the launcher of the header. Every enabled module page that declares itself an app
// (ui.page app: true, icon) is one tile here, the same on a phone (a full-screen grid) and on a computer (a popover);
// the header itself keeps only the tray (small ui.header status pages). Below the apps it is the settings menu:
// the core settings panels and one tile per module with own settings (Host → Modules at that module), so every
// setting is one tap from ▦ like in the settings app of an OS. The launcher knows no module by name.
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { openInspectorTab, type InspectorTab } from './chatPanel';
import { shellApps, shellOpen, shellSettings, type ModuleInfo } from './extBridge';
import { openModulesSettings } from './Modules';

// the core settings panels of the inspector, in the order of the ☰ menu
const CORE_SETTINGS: [InspectorTab, string, string, string][] = [
  ['chat', '⚙', 'Chat', 'Permissions, device, modes, protocol, rename, move, delete'],
  ['project', '🗂', 'Project', 'Instructions, memory, schedules, sharing'],
  ['session', '🧾', 'Session', 'What the agent of this chat gets and spends'],
  ['host', '🖥', 'Host and devices', 'Bridge, agents, CO Desktop, browser devices, notifications'],
];

export default function Launcher({ modules, env, current }: { modules: ModuleInfo[]; env: string; current?: { moduleId: string; pageId: string } | null }) {
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();
  const apps = shellApps(modules);
  const settings = shellSettings(modules);
  useEffect(() => {
    if (!open) return;
    const f = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    window.addEventListener('keydown', f);
    return () => window.removeEventListener('keydown', f);
  }, [open]);
  const desktop = !!(window as unknown as { coDesktop?: unknown }).coDesktop;
  const go = (a: (typeof apps)[number]) => {
    setOpen(false);
    const o = shellOpen(a, env, desktop);
    if (o.target === 'blank') window.open(o.href, '_blank', 'noopener');
    else navigate(o.href);
  };
  const tile = (key: string, icon: string, label: string, title: string, onClick: () => void, here = false) => (
    <button key={key} type="button" className={`launcher-app ${here ? 'here' : ''}`} title={title} onClick={onClick}>
      <span className="launcher-icon" aria-hidden="true">{icon}</span>
      <span className="launcher-label">{label}</span>
    </button>
  );
  return (
    <div className="launcher">
      <button type="button" className={`launcher-btn ${open ? 'open' : ''}`} aria-haspopup="dialog" aria-expanded={open}
        title="Apps and settings" aria-label="Apps and settings" onClick={() => setOpen((v) => !v)}>
        <span aria-hidden="true">▦</span>
      </button>
      {open && (
        <>
          <div className="app-menu-scrim" onClick={() => setOpen(false)} />
          <div className="launcher-pop" role="dialog" aria-label="Apps and settings">
            <div className="launcher-head">
              <span>{apps.length ? 'Apps' : 'Settings'}</span>
              <button type="button" className="launcher-close" aria-label="Close" onClick={() => setOpen(false)}>×</button>
            </div>
            {apps.length > 0 && (
              <div className="launcher-grid">
                {apps.map((a) => tile(`${a.m.id}/${a.ui.id}`, a.icon, a.label, a.ui.title || a.m.description || a.label, () => go(a),
                  current?.moduleId === a.m.id && current?.pageId === a.ui.id))}
              </div>
            )}
            {apps.length > 0 && <div className="launcher-sub">Settings</div>}
            <div className="launcher-grid">
              {CORE_SETTINGS.map(([t, icon, label, hint]) => tile(`core/${t}`, icon, label, hint, () => { setOpen(false); openInspectorTab(t); }))}
              {tile('core/modules', '🧩', 'Modules', 'Turn modules on and off, the switch about you', () => { setOpen(false); openModulesSettings(); })}
              {settings.map((x) => tile(`set/${x.m.id}`, x.icon, x.label, `Settings of ${x.label}`, () => { setOpen(false); openModulesSettings(x.m.id); }))}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
