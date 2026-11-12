// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.30.0: voice input through Whisper. The browser records audio (MediaRecorder) and /agent/transcribe.php
// forwards it to a Whisper-compatible API (OpenAI or Groq) configured on the server. Without a configured key the
// composer keeps the browser's Web Speech dictation (dictation.ts); CO Desktop keeps Windows voice typing (Win+H).
import { useEffect, useRef, useState } from 'react';
import { getToken } from './auth';

const ENDPOINT = '/agent/transcribe.php';
const MAX_SECONDS = 300;
const MIME_CANDIDATES = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/mp4',
  'audio/ogg;codecs=opus',
];

let configuredCache: boolean | null = null;
let configuredPending: Promise<boolean> | null = null;

async function authHeaders(): Promise<Record<string, string>> {
  const headers: Record<string, string> = { 'X-CO-Auth': '1' };
  try {
    const token = await Promise.resolve(getToken());
    if (token) headers['Authorization'] = `Bearer ${token}`;
  } catch {
    /* no token */
  }
  return headers;
}

export async function transcribeConfigured(): Promise<boolean> {
  if (configuredCache !== null) return configuredCache;
  if (configuredPending) return configuredPending;

  configuredPending = (async () => {
    try {
      const res = await fetch(ENDPOINT + '?status=1', {
        method: 'POST',
        headers: await authHeaders(),
        credentials: 'same-origin',
      });
      if (!res.ok) return false;
      const data = await res.json();
      const value = !!(data && data.configured === true);
      configuredCache = value;
      return value;
    } catch {
      return false;
    } finally {
      configuredPending = null;
    }
  })();

  return configuredPending;
}

function pickMimeType(): string {
  if (typeof MediaRecorder === 'undefined' || typeof MediaRecorder.isTypeSupported !== 'function') return '';
  for (const t of MIME_CANDIDATES) {
    try {
      if (MediaRecorder.isTypeSupported(t)) return t;
    } catch {
      /* ignore */
    }
  }
  return '';
}

function fileNameForMime(mime: string): string {
  const m = mime.toLowerCase();
  if (m.includes('mp4') || m.includes('m4a') || m.includes('aac')) return 'audio.m4a';
  if (m.includes('ogg')) return 'audio.ogg';
  return 'audio.webm';
}

function isPermissionError(err: unknown): boolean {
  const name = (err as { name?: string } | null)?.name ?? '';
  return name === 'NotAllowedError' || name === 'PermissionDeniedError' || name === 'SecurityError';
}

export function useRecorder(
  onText: (text: string) => void,
  lang: string,
): {
  supported: boolean;
  recording: boolean;
  busy: boolean;
  error: string | null;
  seconds: number;
  start(): Promise<void>;
  stop(): void;
  cancel(): void;
} {
  const supported =
    typeof navigator !== 'undefined' &&
    !!navigator.mediaDevices?.getUserMedia &&
    typeof MediaRecorder !== 'undefined';

  const [recording, setRecording] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [seconds, setSeconds] = useState(0);

  const recorderRef = useRef<MediaRecorder | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const cancelledRef = useRef(false);
  const startingRef = useRef(false);
  const mountedRef = useRef(true);
  const onTextRef = useRef(onText);
  const langRef = useRef(lang);

  useEffect(() => {
    onTextRef.current = onText;
  }, [onText]);

  useEffect(() => {
    langRef.current = lang;
  }, [lang]);

  const clearTimer = () => {
    if (timerRef.current !== null) {
      clearInterval(timerRef.current);
      timerRef.current = null;
    }
  };

  const releaseTracks = () => {
    const s = streamRef.current;
    if (s) {
      s.getTracks().forEach((t) => {
        try {
          t.stop();
        } catch {
          /* ignore */
        }
      });
    }
    streamRef.current = null;
  };

  const upload = async (blob: Blob) => {
    if (mountedRef.current) setBusy(true);
    try {
      const form = new FormData();
      form.append('audio', blob, fileNameForMime(blob.type));
      if (langRef.current) form.append('lang', langRef.current);

      const res = await fetch(ENDPOINT, {
        method: 'POST',
        headers: await authHeaders(),
        credentials: 'same-origin',
        body: form,
      });

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      let data: any = null;
      try {
        data = await res.json();
      } catch {
        data = null;
      }

      if (!mountedRef.current) return;

      if (res.status === 503) {
        setError('Voice input is not configured on the server.');
        return;
      }
      if (!res.ok) {
        const detail =
          (data && typeof data.detail === 'string' && data.detail) ||
          (data && typeof data.error === 'string' && data.error) ||
          `HTTP ${res.status}`;
        setError(`Transcription failed: ${detail}`);
        return;
      }

      const text = data && typeof data.text === 'string' ? data.text.trim() : '';
      if (text) onTextRef.current(text);
    } catch (err) {
      if (mountedRef.current) {
        const msg = err instanceof Error && err.message ? err.message : 'network error';
        setError(`Transcription failed: ${msg}`);
      }
    } finally {
      if (mountedRef.current) setBusy(false);
    }
  };

  const halt = (cancel: boolean) => {
    cancelledRef.current = cancel;
    clearTimer();
    const rec = recorderRef.current;
    if (rec && rec.state !== 'inactive') {
      try {
        rec.stop();
      } catch {
        /* ignore */
      }
    } else {
      recorderRef.current = null;
    }
    releaseTracks();
    if (cancel) chunksRef.current = [];
    if (mountedRef.current) setRecording(false);
  };

  const stop = () => {
    if (!recorderRef.current) return;
    halt(false);
  };

  const cancel = () => {
    halt(true);
    if (mountedRef.current) setSeconds(0);
  };

  const start = async () => {
    if (!supported || recorderRef.current || startingRef.current || busy) return;
    startingRef.current = true;
    setError(null);
    setSeconds(0);
    cancelledRef.current = false;
    chunksRef.current = [];

    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true },
      });
    } catch (err) {
      startingRef.current = false;
      if (mountedRef.current) {
        setError(
          isPermissionError(err)
            ? 'Microphone access was denied.'
            : `Microphone unavailable: ${err instanceof Error && err.message ? err.message : 'unknown error'}`,
        );
      }
      return;
    }

    if (!mountedRef.current) {
      stream.getTracks().forEach((t) => t.stop());
      startingRef.current = false;
      return;
    }

    const mimeType = pickMimeType();
    let rec: MediaRecorder;
    try {
      rec = mimeType ? new MediaRecorder(stream, { mimeType }) : new MediaRecorder(stream);
    } catch (err) {
      stream.getTracks().forEach((t) => t.stop());
      startingRef.current = false;
      setError(`Recording failed: ${err instanceof Error && err.message ? err.message : 'unknown error'}`);
      return;
    }

    streamRef.current = stream;
    recorderRef.current = rec;

    rec.ondataavailable = (e: BlobEvent) => {
      if (e.data && e.data.size > 0) chunksRef.current.push(e.data);
    };

    rec.onerror = () => {
      if (mountedRef.current) setError('Recording failed.');
      halt(true);
    };

    rec.onstop = () => {
      clearTimer();
      releaseTracks();
      recorderRef.current = null;
      const chunks = chunksRef.current;
      chunksRef.current = [];
      if (cancelledRef.current || !mountedRef.current) return;

      const rawType = rec.mimeType || mimeType || 'audio/webm';
      const baseType = rawType.split(';')[0].trim() || 'audio/webm';
      const blob = new Blob(chunks, { type: baseType });
      if (blob.size === 0) {
        setError('No audio was recorded.');
        return;
      }
      void upload(blob);
    };

    try {
      rec.start(1000);
    } catch (err) {
      recorderRef.current = null;
      releaseTracks();
      startingRef.current = false;
      setError(`Recording failed: ${err instanceof Error && err.message ? err.message : 'unknown error'}`);
      return;
    }

    const t0 = Date.now();
    timerRef.current = setInterval(() => {
      const s = Math.floor((Date.now() - t0) / 1000);
      if (mountedRef.current) setSeconds(Math.min(s, MAX_SECONDS));
      if (s >= MAX_SECONDS) halt(false);
    }, 250);

    startingRef.current = false;
    setRecording(true);
  };

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      cancelledRef.current = true;
      if (timerRef.current !== null) {
        clearInterval(timerRef.current);
        timerRef.current = null;
      }
      const rec = recorderRef.current;
      if (rec && rec.state !== 'inactive') {
        try {
          rec.stop();
        } catch {
          /* ignore */
        }
      }
      recorderRef.current = null;
      const s = streamRef.current;
      if (s) s.getTracks().forEach((t) => t.stop());
      streamRef.current = null;
      chunksRef.current = [];
    };
  }, []);

  return { supported, recording, busy, error, seconds, start, stop, cancel };
}
