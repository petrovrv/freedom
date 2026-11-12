// SPDX-License-Identifier: AGPL-3.0-or-later
// "Share to CO" (CO Web 0.14.0): pick the project and chat for text/links/files shared from another app.
import { useEffect, useId, useState } from 'react';
import { shareText, type SharedItem } from './share';
import { formatBytes } from './util';

interface ProjectOption {
  projectId: string;
  title: string;
}

interface ChatOption {
  chatId: string;
  title: string;
}

interface ShareDialogProps {
  item: SharedItem;
  projects: ProjectOption[];
  loadChats: (projectId: string) => Promise<ChatOption[]>;
  onOpen: (projectId: string, chatId: string | null) => Promise<void>;
  onCancel: () => void;
}

const MAX_CHATS = 15;
const PREVIEW_CHARS = 300;

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

export function ShareDialog({ item, projects, loadChats, onOpen, onCancel }: ShareDialogProps) {
  const uid = useId();
  const [projectId, setProjectId] = useState(projects[0]?.projectId ?? '');
  const [chatId, setChatId] = useState('');
  const [chats, setChats] = useState<ChatOption[]>([]);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const text = shareText(item);
  const preview = text.length > PREVIEW_CHARS ? `${text.slice(0, PREVIEW_CHARS)}…` : text;

  // Reload chats when the selected project changes
  useEffect(() => {
    setChatId('');
    setChats([]);
    setError(null);
    if (!projectId) return;
    let alive = true;
    setLoading(true);
    loadChats(projectId)
      .then((list) => {
        if (alive) setChats(list.slice(0, MAX_CHATS));
      })
      .catch((err) => {
        if (alive) setError(errorMessage(err));
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  // Escape closes the dialog
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !busy) onCancel();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [busy, onCancel]);

  const open = async () => {
    if (!projectId || busy) return;
    setBusy(true);
    setError(null);
    try {
      await onOpen(projectId, chatId || null);
    } catch (err) {
      setError(errorMessage(err));
      setBusy(false);
    }
  };

  return (
    <div className="modal-back">
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby={`${uid}-title`}>
        <h3 id={`${uid}-title`}>Share to CO</h3>

        {preview && <pre className="share-preview">{preview}</pre>}

        {item.files.length > 0 && (
          <ul className="share-files small">
            {item.files.map((f, i) => (
              <li key={`${f.name}-${i}`}>
                {f.name || 'file'} ({formatBytes(f.size)})
              </li>
            ))}
          </ul>
        )}

        <label>
          Project
          <select value={projectId} disabled={busy || projects.length === 0} onChange={(e) => setProjectId(e.target.value)}>
            {projects.map((p) => (
              <option key={p.projectId} value={p.projectId}>
                {p.title}
              </option>
            ))}
          </select>
        </label>

        <label>
          Chat
          <select value={chatId} disabled={busy || loading || !projectId} onChange={(e) => setChatId(e.target.value)}>
            <option value="">New chat</option>
            {loading ? (
              <option value="" disabled>
                Loading…
              </option>
            ) : (
              chats.map((c) => (
                <option key={c.chatId} value={c.chatId}>
                  {c.title || 'Untitled'}
                </option>
              ))
            )}
          </select>
        </label>

        {error && <div className="error-text small">{error}</div>}

        <div className="modal-actions">
          <button type="button" className="btn primary" disabled={busy || !projectId} onClick={open}>
            {busy ? 'Opening…' : 'Open'}
          </button>
          <button type="button" className="btn ghost" disabled={busy} onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
