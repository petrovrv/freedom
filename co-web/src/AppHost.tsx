// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 shell, navigation F14e.1 (SPEC §3.6a п. 9): the apps of CO Web in the centre of the window. The open app has
// a bar on top — "← Chats | ▦ | its name" and the other recent apps — and Back of the browser or of Android, Esc and
// "← Chats" leave it. An app that is left is not destroyed: its iframe is hidden and keeps its state, so switching back
// is instant; the last 3 (phone) or 5 (computer) stay alive (appStack.ts). No window manager of its own: on a computer
// the windows of CO Desktop are the windows of the system (F14e.2).
import { useEffect, useRef, useState } from 'react';
import { appKey, findApp, findPage, LAUNCHER_OPEN, mountOrder, type AppRef } from './appStack';
import type { ModuleInfo, ShellApp } from './extBridge';
import { ModuleFrame, type CoreCalls } from './ModuleSlot';

/** Icons of recent apps (the header on a computer, the app bar everywhere); the current one is marked. */
export function RecentApps({ modules, recent, current, onOpen, className = '' }: {
  modules: ModuleInfo[]; recent: AppRef[]; current: AppRef | null; onOpen: (a: ShellApp) => void; className?: string;
}) {
  const cur = current ? appKey(current) : '';
  const apps = recent.flatMap((r) => { const a = findApp(modules, r); return a ? [{ r, a }] : []; });
  if (!apps.length) return null;
  return (
    <nav className={`recent-apps ${className}`} aria-label="Recent apps">
      {apps.map(({ r, a }) => (
        <button key={appKey(r)} type="button" className={`recent-app ${appKey(r) === cur ? 'here' : ''}`} title={a.label}
          aria-label={a.label} aria-current={appKey(r) === cur ? 'page' : undefined} onClick={() => onOpen(a)}>
          <span aria-hidden="true">{a.icon}</span>
        </button>
      ))}
    </nav>
  );
}

export default function AppHost({ modules, env, live, current, recent, me, owner, makeCoreCalls, onExit, onOpen }: {
  modules: ModuleInfo[]; env: string; live: AppRef[]; current: AppRef | null; recent: AppRef[]; me?: string; owner: boolean;
  /** core calls of the shell for one page frame: each frame keeps its own session for its life */
  makeCoreCalls: () => CoreCalls | undefined; onExit: () => void; onOpen: (a: ShellApp) => void;
}) {
  const cur = current ? appKey(current) : '';
  // any ui.page lives here (an app of ▦ or a page linked from the side panel)
  const shown = current ? findPage(modules, current) : null;
  // the window title of CO Web comes back when no app is shown
  const baseTitle = useRef(document.title);
  const seq = useRef(new Map<string, number>());
  // a fresh frame for a page that reloaded itself and was closed ("Open it again")
  const [gen, setGen] = useState<Record<string, number>>({});
  // the session of core calls of every mounted frame (a new factory — another Drive root or environment — starts anew)
  const calls = useRef<{ make: typeof makeCoreCalls; byFrame: Map<string, CoreCalls | undefined> }>({ make: makeCoreCalls, byFrame: new Map() });
  if (calls.current.make !== makeCoreCalls) calls.current = { make: makeCoreCalls, byFrame: new Map() };
  const callsOf = (frameKey: string): CoreCalls | undefined => {
    const by = calls.current.byFrame;
    if (!by.has(frameKey)) by.set(frameKey, makeCoreCalls());
    return by.get(frameKey);
  };
  useEffect(() => {
    if (!current) document.title = baseTitle.current;
  }, [current]);
  // sessions of unloaded frames go with them (a page opened again starts a fresh session)
  const mounted = new Set(live.filter((a) => a.env === env).flatMap((a) => {
    const app = findPage(modules, a);
    return app ? [`${appKey(a)}/${app.m.version}/${app.ui.entry}/${gen[appKey(a)] || 0}`] : [];
  }));
  for (const k of [...calls.current.byFrame.keys()]) if (!mounted.has(k)) calls.current.byFrame.delete(k);
  if (!live.length && !current) return null;
  const others = current ? recent.filter((r) => r.env === current.env && appKey(r) !== cur) : [];
  return (
    <div className={`app-host ${current ? 'on' : ''}`}>
      {current && (
        <div className="app-bar">
          <button type="button" className="app-bar-back" title="Back to the chats (Esc)" onClick={onExit}>
            <span aria-hidden="true">←</span> <span className="app-bar-back-label">Chats</span>
          </button>
          <button type="button" className="app-bar-apps" title="Apps" aria-label="Apps" onClick={() => window.dispatchEvent(new Event(LAUNCHER_OPEN))}>▦</button>
          <span className="app-bar-title">
            {shown?.icon && <span className="app-bar-icon" aria-hidden="true">{shown.icon}</span>}
            <span className="app-bar-name">{shown ? shown.label : current.pageId}</span>
          </span>
          <RecentApps modules={modules} recent={others.slice(0, 4)} current={null} onOpen={onOpen} className="in-bar" />
        </div>
      )}
      {current && !shown && (
        <div className="empty">{modules.length ? 'This app is not available (the module is off or not installed).' : 'Loading…'}</div>
      )}
      {/* only pages of the current environment: the core calls of the shell (coreCalls) act in it */}
      {/* the frames never move in the DOM (a moved iframe reloads): order of first mount, not of recency */}
      {mountOrder(live.filter((a) => a.env === env), seq.current).map((a) => {
        const app = findPage(modules, a);
        if (!app) return null;
        const on = appKey(a) === cur;
        const frameKey = `${app.m.version}/${app.ui.entry}/${gen[appKey(a)] || 0}`;
        return (
          <div key={appKey(a)} className="app-slot" hidden={!on}>
            <ModuleFrame key={frameKey} m={app.m} ui={app.ui} me={me} owner={owner} mode="fill"
              coreCalls={callsOf(`${appKey(a)}/${frameKey}`)} extra={{ env: a.env, visible: on }} active={on} onEscape={onExit}
              onReopen={() => setGen((g) => ({ ...g, [appKey(a)]: (g[appKey(a)] || 0) + 1 }))} />
          </div>
        );
      })}
    </div>
  );
}
