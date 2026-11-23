// SPDX-License-Identifier: AGPL-3.0-or-later
// F15.1 (SPEC §3.6b п. 1): the Explorer view of the left panel. Projects and chats as folders (child chats are nested
// folders), a path bar with a way up, icons or a list, Ctrl+K to jump to any project or chat. A mouse opens with a
// double click (one click selects), a touch opens with a tap; the keyboard moves with the arrows, Enter opens,
// Backspace or Alt+Up goes up. Logic: explorerTree.ts. The tree view stays the default (☰ → Explorer view).
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { createPortal } from 'react-dom';
import type { Chat, Project } from './types';
import type { SortMode } from './sidebarSort';
import { formatLocal } from './util';
import { ChatStatusIcon, ProjectRing, type ChatStatus } from './status';
import {
  childPath, crumbs, explorerItems, shortDate, moveFocus, parentPath, pathOfChat, quickJump, readExplorerView, ROOT_PATH, samePath,
  validPath, writeExplorerView, type ExplorerItem, type ExplorerPath, type ExplorerView, type JumpHit,
} from './explorerTree';

export interface ExplorerProps {
  rootLabel: string;
  projects: Project[];
  /** chats of a project when loaded, else null */
  chatsOf: (projectId: string) => Chat[] | null;
  /** load (or refresh) the chats of one project */
  loadChats: (projectId: string) => void;
  /** load the chats of every project, for Ctrl+K */
  loadAll: () => void;
  routeProjectId?: string;
  routeChatId?: string;
  sort: SortMode;
  filter: string;
  statusOf: (c: Chat) => ChatStatus;
  workingOf: (projectId: string) => number;
  projectFresh: (p: Project) => string;
  projectWait: (p: Project) => number;
  chatWait: (c: Chat) => number;
  onOpenChat: (projectId: string, chatId: string) => void;
  onOpenProject: (projectId: string) => void;
}

export default function Explorer(props: ExplorerProps) {
  const { projects, chatsOf, routeProjectId, routeChatId } = props;
  const [path, setPath] = useState<ExplorerPath>(() => (routeProjectId ? { projectId: routeProjectId, chatIds: [] } : ROOT_PATH));
  const [view, setViewState] = useState<ExplorerView>(readExplorerView);
  const setView = (v: ExplorerView) => {
    writeExplorerView(v);
    setViewState(v);
  };
  const [focus, setFocus] = useState(0);
  const [jumpOpen, setJumpOpen] = useState(false);
  const chats = path.projectId ? chatsOf(path.projectId) : null;

  // the opened chat (from a link, a search, Ctrl+K) shows the folder it lives in, once its project's chats are loaded
  const routeChats = routeProjectId ? chatsOf(routeProjectId) : null;
  const synced = useRef('');
  useEffect(() => {
    if (!routeProjectId) return;
    const key = `${routeProjectId}/${routeChatId || ''}`;
    if (synced.current === key || (routeChatId && !routeChats)) return;
    synced.current = key;
    setPath(routeChatId && routeChats ? pathOfChat(routeProjectId, routeChatId, routeChats) : { projectId: routeProjectId, chatIds: [] });
  }, [routeProjectId, routeChatId, routeChats]);

  // a folder that is gone (deleted, archived, moved) leads to the nearest one above it
  useEffect(() => {
    if (!projects.length) return;
    const v = validPath(path, projects, chats);
    if (v !== path && !samePath(v, path)) setPath(v);
  }, [path, projects, chats]);

  const pid = path.projectId;
  const { loadChats } = props;
  useEffect(() => {
    if (pid) loadChats(pid);
  }, [pid]); // eslint-disable-line react-hooks/exhaustive-deps

  const items = useMemo(
    () =>
      explorerItems(path, projects, chats, {
        sort: props.sort,
        filter: props.filter,
        projectFresh: props.projectFresh,
        projectWait: props.projectWait,
        chatWait: props.chatWait,
      }),
    [path, projects, chats, props.sort, props.filter, props.projectFresh, props.projectWait, props.chatWait],
  );
  const pathKey = `${path.projectId || ''}/${path.chatIds.join('/')}`;
  // a new folder: focus its first item, or the opened chat when it is here
  useEffect(() => {
    const i = items.findIndex((x) => x.kind !== 'project' && x.chat.chatId === routeChatId);
    setFocus(i >= 0 ? i : 0);
  }, [pathKey]); // eslint-disable-line react-hooks/exhaustive-deps
  const focusIdx = Math.min(focus, Math.max(0, items.length - 1));

  const gridRef = useRef<HTMLUListElement>(null);
  const btns = useRef<Array<HTMLButtonElement | null>>([]);
  const keyFocus = useRef(false);
  useEffect(() => {
    if (!keyFocus.current) return;
    keyFocus.current = false;
    btns.current[focusIdx]?.focus();
  }, [focusIdx, pathKey]);

  const go = (p: ExplorerPath, byKey = false) => {
    keyFocus.current = byKey;
    setPath(p);
  };
  const open = (item: ExplorerItem, byKey = false) => {
    const next = childPath(path, item);
    if (next) return go(next, byKey);
    if (item.kind !== 'project' && pid) props.onOpenChat(pid, item.chat.chatId);
  };
  const up = (byKey = false) => {
    if (path.projectId) go(parentPath(path), byKey);
  };

  const columns = () => {
    if (view === 'list' || !gridRef.current) return 1;
    return Math.max(1, getComputedStyle(gridRef.current).gridTemplateColumns.split(' ').filter(Boolean).length);
  };
  const onKey = (e: KeyboardEvent) => {
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement) return;
    if (e.key === 'Backspace' || (e.altKey && e.key === 'ArrowUp')) {
      e.preventDefault();
      up(true);
      return;
    }
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    const n = moveFocus(e.key, focusIdx, items.length, columns());
    if (n !== null) {
      e.preventDefault();
      keyFocus.current = true;
      setFocus(n);
    }
  };

  // one click selects with a mouse, a tap opens on a touch screen; a double click opens
  const pointer = useRef('mouse');
  const onPointerDown = (e: PointerEvent) => {
    pointer.current = e.pointerType || 'mouse';
  };

  // Ctrl+K (⌘K) anywhere while the Explorer is shown
  useEffect(() => {
    const onDoc = (e: globalThis.KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setJumpOpen(true);
      }
    };
    document.addEventListener('keydown', onDoc);
    return () => document.removeEventListener('keydown', onDoc);
  }, []);

  const bar = crumbs(path, props.rootLabel, projects, chats);
  const loading = !!pid && !chats;

  return (
    <div className="explorer" onKeyDown={onKey}>
      <div className="ex-bar">
        <button type="button" className="ex-up" onClick={() => up()} disabled={!path.projectId} title="Up (Backspace, Alt+↑)" aria-label="Up one folder">↑</button>
        <nav className="ex-crumbs" aria-label="Path">
          {bar.map((c, i) => (
            <span key={i} className="ex-crumb">
              {i > 0 && <span className="ex-sep" aria-hidden="true">›</span>}
              {i === bar.length - 1 ? (
                <span className="ex-here" aria-current="location" title={c.label}>{c.label}</span>
              ) : (
                <button type="button" onClick={() => go(c.path)} title={c.label}>{c.label}</button>
              )}
            </span>
          ))}
        </nav>
      </div>
      <div className="ex-tools">
        <button type="button" className="btn tiny" onClick={() => setJumpOpen(true)} title="Go to a project or chat (Ctrl+K)">🔍 Go to… <kbd>Ctrl K</kbd></button>
        <span className="ex-views" role="group" aria-label="View">
          <button type="button" className={view === 'icons' ? 'on' : ''} aria-pressed={view === 'icons'} onClick={() => setView('icons')} title="Icons">▦</button>
          <button type="button" className={view === 'list' ? 'on' : ''} aria-pressed={view === 'list'} onClick={() => setView('list')} title="List">☰</button>
        </span>
        {pid && <button type="button" className="btn tiny" onClick={() => props.onOpenProject(pid)} title="Start a new chat in this project">+ Chat</button>}
      </div>
      {loading && <div className="muted small pad">Loading chats…</div>}
      {!loading && items.length === 0 && (
        <div className="muted small pad">{props.filter.trim() ? 'Nothing matches the filter here.' : pid ? 'No chats yet.' : 'No projects.'}</div>
      )}
      <ul ref={gridRef} className={`ex-items ex-${view}`} role="listbox" aria-label={bar[bar.length - 1].label}>
        {items.map((it, i) => {
          const active = it.kind === 'project' ? it.project.projectId === routeProjectId && !routeChatId : it.chat.chatId === routeChatId;
          const icon = it.kind === 'project' ? '📁' : it.kind === 'self' ? '💬' : it.folder ? '🗂️' : '💬';
          const sub = it.kind === 'project' ? '' : it.kind === 'chat' && it.folder ? `${it.count} inside` : '';
          const date = it.kind === 'project' ? props.projectFresh(it.project) : it.chat.updatedUtc;
          const when = date ? `
${formatLocal(date)}` : '';
          const hint = (it.kind === 'self' ? `${it.title} (this chat itself)` : it.kind === 'chat' ? it.chat.summary || it.title : it.title) + when;
          return (
            <li key={it.key} role="presentation">
              <button
                type="button"
                ref={(el) => { btns.current[i] = el; }}
                role="option"
                aria-selected={i === focusIdx}
                tabIndex={i === focusIdx ? 0 : -1}
                className={`ex-item${active ? ' active' : ''}${i === focusIdx ? ' sel' : ''}${it.kind === 'self' ? ' self' : ''}`}
                title={hint}
                onPointerDown={onPointerDown}
                onClick={() => {
                  setFocus(i);
                  if (pointer.current !== 'mouse') open(it);
                }}
                onDoubleClick={() => open(it)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    e.stopPropagation();
                    open(it, true);
                  }
                }}
              >
                <span className="ex-icon" aria-hidden="true">
                  {icon}
                  {it.kind !== 'project' && <span className="ex-status"><ChatStatusIcon status={props.statusOf(it.chat)} /></span>}
                  {it.kind === 'project' && <span className="ex-status"><ProjectRing count={props.workingOf(it.project.projectId)} /></span>}
                </span>
                <span className="ex-title">{it.title}</span>
                {view === 'list' && <span className="ex-date">{sub || shortDate(date)}</span>}
                {view === 'icons' && sub && <span className="ex-sub">{sub}</span>}
              </button>
            </li>
          );
        })}
      </ul>
      {jumpOpen &&
        createPortal(
          <QuickJump
            projects={projects}
            chatsOf={chatsOf}
            loadAll={props.loadAll}
            onClose={() => setJumpOpen(false)}
            onPick={(h) => {
              setJumpOpen(false);
              if (h.chatId) props.onOpenChat(h.projectId, h.chatId);
              else go({ projectId: h.projectId, chatIds: [] }, true);
            }}
          />,
          document.body,
        )}
    </div>
  );
}

function QuickJump(props: {
  projects: Project[];
  chatsOf: (projectId: string) => Chat[] | null;
  loadAll: () => void;
  onClose: () => void;
  onPick: (h: JumpHit) => void;
}) {
  const [q, setQ] = useState('');
  const [sel, setSel] = useState(0);
  const { loadAll } = props;
  useEffect(() => loadAll(), []); // eslint-disable-line react-hooks/exhaustive-deps
  const hits = useMemo(() => quickJump(q, props.projects, props.chatsOf), [q, props.projects, props.chatsOf]);
  const cur = Math.min(sel, Math.max(0, hits.length - 1));
  const back = useRef<Element | null>(document.activeElement);
  useEffect(() => {
    const el = back.current;
    return () => {
      if (el instanceof HTMLElement && el.isConnected) el.focus();
    };
  }, []);
  const onKey = (e: KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      props.onClose();
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (hits.length) setSel((cur + (e.key === 'ArrowDown' ? 1 : hits.length - 1)) % hits.length);
    } else if (e.key === 'Enter' && hits[cur]) {
      e.preventDefault();
      props.onPick(hits[cur]);
    } else if (e.key === 'Tab') {
      e.preventDefault(); // focus stays in the dialog
    }
  };
  return (
    <div className="ex-jump-scrim" onMouseDown={(e) => { if (e.target === e.currentTarget) props.onClose(); }}>
      <div className="ex-jump" role="dialog" aria-modal="true" aria-label="Go to a project or chat" onKeyDown={onKey}>
        <input
          autoFocus
          className="search"
          placeholder="Project or chat title…"
          value={q}
          onChange={(e) => { setQ(e.target.value); setSel(0); }}
          role="combobox"
          aria-expanded={hits.length > 0}
          aria-controls="ex-jump-list"
          aria-activedescendant={hits[cur] ? `ex-jump-${cur}` : undefined}
          aria-autocomplete="list"
        />
        <ul id="ex-jump-list" role="listbox" className="ex-jump-list">
          {hits.map((h, i) => (
            <li
              key={`${h.projectId}/${h.chatId || ''}`}
              id={`ex-jump-${i}`}
              role="option"
              aria-selected={i === cur}
              className={i === cur ? 'sel' : ''}
              onMouseEnter={() => setSel(i)}
              onClick={() => props.onPick(h)}
            >
              <span aria-hidden="true">{h.kind === 'project' ? '📁' : '💬'}</span>
              <span className="ex-title">{h.title}</span>
              {h.where && <span className="ex-where">{h.where}</span>}
            </li>
          ))}
        </ul>
        {q.trim() && hits.length === 0 && <div className="muted small pad">Nothing found by title. The AI search of the panel looks inside chats.</div>}
        {!q.trim() && <div className="muted small pad">Type a few letters of a title. ↑↓ choose, Enter opens, Esc closes.</div>}
      </div>
    </div>
  );
}
