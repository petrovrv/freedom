// SPDX-License-Identifier: AGPL-3.0-or-later
// F15.2 part 4 (SPEC §3.6b п. 2): the context switches of folders in the Properties. The chat folder's switch is rendered
// by ChatView into the chat's Properties (ChatView owns the chat's settings, so chat.json has one writer); the project
// folder's switch by the project's Properties. Logic: folderContext.ts.
import { useState } from 'react';
import { FOLDER_CTX_HINT, FOLDER_CTX_LABEL, PROJECT_CTX_HINT, folderCtxOf, folderLevels, projectCtxOf, type FolderCtx, type ProjectCtx } from './folderContext';
import { CONTEXT_MODE_LABEL } from './contextLevels';
import { placeParent } from './chatTree';
import { categorize } from './errors';
import { saveProjectMeta } from './workbench';
import type { Chat, Project } from './types';

// F15.2 part 4: the context switch of this chat as a folder, and what the chat itself gets from the folders above it
export function FolderCtxSection({ chat, chats, project, readOnly, onSet }: {
  chat: Chat; chats: Chat[] | null; project: Project; readOnly: boolean; onSet: (v: FolderCtx) => Promise<void>;
}) {
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = folderCtxOf(chat);
  const levels = folderLevels(chat, chats || []);
  const cmode = chat.contextMode || 'full';
  const pctx = projectCtxOf(project);
  const inside = (chats || []).filter((c) => placeParent(c) === chat.chatId && !c.archived).length;
  return (
    <div className="small folder-ctx">
      <label className="fc-row">
        <span>For the chats inside it</span>
        <select value={set} disabled={readOnly || busy} title={FOLDER_CTX_HINT[set]}
          onChange={async (e) => {
            setBusy(true);
            setErr(null);
            try {
              await onSet(e.target.value as FolderCtx);
            } catch (x) {
              setErr(categorize(x).message);
            } finally {
              setBusy(false);
            }
          }}>
          {(Object.keys(FOLDER_CTX_LABEL) as FolderCtx[]).map((k) => <option key={k} value={k} title={FOLDER_CTX_HINT[k]}>{FOLDER_CTX_LABEL[k]}</option>)}
        </select>
      </label>
      <div className="muted tiny">{FOLDER_CTX_HINT[set]} {inside ? `${inside} chat${inside === 1 ? '' : 's'} inside now.` : 'No chats inside yet.'}</div>
      {err && <div className="error-text tiny">{err}</div>}
      <div className="fc-gets">
        <b>This chat gets</b> in a new agent session
        {cmode !== 'full' ? (
          <div className="muted">{CONTEXT_MODE_LABEL[cmode]}: no project and no folders above (the context mode of the chat, in Settings above).</div>
        ) : (
          <ul>
            <li>Project «{project.title}»: {pctx === 'full' ? 'full' : pctx === 'summary' ? 'summary' : 'off'}</li>
            {levels.map((l) => (
              <li key={l.chat.chatId}>
                «{l.chat.title || l.chat.chatId}»: {l.lineage ? 'the messages this chat started from' : l.mode}
                {!l.lineage && l.set === 'auto' ? <span className="muted"> (automatic, {l.depth === 1 ? 'nearest' : `${l.depth} up`})</span> : null}
              </li>
            ))}
            {!levels.length && <li className="muted">no chat folders above it</li>}
          </ul>
        )}
      </div>
    </div>
  );
}

// F15.2 part 4: the project folder's switch (instructions and memory for its chats)
export function ProjectCtxSwitch({ project, onProjectChanged, onError }: { project: Project; onProjectChanged: (p: Project) => void; onError: (m: string) => void }) {
  const [busy, setBusy] = useState(false);
  const v = projectCtxOf(project);
  return (
    <div className="small folder-ctx">
      <label className="fc-row">
        <span>Instructions and memory</span>
        <select value={v} disabled={busy || !!project.shareRole} title={PROJECT_CTX_HINT[v]}
          onChange={async (e) => {
            setBusy(true);
            try {
              onProjectChanged(await saveProjectMeta(project, { folderContext: e.target.value as ProjectCtx }));
            } catch (x) {
              onError(categorize(x).message);
            } finally {
              setBusy(false);
            }
          }}>
          <option value="full" title={PROJECT_CTX_HINT.full}>Full</option>
          <option value="summary" title={PROJECT_CTX_HINT.summary}>Summary</option>
          <option value="off" title={PROJECT_CTX_HINT.off}>Off</option>
        </select>
      </label>
      <div className="muted tiny">{PROJECT_CTX_HINT[v]} A chat in Reference or Clean view gets none of it anyway.</div>
    </div>
  );
}

