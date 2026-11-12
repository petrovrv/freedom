// chamberlain.mjs - tab chamberlain of the CO browser (0.13.0; settings per chat 0.15.0).
// One view of every tab of the profile's Edge: who holds it (a live agent job, a finished job, CO Desktop, nobody =
// the user), its site, the user's lock (a tab pinned in Edge) and when it was last used. Before opening a tab an
// agent reuses one: its own or a free tab of the same site (also the user's), then a blank tab. A pinned tab is
// never closed and never sent to another site; every other tab may be reused or closed (owner's rule, 07.11).
// Sources: CDP Target.getTargets (raw socket, no attach to pages), ba-tabs.json (browser-agent daemons),
// co-tabs.json (CO Desktop service tabs, never touched) and the "CO agent browser tabs" extension (pinned state,
// lastAccessed, discarded) whose service worker is evaluated over CDP.

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const TABS_EXT_DIR = 'tabs';              // <profile>\co-agent-look\tabs
const EXT_FILES = ['manifest.json', 'co-tabs-sw.js'];
const SW_FILE = '/co-tabs-sw.js';

function readJson(f) {
  try { return JSON.parse(readFileSync(f, 'utf8').replace(/^﻿/, '')); } catch { return null; }
}

// Writes the tabs extension into <profile>\co-agent-look\tabs (missing or outdated); returns its folder or null.
export function ensureTabsExtension(profileDir) {
  const dir = join(profileDir, 'co-agent-look', TABS_EXT_DIR);
  try {
    mkdirSync(dir, { recursive: true });
    for (const f of EXT_FILES) {
      const src = readFileSync(join(HERE, 'ext-tabs', f), 'utf8');
      let cur = null;
      try { cur = readFileSync(join(dir, f), 'utf8'); } catch { /* missing */ }
      if (cur !== src) writeFileSync(join(dir, f), src);
    }
    return dir;
  } catch { return existsSync(join(dir, 'manifest.json')) ? dir : null; }
}

// Site key of a URL: host without "www."; '' for blank and browser pages.
export function siteOf(u) {
  try {
    const x = new URL(u);
    if (!/^https?:$/.test(x.protocol)) return '';
    return x.hostname.toLowerCase().replace(/^www\./, '');
  } catch { return ''; }
}

export function isBlankUrl(u) {
  const s = String(u || '');
  return s === '' || s === 'about:blank' || /^(edge|chrome):\/\/(newtab|new-tab-page)\/?$/i.test(s);
}

// ---------- Agent browser settings per chat (0.15.0, contract 09.11.2026, same in CO Desktop) ----------
// Bridge passes the effective settings of the chat as env vars AIWB_BROWSER_*; the client forwards them with every
// request. Every value is clamped; missing or broken values fall back to the built-in defaults.
export const BROWSER_DEFAULTS = Object.freeze({ tabs: 0, idleMin: 120, formIdleMin: 360, captchaKeep: true });

function intIn(v, lo, hi, d) {
  if (v === null || v === undefined || (typeof v === 'string' && !v.trim()) || typeof v === 'boolean') return d;
  const n = Number(v);
  return Number.isFinite(n) ? Math.min(hi, Math.max(lo, Math.round(n))) : d;
}

function boolOf(v, d) {
  if (typeof v === 'boolean') return v;
  const s = String(v ?? '').trim().toLowerCase();
  if (['1', 'true', 'yes', 'on'].includes(s)) return true;
  if (['0', 'false', 'no', 'off'].includes(s)) return false;
  return d;
}

// {tabs 0..20 (0 = unlimited), idleMin 5..1440, formIdleMin idleMin..2880, captchaKeep} from a raw object.
export function browserSettings(raw) {
  const b = raw && typeof raw === 'object' ? raw : {};
  const D = BROWSER_DEFAULTS;
  const idleMin = intIn(b.idleMin, 5, 1440, D.idleMin);
  return {
    tabs: intIn(b.tabs, 0, 20, D.tabs),
    idleMin,
    formIdleMin: intIn(b.formIdleMin, idleMin, 2880, Math.max(idleMin, D.formIdleMin)),
    captchaKeep: boolOf(b.captchaKeep, D.captchaKeep),
  };
}

// Close time (ms) of an agent tab: idleMin after the last action, formIdleMin when it holds typed data, never (null)
// while a captcha is pending and captchaKeep is on.
export function tabExpiry(t, settings, minuteMs = 60000) {
  const s = settings || BROWSER_DEFAULTS;
  if (t.captcha && s.captchaKeep) return null;
  return (Number(t.lastUsed) || 0) + (t.typed ? s.formIdleMin : s.idleMin) * minuteMs;
}

// Tab of the chat to reuse when the chat holds its limit: least recently used, preferring tabs without typed data
// and without a pending captcha. items: [{lastUsed, typed, captcha, ...}]; returns one of them or null.
export function lruPick(items) {
  const rank = t => (t.captcha ? 2 : 0) + (t.typed ? 1 : 0);
  return [...items].sort((a, b) => rank(a) - rank(b) || (a.lastUsed || 0) - (b.lastUsed || 0))[0] ?? null;
}

// ---------- Raw CDP on the browser endpoint ----------
async function cdpBrowser(port, fn, timeoutMs = 8000) {
  const ver = await (await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(3000) })).json();
  const ws = new WebSocket(ver.webSocketDebuggerUrl);
  let id = 0;
  const waiting = new Map();
  ws.onmessage = ev => {
    let m; try { m = JSON.parse(String(ev.data)); } catch { return; }
    if (m.id !== undefined && waiting.has(m.id)) { const w = waiting.get(m.id); waiting.delete(m.id); m.error ? w.reject(new Error(m.error.message)) : w.resolve(m.result); }
  };
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error('cdp socket error')); });
  const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
    const n = ++id;
    waiting.set(n, { resolve, reject });
    ws.send(JSON.stringify({ id: n, method, params, ...(sessionId ? { sessionId } : {}) }));
  });
  let timer;
  try {
    return await Promise.race([fn(send), new Promise((_, rej) => { timer = setTimeout(() => rej(new Error('cdp timeout')), timeoutMs); })]);
  } finally { clearTimeout(timer); try { ws.close(); } catch { /* ignore */ } }
}

// Tabs reported by the extension: Map targetId -> {pinned, active, discarded, frozen, lastAccessed}, or null when the
// extension does not run (Edge started before 0.13.0 / CO Desktop 0.9.6): locks are unknown then.
async function extensionTabs(send, targets) {
  const sw = targets.find(t => t.type === 'service_worker' && /^chrome-extension:\/\/[a-p]{32}\//.test(t.url) && t.url.endsWith(SW_FILE));
  if (!sw) return null;
  const { sessionId } = await send('Target.attachToTarget', { targetId: sw.targetId, flatten: true });
  try {
    const r = await send('Runtime.evaluate', { expression: 'self.coTabsVersion >= 1 ? self.coTabs() : null', awaitPromise: true, returnByValue: true }, sessionId);
    const list = r?.result?.value;
    if (!Array.isArray(list)) return null;
    return new Map(list.filter(t => t && t.targetId).map(t => [t.targetId, t]));
  } finally { send('Target.detachFromTarget', { sessionId }).catch(() => {}); }
}

// Every normal tab of the browser with its holder and lock. holder: 'agent' (a browser-agent daemon), 'co' (CO
// Desktop service tab), 'kept' (left open for the user by a stopped --keep-tabs session: drafts; its session takes
// it back) or 'user' (nobody: the user's tab or a leftover of a finished client).
export async function browserTabs(port, dir, keptIds = new Set()) {
  return cdpBrowser(port, async send => {
    const { targetInfos } = await send('Target.getTargets');
    let ext = null;
    try { ext = await extensionTabs(send, targetInfos); } catch { ext = null; }
    const reg = readJson(join(dir, 'ba-tabs.json')) || {};
    const co = new Set(Object.values(readJson(join(dir, 'co-tabs.json')) || {}).map(String));
    const live = pid => { try { process.kill(Number(pid), 0); return true; } catch (e) { return e.code === 'EPERM'; } };
    const agentTabs = new Map();
    for (const [pid, list] of Object.entries(reg.byPid || {})) {
      if (!live(pid) || !Array.isArray(list)) continue;
      for (const tid of list) agentTabs.set(tid, { pid: Number(pid), ...(reg.tabs?.[tid]?.pid === Number(pid) ? reg.tabs[tid] : {}) });
    }
    return {
      locksKnown: !!ext,
      tabs: targetInfos.filter(t => t.type === 'page' && !String(t.url).startsWith('devtools://')).map(t => {
        const e = ext?.get(t.targetId) || null;
        const a = agentTabs.get(t.targetId) || null;
        return {
          targetId: t.targetId, url: t.url, title: t.title, site: siteOf(t.url), blank: isBlankUrl(t.url),
          holder: co.has(t.targetId) ? 'co' : a ? 'agent' : keptIds.has(t.targetId) ? 'kept' : 'user',
          agent: a,   // {pid, session, owner, ownerAlive, lastUsed, keep, chat, typed, captcha, expires}
          locked: e ? e.pinned : null, active: e ? e.active : null, discarded: e ? (e.discarded || e.frozen) : null,
          lastAccessed: e?.lastAccessed || 0,
        };
      }),
    };
  });
}

export async function closeTarget(port, targetId) {
  return cdpBrowser(port, send => send('Target.closeTarget', { targetId }));
}
