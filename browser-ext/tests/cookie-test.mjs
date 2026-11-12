// SPDX-License-Identifier: AGPL-3.0-or-later
// Sign-in check for the panel: CO's session cookie is Secure + HttpOnly + SameSite=Strict. Chrome sends such a cookie
// to a site framed in an extension page when the extension has host permission for it (this extension: <all_urls>),
// both on the frame's document request and on its own same-origin POST (CO's /auth/token.php). Local HTTPS server with
// a throwaway self-signed certificate (openssl). Run through run.py tools.
import os from 'node:os'; import path from 'node:path'; import fs from 'node:fs'; import https from 'node:https';
import { execFileSync } from 'node:child_process';

const [extSrc, pwMod] = process.argv.slice(2);
const { chromium } = await import(pwMod);
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'coext-cookie-'));
execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=127.0.0.1',
  '-keyout', path.join(tmp, 'k.pem'), '-out', path.join(tmp, 'c.pem')], { stdio: 'ignore' });
const ext = path.join(tmp, 'ext');
fs.cpSync(extSrc, ext, { recursive: true });
const srv = https.createServer({ key: fs.readFileSync(path.join(tmp, 'k.pem')), cert: fs.readFileSync(path.join(tmp, 'c.pem')) }, (req, res) => {
  if (req.url.startsWith('/set')) {
    res.writeHead(200, { 'set-cookie': 'sess=ok; Path=/; Secure; HttpOnly; SameSite=Strict', 'content-type': 'text/html' });
    return res.end('set');
  }
  if (req.url.startsWith('/token')) { res.writeHead(200, { 'content-type': 'text/plain' }); return res.end('post-cookie=' + (req.headers.cookie || 'none')); }
  res.writeHead(200, { 'content-type': 'text/html' });
  res.end('<p id=c>doc-cookie=' + (req.headers.cookie || 'none') + '</p><script>fetch("/token",{method:"POST",headers:{"X-CO-Auth":"1"},credentials:"same-origin"}).then(r=>r.text()).then(t=>{document.title=t})</script>');
});
await new Promise((r) => srv.listen(0, '127.0.0.1', r));
const base = 'https://127.0.0.1:' + srv.address().port;
fs.writeFileSync(path.join(ext, 'cookie-frame.html'), `<!doctype html><iframe src="${base}/frame"></iframe>`);
const ctx = await chromium.launchPersistentContext(path.join(tmp, 'profile'), {
  headless: true, ignoreHTTPSErrors: true, executablePath: process.env.PW_CHROME,
  args: [`--disable-extensions-except=${ext}`, `--load-extension=${ext}`],
});
let ok = false;
try {
  let [sw] = ctx.serviceWorkers(); if (!sw) sw = await ctx.waitForEvent('serviceworker');
  const id = sw.url().split('/')[2];
  const tab = await ctx.newPage(); await tab.goto(base + '/set'); // the user signs in in a tab
  const panel = await ctx.newPage(); await panel.goto(`chrome-extension://${id}/cookie-frame.html`);
  let doc = ''; let post = '';
  for (let i = 0; i < 20 && !post; i++) {
    await panel.waitForTimeout(250);
    const f = panel.frames().find((x) => x.url().startsWith(base));
    if (f) { doc = await f.textContent('#c').catch(() => ''); post = await f.title(); }
  }
  ok = doc === 'doc-cookie=sess=ok' && post === 'post-cookie=sess=ok';
  console.log((ok ? 'PASS' : 'FAIL') + ' Strict session cookie reaches the framed site: ' + doc + ' | ' + post);
} finally {
  await ctx.close(); srv.close();
}
process.exitCode = ok ? 0 : 1;
