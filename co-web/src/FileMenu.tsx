// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.61.0: one "⋯" menu for a file (message artifacts, the Drive tree of the inspector) instead of a row of buttons.
// Items: Download, Preview, Open in Drive, Open in Google Docs (Office files), Open in canvas, Save to Drive.
// 0.63.0: Open in canvas works for Office files (ONLYOFFICE on the home PC).
// 0.67.0 (M4): text files (md/txt) open as a notebook: canvas-bridge makes a docx next to them and opens that.
// 0.80.0: Open in study viewer (pdf, Office, text, images): the document left of its study chat (StudyViewer.tsx).
import { useEffect, useRef, useState } from 'react';
import { studyKind } from './studyDoc';

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

// What the canvas (SPEC.md in chat CHAT-20261028152233-8P34) will open: Office files in ONLYOFFICE, text as a notebook.
export function canvasKind(name: string): 'office' | 'notebook' | null {
  const e = ext(name);
  if (['docx', 'xlsx', 'pptx'].includes(e)) return 'office';
  if (['md', 'markdown', 'txt'].includes(e)) return 'notebook';
  return null;
}

// Canvas file path relative to AI-Workbench. A legacy artifact copy (artifacts/<job>/x) opens its live scratch file.
export function canvasPath(driveRelativePath: string): string {
  const m = /^(projects\/[^/]+\/chats\/[^/]+)\/(?:branches\/[^/]+\/)?artifacts\/[^/]+\/(.+)$/.exec(driveRelativePath);
  return m ? `${m[1]}/scratch/${m[2]}` : driveRelativePath;
}

// Menu item "Open in canvas" (new tab): Office files as they are, text files as a notebook (a docx next to them).
export function canvasItem(name: string, path: string | null): FileMenuItem | null {
  const kind = canvasKind(name);
  if (!kind) return null;
  const title = kind === 'notebook' ? 'Edit as a Word notebook (headings, lists); Save as Markdown writes it back' : undefined;
  if (!path) return { label: 'Open in canvas', onClick: () => {}, disabled: true, title: 'The file is not on Drive yet' };
  return { label: 'Open in canvas', title, onClick: () => window.open(`/canvas?path=${encodeURIComponent(canvasPath(path))}`, '_blank') };
}

// 0.80.0: the study viewer of a file in the project open in this tab (the project of the file's path otherwise).
export function studyHref(path: string): string | null {
  const here = /^\/e\/([^/]+)(?:\/p\/([^/]+))?/.exec(window.location.pathname);
  const pid = here?.[2] || /^projects\/([^/]+)\//.exec(path)?.[1];
  if (!here || !pid) return null;
  return `/e/${here[1]}/p/${encodeURIComponent(decodeURIComponent(pid))}?study=${encodeURIComponent(canvasPath(path))}`;
}

export function studyItem(name: string, path: string | null, go: (href: string) => void): FileMenuItem | null {
  if (!studyKind(name)) return null;
  const href = path ? studyHref(path) : null;
  if (!href) return { label: 'Open in study viewer', onClick: () => {}, disabled: true, title: 'The file is not on Drive yet' };
  return { label: 'Open in study viewer', title: 'Read it next to a study chat: select text or an area and ask, or press WTF', onClick: () => go(href) };
}
