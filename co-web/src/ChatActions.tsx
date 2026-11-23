// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from 'react';
import { categorize } from './errors';
import type { Chat, Project } from './types';
import { suggestChatTitle } from './search';
import { deleteChat, moveChat, renameChat, type HostDirs, type Root } from './workbench';

// 'title' = the rename dialog that starts with an AI-suggested title.
// F15.2: 'delete' puts the chat into the trash (no dialog); 'restore' brings it back, 'purge' deletes it for good.
export type ChatAction = 'rename' | 'title' | 'move' | 'archive' | 'delete' | 'restore' | 'purge';

// Rename (with AI title suggestion) / move / delete-forever dialogs for one chat.
export function ChatActionDialog({
  action, chat, root, hosts, projects, onClose, onDone,
}: {
  action: ChatAction;
  chat: Chat;
  root: Root;
  hosts: Record<string, HostDirs>;
  projects: Project[];
  onClose: () => void;
  onDone: (result: { chat?: Chat; deleted?: boolean }) => void;
}) {
  const [title, setTitle] = useState(chat.title);
  const targets = projects.filter((p) => p.projectId !== chat.projectId);
  const [target, setTarget] = useState(targets[0]?.projectId || '');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [suggesting, setSuggesting] = useState(false);
  const renaming = action === 'rename' || action === 'title';

  async function suggest() {
    setSuggesting(true);
    setErr(null);
    try {
      setTitle(await suggestChatTitle(chat));
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setSuggesting(false);
    }
  }
  useEffect(() => {
    if (action === 'title') void suggest();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function run() {
    setBusy(true);
    setErr(null);
    try {
      if (renaming) onDone({ chat: await renameChat(chat, title) });
      else if (action === 'move') {
        const p = projects.find((x) => x.projectId === target);
        if (!p) throw new Error('Choose a project.');
        onDone({ chat: await moveChat(root, chat, p) });
      } else {
        await deleteChat(root, chat, hosts, !!projects.find((x) => x.projectId === chat.projectId)?.shareRole);
        onDone({ deleted: true });
      }
    } catch (e) {
      setErr(categorize(e).message);
      setBusy(false);
    }
  }

  return (
    <div className="modal-back" onClick={onClose}>
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
            <h3>Rename chat</h3>
            <label>
              Title
              <span className="row-gap">
                <input value={title} onChange={(e) => setTitle(e.target.value)} disabled={suggesting} autoFocus style={{ flex: 1 }} />
                <button type="button" className="btn small" onClick={() => void suggest()} disabled={suggesting} title="Claude Haiku reads the chat and proposes a title">
                  {suggesting ? 'Thinking…' : '✨ Suggest'}
                </button>
              </span>
            </label>
          </>
        )}
        {action === 'move' && (
          <>
            <h3>Move “{chat.title}”</h3>
            {targets.length ? (
              <label>
                Target project
                <select value={target} onChange={(e) => setTarget(e.target.value)}>
                  {targets.map((p) => <option key={p.projectId} value={p.projectId}>{p.title}</option>)}
                </select>
              </label>
            ) : (
              <p className="muted">No other project in this environment.</p>
            )}
            <p className="hint">
              History, attachments, artifacts and the chat scratch folder move along. The next message starts a new agent session
              in the target project; the history goes with it as context.
            </p>
          </>
        )}
        {action === 'purge' && (
          <>
            <h3>Delete “{chat.title}” forever?</h3>
            <p className="hint">
              The chat leaves the CO trash and its messages leave the database. The chat folder with history and files goes to the
              Google Drive trash, where it can be restored for 30 days.
            </p>
          </>
        )}
        {err && <p className="error-text small">{err}</p>}
        <div className="modal-actions">
          <button type="button" className="btn ghost" onClick={onClose}>Cancel</button>
          <button
            type="submit"
            className={`btn ${action === 'purge' ? 'danger' : 'primary'}`}
            disabled={busy || suggesting || (action === 'move' && !target) || (renaming && !title.trim())}
          >
            {busy ? 'Working…' : renaming ? 'Rename' : action === 'move' ? 'Move' : 'Delete forever'}
          </button>
        </div>
      </form>
    </div>
  );
}

// Compact "⋯" menu used in the sidebar.
const ALL_CHAT_ACTIONS: ChatAction[] = ['rename', 'title', 'move', 'archive', 'delete'];
type ChatMenuProps = { onPick: (a: ChatAction) => void; archived?: boolean; actions?: ChatAction[] };

// 0.45.0: no permitted actions -> no menu (wrapper keeps hooks unconditional)
export function ChatMenu(props: ChatMenuProps) {
  if (props.actions && !props.actions.length) return null;
  return <ChatMenuInner {...props} />;
}

function ChatMenuInner({ onPick, archived, actions = ALL_CHAT_ACTIONS }: ChatMenuProps) {
  const [open, setOpen] = useState(false);
  return (
    <span className="chat-menu" onClick={(e) => e.stopPropagation()}>
      <button className="icon-btn small" aria-label="Chat actions" aria-expanded={open} onClick={() => setOpen(!open)}>⋯</button>
      {open && (
        <span className="menu" role="menu" onMouseLeave={() => setOpen(false)}>
          {actions.map((a) => (
            <button
              key={a}
              role="menuitem"
              className={a === 'delete' || a === 'purge' ? 'danger-text' : ''}
              onClick={() => {
                setOpen(false);
                onPick(a);
              }}
            >
              {a === 'rename' ? 'Rename' : a === 'title' ? 'Suggest title' : a === 'move' ? 'Move to project…' : a === 'archive' ? (archived ? 'Restore from archive' : 'Archive') : a === 'restore' ? 'Restore from trash' : a === 'purge' ? 'Delete forever' : 'Move to trash'}
            </button>
          ))}
        </span>
      )}
    </span>
  );
}
