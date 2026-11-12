// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.84.0: offer of CO Resident for macOS (agent/mac.php). Shown by itself to the members (not the owner) on a Mac (not on
// iPad, not inside CO Desktop) until installed or dismissed; anyone on a Mac opens it from Host → CO Desktop.
// Consent box → one-time Terminal command (curl … | bash, link valid 30 min, one download).
import { useCallback, useEffect, useState } from 'react';
import { getToken } from './auth';
import { ownerName } from './members';

type MacState = {
  env: string;
  eligible: boolean;
  release: { version: string; size: number; publishedUtc: string } | null;
  never: boolean;
  laterUntil: number;
  installedUtc: string;
  installedVersion: string;
  now: number;
};

const OPEN_EVENT = 'co:mac-offer';
const SESSION_KEY = 'co.macOfferShown';

export function isMacDesktop(): boolean {
  const ua = navigator.userAgent;
  if (!/Macintosh|Mac OS X/.test(ua) || /iPhone|iPad/.test(ua)) return false;
  if (navigator.maxTouchPoints > 1) return false; // iPadOS reports itself as a Mac
  return !(window as unknown as { coDesktop?: unknown }).coDesktop;
}

async function macApi<T>(body: object): Promise<T> {
  const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
  const token = getToken();
  if (token != null) headers.Authorization = `Bearer ${token}`;
  const res = await fetch('/agent/mac.php', {
    method: 'POST', headers, credentials: 'same-origin', cache: 'no-store', body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(res.status === 404 ? 'No release published yet.' : `Server answered ${res.status}.`);
  return (await res.json()) as T;
}

export function openMacOffer() {
  window.dispatchEvent(new Event(OPEN_EVENT));
}

// Mounted once in Shell: opens by itself when due, or on openMacOffer().
export default function MacOffer() {
  const [open, setOpen] = useState(false);
  const [state, setState] = useState<MacState | null>(null);

  useEffect(() => {
    const onOpen = () => setOpen(true);
    window.addEventListener(OPEN_EVENT, onOpen);
    return () => window.removeEventListener(OPEN_EVENT, onOpen);
  }, []);

  useEffect(() => {
    if (!isMacDesktop()) return;
    let active = true;
    macApi<MacState>({ op: 'state' })
      .then((s) => {
        if (!active) return;
        setState(s);
        const due = s.eligible && s.release != null && !s.never && s.now > s.laterUntil && !s.installedUtc;
        if (due && sessionStorage.getItem(SESSION_KEY) !== '1') {
          sessionStorage.setItem(SESSION_KEY, '1');
          setOpen(true);
        }
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, []);

  if (!open) return null;
  return <MacOfferDialog state={state} onClose={() => setOpen(false)} />;
}

function MacOfferDialog({ state, onClose }: { state: MacState | null; onClose: () => void }) {
  const [agree, setAgree] = useState(false);
  const [command, setCommand] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);

  const dismiss = useCallback(
    (mode: 'later' | 'never') => {
      macApi({ op: 'dismiss', mode }).catch(() => undefined);
      onClose();
    },
    [onClose],
  );

  const getCommand = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await macApi<{ command: string }>({ op: 'token' });
      setCommand(r.command);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const copy = async () => {
    if (!command) return;
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 2500);
    } catch {
      setError('Could not copy; select the command and press ⌘C.');
    }
  };

  const version = state?.release?.version;
  return (
    <div className="modal-back" onClick={onClose}>
      <div className="modal wide mac-offer" onClick={(e) => e.stopPropagation()} role="dialog" aria-label="CO Resident for Mac">
        <h3>CO helper for your Mac{version ? ` (v${version})` : ''}</h3>
        {state?.installedUtc && (
          <p className="muted small">Already installed on a Mac (v{state.installedVersion}). Install again only to update or repair it.</p>
        )}
        {!command ? (
          <>
            <p>
              A small background program for this Mac. With it, AI Workbench agents can work with files in one folder
              you choose and help you check and fix things on the Mac, for example when something does not work.
            </p>
            <ul className="mac-offer-list">
              <li>Agents see only the folder <code>~/CO</code> (and folders you add yourself).</li>
              <li>
                Every command on the Mac first shows a window with the exact command — it runs only after you press
                <b> Run</b>. No answer = declined.
              </li>
              <li>
                Agents may open web pages in a browser window of their own (orange frame, without your sign-ins); forms
                and purchases ask you first. Downloaded on first use, about 170 MB.
              </li>
              <li>No administrator password. Switch commands or the browser off, or remove the helper, with one command.</li>
              <li>Only your Google account can connect this Mac; {ownerName()} has allowed it in advance, no code needed.</li>
            </ul>
            <label className="mac-offer-agree">
              <input type="checkbox" checked={agree} onChange={(e) => setAgree(e.target.checked)} />
              <span>I understand and allow installing the CO helper on this Mac.</span>
            </label>
            {error && <p className="error-text small">{error}</p>}
            <div className="modal-actions">
              <button className="btn ghost" onClick={() => dismiss('never')}>Don't offer again</button>
              <button className="btn ghost" onClick={() => dismiss('later')}>Later</button>
              <button className="btn primary" disabled={!agree || busy || state?.release === null} onClick={getCommand}>
                {busy ? 'Preparing…' : 'Get the install command'}
              </button>
            </div>
            {state?.release === null && <p className="muted small">No release is published yet.</p>}
          </>
        ) : (
          <>
            <ol className="mac-offer-steps">
              <li>
                Open <b>Terminal</b>: press <kbd>⌘</kbd> <kbd>Space</kbd>, type <i>Terminal</i>, press <kbd>Enter</kbd>.
              </li>
              <li>
                Copy this command, paste it into Terminal (<kbd>⌘</kbd> <kbd>V</kbd>) and press <kbd>Enter</kbd>:
                <pre className="mac-offer-cmd">{command}</pre>
                <button className="btn small" onClick={copy}>{copied ? 'Copied ✓' : 'Copy command'}</button>
              </li>
              <li>A Google page opens in the browser — sign in with your usual account and allow access.</li>
              <li>
                Wait 1–3 minutes: the Mac connects by itself and a notification says so. Only if a window with a
                6-digit code appears after 15 minutes, send that code to {ownerName()}.
              </li>
            </ol>
            <p className="muted small">
              The link works once and for 30 minutes. If macOS asks whether <i>co-resident</i> may show notifications,
              allow it.
            </p>
            {error && <p className="error-text small">{error}</p>}
            <div className="modal-actions">
              <button className="btn primary" onClick={onClose}>Done</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
