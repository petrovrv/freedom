// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.82.0: select text in an agent's answer -> a small box with Ask and WTF?! (as in the study viewer).
// The question goes to the chat as "[quote: #N] …" (chatQuote.ts); formulas are quoted as their TeX source.
import { useEffect, useRef, useState, type RefObject } from 'react';
import { quoteMessage } from './chatQuote';

const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/i.test(navigator.platform || navigator.userAgent);
const MOD = isMac ? '⌘' : 'Ctrl+';

const BLOCKS = 'p,li,h1,h2,h3,h4,h5,h6,pre,blockquote,td,th,.katex-display';

// The selected text of one answer, with KaTeX formulas as $tex$ / $$tex$$ (the rendered math is not useful text).
function rangeText(range: Range, root: Element): string {
  let out = '';
  let prevBlock: Element | null = null;
  const seen = new Set<Element>();
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    if (!range.intersectsNode(n)) continue;
    const el = n.parentElement;
    const block = el?.closest(BLOCKS) || null;
    if (out && block !== prevBlock && !out.endsWith('\n')) out += '\n';
    prevBlock = block;
    const kx = el?.closest('.katex');
    if (kx) {
      if (seen.has(kx)) continue;
      seen.add(kx);
      const tex = kx.querySelector('annotation[encoding="application/x-tex"]')?.textContent?.trim();
      if (tex) out += kx.closest('.katex-display') ? `$$${tex}$$` : `$${tex}$`;
      continue;
    }
    const t = n.textContent || '';
    const from = n === range.startContainer ? range.startOffset : 0;
    const to = n === range.endContainer ? range.endOffset : t.length;
    out += t.slice(from, to);
  }
  return out.replace(/[ \t]+\n/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
}

interface Picked {
  seq: number;
  text: string;
  top: number;
  left: number;
}

export default function QuoteAsk({ box, canAsk, onAsk }: { box: RefObject<HTMLDivElement | null>; canAsk: boolean; onAsk: (text: string) => void }) {
  const [picked, setPicked] = useState<Picked | null>(null);
  const [question, setQuestion] = useState('');
  const [copyStatus, setCopyStatus] = useState<'idle' | 'copied' | 'failed'>('idle');
  const pop = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const host = box.current;
    if (!host) return;
    let timer = 0;
    const check = () => {
      window.clearTimeout(timer);
      // after the browser has settled the selection (touch handles on iPad/iPhone take a moment)
      timer = window.setTimeout(() => {
        const sel = window.getSelection();
        if (!sel || sel.isCollapsed || !sel.rangeCount) return;
        const range = sel.getRangeAt(0);
        const start = (range.startContainer instanceof Element ? range.startContainer : range.startContainer.parentElement)?.closest<HTMLElement>('.msg.assistant[data-seq]');
        const end = (range.endContainer instanceof Element ? range.endContainer : range.endContainer.parentElement)?.closest<HTMLElement>('.msg.assistant[data-seq]');
        if (!start || start !== end || !host.contains(start)) return; // one answer at a time
        const body = start.querySelector('.msg-body');
        if (!body || !range.intersectsNode(body)) return;
        const text = rangeText(range, body);
        if (!text) return;
        const hr = host.getBoundingClientRect();
        const rr = range.getBoundingClientRect();
        const width = Math.min(340, hr.width - 16);
        setCopyStatus('idle');
        setPicked({
          seq: Number(start.dataset.seq),
          text,
          top: rr.bottom - hr.top + host.scrollTop + 6,
          left: Math.max(8, Math.min(rr.left - hr.left + host.scrollLeft, hr.width - width - 8)),
        });
      }, 120);
    };
    // a click outside the box drops it (a new selection then picks again)
    const down = (e: PointerEvent) => {
      if (pop.current && e.target instanceof Node && pop.current.contains(e.target)) return;
      setPicked(null);
    };
    host.addEventListener('mouseup', check);
    host.addEventListener('touchend', check);
    host.addEventListener('keyup', check);
    host.addEventListener('pointerdown', down);
    return () => {
      window.clearTimeout(timer);
      host.removeEventListener('mouseup', check);
      host.removeEventListener('touchend', check);
      host.removeEventListener('keyup', check);
      host.removeEventListener('pointerdown', down);
    };
  }, [box]);

  if (!picked) return null;

  async function copy() {
    if (!picked) return;
    try {
      await navigator.clipboard.writeText(picked.text);
      setCopyStatus('copied');
    } catch {
      setCopyStatus('failed');
    }
  }

  function ask(wtf: boolean) {
    if (!picked || !canAsk) return;
    if (!wtf && !question.trim()) return;
    onAsk(quoteMessage({ seq: picked.seq, quote: picked.text, question, wtf }));
    setPicked(null);
    setQuestion('');
    window.getSelection()?.removeAllRanges();
  }

  return (
    <div ref={pop} className="quote-ask" style={{ top: picked.top, left: picked.left }}
      onMouseUp={(e) => e.stopPropagation()} onTouchEnd={(e) => e.stopPropagation()} onKeyUp={(e) => e.stopPropagation()}>
      {/* the highlight of the selection goes when the box takes the focus, so the box shows what it is about */}
      <div className="quote-ask-frag tiny muted" title={picked.text}>❝ {picked.text.length > 140 ? picked.text.slice(0, 140) + '…' : picked.text}</div>
      <textarea rows={2} placeholder={canAsk ? 'Ask about this…' : 'The chat is read-only'} value={question}
        onChange={(e) => setQuestion(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); ask(false); }
          if (e.key === 'Escape') setPicked(null);
        }} />
      {copyStatus === 'failed' && <div className="tiny" role="alert">Could not copy. Select the text and use your browser's Copy command.</div>}
      <div className="study-ask-row">
        <button type="button" className="btn tiny" title="Copy the selected text" onMouseDown={(e) => e.preventDefault()} onClick={() => void copy()}>{copyStatus === 'copied' ? 'Copied!' : 'Copy'}</button>
        <button type="button" className="btn tiny primary" disabled={!canAsk || !question.trim()} title={`Ask about the selected text (${MOD}Enter)`} onClick={() => ask(false)}>Ask</button>
        <button type="button" className="btn tiny" disabled={!canAsk} title="Explain the selected text again from scratch, more simply" onClick={() => ask(true)}>WTF?!</button>
        <button type="button" className="btn tiny ghost" onClick={() => setPicked(null)}>✕</button>
      </div>
    </div>
  );
}
