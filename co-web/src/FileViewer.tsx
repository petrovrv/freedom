// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.132.0 (plan 10g B): one click on a file opens it inside CO (fileOpen.ts). useOpenFile() decides where: the
// module app that declared the extension its default (canvas for Office and Markdown, the study viewer for PDF), else
// the core's own viewer below for pictures and text (shown as text, never rendered: HTML and SVG source stay text,
// pictures go through <img>, which runs no script), else the old click (download). The viewer has Download and
// "Ask in chat": the selected text goes with the file's path into the composer of the file's chat.
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useNavigate } from 'react-router-dom';
import { livePath, viewerHref } from './FileMenu';
import { modulePageHref } from './ModulePage';
import { moduleList } from './moduleStore';
import { categorize } from './errors';
import { coreViewKind, defaultFileAction, fileAskText, fileChatPath } from './fileOpen';

const TEXT_MAX = 2_000_000;

/** Navigation state the chat of a file takes as a composer prefill (ChatView). */
export interface FileAskState { fileAsk: { text: string; nonce: number } }

/** Opens the chat of a file with "About the file <path>: «quote»" in its composer; false when there is no such chat. */
export function askAboutFile(go: (to: string, o?: { state?: unknown; replace?: boolean }) => void, path: string, quote: string): boolean {
  const env = /^\/e\/([^/]+)/.exec(window.location.pathname)?.[1] || '';
  const to = fileChatPath(decodeURIComponent(env), livePath(path), window.location.pathname);
  if (!to || !quote.trim()) return false;
  const state: FileAskState = { fileAsk: { text: fileAskText(livePath(path), quote), nonce: Date.now() } };
  // the open chat itself: no extra history entry (Back would land in the same chat)
  go(to, { state, replace: to === window.location.pathname } as { state: unknown });
  return true;
}

export interface OpenFileArgs {
  name: string;
  /** path relative to AI-Workbench (null: not on Drive yet; only the core viewer and the fallback can open it) */
  path: string | null;
  load: () => Promise<ArrayBuffer>;
  /** the old click (download) for a file nothing opens */
  fallback: () => void;
}

/** One click on a file: [open, the viewer element to render (or null)]. */
export function useOpenFile(): [(f: OpenFileArgs) => void, ReactNode] {
  const go = useNavigate();
  const [shown, setShown] = useState<OpenFileArgs | null>(null);
  const open = (f: OpenFileArgs) => {
    const hit = defaultFileAction(moduleList(), f.name);
    if (hit && f.path) {
      const { m, a } = hit;
      if ('page' in a.open) {
        const o = a.open;
        const ui = m.ui.find((u) => u.slot === 'ui.page' && u.id === o.page);
        if (ui) {
          const href = modulePageHref(m, ui, { [o.param || 'path']: livePath(f.path) });
          // a desktop app (ui.window) gets its own window in CO Desktop; everywhere else this tab (no new tab)
          const desktop = !!(window as unknown as { coDesktop?: unknown }).coDesktop;
          if (desktop && ui.window && o.target === 'blank') window.open(href, '_blank');
          else go(href);
          return;
        }
      } else {
        const key = a.open.mode;
        const viewer = (m.modes || []).find((x) => x.key === key)?.viewer;
        const href = viewer ? viewerHref(viewer.param, f.path) : null;
        if (href) { go(href); return; }
      }
    }
    if (coreViewKind(f.name)) setShown(f);
    else f.fallback();
  };
  // on <body>: inside a draggable row of Documents/Products a selection would start a drag of the row (review 10g-B)
  const el = shown ? createPortal(<FileViewer f={shown} onClose={() => setShown(null)} />, document.body) : null;
  return [open, el];
}

function FileViewer({ f, onClose }: { f: OpenFileArgs; onClose: () => void }) {
  const go = useNavigate();
  const kind = coreViewKind(f.name);
  const [img, setImg] = useState<string | null>(null);
  const [text, setText] = useState<string | null>(null);
  const [cut, setCut] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [sel, setSel] = useState('');
  const body = useRef<HTMLDivElement>(null);
  const dialog = useRef<HTMLDivElement>(null);
  const downOnBack = useRef(false);
  useEffect(() => {
    const before = document.activeElement as HTMLElement | null;
    dialog.current?.focus();
    return () => before?.focus?.();
  }, []);

  useEffect(() => {
    let gone = false;
    let url = '';
    f.load().then((bytes) => {
      if (gone) return;
      if (kind === 'image' && /\.svg$/i.test(f.name)) {
        // a data: URL, not a blob of CO's origin: "open image in new tab" must not run its scripts as CO (review 10g-B)
        const b = new Uint8Array(bytes);
        let s = '';
        for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode(...b.subarray(i, i + 0x8000));
        setImg(`data:image/svg+xml;base64,${btoa(s)}`);
      } else if (kind === 'image') {
        url = URL.createObjectURL(new Blob([bytes]));
        setImg(url);
      } else {
        setCut(bytes.byteLength > TEXT_MAX);
        setText(new TextDecoder('utf-8').decode(bytes.slice(0, TEXT_MAX)));
      }
    }).catch((e) => { if (!gone) setErr(categorize(e).message); });
    return () => { gone = true; if (url) URL.revokeObjectURL(url); };
  }, [f, kind]);

  useEffect(() => {
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); onClose(); } };
    const pick = () => {
      const s = window.getSelection();
      const inside = !!s && s.rangeCount > 0 && !!body.current?.contains(s.getRangeAt(0).commonAncestorContainer);
      setSel(inside ? s!.toString() : '');
    };
    document.addEventListener('keydown', esc);
    document.addEventListener('selectionchange', pick);
    return () => { document.removeEventListener('keydown', esc); document.removeEventListener('selectionchange', pick); };
  }, [onClose]);

  const canAsk = !!f.path && !!fileChatPath(decodeURIComponent(/^\/e\/([^/]+)/.exec(window.location.pathname)?.[1] || ''),
    livePath(f.path), window.location.pathname);
  return (
    // closes on a click that also began on the backdrop: a selection dragged out of the text must not close it
    <div className="modal-back file-viewer-back" onMouseDown={(e) => { downOnBack.current = e.target === e.currentTarget; }}
      onClick={(e) => { if (e.target === e.currentTarget && downOnBack.current) onClose(); }}>
      <div className="modal file-viewer" role="dialog" aria-modal="true" aria-label={f.name} tabIndex={-1} ref={dialog}>
        <div className="file-viewer-bar">
          <strong className="file-viewer-name" title={f.path || f.name}>{f.name}</strong>
          {kind === 'text' && canAsk && (
            <button type="button" className="btn small" disabled={!sel.trim()} title="Select text first; it goes with the file's path into the chat's message box"
              onClick={() => { if (f.path && askAboutFile(go, f.path, sel)) onClose(); }}>Ask in chat</button>
          )}
          <button type="button" className="btn small ghost" onClick={f.fallback}>Download</button>
          <button type="button" className="btn small ghost" aria-label="Close" onClick={onClose}>✕</button>
        </div>
        <div className="file-viewer-body" ref={body}>
          {err ? <div className="error-text small">{err}</div>
            : img ? <img src={img} alt={f.name} />
              : text !== null ? <pre className="file-viewer-text">{text}{cut ? '\n… (the rest of the file is not shown; download it)' : ''}</pre>
                : <div className="muted small">Loading…</div>}
        </div>
      </div>
    </div>
  );
}
