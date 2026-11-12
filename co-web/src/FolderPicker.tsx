// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from 'react';
import { FOLDER_MIME, ensureFolder, list, q, type DriveFile } from './drive';
import { categorize } from './errors';

interface Crumb {
  id: string;
  name: string;
}

const LAST_KEY = 'co.saveFolder';

function loadLast(): Crumb[] | null {
  try {
    const v = localStorage.getItem(LAST_KEY);
    return v ? (JSON.parse(v) as Crumb[]) : null;
  } catch {
    return null;
  }
}

function storeLast(path: Crumb[]) {
  try {
    localStorage.setItem(LAST_KEY, JSON.stringify(path));
  } catch {
    /* per-viewer convenience only */
  }
}

// Folder chooser over the user's My Drive, used by "Save to Drive".
export function FolderPicker({ onPick, onCancel }: { onPick: (f: { id: string; path: string }) => void; onCancel: () => void }) {
  const [path, setPath] = useState<Crumb[]>(() => loadLast() || [{ id: 'root', name: 'My Drive' }]);
  const [folders, setFolders] = useState<DriveFile[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [newName, setNewName] = useState('');
  const current = path[path.length - 1];

  useEffect(() => {
    setFolders(null);
    setErr(null);
    list(`${q(current.id)} in parents and mimeType=${q(FOLDER_MIME)} and trashed=false`, 'id,name,mimeType', 'name')
      .then(setFolders)
      .catch((e) => {
        // A remembered folder may have been deleted; fall back to My Drive.
        if (path.length > 1) setPath([{ id: 'root', name: 'My Drive' }]);
        else setErr(categorize(e).message);
      });
  }, [current.id]); // eslint-disable-line react-hooks/exhaustive-deps

  async function create() {
    const name = newName.trim();
    if (!name) return;
    try {
      const id = await ensureFolder(current.id, name);
      setNewName('');
      setPath([...path, { id, name }]);
    } catch (e) {
      setErr(categorize(e).message);
    }
  }

  const label = path.map((c) => c.name).join(' / ');

  return (
    <div className="modal-back" onClick={onCancel}>
      <div className="modal picker" onClick={(e) => e.stopPropagation()} role="dialog" aria-label="Choose a folder">
        <h3>Save to Google Drive</h3>
        <div className="crumbs">
          {path.map((c, i) => (
            <span key={c.id + i}>
              {i > 0 && ' / '}
              <button className="link" onClick={() => setPath(path.slice(0, i + 1))} disabled={i === path.length - 1}>
                {c.name}
              </button>
            </span>
          ))}
        </div>
        <ul className="folder-list">
          {folders === null && !err && <li className="muted small">Loading…</li>}
          {folders?.length === 0 && <li className="muted small">No subfolders.</li>}
          {folders?.map((f) => (
            <li key={f.id}>
              <button onClick={() => setPath([...path, { id: f.id, name: f.name }])}>📁 {f.name}</button>
            </li>
          ))}
        </ul>
        <div className="row">
          <input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="New folder name" aria-label="New folder name" />
          <button className="btn small" onClick={() => void create()} disabled={!newName.trim()}>Create</button>
        </div>
        {err && <p className="error-text small">{err}</p>}
        <div className="modal-actions">
          <button className="btn ghost" onClick={onCancel}>Cancel</button>
          <button
            className="btn primary"
            onClick={() => {
              storeLast(path);
              onPick({ id: current.id, path: label });
            }}
          >
            Save here
          </button>
        </div>
      </div>
    </div>
  );
}
