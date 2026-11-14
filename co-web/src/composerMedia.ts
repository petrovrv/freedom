// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (ext/1 media.audio, media.speech): microphone and speech recognition for module pages in the composer.
// A module page runs in a sandboxed frame with an opaque origin, and browsers refuse it the microphone ("Invalid security
// origin"), so CO Web captures on its behalf: it records audio and hands the recording to the page, or runs the
// browser's speech service and passes the text. What to do with it (which service transcribes, which languages, the
// button) is the module's business. The recognition and recording code came from the core's voice input (CO Web 0.30.0).
// Speech recognition runs in the browser's own speech service; CO Desktop (WebView2 has none) offers the platform's
// voice typing (Windows voice typing, Win+H), which types into the focused message box itself.

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Recognition = any;

function recognitionCtor(): (new () => Recognition) | null {
  const w = window as unknown as Record<string, unknown>;
  return (w.SpeechRecognition || w.webkitSpeechRecognition || null) as (new () => Recognition) | null;
}

/** The platform's own voice typing into the focused field (CO Desktop: Windows voice typing), or null. */
export function platformDictation(): (() => Promise<unknown>) | null {
  if (typeof window === 'undefined') return null;
  const d = (window as unknown as { coDesktop?: { voiceTyping?: unknown } }).coDesktop;
  return d && typeof d.voiceTyping === 'function' ? (d.voiceTyping as () => Promise<unknown>).bind(d) : null;
}

/** What this browser can capture for module pages (ext/1 context field media). */
export function mediaSupport(): { audio: boolean; speech: boolean; platform: boolean } {
  return {
    audio: typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== 'undefined',
    speech: !!recognitionCtor(),
    platform: !!platformDictation(),
  };
}

// ---------- speech recognition (Web Speech API) ----------

const NETWORK_ERROR = 'Speech service unreachable in this browser. On Windows press Win+H for Windows voice typing.';
const DENIED_ERROR = 'Microphone access was denied.';
const FAILED_ERROR = 'Speech recognition keeps failing; stopped.';
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

export interface SpeechUpdate { final: string; interim: string; active: boolean; error?: string }

/**
 * Continuous recognition: onUpdate gets the whole text since start (final + interim). Browsers end recognition after
 * a pause; it restarts while wanted, and stops for good after repeated quick failures.
 */
export class SpeechCapture {
  private rec: Recognition | null = null;
  private committed = '';
  private sessionFinal = '';
  private wanted = false;
  private failures = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private lang = '';
  private onUpdate: (u: SpeechUpdate) => void;
  constructor(onUpdate: (u: SpeechUpdate) => void) {
    this.onUpdate = onUpdate;
  }

  get active(): boolean { return this.wanted; }

  start(lang: string): void {
    this.dispose();
    this.lang = lang;
    this.committed = '';
    this.sessionFinal = '';
    this.failures = 0;
    this.wanted = true;
    this.launch();
  }

  stop(): void {
    this.wanted = false;
    this.clearTimer();
    try { this.rec?.stop(); } catch { /* already stopped */ }
    // rec is kept so onend still commits the last session
  }

  dispose(): void {
    this.wanted = false;
    this.clearTimer();
    const r = this.rec;
    this.rec = null;
    try { r?.abort?.(); } catch { /* ignore */ }
  }

  private clearTimer() {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  private commitSession() {
    this.committed = join(this.committed, this.sessionFinal);
    this.sessionFinal = '';
  }

  private halt(msg: string | null) {
    this.wanted = false;
    this.clearTimer();
    const r = this.rec;
    this.rec = null;
    try { r?.abort?.(); } catch { /* already stopped */ }
    this.commitSession();
    this.onUpdate({ final: this.committed, interim: '', active: false, ...(msg ? { error: msg } : {}) });
  }

  private scheduleRestart(quickFailure: boolean) {
    this.failures = quickFailure ? this.failures + 1 : 0;
    if (this.failures >= MAX_QUICK_FAILURES) {
      this.halt(FAILED_ERROR);
      return;
    }
    this.clearTimer();
    this.timer = setTimeout(() => {
      this.timer = null;
      if (this.wanted) this.launch();
    }, quickFailure ? 250 : 0);
  }

  // Creates a NEW recognition object per session (ended objects may refuse start()).
  private launch() {
    const C = recognitionCtor();
    if (!C) {
      this.halt('Speech recognition is not supported in this browser.');
      return;
    }
    const r: Recognition = new C();
    r.lang = this.lang || navigator.language || 'en-US';
    r.continuous = !isAndroid(); // Android: one utterance per session, restarted in onend
    r.interimResults = true;
    r.maxAlternatives = 1;
    const startedAt = Date.now();
    let gotResult = false;

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    r.onresult = (e: any) => {
      if (this.rec !== r) return;
      gotResult = true;
      this.failures = 0;
      const { final, interim } = buildSession(e.results);
      this.sessionFinal = final;
      this.onUpdate({ final: join(this.committed, this.sessionFinal), interim, active: true });
    };
    r.onerror = (e: { error: string }) => {
      switch (e.error) {
        case 'no-speech':
        case 'aborted':
          return;
        case 'network':
          this.halt(NETWORK_ERROR);
          return;
        case 'not-allowed':
        case 'service-not-allowed':
          this.halt(DENIED_ERROR);
          return;
        default:
          this.onUpdate({ final: join(this.committed, this.sessionFinal), interim: '', active: this.wanted, error: `Speech recognition error: ${e.error}` });
      }
    };
    r.onend = () => {
      if (this.rec !== r) return;
      this.commitSession();
      if (this.wanted) {
        this.onUpdate({ final: this.committed, interim: '', active: true });
        this.scheduleRestart(!gotResult && Date.now() - startedAt < QUICK_FAILURE_MS);
      } else {
        this.rec = null;
        this.onUpdate({ final: this.committed, interim: '', active: false });
      }
    };
    this.rec = r;
    try {
      r.start();
    } catch {
      this.scheduleRestart(true);
    }
  }
}

// ---------- audio recording (MediaRecorder) ----------

const MIME_CANDIDATES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];

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

/** File name with the extension of a recording's type (audio.webm, audio.m4a, audio.ogg). */
export function audioFileName(mime: string): string {
  const m = mime.toLowerCase();
  if (m.includes('mp4') || m.includes('m4a') || m.includes('aac')) return 'audio.m4a';
  if (m.includes('ogg')) return 'audio.ogg';
  return 'audio.webm';
}

function isPermissionError(err: unknown): boolean {
  const name = (err as { name?: string } | null)?.name ?? '';
  return name === 'NotAllowedError' || name === 'PermissionDeniedError' || name === 'SecurityError';
}

/**
 * One recording at a time: start asks for the microphone; stop resolves with the recording (or null when cancelled or
 * empty); it also stops by itself at maxSeconds (the promise of the next stop() then gets that recording).
 */
export class AudioCapture {
  private rec: MediaRecorder | null = null;
  private stream: MediaStream | null = null;
  private chunks: Blob[] = [];
  private tick: ReturnType<typeof setInterval> | null = null;
  private cancelled = false;
  private done: Promise<Blob | null> | null = null;
  private gen = 0;
  private starting = false;
  private level: { ctx: AudioContext; timer: ReturnType<typeof setInterval> } | null = null;
  private onTick: (u: { recording: boolean; seconds: number; silence?: boolean }) => void;
  constructor(onTick: (u: { recording: boolean; seconds: number; silence?: boolean }) => void) {
    this.onTick = onTick;
  }

  get recording(): boolean { return !!this.rec; }

  /** silenceMs > 0: stop by itself that long after speech went quiet (the event says silence: true; then stop()). */
  async start(maxSeconds: number, silenceMs = 0): Promise<void> {
    if (this.rec || this.starting) throw new Error('already recording');
    // a cancel (or unmount) while the microphone permission is pending must win over the late stream
    const gen = ++this.gen;
    this.starting = true;
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
    } catch (err) {
      throw new Error(isPermissionError(err) ? 'Microphone access was denied.'
        : `Microphone unavailable: ${err instanceof Error && err.message ? err.message : 'unknown error'}`);
    } finally {
      this.starting = false;
    }
    if (gen !== this.gen) {
      stream.getTracks().forEach((t) => t.stop());
      throw new Error('cancelled');
    }
    const mimeType = pickMimeType();
    let rec: MediaRecorder;
    try {
      rec = mimeType ? new MediaRecorder(stream, { mimeType }) : new MediaRecorder(stream);
    } catch (err) {
      stream.getTracks().forEach((t) => t.stop());
      throw new Error(`Recording failed: ${err instanceof Error && err.message ? err.message : 'unknown error'}`);
    }
    this.stream = stream;
    this.rec = rec;
    this.chunks = [];
    this.cancelled = false;
    rec.ondataavailable = (e: BlobEvent) => {
      if (e.data && e.data.size > 0) this.chunks.push(e.data);
    };
    this.done = new Promise<Blob | null>((resolve) => {
      rec.onstop = () => {
        this.release();
        const chunks = this.chunks;
        this.chunks = [];
        if (this.cancelled) return resolve(null);
        const type = (rec.mimeType || mimeType || 'audio/webm').split(';')[0].trim() || 'audio/webm';
        const blob = new Blob(chunks, { type });
        resolve(blob.size ? blob : null);
      };
      rec.onerror = () => {
        this.cancelled = true;
        try { rec.stop(); } catch { this.release(); resolve(null); }
      };
    });
    try {
      rec.start(1000);
    } catch (err) {
      this.cancelled = true;
      this.done = null;
      this.release();
      throw new Error(`Recording failed: ${err instanceof Error && err.message ? err.message : 'unknown error'}`);
    }
    const t0 = Date.now();
    this.onTick({ recording: true, seconds: 0 });
    if (silenceMs > 0) this.watchSilence(stream, silenceMs);
    this.tick = setInterval(() => {
      const s = Math.floor((Date.now() - t0) / 1000);
      this.onTick({ recording: true, seconds: Math.min(s, maxSeconds) });
      if (s >= maxSeconds) this.halt(false);
    }, 500);
  }

  /** Stops and returns the recording (null when there is none). */
  async stop(): Promise<Blob | null> {
    const done = this.done;
    this.done = null;
    this.halt(false);
    return done ? await done : null;
  }

  cancel(): void {
    this.gen++; // a start still waiting for the microphone stops its stream when it arrives
    this.halt(true);
  }

  private halt(cancel: boolean) {
    if (cancel) this.cancelled = true;
    const rec = this.rec;
    if (rec && rec.state !== 'inactive') {
      try { rec.stop(); } catch { this.release(); }
    } else {
      this.release();
    }
  }

  // voice mode: a level meter on the stream; speech = RMS over an adaptive floor, then quiet for silenceMs -> stop.
  // Nothing heard in the first 8 s stops too (the page gets an empty or short recording and says so).
  private watchSilence(stream: MediaStream, silenceMs: number) {
    const Ctx = (window as unknown as { AudioContext?: typeof AudioContext; webkitAudioContext?: typeof AudioContext }).AudioContext
      || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return;
    let ctx: AudioContext;
    try {
      ctx = new Ctx();
    } catch {
      return;
    }
    let an: AnalyserNode;
    try {
      an = ctx.createAnalyser();
      an.fftSize = 1024;
      ctx.createMediaStreamSource(stream).connect(an);
    } catch {
      void ctx.close().catch(() => undefined);
      return;
    }
    // a context may start suspended (Safari, no fresh gesture): the level would read 0 and stop every recording
    void ctx.resume().catch(() => undefined);
    const buf = new Float32Array(an.fftSize);
    let floor = 0.01, heard = false, quietSince = 0;
    const t0 = Date.now();
    const timer = setInterval(() => {
      if (ctx.state !== 'running') return; // no level to judge: the page stops it (button or limit)
      an.getFloatTimeDomainData(buf);
      let sum = 0;
      for (let i = 0; i < buf.length; i++) sum += buf[i] * buf[i];
      const rms = Math.sqrt(sum / buf.length);
      const now = Date.now();
      if (rms > Math.max(0.02, floor * 3)) {
        heard = true;
        quietSince = 0;
      } else {
        floor = floor * 0.95 + rms * 0.05;
        if (heard && !quietSince) quietSince = now;
      }
      if ((heard && quietSince && now - quietSince >= silenceMs) || (!heard && now - t0 > 8000)) {
        this.onTick({ recording: true, seconds: Math.floor((now - t0) / 1000), silence: true });
        this.halt(false);
      }
    }, 100);
    this.level = { ctx, timer };
  }

  private release() {
    if (this.level) {
      clearInterval(this.level.timer);
      void this.level.ctx.close().catch(() => undefined);
      this.level = null;
    }
    if (this.tick !== null) {
      clearInterval(this.tick);
      this.tick = null;
    }
    this.stream?.getTracks().forEach((t) => {
      try { t.stop(); } catch { /* ignore */ }
    });
    this.stream = null;
    if (this.rec) {
      this.rec = null;
      this.onTick({ recording: false, seconds: 0 });
    }
  }
}

// ---------- audio playback (ext/1 media.play) ----------

/**
 * Plays an answer for a module page (voice mode): the page's frame has no media of its own (CSP), so CO Web plays the
 * bytes; play() resolves when the clip ends or is stopped. One clip at a time per frame.
 */
export class AudioPlayback {
  private el: HTMLAudioElement | null = null;
  private url = '';
  private finish: (() => void) | null = null;

  play(bytes: ArrayBuffer, type: string): Promise<{ ended: boolean }> {
    this.stop();
    this.url = URL.createObjectURL(new Blob([bytes], { type }));
    const el = new Audio(this.url);
    this.el = el;
    return new Promise((resolve, reject) => {
      const done = (ended: boolean) => {
        if (this.el === el) this.cleanup();
        resolve({ ended });
      };
      this.finish = () => done(false);
      el.onended = () => done(true);
      el.onerror = () => {
        if (this.el === el) this.cleanup();
        reject(new Error('This browser cannot play the answer.'));
      };
      el.play().catch((e) => {
        if (this.el === el) this.cleanup();
        reject(new Error(e instanceof Error && e.name === 'NotAllowedError' ? 'Tap the page once so the browser lets it speak.' : 'Playback failed.'));
      });
    });
  }

  stop(): void {
    const f = this.finish;
    try { this.el?.pause(); } catch { /* ignore */ }
    this.cleanup();
    f?.();
  }

  private cleanup() {
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = '';
    this.el = null;
    this.finish = null;
  }
}
