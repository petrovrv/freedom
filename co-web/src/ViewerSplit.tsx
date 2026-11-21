// SPDX-License-Identifier: AGPL-3.0-or-later
// Viewer of a chat mode (ui.panel, e.g. the study viewer) left of its chat, with a draggable divider between them.
// The share of the viewer lives in --viewer-frac on the split and in localStorage (co.viewerSplit, percent);
// double-click resets to half, arrow keys move by 5 %. On a phone (CSS stacks the two) the divider moves vertically.
import React, { useRef, useState, type ReactNode } from 'react';

const KEY = 'co.viewerSplit';
const DEF = 50;
const MIN = 20;
const MAX = 80;

const clamp = (v: number) => Math.min(MAX, Math.max(MIN, v));

function readStored(): number {
  try {
    const n = Number(localStorage.getItem(KEY));
    if (localStorage.getItem(KEY) !== null && Number.isFinite(n)) return clamp(n);
  } catch {
    /* ignore */
  }
  return DEF;
}

function save(v: number) {
  try {
    localStorage.setItem(KEY, String(Math.round(v)));
  } catch {
    /* ignore */
  }
}

export default function ViewerSplit({ pane, chat }: { pane: ReactNode; chat: ReactNode }) {
  const [frac, setFrac] = useState(readStored);
  const box = useRef<HTMLDivElement>(null);
  const drag = useRef<{ id: number; stacked: boolean; v: number } | null>(null);

  const stacked = () => !!box.current && getComputedStyle(box.current).flexDirection === 'column';
  const at = (e: React.PointerEvent, st: boolean) => {
    const r = box.current!.getBoundingClientRect();
    return clamp(st ? ((e.clientY - r.top) / r.height) * 100 : ((e.clientX - r.left) / r.width) * 100);
  };
  const set = (v: number) => { setFrac(v); save(v); };

  const onPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0 || !box.current) return;
    e.preventDefault();
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    const st = stacked();
    drag.current = { id: e.pointerId, stacked: st, v: frac };
    document.body.classList.add(st ? 'resizing-row' : 'resizing');
  };
  const onPointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    d.v = at(e, d.stacked);
    setFrac(d.v);
  };
  const end = (e: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    drag.current = null;
    document.body.classList.remove('resizing', 'resizing-row');
    save(d.v);
  };
  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    const dir = { ArrowLeft: -1, ArrowUp: -1, ArrowRight: 1, ArrowDown: 1 }[e.key];
    if (!dir) return;
    e.preventDefault();
    set(clamp(frac + dir * 5));
  };

  return (
    <div className="viewer-split" ref={box} style={{ ['--viewer-frac' as string]: `${frac}%` }}>
      <div className="viewer-pane">{pane}</div>
      <div
        className="viewer-divider"
        role="separator"
        aria-orientation="vertical"
        aria-valuenow={Math.round(frac)}
        tabIndex={0}
        title="Drag to give the document or the chat more room, double-click for half and half"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={end}
        onPointerCancel={end}
        onLostPointerCapture={end}
        onDoubleClick={() => set(DEF)}
        onKeyDown={onKeyDown}
      >
        <span className="viewer-grip" />
      </div>
      <div className="viewer-chat">{chat}</div>
    </div>
  );
}
