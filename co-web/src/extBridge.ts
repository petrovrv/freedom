// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6): the CO Web side of ext/1. A module page runs in a sandboxed iframe (agent/module.php, no
// same-origin) and talks to CO Web only with postMessage (public/ext/ext1.js). Here every message is checked: it must
// come from the frame of that module, a call must name an op the module declared (manifest server.ops.user, listed by
// db.php modules_list), arguments are a plain object of limited size. Pure functions; used by ModuleSlot.tsx and tested
// in extBridge.selftest.ts.
// Besides its own ops a page may call a few generic calls of the core (CORE_CALLS, names "core.<point>.<call>"), each
// only when the manifest declares the permission of that extension point; CO Web itself answers them (ModuleSlot.tsx
// coreCalls), never db.php directly. A page in the ui.hint slot gets the composer text and answers with a hint.

import type { ModeDecl } from './moduleModes';

export interface ModuleUi {
  slot: string; id: string; entry: string; title: string; /** short text of the sidebar button of a ui.page */ label?: string; path?: string;
  /** ext/1 shell: a ui.page that is an app of the launcher, with its icon (a short emoji or text) */
  app?: boolean; icon?: string;
  /** ext/1 ui.window: a desktop app opens the page in its own window */
  window?: boolean; windowKey?: string;
}
export interface FileAction {
  id: string; label: string; title: string; exts: string[]; exclude: string;
  open: { page: string; param: string; target: 'self' | 'blank' } | { mode: string };
}
export interface MessageView { match: string; label: string; strip: string[]; replace: [string, string][] }
export interface SideJob { key: string; label: string; list: string; item: string; count: string; countNoun: string }
/** ext/1 device.tools: what a device (CO Desktop, CO Resident) loads of an enabled module; null when it is off. */
export interface ModuleDevice {
  tools: { name: string; class: string; description: string }[]; ops: string[]; default: 'on' | 'off'; warning: string;
}
export interface ModulePersonal { about: string; mode: 'auto' | 'ask' | 'off'; question: string }
export interface ModuleInfo {
  id: string; version: string; title: string; description: string; enabled: boolean; standard: boolean;
  permissions: string[]; ui: ModuleUi[]; uiOps: string[]; personal: ModulePersonal | null; uploads?: string[];
  /** outside services the module imports from (manifest provides.import), for labels of imported chats */
  imports?: { source: string; label?: string; speaker?: string }[];
  device?: ModuleDevice | null;
  people?: { env: string; mode: 'auto' | 'ask' | 'off'; own: boolean }[];
  installedUtc?: string | null; updatedUtc?: string | null; publisher?: string;
  // ext/1 declarations CO Web weaves into its own screens (empty while the module is off; older servers send none)
  modes?: ModeDecl[]; context?: string[]; fileActions?: FileAction[]; messageViews?: MessageView[]; sideJobs?: SideJob[];
  navigate?: string[];
  /** F14b-2: ops of the module agents may call (manifest provides.actions); its tray chip listens for their refresh */
  actions?: string[];
  /** F14b-3: the module may notify (permission notify.push); on = the person has not switched its notifications off */
  notify?: { on: boolean };
}

export const EXT_VERSION = 1;
const MAX_ARGS = 16 * 1024;
const MAX_REQ = 40 * 1024 * 1024; // a core service request may carry files (ArrayBuffer): a PNG of a page area, a PDF
// a core call may carry a whole imported conversation
const MAX_CORE_ARGS = 32 * 1024 * 1024;
const CORE_OP = /^core\.[a-z]+\.[a-z][a-zA-Z]{1,30}$/;

/** Slots of ext/1 a module page can be put in (SPEC §3.6). */
export const SLOTS = ['ui.header', 'ui.page', 'ui.panel', 'ui.settings', 'ui.composer', 'ui.hint'] as const;

/**
 * Generic calls of the core a module page may make, with the permission its manifest must declare:
 *   import.write - the `import` point: read the import index and projects of the environment, write one imported
 *                  conversation (chat + messages + attachment texts) into a new or existing project (ModuleSlot.tsx)
 */
export const CORE_CALLS: Record<string, string> = {
  'core.import.context': 'import.write',
  'core.import.write': 'import.write',
  'core.import.finish': 'import.write',
};

/** Permission a slot needs before CO Web feeds it anything ('' = none). */
export const SLOT_PERMISSION: Record<string, string> = { 'ui.hint': 'composer.read' };

/**
 * A hint of a ui.hint page: a short line next to the model picker and the picks it suggests, best first. CO Web shows
 * the first pick this host offers and never applies it by itself.
 */
export interface HintPick { agent: string; model: string | null; label: string }
export interface ModuleHint { text: string; title: string; picks: HintPick[] }

const MODULE_ID = /^[a-z0-9][a-z0-9-]{0,62}$/;
const ENTRY = /^ui\/[A-Za-z0-9._/-]+\.html$/;

/** URL of a module page; null for anything that is not a plain ui/ path of a valid module. */
export function moduleUrl(m: Pick<ModuleInfo, 'id' | 'version'>, entry: string): string | null {
  if (!MODULE_ID.test(m.id) || !ENTRY.test(entry) || entry.includes('..')) return null;
  return `/agent/module.php?m=${encodeURIComponent(m.id)}&f=${encodeURIComponent(entry)}&v=${encodeURIComponent(m.version)}`;
}

// Capabilities of module pages beyond calls (ext/1, Freedom F10). Each needs a permission in the manifest; the
// composer ones only work in the ui.composer slot (a page in the action row of the message box):
//   input   - composer.input: put text into the message box. A session starts with the first input: the box text of that
//             moment stays, the session's text (and a grey interim tail) follows it; done ends the session. focus puts
//             the caret at the end of the box (platform voice typing then types there itself).
//   audio   - media.audio: CO Web records the microphone (an opaque-origin frame may not); stop returns the recording.
//   speech  - media.speech: the browser's own speech recognition (or the platform's voice typing into the box) runs in CO
//             Web; results come back as 'speech' events.
//   upload  - a POST of a file to one of the module's declared uploads (manifest server.uploads, agent/module.php&u=)
//   storage - a few small values of the module kept in this browser (per module, per key)
export const EXT_PERMISSIONS = {
  input: 'composer.input', audio: 'media.audio', speech: 'media.speech',
  // voice mode (module voice, SPEC §3.8): send a text as the chat's next message; play an audio answer
  send: 'composer.send', play: 'media.play',
} as const;
const MAX_PLAY = 10 * 1024 * 1024;
const MAX_INPUT = 20000;
const MAX_UPLOAD = 25 * 1024 * 1024;
const MAX_STORE = 4096;
const NAME = /^[a-z][a-z0-9_-]{0,31}$/;

export type ExtRequest =
  | { t: 'hello' }
  // error: refused while parsing (too_large), answered at once instead of leaving the page to its timeout
  | { t: 'call'; id: string; op: string; args: Record<string, unknown>; error?: string; timeoutMs?: number }
  | { t: 'req'; id: string; name: string; args: Record<string, unknown> }
  | { t: 'navigate'; url: string; target: 'self' | 'blank' }
  | { t: 'title'; text: string }
  // a key the page did not use itself (only Esc): the shell may leave the app
  | { t: 'key'; key: 'Escape' }
  | { t: 'size'; w: number; h: number; overlay: boolean }
  | { t: 'badge'; value: string }
  | { t: 'input'; text: string; interim: string; done: boolean; focus: boolean }
  | { t: 'audio'; id: string; action: 'start' | 'stop' | 'cancel'; maxSeconds: number; silenceMs: number }
  | { t: 'send'; id: string; text: string }
  | { t: 'play'; id: string; action: 'play' | 'stop'; bytes: ArrayBuffer | null; type: string }
  | { t: 'speech'; id: string; action: 'start' | 'stop' | 'platform'; lang: string }
  | { t: 'upload'; id: string; name: string; file: Blob; filename: string; fields: Record<string, string> }
  | { t: 'storage'; id: string; action: 'get' | 'set'; key: string; value: string | null }
  | { t: 'hint'; hint: ModuleHint | null };

const reqId = (v: unknown): v is string => typeof v === 'string' && /^[A-Za-z0-9_-]{1,40}$/.test(v);

const str = (v: unknown, max: number): string | null => (typeof v === 'string' && v.length <= max && !/[\u0000-\u001f]/.test(v) ? v : null);

/** A hint as a page sent it; null = no hint (also for anything malformed). */
export function parseHint(v: unknown): ModuleHint | null {
  if (!v || typeof v !== 'object') return null;
  const h = v as Record<string, unknown>;
  const text = str(h.text, 120), title = str(h.title ?? '', 400);
  if (!text || title === null || !Array.isArray(h.picks) || !h.picks.length || h.picks.length > 5) return null;
  const picks: HintPick[] = [];
  for (const x of h.picks) {
    const p = (x && typeof x === 'object' ? x : {}) as Record<string, unknown>;
    const agent = str(p.agent, 40), label = str(p.label, 120);
    const model = p.model == null ? null : str(p.model, 80);
    if (!agent || !/^[a-z0-9_-]+$/.test(agent) || !label || (p.model != null && model === null)) return null;
    picks.push({ agent, model, label });
  }
  return { text, title, picks };
}

/** Rough size of a request's arguments; binary parts count with their byte length. */
function argsSize(v: unknown, depth = 0): number {
  if (depth > 6) return Infinity;
  if (v instanceof ArrayBuffer) return v.byteLength;
  if (ArrayBuffer.isView(v)) return v.byteLength;
  if (typeof v === 'string') return v.length;
  if (Array.isArray(v)) return v.reduce((n: number, x) => n + argsSize(x, depth + 1), 2);
  if (v && typeof v === 'object') return Object.entries(v).reduce((n, [k, x]) => n + k.length + argsSize(x, depth + 1), 2);
  return 8;
}

/** Parses a message from a module frame; null = ignore it. */
export function parseExtMessage(data: unknown): ExtRequest | null {
  if (!data || typeof data !== 'object') return null;
  const m = data as Record<string, unknown>;
  if (m.ext !== EXT_VERSION || typeof m.t !== 'string') return null;
  if (m.t === 'hello') return { t: 'hello' };
  if (m.t === 'call') {
    if (typeof m.id !== 'string' || !/^[A-Za-z0-9_-]{1,40}$/.test(m.id) || typeof m.op !== 'string') return null;
    const core = m.op.startsWith('core.');
    const args = m.args ?? {};
    if (typeof args !== 'object' || args === null || Array.isArray(args)) return null;
    // the op travels in the request body next to the arguments: "op" or "gz" among them could replace it
    if (Object.prototype.hasOwnProperty.call(args, 'op') || Object.prototype.hasOwnProperty.call(args, 'gz')) return null;
    let size = 0;
    try { size = JSON.stringify(args).length; } catch { return null; }
    if (size > (core ? MAX_CORE_ARGS : MAX_ARGS)) return { t: 'call', id: m.id, op: m.op, args: {}, error: 'too_large' };
    const timeoutMs = Math.min(600000, Math.max(1000, Number(m.timeoutMs) || 0)) || undefined;
    return { t: 'call', id: m.id, op: m.op, args: args as Record<string, unknown>, ...(m.timeoutMs ? { timeoutMs } : {}) };
  }
  if (m.t === 'req') {
    if (!reqId(m.id) || typeof m.name !== 'string' || !/^[a-z]+\.[A-Za-z]+$/.test(m.name)) return null;
    const args = m.args ?? {};
    if (typeof args !== 'object' || args === null || Array.isArray(args) || argsSize(args) > MAX_REQ) return null;
    return { t: 'req', id: m.id, name: m.name, args: args as Record<string, unknown> };
  }
  if (m.t === 'navigate') {
    if (typeof m.url !== 'string' || m.url.length > 4000) return null;
    return { t: 'navigate', url: m.url, target: m.target === 'blank' ? 'blank' : 'self' };
  }
  if (m.t === 'title') return typeof m.text === 'string' ? { t: 'title', text: m.text.slice(0, 200) } : null;
  if (m.t === 'key') return m.key === 'Escape' ? { t: 'key', key: 'Escape' } : null;
  if (m.t === 'badge') {
    // a count or a short mark; "0" and anything blank clear it
    const v = typeof m.value === 'string' ? m.value.trim().slice(0, 3) : '';
    return { t: 'badge', value: v === '0' || /[\u0000-\u001f<>]/.test(v) ? '' : v };
  }
  if (m.t === 'size') {
    const w = Number(m.w), h = Number(m.h);
    if (!Number.isFinite(w) || !Number.isFinite(h)) return null;
    return { t: 'size', w: Math.max(0, Math.min(800, w)), h: Math.max(0, Math.min(900, h)), overlay: m.overlay === true };
  }
  if (m.t === 'input') {
    const text = typeof m.text === 'string' ? m.text : '';
    const interim = typeof m.interim === 'string' ? m.interim : '';
    if (text.length + interim.length > MAX_INPUT) return null;
    return { t: 'input', text, interim, done: m.done === true, focus: m.focus === true };
  }
  if (m.t === 'audio') {
    if (!reqId(m.id) || (m.action !== 'start' && m.action !== 'stop' && m.action !== 'cancel')) return null;
    const max = Number(m.maxSeconds);
    // silenceMs > 0: the recording stops by itself that long after the speaker went quiet (hands-free voice mode)
    const quiet = Number(m.silenceMs);
    return { t: 'audio', id: m.id, action: m.action, maxSeconds: Number.isFinite(max) ? Math.max(1, Math.min(600, max)) : 300,
      silenceMs: Number.isFinite(quiet) && quiet > 0 ? Math.max(300, Math.min(5000, quiet)) : 0 };
  }
  if (m.t === 'send') {
    if (!reqId(m.id) || typeof m.text !== 'string' || !m.text.trim() || m.text.length > MAX_INPUT) return null;
    return { t: 'send', id: m.id, text: m.text };
  }
  if (m.t === 'play') {
    if (!reqId(m.id) || (m.action !== 'play' && m.action !== 'stop')) return null;
    if (m.action === 'stop') return { t: 'play', id: m.id, action: 'stop', bytes: null, type: '' };
    const type = typeof m.type === 'string' && /^audio\/[a-z0-9.+-]{1,30}$/.test(m.type) ? m.type : '';
    if (!(m.bytes instanceof ArrayBuffer) || !m.bytes.byteLength || m.bytes.byteLength > MAX_PLAY || !type) return null;
    return { t: 'play', id: m.id, action: 'play', bytes: m.bytes, type };
  }
  if (m.t === 'speech') {
    if (!reqId(m.id) || (m.action !== 'start' && m.action !== 'stop' && m.action !== 'platform')) return null;
    const lang = typeof m.lang === 'string' && /^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$/.test(m.lang) ? m.lang : '';
    return { t: 'speech', id: m.id, action: m.action, lang };
  }
  if (m.t === 'upload') {
    if (!reqId(m.id) || typeof m.name !== 'string' || !NAME.test(m.name) || !(m.file instanceof Blob) || m.file.size > MAX_UPLOAD) return null;
    const filename = typeof m.filename === 'string' && /^[A-Za-z0-9._-]{1,80}$/.test(m.filename) ? m.filename : 'file';
    const f = m.fields ?? {};
    if (typeof f !== 'object' || f === null || Array.isArray(f)) return null;
    const fields: Record<string, string> = {};
    for (const [k, v] of Object.entries(f as Record<string, unknown>)) {
      if (!NAME.test(k) || k === 'file' || typeof v !== 'string' || v.length > 2000) return null;
      fields[k] = v;
    }
    if (Object.keys(fields).length > 16) return null;
    return { t: 'upload', id: m.id, name: m.name, file: m.file, filename, fields };
  }
  if (m.t === 'storage') {
    if (!reqId(m.id) || (m.action !== 'get' && m.action !== 'set') || typeof m.key !== 'string' || !NAME.test(m.key)) return null;
    const value = typeof m.value === 'string' ? m.value : null;
    if (value !== null && value.length > MAX_STORE) return null;
    return { t: 'storage', id: m.id, action: m.action, key: m.key, value };
  }
  if (m.t === 'hint') return { t: 'hint', hint: parseHint(m.hint) };
  return null;
}

/**
 * Why a capability request of a module page is refused ('' = allowed): the module must be on and hold the permission;
 * the composer capabilities work only from the ui.composer slot; an upload must be one the module declared.
 */
export function capabilityRefusal(m: Pick<ModuleInfo, 'enabled' | 'permissions' | 'uploads'>, slot: string, r: ExtRequest): string {
  if (!m.enabled) return 'module_disabled';
  const need = r.t === 'input' ? EXT_PERMISSIONS.input : r.t === 'audio' ? EXT_PERMISSIONS.audio : r.t === 'speech' ? EXT_PERMISSIONS.speech
    : r.t === 'send' ? EXT_PERMISSIONS.send : r.t === 'play' ? EXT_PERMISSIONS.play : '';
  if (need && !m.permissions.includes(need)) return 'permission_not_declared';
  if (need && slot !== 'ui.composer') return 'composer_only';
  if (r.t === 'upload' && !(m.uploads || []).includes(r.name)) return 'upload_not_declared';
  return '';
}

/**
 * Why a call of a module is refused ('' = allowed). The module may call its own declared user ops and the core calls
 * whose permission its manifest declares.
 */
export function callRefusal(m: Pick<ModuleInfo, 'enabled' | 'uiOps' | 'personal'> & { permissions?: string[] }, op: string): string {
  if (!m.enabled) return 'module_disabled';
  if (op.startsWith('core.')) {
    const perm = CORE_OP.test(op) && Object.prototype.hasOwnProperty.call(CORE_CALLS, op) ? CORE_CALLS[op] : '';
    if (!perm) return 'core_call_unknown';
    return (m.permissions || []).includes(perm) ? '' : 'permission_not_declared';
  }
  if (!/^[a-z][a-z0-9_]{2,40}$/.test(op) || !m.uiOps.includes(op)) return 'op_not_declared';
  return '';
}

/**
 * Where a call of a module page goes: 'server' for its own user ops (db.php), 'device' for its declared device ops
 * (provides.deviceOps), answered by the app this page runs in (window.coDesktop.moduleOp of CO Desktop / CO Resident).
 * A device op outside such an app is refused with device_op_unavailable.
 */
export function callRoute(m: Pick<ModuleInfo, 'enabled' | 'uiOps' | 'personal' | 'device'> & { permissions?: string[] }, op: string, onDevice: boolean):
  { route: 'server' | 'device' } | { error: string } {
  const why = callRefusal(m, op);
  if (why !== 'op_not_declared') return why ? { error: why } : { route: 'server' };
  if (!/^[a-z][a-z0-9_]{2,40}$/.test(op) || !(m.device?.ops ?? []).includes(op)) return { error: why };
  return onDevice ? { route: 'device' } : { error: 'device_op_unavailable' };
}

/**
 * Slots whose page may call the module's device ops: its settings page and its app (a ui.page with app: true, e.g. an
 * app that starts a program of the device); never an always-visible slot (tray, composer, hint, panel).
 */
export function deviceOpSlot(ui: Pick<ModuleUi, 'slot' | 'app'>): boolean {
  return ui.slot === 'ui.settings' || (ui.slot === 'ui.page' && ui.app === true);
}

/**
 * The URL a page may send the window to, or null. Allowed: a target of the module's manifest navigate (an https origin
 * - any path under it; a path of this site - that path with any query). Everything else (javascript:, data:, other
 * sites, other paths of CO) is refused.
 */
export function navTarget(m: Pick<ModuleInfo, 'enabled' | 'permissions' | 'navigate'>, url: string, here: string): string | null {
  if (!m.enabled || !m.permissions.includes('ui.navigate')) return null;
  let u: URL;
  try { u = new URL(url, here); } catch { return null; }
  const own = new URL(here).origin;
  for (const n of m.navigate || []) {
    if (n.startsWith('/')) {
      if (u.origin === own && u.pathname === n) return u.href;
    } else if (u.protocol === 'https:' && u.origin === n.toLowerCase() && u.origin !== own) {
      return u.href;
    }
  }
  return null;
}

/** Permission a core service of ext/1 needs ('' = none). */
export const REQ_PERMISSION: Record<string, string> = {
  'file.get': 'files.read', 'file.pdf': 'files.read', 'file.sibling': 'files.read', 'file.putSibling': 'files.write',
  'chat.ask': 'chat.post', 'chat.reveal': 'chat.read', 'panel.close': '',
  // F14b-3: a push of the module to its own person (any slot; db.php app_notify checks again and applies the limits)
  'notify.push': 'notify.push',
};
const PANEL_ONLY = new Set(['chat.ask', 'chat.reveal', 'panel.close']);

/** Why a core service request is refused ('' = allowed). */
export function reqRefusal(m: Pick<ModuleInfo, 'enabled' | 'permissions'>, name: string, slot: string): string {
  if (!m.enabled) return 'module_disabled';
  if (!Object.prototype.hasOwnProperty.call(REQ_PERMISSION, name)) return 'unknown_request';
  const need = REQ_PERMISSION[name];
  if (need && !m.permissions.includes(need)) return 'permission_not_declared';
  if (PANEL_ONLY.has(name) && slot !== 'ui.panel') return 'not_a_panel';
  return '';
}

/** A path relative to AI-Workbench a page may name: inside projects/, no empty or dot parts. */
export function filePathOk(p: unknown): p is string {
  if (typeof p !== 'string' || p.length > 600 || !p.startsWith('projects/') || /[\x00-\x1f\\]/.test(p)) return false;
  return p.split('/').every((x) => x !== '' && x !== '.' && x !== '..');
}

/** A sibling name a page may read or write next to `path`: it starts with the name of `path` (e.g. "a.pptx.x.pdf"). */
export function siblingOk(path: string, name: unknown): name is string {
  const base = path.split('/').pop() || '';
  return typeof name === 'string' && name.length <= 255 && name.length > base.length && name.startsWith(base) && !/[/\\\x00-\x1f]/.test(name);
}

// ---------- message views and side jobs ----------

function re(src: string): RegExp | null {
  try { return new RegExp(src); } catch { return null; }
}

/** How a user message a module page wrote is shown: its label and the text without the technical parts (first match). */
export function viewMessage(views: MessageView[], text: string | undefined): { label: string; text: string } | null {
  const t = text || '';
  for (const v of views) {
    const r = re(v.match);
    const m = r ? r.exec(t) : null;
    if (!r || !m) continue;
    const label = v.label.replace(/\$(\d)/g, (_, d: string) => (m[Number(d)] || '').trim());
    let body = t.replace(r, '');
    for (const s of v.strip || []) { const x = re(s); if (x) body = body.replace(x, ''); }
    for (const [a, b] of v.replace || []) { const x = re(a); if (x) body = body.replace(x, () => b); }
    return { label, text: body.trim() };
  }
  return null;
}

/** The side job an event belongs to (its key holds {path, file}), or null. */
export function sideJobOf(jobs: SideJob[], ev: object): { job: SideJob; path: string; file: string } | null {
  const r = ev as Record<string, unknown>;
  for (const j of jobs) {
    const v = r[j.key] as { path?: unknown; file?: unknown } | undefined;
    if (v && typeof v === 'object') return { job: j, path: String(v.path ?? ''), file: String(v.file ?? '') };
  }
  return null;
}

/** Items of a side job's answer: JSON (the last fenced block, or the whole text) with an array `list`; else the text. */
export function sideReplies(job: SideJob, text: string): string[] {
  if (!job.list) return text.trim() ? [text.trim()] : [];
  const blocks = [...text.matchAll(/```(?:json)?\s*(\{[\s\S]*?\})\s*```/g)];
  try {
    const data = JSON.parse(blocks.length ? blocks[blocks.length - 1][1] : text) as Record<string, unknown>;
    const list = Array.isArray(data[job.list]) ? (data[job.list] as Record<string, unknown>[]) : [];
    return list.map((x) => {
      const n = job.count && Array.isArray(x?.[job.count]) ? (x[job.count] as unknown[]).length : 0;
      const noun = job.countNoun || 'item';
      return `${String((job.item ? x?.[job.item] : '') ?? '')}${n ? ` (${n} ${noun}${n > 1 ? 's' : ''})` : ''}`;
    });
  } catch {
    return text.trim() ? [text.trim()] : [];
  }
}

/** File menu items modules offer for a file name. */
export function fileActionsFor(modules: ModuleInfo[], name: string): { m: ModuleInfo; a: FileAction }[] {
  const e = /\.([^./]+)$/.exec(name.toLowerCase())?.[1] || '';
  const out: { m: ModuleInfo; a: FileAction }[] = [];
  for (const m of modules) {
    if (!m.enabled) continue;
    for (const a of m.fileActions || []) {
      if (!a.exts.includes(e)) continue;
      const x = a.exclude ? re(a.exclude) : null;
      if (x && x.test(name)) continue;
      out.push({ m, a });
    }
  }
  return out;
}

/** CSS variables of CO Web handed to module pages so they look like the rest of the window. */
export const THEME_VARS = ['--bg', '--panel', '--panel-2', '--fg', '--muted', '--line', '--accent', '--accent-fg', '--ok', '--warn', '--bad', '--env', '--user', '--code'];

/** ext/1 shell: one app of the launcher (a ui.page with app: true of an enabled module). */
export interface ShellApp { m: ModuleInfo; ui: ModuleUi; icon: string; label: string }

/** Apps of the launcher, in the order of the module list; the icon falls back to the first letter of the name. */
export function shellApps(modules: ModuleInfo[]): ShellApp[] {
  return modules.filter((m) => m.enabled).flatMap((m) => m.ui.filter((u) => u.slot === 'ui.page' && u.app === true).map((ui) => {
    const label = ui.label || ui.title || m.title || m.id;
    return { m, ui, label, icon: ui.icon || Array.from(label)[0]?.toUpperCase() || '▫' };
  }));
}

// badges of modules (coExt.badge): set by any loaded page of the module (usually its tray page), shown by the launcher
// on the module's app tiles and as a dot on ▦; they live in this window only
const badges = new Map<string, string>();
const BADGE_EVENT = 'co-ext-badge';

export function setModuleBadge(moduleId: string, value: string): void {
  if ((badges.get(moduleId) || '') === value) return;
  if (value) badges.set(moduleId, value); else badges.delete(moduleId);
  window.dispatchEvent(new Event(BADGE_EVENT));
}

export function moduleBadges(): ReadonlyMap<string, string> { return badges; }

/** Drops the badges of modules that are no longer enabled (called with every new module list). */
export function pruneModuleBadges(enabled: ReadonlySet<string>): void {
  let gone = false;
  for (const id of [...badges.keys()]) if (!enabled.has(id)) { badges.delete(id); gone = true; }
  if (gone) window.dispatchEvent(new Event(BADGE_EVENT));
}

/** Subscribes to badge changes; returns the unsubscribe function. */
export function onModuleBadges(fn: () => void): () => void {
  window.addEventListener(BADGE_EVENT, fn);
  return () => window.removeEventListener(BADGE_EVENT, fn);
}

/** A module with own settings (ui.settings pages or a person's switch): one tile in the Settings part of the launcher. */
export type ShellSetting = { m: ModuleInfo; label: string; icon: string };

/** Settings tiles of enabled modules (also one whose only setting is the switch of its notifications); the icon is the one of the module's app, else of any other page of it (a tray
 * indicator, a settings page), else the first letter. */
export function shellSettings(modules: ModuleInfo[]): ShellSetting[] {
  return modules.filter((m) => m.enabled && (m.personal || m.notify || m.ui.some((u) => u.slot === 'ui.settings'))).map((m) => {
    const label = m.title || m.id;
    const app = m.ui.find((u) => u.slot === 'ui.page' && u.app === true && u.icon) || m.ui.find((u) => u.icon);
    return { m, label, icon: app?.icon || Array.from(label)[0]?.toUpperCase() || '▫' };
  });
}

/**
 * How the launcher opens an app: a desktop app (CO Desktop, CO Resident) opens a ui.window page in its own window
 * (blank → the module address); everywhere else the app fills the centre of CO Web like any ui.page.
 */
export function shellOpen(app: Pick<ShellApp, 'm' | 'ui'>, env: string, desktop: boolean): { href: string; target: 'self' | 'blank' } {
  // ?window=1: the bare page in its own window, not the shell (appStack shellRoute)
  if (desktop && app.ui.window) return { href: `${app.ui.path || `/x/${app.m.id}/${app.ui.id}`}?window=1`, target: 'blank' };
  return { href: `/e/${env}/m/${app.m.id}/${app.ui.id}`, target: 'self' };
}
