// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.63.0: canvas launcher (SPEC.md of chat CHAT-20261028152233-8P34, stage M2). /canvas?path=<file relative to
// AI-Workbench>: the site checks the role and signs a 120 s ticket (db.php op canvas_ticket), then this tab moves to the
// editor page of canvas-bridge on the home PC (<ts.net>/cb/edit#<ticket>), where ONLYOFFICE opens the file and saves
// edits back to Drive. The editor is not embedded here: on tailnet devices ts.net is a local address, and Chrome
// blocks the CO site from reaching it (Local Network Access); a top-level navigation is allowed.
import { useEffect, useState } from 'react';
import { getToken } from './auth';

type Phase = { s: 'loading' } | { s: 'error'; message: string; offline?: string };

export default function Canvas() {
  const path = new URLSearchParams(window.location.search).get('path') || '';
  const name = path.split('/').pop() || 'Canvas';
  const [phase, setPhase] = useState<Phase>({ s: 'loading' });

  useEffect(() => {
    document.title = `${name} · CO canvas`;
    let gone = false;
    (async () => {
      if (!path) throw new Error('No file given.');
      const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
      const token = getToken();
      if (token) headers.Authorization = `Bearer ${token}`;
      const res = await fetch('/agent/db.php', {
        method: 'POST',
        headers,
        credentials: 'same-origin',
        cache: 'no-store',
        body: JSON.stringify({ op: 'canvas_ticket', path }),
      });
      const data = await res.json().catch(() => ({}));
      if (res.status === 403) throw new Error('You have no access to this file.');
      if (res.status === 404) throw new Error('File not found.');
      if (!res.ok || !data.ticket) throw new Error(`The site refused the canvas (${data.error || res.status}).`);
      if (gone) return;
      if (!data.online) {
        // The site's probe may fail while this device still reaches the PC (tailnet): offer to try anyway.
        setPhase({ s: 'error', offline: `${data.editUrl}#${data.ticket}`, message: 'Canvas works when the home computer is on. The site cannot reach it now.' });
        return;
      }
      window.location.replace(`${data.editUrl}#${data.ticket}`);
    })().catch((e) => {
      if (!gone) setPhase({ s: 'error', message: (e as Error).message });
    });
    return () => {
      gone = true;
    };
  }, [path, name]);

  return (
    <div className="login">
      <div className="login-card">
        <div className="brand">CO</div>
        <p className="muted" title={path}>{name}</p>
        {phase.s === 'loading' ? (
          <p className="muted">Opening the canvas…</p>
        ) : (
          <>
            <p className={phase.offline ? 'muted' : 'error-text'}>{phase.message}</p>
            {phase.offline && (
              <>
                <p><button className="btn" onClick={() => window.location.replace(phase.offline!)}>Try anyway</button></p>
                <p className="tiny muted">Or use “Open in Google Docs” from the file menu.</p>
              </>
            )}
          </>
        )}
      </div>
    </div>
  );
}
