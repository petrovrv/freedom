// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useMemo, useRef, useState } from 'react';
import type { EnvironmentId } from './config';
import { categorize } from './errors';
import type { ImportBundle, ImportedConversation, ImportedProject } from './importers/parse';
import {
  indexKey, projectIdFrom, readImportIndex, runImport, type ImportIndex, type ImportResult, type ProjectTarget,
} from './importers/write';
import type { Project } from './types';
import { SAFE_ID, formatLocal } from './util';
import type { Root } from './workbench';

const NO_PROJECT = '__none__';

interface TargetChoice {
  mode: 'new' | 'existing';
  newId: string;
  newTitle: string;
  existingId: string;
}

export default function ImportView({
  root, env, projects, onFinished,
}: { root: Root; env: EnvironmentId; projects: Project[]; onFinished: (projectId?: string) => void }) {
  const [bundle, setBundle] = useState<ImportBundle | null>(null);
  const [parsing, setParsing] = useState<number | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [index, setIndex] = useState<ImportIndex | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [focus, setFocus] = useState<string | null>(null);
  const [q, setQ] = useState('');
  const [projFilter, setProjFilter] = useState<string>('');
  const [hideImported, setHideImported] = useState(true);
  const [targets, setTargets] = useState<Record<string, TargetChoice>>({});
  const [includeContent, setIncludeContent] = useState(true);
  const [running, setRunning] = useState<{ done: number; total: number; label: string } | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    readImportIndex(root).then(setIndex).catch((e) => setErr(categorize(e).message));
  }, [root]);

  function parse(files: File[]) {
    if (!files.length) return;
    setErr(null);
    setBundle(null);
    setResult(null);
    setParsing(0);
    const worker = new Worker(new URL('./importers/worker.ts', import.meta.url), { type: 'module' });
    worker.onmessage = (e) => {
      const m = e.data;
      if (m.type === 'progress') setParsing(m.value);
      else {
        worker.terminate();
        setParsing(null);
        if (m.type === 'error') setErr(m.message);
        else {
          const b = m.bundle as ImportBundle;
          b.conversations.sort((a, c) => (c.updatedUtc || '').localeCompare(a.updatedUtc || ''));
          setBundle(b);
          setSelected(new Set());
          setTargets({});
        }
      }
    };
    worker.onerror = (e) => {
      worker.terminate();
      setParsing(null);
      setErr(e.message || 'Parser failed.');
    };
    worker.postMessage({ files });
  }

  const projectsByKey = useMemo(() => new Map((bundle?.projects || []).map((p) => [p.key, p])), [bundle]);
  const isImported = (c: ImportedConversation) => !!index?.entries[indexKey(c.source, c.id)];

  const visible = useMemo(() => {
    if (!bundle) return [];
    const needle = q.trim().toLowerCase();
    return bundle.conversations.filter((c) => {
      if (hideImported && isImported(c)) return false;
      if (projFilter === NO_PROJECT && c.projectKey) return false;
      if (projFilter && projFilter !== NO_PROJECT && c.projectKey !== projFilter) return false;
      if (!needle) return true;
      return c.title.toLowerCase().includes(needle) || c.messages.some((m) => m.text.toLowerCase().includes(needle));
    });
  }, [bundle, q, projFilter, hideImported, index]); // eslint-disable-line react-hooks/exhaustive-deps

  const selectedConvs = useMemo(() => (bundle?.conversations || []).filter((c) => selected.has(c.id)), [bundle, selected]);
  const groupKeys = useMemo(() => [...new Set(selectedConvs.map((c) => c.projectKey || NO_PROJECT))], [selectedConvs]);

  const prefix = bundle?.source === 'chatgpt' ? 'gpt' : 'cl';
  const defaultTarget = (key: string): TargetChoice => {
    const src = projectsByKey.get(key);
    const title = key === NO_PROJECT ? `Imported from ${bundle?.source === 'chatgpt' ? 'ChatGPT' : 'claude.ai'}` : src?.name || key;
    return { mode: 'new', newId: key === NO_PROJECT ? `${prefix}-imported` : projectIdFrom(title, prefix), newTitle: title, existingId: projects[0]?.projectId || '' };
  };
  const targetOf = (key: string) => targets[key] || defaultTarget(key);

  const targetErrors = groupKeys
    .map((k) => {
      const t = targetOf(k);
      if (t.mode === 'existing') return t.existingId ? null : 'Choose an existing project.';
      if (!SAFE_ID.test(t.newId)) return `Invalid project ID "${t.newId}".`;
      return null;
    })
    .filter(Boolean) as string[];

  async function start() {
    if (!bundle || !index) return;
    setErr(null);
    setResult(null);
    const byKey = new Map<string, ProjectTarget>();
    for (const k of groupKeys) {
      const t = targetOf(k);
      if (t.mode === 'existing') {
        const p = projects.find((x) => x.projectId === t.existingId);
        if (!p) {
          setErr(`Project ${t.existingId} not found.`);
          return;
        }
        byKey.set(k, { kind: 'existing', project: p });
      } else {
        byKey.set(k, { kind: 'new', projectId: t.newId, title: t.newTitle || t.newId, source: projectsByKey.get(k) as ImportedProject | undefined });
      }
    }
    setRunning({ done: 0, total: selectedConvs.length, label: '' });
    try {
      const r = await runImport({
        root, env, bundle, conversations: selectedConvs, index, includeProjectContent: includeContent,
        targetFor: (c) => byKey.get(c.projectKey || NO_PROJECT)!,
        onProgress: (done, total, label) => setRunning({ done, total, label }),
      });
      setResult(r);
      setSelected(new Set());
      setIndex({ ...index });
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setRunning(null);
    }
  }

  const focused = bundle?.conversations.find((c) => c.id === focus) || null;
  const toggle = (id: string) =>
    setSelected((prev) => {
      const n = new Set(prev);
      if (n.has(id)) n.delete(id);
      else n.add(id);
      return n;
    });

  if (!bundle) {
    return (
      <div className="import pad">
        <h2>Import chats from claude.ai or ChatGPT</h2>
        <ol className="small">
          <li><b>claude.ai:</b> Settings → Privacy → Export data. The download link arrives by e-mail.</li>
          <li><b>ChatGPT:</b> Settings → Data controls → Export data. The download link arrives by e-mail.</li>
          <li>Select the downloaded <code>.zip</code> (or <code>conversations.json</code> and <code>projects.json</code>). It is read in this browser; only the chats you select are written to Drive.</li>
        </ol>
        <div
          className="dropzone"
          onDragOver={(e) => e.preventDefault()}
          onDrop={(e) => {
            e.preventDefault();
            parse(Array.from(e.dataTransfer.files));
          }}
        >
          <button className="btn primary" onClick={() => fileInput.current?.click()} disabled={parsing !== null}>
            Choose export file…
          </button>
          <input ref={fileInput} type="file" accept=".zip,.json" multiple hidden onChange={(e) => parse(Array.from(e.target.files || []))} />
          <span className="muted small">or drop it here</span>
        </div>
        {parsing !== null && <p className="muted">Reading export… {Math.round(parsing * 100)}%</p>}
        {err && <p className="error-text">{err}</p>}
      </div>
    );
  }

  const importedCount = bundle.conversations.filter(isImported).length;

  return (
    <div className="import">
      <div className="import-head">
        <div>
          <h2>Import from {bundle.source === 'chatgpt' ? 'ChatGPT' : 'claude.ai'}</h2>
          <div className="muted small">
            {bundle.conversations.length} chats · {bundle.projects.length} projects · {importedCount} already imported · target environment <b>{env}</b>
          </div>
          {bundle.warnings.map((w) => <div key={w} className="warn small">{w}</div>)}
        </div>
        <button className="btn ghost small" onClick={() => setBundle(null)}>Other file</button>
      </div>

      <div className="import-toolbar">
        <input className="search" placeholder="Search titles and messages" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search" />
        <select value={projFilter} onChange={(e) => setProjFilter(e.target.value)} aria-label="Project filter">
          <option value="">All chats</option>
          <option value={NO_PROJECT}>Without project</option>
          {bundle.projects.map((p) => (
            <option key={p.key} value={p.key}>
              {p.name} ({bundle.conversations.filter((c) => c.projectKey === p.key).length})
            </option>
          ))}
        </select>
        <label className="small check">
          <input type="checkbox" checked={hideImported} onChange={(e) => setHideImported(e.target.checked)} /> Hide imported
        </label>
        <button className="btn tiny" onClick={() => setSelected(new Set([...selected, ...visible.filter((c) => !isImported(c)).map((c) => c.id)]))}>
          Select shown ({visible.length})
        </button>
        <button className="btn tiny" onClick={() => setSelected(new Set())}>Clear</button>
      </div>

      <div className="import-body">
        <ul className="import-list" role="listbox" aria-multiselectable>
          {visible.slice(0, 1000).map((c) => {
            const done = isImported(c);
            return (
              <li key={c.id} className={`${focus === c.id ? 'focus' : ''} ${done ? 'done' : ''}`}>
                <input type="checkbox" checked={selected.has(c.id)} disabled={done} onChange={() => toggle(c.id)} aria-label={`Select ${c.title}`} />
                <button className="import-item" onClick={() => setFocus(c.id)}>
                  <span className="nav-title">{c.title}</span>
                  <span className="muted tiny">
                    {formatLocal(c.updatedUtc)} · {c.messages.length} msg
                    {c.projectKey ? ` · ${projectsByKey.get(c.projectKey)?.name || 'project'}` : ''}
                    {done ? ' · imported' : ''}
                  </span>
                </button>
              </li>
            );
          })}
          {visible.length > 1000 && <li className="muted small pad">Showing 1000 of {visible.length}; refine the search.</li>}
          {visible.length === 0 && <li className="muted small pad">Nothing matches.</li>}
        </ul>
        <div className="import-preview">
          {focused ? (
            <>
              <h3>{focused.title}</h3>
              <div className="muted small">
                {formatLocal(focused.createdUtc)} – {formatLocal(focused.updatedUtc)} · {focused.messages.length} messages
                {focused.model ? ` · ${focused.model}` : ''}
              </div>
              {focused.messages.slice(0, 60).map((m, i) => (
                <div key={i} className={`pv-msg ${m.role}`}>
                  <div className="muted tiny">{m.role === 'user' ? 'You' : bundle.source === 'chatgpt' ? 'ChatGPT' : 'Claude'} {m.createdUtc ? '· ' + formatLocal(m.createdUtc) : ''}</div>
                  <div className="pre">{m.text.length > 1500 ? m.text.slice(0, 1500) + ' …' : m.text}</div>
                  {!!m.attachments?.length && <div className="muted tiny">📎 {m.attachments.map((a) => a.name).join(', ')}</div>}
                </div>
              ))}
              {focused.messages.length > 60 && <div className="muted small">… {focused.messages.length - 60} more messages</div>}
            </>
          ) : (
            <p className="muted">Click a chat to preview it.</p>
          )}
        </div>
      </div>

      {selectedConvs.length > 0 && (
        <div className="import-targets">
          <h4>Where to put {selectedConvs.length} selected chat(s)</h4>
          {groupKeys.map((k) => {
            const t = targetOf(k);
            const src = projectsByKey.get(k);
            const count = selectedConvs.filter((c) => (c.projectKey || NO_PROJECT) === k).length;
            const set = (patch: Partial<TargetChoice>) => setTargets({ ...targets, [k]: { ...t, ...patch } });
            return (
              <div key={k} className="target-row">
                <span className="target-src">
                  {k === NO_PROJECT ? 'Chats without project' : src?.name || k} <span className="muted tiny">({count})</span>
                </span>
                <select value={t.mode} onChange={(e) => set({ mode: e.target.value as TargetChoice['mode'] })} aria-label="Target type">
                  <option value="new">New CO project</option>
                  <option value="existing" disabled={!projects.length}>Existing CO project</option>
                </select>
                {t.mode === 'new' ? (
                  <>
                    <input value={t.newId} onChange={(e) => set({ newId: e.target.value })} aria-label="Project ID" className={SAFE_ID.test(t.newId) ? '' : 'invalid'} />
                    <input
                      value={t.newTitle}
                      onChange={(e) => {
                        // Keep the ID in step with the title until the ID is edited by hand.
                        const auto = t.newId === projectIdFrom(t.newTitle, prefix);
                        set({ newTitle: e.target.value, ...(auto ? { newId: projectIdFrom(e.target.value, prefix) } : {}) });
                      }}
                      aria-label="Project title"
                    />
                  </>
                ) : (
                  <select value={t.existingId} onChange={(e) => set({ existingId: e.target.value })} aria-label="Existing project">
                    {projects.map((p) => <option key={p.projectId} value={p.projectId}>{p.title}</option>)}
                  </select>
                )}
              </div>
            );
          })}
          {bundle.source === 'claude' && bundle.projects.some((p) => p.instructions || p.docs.length) && (
            <label className="small check">
              <input type="checkbox" checked={includeContent} onChange={(e) => setIncludeContent(e.target.checked)} /> Import project instructions and knowledge files into new projects
            </label>
          )}
          <p className="muted tiny">
            Imported projects exist in Drive only and run in Chat mode. Add a project to the Bridge <code>config.json</code> to use Cowork with a workspace.
          </p>
          {targetErrors.map((x) => <div key={x} className="error-text small">{x}</div>)}
          <button className="btn primary" disabled={!!running || targetErrors.length > 0} onClick={() => void start()}>
            Import {selectedConvs.length} chat(s)
          </button>
        </div>
      )}

      {running && (
        <div className="import-progress">
          <progress max={running.total} value={running.done} /> {running.done}/{running.total} <span className="muted small">{running.label}</span>
        </div>
      )}
      {err && <p className="error-text pad">{err}</p>}
      {result && (
        <div className="import-result">
          Imported {result.imported}, skipped {result.skipped} (already imported)
          {result.failed.length > 0 && <>, failed {result.failed.length}</>}.
          {result.failed.map((f) => <div key={f.title} className="error-text small">{f.title}: {f.error}</div>)}
          <div>
            <button className="btn small" onClick={() => onFinished(result.projects[0])}>Open projects</button>
          </div>
        </div>
      )}
    </div>
  );
}
