// e2e.mjs - Playwright end-to-end test of CO Web (Node 24 ESM). Config JSON on stdin, result JSON = last stdout line.
import fs from 'node:fs/promises';
import path from 'node:path';

const t0 = Date.now();
const el = () => +((Date.now() - t0) / 1000).toFixed(1);
const log = (m) => process.stderr.write(`[e2e +${el()}s] ${m}\n`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const out = (o) => process.stdout.write(JSON.stringify(o) + '\n');
class Fail extends Error {}
const fail = (m) => { throw new Fail(m); };

const MIME = {
  html: 'text/html; charset=utf-8', js: 'text/javascript; charset=utf-8', mjs: 'text/javascript; charset=utf-8',
  css: 'text/css; charset=utf-8', svg: 'image/svg+xml', png: 'image/png', ico: 'image/x-icon',
  json: 'application/json', webmanifest: 'application/manifest+json', woff2: 'font/woff2', txt: 'text/plain; charset=utf-8',
};

async function main() {
  let raw = '';
  for await (const c of process.stdin) raw += c;
  const cfg = JSON.parse(raw);

  let chromium;
  try { ({ chromium } = await import(cfg.pwModule)); }
  catch (e) { out({ status: 'error', reason: `cannot load playwright: ${e?.message || e}` }); return; }

  const res = {
    status: null, reason: null, runId: cfg.runId, chatId: null, reply: null, timings: {}, cleanup: null,
    screenshot: null, pageErrors: [], servedFiles: 0, base: cfg.base, dist: cfg.dist || null,
  };
  const T = res.timings;
  const pageErrors = [];
  const pushErr = (s) => { pageErrors.push(String(s).slice(0, 500)); if (pageErrors.length > 20) pageErrors.shift(); };
  let browser, page;

  try {
    browser = await chromium.launch({ headless: !cfg.headed });
    const context = await browser.newContext({ viewport: { width: 1400, height: 900 }, serviceWorkers: 'block' });
    // CO Web signs in through window.coDesktop.getAccessToken, as inside CO Desktop.
    await context.addInitScript(([token, email, host]) => {
      localStorage.setItem('co.host', host);
      localStorage.setItem('co.hostPref', host); // CO Web 0.98.0+ (S2): executor preference; co.host is for older builds
      // 0.2.1: the emulated CO Desktop has a paired device; with its tools on, CO Web sends every job to Home
      if (host === 'site') localStorage.setItem('co.useDevice', 'off');
      window.coDesktop = { getAccessToken: async () => ({ access_token: token, expires_in: 3000, email }) };
    }, [cfg.token, cfg.email, cfg.host]);

    // Local build: static files from dist, /agent/ and /auth/ (PHP API) stay on the production site.
    if (cfg.dist) {
      const dist = path.resolve(cfg.dist);
      log(`serving CO Web from ${dist}`);
      const isFile = async (f) => { try { return (await fs.stat(f)).isFile(); } catch { return false; } };
      const serve = (route, file) => {
        res.servedFiles++;
        const ext = path.extname(file).slice(1).toLowerCase();
        return route.fulfill({ status: 200, path: file, headers: { 'content-type': MIME[ext] || 'application/octet-stream', 'cache-control': 'no-store' } });
      };
      await context.route(cfg.base + '/**', async (route) => {
        try {
          const pathname = new URL(route.request().url()).pathname;
          if (pathname.startsWith('/agent/') || pathname.startsWith('/auth/')) return route.continue();
          let p = decodeURIComponent(pathname);
          if (p.includes('..')) return route.fulfill({ status: 403, body: 'forbidden' });
          if (p.endsWith('/')) p += 'index.html';
          const file = path.join(dist, p);
          if (await isFile(file)) return serve(route, file);
          if (!path.extname(p)) return serve(route, path.join(dist, 'index.html'));
          return route.continue();
        } catch (e) { pushErr(`route: ${e?.message || e}`); return route.continue().catch(() => {}); }
      });
    }

    page = await context.newPage();
    page.on('console', (m) => { if (m.type() === 'error') pushErr(`console: ${m.text()}`); });
    page.on('pageerror', (e) => pushErr(`pageerror: ${e?.message || e}`));

    try {
      // 1. Load and sign-in check
      const url = `${cfg.base}/e/${cfg.env}/p/${cfg.project}`;
      log(`goto ${url}`);
      await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 90000 });
      const ta = page.locator('textarea[aria-label="Message"]');
      await ta.waitFor({ state: 'visible', timeout: 90000 }).catch(() => fail('chat UI not ready in 90 s'));
      T.ready = el(); log('ready');
      if (cfg.dist && res.servedFiles === 0) fail('local build not used');
      // 0.4.0: CO Web 0.86.0 keeps the email in the ☰ menu; the menu button carries it as data-email
      const top = await page.locator('header.topbar').evaluate((el) => `${el.innerText} ${el.querySelector('[data-email]')?.getAttribute('data-email') || ''}`, null, { timeout: 10000 }).catch(() => '');
      if (!top.toLowerCase().includes(String(cfg.email).toLowerCase())) fail('not signed in');

      // 2. Agent / model / effort
      // 0.6.0: CO Web 0.100.0 (S6) has one model button (ModelPicker) instead of the Agent / Model selects
      if (await page.locator('select[aria-label="Agent"]').count()) {
        await page.selectOption('select[aria-label="Agent"]', cfg.agent);
        log(`agent ${cfg.agent}`);
        const model = page.locator('select[aria-label="Model"]');
        if (cfg.modelHint && await model.count()) {
          const h = cfg.modelHint.toLowerCase();
          const opts = await model.locator('option').evaluateAll((os) => os.map((o) => ({ v: o.value, t: (o.textContent || '').trim() })));
          const o = opts.find((x) => x.v.toLowerCase().includes(h) || x.t.toLowerCase().includes(h));
          if (o) { await model.selectOption(o.v); log(`model ${o.v}`); } else log(`no model matches "${cfg.modelHint}"`);
        }
      } else {
        await page.locator('button.mp-btn').click({ timeout: 30000 });
        const rows = page.locator(`.mp-pop .mp-row[data-agent="${cfg.agent}"]`);
        await rows.first().waitFor({ state: 'visible', timeout: 15000 }).catch(() => fail(`agent ${cfg.agent} not in the model list`));
        let row = cfg.host && cfg.host !== 'auto' ? rows.and(page.locator(`[data-host="${cfg.host}"]`)) : rows.and(page.locator('[data-here="1"]'));
        if (!await row.count()) row = rows.first();
        log(`agent ${cfg.agent} on ${await row.first().getAttribute('data-host')}`);
        let picked = false;
        if (cfg.modelHint) {
          const h = cfg.modelHint.toLowerCase();
          const chips = await row.first().locator('[data-model]').evaluateAll((bs) => bs.map((b) => ({ v: b.getAttribute('data-model') || '', t: (b.textContent || '').trim() })));
          const o = chips.find((x) => x.v.toLowerCase().includes(h) || x.t.toLowerCase().includes(h));
          if (o) { await row.first().locator(`[data-model="${o.v}"]`).click(); picked = true; log(`model ${o.v}`); } else log(`no model matches "${cfg.modelHint}"`);
        }
        if (!picked) await row.first().locator('[data-pick="default"]').click();
      }
      const effort = page.locator('select[aria-label="Effort"]');
      if (await effort.count() && await effort.locator('option[value="low"]').count()) { await effort.selectOption('low'); log('effort low'); }

      // 3. Send prompt
      await ta.fill(cfg.prompt);
      await page.getByRole('button', { name: 'Send', exact: true }).click();
      T.sent = el(); log('sent');

      // 4. Chat creation
      const re = /\/c\/(CHAT-\d{14}-[A-Z0-9]{4})/;
      await page.waitForURL(re, { timeout: 60000 }).catch(() => fail('chat not created in 60 s'));
      res.chatId = page.url().match(re)[1];
      T.chatCreated = el(); log(`chat ${res.chatId}`);

      // 5. Wait for the final assistant message
      const deadline = Date.now() + cfg.timeoutSec * 1000;
      let done = false;
      while (Date.now() < deadline) {
        if (T.running == null && await page.locator('.msg.assistant.running').count()) { T.running = el(); log('running'); }
        const finals = page.locator('.msg.assistant:not(.running):not(.inherited-msg)');
        const n = await finals.count();
        if (n) {
          const last = finals.nth(n - 1);
          const st = ((await last.getAttribute('class')) || '').match(/(?:^|\s)status-(\S+)/)?.[1];
          if (st) {
            const body = last.locator('.msg-body');
            res.reply = ((await body.count()) ? await body.first().innerText() : '').trim().slice(0, 2000);
            done = true;
            if (st === 'completed') {
              T.answered = el(); log('answered');
              if (new RegExp(cfg.expect, 'i').test(res.reply)) res.status = 'green';
              else fail(`reply does not match /${cfg.expect}/i`);
            } else fail(`job ${st}: ${res.reply}`);
            break;
          }
        }
        await sleep(2000);
      }
      if (!done) fail(`no reply in ${cfg.timeoutSec} s`);
    } catch (e) {
      res.status = 'red';
      res.reason = e instanceof Fail ? e.message : `unexpected: ${e?.message || e}`;
      log(`red: ${res.reason}`);
    }

    // 6. Cleanup: delete the test chat through the UI
    if (res.chatId) {
      if (cfg.keep) res.cleanup = 'kept';
      else {
        try {
          // 0.4.0: CO Web 0.86.0 moved "Delete chat" into the Inspector Chat tab (the status line under the title opens it)
          const del = page.getByRole('button', { name: /^Delete( chat)?$/ }).first();
          if (!(await del.isVisible().catch(() => false))) await page.locator('.head-status').first().click({ timeout: 5000 }).catch(() => {});
          await del.click({ timeout: 15000 });
          const btn = page.locator('.modal-actions button.danger');
          await btn.waitFor({ state: 'visible', timeout: 15000 });
          await btn.click();
          const end = Date.now() + 60000;
          let ok = false;
          while (Date.now() < end) {
            if (!(await page.locator('.modal-actions').count()) && !page.url().includes(res.chatId)) { ok = true; break; }
            await sleep(1000);
          }
          if (!ok) throw new Error('chat still present after 60 s');
          res.cleanup = 'deleted'; log('deleted');
        } catch (e) {
          const m = String(e?.message || e).split('\n')[0];
          res.cleanup = `failed: ${m}`; log(res.cleanup);
          if (res.status === 'green') { res.status = 'red'; res.reason = `cleanup failed: ${m}`; }
        }
      }
    }

    // 7. Screenshot on failure
    if (res.status !== 'green' && page) {
      try {
        await fs.mkdir(cfg.outDir, { recursive: true });
        const f = path.join(cfg.outDir, `${cfg.runId}.png`);
        await page.screenshot({ path: f, fullPage: true });
        res.screenshot = f;
      } catch (e) { log(`screenshot failed: ${e?.message || e}`); }
    }
  } catch (e) {
    res.status = browser ? 'red' : 'error';
    res.reason = `unexpected: ${e?.message || e}`;
  } finally {
    // 0.2.2: Chromium sometimes never finishes closing; the result must still be printed.
    if (browser) await Promise.race([browser.close().catch(() => {}), sleep(15000)]);
  }

  res.pageErrors = pageErrors;
  out(res);
}

try { await main(); }
catch (e) { out({ status: 'error', reason: String(e?.message || e) }); }
// A hung browser process would keep node alive until the caller's timeout: exit once stdout is flushed.
process.stdout.write('', () => process.exit(0));
