// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.31.0: draggable column dividers. Widths live in CSS custom properties on <html> (--left-w, --right-w)
// and in localStorage; double-click resets, arrow keys move by 16 px.
import React, { useCallback, useEffect, useLayoutEffect, useRef } from 'react';

const KEY_STEP = 16;

function clamp(v: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, v));
}

function readStored(storageKey: string, def: number, min: number, max: number): number {
  try {
    const raw = localStorage.getItem(storageKey);
    if (raw !== null) {
      const n = Number(raw);
      if (Number.isFinite(n)) return clamp(n, min, max);
    }
  } catch {
    /* ignore */
  }
  return clamp(def, min, max);
}

function saveStored(storageKey: string, value: number): void {
  try {
    localStorage.setItem(storageKey, String(Math.round(value)));
  } catch {
    /* ignore */
  }
}

function setCssVar(cssVar: string, px: number): void {
  document.documentElement.style.setProperty(cssVar, `${Math.round(px)}px`);
}

function readCssVar(cssVar: string): number | null {
  const raw =
    document.documentElement.style.getPropertyValue(cssVar) ||
    getComputedStyle(document.documentElement).getPropertyValue(cssVar);
  const n = parseFloat(raw);
  return Number.isFinite(n) ? n : null;
}

export function useColumnWidth(
  cssVar: string,
  storageKey: string,
  def: number,
  min: number,
  max: number
): void {
  useLayoutEffect(() => {
    setCssVar(cssVar, readStored(storageKey, def, min, max));
  }, [cssVar, storageKey, def, min, max]);
}

interface ResizerProps {
  cssVar: string;
  storageKey: string;
  side: 'left' | 'right';
  def: number;
  min: number;
  max: number;
}

interface DragState {
  pointerId: number;
  startX: number;
  startWidth: number;
  width: number;
}

export function Resizer({ cssVar, storageKey, side, def, min, max }: ResizerProps) {
  const dragRef = useRef<DragState | null>(null);
  const rafRef = useRef<number | null>(null);

  const currentWidth = useCallback((): number => {
    const v = readCssVar(cssVar);
    return v !== null ? clamp(v, min, max) : readStored(storageKey, def, min, max);
  }, [cssVar, storageKey, def, min, max]);

  const cancelFrame = () => {
    if (rafRef.current !== null) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
  };

  useEffect(() => {
    return () => {
      cancelFrame();
      if (dragRef.current) {
        dragRef.current = null;
        document.body.classList.remove('resizing');
      }
    };
  }, []);

  const onPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    const w = currentWidth();
    dragRef.current = { pointerId: e.pointerId, startX: e.clientX, startWidth: w, width: w };
    document.body.classList.add('resizing');
  };

  const onPointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    const dx = e.clientX - d.startX;
    d.width = clamp(side === 'left' ? d.startWidth + dx : d.startWidth - dx, min, max);
    if (rafRef.current === null) {
      rafRef.current = requestAnimationFrame(() => {
        rafRef.current = null;
        if (dragRef.current) setCssVar(cssVar, dragRef.current.width);
      });
    }
  };

  const endDrag = (e: React.PointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    dragRef.current = null;
    cancelFrame();
    try {
      if (e.currentTarget.hasPointerCapture(e.pointerId)) {
        e.currentTarget.releasePointerCapture(e.pointerId);
      }
    } catch {
      /* ignore */
    }
    setCssVar(cssVar, d.width);
    saveStored(storageKey, d.width);
    document.body.classList.remove('resizing');
  };

  const onDoubleClick = () => {
    const w = clamp(def, min, max);
    setCssVar(cssVar, w);
    saveStored(storageKey, w);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    e.preventDefault();
    const dir = e.key === 'ArrowRight' ? 1 : -1;
    const delta = side === 'left' ? dir * KEY_STEP : -dir * KEY_STEP;
    const w = clamp(currentWidth() + delta, min, max);
    setCssVar(cssVar, w);
    saveStored(storageKey, w);
  };

  return (
    <div
      className={`resizer resizer-${side}`}
      role="separator"
      aria-orientation="vertical"
      tabIndex={0}
      title="Drag to resize, double-click to reset"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={endDrag}
      onPointerCancel={endDrag}
      onLostPointerCapture={endDrag}
      onDoubleClick={onDoubleClick}
      onKeyDown={onKeyDown}
    />
  );
}

// CO Web 0.64.0: horizontal divider above the composer. Dragging it up makes the message box taller and the
// message list shorter. Height lives in --composer-h on <html> and in localStorage; double-click resets,
// arrow keys move by 16 px. The box grows only by what the message list can give up: the list keeps at least
// minRest px, so the Send row never leaves the screen (CSS caps the stored height on smaller windows).
interface RowResizerProps {
  cssVar: string;
  storageKey: string;
  def: number;
  min: number;
  minRest: number;
}

interface RowDragState {
  pointerId: number;
  startY: number;
  startHeight: number;
  height: number;
  max: number;
}

export function RowResizer({ cssVar, storageKey, def, min, minRest }: RowResizerProps) {
  const dragRef = useRef<RowDragState | null>(null);
  const rafRef = useRef<number | null>(null);
  const selfRef = useRef<HTMLDivElement>(null);

  // Rendered height of the box now, and the largest height that leaves the message list minRest px.
  const measure = (): { now: number; max: number } => {
    const box = selfRef.current?.parentElement?.querySelector('textarea');
    const list = selfRef.current?.closest('.chat')?.querySelector('.messages');
    const now = box ? box.offsetHeight : clamp(readCssVar(cssVar) ?? def, min, Number.MAX_SAFE_INTEGER);
    const max = list ? now + (list as HTMLElement).clientHeight - minRest : Math.round(window.innerHeight * 0.5);
    return { now, max: Math.max(min, max) };
  };

  useLayoutEffect(() => {
    setCssVar(cssVar, readStored(storageKey, def, min, Number.MAX_SAFE_INTEGER));
  }, [cssVar, storageKey, def, min]);

  useEffect(() => {
    return () => {
      if (rafRef.current !== null) cancelAnimationFrame(rafRef.current);
      if (dragRef.current) {
        dragRef.current = null;
        document.body.classList.remove('resizing-row');
      }
    };
  }, []);

  const apply = (h: number, save: boolean) => {
    setCssVar(cssVar, h);
    if (save) saveStored(storageKey, h);
  };

  const onPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    const { now, max } = measure();
    dragRef.current = { pointerId: e.pointerId, startY: e.clientY, startHeight: now, height: now, max };
    document.body.classList.add('resizing-row');
  };

  const onPointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    d.height = clamp(d.startHeight - (e.clientY - d.startY), min, d.max);
    if (rafRef.current === null) {
      rafRef.current = requestAnimationFrame(() => {
        rafRef.current = null;
        if (dragRef.current) setCssVar(cssVar, dragRef.current.height);
      });
    }
  };

  const endDrag = (e: React.PointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    dragRef.current = null;
    if (rafRef.current !== null) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
    try {
      if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    apply(d.height, true);
    document.body.classList.remove('resizing-row');
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
    e.preventDefault();
    const { now, max } = measure();
    apply(clamp(now + (e.key === 'ArrowUp' ? KEY_STEP : -KEY_STEP), min, max), true);
  };

  return (
    <div
      ref={selfRef}
      className="row-resizer"
      role="separator"
      aria-orientation="horizontal"
      aria-label="Resize message box"
      tabIndex={0}
      title="Drag to resize the message box, double-click to reset"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={endDrag}
      onPointerCancel={endDrag}
      onLostPointerCapture={endDrag}
      onDoubleClick={() => apply(clamp(def, min, measure().max), true)}
      onKeyDown={onKeyDown}
    />
  );
}
