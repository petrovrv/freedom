// SPDX-License-Identifier: AGPL-3.0-or-later
// CO server agent: the "site" executor of AI-Workbench on Hostinger.
// Implements the BridgeAgent job protocol (PROTOCOL.md v3 minor 1, chat and cowork modes) over the
// Drive API: transport/hosts/site/{inbox,claimed,events,completed,failed,rejected,cancel}.
// Context folders and Pub/Sub are home-only features and are not announced. Since 1.4.0 (S1) Claude jobs get the tools of
// the home devices of their environment through the server (relay-mcp.mjs, db.php relay_*), served by the home Bridge.

import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync, openSync, readSync, closeSync, lstatSync } from 'node:fs';
import { homedir, hostname } from 'node:os';
import { gzipSync } from 'node:zlib';
import { dirname, join, relative, sep } from 'node:path';
import * as drive from './drive.mjs';

const VERSION = '1.8.2';
const HOST_ID = 'site';
const HOME = homedir();
const BASE = join(HOME, 'co-agent');
const STATE = join(BASE, 'state');
const WORK = join(BASE, 'work');
const LOGS = join(BASE, 'executor', 'logs');
const PID_FILE = join(BASE, 'executor', 'agent.pid');
const CLAUDE_TOKEN_FILE = join(BASE, 'secrets', 'claude_oauth_token');
// 1.8.2 (subtask B of chat CHAT-20261112071412-CJZ0): the [Home PC] note when dev_home has pc_* (Bridge 1.15.0): the
// owner's whole PC, or the member's own sandbox seat (schema 26 picks it)
// 1.8.1 (D 12.11): job_result is retried (3 tries); the server writes one answer per job (schema 25) and sends the
// "job finished" push of site jobs once (jobs.pushed_utc), so a retry gives no second message or push
// 1.8.0 (subtask A of chat CHAT-20261112071412-CJZ0): home devices as MCP over HTTP served by the server itself
// (relay-mcp.php?d=<short>, a one-time key of the job from relay_token in mcp.json) instead of a relay-mcp.mjs node
// process per device and job (threads of the account: CONNECTION_CLOSED, exit 128). CO_RELAY_STDIO=1 or the file
// state/relay-stdio = the old way; an old server without relay_token falls back to it by itself.
// 1.7.0 (R2): no home devices for a restricted job (shared chat or below one); relay_devices gets the job and chat
// 1.6.0 (S7): jobs queued in the database (aiwb-subtask, aiwb-smoke, db.php job_submit) are taken with job_claim
// within seconds instead of the Drive inbox copy of the queue fallback (90 s + cron); results go to job_result at once.
// 1.5.0 (S5): the agent's tools (aiwb-tools -> aiwb-subtask) get AIWB_DB_URL / AIWB_DB_KEY_FILE / AIWB_CHAT_ID to start
// subtasks on model resources through db.php (subtask_spawn), as a leading agent on the site
// 1.4.1 (S2): relay-mcp.mjs runs with 4-5 threads instead of 11 (Hostinger ~80 threads per account, EAGAIN otherwise)
// 1.4.0 (S1): home devices through the server; the push key of db.php host ops (also used by the Bridge)
const RELAY_KEY_FILE = join(BASE, 'secrets', 'push-notify-key');
const RELAY_URL = process.env.CO_DB_URL || ''; // set by ping.php from the site config (Freedom F1)
const RELAY_DIR = join(STATE, 'relay');
const RELAY_MCP = join(dirname(new URL(import.meta.url).pathname), 'relay-mcp.mjs');
const RELAY_MCP_URL = process.env.CO_RELAY_MCP_URL || RELAY_URL.replace(/db\.php$/, 'relay-mcp.php');
const relayStdio = () => process.env.CO_RELAY_STDIO === '1' || existsSync(join(STATE, 'relay-stdio')) || !RELAY_MCP_URL.endsWith('relay-mcp.php');

const SCAN_MS = 3000;
const DB_CLAIM_MS = 3000; // 1.6.0: one short db.php call per scan (a long poll would hold an lsphp process all the time)
const OWNER_MS = 60000;
const LIMITS_MS = 60000;
const EVENT_FLUSH_MS = 2000;
const EVENT_FLUSH_BYTES = 65536;
const MAX_PARALLEL = 2;
const MAX_TIMEOUT_MIN = 240;
const MODE_TIMEOUT_MIN = { chat: 120, cowork: 120 };
const DRAIN_MS = 30000;
const EXPORT = { maxFiles: 50, maxBytes: 200 * 1024 * 1024, maxFileBytes: 100 * 1024 * 1024, maxScan: 200000 };
const EXCLUDED_DIRS = new Set(['.git', '.co-attachments', 'node_modules', '.venv', 'venv', '__pycache__', '.pytest_cache', '.mypy_cache', '.idea', '.vs', 'obj', '.claude', '.codex']);
const EXCLUDED_FILE = /^(~\$.*|.*\.tmp|desktop\.ini|Thumbs\.db|\.coignore)$/i;
const SAFE_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/;
const EFFORTS = ['low', 'medium', 'high', 'xhigh', 'max'];
// Advertise GPT (Codex) in owner.json; off since 1.2.1 (see ownerDoc). CO_SITE_CODEX=1 turns it back on.
const SITE_CODEX = process.env.CO_SITE_CODEX === '1';
// Codex could not start its tool host or threads: the hosting process limit, not the task.
const CODEX_NO_TOOLS_RE = /code-mode host|Resource temporarily unavailable|os error 11\b|WouldBlock|creating threadpool failed|failed to spawn thread/;

// Environments that run here (on the owner's Claude/Codex subscriptions); every project is a Drive-only chat project.
// Freedom F1: the owner and members with site_agent of the table members; ping.php keeps their list in
// state/members.json (members-lib.php), read again on every use so a change needs no restart.
function chatProjectEnvironments() {
  let members = [];
  try {
    members = JSON.parse(readFileSync(join(STATE, 'members.json'), 'utf8'));
  } catch {
    /* no list yet: nothing runs here */
  }
  const out = {};
  for (const m of Array.isArray(members) ? members : []) {
    if (m && typeof m.env === 'string' && (m.role === 'owner' || m.siteAgent === true)) {
      out[m.env] = { allowedAgents: ['claude', 'codex'], autoExport: true };
    }
  }
  return out;
}

const CLAUDE_MODELS = [
  { id: 'claude-opus-5-5', label: 'Opus 5.5', description: null, efforts: null, defaultEffort: null },
  { id: 'opus', label: 'Opus (latest)', description: null, efforts: null, defaultEffort: null },
  { id: 'fable', label: 'Fable (latest)', description: null, efforts: null, defaultEffort: null },
  { id: 'sonnet', label: 'Sonnet (latest)', description: null, efforts: null, defaultEffort: null },
];
// Codex chat mode: lean agent without the heavy optional features (Bridge 1.0.3).
const CODEX_CHAT_DISABLE = ['apps', 'browser_use', 'computer_use', 'image_generation', 'multi_agent', 'plugins', 'hooks'];
const CODEX_CHAT_CONFIG = ['model_auto_compact_token_limit=40000', 'tool_output_token_limit=4000', 'project_doc_max_bytes=0'];

mkdirSync(STATE, { recursive: true });
mkdirSync(WORK, { recursive: true });
mkdirSync(LOGS, { recursive: true });

function log(...a) {
  const line = `${new Date().toISOString()} ${a.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))).join(' ')}\n`;
  appendFileSync(join(LOGS, `${new Date().toISOString().slice(0, 10)}.log`), line);
}

const nowUtc = () => new Date().toISOString();
const sha256 = (b) => createHash('sha256').update(b).digest('hex');
const md5 = (b) => createHash('md5').update(b).digest('hex');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- single instance ----------

function alive(pid) {
  try {
    process.kill(pid, 0);
    return readFileSync(`/proc/${pid}/cmdline`, 'utf8').includes('agent.mjs');
  } catch {
    return false;
  }
}
if (existsSync(PID_FILE)) {
  const other = Number(readFileSync(PID_FILE, 'utf8'));
  if (other && other !== process.pid && alive(other)) process.exit(0);
}
writeFileSync(PID_FILE, String(process.pid));

// ---------- Drive layout ----------

let root = null;
let hostDir = null;
const buckets = {};

async function discover() {
  const found = await drive.list(`name='AI-Workbench' and mimeType='${drive.FOLDER_MIME}' and trashed=false`);
  for (const f of found) {
    if (await drive.findChild(f.id, 'transport', true)) {
      root = f.id;
      break;
    }
  }
  if (!root) throw new Error('AI-Workbench folder not found in Drive');
  hostDir = await drive.ensurePath(root, ['transport', 'hosts', HOST_ID]);
  for (const b of ['inbox', 'claimed', 'events', 'completed', 'failed', 'rejected', 'cancel']) buckets[b] = await drive.ensureFolder(hostDir, b);
}

// ---------- owner.json ----------

let codexModels = null;
function codexCatalog() {
  try {
    const cache = JSON.parse(readFileSync(join(HOME, '.codex', 'models_cache.json'), 'utf8'));
    codexModels = cache.models
      .filter((m) => m.visibility === 'list')
      .sort((a, b) => (a.priority ?? 99) - (b.priority ?? 99))
      .map((m) => ({
        id: m.slug, label: m.display_name || m.slug, description: m.description || null,
        efforts: (m.supported_reasoning_levels || []).map((x) => x.effort), defaultEffort: m.default_reasoning_level || null,
      }));
  } catch {
    /* keep the previous catalog */
  }
  return codexModels || [];
}

function ownerDoc() {
  const codex = codexCatalog();
  return {
    schemaVersion: 2, hostId: HOST_ID, machineName: `site (${hostname()})`, processId: process.pid,
    bridgeVersion: `site-${VERSION}`, updatedUtc: nowUtc(), protocolMinor: 1,
    capabilities: ['protocol-v3', 'conversation-resume', 'codex-fork', 'attachments', 'cancel', 'modes', 'model-select', 'effort-select',
      'auto-export', 'usage', 'project-catalog', 'open-chat-projects', 'usage-limits', 'permissions', 'chat-cowork', 'chat-files'],
    // 1.2.1: GPT is not offered here. Since 2026-11-02 Codex cannot start its tool processes on this shared hosting
    // (per-account thread limit, EAGAIN), so CO Web sends GPT jobs to Home (they wait there while Home is offline).
    agents: {
      ...(SITE_CODEX ? { codex: { displayName: 'GPT', modes: ['chat', 'cowork'], models: codex, efforts: EFFORTS, defaultModel: codex[0]?.id || null, defaultEffort: 'medium' } } : {}),
      claude: { displayName: 'Claude', modes: ['chat', 'cowork'], models: CLAUDE_MODELS, efforts: EFFORTS, defaultModel: null, defaultEffort: null },
    },
    projects: {},
    chatProjectEnvironments: chatProjectEnvironments(),
    contextRoots: {},
  };
}

async function writeOwner() {
  await drive.upsertFile(hostDir, 'owner.json', JSON.stringify(ownerDoc()), 'application/json');
}

// ---------- limits.json ----------

const limits = { schemaVersion: 1, updatedUtc: null, agents: {} };
let limitsDirty = false;

function windowId(key, minutes) {
  if (minutes > 0 && minutes <= 1440) return 'session';
  if (key === 'seven_day' || key === 'secondary' || minutes === 10080) return 'week';
  return key;
}
const pct = (v) => Math.round(Math.min(100, Math.max(0, v)) * 10) / 10;

function setLimits(agent, entry) {
  const cur = limits.agents[agent];
  if (cur && cur.observedUtc && entry.observedUtc < cur.observedUtc) return;
  if (JSON.stringify(cur) === JSON.stringify(entry)) return;
  limits.agents[agent] = entry;
  limitsDirty = true;
}

function claudeLimits(ev) {
  const info = ev.rate_limit_info || {};
  const wins = info.unifiedWindows || {};
  const windows = Object.entries(wins).map(([key, w]) => {
    const minutes = key === 'five_hour' ? 300 : key.startsWith('seven_day') ? 10080 : 0;
    return {
      id: windowId(key, minutes), key, windowMinutes: minutes, usedPercent: pct((w.utilization || 0) * 100),
      resetsAtUtc: w.resetsAt ? new Date(w.resetsAt * 1000).toISOString() : null,
    };
  });
  setLimits('claude', { source: 'claude-code', observedUtc: nowUtc(), status: info.status || null, plan: null, windows });
}

function codexLimits() {
  const dir = join(HOME, '.codex', 'sessions');
  let newest = null;
  const cutoff = Date.now() - 8 * 86400000;
  const walk = (d) => {
    let ents;
    try {
      ents = readdirSync(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of ents) {
      const p = join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.name.endsWith('.jsonl')) {
        const m = statSync(p).mtimeMs;
        if (m > cutoff && (!newest || m > newest.m)) newest = { p, m };
      }
    }
  };
  walk(dir);
  if (!newest) return;
  const line = lastLineWith(newest.p, '"rate_limits"');
  if (!line) return;
  try {
    const j = JSON.parse(line);
    const rl = j.payload?.rate_limits;
    if (!rl) return;
    const windows = ['primary', 'secondary'].filter((k) => rl[k]).map((k) => ({
      id: windowId(k, rl[k].window_minutes || 0), key: k, windowMinutes: rl[k].window_minutes || 0, usedPercent: pct(rl[k].used_percent || 0),
      resetsAtUtc: rl[k].resets_at ? new Date(rl[k].resets_at * 1000).toISOString() : null,
    }));
    setLimits('codex', {
      source: 'codex-session-log', observedUtc: j.timestamp || nowUtc(), status: rl.rate_limit_reached_type ? 'limited' : 'allowed',
      plan: rl.plan_type || null, windows,
    });
  } catch {
    /* malformed line */
  }
}

function lastLineWith(file, needle) {
  const size = statSync(file).size;
  const len = Math.min(size, 512 * 1024);
  const buf = Buffer.alloc(len);
  const fd = openSync(file, 'r');
  readSync(fd, buf, 0, len, size - len);
  closeSync(fd);
  const lines = buf.toString('utf8').split('\n');
  for (let i = lines.length - 1; i >= 0; i--) if (lines[i].includes(needle)) return lines[i];
  return null;
}

async function flushLimits() {
  if (!limitsDirty) return;
  limitsDirty = false;
  limits.updatedUtc = nowUtc();
  await drive.upsertFile(hostDir, 'limits.json', JSON.stringify(limits), 'application/json');
}

// ---------- events ----------

class EventWriter {
  constructor(jobId) {
    this.jobId = jobId;
    this.seq = 0;
    this.pending = [];
    this.bytes = 0;
    this.folder = null;
    this.chain = Promise.resolve();
    this.timer = setInterval(() => void this.flush(), EVENT_FLUSH_MS);
  }
  emit(source, type, content) {
    const e = { schemaVersion: 2, sequence: ++this.seq, createdUtc: nowUtc(), source, type, content: String(content ?? '') };
    this.pending.push(e);
    this.bytes += Buffer.byteLength(e.content) + 256;
    if (this.bytes >= EVENT_FLUSH_BYTES) void this.flush();
  }
  flush() {
    this.chain = this.chain.then(async () => {
      if (!this.pending.length) return;
      const batch = this.pending;
      this.pending = [];
      this.bytes = 0;
      try {
        if (!this.folder) this.folder = await drive.ensureFolder(buckets.events, this.jobId);
        const first = batch[0].sequence;
        const last = batch[batch.length - 1].sequence;
        const name = `${String(first).padStart(6, '0')}-${String(last).padStart(6, '0')}.json`;
        await drive.createFile(this.folder, name, JSON.stringify({ schemaVersion: 2, firstSequence: first, lastSequence: last, events: batch }), 'application/json');
      } catch (e) {
        log('event flush failed', this.jobId, String(e));
        this.pending = batch.concat(this.pending);
      }
    });
    return this.chain;
  }
  async close() {
    clearInterval(this.timer);
    await this.flush();
  }
}

// ---------- job validation ----------

class Reject extends Error {}

function normRel(p) {
  const parts = String(p || '').replace(/\\/g, '/').split('/').filter(Boolean);
  if (!parts.length || String(p).startsWith('/')) throw new Error(`Invalid relative path: ${p}`);
  for (const x of parts) if (x === '.' || x === '..' || /[.\s]$/.test(x) || /[\0<>:"|?*]/.test(x)) throw new Error(`Invalid relative path: ${p}`);
  return parts.join('/');
}

function safe(label, v, required = true) {
  if (v == null || v === '') {
    if (required) throw new Error(`${label} is required.`);
    return;
  }
  if (!SAFE_ID.test(String(v))) throw new Error(`${label} must be 1-40 ASCII characters: letters, digits, '_' or '-'.`);
}

function validate(req, folderName) {
  if (req.schemaVersion !== 2 && req.schemaVersion !== 3) throw new Error('Unsupported job schemaVersion. Expected 2 or 3.');
  safe('hostId', req.hostId);
  safe('jobId', req.jobId);
  safe('environmentId', req.environmentId);
  safe('projectId', req.projectId);
  safe('chatId', req.chatId);
  safe('agent', req.agent);
  safe('branchId', req.branchId, false);
  if (req.hostId !== HOST_ID) throw new Error('Job hostId does not match this BridgeAgent hostId.');
  if (req.jobId !== folderName) throw new Error('jobId does not match the job folder name.');
  if (!String(req.instruction || '').trim()) throw new Error('instruction is required.');
  if (!['claude', 'codex'].includes(req.agent)) throw new Error(`Unknown agent profile: ${req.agent}`);
  const policy = chatProjectEnvironments()[req.environmentId];
  if (!policy) throw new Error(`Unknown project profile: ${req.projectId} (environment '${req.environmentId}' does not run on the site host).`);
  if (req.agent === 'codex' && !SITE_CODEX) throw new Error('Site host cannot run Codex tools (hosting thread limit, EAGAIN); send the message to Home.');
  if (!policy.allowedAgents.includes(req.agent)) throw new Error(`Agent '${req.agent}' is not allowed for project '${req.projectId}'.`);
  const conv = req.conversation || { operation: 'new' };
  if (!['new', 'resume', 'fork'].includes(conv.operation)) throw new Error('conversation.operation must be new, resume or fork.');
  if (conv.operation !== 'new' && !conv.sessionId) throw new Error(`conversation.sessionId is required for ${conv.operation}.`);
  if (conv.operation === 'fork' && req.agent === 'claude')
    throw new Error('Native Claude fork is not supported by this Bridge release. Create a new session from the selected branch context instead.');
  const mode = req.mode || 'cowork';
  if (!['chat', 'cowork'].includes(mode)) throw new Error('mode must be chat or cowork.');
  const catalog = req.agent === 'claude' ? CLAUDE_MODELS : codexCatalog();
  const model = req.model || null;
  const entry = model ? catalog.find((m) => m.id === model) : null;
  if (model && !entry) throw new Error(`Model '${model}' is not in the catalog of agent '${req.agent}'.`);
  const effort = req.effort || null;
  if (effort && !(entry?.efforts || EFFORTS).includes(effort)) throw new Error(`Effort '${effort}' is not allowed for agent '${req.agent}'${model ? ` model '${model}'` : ''}.`);
  const out = req.output || {};
  if ((out.maxFiles ?? 0) < 0 || (out.maxBytes ?? 0) < 0) throw new Error('output limits cannot be negative.');
  const perm = req.permissions || null;
  if (perm?.files != null && !['read', 'write'].includes(perm.files)) throw new Error('permissions.files must be read or write.');
  const attachments = (req.attachments || []).map((a) => {
    const path = normRel(a.path);
    if (!(a.sizeBytes >= 0)) throw new Error('Attachment sizeBytes must be non-negative.');
    if (!/^[0-9a-fA-F]{64}$/.test(a.sha256 || '')) throw new Error('Attachment sha256 must be 64 hexadecimal characters.');
    // CO Web 0.55.0: target = path inside the chat scratch the file becomes (a new version of that chat file).
    const target = typeof a.target === 'string' && a.target.trim() ? normRel(a.target) : null;
    return { path, sizeBytes: a.sizeBytes, sha256: a.sha256.toLowerCase(), target };
  });
  const artifactPaths = (req.artifactPaths || []).map(normRel);
  return { ...req, conversation: conv, mode, model, effort, permissions: perm, attachments, artifactPaths, branchId: req.branchId || null, policy };
}

// ---------- command lines ----------

function selectionArgs(job) {
  const a = [];
  const p = job.permissions;
  if (p) {
    const ro = p.files === 'read';
    const shell = p.shell !== false;
    const web = p.web !== false;
    if (job.agent === 'codex') {
      // Shared hosting has no user namespaces, so bubblewrap cannot start: every sandboxed command fails
      // and is retried unsandboxed after review. Write access therefore runs unsandboxed directly.
      a.push('-c', 'approval_policy="never"', '-c', `sandbox_mode="${ro ? 'read-only' : 'danger-full-access'}"`);
      // aiwb-tools MCP (tool library): its tools may write files, so read-only chats need approval (= blocked).
      if (ro) a.push('-c', 'mcp_servers.aiwb-tools.default_tools_approval_mode="prompt"');
      if (!shell) a.push('--disable', 'shell_tool', '--disable', 'unified_exec');
      if (!web) a.push('-c', 'web_search="disabled"');
    } else {
      const deny = [];
      if (ro) deny.push('Write', 'Edit', 'MultiEdit', 'NotebookEdit', 'mcp__aiwb-tools__tool_run');
      if (!shell) deny.push('Bash');
      if (!web) deny.push('WebSearch', 'WebFetch');
      if (deny.length) a.push('--disallowedTools', deny.join(','));
    }
  }
  if (job.agent === 'codex') {
    if (job.mode === 'chat') {
      for (const f of CODEX_CHAT_DISABLE) a.push('--disable', f);
      for (const c of CODEX_CHAT_CONFIG) a.push('-c', c);
    }
    if (job.model) a.push('-m', job.model);
    if (job.effort) a.push('-c', `model_reasoning_effort=${job.effort}`);
  } else {
    if (job.model) a.push('--model', job.model);
    if (job.effort) a.push('--effort', job.effort);
  }
  return a;
}

function commandLine(job, relay) {
  const sel = selectionArgs(job);
  const op = job.conversation.operation;
  if (job.agent === 'codex') {
    if (op === 'new') return ['codex', ['exec', '--json', '--skip-git-repo-check', ...(job.permissions ? [] : ['--approve-for-me']), ...sel, '-']];
    return ['codex', ['exec', op, '--json', '--skip-git-repo-check', ...sel, job.conversation.sessionId, '-']];
  }
  const args = ['-p', '--output-format', 'stream-json', '--verbose', '--permission-mode', 'acceptEdits',
    '--allowedTools', `Read,Write,Edit,Bash,Glob,Grep,WebSearch,WebFetch,mcp__aiwb-tools${relay ? relay.allow : ''}`, ...sel]; // aiwb-tools: tool library (toolbox 1.4.0)
  if (relay) args.push('--mcp-config', relay.config);
  if (op === 'resume') args.push('--resume', job.conversation.sessionId);
  return ['claude', args];
}

// ---------- home devices through the server (1.4.0, S1) ----------
// The devices the home Bridge published for the job's environment (db.php relay_devices), each as an MCP server
// dev_<short>: relay-mcp.php over HTTP (1.8.0) or relay-mcp.mjs. A job that names devices (CO Web on a computer with CO Desktop) gets those only.

async function relayDb(op, body, timeoutMs = 10000, gz = false) {
  const key = readFileSync(RELAY_KEY_FILE, 'utf8').trim();
  // 1.6.0: gz bodies (job_result) like the Bridge - the hosting firewall answers 403 to plain bodies with code or SQL
  const plain = JSON.stringify({ ...body, op });
  const r = await fetch(RELAY_URL, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CO-Push-Key': key },
    body: gz ? JSON.stringify({ op, gz: gzipSync(Buffer.from(plain)).toString('base64') }) : plain,
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return r.json();
}

// 1.6.0 (S7): the database queue of this host is usable when db.php and the host key are known (CO_DB_URL from ping.php)
const dbQueue = () => !!RELAY_URL && existsSync(RELAY_KEY_FILE);

async function relaySetup(job, jobId, ev, timeoutMin) {
  if (job.agent !== 'claude' || !RELAY_URL || !existsSync(RELAY_KEY_FILE)) return null;
  // 1.7.0 (R2): a job of a shared chat (or below one) gets no devices; the server decides (restricted, relay_devices
  // with the job) and refuses its calls in relay_call_put too
  if (job.restricted === true) return null;
  let devices;
  try {
    const listed = await relayDb('relay_devices', { env: job.environmentId, jobId, chatId: job.chatId || '' });
    if (listed.restricted) return null;
    devices = listed.devices || [];
  } catch (e) {
    ev.emit('bridge', 'devices_unavailable', `Home devices could not be listed: ${e.message}`);
    return null;
  }
  const wanted = Array.isArray(job.devices) && job.devices.length ? new Set(job.devices) : null;
  devices = devices.filter((d) => d.online && /^[a-z0-9]{1,12}$/.test(d.short) && (!wanted || wanted.has(d.deviceId) || d.kind === 'home'));
  if (!devices.length) return null;
  const deny = Array.isArray(job.deviceToolsDeny) ? job.deviceToolsDeny.filter((n) => typeof n === 'string').slice(0, 32) : [];
  // 1.8.0: the key of this job for relay-mcp.php; the server binds it to the job, env and chat and refuses it for a
  // restricted job. Deleted by relay_cancel at the end of the job.
  let token = null;
  if (!relayStdio()) {
    try {
      const t = await relayDb('relay_token', { env: job.environmentId, jobId, chatId: job.chatId || '', deny, tainted: !!job.tainted, ttlSec: (timeoutMin + 15) * 60 });
      if (t.restricted) return null;
      if (!t.ok || typeof t.token !== 'string') throw new Error(t.error?.message || 'no key');
      token = t.token;
    } catch (e) {
      log('relay_token failed, node relay instead', jobId, String(e));
    }
  }
  const dir = join(RELAY_DIR, jobId);
  mkdirSync(dir, { recursive: true, mode: 0o700 });
  const servers = {};
  for (const d of devices) {
    if (token) {
      servers[`dev_${d.short}`] = { type: 'http', url: `${RELAY_MCP_URL}?d=${d.short}`, headers: { Authorization: `Bearer ${token}` } };
      continue;
    }
    const toolsFile = join(dir, `${d.short}.json`);
    writeFileSync(toolsFile, JSON.stringify({ short: d.short, name: d.name, kind: d.kind, tools: d.tools, deny }));
    servers[`dev_${d.short}`] = {
      // 1.4.1: one V8 worker and one libuv worker - two devices with default node cost ~22 of the ~80 threads of the account
      type: 'stdio', command: process.execPath, args: ['--v8-pool-size=1', RELAY_MCP],
      env: {
        UV_THREADPOOL_SIZE: '1',
        RELAY_URL, RELAY_KEY_FILE, RELAY_JOB: jobId, RELAY_ENV: job.environmentId, RELAY_CHAT: job.chatId, RELAY_DEVICE: d.deviceId,
        RELAY_TOOLS_FILE: toolsFile, RELAY_TAINTED: job.tainted ? '1' : '0', NODE_OPTIONS: process.env.NODE_OPTIONS || '',
      },
    };
  }
  const config = join(dir, 'mcp.json');
  writeFileSync(config, JSON.stringify({ mcpServers: servers }), { mode: 0o600 });
  const names = devices.map((d) => `dev_${d.short} (${d.kind === 'home' ? 'home PC' : d.name})`).join(', ');
  ev.emit('bridge', 'devices_attached', `Home devices through the server${token ? '' : ' (node relay)'}: ${names}`);
  let note = `\n\n[Home devices] This job runs on the site; these devices of the user are reachable through the home Bridge as MCP servers: ${names}. `
    + 'Use them when the user asks for something on their computer (its browser, files, signed-in sites).\n';
  // 1.8.2 (Bridge 1.15.0, subtask B): the home PC itself as a computer to code, build and run on
  const pc = devices.find((d) => Array.isArray(d.tools) && d.tools.some((t) => t && t.name === 'pc_shell'));
  if (pc) {
    const sandbox = pc.deviceId !== `h-${pc.host}`;
    note += `[Home PC] mcp__dev_${pc.short}__pc_* run ${sandbox
      ? 'in your own Linux sandbox on the home PC (files only in your home folder; compilers and interpreters are there)'
      : 'on the home Windows PC like a Home job (the whole PC: source trees, dotnet, git, Python, the tool library)'}: `
      + 'pc_shell (script; background:true + pc_shell_status / pc_shell_kill for long builds), pc_read / pc_write / pc_edit / pc_list / pc_search for files there, '
      + 'pc_upload / pc_download with local_path to move a file between this site job and the PC. '
      + 'Prefer them over guessing when the user asks to build, test or run something "at home" or "on my computer".'
      + (sandbox ? '' : ' A risky command answers owner_confirm_required with a code: ask the user for "<code> да" and run the grant command it names through pc_shell.')
      + '\n';
  }
  return { config, dir, jobId, allow: devices.map((d) => `,mcp__dev_${d.short}`).join(''), note };
}

function agentEnv(agent) {
  // CloudLinux LVE caps threads per account; Codex sizes its pools by the 48 host CPUs otherwise.
  const env = { ...process.env, HOME, LANG: 'C.UTF-8', LC_ALL: 'C.UTF-8', RAYON_NUM_THREADS: '2', TOKIO_WORKER_THREADS: '2' };
  if (agent === 'claude') {
    env.CLAUDE_CODE_OAUTH_TOKEN = readFileSync(CLAUDE_TOKEN_FILE, 'utf8').trim();
    env.DISABLE_AUTOUPDATER = '1';
  }
  return env;
}

// ---------- output inspection ----------

function newInspection() {
  return { sessionId: null, final: null, finalSeen: false, success: false, failure: null, errorEvents: 0, errors: [], usage: null, modelReported: null };
}

function inspectLine(job, st, line) {
  let ev;
  try {
    ev = JSON.parse(line);
  } catch {
    return;
  }
  if (job.agent === 'codex') {
    if (ev.type === 'thread.started') st.sessionId = ev.thread_id;
    else if (ev.type === 'item.completed' && ev.item?.type === 'agent_message' && ev.item.text) st.final = ev.item.text;
    else if (ev.type === 'turn.completed') {
      st.finalSeen = true;
      st.success = !st.failure;
      const u = ev.usage || {};
      const cur = st.usage || { inputTokens: 0, cachedInputTokens: 0, cacheCreationInputTokens: 0, outputTokens: 0, reasoningOutputTokens: 0, costUsd: null, durationMs: null, numTurns: null };
      cur.inputTokens += u.input_tokens || 0;
      cur.cachedInputTokens += u.cached_input_tokens || 0;
      cur.outputTokens += u.output_tokens || 0;
      cur.reasoningOutputTokens += u.reasoning_output_tokens || 0;
      st.usage = cur;
    } else if (ev.type === 'turn.failed') {
      st.failure = ev.error?.message || ev.message || 'turn failed';
      st.finalSeen = true;
      st.success = false;
    } else if (ev.type === 'error') {
      st.errorEvents++;
      if (st.errors.length < 20) st.errors.push(String(ev.message || ev.error?.message || 'error'));
    }
    return;
  }
  if (ev.type === 'system' && ev.subtype === 'init') {
    st.sessionId = ev.session_id || st.sessionId;
    st.modelReported = ev.model || st.modelReported;
  } else if (ev.type === 'rate_limit_event') claudeLimits(ev);
  else if (ev.type === 'result') {
    st.finalSeen = true;
    st.success = !ev.is_error && ev.subtype === 'success';
    st.sessionId = ev.session_id || st.sessionId;
    st.final = ev.result ?? st.final;
    const u = ev.usage || {};
    st.usage = {
      inputTokens: u.input_tokens || 0, cachedInputTokens: u.cache_read_input_tokens || 0, cacheCreationInputTokens: u.cache_creation_input_tokens || 0,
      outputTokens: u.output_tokens || 0, reasoningOutputTokens: 0, costUsd: ev.total_cost_usd ?? null, durationMs: ev.duration_ms ?? null, numTurns: ev.num_turns ?? null,
    };
    if (!st.success) st.failure = `Claude result subtype: ${ev.subtype}`;
  }
}

// Codex resume reports session totals; subtract the stored total so usage covers this turn only.
function codexBaseline(sessionId) {
  const dir = join(HOME, '.codex', 'sessions');
  let file = null;
  const walk = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.name.endsWith(`${sessionId}.jsonl`)) file = p;
    }
  };
  try {
    walk(dir);
    const line = file && lastLineWith(file, 'total_token_usage');
    return line ? JSON.parse(line).payload?.info?.total_token_usage || null : null;
  } catch {
    return null;
  }
}

// ---------- workspace snapshot and export ----------

function coignore(ws) {
  try {
    return readFileSync(join(ws, '.coignore'), 'utf8').split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#')).map((raw) => {
      let p = raw.replace(/\\/g, '/').replace(/^\//, '');
      const dirOnly = p.endsWith('/');
      if (dirOnly) p = p.slice(0, -1);
      const body = p.replace(/[.+^${}()|[\]]/g, '\\$&').replace(/\*/g, '[^/]*').replace(/\?/g, '[^/]');
      return new RegExp(`${p.includes('/') ? '^' : '(^|/)'}${body}${dirOnly ? '(/|$)' : '(/.*)?$'}`, 'i');
    });
  } catch {
    return [];
  }
}

function snapshot(ws) {
  const out = new Map();
  const ign = coignore(ws);
  let count = 0;
  const walk = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      if (++count > EXPORT.maxScan) throw new Error(`workspace has more than ${EXPORT.maxScan} entries; auto-export skipped (use artifactPaths or .coignore)`);
      const p = join(d, e.name);
      const rel = relative(ws, p).split(sep).join('/');
      if (ign.some((r) => r.test(rel))) continue;
      if (e.isSymbolicLink()) continue;
      if (e.isDirectory()) {
        if (!EXCLUDED_DIRS.has(e.name)) walk(p);
      } else if (e.isFile() && !EXCLUDED_FILE.test(e.name)) {
        const s = statSync(p);
        out.set(rel, { size: s.size, mtime: s.mtimeMs });
      }
    }
  };
  walk(ws);
  return out;
}

// ---------- chat files (2026-10-28, docs/SPEC-chat-files.md) ----------
// The chat scratch on Drive is the one live copy of every chat file; the local scratch is a cache. Before a job the cache
// takes the Drive files whose md5 differs (another host or the user changed them); after the job every changed file is
// written back into the same Drive file, which gives it a new revision (= version).

const SYNC = { maxFiles: 300, maxBytes: 200 * 1024 * 1024 };
const scratchParts = (job) => ['projects', job.projectId, 'chats', job.chatId, 'scratch'];

async function scratchFolder(job, create) {
  return create ? drive.ensurePath(root, scratchParts(job)) : drive.resolvePath(root, scratchParts(job));
}

// A local path under the workspace whose existing components are plain directories / files (no symlink is followed).
function safeLocal(ws, rel) {
  let p = ws;
  for (const part of rel.split('/')) {
    if (!part || part === '.' || part === '..') throw new Error(`unsafe path: ${rel}`);
    p = join(p, part);
    if (existsSync(p) && lstatSync(p).isSymbolicLink()) throw new Error(`symlink in the workspace path: ${rel}`);
  }
  return p;
}

// <chat>/sync.json (next to the scratch): rel -> md5 of the files the cache took from Drive or wrote there. A file that
// left the Drive scratch is removed from the cache when the cached copy is still that content.
const manifestPath = (ws) => join(dirname(ws), 'sync.json');
function readManifest(ws) {
  try {
    return new Map(Object.entries(JSON.parse(readFileSync(manifestPath(ws), 'utf8'))));
  } catch {
    return null; // no manifest yet (cache from before 1.1.0): Drive wins
  }
}
function writeManifest(ws, m) {
  writeFileSync(manifestPath(ws), JSON.stringify(Object.fromEntries(m)));
}

// Returns rel -> md5 of the Drive files as they were before the job (the base for the conflict check of uploadLive).
async function syncDown(job, ws, ev) {
  const base = new Map();
  const manifest = readManifest(ws);
  const known = manifest || new Map();
  const top = await scratchFolder(job, false);
  if (!top) return base;
  let files = 0;
  let bytes = 0;
  const walk = async (id, rel) => {
    for (const f of await drive.children(id)) {
      const r = rel ? `${rel}/${f.name}` : f.name;
      if (f.mimeType === drive.FOLDER_MIME) {
        if (!EXCLUDED_DIRS.has(f.name) && f.name !== '.versions') await walk(f.id, r);
        continue;
      }
      if (!f.md5Checksum || EXCLUDED_FILE.test(f.name)) continue; // Google Docs have no content to cache
      base.set(r, f.md5Checksum);
      const local = safeLocal(ws, r);
      const have = existsSync(local) && lstatSync(local).isFile() ? md5(readFileSync(local)) : null;
      if (have === f.md5Checksum) continue;
      if (have && !manifest) {
        // First sync of a cache from before 1.1.0: the local bytes are kept outside the scratch (<chat>/pre-sync/).
        const keep = join(dirname(ws), 'pre-sync', ...r.split('/'));
        mkdirSync(dirname(keep), { recursive: true });
        writeFileSync(keep, readFileSync(local));
      }
      if (have && manifest && have !== known.get(r)) {
        // A local edit that never reached Drive (a failed upload): kept next to the Drive version and uploaded as its own file.
        const dot = f.name.lastIndexOf('.');
        const keepRel = (rel ? `${rel}/` : '') + (dot > 0 ? `${f.name.slice(0, dot)} (unsent)${f.name.slice(dot)}` : `${f.name} (unsent)`);
        writeFileSync(safeLocal(ws, keepRel), readFileSync(local));
        await uploadLive(job, ws, keepRel, 'auto', null);
        ev.emit('bridge', 'workspace_conflict', `${r}: the unsent local edit is kept as ${keepRel}`);
      }
      if (files >= SYNC.maxFiles || bytes + Number(f.size || 0) > SYNC.maxBytes) {
        // Not cached: an old local copy is removed when it is what the cache took earlier (else kept: an unsent edit),
        // and uploadLive will not overwrite the Drive file with it.
        if (have && manifest && have === known.get(r)) rmSync(local);
        base.set(r, `stale:${f.md5Checksum}`);
        continue;
      }
      const data = await drive.getBytes(f.id);
      mkdirSync(dirname(local), { recursive: true });
      writeFileSync(local, data);
      files++;
      bytes += data.length;
    }
  };
  await walk(top, '');
  let removed = 0;
  for (const [rel, sum] of known) {
    if (base.has(rel)) continue;
    try {
      const local = safeLocal(ws, rel);
      if (existsSync(local) && lstatSync(local).isFile() && md5(readFileSync(local)) === sum) {
        rmSync(local);
        removed++;
      }
    } catch {
      /* unsafe or unreadable: left alone */
    }
  }
  const next = new Map();
  for (const [rel, sum] of base) if (!sum.startsWith('stale:')) next.set(rel, sum);
  writeManifest(ws, next);
  if (files || removed) ev.emit('bridge', 'workspace_synced', `${files} file(s) from Drive${removed ? `, ${removed} removed` : ''}`);
  return base;
}

function noteUploaded(ws, rel, sum) {
  const m = readManifest(ws) || new Map();
  m.set(rel, sum);
  writeManifest(ws, m);
}

// Writes a workspace file into the same Drive file (a new revision). When the Drive file changed after the job started
// (another executor or the user), the result goes into "<name> (site).<ext>" next to it instead of overwriting.
async function uploadLive(job, ws, rel, origin, base) {
  const data = readFileSync(safeLocal(ws, rel));
  const parts = rel.split('/');
  let name = parts.pop();
  const dir = await drive.ensurePath(await scratchFolder(job, true), parts);
  const cur = await drive.findChild(dir, name);
  let f;
  const was = base?.get(rel);
  const changed = cur && cur.md5Checksum !== md5(data) && (was === undefined ? !!base : was !== cur.md5Checksum);
  if (cur && changed) {
    const dot = name.lastIndexOf('.');
    name = dot > 0 ? `${name.slice(0, dot)} (site)${name.slice(dot)}` : `${name} (site)`;
    f = await drive.upsertFile(dir, name, data);
    rel = [...parts, name].join('/');
  } else {
    f = cur ? await drive.updateContent(cur.id, data) : await drive.createFile(dir, name, data);
  }
  noteUploaded(ws, rel, md5(data));
  return {
    sourceRelativePath: rel, driveRelativePath: [...scratchParts(job), rel].join('/'), sizeBytes: data.length,
    sha256: sha256(data), md5: f?.md5Checksum || md5(data), origin, ref: true,
  };
}

// ---------- running one job ----------

const running = new Map(); // jobId -> { cancel: () => void, cancelled: boolean }
const cancelSeen = new Set();

function runProcess(job, ws, prompt, ev, st, timeoutMin, ctl, relay) {
  return new Promise((resolve) => {
    const [cmd, args] = commandLine(job, relay);
    const env = agentEnv(job.agent);
    if (RELAY_URL && existsSync(RELAY_KEY_FILE)) {
      env.AIWB_DB_URL = RELAY_URL;
      env.AIWB_DB_KEY_FILE = RELAY_KEY_FILE;
    }
    env.AIWB_CHAT_ID = job.chatId || '';
    if (relay) {
      env.MCP_TOOL_TIMEOUT = '2460000'; // a device call waits up to 40 min for a confirmation on the device
      env.MAX_MCP_OUTPUT_TOKENS = '25000';
    }
    const child = spawn(cmd, args, { cwd: ws, env, detached: true, stdio: ['pipe', 'pipe', 'pipe'] });
    const stdout = [];
    const stderr = [];
    let timedOut = false;
    let exitCode = null;
    let drained = 0;
    const kill = () => {
      try {
        process.kill(-child.pid, 'SIGTERM');
        setTimeout(() => {
          try {
            process.kill(-child.pid, 'SIGKILL');
          } catch {
            /* gone */
          }
        }, 5000);
      } catch {
        /* gone */
      }
    };
    ctl.cancel = () => {
      ev.emit('bridge', 'cancel_requested', 'Cancellation requested; terminating process tree.');
      kill();
    };
    const timer = setTimeout(() => {
      timedOut = true;
      ev.emit('bridge', 'timeout', `Agent exceeded timeout of ${timeoutMin} minutes.`);
      kill();
    }, timeoutMin * 60000);
    const lines = (stream, sink, type) => {
      let buf = '';
      stream.setEncoding('utf8');
      stream.on('data', (d) => {
        buf += d;
        let i;
        while ((i = buf.indexOf('\n')) >= 0) {
          const line = buf.slice(0, i).replace(/\r$/, '');
          buf = buf.slice(i + 1);
          sink.push(line);
          if (type === 'stdout') inspectLine(job, st, line);
          if (line.trim() || type === 'stdout') ev.emit('agent', type, line);
        }
      });
      stream.on('end', () => {
        if (buf) {
          sink.push(buf);
          if (type === 'stdout') inspectLine(job, st, buf);
          ev.emit('agent', type, buf);
        }
        if (++drained === 2 && exitCode !== null) finish();
      });
    };
    lines(child.stdout, stdout, 'stdout');
    lines(child.stderr, stderr, 'stderr');
    let done = false;
    const finish = (notDrained = false) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      resolve({ exitCode: timedOut ? -2 : exitCode, timedOut, outputDrained: !notDrained, stdout: stdout.join('\n'), stderr: stderr.join('\n') });
    };
    child.on('error', (e) => {
      stderr.push(String(e));
      exitCode = -1;
      finish();
    });
    child.on('exit', (code, signal) => {
      exitCode = code ?? (signal ? 128 : -1);
      if (drained === 2) finish();
      else setTimeout(() => {
        if (!done) {
          ev.emit('bridge', 'output_drain_timeout', `Agent output was not drained within ${DRAIN_MS / 1000} seconds.`);
          finish(true);
        }
      }, DRAIN_MS);
    });
    child.stdin.on('error', () => undefined);
    child.stdin.end(prompt.endsWith('\n') ? prompt : `${prompt}\n`);
  });
}

async function processJob(jobId, folderId) {
  const ctl = { cancel: null, cancelled: cancelSeen.has(jobId) };
  running.set(jobId, ctl);
  const ev = new EventWriter(jobId);
  ev.emit('bridge', 'claimed', 'Job claimed by BridgeAgent.');
  const startedUtc = nowUtc();
  let req = null;
  let job = null;
  let result;
  let stdoutLog = '';
  let stderrLog = '';
  let ws = null;
  let staged = null;
  let relay = null;
  try {
    const files = await drive.children(folderId);
    const reqFile = files.find((f) => f.name === 'request.json');
    req = JSON.parse((await drive.getBytes(reqFile.id)).toString('utf8').replace(/^﻿/, ''));
    job = validate(req, jobId);
    ws = join(WORK, job.projectId, 'chats', job.chatId, 'scratch');
    mkdirSync(ws, { recursive: true });
    // The Drive scratch is the live copy: without a successful sync the job would work on old files, so it fails.
    let base = null;
    if (!ctl.cancelled) {
      try {
        base = await syncDown(job, ws, ev);
      } catch (e) {
        ev.emit('bridge', 'workspace_sync_failed', e.message);
        throw new Error(`Chat files could not be read from Drive: ${e.message}`);
      }
    }
    const timeoutMin = req.timeoutMinutes > 0 ? Math.min(req.timeoutMinutes, MAX_TIMEOUT_MIN) : Math.min(MODE_TIMEOUT_MIN[job.mode], MAX_TIMEOUT_MIN);

    // Attachments: staged into the workspace, verified, removed after the job.
    let prompt = job.instruction;
    if (job.attachments.length) {
      const attRoot = files.find((f) => f.name === 'attachments');
      const lines = [];
      const chatLines = [];
      for (const a of job.attachments) {
        const parts = a.path.split('/');
        const name = parts.pop();
        const dir = attRoot && (await drive.resolvePath(attRoot.id, parts));
        const f = dir && (await drive.findChild(dir, name));
        if (!f) throw new Error(`Attachment not found: ${a.path}`);
        const data = await drive.getBytes(f.id);
        if (data.length !== a.sizeBytes || sha256(data) !== a.sha256) throw new Error(`Attachment does not match its hash: ${a.path}`);
        if (a.target) {
          // A chat file: written into the scratch (replacing the older version) and to the Drive scratch at once.
          const local = safeLocal(ws, a.target);
          const existed = existsSync(local);
          if (existed && md5(readFileSync(local)) === md5(data)) {
            chatLines.push(`- ${a.target} (unchanged, same as the current file)`);
            continue;
          }
          mkdirSync(dirname(local), { recursive: true });
          writeFileSync(local, data);
          await uploadLive(job, ws, a.target, 'attachment', null);
          base?.set(a.target, md5(data));
          ev.emit('bridge', 'attachment_staged', a.target);
          chatLines.push(`- ${a.target} (${existed ? 'new version from the user' : 'new file'})`);
          continue;
        }
        if (!staged) {
          staged = join(ws, '.co-attachments', jobId);
          rmSync(staged, { recursive: true, force: true });
        }
        const target = join(staged, ...a.path.split('/'));
        mkdirSync(dirname(target), { recursive: true });
        writeFileSync(target, data);
        ev.emit('bridge', 'attachment_staged', a.path);
        lines.push(`- .co-attachments/${jobId}/${a.path}`);
      }
      if (chatLines.length) prompt += `\n\nFiles the user attached are now files of this chat in the current workspace:\n${chatLines.join('\n')}\n`;
      if (lines.length) prompt += `\n\nAttached files are staged inside the current workspace under:\n.co-attachments/${jobId}/\nFiles:\n${lines.join('\n')}\n`;
    }

    const autoExport = job.output?.autoExport ?? job.policy.autoExport;
    let before = null;
    if (autoExport && !ctl.cancelled) {
      try {
        before = snapshot(ws);
      } catch (e) {
        ev.emit('bridge', 'auto_export_disabled', e.message);
      }
    }
    const baseline = job.agent === 'codex' && job.conversation.operation === 'resume' ? codexBaseline(job.conversation.sessionId) : null;

    ev.emit('bridge', 'started', `Starting agent '${job.agent}' in project '${job.projectId}' (mode ${job.mode}${job.model ? `, model ${job.model}` : ''}${job.effort ? `, effort ${job.effort}` : ''}).`);
    const st = newInspection();
    relay = ctl.cancelled ? null : await relaySetup(job, jobId, ev, timeoutMin);
    if (relay) prompt += relay.note;
    const run = ctl.cancelled ? { exitCode: -1, timedOut: false, outputDrained: true, stdout: '', stderr: '' } : await runProcess(job, ws, prompt, ev, st, timeoutMin, ctl, relay);
    stdoutLog = run.stdout;
    stderrLog = run.stderr;
    await flushLimits().catch(() => undefined);
    if (baseline && st.usage) {
      st.usage.inputTokens = Math.max(0, st.usage.inputTokens - (baseline.input_tokens || 0));
      st.usage.cachedInputTokens = Math.max(0, st.usage.cachedInputTokens - (baseline.cached_input_tokens || 0));
      st.usage.outputTokens = Math.max(0, st.usage.outputTokens - (baseline.output_tokens || 0));
      st.usage.reasoningOutputTokens = Math.max(0, st.usage.reasoningOutputTokens - (baseline.reasoning_output_tokens || 0));
    }

    // Declared artifacts fail the job when missing; auto-export problems are only reported.
    // A cancelled job exports nothing: its chat may be deleted, and ensurePath would recreate the folder.
    const artifacts = [];
    const missingArtifacts = [];
    const failedArtifacts = [];
    const exportSkipped = [];
    if (ctl.cancelled) before = null;
    for (const rel of ctl.cancelled ? [] : job.artifactPaths) {
      if (!existsSync(join(ws, rel)) || !lstatSync(join(ws, rel)).isFile()) {
        missingArtifacts.push(rel);
        ev.emit('bridge', 'artifact_missing', rel);
        continue;
      }
      try {
        const a = await uploadLive(job, ws, rel, 'declared', base);
        artifacts.push(a);
        ev.emit('bridge', 'artifact', a.driveRelativePath);
      } catch (e) {
        failedArtifacts.push({ path: rel, error: e.message });
        ev.emit('bridge', 'artifact_failed', `${rel}: ${e.message}`);
      }
    }
    if (before) {
      try {
        const after = snapshot(ws);
        const declared = new Set(job.artifactPaths);
        const changed = [...after.entries()]
          .filter(([rel, s]) => !declared.has(rel) && (!before.has(rel) || before.get(rel).size !== s.size || before.get(rel).mtime !== s.mtime))
          .sort((a, b) => b[1].mtime - a[1].mtime);
        const maxFiles = job.output?.maxFiles > 0 ? Math.min(job.output.maxFiles, EXPORT.maxFiles) : EXPORT.maxFiles;
        const maxBytes = job.output?.maxBytes > 0 ? Math.min(job.output.maxBytes, EXPORT.maxBytes) : EXPORT.maxBytes;
        let n = 0;
        let bytes = 0;
        for (const [rel, s] of changed) {
          let reason = null;
          if (s.size > EXPORT.maxFileBytes) reason = `file larger than ${EXPORT.maxFileBytes} bytes`;
          else if (n >= maxFiles || bytes + s.size > maxBytes) reason = `auto-export limit reached (${maxFiles} files / ${maxBytes} bytes)`;
          if (reason) {
            exportSkipped.push({ path: rel, reason });
            ev.emit('bridge', 'artifact_skipped', `${rel}: ${reason}`);
            continue;
          }
          try {
            const a = await uploadLive(job, ws, rel, 'auto', base);
            artifacts.push(a);
            n++;
            bytes += s.size;
            ev.emit('bridge', 'artifact', a.driveRelativePath);
          } catch (e) {
            exportSkipped.push({ path: rel, reason: e.message });
            ev.emit('bridge', 'artifact_skipped', `${rel}: ${e.message}`);
          }
        }
      } catch (e) {
        ev.emit('bridge', 'auto_export_disabled', e.message);
      }
    }

    if (run.timedOut || !run.outputDrained) st.success = false;
    // 1.2.1: Codex answers politely when its tools fail; without this the job looked completed while nothing was done.
    if (job.agent === 'codex' && CODEX_NO_TOOLS_RE.test(run.stderr || '')) {
      st.success = false;
      st.failure = 'Site host cannot run Codex tools (hosting thread limit, EAGAIN); send the message to Home';
    }
    const success = !ctl.cancelled && run.exitCode === 0 && !run.timedOut && run.outputDrained && st.finalSeen && st.success && !missingArtifacts.length && !failedArtifacts.length;
    const status = ctl.cancelled ? 'cancelled' : success ? 'completed' : 'failed';
    let error = null;
    if (status === 'cancelled') error = 'Cancellation requested by client.';
    else if (status === 'failed') {
      const parts = [];
      if (run.timedOut) parts.push('timeout');
      if (run.exitCode !== 0) parts.push(`exitCode=${run.exitCode}`);
      if (!run.outputDrained) parts.push('output not drained');
      if (!st.finalSeen) parts.push('structured final event missing');
      else if (!st.success) parts.push('structured result failed');
      if (st.errorEvents) parts.push(`agent error events: ${st.errorEvents}`);
      if (missingArtifacts.length) parts.push(`missing artifacts: ${missingArtifacts.join(', ')}`);
      if (failedArtifacts.length) parts.push(`artifact export failures: ${failedArtifacts.length}`);
      if (st.failure) parts.push(st.failure);
      else if (!st.finalSeen && run.stderr.trim()) parts.push(run.stderr.trim().split('\n').slice(-3).join(' | ').slice(0, 500));
      error = parts.join('; ') || 'Job did not satisfy completion criteria.';
    }
    result = {
      schemaVersion: 3, hostId: HOST_ID, jobId, environmentId: job.environmentId, projectId: job.projectId, chatId: job.chatId, branchId: job.branchId,
      agent: job.agent, conversationOperation: job.conversation.operation, status, startedUtc, completedUtc: nowUtc(), exitCode: run.exitCode,
      timedOut: run.timedOut, outputDrained: run.outputDrained, structuredFinalSeen: st.finalSeen, structuredSuccess: st.success,
      sessionId: st.sessionId, agentFinalResponse: st.final, agentErrorEvents: st.errorEvents, agentErrorMessages: st.errors, error,
      stdoutLog: 'stdout.log', stderrLog: 'stderr.log', artifacts, missingArtifacts, failedArtifacts, mode: job.mode, model: job.model,
      effort: job.effort, modelReported: st.modelReported, usage: st.usage, autoExport: !!autoExport, exportSkipped, tainted: false,
    };
    ev.emit('bridge', status, status === 'completed' ? 'Job completed successfully.' : status === 'cancelled' ? 'Job cancelled by remote request.' : error);
  } catch (e) {
    log('job failed', jobId, e.stack || String(e));
    ev.emit('bridge', 'failed', e.message);
    result = {
      schemaVersion: 3, hostId: HOST_ID, jobId, environmentId: req?.environmentId ?? null, projectId: req?.projectId ?? null, chatId: req?.chatId ?? null,
      branchId: req?.branchId ?? null, agent: req?.agent ?? null, conversationOperation: req?.conversation?.operation ?? 'new', status: 'failed',
      startedUtc: null, completedUtc: nowUtc(), exitCode: -1, timedOut: false, outputDrained: false, structuredFinalSeen: false, structuredSuccess: false,
      sessionId: null, agentFinalResponse: null, agentErrorEvents: 0, agentErrorMessages: [], error: e.message, stdoutLog: null, stderrLog: null,
      artifacts: [], missingArtifacts: [], failedArtifacts: [], mode: req?.mode ?? null, model: req?.model ?? null, effort: req?.effort ?? null,
      modelReported: null, usage: null, autoExport: false, exportSkipped: [], tainted: false,
    };
  } finally {
    if (staged) rmSync(staged, { recursive: true, force: true });
    if (relay) {
      rmSync(relay.dir, { recursive: true, force: true });
      // calls the Bridge has not taken yet must not run after the job ended (cancelled, timed out)
      await relayDb('relay_cancel', { jobId: relay.jobId }).catch((e) => log('relay_cancel failed', jobId, String(e)));
    }
  }
  // The slot is freed even when publishing fails (e.g. the job folder was trashed with its chat).
  try {
    await ev.close();
    try {
      if (stdoutLog) await drive.createFile(folderId, 'stdout.log', stdoutLog, 'text/plain');
      if (stderrLog) await drive.createFile(folderId, 'stderr.log', stderrLog, 'text/plain');
    } catch (e) {
      log('log copy failed', jobId, String(e));
    }
    // 1.6.0: the result goes to the database first (answer in the chat now); drive-sync sees it already finalized
    if (req && dbQueue()) {
      for (let i = 1; i <= 3; i++) {
        try {
          await relayDb('job_result', { host: HOST_ID, request: req, result }, 30000, true);
          break;
        } catch (e) {
          log('job_result failed', jobId, `try ${i}`, String(e));
          if (i < 3) await new Promise((r) => setTimeout(r, 5000 * i));
        }
      }
    }
    const bytes = Buffer.from(JSON.stringify(result));
    await drive.createFile(folderId, 'result.json', bytes, 'application/json');
    await drive.createFile(folderId, 'result.ready', JSON.stringify({ schemaVersion: 2, payload: 'result.json', sizeBytes: bytes.length, sha256: sha256(bytes) }), 'application/json');
    await drive.moveFile(folderId, buckets.claimed, result.status === 'completed' ? buckets.completed : buckets.failed);
    const marker = await drive.findChild(buckets.cancel, `${jobId}.cancel`).catch(() => null);
    if (marker) await drive.trashFile(marker.id).catch(() => undefined);
    log('job done', jobId, result.status);
  } finally {
    running.delete(jobId);
  }
}

// ---------- inbox scan ----------

const usedFile = join(STATE, 'used-jobids.log');
const used = new Set(existsSync(usedFile) ? readFileSync(usedFile, 'utf8').split('\n').filter(Boolean) : []);

async function reject(folder, reason) {
  await drive.createFile(folder.id, 'rejection.json', JSON.stringify({ schemaVersion: 2, hostId: HOST_ID, createdUtc: nowUtc(), reason }), 'application/json');
  await drive.moveFile(folder.id, buckets.inbox, buckets.rejected);
  log('rejected', folder.name, reason);
}

async function verifyReady(files) {
  const reqFile = files.find((f) => f.name === 'request.json');
  const ready = files.find((f) => f.name === 'request.ready');
  if (!reqFile || !ready) return false;
  try {
    const m = JSON.parse((await drive.getBytes(ready.id)).toString('utf8').replace(/^﻿/, ''));
    if (m.schemaVersion !== 2 || m.payload !== 'request.json') return false;
    const bytes = await drive.getBytes(reqFile.id);
    return bytes.length === m.sizeBytes && sha256(bytes) === String(m.sha256).toLowerCase();
  } catch {
    return false;
  }
}

async function scanCancels() {
  for (const f of await drive.children(buckets.cancel)) {
    if (!f.name.endsWith('.cancel')) continue;
    const jobId = f.name.slice(0, -7);
    if (!SAFE_ID.test(jobId)) {
      await drive.trashFile(f.id).catch(() => undefined);
      continue;
    }
    if (cancelSeen.has(jobId)) continue;
    cancelSeen.add(jobId);
    const r = running.get(jobId);
    if (r) {
      r.cancelled = true;
      r.cancel?.();
    }
  }
}

async function scanInbox() {
  const folders = (await drive.children(buckets.inbox)).filter((f) => f.mimeType === drive.FOLDER_MIME).sort((a, b) => (a.name < b.name ? -1 : 1));
  for (const f of folders) {
    if (running.has(f.name)) continue;
    if (!SAFE_ID.test(f.name)) {
      await reject(f, 'jobId must be 1-40 ASCII characters: letters, digits, \'_\' or \'-\'.');
      continue;
    }
    const files = await drive.children(f.id);
    if (!(await verifyReady(files))) continue;
    if (running.size >= MAX_PARALLEL) return;
    if (used.has(f.name)) {
      await reject(f, 'Duplicate jobId. A job with this ID already exists in host history.');
      continue;
    }
    // 1.6.0: a job the database knows as cancelled or finished is not run (copy of the queue fallback, cancelled meanwhile)
    if (dbQueue()) {
      const dc = await relayDb('job_drive_claim', { host: HOST_ID, jobId: f.name }).catch((e) => {
        log('job_drive_claim failed', f.name, String(e));
        return null;
      });
      if (dc?.action === 'skip') {
        await reject(f, `The job is ${dc.state || 'withdrawn'} in the database queue.`);
        continue;
      }
    }
    await drive.moveFile(f.id, buckets.inbox, buckets.claimed);
    used.add(f.name);
    appendFileSync(usedFile, `${f.name}\n`);
    log('claimed', f.name);
    void processJob(f.name, f.id).catch((e) => log('processJob crashed', f.name, e.stack || String(e)));
  }
}

// 1.6.0 (S7): jobs queued in the database for this host. The job gets the same Drive folder a fallback copy would have
// (claimed/<jobId> with request.json, marked db-fallback), so the rest of the job runs as before. job_claim also lists
// running jobs whose cancellation was requested and records the poll (no Drive fallback copy while this agent polls).
async function claimDb() {
  if (!dbQueue()) return;
  const free = Math.max(0, MAX_PARALLEL - running.size);
  const r = await relayDb('job_claim', { host: HOST_ID, max: free });
  for (const jobId of Array.isArray(r.cancel) ? r.cancel : []) {
    const c = running.get(jobId);
    if (c && !c.cancelled) {
      cancelSeen.add(jobId);
      c.cancelled = true;
      c.cancel?.();
      log('cancel (db)', jobId);
    }
  }
  for (const j of Array.isArray(r.jobs) ? r.jobs : []) {
    const jobId = String(j.jobId || '');
    // a claimed job without job_events comes back after the lease (120 s): already ours, nothing to do
    if (!SAFE_ID.test(jobId) || running.has(jobId) || used.has(jobId)) continue;
    const bytes = Buffer.from(String(j.request || ''), 'utf8');
    const pkg = await drive.ensureFolder(buckets.claimed, jobId);
    await drive.createFile(pkg, 'db-fallback', '{}', 'application/json');
    await drive.createFile(pkg, 'request.json', bytes, 'application/json');
    await drive.createFile(pkg, 'request.ready', JSON.stringify({ schemaVersion: 2, payload: 'request.json', sizeBytes: bytes.length, sha256: sha256(bytes) }), 'application/json');
    used.add(jobId);
    appendFileSync(usedFile, `${jobId}\n`);
    log('claimed (db)', jobId);
    void processJob(jobId, pkg).catch((e) => log('processJob crashed', jobId, e.stack || String(e)));
  }
}

// Jobs left in claimed by a previous process get a failed result (Bridge startup recovery).
async function recoverClaimed() {
  for (const f of await drive.children(buckets.claimed)) {
    if (f.mimeType !== drive.FOLDER_MIME || running.has(f.name)) continue;
    const files = await drive.children(f.id);
    if (!files.some((x) => x.name === 'result.ready')) {
      const result = {
        schemaVersion: 2, hostId: HOST_ID, jobId: f.name, status: 'failed', exitCode: -3, completedUtc: nowUtc(),
        error: 'BridgeAgent restarted while this job was in claimed state.', agentErrorMessages: [], artifacts: [], missingArtifacts: [],
      };
      const bytes = Buffer.from(JSON.stringify(result));
      await drive.createFile(f.id, 'result.json', bytes, 'application/json');
      await drive.createFile(f.id, 'result.ready', JSON.stringify({ schemaVersion: 2, payload: 'result.json', sizeBytes: bytes.length, sha256: sha256(bytes) }), 'application/json');
    }
    await drive.moveFile(f.id, buckets.claimed, buckets.failed);
    log('recovered', f.name);
  }
}

// ---------- main loop ----------

async function main() {
  log(`agent ${VERSION} starting, pid ${process.pid}`);
  while (!drive.hasSession()) await sleep(10000);
  for (;;) {
    try {
      await discover();
      break;
    } catch (e) {
      log('discover failed', String(e));
      await sleep(30000);
    }
  }
  await recoverClaimed().catch((e) => log('recover failed', String(e)));
  let lastOwner = 0;
  let lastLimits = 0;
  let failures = 0;
  let lastDbClaim = 0;
  for (;;) {
    try {
      if (Date.now() - lastOwner >= OWNER_MS) {
        await writeOwner();
        lastOwner = Date.now();
      }
      if (Date.now() - lastLimits >= LIMITS_MS) {
        codexLimits();
        await flushLimits();
        lastLimits = Date.now();
      }
      await scanCancels();
      if (Date.now() - lastDbClaim >= DB_CLAIM_MS) {
        lastDbClaim = Date.now();
        await claimDb().catch((e) => log('db claim failed', String(e)));
      }
      await scanInbox();
      failures = 0;
    } catch (e) {
      failures++;
      log('loop error', e instanceof drive.AuthError ? e.message : e.stack || String(e));
      await sleep(Math.min(60000, 2000 * failures));
    }
    await sleep(SCAN_MS);
  }
}

process.on('unhandledRejection', (e) => log('unhandled', String(e?.stack || e)));
process.on('SIGTERM', () => {
  for (const r of running.values()) r.cancel?.();
  setTimeout(() => process.exit(0), 3000);
});
void main();
