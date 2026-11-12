// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.68.0: Share from the canvas (SPEC.md of chat CHAT-20261028152233-8P34, stage M5). The editor page of
// canvas-bridge puts the prepared file (docx without agent threads, or PDF) on the site and opens
// /canvas-share?id=<id> here. The site makes a Gmail draft with this browser's own CO session (op canvas_share_draft,
// scope gmail.compose; asked once through the sign-in page) and this tab moves to the draft. Nothing is ever sent.
import { useEffect, useState } from 'react';
import { getToken } from './auth';

type Phase = { s: 'loading' } | { s: 'allow'; email: string } | { s: 'error'; message: string };

const ASKED = 'co.canvasShare.askedGmail';

export default function CanvasShare() {
  const id = new URLSearchParams(window.location.search).get('id') || '';
  const [phase, setPhase] = useState<Phase>({ s: 'loading' });

  const allow = (email: string) => {
    sessionStorage.setItem(ASKED, id);
    const q = new URLSearchParams({ add: 'gmail', hint: email, back: `/canvas-share?id=${id}` });
    window.location.assign(`/auth/login.php?${q}`);
  };

  useEffect(() => {
    document.title = 'Gmail draft · CO';
    let gone = false;
    let timer: number | undefined;
    const run = async (tries: number): Promise<void> => {
      if (!/^[0-9a-f]{32}$/.test(id)) throw new Error('This share link is not valid.');
      const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
      const token = getToken();
      if (token) headers.Authorization = `Bearer ${token}`;
      const res = await fetch('/agent/db.php', {
        method: 'POST',
        headers,
        credentials: 'same-origin',
        cache: 'no-store',
        body: JSON.stringify({ op: 'canvas_share_draft', id }),
      });
      const d = await res.json().catch(() => ({}));
      if (gone) return;
      if (d.error === 'uncertain') {
        // an earlier try may have made the draft already: look in Drafts instead of making a second one
        window.location.replace(`https://mail.google.com/mail/?${new URLSearchParams({ authuser: String(d.email || '') })}#drafts`);
        return;
      }
      if (res.status === 409 && tries < 20) {
        timer = window.setTimeout(() => void run(tries + 1).catch(fail), 1500); // the same share in another tab
        return;
      }
      if (res.status === 401 && d.error === 'need_gmail') {
        // First use: Google asks once whether CO may create Gmail drafts. Back here after it; if the box was left
        // unticked, the page offers the button instead of sending the user round in circles.
        if (sessionStorage.getItem(ASKED) !== id) return allow(String(d.email || ''));
        setPhase({ s: 'allow', email: String(d.email || '') });
        return;
      }
      if (res.status === 404) throw new Error('This share has expired. Press Share in the canvas again.');
      if (res.status === 403) throw new Error('This attachment was prepared for another CO account.');
      if (!res.ok || !d.draftId) throw new Error(`Gmail did not take the draft (${d.error || res.status}${d.status ? ' ' + d.status : ''}).`);
      sessionStorage.removeItem(ASKED);
      const q = new URLSearchParams({ authuser: String(d.email || '') });
      window.location.replace(`https://mail.google.com/mail/?${q}#drafts?compose=${encodeURIComponent(d.messageId || '')}`);
    };
    const fail = (e: unknown) => {
      if (!gone) setPhase({ s: 'error', message: (e as Error).message });
    };
    run(0).catch(fail);
    return () => {
      gone = true;
      window.clearTimeout(timer);
    };
  }, [id]);

  return (
    <div className="login">
      <div className="login-card">
        <div className="brand">CO</div>
        {phase.s === 'loading' && <p className="muted">Making the Gmail draft…</p>}
        {phase.s === 'allow' && (
          <>
            <p className="muted">CO needs your permission to create Gmail drafts. It never sends mail: the draft waits in Gmail for you.</p>
            <p><button className="btn primary" onClick={() => allow(phase.email)}>Allow Gmail drafts</button></p>
            <p className="tiny muted">On the Google page, tick “Manage drafts and send emails”: CO uses it only to create the draft.</p>
          </>
        )}
        {phase.s === 'error' && <p className="error-text">{phase.message}</p>}
      </div>
    </div>
  );
}
