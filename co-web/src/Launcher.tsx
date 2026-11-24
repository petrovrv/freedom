// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 shell (SPEC §3.6a): the launcher of the header. Every enabled module page that declares itself an app
// (ui.page app: true, icon) is one tile here, the same on a phone (a full-screen grid) and on a computer (a popover);
// the header itself keeps only the tray (small ui.header status pages). Core apps: 👪 Family first (the family room,
// with the count of new messages) and Settings last (the control panel): the Properties of this chat and of the
// project, System, and one tile per module with own settings (System → Modules at that module), grouped like an OS.
// The ☰ menu keeps navigation only. The launcher knows no module by name.
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { openPanel } from './chatPanel';
import { moduleBadges, onModuleBadges, shellApps, shellOpen, shellSettings, type ModuleInfo } from './extBridge';
import { openModulesSettings } from './Modules';
import { LAUNCHER_OPEN } from './appStack';

type Core = ['chat' | 'project' | 'system' | 'modules', string, string, string];
// the control panel (0.128.0): the Properties of the open chat and project folders and System, grouped
export const SETTINGS_GROUPS: [string, Core[]][] = [
  ['This chat', [
    ['chat', 'ⓘ', 'Chat properties', 'Permissions, device, modes, context of the folders, sharing, what the agent gets and spends, rename, move, delete (Alt+Enter)'],
  ]],
  ['Project', [
    ['project', '📁', 'Project properties', 'Instructions, memory, context for its chats, schedules, sharing, files'],
  ]],
  ['System', [
    ['system', '🖥', 'System', 'Host and executors, CO Desktop, browser devices, notifications, agents, models, data, limits, health, jobs, connectors'],
    ['modules', '🧩', 'Modules', 'Install, switch on and off, the switch about you (programs and features)'],
  ]],
];

export default function Launcher({ modules, env, current, familyUnread = 0 }: {
  modules: ModuleInfo[]; env: string; current?: { moduleId: string; pageId: string } | null;
  /** new messages of the others in the family room (the core app 👪) */
  familyUnread?: number;
}) {
  const [open, setOpen] = useState(false);
  const [view, setView] = useState<'apps' | 'settings'>('apps');
  const navigate = useNavigate();
  const apps = shellApps(modules);
  const settings = shellSettings(modules);
  // coExt.badge of a module: on its app tiles, and a dot on ▦ while any app has one
  const [, setBadgeTick] = useState(0);
  useEffect(() => onModuleBadges(() => setBadgeTick((n) => n + 1)), []);
  const badges = moduleBadges();
  const anyBadge = familyUnread > 0 || apps.some((a) => badges.has(a.m.id));
  // ▦ of the app bar (F14e.1) opens this launcher
  useEffect(() => {
    const f = () => { setView('apps'); setOpen(true); };
    window.addEventListener(LAUNCHER_OPEN, f);
    return () => window.removeEventListener(LAUNCHER_OPEN, f);
  }, []);
  useEffect(() => {
    if (!open) return;
    const f = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.preventDefault(); // Esc closes the launcher, not the app under it
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
    // app to app replaces the entry: Back leaves the apps for the page they were opened from (F14e.1)
    else navigate(o.href, { replace: !!current });
  };
  const core = (t: Core[0]) => { close(); if (t === 'modules') openModulesSettings(); else openPanel(t); };
  const tile = (key: string, icon: string, label: string, title: string, onClick: () => void, here = false, badge = '') => (
    <button key={key} type="button" className={`launcher-app ${here ? 'here' : ''}`} title={badge ? `${title} (${badge})` : title} onClick={onClick}>
      <span className="launcher-icon" aria-hidden="true">{icon}{badge && <span className="launcher-badge">{badge}</span>}</span>
      <span className="launcher-label">{label}</span>
    </button>
  );
  return (
    <div className="launcher">
      <button type="button" className={`launcher-btn ${open ? 'open' : ''}`} aria-haspopup="dialog" aria-expanded={open}
        title="Apps" aria-label="Apps" onClick={() => (open ? close() : setOpen(true))}>
        <span aria-hidden="true">▦</span>
        {anyBadge && <span className="launcher-dot" aria-hidden="true" />}
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
                  {tile('core/family', '👪', 'Family', 'The family chat: messages, photos, emoji; @co calls AI Workbench', () => { close(); openPanel('family'); },
                    false, familyUnread > 0 ? (familyUnread > 99 ? '99+' : String(familyUnread)) : '')}
                  {apps.map((a) => tile(`${a.m.id}/${a.ui.id}`, a.icon, a.label, a.ui.title || a.m.description || a.label, () => go(a),
                    current?.moduleId === a.m.id && current?.pageId === a.ui.id, badges.get(a.m.id) || ''))}
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
