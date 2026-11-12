// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: "This browser as a device" state - device id/name (localStorage), folder handles (IndexedDB), register.
import { newDeviceId, isDeviceId, deviceShort, slugFolderId, TOOL_DEFS } from './webdevCore';
import type { FolderAccess } from './webdevCore';
import { queryPerm, requestPerm } from './webdevFsUtil';
import type { WebFolder, ConfirmRequest } from './webdevFsUtil';
import { dbWebdevHeartbeat, dbWebdevRegister, dbWebdevRevoke } from './dbapi';

export interface FolderState { id: string; name: string; access: FolderAccess; permission: string } // 'granted' | 'prompt' | 'denied'
export interface PendingConfirm { req: ConfirmRequest; deadline: number }
export interface WebDevState {
  email: string | null; deviceId: string; short: string; name: string; folders: FolderState[];
  runner: boolean; lastOkUtc: string | null; error: string | null; pending: PendingConfirm | null;
}

interface PickerWindow { showDirectoryPicker?: (opts?: { mode?: 'read' | 'readwrite' }) => Promise<FileSystemDirectoryHandle> }
interface StoredFolder { id: string; name: string; access: FolderAccess; handle: FileSystemDirectoryHandle }
interface ChangedMsg { type?: string; email?: string }

const DB_NAME = 'co-webdev';
const STORE = 'folders';
const CHANNEL = 'co.webdev';
const idKey = (email: string) => 'co.webdev.id.' + email;
const nameKey = (email: string) => 'co.webdev.name.' + email;

let state: WebDevState = {
  email: null, deviceId: '', short: '', name: '', folders: [],
  runner: false, lastOkUtc: null, error: null, pending: null,
};
const listeners = new Set<(s: WebDevState) => void>();
let webFolders: WebFolder[] = [];
let channel: BroadcastChannel | null = null;
let initFor: string | null = null;
let initP: Promise<void> | null = null;
let loadFailed = false; // IndexedDB read failed: never overwrite the stored list with a partial one

export function getWebDev(): WebDevState { return state; }

export function subscribeWebDev(fn: (s: WebDevState) => void): () => void {
  listeners.add(fn);
  return () => { listeners.delete(fn); };
}

export function patchWebDev(p: Partial<WebDevState>): void {
  state = { ...state, ...p };
  for (const fn of listeners) fn(state);
}

export function webdevSupported(): boolean {
  return typeof (window as unknown as PickerWindow).showDirectoryPicker === 'function' && typeof indexedDB !== 'undefined';
}

export function insideDesktop(): boolean {
  return !!(window as unknown as { coDesktop?: unknown }).coDesktop;
}

export function getWebFolders(): WebFolder[] { return webFolders; }

export function foldersForServer(): { id: string; name: string; access: FolderAccess }[] {
  return webFolders.map((f) => ({ id: f.id, name: f.name, access: f.access }));
}

export function defaultDeviceName(): string {
  const nav = navigator as Navigator & { userAgentData?: { platform?: string } };
  return 'Browser on ' + (nav.userAgentData?.platform || nav.platform || 'web');
}

// localStorage helpers (may throw in restricted contexts)
function lsGet(k: string): string | null { try { return localStorage.getItem(k); } catch { return null; } }
function lsSet(k: string, v: string): void { try { localStorage.setItem(k, v); } catch { /* ignore */ } }
function lsDel(k: string): void { try { localStorage.removeItem(k); } catch { /* ignore */ } }

// IndexedDB helpers
function idbOpen(): Promise<IDBDatabase> {
  return new Promise((res, rej) => {
    const r = indexedDB.open(DB_NAME, 1);
    r.onupgradeneeded = () => { if (!r.result.objectStoreNames.contains(STORE)) r.result.createObjectStore(STORE); };
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
}

async function idbGet(key: string): Promise<unknown> {
  const db = await idbOpen();
  try {
    return await new Promise((res, rej) => {
      const r = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
      r.onsuccess = () => res(r.result);
      r.onerror = () => rej(r.error);
    });
  } finally { db.close(); }
}

async function idbPut(key: string, value: unknown): Promise<void> {
  const db = await idbOpen();
  try {
    await new Promise<void>((res, rej) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(value, key);
      tx.oncomplete = () => res();
      tx.onerror = () => rej(tx.error);
      tx.onabort = () => rej(tx.error);
    });
  } finally { db.close(); }
}

const modeOf = (a: FolderAccess): 'read' | 'readwrite' => (a === 'write' ? 'readwrite' : 'read');

function permOf(f: WebFolder): Promise<string> {
  return queryPerm(f.handle, modeOf(f.access)).catch(() => 'prompt');
}

async function computeStates(): Promise<FolderState[]> {
  return Promise.all(webFolders.map(async (f) => ({ id: f.id, name: f.name, access: f.access, permission: await permOf(f) })));
}

function isStored(v: unknown): v is StoredFolder {
  const o = v as StoredFolder | null;
  return !!o && typeof o.id === 'string' && typeof o.name === 'string' && (o.access === 'read' || o.access === 'write') && !!o.handle;
}

async function loadFolders(email: string): Promise<void> {
  let raw: unknown = null;
  try {
    raw = await idbGet(email);
    loadFailed = false;
  } catch {
    loadFailed = true;
    webFolders = []; // never keep another account's handles
    patchWebDev({ error: 'Could not read the saved folder list in this browser; reload the page.' });
    return;
  }
  const arr = Array.isArray(raw) ? raw.filter(isStored) : [];
  webFolders = arr.map((f) => ({ id: f.id, name: f.name, access: f.access, handle: f.handle }));
}

function freshId(): string { return newDeviceId((n) => crypto.getRandomValues(new Uint8Array(n))); }

function loadIdentity(email: string): { deviceId: string; short: string; name: string } {
  let id = lsGet(idKey(email));
  if (!id || !isDeviceId(id)) { id = freshId(); lsSet(idKey(email), id); }
  const name = lsGet(nameKey(email)) || defaultDeviceName();
  return { deviceId: id, short: deviceShort(id), name };
}

async function reload(): Promise<void> {
  const email = state.email;
  if (!email) return;
  const ident = loadIdentity(email);
  await loadFolders(email);
  const folders = await computeStates();
  if (state.email === email) patchWebDev({ ...ident, folders });
}

async function save(): Promise<void> {
  if (!state.email) return;
  if (loadFailed) throw new Error('The saved folder list could not be read; reload the page before changing folders.');
  await idbPut(state.email, webFolders.map((f) => ({ id: f.id, name: f.name, access: f.access, handle: f.handle })));
}

function broadcast(): void {
  if (channel && state.email) channel.postMessage({ type: 'changed', email: state.email });
}

async function commit(): Promise<void> {
  await save();
  broadcast();
  await registerNow();
}

function requireEmail(): string {
  if (!state.email) throw new Error('Not signed in.');
  return state.email;
}

export async function initWebDev(email: string): Promise<void> {
  if (initFor === email && initP) return initP;
  initFor = email;
  initP = (async () => {
    const ident = loadIdentity(email);
    await loadFolders(email);
    const folders = await computeStates();
    patchWebDev({ email, ...ident, folders, lastOkUtc: null, error: null, pending: null });
    if (!channel && typeof BroadcastChannel !== 'undefined') {
      channel = new BroadcastChannel(CHANNEL);
      channel.onmessage = (ev: MessageEvent) => {
        const d = ev.data as ChangedMsg | null;
        if (d && d.type === 'changed' && d.email && d.email === state.email) void reload();
      };
    }
  })();
  return initP;
}

export async function addFolder(): Promise<void> {
  requireEmail();
  const picker = (window as unknown as PickerWindow).showDirectoryPicker;
  if (!picker) return;
  let handle: FileSystemDirectoryHandle;
  try { handle = await picker.call(window, { mode: 'readwrite' }); } catch (e) {
    if ((e as { name?: string } | null)?.name === 'AbortError') return;
    throw e;
  }
  for (const f of webFolders) if (await f.handle.isSameEntry(handle)) return;
  const folder: WebFolder = { id: slugFolderId(handle.name, webFolders.map((f) => f.id)), name: handle.name, access: 'write', handle };
  webFolders = [...webFolders, folder];
  const permission = await permOf(folder);
  patchWebDev({ folders: [...state.folders, { id: folder.id, name: folder.name, access: folder.access, permission }] });
  await commit();
}

export async function removeFolder(id: string): Promise<void> {
  requireEmail();
  webFolders = webFolders.filter((f) => f.id !== id);
  patchWebDev({ folders: state.folders.filter((f) => f.id !== id) });
  await commit();
}

export async function setFolderAccess(id: string, access: FolderAccess): Promise<void> {
  requireEmail();
  webFolders = webFolders.map((f) => (f.id === id ? { ...f, access } : f));
  patchWebDev({ folders: await computeStates() });
  await commit();
}

export async function renameDevice(name: string): Promise<void> {
  const email = requireEmail();
  const n = name.trim().slice(0, 120) || defaultDeviceName();
  lsSet(nameKey(email), n);
  patchWebDev({ name: n });
  await commit();
}

// Must be called from a user gesture (click) so permission prompts can show
export async function reconnectFolders(): Promise<void> {
  const perms = new Map(state.folders.map((f) => [f.id, f.permission]));
  // SPEC §12: one click for all folders - issue every request before any await (same transient user activation)
  const requests: Promise<unknown>[] = [];
  for (const f of webFolders) {
    if (perms.get(f.id) === 'granted') continue;
    try {
      requests.push(Promise.resolve(requestPerm(f.handle, modeOf(f.access))));
    } catch {
      // a synchronous throw must not stop the remaining requests; keep folder
    }
  }
  await Promise.allSettled(requests); // folders are never dropped
  await refreshPermissions();
  await registerNow();
}

export async function registerNow(): Promise<void> {
  if (!state.email || loadFailed) return;
  if (webFolders.length === 0) {
    // the last folder was removed: clear the server's list (403 = never registered, nothing to clear)
    await dbWebdevHeartbeat(state.deviceId, []).catch(() => {});
    return;
  }
  try {
    await dbWebdevRegister({ deviceId: state.deviceId, name: state.name, folders: foldersForServer(), tools: TOOL_DEFS });
    patchWebDev({ lastOkUtc: new Date().toISOString(), error: null });
  } catch (e) {
    patchWebDev({ error: e instanceof Error ? e.message : String(e) });
  }
}

export async function revokeDevice(): Promise<void> {
  const email = requireEmail();
  await dbWebdevRevoke(state.deviceId);
  lsDel(idKey(email));
  await idbPut(email, []);
  webFolders = [];
  const id = freshId();
  lsSet(idKey(email), id);
  patchWebDev({ deviceId: id, short: deviceShort(id), folders: [], lastOkUtc: null, error: null, pending: null });
  broadcast();
}

export async function refreshPermissions(): Promise<void> {
  patchWebDev({ folders: await computeStates() });
}
