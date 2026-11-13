// SPDX-License-Identifier: AGPL-3.0-or-later
// Database reads (CO Web 0.15.0, step 3 of the Drive → MariaDB migration). /agent/db.php serves the projects, chats
// and events that cron/drive-sync.php copied from Drive (lag ≤ 1 min). null = fall back to Drive.
import { parseRelated, type DossierRow, type Related } from './secretaryHints';
import type { ModuleInfo } from './extBridge';
import { gateway, setGateways } from './gateway';
import { getToken } from './auth';
import type { BrowserSettings, EnvTemplate } from './types';

const ENDPOINT = '/agent/db.php';
const TIMEOUT_MS = 8000;
const MAX_FAILURES = 3;
const COOLDOWN_MS = 5 * 60 * 1000;

// Module-level circuit breaker state.
let failures = 0;
let disabledUntil = 0;

type Row = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

// True when DB reads are not in cooldown.
export function dbEnabled(): boolean {
  return Date.now() >= disabledUntil;
}

// Record a failure; trip the breaker after MAX_FAILURES consecutive ones.
function fail(): null {
  failures += 1;
  if (failures >= MAX_FAILURES) {
    disabledUntil = Date.now() + COOLDOWN_MS;
    failures = 0;
  }
  return null;
}

// POST {op, ...params} and return data[key] as an array, or null on any failure.
async function call(op: string, params: Record<string, unknown>, key: string): Promise<Row[] | null> {
  if (!dbEnabled()) return null;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const token = getToken();
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    if (token) headers.Authorization = `Bearer ${token}`;
    const res = await fetch(ENDPOINT, {
      method: 'POST',
      headers,
      body: JSON.stringify({ op, ...params }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal: ctrl.signal,
    });
    if (!res.ok) return fail();
    let data: unknown;
    try {
      data = await res.json();
    } catch {
      return fail();
    }
    const rows = data && typeof data === 'object' ? (data as Record<string, unknown>)[key] : undefined;
    if (!Array.isArray(rows)) return fail();
    failures = 0;
    return rows as Row[];
  } catch {
    return fail();
  } finally {
    clearTimeout(timer);
  }
}

// Projects of an environment.
export function dbProjects(env: string): Promise<Row[] | null> {
  return call('projects', { env }, 'projects');
}

// Chats of a project (max 500).
// 0.48.0: optional env (admin preview of another environment; ignored server-side for non-admins)
export function dbChats(projectId: string, env?: string): Promise<Row[] | null> {
  return call('chats', env ? { projectId, limit: 500, env } : { projectId, limit: 500 }, 'chats');
}

export interface DbChatHit {
  chatId: string;
  projectId: string;
  title: string;
  updatedUtc: string | null;
  score: number;
  matches: string[];
}

// Every chat of the env with its full-text matches for the terms (AI search, CO Web 0.16.0); null on failure.
// 0.88.0: the user's own query too - the server merges messages, protocol entries and the lists of search providers.
export async function dbSearchChats(env: string, terms: string[], limit = 300, query = ''): Promise<DbChatHit[] | null> {
  const rows = await call('search_chats', { env, terms, limit, ...(query ? { query: query.slice(0, 300) } : {}) }, 'chats');
  if (!rows) return null;
  return rows.map((r) => ({
    chatId: String(r.chatId),
    projectId: String(r.projectId),
    title: String(r.title ?? ''),
    updatedUtc: typeof r.updatedUtc === 'string' ? r.updatedUtc : null,
    score: Number(r.score) || 0,
    matches: Array.isArray(r.matches) ? r.matches.map(String) : [],
  }));
}

// Freedom F10: results of the search providers of enabled modules that are not chats (ext/1 search.provider hook
// 'items', db.php search_items), e.g. project documents of a document index; none without such a module. null on failure.
export interface DbSearchItem {
  provider: string;
  kind: string; // 'doc': path is a project file
  projectId: string;
  projectTitle: string;
  path: string; // kind 'doc': relative to AI-Workbench/projects: <projectId>/files/...
  title: string;
  modified: string;
  snippets: string[];
}

export async function dbSearchItems(env: string, q: string, limit = 8): Promise<DbSearchItem[] | null> {
  const rows = await call('search_items', { env, q: q.slice(0, 300), limit }, 'items');
  if (!rows) return null;
  return rows.map((r) => ({
    provider: String(r.provider ?? ''),
    kind: String(r.kind ?? 'item'),
    projectId: String(r.projectId),
    projectTitle: String(r.projectTitle ?? ''),
    path: String(r.path ?? ''),
    title: String(r.title ?? ''),
    modified: String(r.modified ?? ''),
    snippets: Array.isArray(r.snippets) ? r.snippets.map(String) : [],
  }));
}

// Events of a chat.
export function dbEvents(chatId: string): Promise<Row[] | null> {
  return call('events', { chatId }, 'events');
}

// ---------- 0.18.0: database-first reads and write-through ----------

const READ_MODE_KEY = 'co.dbReads';

// 'primary' (default): chats and messages come from the database only; 'hybrid': Drive listing + database content.
export function dbReadsPrimary(): boolean {
  try {
    return localStorage.getItem(READ_MODE_KEY) !== 'hybrid';
  } catch {
    return true;
  }
}

export function setDbReadsPrimary(on: boolean): void {
  try {
    localStorage.setItem(READ_MODE_KEY, on ? 'primary' : 'hybrid');
  } catch {
    /* per-browser preference only */
  }
}

// gzip + base64: the hosting firewall answers 403 to plain bodies whose chat text contains code or SQL.
async function gzB64(text: string): Promise<string | null> {
  if (typeof CompressionStream === 'undefined') return null;
  const stream = new Blob([text]).stream().pipeThrough(new CompressionStream('gzip'));
  const buf = new Uint8Array(await new Response(stream).arrayBuffer());
  let bin = '';
  for (let i = 0; i < buf.length; i += 0x8000) bin += String.fromCharCode(...buf.subarray(i, i + 0x8000));
  return btoa(bin);
}

async function postGz(body: Record<string, unknown>): Promise<boolean> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const gz = await gzB64(JSON.stringify(body));
    if (!gz) return false;
    const token = getToken();
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    if (token) headers.Authorization = `Bearer ${token}`;
    const res = await fetch(ENDPOINT, {
      method: 'POST', headers, body: JSON.stringify({ gz }), credentials: 'same-origin', cache: 'no-store', signal: ctrl.signal,
    });
    return res.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timer);
  }
}

// Writes projects/chats/events to the database (0.21.0: before their Drive copy). Never throws; true only when every
// record was stored, false = the caller writes Drive first and the sync catches up.
export async function dbPut(records: { projects?: unknown[]; chats?: unknown[]; events?: unknown[] }): Promise<boolean> {
  const res = await post({ op: 'put_user', ...records }, true);
  if (!res?.ok) return false;
  try {
    const d = await res.json();
    return Array.isArray(d?.rejected) && d.rejected.length === 0;
  } catch {
    return false;
  }
}

// Removes a trashed chat at once (the sync would remove it within a minute).
export function dbDeleteChat(chatId: string): Promise<boolean> {
  return postGz({ op: 'delete_chat', chatId });
}

// ---------- 0.19.0: job queue in the database (Bridge 1.3.0 "db-queue") ----------

const QUEUE_KEY = 'co.dbQueue';

// Default on; 'off' sends every job through the Drive inbox again (Host tab → Data).
export function dbQueueOn(): boolean {
  try {
    return localStorage.getItem(QUEUE_KEY) !== 'off';
  } catch {
    return true;
  }
}

export function setDbQueueOn(on: boolean): void {
  try {
    localStorage.setItem(QUEUE_KEY, on ? 'on' : 'off');
  } catch {
    /* per-browser preference only */
  }
}

async function post(body: Record<string, unknown>, gz: boolean, timeoutMs = TIMEOUT_MS): Promise<Response | null> {
  if (!dbEnabled()) return null;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    let payload = JSON.stringify(body);
    if (gz) {
      const z = await gzB64(payload);
      if (!z) return null;
      payload = JSON.stringify({ gz: z });
    }
    const token = getToken();
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    if (token) headers.Authorization = `Bearer ${token}`;
    return await fetch(ENDPOINT, { method: 'POST', headers, body: payload, credentials: 'same-origin', cache: 'no-store', signal: ctrl.signal });
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

// Queues a job; 'queued' also when the jobId is already there (an earlier attempt whose answer was lost).
// 0.45.0: { refused } when the server rejects the job (400/403); the message is user-facing.
export async function dbJobSubmit(
  request: Record<string, unknown>,
): Promise<'queued' | 'failed' | { refused: string }> {
  const res = await post({ op: 'job_submit', request }, true);
  if (!res) return 'failed';
  if (res.ok || res.status === 409) return 'queued';
  if (res.status === 403 || res.status === 400) {
    const j = (await res.json().catch(() => ({}))) as { error?: string; detail?: string };
    let msg: string;
    if (/^shared_chat_no_/.test(String(j.error || ''))) {
      msg = j.detail || 'A shared chat cannot use devices or context folders.';
    } else if (j.error === 'forbidden') {
      msg = 'You cannot send messages in this chat.';
    } else {
      msg = `Database: ${j.error || res.status}${j.detail ? ` (${j.detail})` : ''}`;
    }
    return { refused: msg };
  }
  return 'failed';
}

export interface DbJob {
  state: string | null;
  status: string | null;
  origin: string | null;
  result: Record<string, any> | null; // eslint-disable-line @typescript-eslint/no-explicit-any
  rejection: string | null;
  cancelRequested: boolean;
  answered?: boolean; // 0.20.0: the server has written the assistant_message
}

// undefined = database not reachable (read Drive), null = no such job in the database.
export async function dbJobGet(jobId: string): Promise<DbJob | null | undefined> {
  const res = await post({ op: 'job_get', jobId }, false);
  if (!res) return undefined;
  if (!res.ok) {
    fail();
    return undefined;
  }
  try {
    const d = await res.json();
    failures = 0;
    return d && typeof d === 'object' && 'job' in d ? (d.job as DbJob | null) : undefined;
  } catch {
    return undefined;
  }
}

// Bridge event batches with lastSequence > afterSeq; null = not reachable.
export async function dbJobEvents(jobId: string, afterSeq: number): Promise<{ firstSequence: number; lastSequence: number; events: Row[] }[] | null> {
  const rows = await call('job_events', { jobId, afterSeq }, 'batches');
  return rows as { firstSequence: number; lastSequence: number; events: Row[] }[] | null;
}

// A queued job is withdrawn; a running one is stopped by Bridge at its next claim poll.
export async function dbJobCancel(jobId: string): Promise<void> {
  await post({ op: 'job_cancel', jobId }, false);
}

// ---------- 0.23.0: usage per project and agent (Host tab) ----------

export interface UsageRow {
  env: string | null;
  projectId: string | null;
  title: string | null;
  agent: string | null;
  jobs: number;
  failed: number;
  inputTokens: number;
  cachedInputTokens: number;
  outputTokens: number;
  costUsd: number | null; // Claude Code's API-equivalent cost; Codex reports none
  apiUsd?: number | null; // 0.74.0 (schema 15): reported cost, else tokens x the server's API price table (Codex)
  toolCalls?: number;
  toolErrors?: number;
  durationMs?: number;
  chatId?: string; // group 'chat'
  chatTitle?: string | null;
}

// 0.74.0: group 'chat' = the chats of one project.
export async function dbUsage(days: number, group?: { chatsOf: string }): Promise<UsageRow[] | null> {
  return (await call('usage', group ? { days, group: 'chat', projectId: group.chatsOf } : { days }, 'rows')) as UsageRow[] | null;
}

// ---------- 0.74.0 (schema 15): job timeline and cost (jobstats-lib.php) ----------

// [offsetMs from claim, durMs|null (unfinished), kind, label, err 0|1, depth (1 = subagent), output tokens so far|null]
export type JobStep = [number, number | null, string, string, number, number, number | null];

export interface JobStats {
  jobId: string;
  chatId: string | null;
  title?: string | null;
  agent: string | null;
  model: string | null;
  hostId: string | null;
  outcome: 'completed' | 'failed' | 'cancelled';
  queuedUtc: string | null;
  startedUtc: string | null;
  completedUtc: string | null;
  durationMs: number | null;
  inputTokens: number | null;
  cachedTokens: number | null;
  cacheWriteTokens: number | null;
  outputTokens: number | null;
  reasoningTokens: number | null;
  costUsd: number | null;
  apiUsd: number | null;
  turns: number | null;
  toolCalls: number;
  toolErrors: number;
  subagents: number;
  hasTrace: boolean;
  tools?: Record<string, [number, number, number]> | null; // name -> [calls, errors, ms]
  steps?: JobStep[] | null;
  children?: JobStats[]; // subtask chats (aiwb-subtask) started while the job ran
}

export async function dbJobStats(jobId: string): Promise<JobStats | null> {
  return ((await rawCall('job_stats', { jobId })).stats as JobStats | null) ?? null;
}

// ---------- 0.28.0: environment profile (kv scope env/<env>, name profile) ----------

// One op; throws on failure (unlike call(), which degrades to null for the Drive fallback).
async function rawCall(op: string, params: Record<string, unknown>, gz = false): Promise<Record<string, any>> {
  const res = await post({ op, ...params }, gz);
  if (!res) throw new Error('Database is not reachable.');
  const j = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(`Database: ${j.error || res.status}${j.detail ? ` (${j.detail})` : ''}`);
  return j;
}

export interface EnvProfile {
  text: string;
  updatedUtc: string | null;
  updatedBy: string | null;
  rev: string; // 0.75.0: sha for profile_put ('' = no profile yet); accepted correction rules also write the profile
}

export async function dbGetProfile(env: string): Promise<EnvProfile> {
  const p = (await rawCall('profile_get', { env })).profile || {};
  return {
    text: typeof p.text === 'string' ? p.text : '',
    updatedUtc: typeof p.updatedUtc === 'string' ? p.updatedUtc : null,
    updatedBy: typeof p.updatedBy === 'string' ? p.updatedBy : null,
    rev: typeof p.rev === 'string' ? p.rev : '',
  };
}

// rev: the server refuses (409 conflict) when the profile changed since it was loaded
export async function dbPutProfile(env: string, text: string, rev: string): Promise<void> {
  await rawCall('profile_put', { env, text, rev }, true); // gz: the hosting WAF blocks code-like plain bodies
}

// ---------- 0.85.0 (schema 19): secretary hints and the dossier (secretaryHints.ts) ----------

// gz: the message text may look like code to the hosting WAF
export async function dbRelated(env: string, projectId: string, chatId: string, text: string): Promise<Related> {
  return parseRelated(await rawCall('related', { env, projectId, chatId, text: text.slice(0, 1500) }, true));
}

export async function dbDossierContext(projectId: string, chatId: string): Promise<string> {
  const j = await rawCall('dossier_context', { projectId, chatId });
  return typeof j.text === 'string' ? j.text : '';
}

export async function dbDossierGet(env: string, opts: { all?: boolean; q?: string } = {}): Promise<DossierRow[]> {
  const j = await rawCall('dossier_get', { env, ...(opts.all ? { all: true } : {}), ...(opts.q ? { q: opts.q } : {}) });
  return Array.isArray(j.rows) ? (j.rows as DossierRow[]) : [];
}

export async function dbDossierPut(
  env: string,
  action: 'add' | 'edit' | 'close' | 'delete' | 'restore',
  row: { id?: number; section?: string; subject?: string; text?: string; rev?: string },
): Promise<Record<string, unknown>> {
  return rawCall('dossier_put', { env, action, ...row }, true);
}

// ---------- 0.37.0: environment templates (kv scope env/<env>, name templates) ----------

// rev = sha of the stored set; a put with an outdated rev gets the current set back (another computer saved first).
export interface TemplateSet { list: EnvTemplate[]; rev: string }

function parseTemplateSet(j: { templates?: unknown; rev?: unknown }): TemplateSet {
  const list = Array.isArray(j.templates)
    ? j.templates.filter((t): t is EnvTemplate => !!t && typeof t === 'object' && typeof (t as { name?: unknown }).name === 'string')
    : [];
  return { list, rev: typeof j.rev === 'string' ? j.rev : '' };
}

export async function dbGetTemplates(env: string): Promise<TemplateSet> {
  return parseTemplateSet(await rawCall('templates_get', { env }));
}

export async function dbPutTemplates(
  env: string, templates: EnvTemplate[], rev: string,
): Promise<{ ok: true; rev: string } | { ok: false; current: TemplateSet }> {
  const res = await post({ op: 'templates_put', env, templates, rev }, true);
  if (!res) throw new Error('Database is not reachable.');
  const j = await res.json().catch(() => ({}));
  if (res.status === 409 && j.error === 'templates_changed') return { ok: false, current: parseTemplateSet(j) };
  if (!res.ok) throw new Error(`Database: ${j.error || res.status}`);
  return { ok: true, rev: typeof j.rev === 'string' ? j.rev : '' };
}

// ---------- 0.31.0: activity of the env (running/queued chats, chats waiting for an answer) ----------

export interface ChatActivity {
  chatId: string;
  projectId: string;
  state: 'queued' | 'running';
  since: string | null;
}

// 0.52.0: chat whose job stopped because the gateway model was unavailable (Freedom F10: listed by the module's op
// gateway().ops.waiting, see dbGatewayWaiting)
export interface GatewayWaiting {
  chatId: string;
  projectId: string;
  title: string;
  eventId: string;
  reason: string;
  since: string | null;
}

export async function dbActivity(
  env: string,
): Promise<{ running: ChatActivity[]; asking: string[] } | null> {
  try {
    const j = await rawCall('activity', { env });
    const running: ChatActivity[] = Array.isArray(j?.running)
      ? j.running.map((r: Record<string, unknown>) => ({
          chatId: String(r?.chatId ?? ''),
          projectId: String(r?.projectId ?? ''),
          state: r?.state === 'running' ? 'running' : 'queued',
          since: typeof r?.since === 'string' ? r.since : null,
        }))
      : [];
    const asking: string[] = Array.isArray(j?.asking) ? j.asking.map((id: unknown) => String(id)) : [];
    return { running, asking };
  } catch {
    return null;
  }
}

/** 0.52.0 / F10: chats stopped while the gateway model was unavailable - the module's user op ops.waiting; [] without
 * a gateway, while it is paused or when the module does not serve the op. */
export async function dbGatewayWaiting(env: string): Promise<GatewayWaiting[]> {
  const g = gateway();
  const op = g.ops.waiting;
  if (!op || g.paused) return [];
  try {
    const j = await dbModuleCall(op, { env });
    const waiting = j.waiting ?? j.chats;
    return Array.isArray(waiting)
      ? waiting
          .map((w: Record<string, unknown>) => ({
            chatId: String(w?.chatId ?? ''),
            projectId: String(w?.projectId ?? ''),
            title: String(w?.title ?? ''),
            eventId: String(w?.eventId ?? ''),
            reason: String(w?.reason ?? ''),
            since: typeof w?.since === 'string' ? w.since : null,
          }))
          .filter((w: GatewayWaiting) => w.chatId !== '' && w.eventId !== '')
      : [];
  } catch {
    return [];
  }
}

/** Resume chats the gateway blocked (the module's user op ops.resume; 1..20 chats). null = the module has no such op. */
export async function dbGatewayResume(
  chatIds: string[], deviceId?: string,
): Promise<{ chatId: string; jobId: string | null; error: string | null }[] | null> {
  const g = gateway();
  const op = g.ops.resume;
  if (!op || g.paused) return null;
  const j = await dbModuleCall(op, deviceId ? { chatIds, deviceId } : { chatIds });
  const rows = Array.isArray(j.results) ? (j.results as Record<string, unknown>[]) : [];
  return rows.map((r) => ({ chatId: String(r?.chatId ?? ''), jobId: typeof r?.jobId === 'string' ? r.jobId : null, error: typeof r?.error === 'string' ? r.error : null }));
}

// ---------- 0.32.0: chat protocol kept by the server's secretary (schema 8) ----------

export type ProtocolSection = 'decision' | 'constraint' | 'fact' | 'question' | 'artifact' | 'answer';
export type ProtocolStatus = 'active' | 'superseded' | 'closed';

export interface ProtocolEntry {
  no: number;
  section: ProtocolSection;
  text: string;
  quote: string | null;
  msg: number;
  status: ProtocolStatus;
  replacedBy: number | null;
  closedSeq: number | null;
  author: string;
  model: string | null;
  createdUtc: string;
}

export interface ProtocolState {
  seq: number | null;
  utc: string | null;
  error: string | null;
  model: string | null;
}

export interface ChatProtocol {
  entries: ProtocolEntry[];
  state: ProtocolState;
}

export async function dbProtocol(chatId: string): Promise<ChatProtocol | null> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    const token = getToken();
    if (token) headers.Authorization = `Bearer ${token}`;
    const r = await fetch(ENDPOINT, {
      method: 'POST',
      headers,
      body: JSON.stringify({ op: 'protocol', chatId }),
      credentials: 'same-origin',
      cache: 'no-store',
      signal: ctrl.signal,
    });
    if (!r.ok) return null;
    const j = await r.json();
    if (!j || !Array.isArray(j.entries) || !j.state || typeof j.state !== 'object') return null;
    const s = j.state;
    return {
      entries: j.entries as ProtocolEntry[],
      state: {
        seq: typeof s.seq === 'number' ? s.seq : null,
        utc: typeof s.utc === 'string' ? s.utc : null,
        error: typeof s.error === 'string' && s.error ? s.error : null,
        model: typeof s.model === 'string' && s.model ? s.model : null,
      },
    };
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

// ---------- 0.38.0: project memory candidates proposed by the protocol secretary (schema 9) ----------

export interface MemoryCandidate {
  id: number;
  chatId: string;
  chatTitle: string | null;
  msg: number;
  // 0.75.0 (schema 16): a rule's tag is its target - env (Reflexes, the environment profile) or project (memory.md)
  tag: 'decision' | 'fact' | 'todo' | 'env' | 'project';
  text: string;
  day: string;
  model: string | null;
  createdUtc: string;
  // 0.73.0 (schema 14): 'add' from the secretary; 'revise' (the lines in `replaces` become `text`) and 'drop' (they are
  // removed) from the nightly consolidation. Removed lines go to context/memory-archive.md.
  // 0.75.0 (schema 16): 'rule' from the secretary - a person's correction of the agent (reason = the verbatim correction)
  kind: 'add' | 'revise' | 'drop' | 'rule';
  replaces: string[];
  reason: string | null;
  targetEnv: string | null;
}

const MEMORY_TAGS = ['decision', 'fact', 'todo', 'env', 'project'] as const;
const memoryTag = (t: unknown): MemoryCandidate['tag'] => (MEMORY_TAGS.includes(t as MemoryCandidate['tag']) ? (t as MemoryCandidate['tag']) : 'fact');

export async function dbMemoryCandidates(projectId: string): Promise<{ candidates: MemoryCandidate[]; accepted: number; rejected: number }> {
  const j = await rawCall('memory_candidates', { projectId });
  const list: any[] = Array.isArray(j.candidates) ? j.candidates : [];
  const candidates = list
    .filter((c) => c && typeof c.id === 'number' && typeof c.text === 'string')
    .map((c): MemoryCandidate => ({
      id: c.id,
      chatId: String(c.chatId || ''),
      chatTitle: typeof c.chatTitle === 'string' ? c.chatTitle : null,
      msg: typeof c.msg === 'number' ? c.msg : 0,
      tag: memoryTag(c.tag),
      text: c.text,
      day: String(c.day || ''),
      model: typeof c.model === 'string' ? c.model : null,
      createdUtc: String(c.createdUtc || ''),
      kind: c.kind === 'revise' || c.kind === 'drop' || c.kind === 'rule' ? c.kind : 'add',
      replaces: Array.isArray(c.replaces) ? c.replaces.filter((x: unknown) => typeof x === 'string') : [],
      reason: typeof c.reason === 'string' ? c.reason : null,
      targetEnv: typeof c.targetEnv === 'string' ? c.targetEnv : null,
    }));
  const counts = j.counts || {};
  return { candidates, accepted: Number(counts.accepted) || 0, rejected: Number(counts.rejected) || 0 };
}

// Accept writes the line to memory.md on the server (Drive, serialized per project), then marks the candidate.
// 0.73.0: a revise/drop whose lines left memory.md fails with memory_changed (HTTP 409).
export async function dbMemoryDecide(id: number, action: 'accept' | 'reject', edit?: { tag: string; text: string }): Promise<void> {
  await rawCall('memory_decide', { id, action, ...(edit || {}) }, true);
}

// ---------- 0.39.0: job queue statistics (Host tab → Jobs, owner only) ----------

export interface OpsCounts {
  jobs: number;
  completed: number;
  failed: number;
  cancelled: number;
  running: number;
  queued: number;
  other: number;
}

// Duration percentiles in seconds (null when no samples)
export interface OpsDur {
  waitP50: number | null;
  waitP95: number | null;
  runP50: number | null;
  runP95: number | null;
  totalP50: number | null;
  totalP95: number | null;
}

export interface OpsReport {
  days: number;
  generatedUtc: string;
  totals: OpsCounts;
  durations: OpsDur & { waitN: number; runN: number };
  groups: (OpsCounts & OpsDur & { hostId: string; agent: string })[];
  retries: { retried: number; fallback: number; stuckAlerts: number; rejected: number; timeouts: number };
  errors: { key: string; count: number; example: string; lastUtc: string | null; agents: string[]; hosts: string[] }[];
  daily: { day: string; jobs: number; completed: number; failed: number; cancelled: number }[];
}

// Ops report for the last N days; 'forbidden' for non-owners, null on failure.
export async function dbOps(days: number): Promise<OpsReport | 'forbidden' | null> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    const token = getToken();
    if (token) headers.Authorization = `Bearer ${token}`;
    const res = await fetch(ENDPOINT, {
      method: 'POST',
      headers,
      credentials: 'same-origin',
      cache: 'no-store',
      body: JSON.stringify({ op: 'ops', days }),
      signal: ctrl.signal,
    });
    if (res.status === 403) return 'forbidden';
    if (!res.ok) return null;
    const j: unknown = await res.json();
    const ops = (j as { ops?: unknown } | null)?.ops;
    if (ops && typeof ops === 'object' && Array.isArray((ops as { groups?: unknown }).groups)) {
      return ops as OpsReport;
    }
    return null;
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

// ---------- 0.77.0 (schema 17): causes of failed jobs and Bridge supervisor events (Host tab, Health) ----------

export interface HealthFailure {
  kind: string;
  n24: number;
  n7: number;
  lastUtc: string | null;
  lastJobId: string | null;
  lastEnv: string | null;
  lastProjectId: string | null;
  lastChatId: string | null;
  lastHost: string | null;
  lastAgent: string | null;
  lastDetail: string | null;
}

export interface HealthReport {
  generatedUtc: string;
  failures: HealthFailure[];
  events: { utc: string | null; host: string; kind: string; detail: string | null }[];
}

// Health report; 'forbidden' for non-owners, null on failure (also before the server has schema 17).
export async function dbHealth(): Promise<HealthReport | 'forbidden' | null> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    const token = getToken();
    if (token) headers.Authorization = `Bearer ${token}`;
    const res = await fetch(ENDPOINT, {
      method: 'POST',
      headers,
      credentials: 'same-origin',
      cache: 'no-store',
      body: JSON.stringify({ op: 'health' }),
      signal: ctrl.signal,
    });
    if (res.status === 403) return 'forbidden';
    if (!res.ok) return null;
    const h = ((await res.json()) as { health?: HealthReport } | null)?.health;
    return h && Array.isArray(h.failures) && Array.isArray(h.events) ? h : null;
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

// S2 (CO Web 0.98.0): diagnosis behind the header traffic light (db.php op host_light); null on failure or old server.
export interface HostLightQueue { queued: number; running: number; lastDoneUtc: string | null }
export interface HostLightReport {
  hosts: Record<string, HostLightQueue>;
  relay: { seenUtc: string | null; devices: number };
}

export async function dbHostLight(env: string): Promise<HostLightReport | null> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    const token = getToken();
    if (token) headers.Authorization = `Bearer ${token}`;
    const res = await fetch(ENDPOINT, {
      method: 'POST',
      headers,
      credentials: 'same-origin',
      cache: 'no-store',
      body: JSON.stringify({ op: 'host_light', env }),
      signal: ctrl.signal,
    });
    if (!res.ok) return null;
    const r = (await res.json()) as HostLightReport | null;
    return r && r.hosts && typeof r.hosts === 'object' && r.relay ? r : null;
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

// ---------- 0.45.0: sharing of projects and chats between environments ----------

export type ShareRole = 'editor' | 'viewer';

export interface ShareRow {
  env: string;
  role: ShareRole;
  addedBy: string | null;
  createdUtc: string | null;
}

export interface SharesInfo {
  shares: ShareRow[];
  role: 'owner' | ShareRole;
  ownerEnv: string;
  envs: string[];
  projectShares: ShareRow[];
}

export async function dbSharesGet(kind: 'project' | 'chat', id: string): Promise<SharesInfo> {
  const j = await rawCall('shares_get', { kind, id });
  return {
    shares: Array.isArray(j.shares) ? (j.shares as ShareRow[]) : [],
    role: j.role === 'owner' || j.role === 'editor' || j.role === 'viewer' ? j.role : 'viewer',
    ownerEnv: typeof j.ownerEnv === 'string' ? j.ownerEnv : '',
    envs: Array.isArray(j.envs) ? (j.envs as string[]) : [],
    projectShares: Array.isArray(j.projectShares) ? (j.projectShares as ShareRow[]) : [],
  };
}

export async function dbShareSet(
  kind: 'project' | 'chat',
  id: string,
  env: string,
  role: ShareRole | null,
): Promise<ShareRow[]> {
  const j = await rawCall('share_set', { kind, id, env, role });
  return Array.isArray(j.shares) ? (j.shares as ShareRow[]) : [];
}

// Projects shared with env (whole project or some of its chats); [] on any error
export async function dbSharedProjects(env: string): Promise<Row[]> {
  try {
    const j = await rawCall('projects', { env });
    return Array.isArray(j.shared) ? (j.shared as Row[]) : [];
  } catch {
    return [];
  }
}

// ---------- 0.49.0: web device (lab/web-device/SPEC.md §4) ----------
export class DbOpError extends Error {
  status: number;
  code: string;
  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = 'DbOpError';
    this.status = status;
    this.code = code;
  }
}

// gz for ops whose bodies carry file content or tool schemas (hosting firewall); db.php accepts gz only for those
const WEBDEV_GZ = new Set(['webdev_register', 'webdev_heartbeat', 'webdev_result']);

async function webdevCall(op: string, params: Record<string, unknown>, timeoutMs?: number): Promise<Record<string, any>> { // eslint-disable-line @typescript-eslint/no-explicit-any
  const res = await post({ op, ...params }, WEBDEV_GZ.has(op), timeoutMs);
  if (!res) throw new DbOpError(0, 'unreachable', 'Database is not reachable.');
  const j = await res.json().catch(() => ({}));
  if (!res.ok) throw new DbOpError(res.status, j.error || 'http_' + res.status, `Database: ${j.error || res.status}${j.detail ? ` (${j.detail})` : ''}`);
  return j;
}

export interface WebDeviceRow {
  deviceId: string;
  short: string;
  name: string;
  folders: { id: string; name: string; access: 'read' | 'write' }[];
  online: boolean;
  lastSeenUtc: string | null;
  browser: boolean; // 0.62.0: browser extension panel (browser_* tools)
}

export async function dbWebdevRegister(body: { deviceId: string; name: string; folders: unknown[]; tools: unknown[] }): Promise<void> {
  await webdevCall('webdev_register', body);
}

export async function dbWebdevHeartbeat(deviceId: string, folders: unknown[]): Promise<void> {
  await webdevCall('webdev_heartbeat', { deviceId, folders });
}

export async function dbWebdevClaim(deviceId: string, waitMs: number): Promise<Record<string, unknown>[]> {
  const j = await webdevCall('webdev_claim', { deviceId, waitMs }, waitMs + 15000); // long-poll: server wait + margin
  return Array.isArray(j.calls) ? j.calls : [];
}

export async function dbWebdevResult(callId: string, result: unknown): Promise<'ok' | 'expired'> {
  const j = await webdevCall('webdev_result', { callId, result });
  return j.ok === false && j.error === 'expired' ? 'expired' : 'ok';
}

export async function dbWebdevRevoke(deviceId: string): Promise<void> {
  await webdevCall('webdev_revoke', { deviceId });
}

export async function dbWebdevMine(): Promise<WebDeviceRow[]> {
  const j = await webdevCall('webdev_mine', {});
  const rows: any[] = Array.isArray(j.devices) ? j.devices : []; // eslint-disable-line @typescript-eslint/no-explicit-any
  return rows
    .filter((r) => r && typeof r === 'object' && typeof r.deviceId === 'string')
    .map((r): WebDeviceRow => ({
      deviceId: r.deviceId,
      short: String(r.short ?? r.shortName ?? ''),
      name: typeof r.name === 'string' ? r.name : '',
      folders: (Array.isArray(r.folders) ? r.folders : [])
        .filter((f: any) => f && typeof f === 'object' && typeof f.id === 'string') // eslint-disable-line @typescript-eslint/no-explicit-any
        .map((f: any) => ({ id: f.id, name: typeof f.name === 'string' ? f.name : f.id, access: f.access === 'write' ? 'write' : 'read' })), // eslint-disable-line @typescript-eslint/no-explicit-any
      online: r.online === true,
      lastSeenUtc: typeof r.lastSeenUtc === 'string' ? r.lastSeenUtc : null,
      browser: Array.isArray(r.tools) && r.tools.some((t: any) => t && t.name === 'browser_snapshot'), // eslint-disable-line @typescript-eslint/no-explicit-any
    }));
}

// ---------- 0.55.0: chat files (schema 12, files-lib.php) ----------

export interface FileRow {
  id: number;
  path: string;
  name: string;
  kind: 'doc' | 'temp';
  kindByUser: boolean;
  state: 'live' | 'hidden' | 'merged' | 'gone';
  mergedInto: number | null;
  legacy: boolean;
  versions: number;
  head: { md5: string | null; sha256: string | null; size: number | null; utc: string | null; origin: string | null; seq: number | null };
  firstUtc: string | null;
  annotation: string | null;
  goneUtc: string | null;
  extra: {
    members?: { id: number; name: string; path: string; fileId: string | null; utc: string; legacy?: boolean; trashedUtc?: string }[];
    parent?: { id: number; name: string; md5: string | null; revisionId: string | null; utc: string };
    restored?: { md5: string | null; fromRevision: string | null; utc: string }[];
    fileId?: string;
  };
}

// null = the database is not reachable (the panel then shows the files from the messages only).
export async function dbFiles(chatId: string): Promise<FileRow[] | null> {
  const res = await post({ op: 'files_list', chatId }, true);
  if (!res || !res.ok) return null;
  const j = (await res.json().catch(() => null)) as { files?: FileRow[] } | null;
  return Array.isArray(j?.files) ? j!.files : null;
}

export async function dbFilesOp(op: 'files_set' | 'files_merge' | 'files_branch' | 'files_restored' | 'files_undone', params: Record<string, unknown>): Promise<FileRow[]> {
  const res = await post({ op, ...params }, true);
  if (!res) throw new Error('Database not reachable.');
  const j = (await res.json().catch(() => ({}))) as { files?: FileRow[]; error?: string };
  if (!res.ok || !Array.isArray(j.files)) throw new Error(j.error === 'forbidden' ? 'You cannot change files of this chat.' : `Database: ${j.error || res.status}`);
  return j.files;
}

// ---------- 0.60.0 (schema 13): follow-ups — messages sent while a job of the chat runs ----------

export interface Followup {
  id: number;
  jobId: string;
  author: string | null;
  text: string;
  // pending/offered: on the way to the running agent; late: waits for the end of the job (sent as the next message)
  state: 'pending' | 'offered' | 'late';
  jobOver: boolean;
  createdUtc: string;
}

// deliver = the running agent reads follow-ups (Claude on a Bridge with "followups"); otherwise it waits for the answer.
// 'job_done' = the job finished meanwhile (send a normal message instead); 'no_job' = the server has no row of the job
// (a Drive job not synced yet, or no connection): the caller keeps the text on this device until the job ends.
export async function dbFollowupAdd(jobId: string, text: string, deliver: boolean): Promise<{ id: number; state: string } | 'job_done' | 'no_job'> {
  const res = await post({ op: 'followup_add', jobId, text, deliver }, true);
  if (!res || res.status === 404) return 'no_job';
  if (res.status === 409) return 'job_done';
  const j = (await res.json().catch(() => ({}))) as { id?: number; state?: string; error?: string };
  if (!res.ok || typeof j.id !== 'number') throw new Error(`Database: ${j.error || res.status}`);
  return { id: j.id, state: j.state || 'pending' };
}

// null = not reachable
export async function dbFollowupList(chatId: string): Promise<Followup[] | null> {
  const rows = await call('followup_list', { chatId }, 'followups');
  return rows as Followup[] | null;
}

// Late follow-ups of finished jobs, each given out once (to one device).
export async function dbFollowupTake(chatId: string, ids: number[]): Promise<number[]> {
  const res = await post({ op: 'followup_take', chatId, ids }, false);
  if (!res || !res.ok) return [];
  const j = (await res.json().catch(() => ({}))) as { taken?: number[] };
  return Array.isArray(j.taken) ? j.taken : [];
}

// The taken follow-ups were sent as a message.
// Tried 3 times: an unconfirmed row would be sent again after 2 min.
export async function dbFollowupDone(chatId: string, ids: number[]): Promise<void> {
  for (let i = 0; i < 3; i++) {
    const res = await post({ op: 'followup_done', chatId, ids }, false);
    if (res?.ok) return;
    await new Promise((r) => setTimeout(r, 2000 * (i + 1)));
  }
}

export async function dbFollowupCancel(chatId: string, id: number): Promise<boolean> {
  const res = await post({ op: 'followup_cancel', chatId, id }, false);
  if (!res || !res.ok) return false;
  const j = (await res.json().catch(() => ({}))) as { cancelled?: boolean };
  return j.cancelled === true;
}

// ---------- 0.71.0: "Waiting for you" (db.php inbox / inbox_mark, inbox-lib.php) ----------

export type InboxKind = 'ask' | 'captcha' | 'confirm' | 'action' | 'failed' | 'memory' | 'tools';

// 0.81.0 (schema 18): a risky action read-guard held (send, purchase, delete, publish, git push, ...) - the confirmation card
export interface InboxAction {
  requestId: number;
  kind: string; // send|purchase|delete|publish|push|secret|submit|other
  title: string;
  summary: string;
  command: string;
  actions: { kind: string; summary: string }[]; // all actions held under the same code
  tainted: boolean; // the chat read text with signs of prompt injection
  expiresUtc: string;
}

export interface InboxMemory {
  id: number;
  tag: MemoryCandidate['tag'];
  text: string;
  chatId: string;
  chatTitle: string;
  kind: 'add' | 'rule'; // 0.75.0 (schema 16): rule = a correction turned into a rule for agents
  msg: number;
  reason: string | null;
  targetEnv: string | null;
}

// ---------- 0.75.0 (schema 16): tool library learning (cron/tool-learn.php; db.php tool_candidates / tool_decide, owner only) ----------

export interface ToolEvidence {
  jobId: string;
  chatId: string;
  projectId: string;
  note?: string;
}

export interface ToolCandidate {
  id: number;
  kind: 'new' | 'skill' | 'archive';
  tool: string;
  title: string;
  text: string;
  evidence: ToolEvidence[];
  offeredUtc: string | null; // last Accept & run whose chat was not reported (the task may already be running)
}

function toolCandidate(c: any): ToolCandidate {
  return {
    id: c.id,
    kind: c.kind === 'new' || c.kind === 'archive' ? c.kind : 'skill',
    tool: str(c.tool),
    title: str(c.title),
    text: str(c.text),
    evidence: (Array.isArray(c.evidence) ? c.evidence : [])
      .filter((e: any) => e && typeof e.jobId === 'string')
      .map((e: any): ToolEvidence => ({ jobId: e.jobId, chatId: str(e.chatId), projectId: str(e.projectId), note: str(e.note) || undefined })),
    offeredUtc: typeof c.offeredUtc === 'string' ? c.offeredUtc : null,
  };
}

// Accept returns the agent task that CO Web sends in a new chat of `projectId`; the proposal stays pending until
// 'started' reports the created chat (a lost answer or a closed page loses nothing). The agent changes the library via toolbox.py.
export async function dbToolDecide(id: number, action: 'accept' | 'reject'): Promise<{ task: string; projectId: string } | null> {
  const j = await rawCall('tool_decide', { id, action }, true);
  return action === 'accept' && typeof j.task === 'string' ? { task: j.task, projectId: str(j.projectId) } : null;
}

// Retried (Codex review): a lost 'started' leaves the proposal pending with offeredUtc set, so Inbox warns before a rerun.
export async function dbToolStarted(id: number, chatId: string): Promise<void> {
  for (let i = 0; ; i++) {
    try {
      await rawCall('tool_decide', { id, action: 'started', chatId }, true);
      return;
    } catch (e) {
      if (i >= 5 || /already_decided|not_found|forbidden/.test(String((e as Error)?.message || e))) throw e;
      await new Promise((r) => setTimeout(r, 2000 * (i + 1)));
    }
  }
}

export interface InboxItem {
  id: string; // 'ev:<eventId>' or 'mem:<newest candidate id>'
  kind: InboxKind;
  projectId: string;
  projectTitle: string;
  chatId: string;
  chatTitle: string;
  eventId: string;
  agent: string | null;
  utc: string;
  excerpt: string;
  markers: string; // [[options]] / [[ask]] lines of the answer
  code: string | null; // captcha code to answer with
  error: string;
  count: number; // memory: pending candidates of the project; tools: pending proposals
  candidates: InboxMemory[];
  tools: ToolCandidate[];
  action: InboxAction | null;
  unread: boolean;
}

const str = (v: unknown): string => (typeof v === 'string' ? v : '');

export async function dbInbox(env: string): Promise<{ items: InboxItem[]; unread: number }> {
  const j = await rawCall('inbox', { env });
  const kinds: InboxKind[] = ['ask', 'captcha', 'confirm', 'action', 'failed', 'memory', 'tools'];
  const items = (Array.isArray(j.items) ? j.items : [])
    .filter((x: any) => x && typeof x.id === 'string' && kinds.includes(x.kind))
    .map((x: any): InboxItem => ({
      id: x.id,
      kind: x.kind,
      projectId: str(x.projectId),
      projectTitle: str(x.projectTitle),
      chatId: str(x.chatId),
      chatTitle: str(x.chatTitle),
      eventId: str(x.eventId),
      agent: typeof x.agent === 'string' ? x.agent : null,
      utc: str(x.utc),
      excerpt: str(x.excerpt),
      markers: str(x.markers),
      code: typeof x.code === 'string' ? x.code : null,
      error: str(x.error),
      count: typeof x.count === 'number' ? x.count : 0,
      candidates: (Array.isArray(x.candidates) ? x.candidates : [])
        .filter((c: any) => c && typeof c.id === 'number' && typeof c.text === 'string')
        .map((c: any): InboxMemory => ({
          id: c.id, tag: memoryTag(c.tag), text: c.text, chatId: str(c.chatId), chatTitle: str(c.chatTitle),
          kind: c.kind === 'rule' ? 'rule' : 'add', msg: typeof c.msg === 'number' ? c.msg : 0,
          reason: typeof c.reason === 'string' ? c.reason : null, targetEnv: typeof c.targetEnv === 'string' ? c.targetEnv : null,
        })),
      tools: (Array.isArray(x.tools) ? x.tools : []).filter((c: any) => c && typeof c.id === 'number').map(toolCandidate),
      action: x.action && typeof x.action.requestId === 'number' ? {
        requestId: x.action.requestId, kind: str(x.action.kind), title: str(x.action.title), summary: str(x.action.summary),
        command: str(x.action.command), tainted: x.action.tainted === true, expiresUtc: str(x.action.expiresUtc),
        actions: (Array.isArray(x.action.actions) ? x.action.actions : []).filter((a: any) => a && typeof a.summary === 'string')
          .map((a: any) => ({ kind: str(a.kind), summary: a.summary })),
      } : null,
      unread: x.unread === true,
    }));
  return { items, unread: Number(j.unread) || 0 };
}

// 0.81.0: Yes / No of a confirmation card (the chat message "<code> да|нет" is sent by the chat itself)
export async function dbActionDecide(id: number, decision: 'approved' | 'denied'): Promise<void> {
  await rawCall('action_decide', { id, decision });
}

export async function dbInboxMark(ids: string[], action: 'read' | 'unread' | 'done'): Promise<void> {
  // the server takes up to 300 ids per call
  for (let i = 0; i < ids.length; i += 300) await rawCall('inbox_mark', { ids: ids.slice(i, i + 300), action });
}

// 0.87.0: agent browser defaults of an environment (browser-lib.php); with defaults saves them.
export async function dbBrowserDefaults(env: string, defaults?: BrowserSettings): Promise<{ builtin: Required<BrowserSettings>; defaults: BrowserSettings }> {
  const j = await rawCall('browser_defaults', defaults === undefined ? { env } : { env, defaults });
  return { builtin: j.builtin, defaults: j.defaults || {} };
}

// 0.90.0: the family room of the right-panel tab "Family" (family-lib.php): project, room chat, unread count of this env
export interface FamilyState { project: Row | null; chat: Row | null; lastSeq: number; unread: number }
export async function dbFamilyState(): Promise<FamilyState> {
  const j = await rawCall('family_state', {});
  return { project: j.project ?? null, chat: j.chat ?? null, lastSeq: Number(j.lastSeq) || 0, unread: Number(j.unread) || 0 };
}
export async function dbFamilyRead(chatId: string, seq: number): Promise<void> {
  await rawCall('family_read', { chatId, seq });
}

// Freedom F10 (schema 27, modules-lib.php): modules. The core knows no module by name; their pages call their own
// declared ops through the ext/1 bridge (extBridge.ts, ModuleSlot.tsx), which ends in dbModuleCall.
// F10: the answer also describes the confidential gateway of an installed module (field gateways, gateway.ts); its last
// copy is kept in localStorage so a reload knows it (paused agents re-routed) before modules_list answers again.
const GATEWAYS_KEY = 'co.gateways';
try {
  const c = typeof localStorage !== 'undefined' ? localStorage.getItem(GATEWAYS_KEY) : null;
  if (c) setGateways(JSON.parse(c));
} catch { /* storage unavailable or broken copy: neutral */ }

export async function dbModules(): Promise<ModuleInfo[]> {
  return (await dbModuleList()).modules;
}
/** modules_list with the labels of import sources of every installed module (also switched-off ones). */
export async function dbModuleList(): Promise<{ modules: ModuleInfo[]; importLabels: { source: string; label?: string; speaker?: string }[] }> {
  const r = await rawCall('modules_list', {});
  const gws = Array.isArray(r.gateways) ? r.gateways : [];
  setGateways(gws);
  try { localStorage.setItem(GATEWAYS_KEY, JSON.stringify(gws)); } catch { /* storage unavailable */ }
  return { modules: Array.isArray(r.modules) ? (r.modules as ModuleInfo[]) : [], importLabels: Array.isArray(r.importLabels) ? r.importLabels : [] };
}
export async function dbModuleSet(id: string, enabled: boolean): Promise<void> {
  const r = await rawCall('module_set', { id, enabled });
  if (!r.ok) throw new Error(String(r.error || 'module_set failed'));
}
/** The person's own switch of a module with data about people; env = another member only for the owner's default. */
export async function dbModulePerson(id: string, mode: 'auto' | 'ask' | 'off', env?: string): Promise<void> {
  const r = await rawCall('module_person', env ? { id, mode, env } : { id, mode });
  if (!r.ok) throw new Error(String(r.error || 'module_person failed'));
}
/** A user op of a module (already checked by the bridge against the module's declared ops). */
export async function dbModuleCall(op: string, args: Record<string, unknown>, timeoutMs = TIMEOUT_MS): Promise<Record<string, unknown>> {
  const { op: _op, gz: _gz, ...rest } = args; // rawCall spreads params after op: an "op" key there would replace it
  void _op; void _gz;
  const res = await post({ op, ...rest }, false, Math.min(600000, Math.max(TIMEOUT_MS, timeoutMs)));
  if (!res) throw new Error('Database is not reachable.');
  const j = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(`Database: ${j.error || res.status}${j.detail ? ` (${j.detail})` : ''}`);
  return j;
}
/** ext/1 upload: a file posted to an upload the module declared (agent/module.php?m=<id>&u=<name>, multipart 'file'). */
export async function dbModuleUpload(id: string, name: string, file: Blob, filename: string, fields: Record<string, string>): Promise<Record<string, unknown>> {
  const form = new FormData();
  for (const [k, v] of Object.entries(fields)) form.append(k, v);
  form.append('file', file, filename);
  const headers: Record<string, string> = { 'X-CO-Auth': '1' };
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  const res = await fetch(`/agent/module.php?m=${encodeURIComponent(id)}&u=${encodeURIComponent(name)}`, {
    method: 'POST', headers, body: form, credentials: 'same-origin', cache: 'no-store',
  });
  const j = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(String(j.detail || j.error || `HTTP ${res.status}`));
  return j;
}

// Global switch of the automatic Claude <-> Codex retry on limit / sign-in / model errors (owner sets it).
export async function dbAutoSwitch(enabled?: boolean): Promise<{ enabled: boolean; canSet: boolean }> {
  const j = await rawCall('autoswitch', enabled === undefined ? {} : { enabled });
  return { enabled: j.enabled !== false, canSet: j.canSet === true };
}

// ---------- S5 (0.99.0, schema 23): model resources and "slave allowed" (db.php models_list / model_set) ----------

export interface ModelCard { summary?: string; strengths?: string; limits?: string; dataStaysIn?: string; notes?: string }
export interface ModelResource {
  id: string;
  env: string;
  kind: 'subscription' | 'site' | 'gateway' | 'free' | string; // legacy wire kind: isGatewayKind()
  title: string;
  host: string;
  agent: string;
  models: { id: string; label: string }[];
  defaultModel: string | null;
  card: ModelCard;
  slaveAllowed: boolean;
  confidential: boolean;
  source: 'auto' | 'user' | 'device' | string;
  builtin: boolean;
  online: boolean | null;
  own: boolean;
  callable: boolean;
}

export async function dbModels(): Promise<ModelResource[]> {
  const r = await rawCall('models_list', {});
  return Array.isArray(r.resources) ? (r.resources as ModelResource[]) : [];
}

export async function dbModelSet(id: string, patch: { slaveAllowed?: boolean; confidential?: boolean; title?: string; card?: ModelCard }): Promise<ModelResource | null> {
  const r = await rawCall('model_set', { id, ...patch }, true); // gz: card text may look like code to the hosting WAF
  if (r.ok === false) throw new Error(r.error === 'not_owner' ? 'Only the owner of this model can change it.' : String(r.error));
  return (r.resource as ModelResource | null) ?? null;
}

export async function dbModelDelete(id: string): Promise<void> {
  const r = await rawCall('model_delete', { id });
  if (r.ok === false) throw new Error(String(r.error));
}

// Freedom F12 (schema 28, executors-lib.php): the executors that serve the environment (Bridges on any machine and the
// site agent) with their health and owner.json copy; null on failure or a server before schema 28.
export interface ExecutorInfo {
  id: string;
  kind: 'bridge' | 'site' | string;
  title: string;
  registered: boolean;
  enabled: boolean;
  ownerEnv: string;
  os: string;
  version: string;
  state: string;
  seenUtc: string | null;
  fresh: boolean | null;
  takesJobs: boolean;
  servesMe: boolean;
  agents: string[];
  sandboxForMe: boolean;
  rank: number;
  machine?: string;
  owner?: import('./types').OwnerInfo;
}

// A server before schema 28 answers 400 (unknown op): asked once per page load, then never again.
let executorsUnsupported = false;

export async function dbExecutors(env: string): Promise<ExecutorInfo[] | null> {
  if (executorsUnsupported) return null;
  const res = await post({ op: 'executors', env }, false);
  if (res && res.status === 400) executorsUnsupported = true;
  if (!res || !res.ok) return null;
  const j = (await res.json().catch(() => null)) as { executors?: unknown } | null;
  return j && Array.isArray(j.executors)
    ? (j.executors as ExecutorInfo[]).filter((x) => !!x && typeof x.id === 'string' && /^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/.test(x.id))
    : null;
}

// Owner only: switch a registered executor off (it gets no new jobs) or on, or forget it.
export async function dbExecutorSet(host: string, change: { enabled?: boolean; forget?: boolean }): Promise<boolean> {
  const res = await post({ op: 'executor_set', host, ...change }, false);
  return !!res && res.ok;
}
