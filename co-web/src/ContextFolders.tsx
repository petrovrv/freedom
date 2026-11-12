// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from 'react';
import { categorize } from './errors';
import type { ContextFolder, FolderNode, OwnerInfo, Project } from './types';
import { expandFolder, readFolderIndex, requestFolderIndexRefresh, saveProjectMeta, type HostDirs } from './workbench';

// Project section: folders on the home computer or Google Drive connected as context.
// Nothing is copied: the agent gets the folder paths on the home computer.
export function ContextFoldersSection({
  project, owner, host, onChanged,
}: { project: Project; owner: OwnerInfo | null; host: HostDirs; onChanged: (p: Project) => void }) {
  const [adding, setAdding] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const folders = project.contextFolders || [];
  const roots = owner?.contextRoots || {};
  const supported = owner?.capabilities?.includes('context-folders');

  async function save(next: ContextFolder[]) {
    setErr(null);
    try {
      onChanged(await saveProjectMeta(project, { contextFolders: next }));
    } catch (e) {
      setErr(categorize(e).message);
    }
  }

  if (!supported) return <p className="muted tiny">The home agent does not support context folders (Bridge v0.7 needed).</p>;

  return (
    <div className="ctx">
      {folders.length === 0 && <p className="muted small">No folders connected.</p>}
      <ul className="ctx-list">
        {folders.map((f) => (
          <li key={f.id}>
            <div className="ctx-name" title={`${roots[f.rootId]?.label || f.rootId} / ${f.relativePath}`}>
              {roots[f.rootId]?.kind === 'drive' ? '☁' : '🖥'} {f.label}
            </div>
            <div className="ctx-row">
              <select
                value={f.access}
                onChange={(e) => void save(folders.map((x) => (x.id === f.id ? { ...x, access: e.target.value as ContextFolder['access'] } : x)))}
                aria-label="Access"
              >
                <option value="read">read-only</option>
                <option value="write">read-write</option>
              </select>
              <button className="link danger-text" onClick={() => void save(folders.filter((x) => x.id !== f.id))}>Disconnect</button>
            </div>
          </li>
        ))}
      </ul>
      <button className="btn small" onClick={() => setAdding(true)}>Connect folder…</button>
      <p className="muted tiny">
        The folder stays where it is. The agent reads it on the home computer; nothing is copied to AI-Workbench. Read-only is enforced by the
        Codex sandbox; for Claude it is an instruction.
      </p>
      {err && <p className="error-text small">{err}</p>}
      {adding && (
        <FolderBrowser
          owner={owner}
          host={host}
          onCancel={() => setAdding(false)}
          onPick={(rootId, relativePath, label, access) => {
            setAdding(false);
            const id = `${rootId}:${relativePath}`;
            if (folders.some((x) => x.id === id)) return;
            void save([...folders, { id, rootId, relativePath, label, access }]);
          }}
        />
      )}
    </div>
  );
}

function FolderBrowser({
  owner, host, onCancel, onPick,
}: {
  owner: OwnerInfo | null;
  host: HostDirs;
  onCancel: () => void;
  onPick: (rootId: string, relativePath: string, label: string, access: ContextFolder['access']) => void;
}) {
  const roots = Object.entries(owner?.contextRoots || {});
  const [rootId, setRootId] = useState(roots[0]?.[0] || '');
  const [tree, setTree] = useState<FolderNode | null>(null);
  const [generated, setGenerated] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [label, setLabel] = useState('');
  const [access, setAccess] = useState<ContextFolder['access']>('read');
  const [expanding, setExpanding] = useState<string | null>(null);
  const [refreshAsked, setRefreshAsked] = useState(false);

  useEffect(() => {
    if (!rootId) return;
    setLoading(true);
    setErr(null);
    setTree(null);
    setSelected(null);
    readFolderIndex(host, rootId)
      .then((idx) => {
        if (!idx) setErr('The home agent has not published this folder list yet. Try again in a minute.');
        else {
          setTree(idx.tree);
          setGenerated(idx.generatedUtc);
        }
      })
      .catch((e) => setErr(categorize(e).message))
      .finally(() => setLoading(false));
  }, [rootId, host]);

  async function loadMore(path: string) {
    setExpanding(path);
    setErr(null);
    try {
      const idx = await expandFolder(host, rootId, path);
      setTree((t) => (t ? graft(t, path.split('/').filter(Boolean), idx.tree.c || []) : t));
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setExpanding(null);
    }
  }

  const rootLabel = owner?.contextRoots?.[rootId]?.label || rootId;

  return (
    <div className="modal-back" onClick={onCancel}>
      <div className="modal picker wide" onClick={(e) => e.stopPropagation()} role="dialog" aria-label="Connect a folder">
        <h3>Connect a folder</h3>
        <div className="seg wrap" role="tablist">
          {roots.map(([id, r]) => (
            <button key={id} role="tab" aria-selected={id === rootId} className={id === rootId ? 'active' : ''} onClick={() => setRootId(id)}>
              {r.kind === 'drive' ? '☁ ' : '🖥 '}
              {r.label}
            </button>
          ))}
        </div>
        <div className="folder-list tree-box">
          {loading && <div className="muted small pad">Loading…</div>}
          {tree && (
            <ul className="tree">
              <li>
                <button className={`tree-pick ${selected === '' ? 'sel' : ''}`} onClick={() => { setSelected(''); setLabel(rootLabel); }}>
                  {rootLabel} (whole root)
                </button>
              </li>
              {(tree.c || []).map((n) => (
                <TreeNode key={n.n} node={n} path={n.n} selected={selected} expanding={expanding}
                  onSelect={(p, name) => { setSelected(p); setLabel(name); }} onMore={loadMore} />
              ))}
            </ul>
          )}
        </div>
        <div className="muted tiny">
          Folder list from the home computer{generated ? `, updated ${new Date(generated).toLocaleString()}` : ''}.{' '}
          <button className="link" disabled={refreshAsked} onClick={() => void requestFolderIndexRefresh(host).then(() => setRefreshAsked(true))}>
            {refreshAsked ? 'Refresh requested' : 'Refresh list'}
          </button>
        </div>
        {selected !== null && (
          <div className="row">
            <input value={label} onChange={(e) => setLabel(e.target.value)} aria-label="Label" placeholder="Label" />
            <select value={access} onChange={(e) => setAccess(e.target.value as ContextFolder['access'])} aria-label="Access">
              <option value="read">read-only</option>
              <option value="write">read-write</option>
            </select>
          </div>
        )}
        {err && <p className="error-text small">{err}</p>}
        <div className="modal-actions">
          <button className="btn ghost" onClick={onCancel}>Cancel</button>
          <button className="btn primary" disabled={selected === null} onClick={() => onPick(rootId, selected || '', label.trim() || rootLabel, access)}>
            Connect
          </button>
        </div>
      </div>
    </div>
  );
}

function TreeNode({
  node, path, selected, expanding, onSelect, onMore,
}: {
  node: FolderNode; path: string; selected: string | null; expanding: string | null;
  onSelect: (path: string, name: string) => void; onMore: (path: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const kids = node.c || [];
  const expandable = kids.length > 0 || node.m;
  return (
    <li>
      <div className="tree-row">
        <button className="folder tw" aria-expanded={open} disabled={!expandable} onClick={() => {
          if (!kids.length && node.m) onMore(path);
          setOpen(!open);
        }}>
          {expandable ? (open ? '▾' : '▸') : '·'}
        </button>
        <button className={`tree-pick ${selected === path ? 'sel' : ''}`} onClick={() => onSelect(path, node.n)}>📁 {node.n}</button>
        {expanding === path && <span className="spinner small" />}
      </div>
      {open && kids.length > 0 && (
        <ul className="tree">
          {kids.map((k) => (
            <TreeNode key={k.n} node={k} path={`${path}/${k.n}`} selected={selected} expanding={expanding} onSelect={onSelect} onMore={onMore} />
          ))}
        </ul>
      )}
    </li>
  );
}

// Inserts freshly loaded children at a path of the tree (immutable update).
function graft(node: FolderNode, parts: string[], children: FolderNode[]): FolderNode {
  if (!parts.length) return { ...node, c: children, m: false };
  return {
    ...node,
    c: (node.c || []).map((k) => (k.n === parts[0] ? graft(k, parts.slice(1), children) : k)),
  };
}
