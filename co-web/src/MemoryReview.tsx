// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.51.0: wide modal to review memory candidates as a table with bulk actions.
// 0.73.0 (schema 14): also the nightly consolidation proposals - revise (merge/replace/shorten the listed lines into one)
// and drop (remove them); the original lines are shown struck through, accepted ones move to memory-archive.md.
// 0.75.0 (schema 16): rules from corrections (kind rule) - the tag is the target: env (Reflexes, the environment profile) or
// project (memory.md); the verbatim correction and its message are shown.
import { useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { dbMemoryDecide } from './dbapi';
import type { MemoryCandidate } from './dbapi';
import { categorize } from './errors';

type Tag = 'decision' | 'fact' | 'todo' | 'env' | 'project';
type Edit = { tag: Tag; text: string };
type Filter = 'all' | 'decision' | 'fact' | 'rule' | 'revision';
type Action = 'accept' | 'reject';

type Props = {
  candidates: MemoryCandidate[];
  locked: boolean;
  onClose: () => void;
  // 'gone' = decided in another session: remove from the list without counting
  onDecided: (ids: number[], action: Action | 'gone') => void;
  onError: (m: string) => void;
};

// Minimum text length (after trim) required to accept a candidate
const MIN_LEN = 10;

// Accepted text is trimmed and whitespace is collapsed to single spaces
function normalizeText(s: string): string {
  return s.trim().replace(/\s+/g, ' ');
}

function plural(n: number, one: string): string {
  return n + ' ' + one + (n === 1 ? '' : 's');
}

function noop(): void {
  // Checkboxes are controlled and handled in onClick (needs shiftKey)
}

function sourceLabel(c: MemoryCandidate): string {
  if (c.kind === 'revise' || c.kind === 'drop') return ['nightly consolidation', c.model || '', c.createdUtc.slice(0, 10)].filter(Boolean).join(' · ');
  const parts: string[] = [c.chatTitle || c.chatId, '#' + c.msg];
  if (c.day) parts.push(c.day);
  return parts.join(' · ');
}

export function MemoryReviewDialog({ candidates, locked, onClose, onDecided, onError }: Props) {
  const [edits, setEdits] = useState<Record<number, Edit>>(() => {
    const e: Record<number, Edit> = {};
    for (const c of candidates) e[c.id] = { tag: c.tag, text: c.text };
    return e;
  });
  // Ids decided in this dialog; hidden even if the parent has not updated its list yet
  const [gone, setGone] = useState<Set<number>>(() => new Set());
  const [selected, setSelected] = useState<Set<number>>(() => new Set());
  const [filter, setFilter] = useState<Filter>('all');
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<string | null>(null);

  const lastClickedRef = useRef<number | null>(null);
  const headCheckRef = useRef<HTMLInputElement>(null);
  const busyRef = useRef(false);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  const rows = useMemo(() => candidates.filter((c) => !gone.has(c.id)), [candidates, gone]);

  const editOf = (c: MemoryCandidate): Edit => edits[c.id] ?? { tag: c.tag, text: c.text };

  // The filter uses the current edited tag, not the original one; revisions have their own tab
  const inFilter = (r: MemoryCandidate, f: Filter): boolean =>
    f === 'all' ||
    (f === 'revision' ? r.kind === 'revise' || r.kind === 'drop' : f === 'rule' ? r.kind === 'rule' : r.kind === 'add' && editOf(r).tag === f);
  const decisionCount = rows.filter((r) => inFilter(r, 'decision')).length;
  const factCount = rows.filter((r) => inFilter(r, 'fact')).length;
  const revisionCount = rows.filter((r) => inFilter(r, 'revision')).length;
  const ruleCount = rows.filter((r) => inFilter(r, 'rule')).length;
  const shown = rows.filter((r) => inFilter(r, filter));
  const shownSelected = shown.filter((r) => selected.has(r.id));
  const allShownSelected = shown.length > 0 && shownSelected.length === shown.length;
  const someShownSelected = shownSelected.length > 0 && !allShownSelected;

  // Selection drops ids that are no longer present
  useEffect(() => {
    setSelected((prev) => {
      const ids = new Set(rows.map((r) => r.id));
      const next = new Set<number>();
      let changed = false;
      prev.forEach((id) => {
        if (ids.has(id)) next.add(id);
        else changed = true;
      });
      return changed ? next : prev;
    });
  }, [rows]);

  useEffect(() => {
    if (headCheckRef.current) headCheckRef.current.indeterminate = someShownSelected;
  }, [someShownSelected]);

  // Escape closes the dialog unless a batch is running
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !busyRef.current) {
        e.preventDefault();
        onCloseRef.current();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // The server checks the length after normalization
  const tooShort = (c: MemoryCandidate): boolean => c.kind !== 'drop' && normalizeText(editOf(c).text).length < MIN_LEN;
  const canAccept = (list: MemoryCandidate[]): boolean => !locked && !busy && list.length > 0 && !list.some(tooShort);
  const canReject = (list: MemoryCandidate[]): boolean => !busy && list.length > 0;

  function setEdit(c: MemoryCandidate, patch: Partial<Edit>) {
    setEdits((prev) => ({ ...prev, [c.id]: { ...(prev[c.id] ?? { tag: c.tag, text: c.text }), ...patch } }));
  }

  function markDone(id: number) {
    setGone((prev) => {
      const next = new Set(prev);
      next.add(id);
      return next;
    });
    setSelected((prev) => {
      if (!prev.has(id)) return prev;
      const next = new Set(prev);
      next.delete(id);
      return next;
    });
  }

  function toggle(id: number, shift: boolean) {
    if (busyRef.current) return;
    const idx = shown.findIndex((r) => r.id === id);
    const last = lastClickedRef.current;
    const lastIdx = last == null ? -1 : shown.findIndex((r) => r.id === last);
    setSelected((prev) => {
      const next = new Set(prev);
      const target = !prev.has(id);
      if (shift && idx >= 0 && lastIdx >= 0) {
        const a = Math.min(idx, lastIdx);
        const b = Math.max(idx, lastIdx);
        for (let k = a; k <= b; k++) {
          if (target) next.add(shown[k].id);
          else next.delete(shown[k].id);
        }
      } else if (target) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
    lastClickedRef.current = id;
  }

  function toggleAllShown() {
    if (busy) return;
    setSelected((prev) => {
      const next = new Set(prev);
      for (const r of shown) {
        if (allShownSelected) next.delete(r.id);
        else next.add(r.id);
      }
      return next;
    });
  }

  // Decisions run sequentially; the server appends to memory.md
  async function run(list: MemoryCandidate[], action: Action, ask: boolean) {
    if (busyRef.current || list.length === 0) return;
    if (ask) {
      const msg =
        action === 'accept'
          ? 'Accept ' + plural(list.length, 'candidate') + ' into memory.md' + (list.some((c) => c.kind === 'rule') ? ' / the environment profile' : '') + '?'
          : 'Reject ' + plural(list.length, 'candidate') + '?';
      if (!window.confirm(msg)) return;
    }
    const batch = list.map((c) => ({ id: c.id, edit: editOf(c) }));
    const verb = action === 'accept' ? 'Accepting ' : 'Rejecting ';
    busyRef.current = true;
    setBusy(true);
    try {
      for (let i = 0; i < batch.length; i++) {
        const item = batch[i];
        setProgress(verb + (i + 1) + '/' + batch.length + '…');
        let outcome: Action | 'gone' = action;
        try {
          await dbMemoryDecide(
            item.id,
            action,
            action === 'accept' ? { tag: item.edit.tag, text: normalizeText(item.edit.text) } : undefined,
          );
        } catch (err) {
          const raw = err instanceof Error ? err.message : String(err);
          if (raw.indexOf('already_decided') < 0) {
            // Stop the batch, keep the remaining rows
            onError(
              raw.indexOf('memory_changed') >= 0
                ? 'memory.md changed since this proposal (its original lines are gone). Reject it; the next nightly pass proposes again.'
                : categorize(err).message,
            );
            break;
          }
          // Already decided on the server (another session): treat as done and continue
          outcome = 'gone';
        }
        markDone(item.id);
        // Report per id so the parent stays accurate even if the batch stops
        onDecided([item.id], outcome);
      }
    } finally {
      busyRef.current = false;
      setBusy(false);
      setProgress(null);
    }
  }

  const tabs: { key: Filter; label: string; n: number }[] = [
    { key: 'all', label: 'All', n: rows.length },
    { key: 'decision', label: 'Decisions', n: decisionCount },
    { key: 'fact', label: 'Facts', n: factCount },
    { key: 'rule', label: 'Corrections', n: ruleCount },
    { key: 'revision', label: 'Revisions', n: revisionCount },
  ];

  function onRowClick(e: ReactMouseEvent<HTMLTableRowElement>, id: number) {
    const t = e.target as HTMLElement;
    if (t.closest('input, select, textarea, button, a, label')) return;
    toggle(id, e.shiftKey);
  }

  return (
    <div className="modal-back" onClick={() => { if (!busy) onClose(); }}>
      <div className="modal memreview" role="dialog" aria-label="Memory candidates" onClick={(e) => e.stopPropagation()}>
        <div className="memreview-head">
          <h3>Memory candidates</h3>
          <button className="btn ghost small" onClick={onClose} disabled={busy} aria-label="Close" title="Close">
            {'×'}
          </button>
        </div>

        <div className="memreview-tabs">
          {tabs.map((t) => (
            <button
              key={t.key}
              className={'btn small' + (filter === t.key ? ' active' : '')}
              onClick={() => setFilter(t.key)}
              disabled={busy}
            >
              {t.label + ' (' + t.n + ')'}
            </button>
          ))}
        </div>

        <div className="memreview-toolbar">
          <button className="btn small" disabled={!canAccept(shown)} onClick={() => run(shown, 'accept', true)}>
            {'Accept all (' + shown.length + ')'}
          </button>
          <button className="btn small" disabled={!canReject(shown)} onClick={() => run(shown, 'reject', true)}>
            {'Reject all (' + shown.length + ')'}
          </button>
          <span className="memreview-sep" />
          <button className="btn small" disabled={!canAccept(shownSelected)} onClick={() => run(shownSelected, 'accept', true)}>
            {'Accept selected (' + shownSelected.length + ')'}
          </button>
          <button className="btn small" disabled={!canReject(shownSelected)} onClick={() => run(shownSelected, 'reject', true)}>
            {'Reject selected (' + shownSelected.length + ')'}
          </button>
          {progress && <span className="memreview-progress small">{progress}</span>}
        </div>

        {locked && (
          <div className="memreview-locked muted tiny">
            Accepting is disabled: save the memory edits in the inspector first.
          </div>
        )}

        <div className="memreview-body">
          <table className="memreview-table">
            <thead>
              <tr>
                <th className="col-check">
                  <input
                    ref={headCheckRef}
                    type="checkbox"
                    aria-label="Select all shown"
                    checked={allShownSelected}
                    disabled={busy || shown.length === 0}
                    onChange={toggleAllShown}
                  />
                </th>
                <th className="col-tag">Tag</th>
                <th className="col-text">Text</th>
                <th className="col-src">Source</th>
                <th className="col-act">Actions</th>
              </tr>
            </thead>
            <tbody>
              {shown.length === 0 && (
                <tr>
                  <td colSpan={5} className="memreview-empty muted small">
                    {rows.length === 0 ? 'Nothing left to review.' : 'No candidates in this filter.'}
                  </td>
                </tr>
              )}
              {shown.map((c) => {
                const ed = editOf(c);
                const isSel = selected.has(c.id);
                const short = tooShort(c);
                return (
                  <tr key={c.id} className={isSel ? 'sel' : undefined} onClick={(e) => onRowClick(e, c.id)}>
                    <td className="col-check">
                      <input
                        type="checkbox"
                        aria-label={'Select candidate ' + c.id}
                        checked={isSel}
                        disabled={busy}
                        onClick={(e) => {
                          e.stopPropagation();
                          toggle(c.id, e.shiftKey);
                        }}
                        onChange={noop}
                      />
                    </td>
                    <td className="col-tag">
                      {c.kind === 'drop' ? (
                        <span className="memreview-kind">remove</span>
                      ) : (
                        <select value={ed.tag} disabled={busy} onChange={(e) => setEdit(c, { tag: e.target.value as Tag })}
                          title={c.kind === 'rule' ? 'Rule target: the environment profile (Reflexes, every project) or this project\'s memory.md' : undefined}>
                          {c.kind === 'rule' ? (
                            <>
                              <option value="env">{'rule → profile' + (c.targetEnv ? ' (' + c.targetEnv + ')' : '')}</option>
                              <option value="project">rule → project</option>
                            </>
                          ) : (
                            <>
                              <option value="decision">decision</option>
                              <option value="fact">fact</option>
                              {c.kind === 'revise' && <option value="todo">todo</option>}
                            </>
                          )}
                        </select>
                      )}
                    </td>
                    <td className="col-text">
                      {c.kind === 'rule' && c.reason && (
                        <div className="memreview-replaces tiny muted">{'Correction #' + c.msg + ': “' + c.reason + '”'}</div>
                      )}
                      {(c.kind === 'revise' || c.kind === 'drop') && (
                        <div className="memreview-replaces tiny">
                          {c.replaces.map((l, i) => (
                            <div key={i} className="memreview-old">{l}</div>
                          ))}
                          {c.reason && <div className="muted">{(c.kind === 'drop' ? 'Remove: ' : 'Replace by the line below: ') + c.reason}</div>}
                        </div>
                      )}
                      {c.kind !== 'drop' && <textarea
                        rows={2}
                        value={ed.text}
                        disabled={busy}
                        className={short ? 'short' : undefined}
                        title={short ? 'Text must be at least ' + MIN_LEN + ' characters to accept' : undefined}
                        onChange={(e) => setEdit(c, { text: e.target.value })}
                      />}
                    </td>
                    <td className="col-src muted tiny" title={c.model ? 'Model: ' + c.model + ' · ' + c.createdUtc : c.createdUtc}>
                      {sourceLabel(c)}
                    </td>
                    <td className="col-act">
                      <button className="btn tiny" disabled={!canAccept([c])} onClick={() => run([c], 'accept', false)}>
                        Accept
                      </button>
                      <button className="btn tiny" disabled={!canReject([c])} onClick={() => run([c], 'reject', false)}>
                        Reject
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        <div className="memreview-foot">
          <span className="muted tiny">
            Accept appends the line to memory.md (context of every new agent session); a revision replaces the struck-through
            lines, which move to memory-archive.md with the date; a correction becomes a rule in the environment profile or memory.md.
          </span>
          {rows.length === 0 && (
            <span className="memreview-done">
              <span className="small">All candidates are decided.</span>
              <button className="btn small" onClick={onClose}>Close</button>
            </span>
          )}
        </div>
      </div>
    </div>
  );
}
