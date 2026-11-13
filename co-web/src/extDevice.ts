// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.62.0: CO inside the side panel of the AI Workbench browser extension (a module of the repository). The panel
// frames https://<CO site>/?ext=1; this module registers that panel as its own web device (schema 11, webdev_*
// ops, no server or Bridge change) whose tools come from the extension (browser_*), long-polls its calls and relays
// each one to the extension by postMessage. The extension runs the tools in the user's tabs and owns every
// confirmation; CO only fetches Drive files for browser_upload (it holds the Google token).
import { useEffect, useSyncExternalStore } from 'react';
import { type ToolCall, type ToolDef, newDeviceId, isDeviceId } from './webdevCore';
import { dbWebdevRegister, dbWebdevHeartbeat, dbWebdevClaim, dbWebdevResult, DbOpError } from './dbapi';
import { resolveFile, getBytes } from './drive';
import { EXT_ORIGIN, inExtension } from './extLink';

const CALL_MS = 125_000; // the extension answers confirmations within 100 s
const MAX_UPLOAD = 25 * 1024 * 1024;

export interface ExtState { connected: boolean; version: string; deviceId: string; online: boolean; error: string | null }
let state: ExtState = { connected: false, version: '', deviceId: '', online: false, error: null };
const subs = new Set<() => void>();
function patch(p: Partial<ExtState>): void { state = { ...state, ...p }; subs.forEach((f) => f()); }
export function useExtDevice(): ExtState {
  return useSyncExternalStore((f) => { subs.add(f); return () => subs.delete(f); }, () => state);
}

// ---------- postMessage link to the extension panel ----------
type Reply = { ok: boolean; content: string; untrusted: boolean; error: { code: string; message: string } | null };
let tools: ToolDef[] = [];
let readyWaiters: (() => void)[] = [];
const pending = new Map<string, (r: Reply) => void>();
let listening = false;

function listen(): void {
  if (listening) return;
  listening = true;
  window.addEventListener('message', (ev: MessageEvent) => {
    if (ev.origin !== EXT_ORIGIN || ev.source !== window.parent) return;
    const m = ev.data as Record<string, unknown> | null;
    if (!m || typeof m !== 'object') return;
    if (m.type === 'co-ext-ready' && Array.isArray(m.tools)) {
      tools = (m.tools as ToolDef[]).filter((t) => t && typeof t.name === 'string' && /^[a-z0-9_]{1,64}$/.test(t.name));
      patch({ connected: true, version: String(m.version || '') });
      readyWaiters.forEach((f) => f());
      readyWaiters = [];
    } else if (m.type === 'co-ext-result' && typeof m.id === 'string') {
      const done = pending.get(m.id);
      if (done) {
        pending.delete(m.id);
        done({
          ok: m.ok === true, content: typeof m.content === 'string' ? m.content : '', untrusted: m.untrusted === true,
          error: m.error && typeof m.error === 'object' ? m.error as Reply['error'] : null,
        });
      }
    }
  });
}

function hello(): Promise<void> {
  listen();
  return new Promise((resolve) => {
    if (state.connected) { resolve(); return; }
    readyWaiters.push(resolve);
    const ping = (): void => { if (!state.connected) { window.parent.postMessage({ type: 'co-ext-hello', v: 1 }, EXT_ORIGIN); setTimeout(ping, 2000); } };
    ping();
  });
}

function callExtension(tool: string, args: Record<string, unknown>, call: ToolCall): Promise<Reply> {
  const id = crypto.randomUUID();
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      resolve({ ok: false, content: '', untrusted: false, error: { code: 'device_timeout', message: 'The browser extension did not answer.' } });
    }, CALL_MS);
    pending.set(id, (r) => { clearTimeout(timer); resolve(r); });
    window.parent.postMessage({ type: 'co-ext-call', id, tool, args, runId: call.runId, callId: call.callId }, EXT_ORIGIN);
  });
}

// ---------- browser_upload: Drive path -> bytes ----------
function toBase64(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(bin);
}

async function driveFile(path: string): Promise<{ name: string; mime: string; dataBase64: string }> {
  const norm = path.replace(/\\/g, '/').replace(/^[A-Za-z]:\//, '').replace(/^\/+/, '').replace(/^My Drive\//i, '');
  const parts = norm.split('/').filter(Boolean);
  if (!parts.length || parts.some((p) => p === '..' || p === '.')) throw new Error('invalid Drive path');
  const name = parts.pop() as string;
  const f = await resolveFile('root', parts, name);
  if (!f) throw new Error('not found in Google Drive: ' + path);
  if (Number(f.size || 0) > MAX_UPLOAD) throw new Error('file over 25 MB');
  return { name: f.name, mime: f.mimeType || 'application/octet-stream', dataBase64: toBase64(await getBytes(f.id)) };
}

// ---------- device loop ----------
const idKey = (email: string): string => 'co.extdev.id.' + email;
function platformName(): string {
  const uad = (navigator as Navigator & { userAgentData?: { platform?: string; brands?: { brand: string }[] } }).userAgentData;
  const brand = uad?.brands?.map((b) => b.brand).find((b) => /edge|chrome/i.test(b))?.replace('Google ', '').replace('Microsoft ', '') || 'Browser';
  return brand + ' extension on ' + (uad?.platform || navigator.platform || 'this computer');
}
function loadId(email: string): string {
  let id = '';
  try { id = localStorage.getItem(idKey(email)) || ''; } catch { /* storage blocked */ }
  if (!isDeviceId(id)) {
    id = newDeviceId((n) => crypto.getRandomValues(new Uint8Array(n)));
    try { localStorage.setItem(idKey(email), id); } catch { /* ignore */ }
  }
  return id;
}

// Retries transient failures (an action may already have happened in the tab); any answer or a 4xx is final.
async function postResult(callId: string, result: Record<string, unknown>): Promise<void> {
  const delays = [1000, 3000, 7000, 15000];
  for (let attempt = 0; ; attempt++) {
    try {
      await dbWebdevResult(callId, result);
      return;
    } catch (e) {
      const clientError = e instanceof DbOpError && e.status >= 400 && e.status <= 499;
      if (clientError || attempt >= delays.length) {
        patch({ error: 'Result not delivered: ' + (e instanceof Error ? e.message : String(e)) });
        return;
      }
      await new Promise((r) => setTimeout(r, delays[attempt]));
    }
  }
}

let generation = 0; // a new runner (account switch) ends the previous loop

async function handle(call: ToolCall): Promise<Record<string, unknown>> {
  const base = { v: 1, callId: call.callId, runId: call.runId, deviceId: call.deviceId, tool: call.tool, issuedUtc: call.issuedUtc };
  let args: Record<string, unknown>;
  try {
    const p = JSON.parse(call.argsJson) as unknown;
    args = p && typeof p === 'object' && !Array.isArray(p) ? p as Record<string, unknown> : {};
  } catch {
    return { ...base, ok: false, error: { code: 'invalid_args', message: 'argsJson is not a JSON object' }, content: '', untrusted: false };
  }
  if (call.tool === 'browser_upload' && typeof args.path === 'string' && args.path) {
    try {
      args = { ...args, files: [await driveFile(args.path)] };
      delete args.path;
    } catch (e) {
      return { ...base, ok: false, error: { code: 'not_found', message: e instanceof Error ? e.message : String(e) }, content: '', untrusted: false };
    }
  }
  const r = await callExtension(call.tool, args, call);
  return { ...base, ok: r.ok, error: r.ok ? null : r.error ?? { code: 'internal', message: 'failed' }, content: r.content, untrusted: r.untrusted };
}

async function loop(email: string, gen: number): Promise<void> {
  const stopped = (): boolean => gen !== generation;
  await hello();
  let deviceId = loadId(email);
  patch({ deviceId });
  const register = async (): Promise<void> => {
    try {
      await dbWebdevRegister({ deviceId, name: platformName(), folders: [], tools });
    } catch (e) {
      // A revoked id never registers again: start a new one.
      if (e instanceof DbOpError && e.status === 403) {
        try { localStorage.removeItem(idKey(email)); } catch { /* ignore */ }
        deviceId = loadId(email);
        patch({ deviceId });
        await dbWebdevRegister({ deviceId, name: platformName(), folders: [], tools });
      } else throw e;
    }
  };
  await register();
  patch({ online: true, error: null });
  const beat = setInterval(() => {
    dbWebdevHeartbeat(deviceId, []).then(() => patch({ online: true })).catch(() => patch({ online: false }));
  }, 20_000);
  try {
    while (!stopped()) {
      try {
        const calls = await dbWebdevClaim(deviceId, 20_000);
        patch({ online: true, error: null });
        for (const item of calls as unknown as ToolCall[]) {
          if (stopped() || !item || typeof item.callId !== 'string' || typeof item.argsJson !== 'string') continue;
          const result = await handle(item);
          await postResult(item.callId, result);
        }
      } catch (err) {
        patch({ error: err instanceof Error ? err.message : String(err) });
        if (err instanceof DbOpError && err.status === 403) await register().catch(() => undefined);
        await new Promise((r) => setTimeout(r, 5000));
      }
    }
  } finally {
    clearInterval(beat);
    patch({ online: false });
  }
}

// Runs the extension device while CO is open in the panel and signed in.
export function useExtDeviceRunner(email: string | null): void {
  useEffect(() => {
    if (!email || !inExtension()) return;
    const gen = ++generation;
    const run = async (): Promise<void> => {
      while (gen === generation) {
        try {
          await loop(email, gen);
        } catch (e) {
          patch({ error: e instanceof Error ? e.message : String(e) });
          await new Promise((r) => setTimeout(r, 10_000));
        }
      }
    };
    void (navigator.locks ? navigator.locks.request('co.extdev.loop.' + email, run) : run());
    return () => { if (generation === gen) generation++; };
  }, [email]);
}
