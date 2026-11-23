// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6 ui.page): a full-window page of a module, at /x/<module>/<page> or at an address the module
// declared (ui.page path, e.g. a link that existed before the feature became a module). Every other address that no
// module claims goes to the home screen, as before. The query of the address goes to the page as ctx.params.
import { useEffect, useState } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { dbModules } from './dbapi';
import type { ModuleInfo, ModuleUi } from './extBridge';
import { ModuleFrame } from './ModuleSlot';
import { shellRoute } from './appStack';
import { setModuleList } from './moduleStore';

export function modulePageFor(modules: ModuleInfo[], pathname: string): { m: ModuleInfo; ui: ModuleUi } | null {
  const x = /^\/x\/([a-z0-9][a-z0-9-]{0,62})\/([A-Za-z0-9._-]{1,40})\/?$/.exec(pathname);
  for (const m of modules) {
    if (!m.enabled) continue;
    for (const ui of m.ui) {
      if (ui.slot !== 'ui.page') continue;
      if (x ? x[1] === m.id && x[2] === ui.id : ui.path === pathname) return { m, ui };
    }
  }
  return null;
}

/** The address of a module page with its parameters (the declared path when there is one). */
export function modulePageHref(m: Pick<ModuleInfo, 'id'>, ui: Pick<ModuleUi, 'id' | 'path'>, params: Record<string, string> = {}): string {
  const q = new URLSearchParams(params).toString();
  return `${ui.path || `/x/${m.id}/${ui.id}`}${q ? `?${q}` : ''}`;
}

export default function ModulePage({ home, me, owner }: { home: string; me?: string; owner: boolean }) {
  const { pathname, search } = useLocation();
  const [modules, setModules] = useState<ModuleInfo[] | null>(null);
  const root = pathname === '/' || pathname === '';
  useEffect(() => {
    if (root) return;
    let gone = false;
    dbModules().then(
      (l) => { if (!gone) { setModules(l); setModuleList(l); } },
      () => { if (!gone) setModules([]); },
    );
    return () => { gone = true; };
  }, [root]);
  if (root) return <Navigate to={home} replace />;
  if (modules === null) return <div className="login"><p className="muted">Opening…</p></div>;
  const hit = modulePageFor(modules, pathname);
  if (!hit) return <Navigate to={home} replace />;
  // F14e.1: a page the shell can show opens inside it (app bar, Back/Esc, "← Chats"); the bare page stays for a query
  // and for a desktop app's own window (?window=1)
  const inShell = shellRoute(`/x/${hit.m.id}/${hit.ui.id}`, search, me || '');
  if (inShell) return <Navigate to={inShell} replace />;
  const params = Object.fromEntries(new URLSearchParams(search));
  return (
    <div className="module-page">
      <ModuleFrame key={`${hit.m.id}/${hit.ui.id}/${hit.m.version}/${search}`} m={hit.m} ui={hit.ui} me={me} owner={owner} mode="fill" extra={{ uiId: hit.ui.id, params }} />
    </div>
  );
}
