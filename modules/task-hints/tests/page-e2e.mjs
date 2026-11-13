// SPDX-License-Identifier: AGPL-3.0-or-later
// Browser test of the hint page (ui/hint.html) in a sandboxed frame as CO Web shows it: a host page speaks ext/1
// (hello -> MessageChannel -> init), sends the composer text and records the hints the page answers.
//   PW_CHROME=<chrome.exe> node modules/task-hints/tests/page-e2e.mjs      (playwright from co-web)
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

const HOST = `<!doctype html><meta charset="utf-8"><body>
<iframe id="f" src="/agent/module.php?m=task-hints&f=ui/hint.html" sandbox="allow-scripts allow-forms" style="width:0;height:0;border:0"></iframe>
<script>
window.hints = [];
let port = null;
window.composer = (text) => port.postMessage({ ext: 1, t: 'composer', text });
window.addEventListener('message', (e) => {
  const f = document.getElementById('f');
  if (port || e.source !== f.contentWindow || e.origin !== 'null' || !e.data || e.data.t !== 'hello') return;
  const ch = new MessageChannel();
  port = ch.port1;
  port.onmessage = (ev) => { if (ev.data.t === 'hint') window.hints.push(ev.data.hint); };
  f.contentWindow.postMessage({ ext: 1, t: 'init', ctx: { ext: 1, module: 'task-hints', slot: 'ui.hint', theme: {} } }, '*', [ch.port2]);
  window.ready = true;
});
</script>`;

const CSP = "sandbox allow-scripts allow-forms; default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'";
const server = http.createServer((rq, rs) => {
  const u = new URL(rq.url, 'http://x');
  if (u.pathname === '/') return rs.writeHead(200, { 'Content-Type': 'text/html' }).end(HOST);
  if (u.pathname === '/ext/ext1.js') return rs.writeHead(200, { 'Content-Type': 'text/javascript' }).end(fs.readFileSync(path.join(CO_WEB, 'public', 'ext', 'ext1.js')));
  const m = u.searchParams.get('m') || '', f = u.searchParams.get('f') || '';
  const file = path.join(MODULES, m, f);
  if (u.pathname === '/agent/module.php' && /^[a-z0-9-]+$/.test(m) && /^ui\/[A-Za-z0-9._/-]+$/.test(f) && !f.includes('..') && fs.existsSync(file)) {
    return rs.writeHead(200, { 'Content-Type': f.endsWith('.html') ? 'text/html' : 'text/javascript', 'Content-Security-Policy': CSP }).end(fs.readFileSync(file));
  }
  rs.writeHead(404).end('not found');
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));

let failed = 0;
const ok = (name, cond, extra = '') => { if (!cond) failed++; console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}${cond ? '' : ' ' + extra}`); };
const browser = await chromium.launch(process.env.PW_CHROME ? { executablePath: process.env.PW_CHROME } : {});
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.waitForFunction(() => window.ready === true, null, { timeout: 10000 });
  await page.evaluate(() => window.composer('Build a pivot table in Excel from this csv'));
  await page.waitForFunction(() => window.hints.length === 1, null, { timeout: 5000 });
  const h = await page.evaluate(() => window.hints[0]);
  ok('hint for an Excel task', h && h.text === 'Recommended for excel:' && h.picks.length >= 1 && /model bench/.test(h.title), JSON.stringify(h));
  await page.evaluate(() => window.composer('Build a pivot table in Excel from this csv file'));
  await page.evaluate(() => window.composer('ok'));
  await page.waitForFunction(() => window.hints.length === 2, null, { timeout: 5000 });
  ok('same hint not sent twice, null when the text no longer suggests anything', (await page.evaluate(() => window.hints[1])) === null);
  ok('no errors in the page', errors.length === 0, errors.join(' | '));
} finally {
  await browser.close();
  server.close();
}
if (failed) { console.log(`${failed} failed`); process.exit(1); }
console.log('all passed');
