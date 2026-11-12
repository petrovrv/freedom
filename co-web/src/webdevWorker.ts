// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: web device worker - one tab per browser (Web Lock) registers, heartbeats every 20 s, long-polls
// webdev_claim, runs the tools and posts webdev_result; write confirmations go through the page dialog.
import { type ToolCall, type ToolResult, okResult, errResult, parsePath } from './webdevCore';
import { type ConfirmRequest, mapError, queryPerm } from './webdevFsUtil';
import { fsList, fsStat, fsRead, fsSearch } from './webdevRead';
import { fsWrite, fsEdit, fsMkdir, fsDelete, fsMove } from './webdevWrite';
import { getWebDev, patchWebDev, getWebFolders, foldersForServer, registerNow, refreshPermissions } from './webdevStore';
import { dbWebdevHeartbeat, dbWebdevClaim, dbWebdevResult, DbOpError } from './dbapi';

type Confirm = (req: ConfirmRequest) => Promise<boolean>;
type Active = {
  req: ConfirmRequest;
  resolve: (ok: boolean) => void;
  timer: ReturnType<typeof setTimeout>;
  note: Notification | null;
};

const CONFIRM_MS = 120000;
const RUN_KINDS = new Set<ConfirmRequest['kind']>(['write', 'edit', 'mkdir']); // delete/move: never run-wide
const allowedRuns = new Set<string>();
let active: Active | null = null;

const keyOf = (req: ConfirmRequest): string => req.call.runId + '|' + req.call.deviceId;

export async function executeCall(call: ToolCall, confirm: Confirm): Promise<ToolResult> {
  let parsed: unknown;
  try { parsed = JSON.parse(call.argsJson); } catch { parsed = null; }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return errResult(call, 'invalid_args', 'argsJson is not a JSON object');
  }
  const args = parsed as Record<string, unknown>;
  try {
    const folders = getWebFolders();
    let value: unknown;
    switch (call.tool) {
      case 'fs_list': value = await fsList(args, folders); break;
      case 'fs_stat': value = await fsStat(args, folders); break;
      case 'fs_read': value = await fsRead(args, folders); break;
      case 'fs_search': value = await fsSearch(args, folders); break;
      case 'fs_write': value = await fsWrite(call, args, folders, confirm); break;
      case 'fs_edit': value = await fsEdit(call, args, folders, confirm); break;
      case 'fs_mkdir': value = await fsMkdir(call, args, folders, confirm); break;
      case 'fs_delete': value = await fsDelete(call, args, folders, confirm); break;
      case 'fs_move': value = await fsMove(call, args, folders, confirm); break;
      default: return errResult(call, 'invalid_args', 'Unknown tool');
    }
    return okResult(call, value);
  } catch (e) {
    return mapError(call, e);
  }
}

function settle(ok: boolean): void {
  if (!active) return;
  const { resolve, timer, note } = active;
  active = null;
  clearTimeout(timer);
  try { note?.close(); } catch { /* ignore */ }
  patchWebDev({ pending: null });
  resolve(ok);
}

function notify(req: ConfirmRequest): Notification | null {
  if (document.visibilityState !== 'hidden') return null;
  if (typeof Notification === 'undefined' || Notification.permission !== 'granted') return null;
  try {
    const n = new Notification('CO: allow file change?', { body: req.kind + ' ' + req.path, tag: 'co-webdev' });
    n.onclick = () => { window.focus(); n.close(); };
    return n;
  } catch {
    return null;
  }
}

// No answer within 120 s -> denied
export function requestConfirm(req: ConfirmRequest): Promise<boolean> {
  if (RUN_KINDS.has(req.kind) && allowedRuns.has(keyOf(req))) return Promise.resolve(true);
  settle(false);
  return new Promise<boolean>((resolve) => {
    const timer = setTimeout(() => settle(false), CONFIRM_MS);
    active = { req, resolve, timer, note: null };
    patchWebDev({ pending: { req, deadline: Date.now() + CONFIRM_MS } });
    if (active) active.note = notify(req);
  });
}

export function answerConfirm(choice: 'allow' | 'run' | 'deny'): void {
  if (!active) return;
  const { req } = active;
  if (choice === 'run' && RUN_KINDS.has(req.kind)) allowedRuns.add(keyOf(req));
  settle(choice !== 'deny');
}

// ---------- loop ----------
// Confirm callback that re-validates worker, device and folder state after the user allows
function guardedConfirm(email: string, deviceId: string): (req: ConfirmRequest) => Promise<boolean> {
  const devOk = (): boolean => {
    const dev = getWebDev();
    return !stopped && dev.email === email && dev.deviceId === deviceId;
  };
  const folderOk = async (path: string): Promise<boolean> => {
    const parsed = parsePath(path);
    if (!parsed) return false;
    const folder = getWebFolders().find((f) => f.id === parsed.folderId);
    if (!folder || folder.access !== 'write') return false;
    return (await queryPerm(folder.handle, 'readwrite')) === 'granted';
  };
  return async (req: ConfirmRequest): Promise<boolean> => {
    try {
      if (!(await requestConfirm(req))) return false;
      if (!devOk()) return false;
      if (!(await folderOk(req.path))) return false;
      if (req.to !== undefined && !(await folderOk(req.to))) return false;
      return devOk(); // re-check after async permission queries
    } catch {
      return false;
    }
  };
}

let running = false;
let restartWanted = false; // startWorker while the previous loop is still finishing (account switch)
let stopped = false;
let controller: AbortController | null = null;
const wakers = new Set<() => void>();

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    if (stopped) { resolve(); return; }
    const done = (): void => {
      clearTimeout(timer);
      wakers.delete(done);
      resolve();
    };
    const timer = setTimeout(done, ms);
    wakers.add(done);
  });
}

function isClaimedCall(item: unknown): item is { callId: string; runId: string; tool: string; argsJson: string } {
  if (!item || typeof item !== 'object') return false;
  const r = item as Record<string, unknown>;
  return typeof r.callId === 'string' && typeof r.runId === 'string'
    && typeof r.tool === 'string' && typeof r.argsJson === 'string';
}

// Retries transient failures; any answer (also 'expired') or a 4xx is final
async function postResult(callId: string, result: ToolResult): Promise<void> {
  const delays = [1000, 3000, 7000];
  let message = '';
  for (let attempt = 0; ; attempt++) {
    try {
      await dbWebdevResult(callId, result);
      return;
    } catch (e) {
      message = e instanceof Error ? e.message : String(e);
      const clientError = e instanceof DbOpError && e.status >= 400 && e.status <= 499;
      if (clientError || attempt >= delays.length) break;
      await sleep(delays[attempt]);
    }
  }
  patchWebDev({ error: 'Could not deliver a tool result: ' + message });
}

export function startWorker(): void {
  const email = getWebDev().email;
  if (email === null) return;
  if (running) { restartWanted = true; return; }
  running = true;
  stopped = false;
  const ctrl = new AbortController();
  controller = ctrl;

  const body = async (): Promise<void> => {
    patchWebDev({ runner: true });
    let timer: ReturnType<typeof setInterval> | undefined;
    try {
      await registerNow();
      timer = setInterval(() => {
        if (getWebFolders().length) {
          dbWebdevHeartbeat(getWebDev().deviceId, foldersForServer())
            .then(() => patchWebDev({ lastOkUtc: new Date().toISOString() }))
            .catch(() => {});
        }
        void refreshPermissions();
      }, 20000);
      while (!stopped && getWebDev().email === email) {
        if (!getWebFolders().length) { await sleep(3000); continue; }
        try {
          const deviceId = getWebDev().deviceId; // captured at claim time
          const calls = await dbWebdevClaim(deviceId, 20000);
          patchWebDev({ lastOkUtc: new Date().toISOString(), error: null });
          for (const item of calls as unknown[]) {
            if (!isClaimedCall(item)) continue;
            // stopped, signed out, other account or revoked meanwhile: skip (the call expires)
            if (stopped || getWebDev().email !== email || deviceId !== getWebDev().deviceId) continue;
            const result = await executeCall(item as unknown as ToolCall, guardedConfirm(email, deviceId));
            await postResult(item.callId, result);
          }
        } catch (err) {
          if (err instanceof DbOpError && err.status === 403) {
            patchWebDev({ error: 'This device was revoked or belongs to another account.' });
            await registerNow();
            await sleep(30000);
          } else {
            patchWebDev({ error: err instanceof Error ? err.message : String(err) });
            await sleep(5000);
          }
        }
      }
    } finally {
      if (timer !== undefined) clearInterval(timer);
      patchWebDev({ runner: false });
    }
  };

  const run = navigator.locks
    ? navigator.locks.request('co.webdev.loop.' + email, { signal: ctrl.signal }, body)
    : body();
  void Promise.resolve(run)
    .catch((err: unknown) => {
      if ((err as { name?: string } | null)?.name === 'AbortError') return;
      patchWebDev({ error: err instanceof Error ? err.message : String(err) });
    })
    .finally(() => {
      running = false;
      if (controller === ctrl) controller = null;
      if (restartWanted) { restartWanted = false; startWorker(); }
    });
}

export function stopWorker(): void {
  stopped = true;
  restartWanted = false;
  settle(false); // an open confirmation is denied
  controller?.abort();
  for (const wake of [...wakers]) wake();
}
