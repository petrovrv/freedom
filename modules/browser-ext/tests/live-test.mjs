// SPDX-License-Identifier: AGPL-3.0-or-later
// Live check: CO (local build, prod API) in the extension panel -> real Claude job on home-main -> browser_* tools
// on the extension device fill a field on a local test page. Config JSON on stdin {token,email,ext,dist,pwModule,chrome,site,env}
// (site: the CO site, as in modules/browser-ext/extension/site.js; env: environment of the account).
import os from 'node:os'; import path from 'node:path'; import fs from 'node:fs'; import http from 'node:http';
let raw = ''; for await (const c of process.stdin) raw += c;
const cfg = JSON.parse(raw);
const CO = String(cfg.site || '').replace(/\/+$/, '');
if (!CO) throw new Error('config: site (the CO site) is required');
const { chromium } = await import(cfg.pwModule);
const log = (...a) => console.error('[live]', ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const SITE = '<!doctype html><title>Live form</title><h1>Contact form 7341</h1><form onsubmit="event.preventDefault()"><label>Full name <input id=nm name=name></label><button type=submit>Submit</button></form>';
const srv = http.createServer((q, s) => { s.writeHead(200, { 'content-type': 'text/html' }); s.end(SITE); });
await new Promise((r) => srv.listen(0, '127.0.0.1', r));
const url = 'http://127.0.0.1:' + srv.address().port + '/form';
const MIME = { html: 'text/html; charset=utf-8', js: 'text/javascript', css: 'text/css', svg: 'image/svg+xml', png: 'image/png', json: 'application/json', webmanifest: 'application/manifest+json', ico: 'image/x-icon' };
const ctx = await chromium.launchPersistentContext(fs.mkdtempSync(path.join(os.tmpdir(), 'coext-live-')), {
  headless: true, executablePath: cfg.chrome, viewport: { width: 1300, height: 900 },
  args: [`--disable-extensions-except=${cfg.ext}`, `--load-extension=${cfg.ext}`],
});
const res = { status: 'red', reason: '', chat: null, reply: '', field: null, errors: [] };
try {
  await ctx.addInitScript(([token, email, co]) => {
    if (location.origin !== co) return;
    localStorage.setItem('co.host', 'home-main');
    window.coDesktop = { getAccessToken: async () => ({ access_token: token, expires_in: 3000, email }) };
  }, [cfg.token, cfg.email, CO]);
  await ctx.route(CO + '/**', async (route) => {
    const p0 = new URL(route.request().url()).pathname;
    if (p0.startsWith('/agent/') || p0.startsWith('/auth/')) return route.continue();
    let p = decodeURIComponent(p0); if (p.endsWith('/')) p += 'index.html';
    let f = path.join(cfg.dist, p);
    if (!fs.existsSync(f) || !fs.statSync(f).isFile()) { if (path.extname(p)) return route.continue(); f = path.join(cfg.dist, 'index.html'); }
    return route.fulfill({ status: 200, path: f, headers: { 'content-type': MIME[path.extname(f).slice(1)] || 'application/octet-stream' } });
  });
  let [sw] = ctx.serviceWorkers(); if (!sw) sw = await ctx.waitForEvent('serviceworker');
  const id = sw.url().split('/')[2];
  const panel = ctx.pages()[0] || await ctx.newPage();
  panel.on('console', (m) => { if (m.type() === 'error') res.errors.push(m.text().slice(0, 300)); });
  await panel.goto(`chrome-extension://${id}/panel.html`);
  const co = async () => { for (let i = 0; i < 60; i++) { const f = panel.frames().find((x) => x.url().startsWith(CO)); if (f) return f; await sleep(500); } throw new Error('no CO frame'); };
  let f = await co();
  await sleep(3000);
  await f.goto(CO + '/e/' + (cfg.env || 'owner') + '/p/bridge-test?ext=1');
  f = await co();
  const ta = f.locator('textarea[aria-label="Message"]');
  await ta.waitFor({ timeout: 90000 });
  // ext device chip
  let chip = null;
  for (let i = 0; i < 40 && !chip; i++) { chip = await f.evaluate(() => [...document.querySelectorAll('button')].map((b) => b.textContent + '|' + b.getAttribute('aria-pressed')).find((t) => t.includes('this panel')) || null); if (!chip) await sleep(1000); }
  log('device chip:', chip);
  if (!chip) throw new Error('extension device chip not shown');
  await f.selectOption('select[aria-label="Agent"]', 'claude').catch(() => {});
  const effort = f.locator('select[aria-label="Effort"]');
  if (await effort.count() && await effort.locator('option[value="low"]').count()) await effort.selectOption('low');
  await ta.fill(`[ext-live test] Use the browser tools of the AI Workbench extension device: browser_open ${url} , then type "Ext Live 42" into the Full name field (do not submit). Reply with the h1 heading text.`);
  await f.getByRole('button', { name: 'Send', exact: true }).click();
  const t0 = Date.now();
  log('SENT');
  while (Date.now() - t0 < 420000) {
    await sleep(3000);
    f = await co();
    const m = /\/c\/(CHAT-\d{14}-[A-Z0-9]{4})/.exec(f.url()); if (m) res.chat = m[1];
    const st = await f.evaluate(() => { const a = [...document.querySelectorAll('.msg.assistant:not(.running):not(.inherited-msg)')].pop(); if (!a) return null; return { cls: a.className, text: (a.querySelector('.msg-body')?.innerText || '').slice(0, 1500) }; }).catch(() => null);
    if (st && /status-\S+/.test(st.cls)) { res.reply = st.text; res.jobStatus = /status-(\S+)/.exec(st.cls)[1]; break; }
  }
  const site = ctx.pages().find((p) => p.url().startsWith(url));
  res.field = site ? await site.inputValue('#nm') : null;
  res.status = res.jobStatus === 'completed' && res.field === 'Ext Live 42' && /7341/.test(res.reply) ? 'green' : 'red';
  if (res.status !== 'green') { res.reason = 'job=' + res.jobStatus + ' field=' + res.field; await panel.screenshot({ path: cfg.shot }); }
  if (res.chat) {
    try {
      await f.getByRole('button', { name: 'Delete', exact: true }).first().click({ timeout: 15000 });
      await f.locator('.modal-actions button.danger').click({ timeout: 15000 });
      await sleep(4000); res.cleanup = 'deleted';
    } catch (e) { res.cleanup = 'failed: ' + String(e.message).split('\n')[0]; }
  }
} catch (e) { res.reason = String(e?.message || e).split(String.fromCharCode(10))[0]; try { const pg = ctx.pages().find((p) => p.url().includes('panel.html')); await pg.screenshot({ path: cfg.shot }); res.frameText = (await pg.frames().find((x) => x.parentFrame())?.evaluate(() => document.body.innerText.slice(0, 800))); } catch {} }
finally { await ctx.close().catch(() => {}); srv.close(); }
console.log(JSON.stringify(res));
