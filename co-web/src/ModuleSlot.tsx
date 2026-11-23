// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6): slots for module pages. CO Web knows no module by name: it asks db.php modules_list which
// modules are on, and shows each page a module declared for a slot in a sandboxed iframe without same-origin:
//   ui.header   - inline in the header, sized by the page;  ui.page - a whole page in the centre (ModulePage);
//   ui.composer - in the action row of the message box;  ui.hint - a hidden page that reads the composer text
//                 (permission composer.read) and answers with a hint.
// The page talks to CO Web only through ext/1 (extBridge.ts): context, calls of its own declared ops, generic core calls
// of the points it has the permission for (coreCalls, given by the place that shows the slot), its size, its hint; with
// permissions also the message box, microphone and speech of the composer slot (composerMedia.ts), uploads and a little
// storage. A module with data about people (manifest personal) asks a person whose switch is "ask" right in the slot.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AudioCapture, audioFileName, AudioPlayback, mediaSupport, platformDictation, SpeechCapture } from './composerMedia';
import { dbModuleCall, dbModuleList, dbModulePerson, dbModuleUpload } from './dbapi';
import { callRoute, capabilityRefusal, deviceOpSlot, moduleUrl, navTarget, parseExtMessage, reqRefusal, setModuleBadge, SLOT_PERMISSION, THEME_VARS, type ExtRequest, type ModuleHint, type ModuleInfo, type ModuleUi } from './extBridge';
import { setModuleList } from './moduleStore';
import { fileService } from './moduleServices';
import type { ChatEvent } from './types';

import { useInterval } from './hooks';
import { APP_REFRESH } from './appEvents';
import { setImportSources } from './importPoint';

/** Handlers of generic core calls ("core.<point>.<call>") a slot offers its pages; refused unless the manifest declares the permission. */
export type CoreCalls = Record<string, (args: Record<string, unknown>, caller: ModuleInfo) => Promise<unknown>>;

const RELOAD_MS = 5 * 60 * 1000;
export const MODULES_CHANGED = 'co-modules-changed';
const MODULES_LOADED = 'co-modules-loaded';
let lastList: ModuleInfo[] = [];

/** The list the shell loaded last (useModules), for places deeper in the tree such as the composer. */
export function useModuleList(): ModuleInfo[] {
  const [list, setList] = useState<ModuleInfo[]>(lastList);
  useEffect(() => {
    const f = () => setList(lastList);
    f();
    window.addEventListener(MODULES_LOADED, f);
    return () => window.removeEventListener(MODULES_LOADED, f);
  }, []);
  return list;
}

/** The module part of window.coDesktop (CO Desktop, CO Resident): device ops and the per-device switch (ext/1 device.tools). */
export interface DeviceModules {
  moduleOp?: (module: string, op: string, args: Record<string, unknown>) => Promise<unknown>;
  moduleDevice?: () => Promise<{ id: string; on: boolean; active: boolean; tools: string[]; problem: string; warning: string }[]>;
  moduleDeviceSet?: (module: string, on: boolean) => Promise<unknown>;
}
export function deviceModules(): DeviceModules | null {
  const d = (window as unknown as { coDesktop?: DeviceModules }).coDesktop;
  return d && typeof d.moduleOp === 'function' ? d : null;
}

/** Enabled modules of the signed-in person (the owner also gets the disabled ones); reloads on MODULES_CHANGED. */
export function useModules(authOk: boolean): ModuleInfo[] {
  const [list, setList] = useState<ModuleInfo[]>([]);
  const load = useCallback(async () => {
    if (!authOk) return;
    try {
      const { modules: l, importLabels } = await dbModuleList();
      // a source an enabled module declares wins over the label of a switched-off one
      setImportSources([...importLabels, ...l.filter((m) => m.enabled).flatMap((m) => m.imports || [])]);
      lastList = l;
      setList(l);
      setModuleList(l);
      window.dispatchEvent(new Event(MODULES_LOADED));
    } catch { /* keep the last list (database down, schema < 27) */ }
  }, [authOk]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    const f = () => void load();
    window.addEventListener(MODULES_CHANGED, f);
    return () => window.removeEventListener(MODULES_CHANGED, f);
  }, [load]);
  useInterval(() => void load(), RELOAD_MS, authOk);
  return list;
}

/** The list the shell loaded (useModules), for slots deeper in the page such as the composer; follows its reloads. */
export function useLoadedModules(): ModuleInfo[] {
  const [list, setList] = useState<ModuleInfo[]>(lastList);
  useEffect(() => {
    const f = () => setList(lastList);
    window.addEventListener(MODULES_LOADED, f);
    f();
    return () => window.removeEventListener(MODULES_LOADED, f);
  }, []);
  return list;
}

/** The message box as the ui.composer slot sees it (ext/1 composer.input). */
export interface ComposerInput {
  /** text of the current input session (and its grey interim tail); done ends the session */
  set(text: string, interim: string, done: boolean): void;
  /** caret to the end of the box (platform voice typing types there itself) */
  focus(): void;
  /** ext/1 composer.send: the text goes out as the chat's next message with the composer's agent and model */
  send?(text: string): Promise<void>;
  /** ext/1 chat.read in the composer: the messages of the chat (voice mode waits for the agent's answer) */
  events?: ChatEvent[];
}

const STORE_PREFIX = 'co.ext.';
const SEND_TOUCH_MS = 10 * 60_000;
const SEND_PER_TOUCH = 10;
/** The composer sent its message: composer pages get the event 'sent', speech recognition stops without a last result. */
export const COMPOSER_SENT = 'co-composer-sent';

function theme(el: HTMLElement | null): Record<string, string> {
  const out: Record<string, string> = {};
  if (!el) return out;
  const cs = getComputedStyle(el);
  for (const v of THEME_VARS) {
    const x = cs.getPropertyValue(v).trim();
    if (x) out[v] = x;
  }
  out.font = cs.fontFamily;
  out.scheme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  return out;
}

interface FrameProps {
  m: ModuleInfo; ui: ModuleUi; me?: string; owner: boolean;
  /** inline: sized by the page (header); fill: the whole box (page); hidden: no box at all (hint) */
  mode?: 'inline' | 'fill' | 'hidden';
  coreCalls?: CoreCalls;
  /** extra context of the place (e.g. the environment of a page) */
  extra?: Record<string, unknown>;
  /** text of the composer for a ui.hint page (only sent when the module has the slot's permission) */
  composer?: string;
  onHint?: (h: ModuleHint | null) => void;
  input?: ComposerInput;
  /** Drive root for the file services of ext/1 (file.*) */
  rootId?: string | null;
  /** a viewer pane of a chat mode (ui.panel): its chat and what it may do there */
  panel?: PanelLink;
  /** a full-window page shown now (false: an app kept alive while hidden, F14e.1); its title applies only while shown */
  active?: boolean;
  /** Esc the page did not use itself (ext/1 'key'): the shell leaves the app */
  onEscape?: () => void;
  /** "Open it again" of a full-window page that reloaded itself and was closed (the shell mounts a fresh frame) */
  onReopen?: () => void;
}

/** What a viewer pane (ui.panel of a chat mode) is linked to: its chat, and what it may do there. */
export interface PanelLink {
  chat: { id: string | null; title: string };
  events: ChatEvent[] | null;
  canAsk: boolean;
  ask: (text: string, files: File[]) => void;
  reveal: (eventId: string) => void;
  close: () => void;
}

// the fields of chat events a viewer gets (permission chat.read)
const slimEvent = (e: ChatEvent) => ({ eventId: e.eventId, type: e.type, text: e.text ?? '', status: e.status ?? null, createdUtc: e.createdUtc, jobId: e.jobId ?? null });

/** Core services of ext/1 for a page (modules/EXT1.md; the permission is checked by the caller). */
async function service(name: string, args: Record<string, unknown>, rootId: string | null, panel: PanelLink | undefined): Promise<{ data: unknown; transfer: Transferable[] }> {
  if (name.startsWith('file.')) {
    if (!rootId) throw new Error('Google Drive is not ready yet.');
    return fileService(rootId, name, args);
  }
  if (!panel) throw new Error('not_a_panel');
  if (name === 'chat.ask') {
    if (!panel.canAsk) throw new Error('This chat does not take questions now.');
    const text = typeof args.text === 'string' ? args.text.slice(0, 100_000) : '';
    if (!text.trim()) throw new Error('empty question');
    const files = (Array.isArray(args.files) ? args.files : []).slice(0, 10).flatMap((f) => {
      const x = f as { name?: unknown; type?: unknown; bytes?: unknown };
      if (typeof x?.name !== 'string' || !(x.bytes instanceof ArrayBuffer) || /[/\\]/.test(x.name)) return [];
      return [new File([x.bytes], x.name.slice(0, 120), { type: typeof x.type === 'string' ? x.type : 'application/octet-stream' })];
    });
    panel.ask(text, files);
    return { data: { ok: true }, transfer: [] };
  }
  if (name === 'chat.reveal') {
    if (typeof args.eventId !== 'string') throw new Error('invalid eventId');
    panel.reveal(args.eventId);
    return { data: { ok: true }, transfer: [] };
  }
  if (name === 'panel.close') {
    panel.close();
    return { data: { ok: true }, transfer: [] };
  }
  throw new Error('unknown_request');
}

export function ModuleFrame({ m, ui, me, owner, mode = 'inline', coreCalls, extra, composer, onHint, input, rootId, panel, active = true, onEscape, onReopen }: FrameProps) {
  const frame = useRef<HTMLIFrameElement>(null);
  const wrap = useRef<HTMLSpanElement>(null);
  const [size, setSize] = useState<{ w: number; h: number; overlay: boolean }>({ w: 0, h: 0, overlay: false });
  const [closed, setClosed] = useState<{ w: number; h: number }>({ w: 0, h: 0 });
  const mod = useRef(m);
  mod.current = m;
  const calls = useRef(coreCalls);
  calls.current = coreCalls;
  const hintCb = useRef(onHint);
  hintCb.current = onHint;
  const escCb = useRef(onEscape);
  escCb.current = onEscape;
  const activeRef = useRef(active);
  activeRef.current = active;
  // the window title a full-window page asked for; a hidden app gets it back when it is shown again
  const pageTitle = useRef('');
  useEffect(() => {
    if (active && pageTitle.current && mode === 'fill' && ui.slot === 'ui.page') document.title = pageTitle.current;
  }, [active, mode, ui.slot]);
  const src = moduleUrl(m, ui.entry);
  const extraKey = JSON.stringify(extra ?? {});

  const ctx = useCallback(() => ({
    ext: 1, module: m.id, version: m.version, slot: ui.slot, me, owner, vw: window.innerWidth,
    theme: theme(wrap.current), person: m.personal?.mode ?? null,
    ...(ui.slot === 'ui.composer' ? { media: mediaSupport() } : {}), ...(JSON.parse(extraKey) as Record<string, unknown>),
  }), [m.id, m.version, m.personal?.mode, ui.slot, me, owner, extraKey]);

  // ext/1 runs over a MessageChannel handed to the page on its first hello: a page the frame navigates to later (only
  // 'self' and the Google sign-in are allowed by frame-src) gets the same window and the same 'null' origin, but not
  // the port. A second load of the frame means it navigated or reloaded itself: the channel closes, the slot goes dark
  // (an update of the module changes the key of the frame, so it mounts fresh).
  const port = useRef<MessagePort | null>(null);
  const loads = useRef(0);
  const [dead, setDead] = useState(false);
  const send = useCallback((msg: Record<string, unknown>, transfer: Transferable[] = []) => { port.current?.postMessage({ ext: 1, ...msg }, transfer); }, []);
  const ctxRef = useRef(ctx);
  ctxRef.current = ctx;
  const panelRef = useRef(panel);
  panelRef.current = panel;
  const rootRef = useRef(rootId);
  rootRef.current = rootId;
  // a viewer pane gets the messages of its chat (chat.read); so does a composer page (voice mode waits for the answer)
  const composerChat = ui.slot === 'ui.composer' && !!input?.events;
  const chatOn = (!!panel || composerChat) && m.permissions.includes('chat.read');
  const evs = panel ? panel.events : input?.events;
  const canAsk = panel ? !!panel.canAsk : !!input?.send;
  const slim = useMemo(() => (chatOn ? (evs || []).map(slimEvent) : null), [chatOn, evs]);
  const chatRef = useRef({ slim, canAsk });
  chatRef.current = { slim, canAsk };
  const sendChat = useCallback(() => {
    if (chatRef.current.slim) send({ t: 'chat', events: chatRef.current.slim, canAsk: chatRef.current.canAsk });
  }, [send]);
  useEffect(() => sendChat(), [slim, canAsk, sendChat]);
  const inputRef = useRef(input);
  inputRef.current = input;
  // microphone and speech run here on the page's behalf (ext/1 media.audio / media.speech); one of each per frame
  const audio = useRef<AudioCapture | null>(null);
  const speech = useRef<SpeechCapture | null>(null);
  const player = useRef<AudioPlayback | null>(null);
  useEffect(() => () => { audio.current?.cancel(); speech.current?.dispose(); player.current?.stop(); }, []);
  // composer.send needs the person: a click into this frame (the frame takes the focus) allows up to SEND_PER_TOUCH
  // messages within SEND_TOUCH_MS (a voice conversation), so a page cannot send on its own
  const touch = useRef({ at: 0, sent: 0 });
  useEffect(() => {
    if (ui.slot !== 'ui.composer' || !m.permissions.includes('composer.send')) return;
    const f = () => setTimeout(() => {
      if (document.activeElement === frame.current) touch.current = { at: Date.now(), sent: 0 };
    }, 0);
    window.addEventListener('blur', f);
    return () => window.removeEventListener('blur', f);
  }, [ui.slot, m.permissions]);
  // ext/1 F14b-1: an agent ran an action of this module (appEvents.ts): its pages load their data again
  useEffect(() => {
    const f = (e: Event) => {
      const d = (e as CustomEvent<{ module: string; actions: string[] }>).detail;
      if (d?.module === m.id) send({ t: 'event', type: 'refresh', data: { by: 'agent', actions: d.actions } });
    };
    window.addEventListener(APP_REFRESH, f);
    return () => window.removeEventListener(APP_REFRESH, f);
  }, [m.id, send]);
  useEffect(() => {
    if (ui.slot !== 'ui.composer') return;
    const f = () => {
      speech.current?.dispose();
      send({ t: 'event', type: 'sent', data: null });
    };
    window.addEventListener(COMPOSER_SENT, f);
    return () => window.removeEventListener(COMPOSER_SENT, f);
  }, [ui.slot, send]);

  const capability = useCallback(async (r: ExtRequest): Promise<unknown> => {
    switch (r.t) {
      case 'input': {
        const box = inputRef.current;
        if (!box) throw new Error('no_message_box');
        if (r.focus) box.focus();
        else box.set(r.text, r.interim, r.done);
        return null;
      }
      case 'audio': {
        audio.current ??= new AudioCapture((u) => send({ t: 'event', type: 'audio', data: u }));
        if (r.action === 'start') {
          await audio.current.start(r.maxSeconds, r.silenceMs);
          return null;
        }
        if (r.action === 'cancel') {
          audio.current.cancel();
          return null;
        }
        const blob = await audio.current.stop();
        return blob ? { file: blob, filename: audioFileName(blob.type), type: blob.type } : null;
      }
      case 'speech': {
        if (r.action === 'platform') {
          const p = platformDictation();
          if (!p) throw new Error('no_platform_dictation');
          inputRef.current?.focus();
          await p();
          return null;
        }
        speech.current ??= new SpeechCapture((u) => send({ t: 'event', type: 'speech', data: u }));
        if (r.action === 'start') speech.current.start(r.lang);
        else speech.current.stop();
        return null;
      }
      case 'send': {
        const box = inputRef.current;
        if (!box?.send) throw new Error('no_message_box');
        const t = touch.current;
        if (Date.now() - t.at > SEND_TOUCH_MS || t.sent >= SEND_PER_TOUCH) throw new Error('needs_user_touch');
        t.sent++;
        await box.send(r.text);
        return { ok: true };
      }
      case 'play': {
        player.current ??= new AudioPlayback();
        if (r.action === 'stop' || !r.bytes) {
          player.current.stop();
          return null;
        }
        return player.current.play(r.bytes, r.type);
      }
      case 'upload':
        return dbModuleUpload(mod.current.id, r.name, r.file, r.filename, r.fields);
      case 'storage': {
        const key = `${STORE_PREFIX}${mod.current.id}.${r.key}`;
        try {
          if (r.action === 'get') return localStorage.getItem(key);
          if (r.value === null) localStorage.removeItem(key);
          else localStorage.setItem(key, r.value);
        } catch { /* private mode: nothing kept */ }
        return null;
      }
      default:
        throw new Error('unknown_request');
    }
  }, [send]);

  const onPortMsg = useCallback((e: MessageEvent) => {
    const r = parseExtMessage(e.data);
    if (!r) return;
    if (r.t === 'size') {
      setSize(r);
      if (!r.overlay) setClosed({ w: r.w, h: r.h });
    } else if (r.t === 'badge') {
      if (mod.current.enabled) setModuleBadge(mod.current.id, r.value);
    } else if (r.t === 'call') {
      if (r.error) {
        send({ t: 'result', id: r.id, ok: false, error: r.error });
        return;
      }
      const dev = deviceModules();
      // device ops only from the module's settings page or its app, never from an always-visible slot
      const to = callRoute(mod.current, r.op, !!dev && deviceOpSlot(ui));
      if ('error' in to) {
        send({ t: 'result', id: r.id, ok: false, error: to.error });
        return;
      }
      let run: Promise<unknown>;
      if (r.op.startsWith('core.')) {
        // answered by CO Web itself; a slot that offers no such call refuses it
        const h = calls.current?.[r.op];
        run = h ? h(r.args, mod.current) : Promise.reject(new Error('core_call_unavailable'));
      } else if (to.route === 'device') {
        // a device op goes to the app this page runs in (CO Desktop / CO Resident), which checks it again
        run = dev!.moduleOp!(mod.current.id, r.op, r.args);
      } else {
        run = dbModuleCall(r.op, r.args, r.timeoutMs);
      }
      run.then(
        (data) => send({ t: 'result', id: r.id, ok: true, data }),
        (err) => send({ t: 'result', id: r.id, ok: false, error: err instanceof Error ? err.message : String(err) }),
      );
    } else if (r.t === 'req') {
      // core services of ext/1: files, the chat of a viewer pane (modules/EXT1.md)
      const why = reqRefusal(mod.current, r.name, ui.slot);
      if (why) {
        send({ t: 'result', id: r.id, ok: false, error: why });
        return;
      }
      service(r.name, r.args, rootRef.current ?? null, panelRef.current).then(
        ({ data, transfer }) => send({ t: 'result', id: r.id, ok: true, data }, transfer),
        (err) => send({ t: 'result', id: r.id, ok: false, error: err instanceof Error ? err.message : String(err) }),
      );
    } else if (r.t === 'navigate') {
      const url = navTarget(mod.current, r.url, window.location.href);
      if (!url) return;
      if (r.target === 'blank') window.open(url, '_blank', 'noopener');
      else window.location.replace(url);
    } else if (r.t === 'title') {
      if (mode === 'fill' && ui.slot === 'ui.page') {
        pageTitle.current = r.text;
        if (activeRef.current) document.title = r.text;
      }
    } else if (r.t === 'key') {
      if (activeRef.current) escCb.current?.();
    } else if (r.t === 'hint') {
      hintCb.current?.(r.hint);
    } else if (r.t === 'input') {
      // no answer: the box simply follows the page's session (a refused input is dropped)
      if (!capabilityRefusal(mod.current, ui.slot, r)) void capability(r).catch(() => undefined);
    } else if (r.t !== 'hello') {
      const why = capabilityRefusal(mod.current, ui.slot, r);
      if (why) {
        send({ t: 'result', id: r.id, ok: false, error: why });
        return;
      }
      capability(r).then(
        (data) => send({ t: 'result', id: r.id, ok: true, data }),
        (err) => send({ t: 'result', id: r.id, ok: false, error: err instanceof Error ? err.message : String(err) }),
      );
    }
  }, [send, capability, ui.slot, mode]);

  // the composer text goes only to a page of a slot whose permission the module declared
  const feedOk = !SLOT_PERMISSION[ui.slot] || m.permissions.includes(SLOT_PERMISSION[ui.slot]);
  const composerRef = useRef(composer);
  composerRef.current = composer;
  const feedRef = useRef(feedOk);
  feedRef.current = feedOk;
  useEffect(() => {
    if (feedOk && composer !== undefined) send({ t: 'composer', text: composer });
  }, [composer, feedOk, send]);

  useEffect(() => {
    const onMsg = (e: MessageEvent) => {
      // the page is sandboxed without same-origin, so its origin is 'null'; only its first hello opens the channel
      if (!frame.current || e.source !== frame.current.contentWindow || e.origin !== 'null') return;
      if (port.current || loads.current > 1 || parseExtMessage(e.data)?.t !== 'hello') return;
      const ch = new MessageChannel();
      ch.port1.onmessage = onPortMsg;
      port.current = ch.port1;
      // the frame has an opaque origin, so '*' is the only target; the port goes to that window only
      frame.current.contentWindow?.postMessage({ ext: 1, t: 'init', ctx: ctxRef.current() }, '*', [ch.port2]);
      sendChat();
      if (feedRef.current && composerRef.current !== undefined) ch.port1.postMessage({ ext: 1, t: 'composer', text: composerRef.current });
    };
    window.addEventListener('message', onMsg);
    return () => window.removeEventListener('message', onMsg);
  }, [onPortMsg, sendChat]);
  useEffect(() => () => { port.current?.close(); port.current = null; }, []);
  const onLoad = () => {
    loads.current += 1;
    if (loads.current > 1) {
      port.current?.close();
      port.current = null;
      setDead(true);
    }
  };

  // context changes (window width, theme, the person's switch) go to the page
  useEffect(() => { send({ t: 'context', ctx: ctx() }); }, [ctx, send]);
  useEffect(() => {
    const f = () => send({ t: 'context', ctx: ctx() });
    window.addEventListener('resize', f);
    const mq = matchMedia('(prefers-color-scheme: dark)');
    mq.addEventListener('change', f);
    return () => { window.removeEventListener('resize', f); mq.removeEventListener('change', f); };
  }, [ctx, send]);

  // an open popover of the page closes on a click anywhere else in CO Web
  // an open popover follows its slot when the tray scrolls sideways, the page scrolls or the window is resized
  const [, setPlace] = useState(0);
  useEffect(() => {
    if (!size.overlay) return;
    const f = () => setPlace((n) => n + 1);
    window.addEventListener('scroll', f, true);
    window.addEventListener('resize', f);
    const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(f) : null;
    if (wrap.current) ro?.observe(wrap.current);
    return () => { window.removeEventListener('scroll', f, true); window.removeEventListener('resize', f); ro?.disconnect(); };
  }, [size.overlay]);
  useEffect(() => {
    if (!size.overlay) return;
    const f = (e: PointerEvent) => { if (e.target !== frame.current) send({ t: 'blur' }); };
    document.addEventListener('pointerdown', f, true);
    return () => document.removeEventListener('pointerdown', f, true);
  }, [size.overlay, send]);

  if (!src || dead) {
    if (mode !== 'fill' || !dead) return null;
    return (
      <div className="muted pad">
        This page of {m.title} reloaded itself and was closed.{' '}
        {onReopen ? <button type="button" className="btn small" onClick={onReopen}>Open it again</button> : 'Open it again.'}
      </div>
    );
  }
  if (mode !== 'inline') {
    return (
      <span ref={wrap} className={mode === 'fill' ? 'module-page' : 'module-hidden'} data-module={m.id} aria-hidden={mode === 'hidden' || undefined}>
        <iframe
          ref={frame}
          src={src}
          title={ui.title || m.title}
          sandbox="allow-scripts allow-forms"
          referrerPolicy="no-referrer"
          onLoad={onLoad}
          tabIndex={mode === 'hidden' ? -1 : undefined}
          className="module-frame"
        />
      </span>
    );
  }
  return (
    <span ref={wrap} className="module-slot" style={{ width: closed.w, height: closed.h || 28 }} data-module={m.id}>
      <iframe
        ref={frame}
        src={src}
        title={ui.title || m.title}
        sandbox="allow-scripts allow-forms"
        referrerPolicy="no-referrer"
        onLoad={onLoad}
        className={size.overlay ? 'module-frame overlay' : 'module-frame'}
        style={size.overlay ? { width: size.w, height: size.h || 28, ...overlayPos(wrap.current, size.w) } : { width: size.w, height: size.h || 28 }}
      />
    </span>
  );
}

/**
 * An open popover of a tray page is drawn fixed over the page at the place of its slot, kept inside the window: the tray
 * may scroll or clip sideways on a narrow screen, the popover must not be cut by it.
 */
function overlayPos(wrap: HTMLElement | null, w: number): { left: number; top: number } {
  const r = wrap?.getBoundingClientRect();
  if (!r) return { left: 4, top: 4 };
  const vw = window.innerWidth;
  return { left: Math.max(4, Math.min(r.right - w, vw - w - 4)), top: Math.max(0, r.top) };
}

/** The pages one module declared for `slot` (Host → Modules shows ui.settings this way). */
export function ModulePages({ slot, m, me, owner }: { slot: string; m: ModuleInfo; me?: string; owner: boolean }) {
  if (!m.enabled) return null;
  return <>{m.ui.filter((u) => u.slot === slot).map((u) => <ModuleFrame key={`${m.id}/${u.id}/${m.version}/${u.entry}`} m={m} ui={u} me={me} owner={owner} />)}</>;
}

/** "Show me in <module>?" for a person whose own switch of a personal module is still "ask". */
function PersonAsk({ m, onDone }: { m: ModuleInfo; onDone: () => void }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const set = async (mode: 'auto' | 'off') => {
    setBusy(true);
    try {
      await dbModulePerson(m.id, mode);
      onDone();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <span className="module-ask-wrap">
      <button type="button" className="module-ask" title={`${m.title}: your choice`} onClick={() => setOpen((o) => !o)}>{m.title} ?</button>
      {open && (
        <>
          <div className="app-menu-scrim" onClick={() => setOpen(false)} />
          <div className="module-ask-pop" role="dialog" aria-label={m.title}>
            <div><b>{m.title}</b></div>
            <div className="small">{m.personal?.question || m.description}</div>
            <div className="small muted">Only you decide this; change it any time in Host → Modules.</div>
            <div className="module-ask-btns">
              <button className="btn small primary" type="button" disabled={busy} onClick={() => void set('auto')}>Yes, show me</button>
              <button className="btn small ghost" type="button" disabled={busy} onClick={() => void set('off')}>No</button>
            </div>
            {err && <div className="small" style={{ color: 'var(--bad)' }}>{err}</div>}
          </div>
        </>
      )}
    </span>
  );
}

/** Every module page declared for `slot`, in the order of the module list. */
export function ModuleSlots({ slot, modules, me, owner, input }: { slot: string; modules: ModuleInfo[]; me?: string; owner: boolean; input?: ComposerInput }) {
  const changed = () => window.dispatchEvent(new Event(MODULES_CHANGED));
  return (
    <>
      {modules.filter((m) => m.enabled).map((m) => (
        <span key={m.id} className="module-group">
          {m.personal?.mode === 'ask' && slot === 'ui.header' && <PersonAsk m={m} onDone={changed} />}
          {m.ui.filter((u) => u.slot === slot).map((u) => <ModuleFrame key={`${m.id}/${u.id}/${m.version}/${u.entry}`} m={m} ui={u} me={me} owner={owner} input={slot === 'ui.composer' ? input : undefined} />)}
        </span>
      ))}
    </>
  );
}

/** Pages (ui.page) of the enabled modules, for links in the sidebar: [module, page]. */
export function modulePages(modules: ModuleInfo[]): { m: ModuleInfo; ui: ModuleUi }[] {
  return modules.filter((m) => m.enabled).flatMap((m) => m.ui.filter((u) => u.slot === 'ui.page').map((ui) => ({ m, ui })));
}

/** One ui.page of a module, filling the centre of CO Web (route /e/:env/m/:module/:page). */
export function ModulePage({ modules, moduleId, pageId, me, owner, coreCalls, extra }: {
  modules: ModuleInfo[]; moduleId: string; pageId: string; me?: string; owner: boolean; coreCalls?: CoreCalls; extra?: Record<string, unknown>;
}) {
  const m = modules.find((x) => x.id === moduleId && x.enabled);
  const ui = m?.ui.find((u) => u.slot === 'ui.page' && u.id === pageId);
  if (!m || !ui) return <div className="empty">{modules.length ? 'This module page is not available (the module is off or not installed).' : 'Loading…'}</div>;
  return <ModuleFrame key={`${m.id}/${ui.id}/${m.version}/${ui.entry}`} m={m} ui={ui} me={me} owner={owner} mode="fill" coreCalls={coreCalls} extra={extra} />;
}

const HINT_DEBOUNCE_MS = 400;

/**
 * Hidden ui.hint pages of the enabled modules that declare composer.read: they get the composer text (debounced) and
 * answer with a hint; onHint gets the first hint of the list (null = none). CO Web shows it and never applies it.
 */
export function ModuleHints({ modules, text, me, owner, onHint }: {
  modules: ModuleInfo[]; text: string; me?: string; owner: boolean; onHint: (h: ModuleHint | null) => void;
}) {
  const [fed, setFed] = useState(text);
  useEffect(() => {
    const t = setTimeout(() => setFed(text), HINT_DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [text]);
  const pages = modules.filter((m) => m.enabled && m.permissions.includes(SLOT_PERMISSION['ui.hint']))
    .flatMap((m) => m.ui.filter((u) => u.slot === 'ui.hint').map((ui) => ({ m, ui })));
  const hints = useRef<Record<string, ModuleHint | null>>({});
  const order = pages.map((p) => `${p.m.id}/${p.ui.id}`).join(',');
  const cb = useRef(onHint);
  cb.current = onHint;
  const publish = useCallback(() => {
    const first = order.split(',').map((k) => hints.current[k]).find((h) => !!h) ?? null;
    cb.current(first);
  }, [order]);
  useEffect(() => publish(), [publish]);
  if (!pages.length) return null;
  return (
    <>
      {pages.map(({ m, ui }) => {
        const k = `${m.id}/${ui.id}`;
        return (
          <ModuleFrame key={`${k}/${m.version}/${ui.entry}`} m={m} ui={ui} me={me} owner={owner} mode="hidden" composer={fed}
            onHint={(h) => { hints.current[k] = h; publish(); }} />
        );
      })}
    </>
  );
}
