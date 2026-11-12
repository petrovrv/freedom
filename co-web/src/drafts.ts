// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.31.0: unsent composer text survives reloads and app upgrades (localStorage, per chat; drafts older
// than 30 days are pruned).
import { useCallback, useEffect, useRef, useState } from 'react';

const PREFIX = 'co.draft.';
const MAX_AGE_MS = 30 * 24 * 60 * 60 * 1000;
const DEBOUNCE_MS = 400;

interface StoredDraft {
  t: string;
  u: number;
}

function storageKey(key: string): string {
  return PREFIX + key;
}

function readDraft(key: string): string {
  try {
    const raw = localStorage.getItem(storageKey(key));
    if (!raw) return '';
    const parsed = JSON.parse(raw) as Partial<StoredDraft> | null;
    return parsed && typeof parsed.t === 'string' ? parsed.t : '';
  } catch {
    return '';
  }
}

function writeDraft(key: string, text: string): void {
  try {
    if (!text) {
      localStorage.removeItem(storageKey(key));
    } else {
      const value: StoredDraft = { t: text, u: Date.now() };
      localStorage.setItem(storageKey(key), JSON.stringify(value));
    }
  } catch {
    /* ignore quota / access errors */
  }
}

function removeDraft(key: string): void {
  try {
    localStorage.removeItem(storageKey(key));
  } catch {
    /* ignore */
  }
}

let pruned = false;

function pruneOldDrafts(): void {
  if (pruned) return;
  pruned = true;
  try {
    if (typeof localStorage === 'undefined') return;
    const now = Date.now();
    const toRemove: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i);
      if (!k || !k.startsWith(PREFIX)) continue;
      try {
        const raw = localStorage.getItem(k);
        const parsed = raw ? (JSON.parse(raw) as Partial<StoredDraft> | null) : null;
        if (
          !parsed ||
          typeof parsed.t !== 'string' ||
          typeof parsed.u !== 'number' ||
          now - parsed.u > MAX_AGE_MS
        ) {
          toRemove.push(k);
        }
      } catch {
        toRemove.push(k);
      }
    }
    for (const k of toRemove) {
      try {
        localStorage.removeItem(k);
      } catch {
        /* ignore */
      }
    }
  } catch {
    /* ignore */
  }
}

pruneOldDrafts();

interface DraftState {
  key: string;
  text: string;
  dirty: boolean;
}

interface Pending {
  key: string;
  text: string;
}

export function useDraft(
  key: string
): [string, (v: string | ((prev: string) => string)) => void, () => void] {
  const [state, setState] = useState<DraftState>(() => ({
    key,
    text: readDraft(key),
    dirty: false,
  }));

  let current = state;
  if (current.key !== key) {
    current = { key, text: readDraft(key), dirty: false };
    setState(current);
  }

  const pendingRef = useRef<Pending | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const keyRef = useRef(key);
  keyRef.current = key;

  const clearTimer = useCallback(() => {
    if (timerRef.current !== null) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const flush = useCallback(() => {
    clearTimer();
    const p = pendingRef.current;
    if (p) {
      pendingRef.current = null;
      writeDraft(p.key, p.text);
    }
  }, [clearTimer]);

  // Flush pending save to the OLD key on key change / unmount.
  useEffect(() => {
    return () => {
      flush();
    };
  }, [key, flush]);

  // Debounced save of user edits.
  useEffect(() => {
    if (!state.dirty || state.key !== key) return;
    pendingRef.current = { key: state.key, text: state.text };
    clearTimer();
    timerRef.current = setTimeout(() => {
      timerRef.current = null;
      flush();
    }, DEBOUNCE_MS);
    return clearTimer;
  }, [state, key, clearTimer, flush]);

  // Flush on page hide / unload.
  useEffect(() => {
    const onHide = () => flush();
    window.addEventListener('pagehide', onHide);
    window.addEventListener('beforeunload', onHide);
    return () => {
      window.removeEventListener('pagehide', onHide);
      window.removeEventListener('beforeunload', onHide);
    };
  }, [flush]);

  const setText = useCallback((v: string | ((prev: string) => string)) => {
    setState((prev) => {
      const next = typeof v === 'function' ? v(prev.text) : v;
      if (next === prev.text && prev.dirty) return prev;
      return { key: prev.key, text: next, dirty: true };
    });
  }, []);

  const clear = useCallback(() => {
    clearTimer();
    pendingRef.current = null;
    const k = keyRef.current;
    removeDraft(k);
    setState({ key: k, text: '', dirty: false });
  }, [clearTimer]);

  return [current.text, setText, clear];
}
