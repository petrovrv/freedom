// SPDX-License-Identifier: AGPL-3.0-or-later
// Fast transport, browser side (Bridge v0.9 "fast-transport" capability).
// Pub/Sub carries hints and live copies only; Drive stays canonical. Every failure here
// silently disables the fast path for a while and the Drive polling keeps working.

import { getToken } from './auth';
import type { OwnerInfo } from './types';

const API = 'https://pubsub.googleapis.com/v1';

export interface FastConfig {
  projectId: string;
  jobsTopic: string;
  eventsTopic: string;
  webSubscription: string; // per-environment pull subscription, created by Bridge
}

export interface FastMessage {
  type: 'job.claimed' | 'job.events' | 'job.result' | string;
  jobId: string;
  data: any;
  sentMs?: number;
}

type Listener = (m: FastMessage) => void;

let cfg: FastConfig | null = null;
let disabledUntil = 0;
let subName: string | null = null;
let pulling = false;
const listeners = new Map<string, Set<Listener>>();
const early = new Map<string, { at: number; msgs: FastMessage[] }>(); // messages before a listener exists
let lastOkMs = 0;

export function fastConfigFromOwner(owner: OwnerInfo | null, env: string): FastConfig | null {
  const ft = (owner as any)?.fastTransport;
  if (!owner?.capabilities?.includes('fast-transport') || !ft?.projectId || !ft?.jobsTopic || !ft?.webSubscriptionPattern) return null;
  return {
    projectId: String(ft.projectId),
    jobsTopic: String(ft.jobsTopic),
    eventsTopic: String(ft.eventsTopic),
    webSubscription: String(ft.webSubscriptionPattern).replace('{env}', env),
  };
}

export function configureFast(next: FastConfig | null) {
  if (JSON.stringify(next) === JSON.stringify(cfg)) return;
  cfg = next;
  subName = null;
  if (cfg && listeners.size) void pullLoop();
}

export function fastHealthy(): boolean {
  return !!cfg && Date.now() >= disabledUntil && Date.now() - lastOkMs < 120000;
}

function disable(reason: unknown) {
  disabledUntil = Date.now() + 5 * 60000;
  console.info('fast transport off for 5 min:', reason);
}

async function call(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<any> {
  const token = getToken();
  if (!token) throw new Error('no token');
  const r = await fetch(`${API}/${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (r.status === 409) return null;
  if (!r.ok) throw new Error(`Pub/Sub ${r.status}: ${(await r.text()).slice(0, 200)}`);
  lastOkMs = Date.now();
  return r.status === 204 ? null : r.json();
}

const b64 = (o: unknown) => btoa(unescape(encodeURIComponent(JSON.stringify(o))));
const unb64 = (s: string) => JSON.parse(decodeURIComponent(escape(atob(s))));

// ---- CO -> Bridge ----

async function publishJobs(type: string, jobId: string, data: unknown) {
  if (!cfg || Date.now() < disabledUntil) return;
  try {
    await call('POST', `projects/${cfg.projectId}/topics/${cfg.jobsTopic}:publish`, {
      messages: [{ data: b64(data), attributes: { type, jobId } }],
    });
  } catch (e) {
    disable(e);
  }
}

export const publishJobReady = (jobId: string, folderId: string) => publishJobs('job.ready', jobId, { jobId, folderId });
// 0.19.0: the job waits in the database queue; Bridge claims it through db.php.
export const publishJobReadyDb = (jobId: string) => publishJobs('job.ready', jobId, { jobId, db: true });
export const publishJobCancel = (jobId: string) => publishJobs('job.cancel', jobId, { jobId });

// ---- Bridge -> CO ----

export function watchJob(jobId: string, l: Listener): () => void {
  let set = listeners.get(jobId);
  if (!set) listeners.set(jobId, (set = new Set()));
  set.add(l);
  const buffered = early.get(jobId);
  if (buffered) {
    early.delete(jobId);
    for (const m of buffered.msgs) l(m);
  }
  void pullLoop();
  return () => {
    set!.delete(l);
    if (!set!.size) listeners.delete(jobId);
  };
}

// Live output is partitioned per environment; Bridge owns the subscription, access is IAM.
// Several open tabs share it: each message reaches one tab, the others fall back to Drive.
async function ensureSubscription(): Promise<string> {
  if (!subName) subName = cfg!.webSubscription;
  return subName;
}

function dispatch(m: FastMessage) {
  const set = listeners.get(m.jobId);
  if (set?.size) {
    for (const l of set) l(m);
    return;
  }
  const now = Date.now();
  for (const [k, v] of early) if (now - v.at > 120000) early.delete(k);
  const e = early.get(m.jobId) || { at: now, msgs: [] };
  e.msgs.push(m);
  early.set(m.jobId, e);
}

async function pullLoop() {
  if (pulling) return;
  pulling = true;
  try {
    while (cfg && listeners.size && Date.now() >= disabledUntil) {
      const c = cfg;
      const sub = await ensureSubscription();
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 60000);
      let j: any;
      try {
        j = await call('POST', `projects/${c.projectId}/subscriptions/${sub}:pull`, { maxMessages: 50 }, ctl.signal);
      } catch (e) {
        if ((e as Error).name === 'AbortError') continue;
        throw e;
      } finally {
        clearTimeout(timer);
      }
      const rec: any[] = j?.receivedMessages || [];
      if (!rec.length) continue;
      void call('POST', `projects/${c.projectId}/subscriptions/${sub}:acknowledge`, { ackIds: rec.map((r) => r.ackId) }).catch(() => undefined);
      for (const r of rec) {
        const a = r.message?.attributes || {};
        try {
          dispatch({ type: a.type, jobId: a.jobId, sentMs: Number(a.sentMs) || undefined, data: r.message?.data ? unb64(r.message.data) : null });
        } catch (e) {
          console.warn('fast message', e);
        }
      }
    }
  } catch (e) {
    subName = null;
    disable(e);
  } finally {
    pulling = false;
  }
}

// Decodes a job.result message into the verified result object (same check as the Drive path).
export async function decodeFastResult(data: any, sha256Hex: (b: ArrayBuffer) => Promise<string>): Promise<any | null> {
  try {
    const bin = Uint8Array.from(atob(String(data.result)), (ch) => ch.charCodeAt(0));
    const marker = JSON.parse(String(data.marker).replace(/^﻿/, ''));
    const buf = bin.buffer.slice(bin.byteOffset, bin.byteOffset + bin.byteLength) as ArrayBuffer;
    if (bin.byteLength !== marker.sizeBytes) return null;
    if ((await sha256Hex(buf)).toLowerCase() !== String(marker.sha256).toLowerCase()) return null;
    return JSON.parse(new TextDecoder().decode(bin).replace(/^﻿/, ''));
  } catch {
    return null;
  }
}
