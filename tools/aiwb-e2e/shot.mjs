// shot.mjs - aiwb-e2e "shot" subcommand: open a page with injected auth and save a PNG screenshot.
// Reads cfg JSON from stdin, writes a single JSON result line to stdout. Logs go to stderr.
import { pathToFileURL } from 'node:url';
import fs from 'node:fs';
import path from 'node:path';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.json': 'application/json; charset=utf-8',
  '.webmanifest': 'application/manifest+json',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

const MAX_ERRORS = 20;
const MAX_ERROR_LEN = 500;

function log(...args) {
  process.stderr.write(`[shot] ${args.join(' ')}\n`);
}

function readStdin() {
  return new Promise((resolve, reject) => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (c) => { data += c; });
    process.stdin.on('end', () => resolve(data));
    process.stdin.on('error', reject);
  });
}

function isFile(p) {
  try { return fs.statSync(p).isFile(); } catch { return false; }
}

async function run(result) {
  const t0 = Date.now();
  let cfg;
  try {
    cfg = JSON.parse(await readStdin());
  } catch (e) {
    result.reason = `invalid cfg on stdin: ${e.message}`;
    return;
  }
  result.out = cfg.out;

  let browser = null;
  let page = null;
  try {
    // Playwright from an explicit (Windows) path
    const pw = await import(pathToFileURL(cfg.pwModule).href);
    // 0.3.0: --browser webkit (Safari engine, e.g. a MacBook check); chromium by default
    // 0.4.0: msedge - installed Edge through the chromium driver (the bundled headless shell may load pages very slowly)
    const engine = cfg.browser || 'chromium';
    const driver = engine === 'msedge' ? 'chromium' : engine;
    const bt = pw[driver] || (pw.default && pw.default[driver]);
    if (!bt) throw new Error(`${driver} not exported by ${cfg.pwModule}`);

    browser = await bt.launch({ headless: !cfg.headed, ...(engine === 'msedge' ? { channel: 'msedge' } : {}) });
    result.browser = engine;

    const ctxOpts = cfg.mobile
      ? { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 }
      : { viewport: { width: cfg.width || 1400, height: cfg.height || 900 } };
    ctxOpts.serviceWorkers = 'block';
    const context = await browser.newContext(ctxOpts);

    // Inject host selection and desktop token bridge before any page script runs
    await context.addInitScript(({ host, token, email, storage }) => {
      try {
        localStorage.setItem('co.host', host);
        localStorage.setItem('co.hostPref', host); // CO Web 0.98.0+ (S2)
        for (const [k, v] of Object.entries(storage || {})) localStorage.setItem(k, v);
      } catch { /* storage unavailable */ }
      window.coDesktop = {
        getAccessToken: async () => ({ access_token: token, expires_in: 3000, email }),
      };
    }, { host: cfg.host, token: cfg.token, email: cfg.email, storage: cfg.storage });

    const base = String(cfg.base).replace(/\/+$/, '');

    // Optional: serve the frontend from a local build, keep API/auth on the network
    if (cfg.dist) {
      const distRoot = path.resolve(cfg.dist);
      const indexHtml = path.join(distRoot, 'index.html');
      await context.route(`${base}/**`, async (route) => {
        let pathname;
        try {
          pathname = decodeURIComponent(new URL(route.request().url()).pathname);
        } catch {
          return route.fulfill({ status: 400, body: 'bad path' });
        }
        if (pathname.startsWith('/agent/') || pathname.startsWith('/auth/')) return route.continue();
        if (pathname.split('/').includes('..')) return route.fulfill({ status: 400, body: 'rejected path' });

        const rel = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
        let file = path.resolve(distRoot, rel);
        if (file !== distRoot && !file.startsWith(distRoot + path.sep)) {
          return route.fulfill({ status: 400, body: 'rejected path' });
        }
        if (!isFile(file)) {
          if (path.extname(rel) === '') file = indexHtml; // SPA fallback
          else return route.continue();
        }
        result.servedFiles += 1;
        const contentType = MIME[path.extname(file).toLowerCase()] || 'application/octet-stream';
        return route.fulfill({
          path: file,
          headers: { 'content-type': contentType, 'cache-control': 'no-store' },
        });
      });
    }

    page = await context.newPage();

    const pushError = (msg) => {
      if (result.pageErrors.length < MAX_ERRORS) result.pageErrors.push(String(msg).slice(0, MAX_ERROR_LEN));
    };
    page.on('console', (m) => { if (m.type() === 'error') pushError(`console: ${m.text()}`); });
    page.on('pageerror', (e) => pushError(`pageerror: ${e && e.message ? e.message : e}`));

    const url = base + cfg.path;
    log(`goto ${url}`);
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 90000 });

    if (cfg.click) {
      log(`click ${cfg.click}`);
      const target = page.locator(cfg.click).first();
      await target.waitFor({ state: 'visible', timeout: 30000 });
      await target.click();
    }

    if (cfg.waitSelector) {
      log(`wait ${cfg.waitSelector}`);
      await page.waitForSelector(cfg.waitSelector, { timeout: 60000 });
    }

    if (cfg.settleMs > 0) await page.waitForTimeout(cfg.settleMs);

    // 0.5.0: --steps - a scripted UI walk after the page settled: click/fill/press/wait/sleep/files/eval/shot
    if (Array.isArray(cfg.steps)) {
      result.steps = [];
      for (const [i, st] of cfg.steps.entries()) {
        const sel = (s) => page.locator(s).first();
        const t = st.timeout || 30000;
        log(`step ${i + 1}: ${JSON.stringify(st).slice(0, 160)}`);
        try {
          if (st.click) { await sel(st.click).waitFor({ state: 'visible', timeout: t }); await sel(st.click).click(); }
          else if (st.fill) await sel(st.fill).fill(String(st.text ?? ''), { timeout: t });
          else if (st.press) await (st.on ? sel(st.on).press(st.press, { timeout: t }) : page.keyboard.press(st.press));
          else if (st.wait) await page.waitForSelector(st.wait, { timeout: t, state: st.state || 'visible' });
          else if (st.sleep) await page.waitForTimeout(st.sleep);
          else if (st.select) await sel(st.select).selectOption(String(st.value ?? ''), { timeout: t });
          else if (st.files) await sel(st.files).setInputFiles(st.paths || [], { timeout: t });
          else if (st.eval) result.steps.push({ step: i + 1, eval: await page.evaluate(st.eval) ?? null });
          else if (st.shot) { fs.mkdirSync(path.dirname(st.shot), { recursive: true }); await page.screenshot({ path: st.shot }); }
          else throw new Error('unknown step');
        } catch (e) {
          if (!st.optional) throw new Error(`step ${i + 1} ${JSON.stringify(st).slice(0, 120)}: ${e && e.message ? e.message : e}`);
          result.steps.push({ step: i + 1, skipped: String(e && e.message ? e.message : e).slice(0, 200) });
        }
      }
    }

    if (cfg.eval) {
      try {
        const v = await page.evaluate(cfg.eval);
        result.eval = v === undefined ? null : v;
      } catch (e) {
        result.evalError = String(e && e.message ? e.message : e).slice(0, MAX_ERROR_LEN);
      }
    }

    fs.mkdirSync(path.dirname(cfg.out), { recursive: true });
    if (cfg.clipSelector) {
      await page.locator(cfg.clipSelector).first().screenshot({ path: cfg.out, timeout: 30000 });
    } else {
      await page.screenshot({ path: cfg.out });
    }

    result.status = 'ok';
  } catch (e) {
    result.status = 'error';
    result.reason = String(e && e.message ? e.message : e).slice(0, 2000);
    // Best effort: save whatever is on screen for diagnosis
    if (page && cfg && cfg.out) {
      try {
        fs.mkdirSync(path.dirname(cfg.out), { recursive: true });
        await page.screenshot({ path: cfg.out, timeout: 10000 });
      } catch (e2) {
        log(`error screenshot failed: ${e2.message}`);
      }
    }
  } finally {
    if (browser) {
      try { await browser.close(); } catch { /* ignore */ }
    }
    result.elapsedSec = Math.round((Date.now() - t0) / 100) / 10;
  }
}

const result = {
  status: 'error',
  reason: null,
  out: null,
  eval: null,
  pageErrors: [],
  servedFiles: 0,
  elapsedSec: 0,
};

try {
  await run(result);
} catch (e) {
  result.status = 'error';
  result.reason = `unexpected: ${e && e.message ? e.message : e}`;
}
process.stdout.write('\n' + JSON.stringify(result) + '\n');
process.exitCode = result.status === 'ok' ? 0 : 1;
