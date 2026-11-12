// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: this browser as a device (lab/web-device/SPEC.md §9, §12) - Host tab section, Drive-for-desktop guide,
// confirmation dialog for agent file changes, and the hook that runs the worker loop.
import { useEffect, useState, useSyncExternalStore } from 'react';
import { getWebDev, subscribeWebDev, webdevSupported, insideDesktop, addFolder, removeFolder, setFolderAccess, renameDevice, reconnectFolders, revokeDevice, initWebDev } from './webdevStore';
import { startWorker, stopWorker, answerConfirm } from './webdevWorker';
import { dbWebdevMine } from './dbapi';
import { formatBytes, formatLocal } from './util';

type WebDevState = ReturnType<typeof getWebDev>;
type Folder = WebDevState['folders'][number];
type PendingReq = NonNullable<WebDevState['pending']>['req'];

const ONLINE_MS = 60_000;
const rowStyle = { display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', padding: '4px 0' } as const;
const preBase = { maxHeight: 200, overflow: 'auto', whiteSpace: 'pre-wrap', fontSize: '0.85em', padding: 6, margin: '2px 0 8px', borderRadius: 4 } as const;

// Current web device state from the store
export function useWebDev(): WebDevState {
  return useSyncExternalStore(subscribeWebDev, getWebDev);
}

// Starts the browser device worker for the signed-in user
export function useWebDeviceRunner(email: string | null): void {
  useEffect(() => {
    if (!email || insideDesktop() || !webdevSupported()) return;
    let alive = true;
    initWebDev(email).then(() => { if (alive) startWorker(); }).catch(() => undefined); // store records init errors
    return () => { alive = false; stopWorker(); };
  }, [email]);
}

function errText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

// Host tab section body; the caller renders the heading
export function WebDeviceSection() {
  const s = useWebDev();
  const [now, setNow] = useState(() => Date.now());
  const [busy, setBusy] = useState(false);
  const [localErr, setLocalErr] = useState<string | null>(null);
  const [nameDraft, setNameDraft] = useState(s.name);
  const [serverOnline, setServerOnline] = useState<boolean | null>(null); // webdev_mine: also when another tab runs the loop

  useEffect(() => { setNameDraft(s.name); }, [s.name]);
  useEffect(() => {
    const t = window.setInterval(() => setNow(Date.now()), 10_000);
    return () => window.clearInterval(t);
  }, []);
  useEffect(() => {
    if (!s.deviceId || insideDesktop()) return;
    let alive = true;
    const load = () => dbWebdevMine()
      .then((list) => { if (alive) setServerOnline(list.find((d) => d.deviceId === s.deviceId)?.online ?? false); })
      .catch(() => { if (alive) setServerOnline(null); });
    void load();
    const t = window.setInterval(load, 20_000);
    return () => { alive = false; window.clearInterval(t); };
  }, [s.deviceId, s.folders.length]);

  if (insideDesktop()) return <p className="muted small">CO Desktop is a device itself; this section is for browsers.</p>;
  if (!webdevSupported()) return <p className="muted small">This browser cannot share folders (needs Chrome or Edge on a computer). Use Google Drive for desktop instead - see below.</p>;

  // Wraps async actions with busy flag and local error
  const run = async (fn: () => unknown) => {
    setBusy(true);
    setLocalErr(null);
    try { await fn(); } catch (e) { setLocalErr(errText(e)); } finally { setBusy(false); }
  };

  const localOnline = !!s.lastOkUtc && now - Date.parse(s.lastOkUtc) < ONLINE_MS;
  const online = (s.runner && localOnline) || serverOnline === true;
  const status = (online ? 'online' : 'offline') + (s.runner ? ' · this tab runs it' : online ? ' · another CO tab runs it' : '');
  const trimmed = nameDraft.trim();
  const nameChanged = trimmed !== '' && trimmed !== s.name;
  const needsReconnect = s.folders.some((f: Folder) => f.permission !== 'granted');
  const canNotify = typeof Notification !== 'undefined' && Notification.permission === 'default';

  const onRemove = (f: Folder) => {
    if (window.confirm(`Remove folder ${f.name}? Agents lose access to it.`)) void run(() => removeFolder(f.id));
  };
  const onRevoke = () => {
    if (window.confirm('Revoke this browser as a device? Pending agent calls fail and the folder list is cleared.')) void run(() => revokeDevice());
  };
  const onNotify = () => run(async () => { await Notification.requestPermission(); setNow(Date.now()); });

  return (
    <div>
      <p className="muted small">Agents in your chats can read and, after your confirmation, change files in folders you add here, while a CO tab is open in this browser.</p>
      <dl className="kv">
        <dt>Name</dt>
        <dd>
          <input type="text" value={nameDraft} maxLength={120} disabled={busy} onChange={(e) => setNameDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter' && nameChanged) void run(() => renameDevice(trimmed)); }} />
          {nameChanged && <button className="btn tiny" disabled={busy} onClick={() => run(() => renameDevice(trimmed))}>Save</button>}
        </dd>
        <dt>Device</dt>
        <dd>{s.short} · <code>{s.deviceId}</code></dd>
        <dt>Status</dt>
        <dd>{s.folders.length ? status : 'add a folder to start'}</dd>
        {s.lastOkUtc && <><dt>Last contact</dt><dd>{formatLocal(s.lastOkUtc)}</dd></>}
      </dl>
      {s.error && <p className="error-text small">{s.error}</p>}
      {s.folders.length === 0 ? <p className="muted small">No folders yet.</p> : (
        <ul style={{ listStyle: 'none', padding: 0, margin: '8px 0' }}>
          {s.folders.map((f: Folder) => (
            <li key={f.id} style={rowStyle}>
              <strong>{f.name}</strong>
              <span className="muted small">{f.id}</span>
              <button className={'perm-chip ' + (f.access === 'write' ? 'on' : 'off')} disabled={busy}
                title={f.access === 'write' ? 'Read and write; click for read only' : 'Read only; click to allow writing'}
                onClick={() => run(() => setFolderAccess(f.id, f.access === 'write' ? 'read' : 'write'))}>{f.access === 'write' ? 'rw' : 'ro'}</button>
              {f.permission !== 'granted' && <span className="error-text tiny">needs reconnect</span>}
              <button className="btn tiny ghost" disabled={busy} onClick={() => onRemove(f)}>Remove</button>
            </li>
          ))}
        </ul>
      )}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <button className="btn small primary" disabled={busy} onClick={() => run(() => addFolder())}>Add folder</button>
        {needsReconnect && <button className="btn small" disabled={busy} onClick={() => run(() => reconnectFolders())}>Reconnect folders</button>}
        {canNotify && <button className="btn small ghost" disabled={busy} onClick={onNotify}>Enable notifications</button>}
        <button className="btn small danger" disabled={busy} onClick={onRevoke}>Revoke device</button>
      </div>
      {localErr && <p className="error-text small">{localErr}</p>}
    </div>
  );
}

// Alternative setup through Google Drive for desktop
export function DriveDesktopGuide() {
  return (
    <>
      <p className="muted small">Works everywhere, including Safari and phones; files sync through Google Drive.</p>
      <ol className="small">
        <li>Install <a href="https://www.google.com/drive/download/" target="_blank" rel="noreferrer">Google Drive for desktop</a>.</li>
        <li>Put the files you want agents to use into a folder in your Google Drive.</li>
        <li>Share that folder with the workbench owner's account as Editor.</li>
        <li>The owner adds a shortcut to it in My Drive.</li>
        <li>Connect it in Project → Context folders.</li>
      </ol>
    </>
  );
}

const TITLES: Record<PendingReq['kind'], string> = {
  write: 'Allow writing a file?',
  edit: 'Allow editing a file?',
  mkdir: 'Allow creating a folder?',
  delete: 'Delete?',
  move: 'Move?',
};

// Request details per kind
function ConfirmBody({ req }: { req: PendingReq }) {
  switch (req.kind) {
    case 'write':
      return (
        <>
          <p><code>{req.path}</code>{req.size != null && <> · {formatBytes(req.size)}</>}</p>
          {req.replaces && <p className="error-text small">The existing file is replaced.</p>}
        </>
      );
    case 'edit':
      return (
        <>
          <p><code>{req.path}</code></p>
          <div className="small">Remove</div>
          <pre style={{ ...preBase, background: 'rgba(220,0,0,.08)' }}>{req.before ?? ''}</pre>
          <div className="small">Insert</div>
          <pre style={{ ...preBase, background: 'rgba(0,160,0,.08)' }}>{req.after ?? ''}</pre>
        </>
      );
    case 'mkdir':
      return <p><code>{req.path}</code></p>;
    case 'delete': {
      const n = req.items ?? 1;
      return n === 1 ? <p>Delete <code>{req.path}</code></p> : <p>Delete {n} files in <code>{req.path}</code></p>;
    }
    case 'move':
      return (
        <>
          <p>Move <code>{req.path}</code> → <code>{req.to ?? ''}</code></p>
          {req.replaces && <p className="error-text small">The existing file at the target is replaced.</p>}
        </>
      );
    default:
      return null;
  }
}

// Modal asking the user to confirm a pending agent file operation
export function WebDevConfirmDialog() {
  const { pending } = useWebDev();
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!pending) return;
    setNow(Date.now());
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, [pending]);

  useEffect(() => {
    if (!pending) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); answerConfirm('deny'); } };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [pending]);

  if (!pending) return null;
  const { req, deadline } = pending;
  const left = Math.max(0, Math.ceil((deadline - now) / 1000));
  const destructive = req.kind === 'delete' || req.kind === 'move';

  return (
    <div className="modal-back">
      <div className="modal wide" role="dialog" aria-modal="true" aria-labelledby="webdev-confirm-title">
        <h3 id="webdev-confirm-title">{TITLES[req.kind]}</h3>
        <ConfirmBody req={req} />
        <p className="muted small">{`An agent asks from a chat run. No answer in ${left} s → denied.`}</p>
        <div className="modal-actions">
          {destructive ? (
            <>
              <button className="btn ghost" autoFocus onClick={() => answerConfirm('deny')}>Cancel</button>
              <button className="btn danger" onClick={() => answerConfirm('allow')}>{req.kind === 'delete' ? 'Delete' : 'Move'}</button>
            </>
          ) : (
            <>
              <button className="btn ghost" onClick={() => answerConfirm('deny')}>Deny</button>
              <button className="btn" onClick={() => answerConfirm('run')}>Allow for this run</button>
              <button className="btn primary" autoFocus onClick={() => answerConfirm('allow')}>Allow</button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
