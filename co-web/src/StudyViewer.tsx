// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.80.0: study viewer, stage V1 (SPEC.md of chat CHAT-20261103081329-PX01, part V). The document on the left
// (pdf.js; other formats arrive as PDF from studyConvert.ts), the chat of the document on the right (Shell). Select text
// or drag an area, then Ask or WTF: the message carries a "[doc: …]" tag (studyDoc.ts), a PNG of the area and the text
// of the page and its neighbours. Every question leaves a marker on its page; a click on it scrolls the chat to it.
// Mac: ⌘F search, ⌘+/⌘− zoom, pinch on the trackpad (Safari gesture events, Chrome ctrl+wheel).
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as pdfjs from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import type { PDFDocumentProxy, PDFPageProxy } from 'pdfjs-dist';
import type { ChatEvent } from './types';
import type { Root } from './workbench';
import { loadStudyPdf } from './studyConvert';
import { askMessage, parseDocTag, type DocTag } from './studyDoc';
import './study.css';

pdfjs.GlobalWorkerOptions.workerSrc = workerUrl;

const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
const MOD = isMac ? '⌘' : 'Ctrl+';
const ZMIN = 0.3;
const ZMAX = 5;

interface Rect { x: number; y: number; w: number; h: number }
interface Picked { page: number; rect: Rect; text: string; shape: 'area' | 'text' }
interface Size { w: number; h: number }
interface Marker { eventId: string; tag: DocTag; title: string; answered: boolean }
interface OutlineItem { title: string; page: number | null; items: OutlineItem[] }

interface Props {
  root: Root;
  path: string; // relative to AI-Workbench
  events: ChatEvent[] | null;
  canAsk: boolean;
  onAsk: (text: string, files: File[]) => void;
  onOpenAnswer: (eventId: string) => void;
  onClose: () => void;
}

const clamp = (v: number, a: number, b: number) => Math.min(b, Math.max(a, v));

export default function StudyViewer({ root, path, events, canAsk, onAsk, onOpenAnswer, onClose }: Props) {
  const name = path.split('/').pop() || 'Document';
  const [status, setStatus] = useState('Loading…');
  const [err, setErr] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [doc, setDoc] = useState<PDFDocumentProxy | null>(null);
  const [sizes, setSizes] = useState<Size[]>([]);
  const [scale, setScale] = useState(1);
  const [page, setPage] = useState(1);
  const [areaMode, setAreaMode] = useState(false);
  const [side, setSide] = useState<'none' | 'pages' | 'contents'>('none');
  const [outline, setOutline] = useState<OutlineItem[] | null>(null);
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<number[] | null>(null);
  const [hitAt, setHitAt] = useState(0);
  const [picked, setPicked] = useState<Picked | null>(null);
  const [question, setQuestion] = useState('');
  const [asking, setAsking] = useState(false);
  const scroller = useRef<HTMLDivElement>(null);
  const searchBox = useRef<HTMLInputElement>(null);
  const texts = useRef<Map<number, string>>(new Map());
  const scaleRef = useRef(scale);
  scaleRef.current = scale;

  // load
  useEffect(() => {
    let gone = false;
    let task: ReturnType<typeof pdfjs.getDocument> | null = null;
    setDoc(null);
    setErr(null);
    texts.current = new Map();
    (async () => {
      const pdf = await loadStudyPdf(root.id, path, (s) => { if (!gone) setStatus(s); });
      if (gone) return;
      setNote(pdf.note || null);
      setStatus('Opening…');
      task = pdfjs.getDocument({ data: pdf.bytes.slice(), useWasm: false }); // no wasm: the site's CSP has no wasm-unsafe-eval
      const d = await task.promise;
      const all: Size[] = [];
      for (let i = 1; i <= d.numPages; i++) {
        const v = (await d.getPage(i)).getViewport({ scale: 1 });
        all.push({ w: v.width, h: v.height });
      }
      if (gone) return;
      const avail = (scroller.current?.clientWidth || 800) - 32;
      setScale(clamp(avail / Math.max(...all.map((s) => s.w)), ZMIN, 2));
      setSizes(all);
      setDoc(d);
      document.title = `${name} · CO study`;
    })().catch((e) => { if (!gone) setErr((e as Error).message || String(e)); });
    return () => {
      gone = true;
      void task?.destroy();
    };
  }, [root.id, path, name]);

  const pageText = useCallback(async (n: number): Promise<string> => {
    const cached = texts.current.get(n);
    if (cached !== undefined || !doc) return cached || '';
    const tc = await (await doc.getPage(n)).getTextContent();
    const t = tc.items.map((it) => ('str' in it ? it.str + (it.hasEOL ? '\n' : '') : '')).join('');
    texts.current.set(n, t);
    return t;
  }, [doc]);

  // markers: questions of this chat about this document
  const markers = useMemo<Marker[]>(() => {
    const out: Marker[] = [];
    const answered = new Set((events || []).filter((e) => e.type === 'assistant_message').map((e) => e.jobId));
    for (const e of events || []) {
      if (e.type !== 'user_message') continue;
      const tag = parseDocTag(e.text);
      if (!tag || tag.doc !== name.replace(/[\],]/g, ' ')) continue;
      const q = (e.text || '').split('\n').slice(1).find((l) => l.trim()) || '';
      out.push({ eventId: e.eventId, tag, title: tag.wtf ? 'WTF' : q.slice(0, 120), answered: answered.has(e.jobId) });
    }
    return out;
  }, [events, name]);

  const goTo = useCallback((n: number) => {
    const el = scroller.current?.querySelector<HTMLElement>(`[data-page="${n}"]`);
    if (el) scroller.current!.scrollTo({ top: el.offsetTop - 8 });
    setPage(n);
  }, []);

  // current page from the scroll position
  const onScroll = useCallback(() => {
    const sc = scroller.current;
    if (!sc) return;
    const mid = sc.scrollTop + sc.clientHeight / 3;
    const els = sc.querySelectorAll<HTMLElement>('[data-page]');
    let n = 1;
    for (const el of els) {
      if (el.offsetTop <= mid) n = Number(el.dataset.page);
      else break;
    }
    // at the very end the last pages may never reach the line: the bottom of the document is its last page
    if (els.length && sc.scrollTop + sc.clientHeight >= sc.scrollHeight - 4 && sc.scrollTop > 0) n = els.length;
    setPage(n);
  }, []);

  // zoom keeping the point under the view's centre
  const zoomTo = useCallback((next: number) => {
    const sc = scroller.current;
    const s = clamp(next, ZMIN, ZMAX);
    if (!sc) return setScale(s);
    const k = s / scaleRef.current;
    const top = (sc.scrollTop + sc.clientHeight / 2) * k - sc.clientHeight / 2;
    const left = (sc.scrollLeft + sc.clientWidth / 2) * k - sc.clientWidth / 2;
    setScale(s);
    requestAnimationFrame(() => sc.scrollTo({ top, left }));
  }, []);
  const fitWidth = () => {
    if (!sizes.length) return;
    zoomTo(((scroller.current?.clientWidth || 800) - 32) / Math.max(...sizes.map((s) => s.w)));
  };

  // pinch (Chrome: ctrl+wheel; Safari: gesture events) and keys
  useEffect(() => {
    const sc = scroller.current;
    if (!sc) return;
    const wheel = (e: WheelEvent) => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      zoomTo(scaleRef.current * Math.exp(-e.deltaY / 200));
    };
    let g0 = 1;
    const gs = (e: Event) => { e.preventDefault(); g0 = scaleRef.current; };
    const gc = (e: Event) => { e.preventDefault(); const s = (e as unknown as { scale: number }).scale; if (s) setScale(clamp(g0 * s, ZMIN, ZMAX)); };
    sc.addEventListener('wheel', wheel, { passive: false });
    sc.addEventListener('gesturestart', gs);
    sc.addEventListener('gesturechange', gc);
    return () => {
      sc.removeEventListener('wheel', wheel);
      sc.removeEventListener('gesturestart', gs);
      sc.removeEventListener('gesturechange', gc);
    };
  }, [zoomTo, doc]);

  const rootRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      const inside = rootRef.current?.contains(document.activeElement) || rootRef.current?.matches(':hover');
      if (!inside) return;
      const mod = e.metaKey || e.ctrlKey;
      if (mod && e.key.toLowerCase() === 'f') { e.preventDefault(); searchBox.current?.focus(); searchBox.current?.select(); }
      else if (mod && (e.key === '+' || e.key === '=')) { e.preventDefault(); zoomTo(scaleRef.current * 1.2); }
      else if (mod && e.key === '-') { e.preventDefault(); zoomTo(scaleRef.current / 1.2); }
      else if (e.key === 'Escape') { setPicked(null); }
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [zoomTo]);

  // outline
  useEffect(() => {
    if (!doc || side !== 'contents' || outline) return;
    (async () => {
      const conv = async (items: Awaited<ReturnType<PDFDocumentProxy['getOutline']>>): Promise<OutlineItem[]> => {
        const out: OutlineItem[] = [];
        for (const it of items || []) {
          let pg: number | null = null;
          try {
            const dest = typeof it.dest === 'string' ? await doc.getDestination(it.dest) : it.dest;
            if (dest && dest[0]) pg = typeof dest[0] === 'number' ? dest[0] + 1 : (await doc.getPageIndex(dest[0])) + 1;
          } catch { /* broken destination */ }
          out.push({ title: it.title, page: pg, items: await conv(it.items) });
        }
        return out;
      };
      setOutline(await conv(await doc.getOutline()));
    })().catch(() => setOutline([]));
  }, [doc, side, outline]);

  // search over the text of all pages
  async function runSearch(dir: 1 | -1 = 1) {
    const qy = query.trim().toLowerCase();
    if (!doc || !qy) { setHits(null); return; }
    let list = hits;
    if (!list) {
      list = [];
      for (let i = 1; i <= doc.numPages; i++) if ((await pageText(i)).toLowerCase().includes(qy)) list.push(i);
      setHits(list);
      const first = Math.max(0, list.findIndex((p) => p >= page));
      setHitAt(first);
      if (list.length) goTo(list[first]);
      return;
    }
    if (!list.length) return;
    const next = (hitAt + dir + list.length) % list.length;
    setHitAt(next);
    goTo(list[next]);
  }

  // text selection -> picked
  function onMouseUp() {
    if (areaMode) return;
    const sel = window.getSelection();
    const text = sel?.toString() || '';
    if (!sel || !text.trim() || !sel.rangeCount) return;
    const range = sel.getRangeAt(0);
    const pageEl = (range.startContainer.parentElement || null)?.closest<HTMLElement>('[data-page]');
    if (!pageEl || !scroller.current?.contains(pageEl)) return;
    const pr = pageEl.getBoundingClientRect();
    const rr = range.getBoundingClientRect();
    const rect = {
      x: clamp((rr.left - pr.left) / pr.width, 0, 1),
      y: clamp((rr.top - pr.top) / pr.height, 0, 1),
      w: clamp(rr.width / pr.width, 0, 1),
      h: clamp(rr.height / pr.height, 0, 1),
    };
    setPicked({ page: Number(pageEl.dataset.page), rect, text, shape: 'text' });
  }

  async function ask(wtf: boolean) {
    if (!doc || !picked || asking || !canAsk) return; // also the ⌘/Ctrl+Enter path (review)
    if (!wtf && !question.trim()) return;
    setAsking(true);
    try {
      const pg = await doc.getPage(picked.page);
      const png = await cropPng(pg, picked.rect, picked.shape === 'text' ? 0.02 : 0);
      const from = Math.max(1, picked.page - 1);
      const to = Math.min(doc.numPages, picked.page + 1);
      let ctx = '';
      for (let i = from; i <= to; i++) ctx += `===== page ${i} =====\n${(await pageText(i)).trim()}\n\n`;
      const stem = name.replace(/\.[^.]+$/, '').replace(/[^\p{L}\p{N}._ -]+/gu, '_').slice(0, 60);
      const stamp = new Date().toISOString().replace(/\D/g, '').slice(4, 14);
      const files = [
        new File([png], `${stem} p${picked.page} ${stamp}.png`, { type: 'image/png' }),
        new File([ctx], `${stem} p${from}-${to} ${stamp}.txt`, { type: 'text/plain' }),
      ];
      const tag: DocTag = { doc: name, page: picked.page, shape: picked.shape, rect: picked.rect, wtf };
      onAsk(askMessage({ tag, question, path, selected: picked.shape === 'text' ? picked.text : undefined, pagesNote: from === to ? `page ${from}` : `pages ${from}-${to}`, hasImage: true }), files);
      setPicked(null);
      setQuestion('');
      window.getSelection()?.removeAllRanges();
    } catch (e) {
      setErr(`Could not prepare the question: ${(e as Error).message}`);
    } finally {
      setAsking(false);
    }
  }

  const n = sizes.length;
  return (
    <div className="study" ref={rootRef} tabIndex={-1}>
      <div className="study-bar">
        <button type="button" className="btn tiny ghost" title="Pages and contents" aria-pressed={side !== 'none'} onClick={() => setSide(side === 'none' ? 'pages' : 'none')}>☰</button>
        <span className="study-name" title={path}>📖 {name}</span>
        <span className="study-pages">
          <input key={page} aria-label="Page" className="study-pg" defaultValue={page} inputMode="numeric"
            onKeyDown={(e) => { if (e.key === 'Enter') { const v = Number(e.currentTarget.value); if (v >= 1 && v <= n) goTo(v); else e.currentTarget.value = String(page); } }}
            onBlur={(e) => { e.currentTarget.value = String(page); }} /> / {n || '…'}
        </span>
        <button type="button" className="btn tiny ghost" title={`Zoom out (${MOD}−)`} onClick={() => zoomTo(scale / 1.2)}>−</button>
        <button type="button" className="btn tiny ghost" title="Fit width" onClick={fitWidth}>{Math.round(scale * 100)}%</button>
        <button type="button" className="btn tiny ghost" title={`Zoom in (${MOD}+)`} onClick={() => zoomTo(scale * 1.2)}>+</button>
        <button type="button" className={`btn tiny ${areaMode ? 'study-on' : 'ghost'}`} aria-pressed={areaMode}
          title="Area: drag a rectangle over a formula, figure or scan, then ask about it. Off: select text."
          onClick={() => { setAreaMode(!areaMode); setPicked(null); }}>▭ Area</button>
        <span className="study-search">
          <input ref={searchBox} type="search" placeholder={`Search (${MOD}F)`} value={query}
            onChange={(e) => { setQuery(e.target.value); setHits(null); }}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void runSearch(e.shiftKey ? -1 : 1); } }} />
          {hits && <span className="tiny muted">{hits.length ? `${hitAt + 1}/${hits.length} p.` : 'none'}</span>}
        </span>
        <button type="button" className="btn tiny ghost" title="Close the document" onClick={onClose}>✕</button>
      </div>
      {note && <div className="study-note small muted">{note}</div>}
      <div className="study-body">
        {side !== 'none' && doc && (
          <div className="study-side">
            <div className="study-tabs">
              <button type="button" className={`btn tiny ${side === 'pages' ? '' : 'ghost'}`} onClick={() => setSide('pages')}>Pages</button>
              <button type="button" className={`btn tiny ${side === 'contents' ? '' : 'ghost'}`} onClick={() => setSide('contents')}>Contents</button>
            </div>
            {side === 'pages' ? (
              <div className="study-thumbs">
                {sizes.map((s, i) => (
                  <button type="button" key={i} className={`study-thumb ${page === i + 1 ? 'on' : ''}`} onClick={() => goTo(i + 1)} title={`Page ${i + 1}`}>
                    <PageCanvas doc={doc} n={i + 1} size={s} scale={110 / s.w} />
                    <span className="tiny">{i + 1}{markers.some((m) => m.tag.page === i + 1) ? ' •' : ''}</span>
                  </button>
                ))}
              </div>
            ) : (
              <div className="study-outline small">
                {outline === null ? <span className="muted">Loading…</span> : outline.length ? <Outline items={outline} goTo={goTo} /> : <span className="muted">This document has no contents.</span>}
              </div>
            )}
          </div>
        )}
        <div className="study-scroll" ref={scroller} onScroll={onScroll} onMouseUp={onMouseUp}>
          {err ? (
            <div className="empty"><p className="error-text">{err}</p></div>
          ) : !doc ? (
            <div className="empty"><p className="muted">{status}</p></div>
          ) : (
            sizes.map((s, i) => (
              <PageView key={i} doc={doc} n={i + 1} size={s} scale={scale} query={hits ? query.trim() : ''} areaMode={areaMode}
                markers={markers.filter((m) => m.tag.page === i + 1)} onOpenAnswer={onOpenAnswer}
                picked={picked?.page === i + 1 ? picked : null}
                onArea={(rect) => setPicked({ page: i + 1, rect, text: '', shape: 'area' })}>
                {picked?.page === i + 1 && (
                  <div className="study-ask" style={{ left: `${Math.min(picked.rect.x, 0.6) * 100}%`, top: `${Math.min(1, picked.rect.y + picked.rect.h) * 100}%` }}
                    onMouseUp={(e) => e.stopPropagation()} onPointerDown={(e) => e.stopPropagation()}>
                    <textarea autoFocus rows={2} placeholder={canAsk ? 'Ask about this…' : 'The chat is busy or read-only'} value={question}
                      onChange={(e) => setQuestion(e.target.value)}
                      onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); void ask(false); } }} />
                    <div className="study-ask-row">
                      <button type="button" className="btn tiny primary" disabled={!canAsk || asking || !question.trim()} title={`Ask (${MOD}Enter)`} onClick={() => void ask(false)}>Ask</button>
                      <button type="button" className="btn tiny" disabled={!canAsk || asking} title="Explain this from scratch: every symbol, each line, what it is for" onClick={() => void ask(true)}>WTF?!</button>
                      <button type="button" className="btn tiny ghost" onClick={() => setPicked(null)}>✕</button>
                    </div>
                  </div>
                )}
              </PageView>
            ))
          )}
        </div>
      </div>
    </div>
  );
}

function Outline({ items, goTo }: { items: OutlineItem[]; goTo: (n: number) => void }) {
  return (
    <ul>
      {items.map((it, i) => (
        <li key={i}>
          <button type="button" className="link" disabled={!it.page} onClick={() => it.page && goTo(it.page)}>{it.title}</button>
          {it.items.length > 0 && <Outline items={it.items} goTo={goTo} />}
        </li>
      ))}
    </ul>
  );
}

// PNG of a part of a page, rendered at about 2x screen resolution (the agent reads formulas from it).
async function cropPng(pg: PDFPageProxy, r: { x: number; y: number; w: number; h: number }, pad: number): Promise<Blob> {
  const base = pg.getViewport({ scale: 1 });
  const k = clamp(2400 / base.width, 1.5, 4);
  const vp = pg.getViewport({ scale: k });
  const x0 = clamp(r.x - pad, 0, 1) * vp.width;
  const y0 = clamp(r.y - pad, 0, 1) * vp.height;
  const x1 = clamp(r.x + r.w + pad, 0, 1) * vp.width;
  const y1 = clamp(r.y + r.h + pad, 0, 1) * vp.height;
  const w = Math.max(1, Math.round(x1 - x0));
  const h = Math.max(1, Math.round(y1 - y0));
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  const ctx = c.getContext('2d')!;
  ctx.fillStyle = '#fff';
  ctx.fillRect(0, 0, w, h);
  await pg.render({ canvas: c, viewport: vp, transform: [1, 0, 0, 1, -x0, -y0] }).promise;
  return new Promise((ok, no) => c.toBlob((b) => (b ? ok(b) : no(new Error('Could not encode the image.'))), 'image/png'));
}

// A canvas of one page, rendered when it comes near the view.
function PageCanvas({ doc, n, size, scale }: { doc: PDFDocumentProxy; n: number; size: Size; scale: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const near = useNear(ref);
  useEffect(() => {
    if (!near) return;
    let task: ReturnType<PDFPageProxy['render']> | null = null;
    let gone = false;
    void doc.getPage(n).then((pg) => {
      const c = ref.current;
      if (gone || !c) return;
      const dpr = window.devicePixelRatio || 1;
      const vp = pg.getViewport({ scale: scale * dpr });
      c.width = Math.floor(vp.width);
      c.height = Math.floor(vp.height);
      task = pg.render({ canvas: c, viewport: vp });
      task.promise.catch(() => undefined);
    });
    return () => {
      gone = true;
      task?.cancel();
    };
  }, [doc, n, scale, near]);
  return <canvas ref={ref} style={{ width: size.w * scale, height: size.h * scale }} />;
}

function useNear(ref: React.RefObject<Element | null>): boolean {
  const [near, setNear] = useState(false);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const io = new IntersectionObserver((es) => setNear(es.some((e) => e.isIntersecting)), { rootMargin: '800px 0px' });
    io.observe(el);
    return () => io.disconnect();
  }, [ref]);
  return near;
}

function PageView({ doc, n, size, scale, query, areaMode, markers, picked, onArea, onOpenAnswer, children }: {
  doc: PDFDocumentProxy; n: number; size: Size; scale: number; query: string; areaMode: boolean; markers: Marker[];
  picked: Picked | null; onArea: (r: Rect) => void; onOpenAnswer: (eventId: string) => void; children?: React.ReactNode;
}) {
  const box = useRef<HTMLDivElement>(null);
  const textRef = useRef<HTMLDivElement>(null);
  const near = useNear(box);
  const [drag, setDragState] = useState<{ x0: number; y0: number; x1: number; y1: number } | null>(null);
  const dragRef = useRef(drag); // the handlers read the ref: pointer events may come before a re-render
  const setDrag = (d: typeof drag) => { dragRef.current = d; setDragState(d); };

  // text layer (selection and search hits)
  useEffect(() => {
    const div = textRef.current;
    if (!near || !div) return;
    let tl: pdfjs.TextLayer | null = null;
    let gone = false;
    div.replaceChildren();
    void doc.getPage(n).then(async (pg) => {
      if (gone) return;
      tl = new pdfjs.TextLayer({ textContentSource: pg.streamTextContent(), container: div, viewport: pg.getViewport({ scale }) });
      await tl.render().catch(() => undefined);
      if (gone || !query) return;
      const qy = query.toLowerCase();
      for (const s of div.querySelectorAll('span')) if (s.textContent?.toLowerCase().includes(qy)) s.classList.add('study-hit');
    });
    return () => {
      gone = true;
      tl?.cancel();
    };
  }, [doc, n, scale, near, query]);

  const pt = (e: React.PointerEvent) => {
    const r = box.current!.getBoundingClientRect();
    return { x: clamp((e.clientX - r.left) / r.width, 0, 1), y: clamp((e.clientY - r.top) / r.height, 0, 1) };
  };
  const shown = drag
    ? { x: Math.min(drag.x0, drag.x1), y: Math.min(drag.y0, drag.y1), w: Math.abs(drag.x1 - drag.x0), h: Math.abs(drag.y1 - drag.y0) }
    : picked?.rect;
  const pct = (r: Rect) => ({ left: `${r.x * 100}%`, top: `${r.y * 100}%`, width: `${r.w * 100}%`, height: `${r.h * 100}%` });

  return (
    <div className="study-page" data-page={n} ref={box}
      style={{ width: size.w * scale, height: size.h * scale, ['--scale-factor' as string]: scale, ['--total-scale-factor' as string]: scale, ['--scale-round-x' as string]: '1px', ['--scale-round-y' as string]: '1px' }}>
      <PageCanvas doc={doc} n={n} size={size} scale={scale} />
      <div className="textLayer" ref={textRef} />
      {areaMode && (
        <div className="study-area"
          onPointerDown={(e) => { const p = pt(e); try { (e.target as Element).setPointerCapture(e.pointerId); } catch { /* synthetic or ended pointer */ } setDrag({ x0: p.x, y0: p.y, x1: p.x, y1: p.y }); }}
          onPointerMove={(e) => { const d = dragRef.current; if (d) { const p = pt(e); setDrag({ ...d, x1: p.x, y1: p.y }); } }}
          onPointerUp={(e) => {
            const d0 = dragRef.current;
            if (!d0) return;
            const p = pt(e);
            const d = { ...d0, x1: p.x, y1: p.y };
            const r = { x: Math.min(d.x0, d.x1), y: Math.min(d.y0, d.y1), w: Math.abs(d.x1 - d.x0), h: Math.abs(d.y1 - d.y0) };
            setDrag(null);
            if (r.w > 0.01 && r.h > 0.005) onArea(r);
          }} />
      )}
      {shown && (drag || picked?.shape === 'area') && <div className="study-rect" style={pct(shown)} />}
      {markers.map((m) => {
        const r = m.tag.rect || { x: 0.95, y: 0.01, w: 0, h: 0 };
        return (
          <button type="button" key={m.eventId} className={`study-marker ${m.answered ? 'done' : ''}`}
            style={{ left: `${Math.min(0.97, r.x + r.w) * 100}%`, top: `${r.y * 100}%` }}
            title={`${m.title}${m.answered ? '' : ' (waiting for the answer)'} - open in the chat`}
            onClick={() => onOpenAnswer(m.eventId)}>{m.tag.wtf ? '!' : '?'}</button>
        );
      })}
      {children}
      <span className="study-pno tiny muted">{n}</span>
    </div>
  );
}
