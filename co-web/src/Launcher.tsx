// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 shell (SPEC §3.6a): the launcher of the header. Every enabled module page that declares itself an app
// (ui.page app: true, icon) is one tile here, the same on a phone (a full-screen grid) and on a computer (a popover);
// the header itself keeps only the tray (small ui.header status pages). The launcher knows no module by name.
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { shellApps, shellOpen, type ModuleInfo } from './extBridge';

export default function Launcher({ modules, env, current }: { modules: ModuleInfo[]; env: string; current?: { moduleId: string; pageId: string } | null }) {
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();
  const apps = shellApps(modules);
  useEffect(() => {
    if (!open) return;
    const f = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    window.addEventListener('keydown', f);
    return () => window.removeEventListener('keydown', f);
  }, [open]);
  if (!apps.length) return null;
  const desktop = !!(window as unknown as { coDesktop?: unknown }).coDesktop;
  const go = (a: (typeof apps)[number]) => {
    setOpen(false);
    const o = shellOpen(a, env, desktop);
    if (o.target === 'blank') window.open(o.href, '_blank', 'noopener');
    else navigate(o.href);
  };
  return (
    <div className="launcher">
      <button type="button" className={`launcher-btn ${open ? 'open' : ''}`} aria-haspopup="dialog" aria-expanded={open}
        title="Apps" aria-label="Apps" onClick={() => setOpen((v) => !v)}>
        <span aria-hidden="true">▦</span>
      </button>
      {open && (
        <>
          <div className="app-menu-scrim" onClick={() => setOpen(false)} />
          <div className="launcher-pop" role="dialog" aria-label="Apps">
            <div className="launcher-head">
              <span>Apps</span>
              <button type="button" className="launcher-close" aria-label="Close" onClick={() => setOpen(false)}>×</button>
            </div>
            <div className="launcher-grid">
              {apps.map((a) => {
                const here = current?.moduleId === a.m.id && current?.pageId === a.ui.id;
                return (
                  <button key={`${a.m.id}/${a.ui.id}`} type="button" className={`launcher-app ${here ? 'here' : ''}`} title={a.ui.title || a.m.description || a.label} onClick={() => go(a)}>
                    <span className="launcher-icon" aria-hidden="true">{a.icon}</span>
                    <span className="launcher-label">{a.label}</span>
                  </button>
                );
              })}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
