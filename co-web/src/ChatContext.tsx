// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.129.0 (SPEC §3.6b, «Папка чата» → Context): what a new agent session of the open chat gets, one line per source
// (chatFolder.ts contextItems, the same rules as ChatView.sendToChat). Rendered by ChatView into the Context part of the
// chat folder (chatPanel ctxListSlot), so the chat's context mode is written by the one writer of chat.json. What has
// its own switch says where it is; the rest is shown read only.
import { useEffect, useState } from 'react';
import type { Chat, Project } from './types';
import { dbProtocol } from './dbapi';
import { categorize } from './errors';
import { contextItems, type ContextInput } from './chatFolder';
import { CONTEXT_MODE_HINT, CONTEXT_MODE_LABEL, type ContextMode } from './contextLevels';
import { folderLevels, projectCtxOf } from './folderContext';
import { openPanel } from './chatPanel';

const STATE_MARK = { on: '✓', summary: '½', off: '–' } as const;
const STATE_WORD = { on: 'included', summary: 'summary', off: 'not included' } as const;

/** The lineage parent's title when the chat starts from its messages (a subtask from a prompt of its own does not). */
export function lineageTitleOf(chat: Chat, chats: Chat[] | null): string | null {
  const p = chat.parent;
  if (!p || (p.kind === 'subtask' && p.seq === 0)) return null;
  const c = (chats || []).find((x) => x.chatId === p.chatId);
  return (c?.title || p.chatId).trim();
}

export function contextInputOf(chat: Chat, project: Project, chats: Chat[] | null, protocolEntries: number | null, shared: boolean): ContextInput {
  return {
    mode: chat.contextMode || 'full',
    projectCtx: projectCtxOf(project),
    projectTitle: project.title || project.projectId,
    // a branch or subtask keeps its own history: the live protocol is not used for it (ChatView.sendToChat)
    protocolEntries: chat.parent ? 0 : protocolEntries,
    lineageTitle: lineageTitleOf(chat, chats),
    levels: chat.family ? [] : folderLevels(chat, chats || []),
    contextFolders: (project.contextFolders || []).length,
    shared,
    family: !!chat.family,
  };
}

export function ChatContext({ chat, project, chats, shared, readOnly, onMode }: {
  chat: Chat; project: Project; chats: Chat[] | null; shared: boolean; readOnly: boolean; onMode: (m: ContextMode) => Promise<void>;
}) {
  const [entries, setEntries] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    setEntries(null);
    dbProtocol(chat.chatId)
      .then((p) => { if (live) setEntries(p ? p.entries.filter((e) => e.status === 'active').length : 0); })
      .catch(() => { if (live) setEntries(0); });
    return () => { live = false; };
  }, [chat.chatId, chat.updatedUtc]);
  const mode: ContextMode = chat.contextMode || 'full';
  const items = contextItems(contextInputOf(chat, project, chats, entries, shared));
  return (
    <div className="small chat-ctx">
      <label className="fc-row">
        <span>Context mode of this chat</span>
        <select value={mode} disabled={readOnly || busy} title={CONTEXT_MODE_HINT[mode]}
          onChange={async (e) => {
            setBusy(true);
            setErr(null);
            try {
              await onMode(e.target.value as ContextMode);
            } catch (x) {
              setErr(categorize(x).message);
            } finally {
              setBusy(false);
            }
          }}>
          {(Object.keys(CONTEXT_MODE_LABEL) as ContextMode[]).map((k) => <option key={k} value={k} title={CONTEXT_MODE_HINT[k]}>{CONTEXT_MODE_LABEL[k]}</option>)}
        </select>
      </label>
      <div className="muted tiny">{CONTEXT_MODE_HINT[mode]} A change applies from the next new agent session.</div>
      {err && <div className="error-text tiny">{err}</div>}
      <ul className="ctx-list">
        {items.map((it) => (
          <li key={it.key} className={`ctx-item ctx-${it.state}`} title={`${it.label}: ${STATE_WORD[it.state]}`}>
            <span className="ctx-mark" aria-label={STATE_WORD[it.state]}>{STATE_MARK[it.state]}</span>
            <span aria-hidden="true">{it.icon}</span>
            <span className="ctx-text">
              <span className="ctx-label">{it.label}</span>
              <span className="muted tiny"> — {it.detail}</span>
              {it.switch === 'project' && !shared && (
                <> · <button type="button" className="link tiny" onClick={() => openPanel('project', { projectId: project.projectId })}>project Properties</button></>
              )}
              {it.switch === 'folder' && <span className="muted tiny"> · switch in that folder's Properties</span>}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
