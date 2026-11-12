// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.72.0: "Undo file changes of this answer" - preview of the files with a tick each, then undo after a confirmation.
import { useEffect, useState } from 'react';
import type { ChatEvent } from './types';
import { categorize } from './errors';
import { formatLocal } from './util';
import { applyUndo, planUndoJob, type UndoJobPlan } from './undoJob';
import { undoSummary } from './undoPlan';

export function UndoPanel({ rootId, ev, events, projectId, chatId, onClose }: {
  rootId: string; ev: ChatEvent; events: ChatEvent[]; projectId: string; chatId: string; onClose: () => void;
}) {
  const [plan, setPlan] = useState<UndoJobPlan | null>(null);
  const [on, setOn] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    let live = true;
    setBusy(true);
    setErr(null);
    planUndoJob(rootId, ev, events, projectId, chatId)
      .then((p) => {
        if (!live) return;
        setPlan(p);
        setOn(new Set(p.items.filter((i) => i.defaultOn).map((i) => i.key)));
      })
      .catch((e) => live && setErr(categorize(e).message))
      .finally(() => live && setBusy(false));
    return () => { live = false; };
  }, [rootId, ev.eventId, nonce]); // eslint-disable-line react-hooks/exhaustive-deps

  const todo = plan?.items.filter((i) => i.action !== 'none' && on.has(i.key)) || [];
  const toggle = (k: string) => setOn((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n; });

  async function undo() {
    const r = todo.filter((i) => i.action === 'restore').length;
    const t = todo.length - r;
    if (!window.confirm(`Undo the changes of this answer?\n\n${r} file(s) go back to the version before it, ${t} new file(s) move to the Drive trash.\nThe answer's versions stay in Versions.`)) return;
    setBusy(true);
    setErr(null);
    try {
      const res = await applyUndo(rootId, chatId, ev.sequence, todo);
      setDone(undoSummary(res.restored, res.trashed) + (res.errors.length ? `. Not undone: ${res.errors.join('; ')}` : '.'));
      setNonce((n) => n + 1);
    } catch (e) {
      setErr(categorize(e).message);
      setBusy(false);
    }
  }

  return (
    <div className="undo-panel">
      <div className="muted small">
        <strong>Undo file changes of this answer</strong> — only files in this chat’s folder on Drive. Commands, mail, git and other
        effects of the job are not undone.{' '}
        <button className="link" onClick={onClose}>Close</button>
      </div>
      {busy && <div className="muted small">{plan ? 'Working…' : 'Checking the Drive history…'}</div>}
      {err && <div className="error-box">{err} <button className="link" disabled={busy} onClick={() => setNonce((n) => n + 1)}>Retry</button></div>}
      {done && <div className="ok-text small">{done}</div>}
      {plan && (
        <ul className="undo-list">
          {plan.items.map((i) => (
            <li key={i.key} className={i.changedLater ? 'warn' : undefined}>
              <label>
                <input type="checkbox" disabled={i.action === 'none' || busy} checked={i.action !== 'none' && on.has(i.key)} onChange={() => toggle(i.key)} />{' '}
                <span>{i.name}</span>{' '}
                <span className="muted tiny">
                  {i.action === 'restore' ? `↶ back to ${i.revisionUtc ? formatLocal(i.revisionUtc) : 'the earlier version'}` : i.action === 'trash' ? '🗑 to trash' : '—'}
                  {' · '}{i.note}
                </span>
              </label>
            </li>
          ))}
          {plan.outside.map((o, n) => (
            <li key={`o${n}`} className="muted">
              <input type="checkbox" disabled checked={false} readOnly /> <span>{o.name}</span> <span className="tiny">· cannot undo: {o.note}</span>
            </li>
          ))}
          {!plan.items.length && !plan.outside.length && <li className="muted small">This answer reported no changed files.</li>}
        </ul>
      )}
      {plan && (
        <button className="btn small" disabled={busy || !todo.length} onClick={() => void undo()}>
          Undo {todo.length} file{todo.length === 1 ? '' : 's'}
        </button>
      )}
    </div>
  );
}
