// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.128.0 (F15.2 part 4, SPEC §3.6b п. 1): what used to be the Files and Workspace tabs of the right panel is the
// content of the folders now. A chat folder holds the chat's files (version streams, FilesPanel.tsx), its scratch on
// Drive and the context folder chips; a project folder holds the project workspace. Shown under the items of the
// Explorer (and under the tree when the tree view is chosen); each part folds and loads only when unfolded.
import { useState, type ReactNode } from 'react';
import { FilesTab } from './FilesPanel';
import { setFilesSlot } from './chatPanel';
import { DriveTree } from './Properties';
import { chatScratchPath, type Root } from './workbench';
import type { Chat, OwnerInfo, Project } from './types';

export function Fold({ id, label, hint, children, defOpen = false }: { id: string; label: string; hint?: string; children: () => ReactNode; defOpen?: boolean }) {
  const key = `co.fold.${id}`;
  const [open, setOpen] = useState(() => {
    try {
      const v = localStorage.getItem(key);
      return v === null ? defOpen : v === '1';
    } catch {
      return defOpen;
    }
  });
  const toggle = () => {
    try {
      localStorage.setItem(key, open ? '0' : '1');
    } catch {
      /* storage unavailable */
    }
    setOpen(!open);
  };
  return (
    <div className="fold">
      <button type="button" className="fold-head" aria-expanded={open} title={hint} onClick={toggle}>
        <span aria-hidden="true">{open ? '▾' : '▸'}</span> {label}
      </button>
      {open && <div className="fold-body">{children()}</div>}
    </div>
  );
}

/** Files of the open chat: its files, the scratch folder on Drive and the context folder chips (ChatView portal). */
export function ChatFiles({ chat, project, root, authOk }: { chat: Chat; project: Project; root: Root | null; authOk: boolean }) {
  if (!root || !authOk || chat.draft) return null;
  const canWrite = chat.shareRole !== 'viewer' && project.shareRole !== 'viewer';
  return (
    <Fold id="chatFiles" label={`📄 Files of «${chat.title || chat.chatId}»`} hint="Files of this chat with their versions, its scratch folder and the context folders" defOpen>
      {() => (
        <>
          <FilesTab key={chat.folderId} chat={chat} root={root} canWrite={canWrite} />
          <div className="files-slot" ref={setFilesSlot} />
          <ChatScratch chat={chat} root={root} />
        </>
      )}
    </Fold>
  );
}

function ChatScratch({ chat, root }: { chat: Chat; root: Root }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="small">
      <button type="button" className="link" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? '▾' : '▸'} Scratch folder on Drive</button>
      {open && <DriveTree root={root} path={chatScratchPath(chat)} emptyText="Empty. Chat-mode agents work here." />}
    </div>
  );
}

/** The workspace of a project (its Drive folder when the home computer keeps it on Drive). */
export function ProjectWorkspace({ project, owner, root, authOk }: { project: Project; owner: OwnerInfo | null; root: Root | null; authOk: boolean }) {
  // a project shared with me has no workspace of mine
  if (!root || !authOk || project.shareRole) return null;
  const path = owner?.projects?.[project.projectId]?.workspaceDriveRelativePath || null;
  return (
    <Fold id="workspace" label="🗂 Workspace" hint="The working folder of the project">
      {() => (path
        ? <DriveTree key={path} root={root} path={path} />
        : <p className="muted small">The workspace is on the home computer, outside Google Drive. Changed files arrive as artifacts.</p>)}
    </Fold>
  );
}
