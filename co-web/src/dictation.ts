// SPDX-License-Identifier: AGPL-3.0-or-later
// Voice dictation through the browser Web Speech API (Chrome, Edge, Safari).
// Recognition runs in the browser's own speech service; CO sends no audio anywhere.
// CO Desktop (WebView2 has no speech service) uses Windows voice typing (Win+H) instead.
import { useEffect, useRef, useState } from 'react';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Recognition = any;

export const DICTATION_LANGS: { id: string; label: string }[] = [
  { id: 'ru-RU', label: 'RU' },
  { id: 'uk-UA', label: 'UK' },
  { id: 'en-US', label: 'EN' },
  { id: 'nl-BE', label: 'NL' },
  { id: 'fr-BE', label: 'FR' },
  { id: 'de-DE', label: 'DE' },
];

const LANG_KEY = 'co.dictationLang';

function ctor(): (new () => Recognition) | null {
  const w = window as unknown as Record<string, unknown>;
  return (w.SpeechRecognition || w.webkitSpeechRecognition || null) as (new () => Recognition) | null;
}

export const dictationSupported = () => !!ctor();

function loadLang(): string {
  try {
    return localStorage.getItem(LANG_KEY) || 'ru-RU';
  } catch {
    return 'ru-RU';
  }
}

// CO Desktop exposes native Windows voice typing (Win+H).
export function desktopVoiceTyping(): (() => Promise<unknown>) | null {
  if (typeof window === 'undefined') return null;
  const d = (window as unknown as { coDesktop?: { voiceTyping?: unknown } }).coDesktop;
  return d && typeof d.voiceTyping === 'function' ? (d.voiceTyping as () => Promise<unknown>).bind(d) : null;
}

const NETWORK_ERROR = 'Speech service unreachable in this browser. On Windows press Win+H for Windows voice typing.';
const DENIED_ERROR = 'Microphone access was denied.';
const FAILED_ERROR = 'Speech recognition keeps failing; dictation stopped.';
const MAX_QUICK_FAILURES = 3;
const QUICK_FAILURE_MS = 1000;

const isAndroid = () => typeof navigator !== 'undefined' && /Android/i.test(navigator.userAgent);
const join = (a: string, b: string) => (a && b ? a + ' ' + b : a || b);

// Rebuilds the current session text from ALL results: Android re-delivers results and may send
// cumulative finals, so appending from resultIndex duplicated words.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function buildSession(results: any): { final: string; interim: string } {
  let final = '';
  let interim = '';
  let prev = '';
  for (let i = 0; i < results.length; i++) {
    const res = results[i];
    const t = String(res?.[0]?.transcript ?? '').trim();
    if (!t) continue;
    if (res.isFinal) {
      const low = t.toLowerCase();
      if (low === prev) continue; // duplicate final
      // Cumulative final: replaces what was accumulated so far
      final = final && low.startsWith(final.toLowerCase()) ? t : join(final, t);
      prev = low;
    } else {
      interim = join(interim, t);
    }
  }
  return { final, interim };
}

// Calls onText(finalText, interimText) with the text dictated since start().
export function useDictation(onText: (finalText: string, interim: string) => void) {
  const [active, setActive] = useState(false);
  const [lang, setLangState] = useState(loadLang);
  const [error, setError] = useState<string | null>(null);
  const rec = useRef<Recognition | null>(null);
  const committed = useRef(''); // text of finished sessions
  const sessionFinal = useRef(''); // finals of the current session
  const wanted = useRef(false);
  const failures = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const langRef = useRef(lang);
  langRef.current = lang;
  const cb = useRef(onText);
  cb.current = onText;

  const setLang = (l: string) => {
    setLangState(l);
    try {
      localStorage.setItem(LANG_KEY, l);
    } catch {
      /* per-viewer convenience only */
    }
  };

  function clearTimer() {
    if (timer.current) {
      clearTimeout(timer.current);
      timer.current = null;
    }
  }

  // Moves the current session into committed text.
  function commitSession() {
    committed.current = join(committed.current, sessionFinal.current);
    sessionFinal.current = '';
  }

  // Stops for good (fatal error or too many failures).
  function halt(msg: string | null) {
    wanted.current = false;
    clearTimer();
    const r = rec.current;
    rec.current = null;
    try {
      r?.abort?.();
    } catch {
      /* already stopped */
    }
    commitSession();
    cb.current(committed.current, '');
    setActive(false);
    if (msg) setError(msg);
  }

  function scheduleRestart(quickFailure: boolean) {
    failures.current = quickFailure ? failures.current + 1 : 0;
    if (failures.current >= MAX_QUICK_FAILURES) {
      halt(FAILED_ERROR);
      return;
    }
    clearTimer();
    timer.current = setTimeout(() => {
      timer.current = null;
      if (wanted.current) launch();
    }, quickFailure ? 250 : 0);
  }

  // Creates a NEW recognition object per session (ended objects may refuse start()).
  function launch() {
    const C = ctor();
    if (!C) {
      halt('Dictation is not supported in this browser.');
      return;
    }
    const r: Recognition = new C();
    r.lang = langRef.current;
    r.continuous = !isAndroid(); // Android: one utterance per session, restarted in onend
    r.interimResults = true;
    r.maxAlternatives = 1;
    const startedAt = Date.now();
    let gotResult = false;

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    r.onresult = (e: any) => {
      if (rec.current !== r) return;
      gotResult = true;
      failures.current = 0;
      const { final, interim } = buildSession(e.results);
      sessionFinal.current = final;
      cb.current(join(committed.current, sessionFinal.current), interim);
    };

    r.onerror = (e: { error: string }) => {
      switch (e.error) {
        case 'no-speech':
        case 'aborted':
          return;
        case 'network':
          halt(NETWORK_ERROR);
          return;
        case 'not-allowed':
        case 'service-not-allowed':
          halt(DENIED_ERROR);
          return;
        default:
          setError(`Dictation error: ${e.error}`);
      }
    };

    // Browsers end recognition after a pause; restart while the user keeps it on.
    r.onend = () => {
      if (rec.current !== r) return;
      commitSession();
      cb.current(committed.current, '');
      if (wanted.current) scheduleRestart(!gotResult && Date.now() - startedAt < QUICK_FAILURE_MS);
      else setActive(false);
    };

    rec.current = r;
    try {
      r.start();
    } catch {
      scheduleRestart(true);
    }
  }

  function start() {
    setError(null);
    committed.current = '';
    sessionFinal.current = '';
    failures.current = 0;
    wanted.current = true;
    setActive(true);
    launch();
  }

  function stop() {
    wanted.current = false;
    clearTimer();
    try {
      rec.current?.stop();
    } catch {
      /* already stopped */
    }
    setActive(false);
    // rec.current is kept so onend still commits the last session
  }

  useEffect(
    () => () => {
      wanted.current = false;
      clearTimer();
      const r = rec.current;
      rec.current = null;
      try {
        r?.abort?.();
      } catch {
        /* ignore */
      }
    },
    [],
  );

  return { active, start, stop, lang, setLang, error, supported: dictationSupported() || !!desktopVoiceTyping() };
}
