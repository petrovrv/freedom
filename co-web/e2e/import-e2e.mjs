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
add('owner.json', HOST, JSON.stringify({ schemaVersion: 2, hostId: 'home-main', machineName: 'HOME-PC', updatedUtc: new Date().toISOString(), bridgeVersion: '0.5.1', capabilities: ['protocol-v3', 'conversation-resume', 'codex-fork', 'attachments', 'cancel', 'modes', 'model-select', 'effort-select', 'auto-export', 'usage', 'project-catalog'],
  protocolMinor: 1,
  agents: {
    claude: { displayName: 'Claude', modes: ['chat', 'cowork'], models: [{ id: 'opus', label: 'Opus (latest)' }, { id: 'sonnet', label: 'Sonnet (latest)' }], efforts: ['low', 'medium', 'high'], defaultModel: null, defaultEffort: null },
    codex: { displayName: 'Codex', modes: ['chat', 'cowork'], models: [{ id: 'gpt-6-luna', label: 'GPT-6-Luna', efforts: ['low', 'medium'] }], efforts: ['low', 'medium', 'high'], defaultModel: 'gpt-6-luna', defaultEffort: 'medium' },
  },
  chatProjectEnvironments: { owner: { allowedAgents: ['claude', 'codex'], autoExport: true } },
  projects: { 'bridge-test': { environmentId: 'owner', allowedAgents: ['codex', 'claude'], allowedModes: ['chat', 'cowork'], autoExport: true, workspaceDriveRelativePath: 'projects/bridge-test/workspace' } },
}));
mkpath(ROOT, ['projects', 'bridge-test', 'workspace']);
add('notes.md', child(child(child(ROOT, 'projects').id, 'bridge-test').id, 'workspace').id, '# notes\n', 'text/markdown');
mkpath(ROOT, ['projects', 'bridge-test', 'chats']);

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


import { zipSync, strToU8 } from 'fflate';

// ---------- fixture exports ----------
const claudeExport = zipSync({
  'conversations.json': strToU8(JSON.stringify([
    { uuid: 'c-1', name: 'Расчёт вентиляции', created_at: '2026-03-01T10:00:00Z', updated_at: '2026-03-02T10:00:00Z', project_uuid: 'p-1',
      chat_messages: [
        { uuid: 'm1', sender: 'human', text: '', content: [{ type: 'text', text: 'Посчитай воздухообмен для помещения 40 м²' }], created_at: '2026-03-01T10:00:00Z',
          attachments: [{ file_name: 'plan.txt', extracted_content: 'Room 40 m2, height 3 m' }], files: [] },
        { uuid: 'm2', sender: 'assistant', text: '', content: [{ type: 'thinking', thinking: 'x' }, { type: 'text', text: 'Воздухообмен: 240 м³/ч при кратности 2.' }], created_at: '2026-03-01T10:00:10Z' },
      ] },
    { uuid: 'c-2', name: '', created_at: '2026-04-01T10:00:00Z', updated_at: '2026-04-01T11:00:00Z',
      chat_messages: [{ uuid: 'm3', sender: 'human', text: 'Hello without name', created_at: '2026-04-01T10:00:00Z' },
                      { uuid: 'm4', sender: 'assistant', text: 'Hi', created_at: '2026-04-01T10:00:05Z' }] },
    { uuid: 'c-3', name: 'Not selected', created_at: '2026-05-01T10:00:00Z', updated_at: '2026-05-01T11:00:00Z', chat_messages: [{ uuid: 'm5', sender: 'human', text: 'skip me' }] },
  ])),
  'projects.json': strToU8(JSON.stringify([
    { uuid: 'p-1', name: 'Атамекен HVAC', description: 'KZ project', prompt_template: 'Answer in SI units.', docs: [{ filename: 'brief.md', content: '# Brief' }] },
  ])),
  'users.json': strToU8('[]'),
});
const gptExport = zipSync({
  'conversations.json': strToU8(JSON.stringify([
    { id: 'g-1', conversation_id: 'g-1', title: 'Branchy chat', create_time: 1750000000, update_time: 1750000500, gizmo_id: 'g-p-abc123456', current_node: 'n4', default_model_slug: 'gpt-5',
      mapping: {
        root: { id: 'root', message: null, parent: null, children: ['n1'] },
        n1: { id: 'n1', parent: 'root', children: ['n2', 'n2b'], message: { author: { role: 'system' }, content: { content_type: 'text', parts: ['sys'] }, metadata: { is_visually_hidden_from_conversation: true } } },
        n2: { id: 'n2', parent: 'n1', children: ['n3'], message: { author: { role: 'user' }, create_time: 1750000001, content: { content_type: 'text', parts: ['Question A'] }, metadata: {} } },
        n2b: { id: 'n2b', parent: 'n1', children: [], message: { author: { role: 'user' }, content: { content_type: 'text', parts: ['ABANDONED BRANCH'] }, metadata: {} } },
        n3: { id: 'n3', parent: 'n2', children: ['n4'], message: { author: { role: 'assistant' }, recipient: 'browser', content: { content_type: 'code', text: 'search()' }, metadata: {} } },
        n4: { id: 'n4', parent: 'n3', children: [], message: { author: { role: 'assistant' }, recipient: 'all', create_time: 1750000009, content: { content_type: 'text', parts: ['Answer A'] }, metadata: {} } },
      } },
  ])),
  'image-1.png': new Uint8Array(1000),
});
fs.writeFileSync('/tmp/claude-export.zip', claudeExport);
fs.writeFileSync('/tmp/gpt-export.zip', gptExport);

// ---------- run ----------
const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium' });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
const consoleErrors = [];
page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
page.on('pageerror', (e) => consoleErrors.push(String(e)));
await page.route('https://accounts.google.com/**', (r) => r.fulfill({ contentType: 'text/javascript', body: GIS_STUB }));
await page.route('https://www.googleapis.com/**', driveRoute);
// Freedom F1: members of the workbench (db.php op members); other db.php ops go on to the preview server (Drive fallback).
await page.route('**/agent/db.php', (r) => (JSON.parse(r.request().postData() || '{}').op === 'members'
  ? r.fulfill({ contentType: 'application/json', body: JSON.stringify({ members: [{ env: 'owner', email: 'owner@example.org', name: 'Owner', color: '#1f5fbf', role: 'owner' }] }) })
  : r.continue()));
const fail = (msg) => { console.error('FAIL:', msg); process.exitCode = 1; };
const ok = (msg) => console.log('OK:', msg);
const byName = (n) => [...files.values()].filter((f) => f.name === n);

await page.goto('http://localhost:5199/e/owner/import');
await page.getByRole('button', { name: 'Sign in with Google' }).click();
await page.getByRole('button', { name: 'Choose export file…' }).waitFor({ timeout: 10000 });
await page.locator('input[type=file]').setInputFiles('/tmp/claude-export.zip');
await page.getByText(/3 chats · 1 projects/).waitFor({ timeout: 10000 });
ok('claude export parsed: 3 chats, 1 project');

await page.getByRole('button', { name: /Hello without name/ }).click();
await page.getByText('Hi', { exact: true }).waitFor();
ok('untitled chat got title from first message; preview works');
await page.getByLabel('Select Расчёт вентиляции').check();
await page.getByLabel('Select Hello without name').check();
await page.getByText(/Where to put 2 selected chat/).waitFor();
const ids = await page.getByLabel('Project ID').evaluateAll((els) => els.map((e) => e.value));
console.log('proposed project ids:', ids);
if (!ids.includes('cl-atameken-hvac') || !ids.includes('cl-imported')) fail('project id proposals: ' + ids.join(','));
else ok('project ids proposed (Cyrillic transliterated): ' + ids.join(', '));
await page.getByRole('button', { name: 'Import 2 chat(s)' }).click();
await page.getByText(/Imported 2, skipped 0/).waitFor({ timeout: 15000 });
ok('import finished');

const seg = byName('000000-imported.segment.json');
const pj = byName('project.json').map((f) => JSON.parse(f.content.toString())).find((p) => p.projectId === 'cl-atameken-hvac');
const pmd = byName('project.md')[0];
const brief = byName('brief.md')[0];
const idx = JSON.parse(byName('index.json')[0].content.toString());
const att = byName('0000-plan.txt')[0];
if (seg.length !== 2) fail('segments: ' + seg.length);
else ok('2 sealed segments written');
if (!pj || pj.environmentId !== 'owner' || !pmd || pmd.content.toString() !== 'Answer in SI units.' || !brief) fail('project content');
else ok('project.json, instructions and knowledge file imported');
if (Object.keys(idx.entries).sort().join(',') !== 'claude:c-1,claude:c-2') fail('index ' + JSON.stringify(idx));
else ok('import index records claude:c-1, claude:c-2');
if (!att || att.content.toString() !== 'Room 40 m2, height 3 m') fail('attachment text'); else ok('attachment extracted text stored');
const segEv = JSON.parse(seg.find((s) => s.content.toString().includes('c-1')).content.toString()).events;
if (segEv.length !== 2 || segEv[1].text !== 'Воздухообмен: 240 м³/ч при кратности 2.') fail('thinking block leaked or text wrong'); else ok('thinking blocks dropped, text kept');

// Re-open the import: already imported chats are hidden
await page.goto('http://localhost:5199/e/owner/import');
await page.getByRole('button', { name: 'Sign in with Google' }).click();
await page.locator('input[type=file]').setInputFiles('/tmp/claude-export.zip');
await page.getByText(/2 already imported/).waitFor({ timeout: 10000 });
const listed = await page.locator('.import-list li').count();
if (listed !== 1) fail('expected 1 visible chat, got ' + listed); else ok('already imported chats hidden (1 left)');

// Continue an imported chat
await page.goto('http://localhost:5199/e/owner/p/cl-atameken-hvac');
await page.getByRole('button', { name: 'Sign in with Google' }).click();
await page.getByRole('button', { name: /Расчёт вентиляции/ }).click();
await page.getByText('imported · claude.ai').waitFor({ timeout: 10000 });
ok('imported chat renders with imported tag');
const modeTag = await page.locator('.composer .tag').first().innerText().catch(() => '');
if (modeTag !== 'Chat') fail('drive-only project should offer Chat only, got ' + modeTag); else ok('Drive-only project → Chat mode only');
await page.getByPlaceholder(/Message Claude/).fill('Продолжим: а для 60 м²?');
await page.getByRole('button', { name: 'Send' }).click();
await page.waitForTimeout(1500);
const job = [...files.values()].find((f) => f.parents.includes(B.inbox));
const req = JSON.parse(child(job.id, 'request.json').content.toString());
if (req.mode !== 'chat' || req.projectId !== 'cl-atameken-hvac' || req.conversation.operation !== 'new' || !req.instruction.includes('Воздухообмен: 240') || !req.instruction.includes('Answer in SI units.'))
  fail('continuation request wrong: ' + req.instruction.slice(0, 300));
else ok('continuation: chat mode, new session, context package carries imported history + project instructions');

// ChatGPT export
await page.goto('http://localhost:5199/e/owner/import');
await page.getByRole('button', { name: 'Sign in with Google' }).click();
await page.locator('input[type=file]').setInputFiles('/tmp/gpt-export.zip');
await page.getByText(/1 chats · 1 projects/).waitFor({ timeout: 10000 });
await page.getByRole('button', { name: /Branchy chat/ }).click();
const pv = await page.locator('.import-preview').innerText();
if (pv.includes('ABANDONED') || pv.includes('search()') || pv.includes('sys') && pv.includes('sys\n') || !pv.includes('Question A') || !pv.includes('Answer A')) fail('chatgpt branch/tool filtering: ' + pv);
else ok('ChatGPT: active branch only, tool calls and hidden system dropped');
await page.getByLabel('Select Branchy chat').check();
await page.getByLabel('Project title').fill('Мой GPT проект');
await page.getByRole('button', { name: 'Import 1 chat(s)' }).click();
await page.getByText(/Imported 1, skipped 0/).waitFor({ timeout: 15000 });
ok('ChatGPT chat imported into ' + JSON.parse(byName('index.json')[0].content.toString()).entries['chatgpt:g-1'].projectId);

await page.screenshot({ path: 'e2e/import.png' });
if (consoleErrors.length) console.log('console errors:', consoleErrors);
await browser.close();
server.close();
