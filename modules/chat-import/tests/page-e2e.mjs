// SPDX-License-Identifier: AGPL-3.0-or-later
// Browser test of the import page (ui/import.html) in a sandboxed frame as CO Web shows it: a host page speaks ext/1
// (hello -> MessageChannel -> init, calls) and answers the core calls core.import.* from memory, so the built
// ui/import.js, the zip reader and the steps of the page run for real. No CO Web, no Drive.
//   PW_CHROME=<chrome.exe> node modules/chat-import/tests/page-e2e.mjs      (playwright and fflate from co-web)
import fs from 'node:fs';
import http from 'node:http';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const MODULES = path.resolve(HERE, '..', '..');
const CO_WEB = path.resolve(MODULES, '..', 'co-web');
const req = createRequire(path.join(CO_WEB, 'package.json'));
const { chromium } = await import(pathToFileURL(path.join(path.dirname(req.resolve('playwright/package.json')), 'index.mjs')).href);
const { zipSync, strToU8 } = await import(pathToFileURL(path.join(path.dirname(req.resolve('fflate/package.json')), 'esm', 'index.mjs')).href);

const HOST = `<!doctype html><meta charset="utf-8"><body style="margin:0">
<iframe id="f" src="/agent/module.php?m=chat-import&f=ui/import.html" sandbox="allow-scripts allow-forms" style="width:1200px;height:850px;border:0"></iframe>
<script>
window.writes = []; window.finished = [];
const projects = [{ projectId: 'existing-1', title: 'Existing project' }];
const imported = { 'claude:c-old': { projectId: 'existing-1', chatId: 'CHAT-OLD' } };
const calls = {
  'core.import.context': async () => ({ env: 'owner', projects, imported }),
  'core.import.write': async (a) => {
    const key = a.conversation.source + ':' + a.conversation.id;
    if (imported[key]) return { skipped: true, ...imported[key] };
    window.writes.push(a);
    imported[key] = { projectId: a.target.projectId, chatId: 'CHAT-' + window.writes.length };
    return { skipped: false, ...imported[key] };
  },
  'core.import.finish': async (a) => { window.finished.push(a); return { created: [...new Set(window.writes.filter((w) => w.target.kind === 'new').map((w) => w.target.projectId))] }; },
};
let port = null;
window.addEventListener('message', (e) => {
  const f = document.getElementById('f');
  if (port || e.source !== f.contentWindow || e.origin !== 'null' || !e.data || e.data.t !== 'hello') return;
  const ch = new MessageChannel();
  port = ch.port1;
  port.onmessage = async (ev) => {
    const m = ev.data;
    if (m.t !== 'call') return;
    try { port.postMessage({ ext: 1, t: 'result', id: m.id, ok: true, data: await calls[m.op](m.args) }); }
    catch (err) { port.postMessage({ ext: 1, t: 'result', id: m.id, ok: false, error: String(err) }); }
  };
  f.contentWindow.postMessage({ ext: 1, t: 'init', ctx: { ext: 1, module: 'chat-import', slot: 'ui.page', env: 'owner', theme: {} } }, '*', [ch.port2]);
});
</script>`;

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' };
const CSP = "sandbox allow-scripts allow-forms; default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'";
const server = http.createServer((rq, rs) => {
  const u = new URL(rq.url, 'http://x');
  if (u.pathname === '/') return rs.writeHead(200, { 'Content-Type': 'text/html' }).end(HOST);
  if (u.pathname === '/ext/ext1.js') return rs.writeHead(200, { 'Content-Type': 'text/javascript' }).end(fs.readFileSync(path.join(CO_WEB, 'public', 'ext', 'ext1.js')));
  if (u.pathname === '/agent/module.php') {
    const m = u.searchParams.get('m') || '', f = u.searchParams.get('f') || '';
    const file = path.join(MODULES, m, f);
    if (/^[a-z0-9-]+$/.test(m) && /^ui\/[A-Za-z0-9._/-]+$/.test(f) && !f.includes('..') && fs.existsSync(file)) {
      return rs.writeHead(200, { 'Content-Type': MIME[path.extname(f)] || 'application/octet-stream', 'Content-Security-Policy': CSP }).end(fs.readFileSync(file));
    }
  }
  rs.writeHead(404).end('not found');
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${server.address().port}/`;

const zip = path.join(fs.mkdtempSync(path.join(fs.realpathSync(process.env.TEMP || '/tmp'), 'chat-import-')), 'export.zip');
fs.writeFileSync(zip, zipSync({
  'conversations.json': strToU8(JSON.stringify([
    { uuid: 'c-1', name: 'Расчёт вентиляции', created_at: '2026-03-01T10:00:00Z', updated_at: '2026-03-02T10:00:00Z', project_uuid: 'p-1',
      chat_messages: [
        { sender: 'human', content: [{ type: 'text', text: 'Посчитай воздухообмен' }], created_at: '2026-03-01T10:00:00Z', attachments: [{ file_name: 'plan.txt', extracted_content: 'Room 40 m2' }] },
        { sender: 'assistant', content: [{ type: 'thinking', thinking: 'x' }, { type: 'text', text: 'Воздухообмен: 240 м³/ч.' }] },
      ] },
    { uuid: 'c-2', name: '', created_at: '2026-04-01T10:00:00Z', updated_at: '2026-04-01T11:00:00Z',
      chat_messages: [{ sender: 'human', text: 'Hello without name' }, { sender: 'assistant', text: 'Hi' }] },
    { uuid: 'c-old', name: 'Imported before', created_at: '2026-01-01T10:00:00Z', updated_at: '2026-01-01T11:00:00Z', chat_messages: [{ sender: 'human', text: 'old' }] },
  ])),
  'projects.json': strToU8(JSON.stringify([{ uuid: 'p-1', name: 'Атамекен HVAC', prompt_template: 'Answer in SI units.', docs: [{ filename: 'brief.md', content: '# Brief' }] }])),
  'image.png': new Uint8Array(5000),
}));

let failed = 0;
const ok = (name, cond, extra = '') => { if (!cond) failed++; console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}${cond ? '' : ' ' + extra}`); };
const browser = await chromium.launch(process.env.PW_CHROME ? { executablePath: process.env.PW_CHROME } : {});
try {
  const page = await browser.newPage({ viewport: { width: 1250, height: 900 } });
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(m.text()); });
  page.on('response', (r) => { if (r.status() >= 400 && !r.url().endsWith('/favicon.ico')) errors.push(`${r.status()} ${r.url()}`); });
  await page.goto(base);
  const fr = page.frameLocator('#f');
  const choose = fr.getByRole('button', { name: 'Choose export file…' });
  await choose.waitFor({ timeout: 10000 });
  await fr.locator('button:enabled', { hasText: 'Choose export file…' }).waitFor({ timeout: 10000 });
  ok('page loaded in the sandboxed frame, context from core.import.context', true);
  await fr.locator('input[type=file]').setInputFiles(zip);
  await fr.getByText(/3 chats · 1 projects · 1 already imported/).waitFor({ timeout: 10000 });
  ok('export read in the frame: 3 chats, 1 project, 1 imported before', true);
  ok('imported chats hidden', (await fr.locator('.import-list li').count()) === 2);
  await fr.getByRole('button', { name: /Hello without name/ }).click();
  await fr.getByText('Hi', { exact: true }).waitFor();
  ok('preview of an untitled chat', true);
  await fr.getByLabel('Select Расчёт вентиляции').check();
  await fr.getByLabel('Select Hello without name').check();
  const ids = await fr.getByLabel('Project ID').evaluateAll((els) => els.map((e) => e.value));
  ok('project ids proposed (Cyrillic transliterated)', ids.includes('cl-atameken-hvac') && ids.includes('cl-imported'), ids.join(','));
  await fr.getByLabel('Project ID').first().fill('bad id!');
  ok('invalid id blocks the import', await fr.getByRole('button', { name: 'Import 2 chat(s)' }).isDisabled());
  await fr.getByLabel('Project ID').first().fill(ids[0]);
  await fr.getByRole('button', { name: 'Import 2 chat(s)' }).click();
  await fr.getByText(/Imported 2, skipped 0/).waitFor({ timeout: 15000 });
  const w = await page.evaluate(() => window.writes);
  const fin = await page.evaluate(() => window.finished);
  const vent = w.find((x) => x.conversation.id === 'c-1');
  ok('two core.import.write calls, source only (the core takes the label from the manifest)', w.length === 2 && w.every((x) => x.conversation.source === 'claude' && !('sourceLabel' in x)));
  ok('conversation: thinking dropped, attachment text kept, projectKey not sent', !!vent && vent.conversation.messages[1].text === 'Воздухообмен: 240 м³/ч.'
    && vent.conversation.messages[0].attachments[0].text === 'Room 40 m2' && !('projectKey' in vent.conversation));
  ok('new project carries instructions and docs', vent?.target.kind === 'new' && vent.target.projectId === 'cl-atameken-hvac'
    && vent.target.instructions === 'Answer in SI units.' && vent.target.docs.length === 1);
  ok('finish called once after the writes', fin.length === 1);
  await fr.getByRole('button', { name: 'Open projects' }).click();
  await page.waitForFunction(() => window.finished.length === 2);
  ok('"Open projects" asks the core to open a new project', ['cl-atameken-hvac', 'cl-imported'].includes(await page.evaluate(() => window.finished[1].open)));
  // into an existing project
  await fr.getByRole('button', { name: 'Other file' }).click();
  await fr.locator('input[type=file]').setInputFiles(zip);
  await fr.getByText(/3 already imported/).waitFor({ timeout: 10000 });
  await fr.getByLabel('Hide imported').uncheck();
  ok('all chats shown when "Hide imported" is off', (await fr.locator('.import-list li').count()) === 3);
  ok('no errors in the page', errors.length === 0, errors.join(' | '));
  await page.screenshot({ path: path.join(path.dirname(zip), 'page-e2e.png') }); // outside the signed module folder
} finally {
  await browser.close();
  server.close();
}
if (failed) { console.log(`${failed} failed`); process.exit(1); }
console.log('all passed');
