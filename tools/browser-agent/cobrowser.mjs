// cobrowser.mjs - attach to the CO browser (Edge of CO Desktop) over CDP instead of launching a browser (0.7.0).
// One Edge process per profile; every tool connects to it and works in its own tabs.
// Contract with CO Desktop 0.9.0 (files in the profile folder):
//   DevToolsActivePort  port of the running Edge (written by Edge)
//   co-browser.json     {port, pid, writer, cmdline[]} - how CO Desktop starts Edge (cmdline without URL)
//   co-tabs.json        CO Desktop service tabs - never touched here
//   ba-tabs.json        {targets[], byPid{}, tabs{}} - tabs of browser-agent clients; CO Desktop skips and never closes
//                       them; tabs{targetId: {pid, session, owner, ownerAlive, lastUsed, keep}} for the chamberlain (0.13.0)
//   lockfile            held exclusively by a running Edge (Windows)

import { spawn } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync, renameSync, openSync, closeSync, mkdirSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { ensureTabsExtension } from './chamberlain.mjs';

const LOCAL = process.env.LOCALAPPDATA || join(process.env.USERPROFILE || '.', 'AppData', 'Local');
export const CO_DIR = join(LOCAL, 'CO');
export const SETTINGS_FILE = join(CO_DIR, 'agent-browser.json');

export class CoBrowserError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}

export function profileDir(name = 'default') {
  const n = String(name || 'default').toLowerCase();
  if (!/^[a-z0-9-]{1,20}$/.test(n)) throw new CoBrowserError('bad_profile', `invalid profile name: ${name}`);
  return n === 'default' ? join(CO_DIR, 'browser') : join(CO_DIR, `browser-${n}`);
}

export function findEdge() {
  const cands = [
    join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    join(process.env.ProgramFiles || 'C:\\Program Files', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    join(LOCAL, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  ];
  return cands.find(f => existsSync(f)) || null;
}

function readJson(f) {
  try { return JSON.parse(readFileSync(f, 'utf8').replace(/^\uFEFF/, '')); } catch { return null; }
}

function writeJsonAtomic(f, obj) {
  const tmp = `${f}.${process.pid}.tmp`;
  writeFileSync(tmp, JSON.stringify(obj, null, 2));
  renameSync(tmp, f);
}

export function portFromFile(dir) {
  try {
    const n = Number(readFileSync(join(dir, 'DevToolsActivePort'), 'utf8').split(/\r?\n/)[0]);
    return Number.isInteger(n) && n > 0 && n < 65536 ? n : null;
  } catch { return null; }
}

async function portAlive(port) {
  try {
    const r = await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(2000) });
    return r.ok;
  } catch { return false; }
}

// True if a running Edge holds the profile (lockfile opened without sharing).
export function profileLocked(dir) {
  const lock = join(dir, 'lockfile');
  if (!existsSync(lock)) return false;
  try { closeSync(openSync(lock, 'r+')); return false; } catch (e) {
    return e.code === 'EBUSY' || e.code === 'EPERM' || e.code === 'EACCES';
  }
}

// 0.10.1: CO Desktop 0.9.3+ writes the agent look (orange theme + page frame) into <profile>\co-agent-look;
// load it also when Edge is started from an older co-browser.json or without one.
// 0.10.2: other profiles (CO Desktop writes the look only into its own) take the look of the default profile.
// 0.13.0: plus the tabs extension (co-agent-look	abs: pinned tabs = the user's lock, for the chamberlain), also
// added to an older --load-extension value of co-browser.json.
function withAgentLook(dir, args) {
  const tabs = ensureTabsExtension(dir);
  const i = args.findIndex(a => String(a).toLowerCase().startsWith('--load-extension='));
  if (i >= 0) {
    if (!tabs || String(args[i]).toLowerCase().includes(tabs.toLowerCase())) return args;
    return args.map((a, k) => k === i ? `${a},${tabs}` : a);
  }
  for (const base of [dir, profileDir('default')]) {
    const look = ['theme', 'frame'].map(d => join(base, 'co-agent-look', d));
    if (look.every(d => existsSync(join(d, 'manifest.json')))) return [...args, `--load-extension=${[...look, ...(tabs ? [tabs] : [])].join(',')}`];
  }
  return tabs ? [...args, `--load-extension=${tabs}`] : args;
}

// 0.11.1: Edge started with --remote-debugging-port reports navigator.webdriver = true in every tab, attached or not
// (checked 04.11 on Edge 156); Cloudflare Turnstile then fails even a human click. This flag keeps it false.
const NO_WEBDRIVER = '--disable-blink-features=AutomationControlled';
function withoutWebdriver(args) {
  return args.some(a => String(a).toLowerCase().startsWith('--disable-blink-features=')) ? args : [...args, NO_WEBDRIVER];
}

// Same flags as CO Desktop EdgeCommandLine(); co-browser.json wins if CO Desktop wrote it.
function edgeCommand(dir) {
  const cb = readJson(join(dir, 'co-browser.json'));
  if (Array.isArray(cb?.cmdline) && cb.cmdline.length > 1 && typeof cb.cmdline[0] === 'string' && existsSync(cb.cmdline[0])
      && cb.cmdline.some(a => String(a).toLowerCase() === `--user-data-dir=${dir}`.toLowerCase())) {
    return { exe: cb.cmdline[0], args: withoutWebdriver(withAgentLook(dir, cb.cmdline.slice(1).map(String))) };
  }
  const exe = findEdge();
  if (!exe) throw new CoBrowserError('edge_missing', 'Microsoft Edge not found');
  return { exe, args: withoutWebdriver(withAgentLook(dir, [`--user-data-dir=${dir}`, '--remote-debugging-port=0', '--no-first-run', '--no-default-browser-check', '--new-window'])) };
}

// Port of the running Edge of the profile; starts Edge (like CO Desktop) if none is running.
export async function ensureEdge(dir, log = () => {}) {
  for (let i = 0; i < 3; i++) {
    const p = portFromFile(dir);
    if (p && await portAlive(p)) return p;
    if (!p) break;
    await new Promise(r => setTimeout(r, 300));
  }
  if (profileLocked(dir)) {
    throw new CoBrowserError('profile_busy', 'the CO browser is open but its DevTools port does not answer; close all CO browser windows and retry');
  }
  mkdirSync(dir, { recursive: true });
  const { exe, args } = edgeCommand(dir);
  try { unlinkSync(join(dir, 'DevToolsActivePort')); } catch { /* ignore */ }
  const child = spawn(exe, [...args, 'about:blank'], { detached: true, stdio: 'ignore', windowsHide: false });
  child.unref();
  log(`started Edge pid=${child.pid} profile=${dir}`);
  for (let i = 0; i < 100; i++) {
    await new Promise(r => setTimeout(r, 150));
    const p = portFromFile(dir);
    if (p && await portAlive(p)) {
      // Only for profiles CO Desktop does not run (it rewrites the file for its own profile).
      if (!existsSync(join(dir, 'co-browser.json'))) {
        try { writeJsonAtomic(join(dir, 'co-browser.json'), { port: p, pid: child.pid, writer: 'browser-agent', cmdline: [exe, ...args] }); } catch { /* ignore */ }
      }
      return p;
    }
  }
  throw new CoBrowserError('start_failed', 'Edge did not open its DevTools port in 15 s');
}

// ---------- Filtering CDP proxy ----------
// Playwright's connectOverCDP attaches to every target of the browser and waits for each page to answer
// Page.enable. Discarded (sleeping) tabs of the user never answer, so the connect hangs; it would also hand the
// user's and CO Desktop's tabs to Playwright. The proxy between Playwright and Edge shows Playwright only the
// targets of this client: tabs it creates (Target.createTarget), their popups and the tabs passed in `allowed`
// (kept tabs taken back). Every other target is released and detached at once.
// Playwright of the local site workspace ~/aiwb-site (PW_MODULE or the pwModule setting override it)
const DEFAULT_PW = homedir().replace(/\\/g, '/') + '/aiwb-site/node_modules/playwright/index.mjs';

// 0.11.0: captcha widgets run in cross-site iframes (own renderer, OOPIF). Playwright attaches to them and enables
// Runtime there; Cloudflare Turnstile sees the debugger and answers "Verification failed" even to a human click.
// The proxy never attaches to these frames: they run like in a normal browser. Their content stays out of
// snapshots; the widget is still seen from the page (detectChallenge) and clicked through it (captcha tap).
const CAPTCHA_FRAME = /^https?:\/\/([a-z0-9-]+\.)*(challenges\.cloudflare\.com|hcaptcha\.com|arkoselabs\.com|funcaptcha\.com|recaptcha\.net)(?=[:/]|$)|^https?:\/\/(www\.)?google\.com\/recaptcha\//i;
const WORKER_PROBE_MS = 3000;   // a worker that does not answer a probe within this time is released (0.12.3)
export function isCaptchaFrame(url) { return CAPTCHA_FRAME.test(String(url || '')); }

// Frame id and URL of a document navigation reported by any page session (Network, Fetch or Page domain).
function captchaNav(m) {
  const p = m.params;
  if (!p?.frameId) return null;
  switch (m.method) {
    case 'Network.requestWillBeSent': return p.type === 'Document' ? { frameId: p.frameId, url: p.request?.url } : null;
    case 'Fetch.requestPaused': return p.resourceType === 'Document' ? { frameId: p.frameId, url: p.request?.url } : null;
    case 'Page.frameRequestedNavigation':
    case 'Page.frameStartedNavigating': return { frameId: p.frameId, url: p.url };
  }
  return null;
}

async function startProxy(browserWs, allowed, pwModule, log, ctl = {}) {
  const req = createRequire(pathToFileURL(pwModule).href);
  const { ws: WebSocket, wsServer: WebSocketServer } = req('playwright-core/lib/utilsBundle');
  const server = new WebSocketServer({ host: '127.0.0.1', port: 0, perMessageDeflate: false, maxPayload: 256 * 1024 * 1024 });
  await new Promise((resolve, reject) => { server.once('listening', resolve); server.once('error', reject); });
  server.on('connection', client => {
    const up = new WebSocket(browserWs, { perMessageDeflate: false, maxPayload: 256 * 1024 * 1024 });
    const toUp = [];               // client messages until the upstream socket opens
    const creates = new Set();     // ids of pending Target.createTarget calls of the client
    const held = new Map();        // sessionId -> [raw messages] of pages attached while a create is pending
    const hidden = new Set();      // sessionIds of foreign targets (released and detached)
    const own = new Map();         // ids of commands sent by the proxy itself -> callback for the answer or null
    const probing = new Map();     // sessionId -> {list, timer} of workers waiting for their probe answer
    const captchaFrames = new Set(); // frame ids whose document is a captcha widget (see CAPTCHA_FRAME)
    let nextId = 2_000_000_000;
    const sendUp = raw => { if (up.readyState === 1) up.send(raw); else toUp.push(raw); };
    const sendOwn = (msg, cb = null) => { const id = nextId++; own.set(id, cb); sendUp(JSON.stringify({ id, ...msg })); };
    // parent: session of the page that auto-attached the target (iframes); a child is detached through its parent.
    const release = (sessionId, parent) => {
      hidden.add(sessionId);
      sendOwn({ method: 'Runtime.runIfWaitingForDebugger', sessionId });
      sendOwn({ method: 'Target.detachFromTarget', params: { sessionId }, ...(parent ? { sessionId: parent } : {}) });
    };
    const toClient = raw => { if (client.readyState === 1) client.send(raw); };
    // 0.13.0: takes an existing tab (the user's or a free one) under this client: the attach event passes to Playwright.
    ctl.attach = tid => new Promise((resolve, reject) => {
      allowed.add(tid);
      sendOwn({ method: 'Target.attachToTarget', params: { targetId: tid, flatten: true } }, m => m.error ? reject(new Error(m.error.message)) : resolve(m.result?.sessionId));
    });
    // Gives a tab back (it did not come up, or another client took it): Playwright sees it detach, the tab stays open.
    ctl.detach = (tid, sessionId) => {
      allowed.delete(tid);
      if (sessionId) sendOwn({ method: 'Target.detachFromTarget', params: { sessionId } });
    };
    up.on('open', () => { for (const raw of toUp.splice(0)) up.send(raw); });
    up.on('close', () => client.close());
    up.on('error', e => { log(`cdp proxy upstream: ${e.message}`); client.close(); });
    client.on('close', () => up.close());
    client.on('error', () => up.close());
    client.on('message', data => {
      const raw = data.toString();
      try {
        const m = JSON.parse(raw);
        if (!m.sessionId && m.method === 'Target.createTarget') creates.add(m.id);
      } catch { /* pass through */ }
      sendUp(raw);
    });
    up.on('message', data => {
      const raw = data.toString();
      let m;
      try { m = JSON.parse(raw); } catch { toClient(raw); return; }
      if (m.id !== undefined && own.has(m.id)) { const cb = own.get(m.id); own.delete(m.id); if (cb) cb(m); return; }
      if (m.sessionId && hidden.has(m.sessionId)) return;
      if (m.sessionId && held.has(m.sessionId)) { held.get(m.sessionId).push(raw); return; }
      // An iframe target is attached before its URL is known (url ""): its frame id (= target id) is taken from
      // the document request seen earlier in the parent page.
      const nav = captchaNav(m);
      if (nav && isCaptchaFrame(nav.url)) { captchaFrames.add(nav.frameId); if (captchaFrames.size > 500) captchaFrames.clear(); }
      if (m.sessionId && m.method === 'Target.attachedToTarget' && m.params?.targetInfo?.type === 'iframe'
          && (isCaptchaFrame(m.params.targetInfo.url) || captchaFrames.has(m.params.targetInfo.targetId))) {
        log(`captcha frame left alone: ${String(m.params.targetInfo.url).slice(0, 80)}`);
        release(m.params.sessionId, m.sessionId); return;
      }
      if (m.sessionId && probing.has(m.sessionId)) {
        const pr = probing.get(m.sessionId);
        if (pr.list.length < 5000) pr.list.push(raw);
        return;
      }
      // A dedicated worker reaches the client only after it answers a probe: a worker that never answers CDP (seen
      // with WhatsApp VoIP wasm workers) would hang page.route forever. A silent worker stays attached here (only
      // resumed if it waits for the debugger) and passes to the client as soon as it answers; a dead worker sends no
      // requests. Workers are not released: their requests pass the page guard only while attached (0.12.3).
      if (m.sessionId && m.method === 'Target.attachedToTarget' && /worker$/.test(m.params?.targetInfo?.type || '')
          && m.params.targetInfo.type !== 'service_worker') {
        const sid = m.params.sessionId;
        const pr = { list: [raw], timer: null };
        probing.set(sid, pr);
        pr.timer = setTimeout(() => {
          if (probing.get(sid) !== pr) return;
          log(`worker silent for ${WORKER_PROBE_MS} ms, kept back: ${String(m.params.targetInfo.url).slice(0, 80)}`);
          if (m.params.waitingForDebugger) sendOwn({ method: 'Runtime.runIfWaitingForDebugger', sessionId: sid });
        }, WORKER_PROBE_MS);
        sendOwn({ method: 'Runtime.getIsolateId', sessionId: sid }, () => {
          if (probing.get(sid) !== pr) return;
          clearTimeout(pr.timer); probing.delete(sid);
          for (const r of pr.list) toClient(r);
        });
        return;
      }
      if (m.sessionId && m.method === 'Target.detachedFromTarget' && probing.has(m.params?.sessionId)) {
        clearTimeout(probing.get(m.params.sessionId).timer); probing.delete(m.params.sessionId); return;
      }
      if (m.sessionId && m.method === 'Target.detachedFromTarget' && hidden.delete(m.params?.sessionId)) return;
      if (!m.sessionId && m.method === 'Target.attachedToTarget') {
        const ti = m.params?.targetInfo || {};
        const sid = m.params?.sessionId;
        if (ti.type === 'browser') {
          // Target.attachToBrowserTarget (Playwright's newCDPSession): a session of the client itself.
        } else if (allowed.has(ti.targetId) || (ti.openerId && allowed.has(ti.openerId))) {
          allowed.add(ti.targetId);
        } else if (creates.size && ti.type === 'page') {
          // Edge reports the new tab before the createTarget answer: hold it until the answer names it.
          held.set(sid, [raw]); return;
        } else { release(sid); return; }
      }
      if (!m.sessionId && m.method === 'Target.detachedFromTarget') {
        const sid = m.params?.sessionId;
        if (hidden.delete(sid)) return;
        if (held.has(sid)) { held.delete(sid); return; }
      }
      if (!m.sessionId && m.id !== undefined && creates.delete(m.id)) {
        const tid = m.result?.targetId;
        if (tid) allowed.add(tid);
        for (const [sid, list] of [...held]) {
          let info = {};
          try { info = JSON.parse(list[0]).params.targetInfo; } catch { /* ignore */ }
          if (info.targetId === tid || (info.openerId && allowed.has(info.openerId))) {
            held.delete(sid); allowed.add(info.targetId); for (const r of list) toClient(r);
          } else if (!creates.size) { held.delete(sid); release(sid); }
        }
      }
      toClient(raw);
    });
  });
  return server;
}

// Number of normal tabs in the browser (all owners); closing the last one would end Edge.
export async function browserTabCount(port) {
  try {
    const r = await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(3000) });
    const list = await r.json();
    return list.filter(t => t.type === 'page' && !String(t.url || '').startsWith('devtools://')).length;
  } catch { return 0; }
}

// Playwright Browser connected to the profile's Edge through the filtering proxy. browser.close() only
// disconnects. opts.allowTargets: target ids to take back (kept tabs); opts.pwModule: path of playwright/index.mjs.
export async function connect(chromium, profile = 'default', log = () => {}, opts = {}) {
  const dir = profileDir(profile);
  const port = await ensureEdge(dir, log);
  const ver = await (await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(5000) })).json();
  if (!ver?.webSocketDebuggerUrl) throw new CoBrowserError('start_failed', 'no DevTools websocket of the CO browser');
  const allowed = new Set((opts.allowTargets || []).filter(Boolean));
  const ctl = {};
  const proxy = await startProxy(ver.webSocketDebuggerUrl, allowed, opts.pwModule || process.env.PW_MODULE || DEFAULT_PW, log, ctl);
  let browser;
  try {
    browser = await chromium.connectOverCDP(`ws://127.0.0.1:${proxy.address().port}/`, { timeout: 30000 });
  } catch (e) { proxy.close(); throw e; }
  browser.on('disconnected', () => proxy.close());
  const context = browser.contexts()[0];
  if (!context) throw new CoBrowserError('start_failed', 'no default browser context');
  return { browser, context, dir, port, attach: tid => ctl.attach(tid), detach: (tid, sid) => ctl.detach?.(tid, sid) };
}

export async function targetId(page) {
  const s = await page.context().newCDPSession(page);
  try { return (await s.send('Target.getTargetInfo')).targetInfo.targetId; } finally { s.detach().catch(() => {}); }
}

function pidAlive(pid) {
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}

// Publishes this process's tabs in ba-tabs.json (union over live client processes); details: targetId -> state.
export function publishTabs(dir, ids, details = {}) {
  const f = join(dir, 'ba-tabs.json');
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      const cur = readJson(f) || {};
      const byPid = {};
      const tabs = {};
      for (const [pid, list] of Object.entries(cur.byPid || {})) {
        if (Number(pid) !== process.pid && pidAlive(Number(pid)) && Array.isArray(list)) {
          byPid[pid] = list;
          for (const t of list) if (cur.tabs?.[t]?.pid === Number(pid)) tabs[t] = cur.tabs[t];
        }
      }
      if (ids.length) byPid[process.pid] = [...ids];
      for (const t of ids) if (details[t]) tabs[t] = { ...details[t], pid: process.pid };
      const targets = [...new Set(Object.values(byPid).flat())];
      writeJsonAtomic(f, { targets, byPid, tabs });
      return;
    } catch { /* concurrent rename: retry */ }
  }
}

// ---------- Site rules shared with CO Desktop (agent-browser.json) ----------
let blockCache = { t: 0, list: [] };

export function blockedSites() {
  if (Date.now() - blockCache.t < 5000) return blockCache.list;
  const s = readJson(process.env.BA_SITES_FILE || SETTINGS_FILE);  // env: tests only
  const list = Array.isArray(s?.Block) ? s.Block.map(x => String(x || '').trim().toLowerCase()
    .replace(/^[a-z]+:\/\//, '').replace(/[/:].*$/, '').replace(/^\*\./, '').replace(/^\.+|\.+$/g, '')).filter(Boolean) : [];
  blockCache = { t: Date.now(), list };
  return list;
}
