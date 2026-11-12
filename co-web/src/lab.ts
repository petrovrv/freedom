// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Lab: Drive round-trip latency probe for the remote-tools design.
// A "call" file is written through the Drive API; the answering side writes a "result" file.
// Mode "g": the home PC answers from its G: (Drive for Desktop) mirror (lab-watcher.ps1).
// Mode "api": this page answers itself through the API (lower bound for an API-only Bridge).

import { onAuth, signIn } from './auth';
import { CLIENT_ID } from './config';
import { createFile, ensurePath, getJson, list, q, trashFile, type DriveFile } from './drive';
import { discoverRoot } from './workbench';

interface ResultFile {
  id: string;
  seenAt: number;
  writtenAt: number;
  side: string;
}

interface Row {
  seq: number;
  mode: string;
  createMs: number;
  rttMs: number;
  upMs: number;
  homeMs: number;
  downMs: number;
  polls: number;
}

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const status = (s: string) => ($('status').textContent = s);
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

let dirs: { calls: string; results: string; runs: string } | null = null;
let stopFlag = false;
const created: string[] = [];

onAuth((s) => {
  const ok = s.status === 'signed_in';
  $('who').textContent = ok ? s.email : s.status === 'expired' ? 'Session expired — sign in again' : s.error || '';
  $<HTMLButtonElement>('runG').disabled = !ok;
  $<HTMLButtonElement>('runApi').disabled = !ok;
  $<HTMLButtonElement>('grantPs').disabled = !ok;
});

$('signin').onclick = async () => {
  try {
    await signIn();
  } catch (e) {
    status('Sign-in failed: ' + (e as Error).message);
  }
};

async function ensureDirs() {
  if (dirs) return dirs;
  status('Locating AI-Workbench…');
  const root = await discoverRoot();
  const base = ['lab', 'latency'];
  dirs = {
    calls: await ensurePath(root.id, [...base, 'calls']),
    results: await ensurePath(root.id, [...base, 'results']),
    runs: await ensurePath(root.id, [...base, 'runs']),
  };
  return dirs;
}

// Mode "api": answer our own calls through the API, as an API-polling Bridge would.
async function apiResponder(d: NonNullable<typeof dirs>, pollMs: number, done: () => boolean) {
  const answered = new Set<string>();
  while (!done()) {
    const files = await list(`${q(d.calls)} in parents and name contains 'api-' and trashed=false`, 'id,name');
    for (const f of files) {
      const id = f.name.replace(/\.json$/, '');
      if (answered.has(id)) continue;
      answered.add(id);
      const seenAt = Date.now();
      const body: ResultFile = { id, seenAt, writtenAt: Date.now(), side: 'api-echo' };
      const r = await createFile(d.results, `${id}.json`, JSON.stringify(body), 'application/json');
      created.push(r.id);
    }
    await sleep(pollMs);
  }
}

function stats(xs: number[]) {
  const s = [...xs].sort((a, b) => a - b);
  const pct = (p: number) => s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))];
  const mean = s.reduce((a, b) => a + b, 0) / (s.length || 1);
  return { n: s.length, min: s[0], median: pct(50), p90: pct(90), max: s[s.length - 1], mean: Math.round(mean) };
}

function addRow(r: Row) {
  const tr = document.createElement('tr');
  for (const v of [r.seq, r.mode, r.createMs, r.rttMs, r.upMs, r.homeMs, r.downMs, r.polls]) {
    const td = document.createElement('td');
    td.textContent = String(v);
    tr.appendChild(td);
  }
  $('tbl').querySelector('tbody')!.appendChild(tr);
}

async function run(mode: 'g' | 'api') {
  const n = Math.max(1, Math.min(100, Number($<HTMLInputElement>('n').value) || 20));
  const pollMs = Math.max(200, Number($<HTMLInputElement>('poll').value) || 400);
  stopFlag = false;
  for (const b of ['runG', 'runApi']) $<HTMLButtonElement>(b).disabled = true;
  $<HTMLButtonElement>('stop').disabled = false;
  const rows: Row[] = [];
  let responderDone = false;
  try {
    const d = await ensureDirs();
    const responder = mode === 'api' ? apiResponder(d, pollMs, () => responderDone) : Promise.resolve();
    const runTag = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
    for (let i = 1; i <= n && !stopFlag; i++) {
      const id = `${mode}-${runTag}-${String(i).padStart(3, '0')}`;
      status(`${mode === 'g' ? 'G: sync' : 'API echo'}: iteration ${i}/${n}`);
      const t0 = Date.now();
      const call = await createFile(d.calls, `${id}.json`, JSON.stringify({ id, t0 }), 'application/json');
      created.push(call.id);
      const createMs = Date.now() - t0;
      let found: DriveFile | undefined;
      let polls = 0;
      const deadline = t0 + 180000;
      while (!found && !stopFlag && Date.now() < deadline) {
        polls++;
        const hits = await list(`${q(d.results)} in parents and name=${q(id + '.json')} and trashed=false`, 'id,name');
        found = hits[0];
        if (!found) await sleep(pollMs);
      }
      const t2 = Date.now();
      if (!found) {
        status(stopFlag ? 'Stopped' : `Timeout on iteration ${i}: no result after 180 s. Is lab-watcher.ps1 running?`);
        break;
      }
      created.push(found.id);
      const res = await getJson<ResultFile>(found.id);
      const row: Row = {
        seq: i,
        mode,
        createMs,
        rttMs: t2 - t0,
        upMs: res.seenAt - t0,
        homeMs: res.writtenAt - res.seenAt,
        downMs: t2 - res.writtenAt,
        polls,
      };
      rows.push(row);
      addRow(row);
      await sleep(300);
    }
    responderDone = true;
    await responder;
    const summary = {
      mode,
      pollMs,
      when: new Date().toISOString(),
      userAgent: navigator.userAgent,
      rtt: stats(rows.map((r) => r.rttMs)),
      create: stats(rows.map((r) => r.createMs)),
      up: stats(rows.map((r) => r.upMs)),
      down: stats(rows.map((r) => r.downMs)),
      rows,
    };
    $('summary').textContent = JSON.stringify({ ...summary, rows: undefined }, null, 2);
    if (rows.length) {
      await createFile(d.runs, `run-${mode}-${runTag}.json`, JSON.stringify(summary, null, 2), 'application/json');
      status(`Done: ${rows.length} iterations, median RTT ${summary.rtt.median} ms. Saved to AI-Workbench/lab/latency/runs.`);
    }
    // Remove call/result files; the run summary stays.
    for (const id of created.splice(0)) await trashFile(id).catch(() => undefined);
  } catch (e) {
    status('Error: ' + (e as Error).message);
  } finally {
    responderDone = true;
    for (const b of ['runG', 'runApi']) $<HTMLButtonElement>(b).disabled = false;
    $<HTMLButtonElement>('stop').disabled = true;
  }
}

// ---------- Pub/Sub echo ----------
// Separate token with the pubsub scope, kept in memory only like the Drive token.
const PS_SCOPE = 'https://www.googleapis.com/auth/pubsub';
const PS = 'https://pubsub.googleapis.com/v1';
let psToken: string | null = null;

$('grantPs').onclick = () => {
  const client = google.accounts.oauth2.initTokenClient({
    client_id: CLIENT_ID,
    scope: PS_SCOPE,
    callback: (resp) => {
      if (resp.error) return status('Pub/Sub grant failed: ' + (resp.error_description || resp.error));
      psToken = resp.access_token;
      $<HTMLButtonElement>('runPs').disabled = false;
      status('Pub/Sub access granted.');
    },
  });
  client.requestAccessToken({ prompt: '' } as google.accounts.oauth2.OverridableTokenClientConfig);
};

async function ps(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<any> {
  const r = await fetch(`${PS}/${path}`, {
    method,
    headers: { Authorization: `Bearer ${psToken}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (r.status === 409) return null; // already exists
  if (!r.ok) throw new Error(`Pub/Sub ${method} ${path}: ${r.status} ${await r.text()}`);
  return r.json();
}

const b64 = (o: unknown) => btoa(JSON.stringify(o));
const unb64 = (s: string) => JSON.parse(atob(s));

async function psSetup(project: string) {
  const p = `projects/${project}`;
  for (const t of ['co-lab-calls', 'co-lab-results']) {
    await ps('PUT', `${p}/topics/${t}`, {});
    await ps('PUT', `${p}/subscriptions/${t}-sub`, {
      topic: `${p}/topics/${t}`,
      ackDeadlineSeconds: 10,
      messageRetentionDuration: '600s',
      expirationPolicy: { ttl: '86400s' },
    });
  }
  return p;
}

// One unary pull; returns decoded payloads and acks them.
async function psPull(p: string, sub: string, signal: AbortSignal): Promise<any[]> {
  const j = await ps('POST', `${p}/subscriptions/${sub}:pull`, { maxMessages: 10 }, signal);
  const msgs = (j?.receivedMessages || []) as { ackId: string; message: { data: string } }[];
  if (!msgs.length) return [];
  await ps('POST', `${p}/subscriptions/${sub}:acknowledge`, { ackIds: msgs.map((m) => m.ackId) });
  return msgs.map((m) => unb64(m.message.data));
}

async function runPubSub() {
  const n = Math.max(1, Math.min(100, Number($<HTMLInputElement>('n').value) || 20));
  const project = $<HTMLInputElement>('proj').value.trim();
  stopFlag = false;
  for (const b of ['runG', 'runApi', 'runPs']) $<HTMLButtonElement>(b).disabled = true;
  $<HTMLButtonElement>('stop').disabled = false;
  const ctl = new AbortController();
  const rows: Row[] = [];
  const waiting = new Map<string, (r: ResultFile) => void>();
  try {
    status('Creating Pub/Sub topics…');
    const tSetup = Date.now();
    const p = await psSetup(project);
    const setupMs = Date.now() - tSetup;
    // Drain leftovers from earlier runs. A pull on an empty subscription is held open by
    // the server, so each drain is capped at 1.5 s; this wait is test setup, not transport.
    status('Draining old messages…');
    const tDrain = Date.now();
    for (const sub of ['co-lab-calls-sub', 'co-lab-results-sub']) {
      await psPull(p, sub, AbortSignal.any([ctl.signal, AbortSignal.timeout(1500)])).catch(() => []);
    }
    const drainMs = Date.now() - tDrain;
    // Responder: the "home" side.
    const responder = (async () => {
      while (!ctl.signal.aborted) {
        const calls = await psPull(p, 'co-lab-calls-sub', ctl.signal).catch(() => []);
        for (const c of calls) {
          const seenAt = Date.now();
          const res: ResultFile = { id: c.id, seenAt, writtenAt: Date.now(), side: 'pubsub-echo' };
          await ps('POST', `${p}/topics/co-lab-results:publish`, { messages: [{ data: b64(res) }] });
        }
      }
    })();
    // Result listener: the "remote app" side.
    const listener = (async () => {
      while (!ctl.signal.aborted) {
        const res = await psPull(p, 'co-lab-results-sub', ctl.signal).catch(() => []);
        const t = Date.now();
        for (const r of res) waiting.get(r.id)?.({ ...r, receivedAt: t } as ResultFile);
      }
    })();
    const runTag = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
    for (let i = 1; i <= n && !stopFlag; i++) {
      const id = `ps-${runTag}-${String(i).padStart(3, '0')}`;
      status(`Pub/Sub echo: iteration ${i}/${n}`);
      const got = new Promise<ResultFile & { receivedAt: number }>((resolve) => waiting.set(id, resolve as never));
      const t0 = Date.now();
      await ps('POST', `${p}/topics/co-lab-calls:publish`, { messages: [{ data: b64({ id, t0 }) }] });
      const createMs = Date.now() - t0;
      const res = await Promise.race([got, sleep(60000).then(() => null)]);
      if (!res) {
        status(`Timeout on iteration ${i}`);
        break;
      }
      const row: Row = {
        seq: i,
        mode: 'pubsub',
        createMs,
        rttMs: res.receivedAt - t0,
        upMs: res.seenAt - t0,
        homeMs: res.writtenAt - res.seenAt,
        downMs: res.receivedAt - res.writtenAt,
        polls: 0,
      };
      rows.push(row);
      addRow(row);
      await sleep(300);
    }
    ctl.abort();
    await Promise.allSettled([responder, listener]);
    const summary = {
      mode: 'pubsub',
      setupMs,
      drainMs,
      when: new Date().toISOString(),
      userAgent: navigator.userAgent,
      rtt: stats(rows.map((r) => r.rttMs)),
      create: stats(rows.map((r) => r.createMs)),
      up: stats(rows.map((r) => r.upMs)),
      down: stats(rows.map((r) => r.downMs)),
      rows,
    };
    $('summary').textContent = JSON.stringify({ ...summary, rows: undefined }, null, 2);
    if (rows.length) {
      const d = await ensureDirs();
      await createFile(d.runs, `run-pubsub-${runTag}.json`, JSON.stringify(summary, null, 2), 'application/json');
      status(`Done: ${rows.length} iterations, median RTT ${summary.rtt.median} ms. Saved to AI-Workbench/lab/latency/runs.`);
    }
  } catch (e) {
    status('Error: ' + (e as Error).message);
  } finally {
    ctl.abort();
    for (const b of ['runG', 'runApi']) $<HTMLButtonElement>(b).disabled = false;
    $<HTMLButtonElement>('runPs').disabled = !psToken;
    $<HTMLButtonElement>('stop').disabled = true;
  }
}

$('runPs').onclick = () => void runPubSub();
$('runG').onclick = () => void run('g');
$('runApi').onclick = () => void run('api');
$('stop').onclick = () => (stopFlag = true);
