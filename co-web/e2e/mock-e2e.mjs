// SPDX-License-Identifier: AGPL-3.0-or-later
// End-to-end test of the CO web client against an in-memory Drive mock.
// Simulates the Bridge side (claim, events, result + commit marker) between steps.
import { chromium } from 'playwright';
import crypto from 'node:crypto';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const DIST = path.resolve('dist');
const sha = (b) => crypto.createHash('sha256').update(b).digest('hex');

// ---------- static server with SPA fallback ----------
const server = http.createServer((req, res) => {
  let p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  let f = path.join(DIST, p);
  if (!f.startsWith(DIST) || !fs.existsSync(f) || fs.statSync(f).isDirectory()) f = path.join(DIST, 'index.html');
  const ext = path.extname(f);
  const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.webmanifest': 'application/manifest+json' };
  res.writeHead(200, { 'Content-Type': types[ext] || 'application/octet-stream' });
  res.end(fs.readFileSync(f));
});
await new Promise((r) => server.listen(5199, r));

// ---------- in-memory Drive ----------
const FOLDER = 'application/vnd.google-apps.folder';
let seq = 0;
const files = new Map(); // id -> {id,name,mimeType,parents,content:Buffer,createdTime,modifiedTime}
function add(name, parent, content = null, mime = null) {
  const id = 'F' + ++seq;
  const t = new Date(Date.now() + seq).toISOString();
  files.set(id, { id, name, mimeType: mime || (content === null ? FOLDER : 'application/json'), parents: parent ? [parent] : [], content: content === null ? null : Buffer.from(content), createdTime: t, modifiedTime: t, trashed: false });
  return id;
}
const child = (parent, name) => [...files.values()].find((f) => f.parents.includes(parent) && f.name === name && !f.trashed);
const mkpath = (root, parts) => parts.reduce((id, p) => child(id, p)?.id || add(p, id), root);

const ROOT = add('AI-Workbench', 'myroot');
const DOCS = add('Documents', 'myroot');
const HOST = mkpath(ROOT, ['transport', 'hosts', 'home-main']);
const B = {};
for (const b of ['inbox', 'claimed', 'events', 'completed', 'failed', 'rejected', 'cancel']) B[b] = add(b, HOST);
add('owner.json', HOST, JSON.stringify({ schemaVersion: 2, hostId: 'home-main', machineName: 'HOME-PC', updatedUtc: new Date().toISOString(), bridgeVersion: '0.5.1', capabilities: ['protocol-v3', 'conversation-resume', 'codex-fork', 'attachments', 'cancel', 'modes', 'model-select', 'effort-select', 'auto-export', 'usage', 'project-catalog', 'context-folders'],
  protocolMinor: 1,
  agents: {
    claude: { displayName: 'Claude', modes: ['chat', 'cowork'], models: [{ id: 'opus', label: 'Opus (latest)' }, { id: 'sonnet', label: 'Sonnet (latest)' }], efforts: ['low', 'medium', 'high'], defaultModel: null, defaultEffort: null },
    codex: { displayName: 'Codex', modes: ['chat', 'cowork'], models: [{ id: 'gpt-6-luna', label: 'GPT-6-Luna', efforts: ['low', 'medium'] }], efforts: ['low', 'medium', 'high'], defaultModel: 'gpt-6-luna', defaultEffort: 'medium' },
  },
  contextRoots: { home: { label: 'PC — home', kind: 'local' }, gdrive: { label: 'Google Drive — My Drive', kind: 'drive' } },
  projects: { 'bridge-test': { environmentId: 'owner', allowedAgents: ['codex', 'claude'], allowedModes: ['chat', 'cowork'], autoExport: true, workspaceDriveRelativePath: 'projects/bridge-test/workspace' } },
}));
mkpath(ROOT, ['projects', 'bridge-test', 'workspace']);
add('notes.md', child(child(child(ROOT, 'projects').id, 'bridge-test').id, 'workspace').id, '# notes\n', 'text/markdown');
mkpath(ROOT, ['projects', 'bridge-test', 'chats']);
add('limits.json', HOST, JSON.stringify({ schemaVersion: 1, updatedUtc: new Date().toISOString(), agents: {
  codex: { source: 'codex-session-log', observedUtc: new Date().toISOString(), plan: 'plus', windows: [
    { id: 'session', key: 'primary', windowMinutes: 300, usedPercent: 25, resetsAtUtc: new Date(Date.now() + 3600e3).toISOString() },
    { id: 'week', key: 'secondary', windowMinutes: 10080, usedPercent: 81, resetsAtUtc: new Date(Date.now() + 3 * 86400e3).toISOString() }] },
  claude: { source: 'claude-code', observedUtc: new Date().toISOString(), windows: [
    { id: 'session', key: 'five_hour', windowMinutes: 300, usedPercent: 6, resetsAtUtc: new Date(Date.now() + 7200e3).toISOString() },
    { id: 'week', key: 'seven_day', windowMinutes: 10080, usedPercent: 3, resetsAtUtc: new Date(Date.now() + 5 * 86400e3).toISOString() }] } } }));
const FOLDERS = mkpath(HOST, ['folders']);
mkpath(FOLDERS, ['requests']);
mkpath(FOLDERS, ['results']);
add('home.json', FOLDERS, JSON.stringify({ rootId: 'home', relativePath: '', generatedUtc: new Date().toISOString(), depth: 3, truncated: false,
  tree: { n: '', c: [{ n: 'Documents', c: [{ n: 'Acme', c: [], m: true }] }, { n: 'Downloads', c: [] }] } }));
{
  const second = mkpath(ROOT, ['projects', 'second']);
  add('project.json', second, JSON.stringify({ projectId: 'second', environmentId: 'owner', title: 'Second project', defaultAgentId: 'claude', createdUtc: '2026-01-01T00:00:00Z', updatedUtc: '2026-01-01T00:00:00Z', archived: false }));
}

function evalQuery(q) {
  // Supports the query shapes used by the client.
  const conds = [];
  const re = /'((?:[^'\\]|\\.)*)' in parents|name='((?:[^'\\]|\\.)*)'|mimeType='([^']*)'|trashed=false/g;
  let m;
  while ((m = re.exec(q))) {
    if (m[1] !== undefined) { let v = m[1].replace(/\\(.)/g, '$1'); if (v === 'root') v = 'myroot'; conds.push((f) => f.parents.includes(v)); }
    else if (m[2] !== undefined) { const v = m[2].replace(/\\(.)/g, '$1'); conds.push((f) => f.name === v); }
    else if (m[3] !== undefined) { const v = m[3]; conds.push((f) => f.mimeType === v); }
    else conds.push((f) => !f.trashed);
  }
  return [...files.values()].filter((f) => conds.every((c) => c(f)));
}
const meta = (f) => ({ id: f.id, name: f.name, mimeType: f.mimeType, parents: f.parents, size: f.content ? String(f.content.length) : undefined, createdTime: f.createdTime, modifiedTime: f.modifiedTime });

function parseMultipart(body, ctype) {
  const boundary = /boundary=(.+)$/.exec(ctype)[1];
  const buf = Buffer.from(body);
  const sep = Buffer.from(`--${boundary}`);
  const parts = [];
  let idx = buf.indexOf(sep);
  while (idx >= 0) {
    const next = buf.indexOf(sep, idx + sep.length);
    if (next < 0) break;
    const part = buf.subarray(idx + sep.length + 2, next - 2);
    const hEnd = part.indexOf('\r\n\r\n');
    parts.push(part.subarray(hEnd + 4));
    idx = next;
  }
  return { meta: JSON.parse(parts[0].toString()), data: parts[1] };
}

const log = [];
async function driveRoute(route) {
  const req = route.request();
  const url = new URL(req.url());
  const method = req.method();
  const json = (o, s = 200) => route.fulfill({ status: s, contentType: 'application/json', body: JSON.stringify(o) });
  if (url.pathname === '/oauth2/v3/userinfo') return json({ email: 'owner@example.org' });
  let m;
  if (url.pathname === '/drive/v3/files' && method === 'GET') {
    return json({ files: evalQuery(url.searchParams.get('q')).map(meta) });
  }
  if (url.pathname === '/drive/v3/files' && method === 'POST') {
    const b = JSON.parse(req.postData());
    const id = add(b.name, b.parents[0], null, b.mimeType);
    log.push(`mkdir ${b.name}`);
    return json({ id });
  }
  if (url.pathname === '/upload/drive/v3/files' && method === 'POST') {
    const { meta: md, data } = parseMultipart(req.postDataBuffer(), req.headers()['content-type']);
    const id = add(md.name, md.parents[0], data, md.mimeType);
    log.push(`create ${md.name} (${data.length} B)`);
    return json(meta(files.get(id)));
  }
  if ((m = /^\/upload\/drive\/v3\/files\/([^/]+)$/.exec(url.pathname)) && method === 'PATCH') {
    const f = files.get(m[1]);
    f.content = req.postDataBuffer();
    return json(meta(f));
  }
  if ((m = /^\/drive\/v3\/files\/([^/]+)\/copy$/.exec(url.pathname))) {
    const src = files.get(m[1]);
    const b = JSON.parse(req.postData());
    const id = add(b.name, b.parents[0], src.content, src.mimeType);
    log.push(`copy ${b.name}`);
    return json(meta(files.get(id)));
  }
  if ((m = /^\/drive\/v3\/files\/([^/]+)$/.exec(url.pathname)) && method === 'PATCH') {
    const f = files.get(m[1]);
    const add = url.searchParams.get('addParents'), rem = url.searchParams.get('removeParents');
    if (add) f.parents = [...f.parents.filter((x) => x !== rem), add];
    const b = req.postData() ? JSON.parse(req.postData()) : {};
    if (b.trashed) f.trashed = true;
    log.push(`patch ${f.name}`);
    return json(meta(f));
  }
  if ((m = /^\/drive\/v3\/files\/([^/]+)$/.exec(url.pathname)) && method === 'GET') {
    const f = files.get(m[1]);
    if (!f) return json({ error: { message: 'not found' } }, 404);
    if (url.searchParams.get('alt') === 'media') return route.fulfill({ status: 200, body: f.content || Buffer.alloc(0) });
    return json(meta(f));
  }
  return json({ error: { message: `unhandled ${method} ${url.pathname}` } }, 400);
}

const GIS_STUB = `window.google = { accounts: { oauth2: {
  initTokenClient: (cfg) => ({ requestAccessToken: () => setTimeout(() => cfg.callback({ access_token: 'tok', expires_in: 3599, scope: cfg.scope }), 10) }),
  revoke: (t, cb) => cb && cb() } } };`;

// ---------- Bridge simulation ----------
function bridgeClaimAndRun(jobId, { finalText, sessionId, artifactName }) {
  const job = child(B.inbox, jobId);
  job.parents = [B.claimed];
  const evDir = add(jobId, B.events);
  add('000001-000003.json', evDir, JSON.stringify({ schemaVersion: 2, firstSequence: 1, lastSequence: 3, events: [
    { schemaVersion: 2, sequence: 1, createdUtc: new Date().toISOString(), source: 'bridge', type: 'claimed', content: 'Job claimed by BridgeAgent.' },
    { schemaVersion: 2, sequence: 2, createdUtc: new Date().toISOString(), source: 'bridge', type: 'started', content: 'Starting agent' },
    { schemaVersion: 2, sequence: 3, createdUtc: new Date().toISOString(), source: 'agent', type: 'stdout', content: JSON.stringify({ type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Read', input: { file_path: '.co-attachments/x/привет.txt' } }] } }) },
  ] }));
  return () => {
    const artifacts = [];
    if (artifactName) {
      const content = Buffer.from('artifact-content\n');
      const dir = mkpath(ROOT, ['projects', 'bridge-test', 'chats', 'CHATX', 'branches', 'BR-root', 'artifacts', jobId]);
      add(artifactName, dir, content, 'text/plain');
      artifacts.push({ sourceRelativePath: artifactName, driveRelativePath: `projects/bridge-test/chats/CHATX/branches/BR-root/artifacts/${jobId}/${artifactName}`, sizeBytes: content.length, sha256: sha(content) });
    }
    const result = Buffer.from(JSON.stringify({ schemaVersion: 3, jobId, agent: 'claude', status: 'completed', sessionId, agentFinalResponse: finalText, artifacts, missingArtifacts: [], error: null, completedUtc: new Date().toISOString(),
      mode: 'cowork', model: 'opus', effort: 'high', modelReported: 'claude-opus-5', usage: { inputTokens: 10, cachedInputTokens: 1000, cacheCreationInputTokens: 0, outputTokens: 50, costUsd: 0.0123, durationMs: 4200, numTurns: 2 }, exportSkipped: [] }));
    add('result.json', job.id, result);
    add('result.ready', job.id, JSON.stringify({ schemaVersion: 2, payload: 'result.json', sizeBytes: result.length, sha256: sha(result) }));
    job.parents = [B.completed];
  };
}

// ---------- run ----------
const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium' });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
const consoleErrors = [];
page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
page.on('pageerror', (e) => consoleErrors.push(String(e)));
await page.route('https://accounts.google.com/**', (r) => r.fulfill({ contentType: 'text/javascript', body: GIS_STUB }));
await page.route('https://www.googleapis.com/**', driveRoute);
// Server-side sign-in (co-web-auth): the session cookie is simulated by a flag set on /auth/login.php.
let signedIn = false;
await page.route('**/auth/login.php*', (r) => { signedIn = true; return r.fulfill({ contentType: 'text/html', body: '<script>location.replace("/")</script>' }); });
await page.route('**/auth/token.php', (r) => signedIn
  ? r.fulfill({ contentType: 'application/json', body: JSON.stringify({ access_token: 'T', expires_in: 3600, email: 'owner@example.org' }) })
  : r.fulfill({ status: 401, body: '' }));
// Freedom F1: members of the workbench (db.php op members); other db.php ops go on to the preview server (Drive fallback).
await page.route('**/agent/db.php', (r) => (JSON.parse(r.request().postData() || '{}').op === 'members'
  ? r.fulfill({ contentType: 'application/json', body: JSON.stringify({ members: [{ env: 'owner', email: 'owner@example.org', name: 'Owner', color: '#1f5fbf', role: 'owner' }] }) })
  : r.continue()));
await page.route('**/agent/ping.php', (r) => r.fulfill({ contentType: 'application/json', body: '{}' }));
const signIn = async () => {
  const b = page.getByRole('button', { name: 'Sign in with Google' });
  await b.or(page.locator('.host-switch')).first().waitFor({ timeout: 10000 });
  if (await b.isVisible()) await b.click();
};

const fail = (msg) => { console.error('FAIL:', msg); process.exitCode = 1; };
const ok = (msg) => console.log('OK:', msg);

await page.goto('http://localhost:5199/');
await signIn();
await page.getByText('Home · online').waitFor({ timeout: 10000 });
ok('signed in, host online, env badge: ' + (await page.locator('.env-badge').innerText()));
await page.locator('.lim-agent').first().waitFor({ timeout: 8000 });
const limNames = await page.locator('.lim-name').allInnerTexts();
const widths = await page.locator('.lim-fill').evaluateAll((els) => els.map((e) => e.style.width));
if (limNames.join(',') !== 'GPT,Claude' || widths.join(',') !== '25%,81%,6%,3%') fail('limits bar: ' + limNames + ' ' + widths);
else ok('usage limit bars: ' + limNames.join(', ') + ' → ' + widths.join(' '));
await page.screenshot({ path: 'e2e/topbar.png', clip: { x: 0, y: 0, width: 1400, height: 60 } });

await page.getByRole('button', { name: /bridge-test/ }).click();
await page.getByRole('button', { name: 'New Cowork task' }).click();
await page.getByPlaceholder(/Message Claude/).waitFor();
ok('chat created: ' + page.url());

// Attach a Cyrillic-named file and send
await page.locator('input[type=file]').first().setInputFiles({ name: 'привет.txt', mimeType: 'text/plain', buffer: Buffer.from('ATTACHMENT-ALPHA\n') });
await page.getByPlaceholder(/Message Claude/).fill('Прочитай вложение и создай out.txt');
await page.getByLabel('Model').selectOption('opus');
await page.getByLabel('Effort').selectOption('high');
await page.getByRole('button', { name: 'Options' }).click();
await page.getByPlaceholder(/return files/i).fill('out.txt');
await page.getByRole('button', { name: 'Send' }).click();
await page.getByText(/Queued|Running/).first().waitFor({ timeout: 10000 });

const job = [...files.values()].find((f) => f.parents.includes(B.inbox));
if (!job) fail('no job folder in inbox');
const req = child(job.id, 'request.json');
const ready = child(job.id, 'request.ready');
const request = JSON.parse(req.content.toString());
const marker = JSON.parse(ready.content.toString());
if (marker.sha256 !== sha(req.content) || marker.sizeBytes !== req.content.length) fail('request.ready does not match request.json');
else ok('request.ready verifies request.json (sha256 + size)');
const att = child(child(job.id, 'attachments').id, 'привет.txt');
if (!att || sha(att.content) !== request.attachments[0].sha256) fail('attachment hash mismatch');
else ok('attachment staged with matching sha256, Cyrillic name preserved');
const order = log.filter((l) => /copy|request\./.test(l));
if (order.join('|') !== 'copy привет.txt|create request.json (' + req.content.length + ' B)|create request.ready (' + ready.content.length + ' B)') fail('write order wrong: ' + order.join(' | '));
else ok('write order: attachments -> request.json -> request.ready');
console.log('request.json:', JSON.stringify({ ...request, instruction: request.instruction.slice(0, 60) }));
if (request.protocolMinor !== 1 || request.mode !== 'cowork' || request.model !== 'opus' || request.effort !== 'high' || request.output?.autoExport !== true || request.timeoutMinutes !== 0)
  fail('v0.6 request fields wrong');
else ok('request carries mode=cowork, model=opus, effort=high, autoExport, timeout from mode');

// Bridge claims and streams, then completes
const finish = bridgeClaimAndRun(request.jobId, { finalText: 'Готово: **out.txt** создан.', sessionId: 'S-CLAUDE-1', artifactName: 'out.txt' });
await page.getByText(/Reading \.co-attachments/).waitFor({ timeout: 10000 });
ok('live activity rendered (Reading …)');
finish();
await page.getByText('out.txt создан').waitFor({ timeout: 15000 });
ok('final response rendered from verified result');
await page.getByText(/ in(?: \([0-9,]+ cached\))? · 50 out · 0\.012 USD · 4\.2 s/).waitFor({ timeout: 3000 });
ok('usage line rendered; reported model tag: ' + (await page.getByText('claude-opus-5').count()));

const events = [...files.values()].filter((f) => /^\d{6}-(user|assistant)_message-/.test(f.name)).map((f) => f.name).sort();
if (events.length !== 2) fail('expected 2 canonical events, got ' + events.join(','));
else ok('canonical events: ' + events.join(', '));

// Artifact download with hash verification
const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download' }).first().click()]);
const dl = fs.readFileSync(await download.path()).toString();
if (dl !== 'artifact-content\n') fail('artifact content mismatch'); else ok('artifact downloaded and sha256-verified');

// Save to Drive: copy into a user folder, canonical copy stays in AI-Workbench
await page.getByRole('button', { name: 'Save to Drive…' }).first().click();
await page.getByRole('button', { name: '📁 Documents' }).click();
await page.getByRole('button', { name: 'Save here' }).click();
await page.getByText(/Saved: My Drive \/ Documents\/out.txt/).waitFor({ timeout: 5000 });
const savedCopy = child(DOCS, 'out.txt');
const canonical = [...files.values()].filter((f) => f.name === 'out.txt' && f !== savedCopy);
if (!savedCopy || savedCopy.content.toString() !== 'artifact-content\n' || canonical.length !== 1) fail('save to drive failed');
else ok('saved copy in My Drive/Documents, canonical copy kept in AI-Workbench');
await page.getByRole('button', { name: 'Save to Drive…' }).first().click();
await page.getByRole('button', { name: 'Save here' }).click();
await page.waitForTimeout(800);
if (!child(DOCS, 'out (1).txt')) fail('second save should not overwrite'); else ok('second save → "out (1).txt", remembered last folder');

// Second turn: resume with same provider
await page.getByPlaceholder(/Message Claude/).fill('Второй ход');
await page.getByRole('button', { name: 'Send' }).click();
await page.getByText(/Queued|Running/).first().waitFor({ timeout: 10000 });
const job2 = [...files.values()].find((f) => f.parents.includes(B.inbox));
const req2 = JSON.parse(child(job2.id, 'request.json').content.toString());
if (req2.conversation.operation !== 'resume' || req2.conversation.sessionId !== 'S-CLAUDE-1') fail('second turn is not resume: ' + JSON.stringify(req2.conversation));
else ok('second turn uses provider-native resume S-CLAUDE-1');
if (/\[AI Workbench environment\]/.test(req2.instruction)) fail('resume repeats the environment note'); else ok('resume without a repeated environment note');

// Stop
await page.getByRole('button', { name: 'Stop' }).click();
await page.waitForTimeout(500);
// Mode switch: a cowork session must not be resumed in chat mode
{
  const j2 = [...files.values()].find((f) => f.parents.includes(B.inbox));
  j2.parents = [B.claimed];
}
await page.getByRole('radio', { name: 'Chat' }).click();
await page.getByPlaceholder(/Message Claude/).fill('Chat-mode question');
await page.getByRole('button', { name: 'Send' }).click();
await page.waitForTimeout(1500);
const job3 = [...files.values()].find((f) => f.parents.includes(B.inbox));
const req3 = job3 && JSON.parse(child(job3.id, 'request.json').content.toString());
if (!req3 || req3.mode !== 'chat' || req3.conversation.operation !== 'new' || !/CO context/.test(req3.instruction)) fail('mode switch should start a new session with context: ' + JSON.stringify(req3 && req3.conversation));
else ok('mode switch → chat, new session with context package');
job3.parents = [B.claimed];
await page.waitForTimeout(800);
if (!child(B.cancel, `${req2.jobId}.cancel`)) fail('cancel marker missing'); else ok('cancel marker written');
await page.getByText('Cancel requested').first().waitFor({ timeout: 5000 });
ok('UI shows cancel_requested');

// Reload: history restored from Drive
await page.reload();
await signIn();
await page.getByText('out.txt создан').waitFor({ timeout: 15000 });
ok('history restored from Drive after reload');

// Chat management: rename, move, delete
{
  // settle running jobs so that the chat is idle
  for (const f of [...files.values()].filter((f) => f.parents.includes(B.claimed))) f.parents = [B.completed];
  const chatUrl = page.url();
  const chatIdNow = chatUrl.split('/c/')[1];
  // finish pending jobs with results so the chat is idle
  for (const f of [...files.values()].filter((f) => f.parents.includes(B.completed) && !child(f.id, 'result.json'))) {
    const r = Buffer.from(JSON.stringify({ schemaVersion: 3, jobId: f.name, status: 'cancelled', completedUtc: new Date().toISOString() }));
    add('result.json', f.id, r);
    add('result.ready', f.id, JSON.stringify({ schemaVersion: 2, payload: 'result.json', sizeBytes: r.length, sha256: sha(r) }));
  }
  await page.waitForTimeout(6000);
  await page.getByRole('button', { name: 'Rename' }).click();
  await page.getByLabel('Title').fill('Переименованный чат');
  await page.getByRole('button', { name: 'Rename', exact: true }).last().click();
  await page.getByRole('heading', { name: 'Переименованный чат' }).waitFor({ timeout: 5000 });
  const cj = [...files.values()].find((f) => f.name === 'chat.json' && f.content.toString().includes(chatIdNow));
  if (!JSON.parse(cj.content.toString()).title.startsWith('Переименованный')) fail('rename not persisted'); else ok('chat renamed (chat.json updated)');

  await page.getByRole('button', { name: 'Move…' }).click();
  await page.getByRole('button', { name: 'Move', exact: true }).click();
  await page.waitForURL(/\/p\/second\/c\//, { timeout: 8000 });
  const chatFolder = [...files.values()].find((f) => f.name === chatIdNow);
  const secondChats = child(child(child(ROOT, 'projects').id, 'second').id, 'chats');
  if (!secondChats || !chatFolder.parents.includes(secondChats.id)) fail('chat folder not moved');
  else ok('chat folder moved to project "second" with its history');
  await page.getByText('out.txt создан').waitFor({ timeout: 8000 });
  const [dl2] = await Promise.all([page.waitForEvent('download'), page.locator('.artifact', { hasText: 'out.txt' }).first().getByRole('button', { name: 'Download' }).click()]);
  if (fs.readFileSync(await dl2.path()).toString() !== 'artifact-content\n') fail('artifact after move'); else ok('artifact downloads after move (path remapped)');
  await page.getByPlaceholder(/Message Claude/).fill('After move');
  await page.getByRole('button', { name: 'Send' }).click();
  await page.waitForTimeout(1500);
  const jm = [...files.values()].find((f) => f.parents.includes(B.inbox));
  const rm = JSON.parse(child(jm.id, 'request.json').content.toString());
  if (rm.projectId !== 'second' || rm.conversation.operation !== 'new') fail('after move: ' + JSON.stringify(rm.conversation) + rm.projectId);
  else ok('first message after move: new session in the target project');
  if (!/\[AI Workbench environment\][\s\S]*chats\/[^/]+\/[\s\S]*\[End of AI Workbench environment\]/.test(rm.instruction)) fail('new session lacks the environment note');
  else ok('new session carries the AI Workbench environment note');
  jm.parents = [B.completed];
  const r2 = Buffer.from(JSON.stringify({ schemaVersion: 3, jobId: jm.name, status: 'completed', agentFinalResponse: 'ok after move', completedUtc: new Date().toISOString() }));
  add('result.json', jm.id, r2);
  add('result.ready', jm.id, JSON.stringify({ schemaVersion: 2, payload: 'result.json', sizeBytes: r2.length, sha256: sha(r2) }));
  await page.getByText('ok after move').waitFor({ timeout: 10000 });

  // Delete with unfinished jobs: one claimed (gets a cancel marker), one still queued (withdrawn).
  const sendAndFind = async (text) => {
    await page.getByPlaceholder(/Message Claude/).fill(text);
    await page.getByRole('button', { name: 'Send' }).click();
    await page.waitForTimeout(1500);
    return [...files.values()].find((f) => f.parents.includes(B.inbox) && !f.trashed);
  };
  const jClaimed = await sendAndFind('Running before delete');
  jClaimed.parents = [B.claimed];
  const jQueued = await sendAndFind('Queued before delete');
  await page.getByRole('button', { name: 'Delete' }).first().click();
  await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
  await page.waitForURL(/\/p\/second$/, { timeout: 8000 });
  if (!chatFolder.trashed) fail('chat not trashed'); else ok('chat with running jobs deleted to Drive trash');
  if (!child(B.cancel, `${jClaimed.name}.cancel`)) fail('claimed job not cancelled on delete'); else ok('claimed job cancelled on delete');
  if (!jQueued.trashed) fail('queued job not withdrawn on delete'); else ok('queued job withdrawn from inbox on delete');
  await page.goto(chatUrl.replace(/\/c\/.*/, ''));
  await signIn();
}

// Context folders
{
  await page.getByRole('tab', { name: 'Project' }).click();
  await page.getByRole('button', { name: 'Connect folder…' }).click();
  await page.getByRole('tab', { name: /PC — home/ }).click();
  await page.getByRole('button', { name: '📁 Documents' }).waitFor({ timeout: 8000 });
  await page.locator('.tree-row', { hasText: 'Documents' }).first().getByRole('button', { name: '▸' }).click();
  await page.locator('.tree-row', { hasText: 'Acme' }).getByRole('button', { name: '▸' }).click();
  let req = null;
  for (let i = 0; i < 40 && !req; i++) {
    await page.waitForTimeout(250);
    req = [...files.values()].find((f) => f.parents.includes(child(FOLDERS, 'requests').id));
  }
  if (!req) fail('no expand request written');
  const body = JSON.parse(req.content.toString());
  add(req.name, child(FOLDERS, 'results').id, JSON.stringify({ rootId: 'home', relativePath: body.relativePath, depth: 2, truncated: false, tree: { n: '', c: [{ n: 'LTO', c: [] }] } }));
  await page.getByRole('button', { name: '📁 LTO' }).waitFor({ timeout: 10000 });
  ok('expand beyond index depth via Bridge request: ' + body.relativePath);
  await page.getByRole('button', { name: '📁 LTO' }).click();
  await page.getByLabel('Access').last().selectOption('write');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByText('🖥 LTO').waitFor({ timeout: 5000 });
  const pj = [...files.values()].filter((f) => f.name === 'project.json').map((f) => JSON.parse(f.content.toString())).find((p) => p.projectId === 'bridge-test');
  const cf = pj?.contextFolders?.[0];
  if (!cf || cf.rootId !== 'home' || cf.relativePath !== 'Documents/Acme/LTO' || cf.access !== 'write') fail('project.json contextFolders: ' + JSON.stringify(pj?.contextFolders));
  else ok('context folder saved in project.json: home/Documents/Acme/LTO (write)');
  await page.getByRole('button', { name: /New Cowork task/ }).click();
  await page.getByPlaceholder(/Message/).fill('Use the context folder');
  await page.getByRole('button', { name: 'Send' }).click();
  await page.waitForTimeout(1500);
  const jc = [...files.values()].find((f) => f.parents.includes(B.inbox) && !f.trashed);
  const rc = JSON.parse(child(jc.id, 'request.json').content.toString());
  if (rc.contextFolders?.[0]?.relativePath !== 'Documents/Acme/LTO' || rc.contextFolders[0].access !== 'write') fail('request contextFolders: ' + JSON.stringify(rc.contextFolders));
  else ok('job request carries contextFolders');
  jc.parents = [B.claimed];
}

// Workspace tab
await page.getByRole('tab', { name: 'Workspace' }).click();
await page.getByText('📄 notes.md').waitFor({ timeout: 5000 });
ok('workspace tab lists Drive workspace files');
await page.screenshot({ path: 'e2e/chat.png' });
await page.setViewportSize({ width: 390, height: 800 });
await page.waitForTimeout(300);
await page.screenshot({ path: 'e2e/mobile.png' });
const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
if (overflow) fail('horizontal overflow at 390 px'); else ok('no horizontal overflow at 390 px');

if (consoleErrors.length) console.log('console errors:', consoleErrors);
await browser.close();
server.close();
