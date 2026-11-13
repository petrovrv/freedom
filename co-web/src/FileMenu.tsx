// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.61.0: one "⋯" menu for a file (message artifacts, the Drive tree of the inspector) instead of a row of buttons.
// Items: Download, Preview, Open in Drive, Open in Google Docs (Office files), Save to Drive, and the items of enabled
// modules (manifest fileActions, F10).
import { useEffect, useRef, useState } from 'react';
import { fileActionsFor, type ModuleInfo } from './extBridge';
import { modulePageHref } from './ModulePage';

export interface FileMenuItem {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  title?: string;
}

export function FileMenu({ name, items, busy }: { name: string; items: (FileMenuItem | false | null | undefined)[]; busy?: boolean }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', esc);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', esc);
    };
  }, [open]);

  return (
    <span className="file-menu" ref={ref}>
      <button className="btn tiny" aria-label={`Actions for ${name}`} aria-haspopup="menu" aria-expanded={open} disabled={busy} onClick={() => setOpen(!open)}>⋯</button>
      {open && (
        <div className="menu-pop" role="menu">
          {items.filter((i): i is FileMenuItem => !!i).map((i) => (
            <button key={i.label} role="menuitem" disabled={i.disabled} title={i.title} onClick={() => { setOpen(false); i.onClick(); }}>
              {i.label}
            </button>
          ))}
        </div>
      )}
    </span>
  );
}

function ext(name: string): string {
  return name.toLowerCase().split('.').pop() || '';
}

// Google editor for an Office file kept on Drive (edited in Office compatibility mode, the file stays .docx/.xlsx/.pptx).
export function googleDocsUrl(id: string, name: string): string | null {
  const e = ext(name);
  if (['doc', 'docx', 'odt', 'rtf'].includes(e)) return `https://docs.google.com/document/d/${id}/edit`;
  if (['xls', 'xlsx', 'xlsm', 'ods', 'csv'].includes(e)) return `https://docs.google.com/spreadsheets/d/${id}/edit`;
  if (['ppt', 'pptx', 'odp'].includes(e)) return `https://docs.google.com/presentation/d/${id}/edit`;
  return null;
}

export const hasGoogleDocs = (name: string) => googleDocsUrl('x', name) !== null;

// File path relative to AI-Workbench as modules get it. A legacy artifact copy (artifacts/<job>/x) means its live scratch file.
export function livePath(driveRelativePath: string): string {
  const m = /^(projects\/[^/]+\/chats\/[^/]+)\/(?:branches\/[^/]+\/)?artifacts\/[^/]+\/(.+)$/.exec(driveRelativePath);
  return m ? `${m[1]}/scratch/${m[2]}` : driveRelativePath;
}

// F10 (ext/1 mode viewer): "?<param>=<path>" on the project open in this tab (the project of the file's path otherwise).
export function viewerHref(param: string, path: string): string | null {
  const here = /^\/e\/([^/]+)(?:\/p\/([^/]+))?/.exec(window.location.pathname);
  const pid = here?.[2] || /^projects\/([^/]+)\//.exec(path)?.[1];
  if (!here || !pid) return null;
  return `/e/${here[1]}/p/${encodeURIComponent(decodeURIComponent(pid))}?${param}=${encodeURIComponent(livePath(path))}`;
}

// F10 (ext/1 fileActions): items modules add for this file - open a module page (new tab, or this one) or the viewer of
// a chat mode next to its chat.
export function moduleFileItems(modules: ModuleInfo[], name: string, path: string | null, go: (href: string) => void): FileMenuItem[] {
  return fileActionsFor(modules, name).flatMap(({ m, a }): FileMenuItem[] => {
    const title = a.title || undefined;
    let open: (() => void) | null = null;
    if ('page' in a.open) {
      const o = a.open;
      const ui = m.ui.find((u) => u.slot === 'ui.page' && u.id === o.page);
      if (!ui) return [];
      if (path) {
        const href = modulePageHref(m, ui, { [o.param || 'path']: livePath(path) });
        open = o.target === 'blank' ? () => window.open(href, '_blank') : () => go(href);
      }
    } else {
      const key = a.open.mode;
      const viewer = (m.modes || []).find((x) => x.key === key)?.viewer;
      if (!viewer) return [];
      const href = path ? viewerHref(viewer.param, path) : null;
      if (href) open = () => go(href);
    }
    if (!open) return [{ label: a.label, onClick: () => {}, disabled: true, title: 'The file is not on Drive yet' }];
    return [{ label: a.label, title, onClick: open }];
  });
}
