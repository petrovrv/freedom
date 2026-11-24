// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.58.0: "⋯" menu of a project in the sidebar - the chat menu plus Merge and "as a chat with subchats".
import { useEffect, useState } from 'react';
import { categorize } from './errors';
import type { Chat, Project } from './types';
import { suggestProjectTitle } from './search';
import { allProjectChats, deleteProject, foldProjectIntoChat, mergeProject, saveProjectMeta, type HostDirs, type Root } from './workbench';

// 'title' = the rename dialog that starts with an AI-suggested title; 'fold' = move as a chat with subchats.
// 'mode:<key>' (F10) switches a chat mode of a module for new chats of the project (<key>Default), no dialog.
export type ProjectAction = 'rename' | 'title' | 'merge' | 'fold' | 'archive' | 'delete' | `mode:${string}`;

// Owner: everything; editor of a shared project: rename only; viewer: no menu.
export function projectActionsFor(p: Project, branched?: boolean, modeKeys: string[] = []): ProjectAction[] {
  const modes = modeKeys.map((k): ProjectAction => `mode:${k}`);
  if (p.shareRole === 'viewer') return [];
  if (p.shareRole === 'editor') return ['rename', 'title', ...modes];
  const all: ProjectAction[] = ['rename', 'title', ...modes, 'merge', 'fold', 'archive', 'delete'];
  return branched ? all.filter((a) => a !== 'fold') : all;
}

const LABEL: Record<Exclude<ProjectAction, 'archive' | `mode:${string}`>, string> = {
  rename: 'Rename',
  title: 'Suggest title',
  merge: 'Merge with project…',
  fold: 'Move to project as a chat with subchats…',
  delete: 'Delete',
};

export function ProjectActionDialog({
  action, project, root, hosts, projects, onClose, onDone,
}: {
  action: ProjectAction;
  project: Project;
  root: Root;
  hosts: Record<string, HostDirs>;
  projects: Project[];
  onClose: () => void;
  onDone: (r: { project?: Project; deleted?: boolean; movedTo?: Project; hub?: Chat }) => void;
}) {
  const [title, setTitle] = useState(project.title);
  const targets = projects.filter((p) => p.projectId !== project.projectId && !p.shareRole && !p.archived);
  const [target, setTarget] = useState(targets[0]?.projectId || '');
  const [chats, setChats] = useState<Chat[] | null>(null);
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [suggesting, setSuggesting] = useState(false);
  const renaming = action === 'rename' || action === 'title';
  const moving = action === 'merge' || action === 'fold';
  const branched = !!chats?.some((c) => c.parent);

  useEffect(() => {
    void (async () => {
      try {
        const list = await allProjectChats(project);
        setChats(list);
        if (action === 'title') await suggest(list);
      } catch (e) {
        setErr(categorize(e).message);
      }
    })();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function suggest(list = chats || []) {
    setSuggesting(true);
    setErr(null);
    try {
      setTitle(await suggestProjectTitle(project, list));
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setSuggesting(false);
    }
  }

  async function run() {
    setBusy(true);
    setErr(null);
    const step = (d: number, t: number) => setProgress(`${d} / ${t}`);
    try {
      if (renaming) {
        const t = title.trim();
        if (!t) throw new Error('Title cannot be empty.');
        onDone({ project: await saveProjectMeta(project, { title: t }) });
      } else if (moving) {
        const p = targets.find((x) => x.projectId === target);
        if (!p) throw new Error('Choose a project.');
        if (action === 'merge') {
          await mergeProject(root, project, p, step);
          onDone({ project: { ...project, archived: true }, movedTo: p });
        } else {
          const hub = await foldProjectIntoChat(root, project, p, step);
          onDone({ project: { ...project, archived: true }, movedTo: p, hub });
        }
      } else {
        await deleteProject(root, project, hosts, step);
        onDone({ deleted: true });
      }
    } catch (e) {
      setErr(categorize(e).message);
      setBusy(false);
    }
  }

  const n = chats === null ? '…' : String(chats.length);
  const blocked =
    busy || suggesting ||
    (renaming && !title.trim()) ||
    (moving && (!target || chats === null)) ||
    (action === 'fold' && branched) ||
    (action === 'delete' && (chats === null || confirm.trim() !== project.title.trim()));

  return (
    <div className="modal-back" onClick={busy ? undefined : onClose}>
      <form
        className="modal"
        onClick={(e) => e.stopPropagation()}
        onSubmit={(e) => {
          e.preventDefault();
          void run();
        }}
      >
        {renaming && (
          <>
            <h3>Rename project</h3>
            <label>
              Title
              <span className="row-gap">
                <input value={title} onChange={(e) => setTitle(e.target.value)} disabled={suggesting} autoFocus style={{ flex: 1 }} />
                <button type="button" className="btn small" onClick={() => void suggest()} disabled={suggesting || chats === null} title="Claude Haiku reads the chat titles and proposes a project title">
                  {suggesting ? 'Thinking…' : '✨ Suggest'}
                </button>
              </span>
            </label>
            <p className="hint">Only the title changes; the project id <code>{project.projectId}</code> and its folder stay.</p>
          </>
        )}
        {moving && (
          <>
            <h3>{action === 'merge' ? `Merge “${project.title}” into…` : `Move “${project.title}” as a chat`}</h3>
            {targets.length ? (
              <label>
                Target project
                <select value={target} onChange={(e) => setTarget(e.target.value)} disabled={busy}>
                  {targets.map((p) => <option key={p.projectId} value={p.projectId}>{p.title}</option>)}
                </select>
              </label>
            ) : (
              <p className="muted">No other project in this environment.</p>
            )}
            {action === 'merge' ? (
              <p className="hint">
                All {n} chats (archived ones too) move to the target project with history, files and chat trees. Instructions,
                memory and files of “{project.title}” are not merged: it stays as an empty archived project.
              </p>
            ) : branched ? (
              <p className="error-text small">This project has branched chats (chat trees). Use Merge with project instead.</p>
            ) : (
              <p className="hint">
                The target gets one chat “📁 {project.title}”; all {n} chats become its subchats. Instructions, memory and files of
                the project are copied to that chat (scratch/project/). “{project.title}” stays as an empty archived project.
              </p>
            )}
          </>
        )}
        {action === 'delete' && (
          <>
            <h3>Delete project “{project.title}”?</h3>
            <p className="hint">
              The project folder with all {n} chats goes to the Google Drive trash (restorable there for 30 days, without the
              messages); the messages leave the database for good. Stop running jobs first. Archive keeps everything.
            </p>
            <label>
              Type the project title to confirm
              <input value={confirm} onChange={(e) => setConfirm(e.target.value)} placeholder={project.title} autoFocus />
            </label>
          </>
        )}
        {busy && progress && <p className="muted small">Chats: {progress}</p>}
        {err && <p className="error-text small">{err}</p>}
        <div className="modal-actions">
          <button type="button" className="btn ghost" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className={`btn ${action === 'delete' ? 'danger' : 'primary'}`} disabled={blocked}>
            {busy ? 'Working…' : renaming ? 'Rename' : action === 'merge' ? 'Merge' : action === 'fold' ? 'Move' : 'Delete project'}
          </button>
        </div>
      </form>
    </div>
  );
}

function modeLabel(modes: { key: string; label: string; on: boolean }[], key: string): string {
  const m = modes.find((x) => x.key === key);
  return m ? (m.on ? `${m.label}: on (turn off)` : m.label) : key;
}

// 0.128.0: onProperties - the first item "Properties" (the project folder's Properties), also for a shared project
type ProjectMenuProps = { onPick: (a: ProjectAction) => void; archived?: boolean; modes?: { key: string; label: string; on: boolean }[]; actions: ProjectAction[]; onProperties?: () => void };

export function ProjectMenu(props: ProjectMenuProps) {
  if (!props.actions.length && !props.onProperties) return null;
  return <ProjectMenuInner {...props} />;
}

function ProjectMenuInner({ onPick, archived, modes = [], actions, onProperties }: ProjectMenuProps) {
  const [open, setOpen] = useState(false);
  return (
    <span className="chat-menu" onClick={(e) => e.stopPropagation()}>
      <button className="icon-btn small" aria-label="Project actions" aria-expanded={open} onClick={() => setOpen(!open)}>⋯</button>
      {open && (
        <span className="menu" role="menu" onMouseLeave={() => setOpen(false)}>
          {onProperties && <button role="menuitem" onClick={() => { setOpen(false); onProperties(); }}>Properties</button>}
          {actions.map((a) => (
            <button
              key={a}
              role="menuitem"
              className={a === 'delete' ? 'danger-text' : ''}
              onClick={() => {
                setOpen(false);
                onPick(a);
              }}
            >
              {a === 'archive' ? (archived ? 'Restore from archive' : 'Archive') : a.startsWith('mode:') ? modeLabel(modes, a.slice(5)) : LABEL[a as keyof typeof LABEL]}
            </button>
          ))}
        </span>
      )}
    </span>
  );
}
